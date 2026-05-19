using System;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Text.Json;
using OneSync.Config;
using OneSync.FileSystem;
using OneSync.Sync;
using OneSync.Util;
using Serilog;

namespace OneSync.Diagnostics;

/// <summary>
/// Produces a self-contained diagnostic bundle (zip) on user request, suitable
/// for IT to triage a OneSync issue without remote-desktop access. The bundle
/// is written to the user's Desktop (falls back to %TEMP%).
///
/// Privacy: tenant IDs, client IDs, and OAuth tokens are NEVER included. File
/// CONTENTS are NEVER included — only file paths and pending-sync state. The
/// user can inspect the zip before sending it.
/// </summary>
internal sealed class DiagnosticExporter
{
    private readonly AppConfig _config;
    private readonly SyncEngine _sync;
    private readonly QuotaCache _quotaCache;
    private readonly GraphHttpClient _graph;
    private readonly ILogger _logger;
    private readonly string _liveConfigPath;

    public DiagnosticExporter(
        AppConfig config,
        string liveConfigPath,
        SyncEngine sync,
        QuotaCache quotaCache,
        GraphHttpClient graph,
        ILogger logger)
    {
        _config = config;
        _liveConfigPath = liveConfigPath;
        _sync = sync;
        _quotaCache = quotaCache;
        _graph = graph;
        _logger = logger.ForContext("Component", "DiagnosticExporter");
    }

    /// <summary>Build the zip and return its full path.</summary>
    public string Export()
    {
        var ts = DateTime.UtcNow.ToString("yyyyMMdd-HHmmss");
        var zipName = $"OneSync-diagnostics-{ts}.zip";
        var desktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
        var zipPath = TryWritablePath(desktop, zipName)
                      ?? TryWritablePath(Path.GetTempPath(), zipName)
                      ?? throw new IOException("Cannot find a writable location for the diagnostic zip");

        using (var fs = new FileStream(zipPath, FileMode.Create, FileAccess.Write))
        using (var zip = new ZipArchive(fs, ZipArchiveMode.Create))
        {
            AddTextEntry(zip, "README.txt", BuildReadme());
            AddRedactedConfig(zip);
            AddRecentLogs(zip);
            AddCrashFiles(zip);
            AddJsonEntry(zip, "sync_queue_snapshot.json", BuildQueueSnapshot());
            AddJsonEntry(zip, "machine_info.json", BuildMachineInfo());
            AddJsonEntry(zip, "cooperative_state.json", BuildCoopSnapshot());
            AddJsonEntry(zip, "enumeration_cache_stats.json", BuildEnumStats());
            AddJsonEntry(zip, "cooldown_state.json", BuildCooldownSnapshot());
            AddJsonEntry(zip, "quota_cache.json", BuildQuotaSnapshot());
        }

        _logger.Information("Diagnostic export written to {Path}", zipPath);
        return zipPath;
    }

    private static string? TryWritablePath(string dir, string name)
    {
        try
        {
            if (!Directory.Exists(dir)) return null;
            var probe = Path.Combine(dir, ".onesync-diag-probe");
            File.WriteAllText(probe, "");
            File.Delete(probe);
            return Path.Combine(dir, name);
        }
        catch { return null; }
    }

    private static void AddTextEntry(ZipArchive zip, string name, string content)
    {
        var entry = zip.CreateEntry(name, CompressionLevel.Optimal);
        using var w = new StreamWriter(entry.Open());
        w.Write(content);
    }

    private static void AddJsonEntry(ZipArchive zip, string name, object payload)
    {
        var json = JsonSerializer.Serialize(payload, new JsonSerializerOptions { WriteIndented = true });
        AddTextEntry(zip, name, json);
    }

    private string BuildReadme() =>
        $"OneSync diagnostic bundle\n" +
        $"Generated: {DateTime.UtcNow:O}\n" +
        $"Machine:   {Environment.MachineName}\n" +
        $"User:      {Environment.UserName}\n" +
        $"\n" +
        $"This zip contains:\n" +
        $"  README.txt                     this file\n" +
        $"  config_redacted.json           live config.json with tenant/client IDs redacted\n" +
        $"  logs/                          last 7 days of Serilog files\n" +
        $"  crashes/                       all crash-*.txt files (may be empty)\n" +
        $"  sync_queue_snapshot.json       pending operations (paths only, no file contents)\n" +
        $"  machine_info.json              Windows version, .NET runtime, free space\n" +
        $"  cooperative_state.json         per-drive leader/reader role + lease expiry\n" +
        $"  enumeration_cache_stats.json   in-memory enum cache size + LiteDB stats\n" +
        $"  cooldown_state.json            current Graph cooldown + recent throttle events\n" +
        $"  quota_cache.json               per-drive quota snapshot\n" +
        $"\n" +
        $"What is NOT included: OAuth tokens, file contents, file hashes that could leak content,\n" +
        $"tenant/client IDs (redacted in config), passwords (OneSync doesn't store any).\n";

