using System;
using System.Collections.Concurrent;
using System.IO;

namespace OneSync.Sync;

/// <summary>
/// Paths OneSync itself is writing to, so the local watcher doesn't mistake our
/// own writes (placeholders, downloads, evictions) for user edits. Suppression is
/// per path (a folder path covers everything under it) and never the whole drive,
/// so a user saving another file at the same moment is still picked up.
/// </summary>
internal sealed class LocalChangeSuppressor
{
    private static readonly TimeSpan ReleaseTail = TimeSpan.FromSeconds(3);

    private readonly ConcurrentDictionary<string, int> _held = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, long> _until = new(StringComparer.OrdinalIgnoreCase);

    public bool IsSuppressed(string fullPath)
    {
        if (_held.IsEmpty && _until.IsEmpty) return false;

        var now = DateTime.UtcNow.Ticks;
        foreach (var kvp in _held)
            if (kvp.Value > 0 && Covers(kvp.Key, fullPath)) return true;

        foreach (var kvp in _until)
        {
            if (kvp.Value < now)
            {
                _until.TryRemove(kvp);
                continue;
            }
            if (Covers(kvp.Key, fullPath)) return true;
        }
        return false;
    }

    /// <summary>Suppress until a matching <see cref="Release"/>, plus a short tail
    /// for watcher events that arrive late.</summary>
    public void Suppress(string path)
    {
        _held.AddOrUpdate(Normalise(path), 1, (_, v) => v + 1);
    }

    public void Release(string path)
    {
        var key = Normalise(path);
        var left = _held.AddOrUpdate(key, 0, (_, v) => Math.Max(0, v - 1));
        if (left == 0) _held.TryRemove(key, out _);
        SuppressFor(key, ReleaseTail);
    }

    public void SuppressFor(string path, TimeSpan duration)
    {
        var until = DateTime.UtcNow.Add(duration).Ticks;
        _until.AddOrUpdate(Normalise(path), until, (_, v) => Math.Max(v, until));
    }

    private static string Normalise(string path) =>
        path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

    private static bool Covers(string root, string path)
    {
        if (!path.StartsWith(root, StringComparison.OrdinalIgnoreCase)) return false;
        if (path.Length == root.Length) return true;
        var next = path[root.Length];
        return next == Path.DirectorySeparatorChar || next == Path.AltDirectorySeparatorChar;
    }
}
