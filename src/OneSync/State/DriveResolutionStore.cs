using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using OneSync.Config;
using Serilog;

namespace OneSync.State;

/// <summary>
/// Cross-process sidecar that carries per-drive runtime resolution state
/// (specifically the Graph-canonical library webUrl) from the main OneSync
/// service to the short-lived <c>--launch-office</c> launcher process.
///
/// The launcher reads <c>config.json</c> directly and never runs
/// <see cref="OneSync.FileSystem.DrivePermissionChecker"/>, so without this
/// sidecar it has no way to learn that e.g. drive J:'s library URL slug is
/// <c>Shared%20Documents</c> rather than the display name <c>Documents</c>.
///
/// Layout (<c>%LOCALAPPDATA%\OneSync\drive-resolution.json</c>):
/// <code>
/// {
///   "drives": {
///     "H": { "libraryWebUrl": null },
///     "I": { "libraryWebUrl": "https://.../sites/X/Shared%20Documents" },
///     "J": { "libraryWebUrl": "https://.../sites/Y/Shared%20Documents" }
///   }
/// }
/// </code>
///
/// Keyed by drive letter (stable across config edits; unique per machine).
/// Write fails are warnings, not errors — the file is best-effort cache;
/// launcher falls back to its old siteUrl+libraryName construction if the
/// sidecar is absent.
/// </summary>
internal static class DriveResolutionStore
{
    private static readonly string FilePath = Path.Combine(
        Environment.ExpandEnvironmentVariables(@"%LOCALAPPDATA%\OneSync"),
        "drive-resolution.json");

    /// <summary>Write the current resolved state for all drives.
    /// Called from Program.cs after DrivePermissionChecker.FilterByPermissionAsync.</summary>
    public static void Write(IEnumerable<DriveConfig> drives, ILogger logger)
    {
        try
        {
            var dir = Path.GetDirectoryName(FilePath);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

            var entries = new Dictionary<string, DriveEntry>(StringComparer.OrdinalIgnoreCase);
            foreach (var d in drives)
            {
                entries[d.Letter] = new DriveEntry { LibraryWebUrl = d.ResolvedLibraryWebUrl };
            }
            var payload = new Sidecar { Drives = entries };
            var json = JsonSerializer.Serialize(payload, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(FilePath, json);
            logger.Debug("DriveResolutionStore wrote {Count} drive entries to {Path}", entries.Count, FilePath);
        }
        catch (Exception ex)
        {
            logger.Warning(ex, "DriveResolutionStore write failed — launcher will fall back to siteUrl+libraryName construction");
        }
    }

    /// <summary>Hydrate <see cref="DriveConfig.ResolvedLibraryWebUrl"/> on the
    /// given drive from the sidecar. Best-effort: if the file is missing or
    /// the drive isn't in it, the property stays null and the caller falls
    /// back. Called from OfficeLauncher.</summary>
    public static void Hydrate(DriveConfig drive)
    {
        try
        {
            if (!File.Exists(FilePath)) return;
            var json = File.ReadAllText(FilePath);
            var payload = JsonSerializer.Deserialize<Sidecar>(json);
            if (payload?.Drives is null) return;
            if (payload.Drives.TryGetValue(drive.Letter, out var entry)
                && !string.IsNullOrEmpty(entry?.LibraryWebUrl))
            {
                drive.ResolvedLibraryWebUrl = entry.LibraryWebUrl;
            }
        }
        catch
        {
            // Best-effort; launcher falls back to siteUrl+libraryName.
        }
    }

    private sealed class Sidecar
    {
        public Dictionary<string, DriveEntry> Drives { get; set; } = new();
    }

    private sealed class DriveEntry
    {
        public string? LibraryWebUrl { get; set; }
    }
}