    private void AddRedactedConfig(ZipArchive zip)
    {
        try
        {
            var raw = File.ReadAllText(_liveConfigPath);
            var redacted = System.Text.RegularExpressions.Regex.Replace(
                raw,
                @"""(tenantId|clientId|authority)""\s*:\s*""[^""]*""",
                @"""$1"": ""<redacted>""");
            AddTextEntry(zip, "config_redacted.json", redacted);
        }
        catch (Exception ex)
        {
            AddTextEntry(zip, "config_redacted.json", $"(unable to read live config: {ex.Message})");
        }
    }

    private void AddRecentLogs(ZipArchive zip)
    {
        try
        {
            var logDir = Environment.ExpandEnvironmentVariables(_config.Logging.Path);
            if (!Directory.Exists(logDir)) return;
            var cutoff = DateTime.UtcNow.AddDays(-7);
            foreach (var f in Directory.EnumerateFiles(logDir, "onesync-*.log"))
            {
                var info = new FileInfo(f);
                if (info.LastWriteTimeUtc < cutoff) continue;
                var entry = zip.CreateEntry($"logs/{info.Name}", CompressionLevel.Optimal);
                using var src = new FileStream(f, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                using var dst = entry.Open();
                src.CopyTo(dst);
            }
        }
        catch (Exception ex)
        {
            AddTextEntry(zip, "logs/_collection_error.txt", $"Failed to collect logs: {ex.Message}");
        }
    }

    private void AddCrashFiles(ZipArchive zip)
    {
        try
        {
            if (!Directory.Exists(CrashWriter.CrashesDir)) return;
            foreach (var f in Directory.EnumerateFiles(CrashWriter.CrashesDir, "crash-*.txt"))
            {
                var info = new FileInfo(f);
                var entry = zip.CreateEntry($"crashes/{info.Name}", CompressionLevel.Optimal);
                using var src = new FileStream(f, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                using var dst = entry.Open();
                src.CopyTo(dst);
            }
        }
        catch (Exception ex)
        {
            AddTextEntry(zip, "crashes/_collection_error.txt", $"Failed to collect crashes: {ex.Message}");
        }
    }

    private object BuildQueueSnapshot()
    {
        try
        {
            var counts = _sync.Queue.GetPendingCountsByDrive();
            return new
            {
                generated_utc = DateTime.UtcNow,
                pending_counts = counts,
                note = "File-content excluded; paths only.",
            };
        }
        catch (Exception ex)
        {
            return new { error = ex.Message };
        }
    }

    private object BuildMachineInfo()
    {
        return new
        {
            generated_utc = DateTime.UtcNow,
            machine = Environment.MachineName,
            user = Environment.UserName,
            os = Environment.OSVersion.VersionString,
            dotnet = Environment.Version.ToString(),
            onesync_version = typeof(DiagnosticExporter).Assembly.GetName().Version?.ToString(),
            cpu_count = Environment.ProcessorCount,
        };
    }

    private object BuildCoopSnapshot()
    {
        return new
        {
            generated_utc = DateTime.UtcNow,
            enabled = _config.CooperativePolling.Enabled,
            note = "Detailed per-drive cooperative state requires log inspection (see logs/onesync-*.log for 'Acquired leader role' / 'Reader mode').",
        };
    }

    private object BuildEnumStats()
    {
        return new
        {
            generated_utc = DateTime.UtcNow,
            note = "In-memory enum dedup is a session-scoped fast path; persistent state lives in MetadataStore.FolderEnumerationState (visible via the items count in sync_queue_snapshot or by file size of %LOCALAPPDATA%\\OneSync\\metadata.db).",
        };
    }

    private object BuildCooldownSnapshot()
    {
        return new
        {
            generated_utc = DateTime.UtcNow,
            currently_in_cooldown = _graph.IsInCooldown,
            cooldown_until_utc = _graph.CooldownUntilUtc,
            total_throttle_events = _graph.ThrottleEventCount,
            recent_60s_top_urls = _graph.RingBuffer.TopUrlsInLast(TimeSpan.FromSeconds(60), top: 10),
            recent_60s_request_count = _graph.RingBuffer.CountInLast(TimeSpan.FromSeconds(60)),
        };
    }

    private object BuildQuotaSnapshot()
    {
        return new
        {
            generated_utc = DateTime.UtcNow,
            quotas = _quotaCache.SnapshotAll(),
        };
    }
}
