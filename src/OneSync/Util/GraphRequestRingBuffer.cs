using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;

namespace OneSync.Util;

/// <summary>
/// Bounded sliding-window log of Graph request URLs + status codes. Used to
/// answer "what was the app doing in the 60 seconds before this cooldown?".
/// Flushed into the structured cooldown_applied log event in GraphHttpClient.
///
/// Lock-free throughout. Record is the hot path and uses Interlocked.Increment
/// for the slot index. Queries iterate the buffer without locking — under
/// concurrent Record they may see best-effort/stale entries. Acceptable
/// because queries are rare (one per cooldown event) and the use case is
/// forensic logging, not transactional correctness.
/// </summary>
internal sealed class GraphRequestRingBuffer
{
    private readonly int _capacity;
    private readonly Entry[] _buffer;
    private long _writeIndex;  // monotonic; modulo capacity gives slot

    public GraphRequestRingBuffer(int capacity = 1024)
    {
        _capacity = capacity;
        _buffer = new Entry[capacity];
    }

    public void Record(string url, int statusCode)
    {
        var next = Interlocked.Increment(ref _writeIndex) - 1;
        // Use long-modulo BEFORE cast so we don't truncate to a negative int
        // when _writeIndex passes int.MaxValue. The (+ _capacity) % _capacity
        // dance also normalises any negative result after the eventual long
        // overflow at 2^63 writes.
        var slot = (int)(((next % _capacity) + _capacity) % _capacity);
        _buffer[slot] = new Entry(DateTime.UtcNow, url, statusCode);
    }

    public int CountInLast(TimeSpan window)
    {
        var cutoff = DateTime.UtcNow - window;
        int count = 0;
        for (int i = 0; i < _capacity; i++)
        {
            var e = _buffer[i];
            if (e.Url is null) continue;
            if (e.TimestampUtc >= cutoff) count++;
        }
        return count;
    }

    public IReadOnlyList<UrlFrequency> TopUrlsInLast(TimeSpan window, int top)
    {
        if (top <= 0) return Array.Empty<UrlFrequency>();
        var cutoff = DateTime.UtcNow - window;
        var counts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < _capacity; i++)
        {
            var e = _buffer[i];
            if (e.Url is null) continue;
            if (e.TimestampUtc < cutoff) continue;
            var bucket = BucketUrl(e.Url);
            counts.TryGetValue(bucket, out var c);
            counts[bucket] = c + 1;
        }
        return counts
            .OrderByDescending(kvp => kvp.Value)
            .Take(top)
            .Select(kvp => new UrlFrequency(kvp.Key, kvp.Value))
            .ToList();
    }

    /// <summary>
    /// Bucket per-item URLs so "{driveId}/items/{itemId}/content" entries
    /// aggregate into a single bucket — otherwise the top-K is uninformative
    /// noise of unique URLs.
    /// </summary>
    private static string BucketUrl(string url)
    {
        // Replace GUID-shaped path segments with {id}
        var span = url.AsSpan();
        var idx = span.IndexOf("/items/", StringComparison.OrdinalIgnoreCase);
        if (idx >= 0)
        {
            var afterItems = idx + 7;
            var rest = span[afterItems..];
            var nextSlash = rest.IndexOf('/');
            if (nextSlash > 0)
            {
                return string.Concat(url.AsSpan(0, afterItems), "{id}", rest[nextSlash..]);
            }
            return string.Concat(url.AsSpan(0, afterItems), "{id}");
        }
        return url;
    }

    private readonly record struct Entry(DateTime TimestampUtc, string? Url, int StatusCode);
    public readonly record struct UrlFrequency(string Url, int Count);
}
