using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using OneSync.Config;
using OneSync.Sync;
using OneSync.Util;
using Serilog;

namespace OneSync;

/// <summary>
/// Subcommand handler: <c>OneSync.exe --launch-office &lt;path&gt;</c>
///
/// Registered as the default app for .docx/.xlsx/.pptx (etc.) so that
/// double-click in Explorer routes here. We look up the SharePoint webUrl
/// for the file in MetadataStore and launch Office with
/// <c>ms-word:ofe|u|&lt;url&gt;</c> (or excel/powerpoint equivalent), which
/// makes Office open the file directly from SharePoint - co-authoring and
/// AutoSave activate natively, same as if the user opened the file from
/// Office.com or the native OneDrive client.
///
/// Falls back to opening the local file via the normal Office handler if:
///   - File isn't inside a configured OneSync drive
///   - MetadataStore doesn't know the webUrl (yet)
///   - Anything in the lookup throws
///
/// MUST be fast: the user is sitting at Explorer waiting for Word to appear.
/// We do the absolute minimum work - open MetadataStore in shared mode,
/// one lookup, one Process.Start, exit.
/// </summary>
internal static class OfficeLauncher
{
    public static int Launch(string filePath)
    {
        // Quiet logger that writes to a dedicated launcher log file so we
        // can diagnose redirect issues without flooding the main log.
        ILogger logger;
        try
        {
            var stateDir = PathUtil.Expand(@"%LOCALAPPDATA%\OneSync");
            logger = new LoggerConfiguration()
                .MinimumLevel.Information()
                .WriteTo.File(
                    path: Path.Combine(stateDir, "Logs", "office-launcher.log"),
                    rollingInterval: Serilog.RollingInterval.Day,
                    retainedFileCountLimit: 7)
                .CreateLogger();
        }
        catch
        {
            logger = Serilog.Core.Logger.None;
        }

        logger.Information("--launch-office '{Path}'", filePath);

        try
        {
            // Resolve the file path to (DriveConfig, relative path).
            // OneSync drives are subst-mapped/Dokan-mapped to local NTFS
            // folders under %LOCALAPPDATA%\OneSync\Drives\... - but file
            // associations get the H:\... / I:\... / J:\... drive-letter
            // form. We normalise both ways.
            var (drive, relativePath, localPath) = ResolveToConfiguredDrive(filePath, logger);
            if (drive == null)
            {
                logger.Information("Path not in a OneSync drive; opening locally");
                return OpenLocally(filePath);
            }

            // Hydrate ResolvedLibraryWebUrl from the sidecar the main service
            // wrote after DrivePermissionChecker. Without this, BuildDirectSharePointUrl
            // falls back to the (often-wrong) siteUrl+libraryName guess.
            OneSync.State.DriveResolutionStore.Hydrate(drive);

            // Look up the webUrl in MetadataStore (shared mode read).
            string? webUrl = TryLookupWebUrl(drive, relativePath, localPath, logger, out var itemId);

            if (string.IsNullOrEmpty(webUrl))
            {
                logger.Information("No webUrl for {Path} (drive {Drive}, rel {Rel}) - opening locally",
                    filePath, drive.Letter, relativePath);
                return OpenLocally(filePath);
            }

            // Build the Office protocol URL based on extension.
            var ext = Path.GetExtension(filePath).ToLowerInvariant();
            var scheme = ext switch
            {
                ".docx" or ".doc" or ".docm" or ".dotx" or ".dot" or ".rtf"   => "ms-word",
                ".xlsx" or ".xls" or ".xlsm" or ".xltx" or ".xlt" or ".csv"   => "ms-excel",
                ".pptx" or ".ppt" or ".pptm" or ".potx" or ".pot" or ".ppsx"  => "ms-powerpoint",
                ".vsdx" or ".vsd"                                             => "ms-visio",
                ".one" or ".onetoc2"                                          => "ms-onenote",
                _ => null,
            };

            if (scheme == null)
            {
                logger.Information("No Office protocol for extension {Ext}; opening locally", ext);
                return OpenLocally(filePath);
            }

            // Graph's webUrl is the "Office Web Viewer" URL (.../_layouts/15/Doc.aspx?...).
            // ms-excel:ofe|u| expects the DIRECT file URL (.../personal/.../Documents/file.xlsx).
            // Construct the direct URL from the Doc.aspx URL by extracting the
            // personal-site / SharePoint-site root and appending Documents/<rel>.
            var directUrl = BuildDirectSharePointUrl(drive, relativePath, webUrl, logger) ?? webUrl;

            logger.Information("Direct URL: {Url}", directUrl);

            if (!string.IsNullOrEmpty(itemId) && UrlNowHoldsOtherItem(directUrl, itemId, logger))
                return OpenLocally(filePath);

            // ms-word:ofe|u|<url> - "ofe" = "open for editing"
            var protocolUrl = $"{scheme}:ofe|u|{directUrl}";

            logger.Information("Redirecting to {Url}", protocolUrl);

            try
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = protocolUrl,
                    UseShellExecute = true,
                });
                return 0;
            }
            catch (Exception ex)
            {
                logger.Warning(ex, "Process.Start of {Url} failed; falling back to local open", protocolUrl);
                return OpenLocally(filePath);
            }
        }
        catch (Exception ex)
        {
            logger.Error(ex, "Office launcher failed; opening locally");
            return OpenLocally(filePath);
        }
        finally
        {
            try { (logger as IDisposable)?.Dispose(); } catch { }
        }
    }

    /// <summary>
    /// Construct the direct SharePoint file URL that ms-word:ofe / ms-excel:ofe /
    /// ms-powerpoint:ofe expect. Graph's webUrl is the wrong format (Office Web
    /// Viewer entry-point URL - .../_layouts/15/Doc.aspx?...), so we build the
    /// direct one ourselves by extracting the site root from webUrl.
    /// </summary>
    private static string? BuildDirectSharePointUrl(
        DriveConfig drive, string relativePath, string graphWebUrl, ILogger logger)
    {
        try
        {
            var rel = relativePath.TrimStart('/').Replace('\\', '/');
            // Escape each path segment but keep the slashes.
            var escapedSegments = rel.Split('/').Select(Uri.EscapeDataString);
            var escapedRel = string.Join("/", escapedSegments);

            if (drive.IsOneDrive)
            {
                // graphWebUrl looks like:
                //   https://tenant-my.sharepoint.com/personal/user_xyz/_layouts/15/Doc.aspx?sourcedoc=...
                // Extract up to /_layouts to get the personal site root.
                var idx = graphWebUrl.IndexOf("/_layouts", StringComparison.OrdinalIgnoreCase);
                if (idx < 0)
                {
                    logger.Debug("No /_layouts in webUrl '{Url}' - cannot extract site root", graphWebUrl);
                    return null;
                }
                var personalRoot = graphWebUrl.Substring(0, idx);
                // OneDrive Personal files live under /Documents in SharePoint.
                return $"{personalRoot}/Documents/{escapedRel}";
            }
            else if (drive.IsSharePoint && !string.IsNullOrEmpty(drive.ResolvedLibraryWebUrl))
            {
                // Use the Graph-canonical library URL as the root, then append
                // the file's relative path. The library DISPLAY NAME (in config
                // as `libraryName`) is often "Documents" but the actual URL SLUG
                // is "Shared Documents" on modern team sites - and the slug is
                // what Office needs. Graph's drive.webUrl gives us the slug.
                //
                // Doc.aspx URLs DO NOT work via ms-excel:ofe|u| - Office returns
                // "this action couldn't be performed because office doesn't
                // recognise the command it was given" (the embedded & chars
                // break the protocol parser).
                return $"{drive.ResolvedLibraryWebUrl.TrimEnd('/')}/{escapedRel}";
            }
            else if (drive.IsSharePoint && !string.IsNullOrEmpty(drive.SiteUrl))
            {
                // Fallback when ResolvedLibraryWebUrl wasn't populated (e.g.
                // the DriveResolutionStore sidecar is missing or stale).
                // Best-effort guess using the display name - may construct
                // an invalid URL on modern team sites where slug ≠ display name.
                var libraryName = string.IsNullOrEmpty(drive.LibraryName) ? "Shared Documents" : drive.LibraryName!;
                var libraryEscaped = Uri.EscapeDataString(libraryName);
                logger.Warning("ResolvedLibraryWebUrl unavailable for {Letter}: - falling back to siteUrl+libraryName (may be wrong if library slug differs from display name)", drive.Letter);
                return $"{drive.SiteUrl.TrimEnd('/')}/{libraryEscaped}/{escapedRel}";
            }
        }
        catch (Exception ex)
        {
            logger.Debug(ex, "BuildDirectSharePointUrl threw");
        }
        return null;
    }

    /// <summary>
    /// Walks the configured drives and returns the (drive, posix-style relative path)
    /// pair if the file is inside one of them. Handles both the drive-letter form
    /// (H:\Documents\foo.docx) and the local NTFS form
    /// (C:\Users\&lt;u&gt;\AppData\Local\OneSync\Drives\Home_Folder\Documents\foo.docx).
    /// </summary>
    private static (DriveConfig?, string, string) ResolveToConfiguredDrive(string filePath, ILogger logger)
    {
        AppConfig config;
        try
        {
            config = ConfigLoader.LoadFromDefaultLocations(out _);
        }
        catch (Exception ex)
        {
            logger.Warning(ex, "Could not load config - falling back");
            return (null, "", "");
        }

        // For each configured drive, also expand its LocalRootPath the way
        // OneSync does at runtime so we can match the C:\... form too.
        foreach (var drive in config.Drives)
        {
            // Local NTFS form: %LOCALAPPDATA%\OneSync\Drives\<DriveFolderName>
            // OneSync's runtime sets this to "<localStorageRoot>\<Label with spaces -> underscores>"
            var driveFolder = drive.Label.Replace(' ', '_');
            var localRoot = Path.Combine(
                PathUtil.Expand(config.LocalStorageRoot ?? @"%LOCALAPPDATA%\OneSync\Drives"),
                driveFolder).TrimEnd('\\');

            // Drive-letter prefix: "H:\"
            var letterPrefix = drive.Letter.ToUpperInvariant() + ":\\";
            if (filePath.StartsWith(letterPrefix, StringComparison.OrdinalIgnoreCase))
            {
                var rest = filePath.Substring(letterPrefix.Length);
                return (drive, "/" + rest.Replace('\\', '/'), Path.Combine(localRoot, rest));
            }

            if (filePath.StartsWith(localRoot + "\\", StringComparison.OrdinalIgnoreCase))
            {
                var rest = filePath.Substring(localRoot.Length + 1);
                return (drive, "/" + rest.Replace('\\', '/'), filePath);
            }
        }

        return (null, "", "");
    }

    /// <summary>
    /// Word keeps what it knows about a cloud document under its URL: an open
    /// window, or its cached copy (kept 14 days by default). When a file is deleted
    /// and a new one put at the same path (the same download sent again, a folder
    /// cleared out and refilled), the URL stays but the cloud item is new, and Word
    /// shows its copy of the old item under "a newer version is on the server".
    /// So we remember which item we last sent Word to at each URL; while that is
    /// recent, a different item at that URL opens from the local copy instead,
    /// which always has the latest content.
    /// </summary>
    private static bool UrlNowHoldsOtherItem(string url, string itemId, ILogger logger)
    {
        var window = TimeSpan.FromDays(14);
        var stateDir = PathUtil.Expand(@"%LOCALAPPDATA%\OneSync");
        var path = Path.Combine(stateDir, "office-launch-history.json");
        var launches = new System.Collections.Generic.Dictionary<string, LaunchRecord>(StringComparer.OrdinalIgnoreCase);
        try
        {
            if (File.Exists(path))
            {
                var read = System.Text.Json.JsonSerializer.Deserialize<
                    System.Collections.Generic.Dictionary<string, LaunchRecord>>(File.ReadAllText(path));
                if (read != null)
                    foreach (var (k, v) in read) launches[k] = v;
            }
            else
            {
                // First run: rebuild from this launcher's own logs, so documents Word
                // already holds an old copy of are caught too.
                SeedFromLauncherLogs(Path.Combine(stateDir, "Logs"), launches, logger);
                try { File.Delete(Path.Combine(stateDir, "office-launches.json")); } catch { }
            }
        }
        catch (Exception ex) { logger.Warning(ex, "Could not read {Path}; starting afresh", path); }

        foreach (var k in launches.Where(kv => DateTime.UtcNow - kv.Value.At >= window).Select(kv => kv.Key).ToList())
            launches.Remove(k);

        bool other = Record(launches, url, itemId, DateTime.UtcNow, out var last);
        if (other)
            logger.Information("{Url} now holds a different item ({New}) from the one Word was given on {At:u} ({Old}); opening the local copy",
                url, itemId, last!.At, last.Id);

        try { File.WriteAllText(path, System.Text.Json.JsonSerializer.Serialize(launches)); }
        catch (Exception ex) { logger.Warning(ex, "Could not save {Path}", path); }
        return other;
    }

    /// <summary>Notes that Word is given <paramref name="itemId"/> at <paramref name="url"/>.
    /// True if Word was given a different item there before; that record is kept,
    /// so the URL goes on opening locally until it ages out.</summary>
    private static bool Record(System.Collections.Generic.Dictionary<string, LaunchRecord> launches,
        string url, string itemId, DateTime at, out LaunchRecord? last)
    {
        if (launches.TryGetValue(url, out last) && !string.Equals(last.Id, itemId, StringComparison.OrdinalIgnoreCase))
            return true;
        launches[url] = new LaunchRecord { Id = itemId, At = at };
        return false;
    }

    /// <summary>Replays the "Found item" / "Direct URL" pairs in office-launcher*.log.</summary>
    private static void SeedFromLauncherLogs(string logDir,
        System.Collections.Generic.Dictionary<string, LaunchRecord> launches, ILogger logger)
    {
        if (!Directory.Exists(logDir)) return;
        var found = new System.Text.RegularExpressions.Regex(@"Found item: ETag=""?\{([0-9A-Fa-f-]{36})\}");
        var direct = new System.Text.RegularExpressions.Regex(@"Direct URL: (\S+)");
        foreach (var file in Directory.GetFiles(logDir, "office-launcher*.log").OrderBy(f => f, StringComparer.Ordinal))
        {
            try
            {
                using var fs = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                using var reader = new StreamReader(fs);
                string? id = null;
                for (var line = reader.ReadLine(); line != null; line = reader.ReadLine())
                {
                    var f = found.Match(line);
                    if (f.Success) { id = f.Groups[1].Value; continue; }
                    var d = direct.Match(line);
                    if (d.Success && id != null &&
                        line.Length >= 30 && DateTimeOffset.TryParse(line[..30], out var at))
                        Record(launches, d.Groups[1].Value, id, at.UtcDateTime, out _);
                    if (line.Contains("--launch-office")) id = null;
                }
            }
            catch (Exception ex) { logger.Debug(ex, "Could not read {File}", file); }
        }
        logger.Information("Rebuilt {Count} Office launch records from the launcher logs", launches.Count);
    }

    /// <summary>The SharePoint id in the eTag ("{guid},version"): it is what the
    /// launcher log records, and it changes when a file is deleted and made again.</summary>
    private static string? ItemKey(RemoteItem item)
    {
        var e = item.ETag;
        int open = e?.IndexOf('{') ?? -1, close = e?.IndexOf('}') ?? -1;
        return open >= 0 && close > open ? e![(open + 1)..close] : item.RemoteItemId;
    }

    private sealed class LaunchRecord
    {
        public string Id { get; set; } = "";
        public DateTime At { get; set; }
    }

    private static string? TryLookupWebUrl(DriveConfig drive, string relativePath, string localPath, ILogger logger,
        out string? itemId)
    {
        itemId = null;
        try
        {
            var stateDir = PathUtil.Expand(@"%LOCALAPPDATA%\OneSync");
            var dbPath = Path.Combine(stateDir, "metadata.db");
            if (!File.Exists(dbPath))
            {
                logger.Information("metadata.db not present at {Path}", dbPath);
                return null;
            }

            using var metadata = new MetadataStore(dbPath, logger);
            var item = metadata.Get(drive.ConfigId, relativePath);

            if (item == null)
            {
                // Diagnostic: try a few variants in case key normalization differs
                logger.Information("Direct lookup miss for {Drive}/{Path} - trying variants",
                    drive.ConfigId, relativePath);

                var variants = new[]
                {
                    relativePath,
                    relativePath.Replace('\\', '/'),
                    relativePath.ToLowerInvariant(),
                    "/" + relativePath.TrimStart('/'),
                };
                foreach (var v in variants.Distinct())
                {
                    var it = metadata.Get(drive.ConfigId, v);
                    if (it != null)
                    {
                        logger.Information("HIT via variant '{Variant}': webUrl={WebUrl}", v, it.WebUrl ?? "<null>");
                        itemId = ItemKey(it);
                        return it.WebUrl;
                    }
                }

                logger.Information("Total items in MetadataStore for drive: {Count}",
                    metadata.CountFor(drive.ConfigId));
                return null;
            }

            logger.Information("Found item: ETag={ETag}, WebUrl={WebUrl}, Size={Size}",
                item.ETag ?? "<null>", item.WebUrl ?? "<null>", item.Size);

            // The local copy has changed since it last matched the cloud (a fresh
            // download over an old file, say) and its upload hasn't gone yet: the
            // cloud copy is out of date, so open this one. Records with no baseline
            // aren't judged here, as that would mean hashing the file.
            if (LocalState.HasBaseline(item) && LocalState.IsDirty(new FileInfo(localPath), item))
            {
                logger.Information("Local copy has changes not yet uploaded; opening it locally");
                return null;
            }
            itemId = ItemKey(item);
            return item.WebUrl;
        }
        catch (Exception ex)
        {
            logger.Warning(ex, "MetadataStore lookup failed");
            return null;
        }
    }

    /// <summary>
    /// Open the file with Office directly. We DON'T use ShellExecute on the
    /// file path because we ARE the registered default for .docx/.xlsx/.pptx
    /// - that would recurse infinitely. We invoke WINWORD.EXE / EXCEL.EXE /
    /// POWERPNT.EXE explicitly with the file path as an argument.
    /// </summary>
    private static int OpenLocally(string filePath)
    {
        try
        {
            var ext = Path.GetExtension(filePath).ToLowerInvariant();
            var exeName = ext switch
            {
                ".docx" or ".doc" or ".docm" or ".dotx" or ".dot" or ".rtf"   => "WINWORD.EXE",
                ".xlsx" or ".xls" or ".xlsm" or ".xltx" or ".xlt" or ".csv"   => "EXCEL.EXE",
                ".pptx" or ".ppt" or ".pptm" or ".potx" or ".pot" or ".ppsx"  => "POWERPNT.EXE",
                ".vsdx" or ".vsd"                                             => "VISIO.EXE",
                ".one" or ".onetoc2"                                          => "ONENOTE.EXE",
                _ => null,
            };

            if (exeName == null)
            {
                // Unknown extension - last-resort ShellExecute, but only if we
                // didn't claim this extension ourselves.
                Process.Start(new ProcessStartInfo
                {
                    FileName = filePath,
                    UseShellExecute = true,
                });
                return 0;
            }

            var officeExePath = FindOfficeExe(exeName);
            if (string.IsNullOrEmpty(officeExePath))
            {
                // No Office found - fall back to ShellExecute. This MIGHT
                // recurse if we're the default, but at this point we have
                // no better option.
                Process.Start(new ProcessStartInfo
                {
                    FileName = filePath,
                    UseShellExecute = true,
                });
                return 0;
            }

            Process.Start(new ProcessStartInfo
            {
                FileName = officeExePath,
                ArgumentList = { filePath },
                UseShellExecute = false,
            });
            return 0;
        }
        catch
        {
            return 2;
        }
    }

    /// <summary>Locate Office exe (WINWORD.EXE / EXCEL.EXE / etc.) on disk.
    /// Checks the standard Click-to-Run path first, then Program Files (x86),
    /// then falls back to PATH lookup.</summary>
    private static string? FindOfficeExe(string exeName)
    {
        // Click-to-Run path (Office 2016+)
        string[] candidates =
        {
            $@"C:\Program Files\Microsoft Office\root\Office16\{exeName}",
            $@"C:\Program Files (x86)\Microsoft Office\root\Office16\{exeName}",
            $@"C:\Program Files\Microsoft Office\Office16\{exeName}",
            $@"C:\Program Files (x86)\Microsoft Office\Office16\{exeName}",
            $@"C:\Program Files\Microsoft Office\Office15\{exeName}",
            $@"C:\Program Files (x86)\Microsoft Office\Office15\{exeName}",
        };
        foreach (var c in candidates)
        {
            if (File.Exists(c)) return c;
        }
        return null;
    }
}
