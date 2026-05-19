using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace OneSync.Config;

internal sealed class AppConfig
{
    [JsonPropertyName("tenantId")]
    public string TenantId { get; set; } = string.Empty;

    [JsonPropertyName("clientId")]
    public string ClientId { get; set; } = string.Empty;

    [JsonPropertyName("authority")]
    public string Authority { get; set; } = string.Empty;

    [JsonPropertyName("localStorageRoot")]
    public string LocalStorageRoot { get; set; } = @"%LOCALAPPDATA%\OneSync\Drives";

    [JsonPropertyName("drives")]
    public List<DriveConfig> Drives { get; set; } = new();

    [JsonPropertyName("driveFiltering")]
    public DriveFilteringConfig DriveFiltering { get; set; } = new();

    [JsonPropertyName("cleanup")]
    public CleanupConfig Cleanup { get; set; } = new();

    [JsonPropertyName("syncSettings")]
    public SyncSettings SyncSettings { get; set; } = new();

    [JsonPropertyName("logging")]
    public LoggingConfig Logging { get; set; } = new();

    [JsonPropertyName("cooperativePolling")]
    public CooperativePollingConfig CooperativePolling { get; set; } = new();
}

internal sealed class DriveConfig
{
    [JsonPropertyName("letter")]
    public string Letter { get; set; } = "H";

    [JsonPropertyName("label")]
    public string Label { get; set; } = string.Empty;

    [JsonPropertyName("type")]
    public string Type { get; set; } = "onedrive"; // "onedrive" or "sharepoint"

    [JsonPropertyName("remotePath")]
    public string RemotePath { get; set; } = "/";

    [JsonPropertyName("syncMode")]
    public string SyncMode { get; set; } = "bidirectional";

    [JsonPropertyName("priority")]
    public int Priority { get; set; } = 100;

    [JsonPropertyName("siteUrl")]
    public string? SiteUrl { get; set; }

    [JsonPropertyName("libraryName")]
    public string? LibraryName { get; set; }

    [JsonPropertyName("folderRedirection")]
    public List<string>? FolderRedirection { get; set; }

    // Runtime-only, populated by DrivePermissionChecker (not serialized to disk on read)
    [JsonIgnore]
    public string? ResolvedDriveId { get; set; }

    [JsonIgnore]
    public string? ResolvedSiteId { get; set; }

    /// <summary>The canonical library root URL from Graph (e.g.
    /// "https://contoso.sharepoint.com/sites/TeamSite/Shared%20Documents").
    /// Used by OfficeLauncher to construct the direct file URL — `libraryName` in
    /// config is the display name and may differ from the URL slug (modern
    /// SharePoint libraries display as "Documents" but URL is "Shared Documents").
    /// Null until DrivePermissionChecker resolves the drive.</summary>
    [JsonIgnore]
    public string? ResolvedLibraryWebUrl { get; set; }

    [JsonIgnore]
    public string LocalRootPath { get; set; } = string.Empty;

    public string DriveKey =>
        Type.Equals("sharepoint", StringComparison.OrdinalIgnoreCase)
            ? $"sp:{ResolvedDriveId ?? SiteUrl + "/" + LibraryName}"
            : $"od:{Letter}";

    public string ConfigId => Type.Equals("sharepoint", StringComparison.OrdinalIgnoreCase)
        ? $"SharePoint_{Letter}_{Label.Replace(' ', '_')}"
        : $"OneDrive_{Letter}";

    public bool IsOneDrive => Type.Equals("onedrive", StringComparison.OrdinalIgnoreCase);
    public bool IsSharePoint => Type.Equals("sharepoint", StringComparison.OrdinalIgnoreCase);
}

internal sealed class DriveFilteringConfig
{
    [JsonPropertyName("enabled")] public bool Enabled { get; set; } = true;
    [JsonPropertyName("checkPermissionsAtStartup")] public bool CheckPermissionsAtStartup { get; set; } = true;
    [JsonPropertyName("skipDriveOn403")] public bool SkipDriveOn403 { get; set; } = true;
    [JsonPropertyName("logSkippedDrives")] public bool LogSkippedDrives { get; set; } = true;
}

internal sealed class CleanupConfig
{
    [JsonPropertyName("cleanOnLogoff")] public bool CleanOnLogoff { get; set; } = true;
    [JsonPropertyName("cleanOnLogon")] public bool CleanOnLogon { get; set; } = true;
    [JsonPropertyName("cleanupDelayAfterSyncFlushSeconds")] public int CleanupDelayAfterSyncFlushSeconds { get; set; } = 5;
    [JsonPropertyName("preserveAuthCache")] public bool PreserveAuthCache { get; set; } = true;
    [JsonPropertyName("preserveLogs")] public bool PreserveLogs { get; set; } = true;
    [JsonPropertyName("preserveQuotaCache")] public bool PreserveQuotaCache { get; set; } = true;
}

internal sealed class GraphRateLimitConfig
{
    /// <summary>Master switch. When false, no client-side throttling — every request
    /// goes straight to the existing cooldown/semaphore wait.</summary>
    [JsonPropertyName("enabled")] public bool Enabled { get; set; } = true;

    /// <summary>Sustained budget. Below Microsoft's documented per-app-per-tenant
    /// limit of ~2000 req/min (33/s), with headroom for bursts.</summary>
    [JsonPropertyName("requestsPerSecond")] public int RequestsPerSecond { get; set; } = 30;

    /// <summary>Maximum tokens the bucket can accumulate while idle.</summary>
    [JsonPropertyName("burstSize")] public int BurstSize { get; set; } = 60;

    /// <summary>Wait this many ms or longer triggers a structured WARN log event so
    /// pile-ups are visible without spamming on every micro-wait.</summary>
    [JsonPropertyName("waitWarnThresholdMs")] public int WaitWarnThresholdMs { get; set; } = 1000;
}

internal sealed class SyncSettings
{
    [JsonPropertyName("pollIntervalSeconds")] public int PollIntervalSeconds { get; set; } = 300;
    [JsonPropertyName("maxConcurrentUploads")] public int MaxConcurrentUploads { get; set; } = 3;
    [JsonPropertyName("maxConcurrentHydrations")] public int MaxConcurrentHydrations { get; set; } = 4;
    [JsonPropertyName("maxConcurrentGraphRequests")] public int MaxConcurrentGraphRequests { get; set; } = 8;
    [JsonPropertyName("shutdownTimeoutSeconds")] public int ShutdownTimeoutSeconds { get; set; } = 90;
    [JsonPropertyName("maxFileSizeMB")] public int MaxFileSizeMB { get; set; } = 250;
    [JsonPropertyName("excludePatterns")] public List<string> ExcludePatterns { get; set; } = new()
    {
        "*.tmp", "*.~tmp", "~$*", "Thumbs.db", "desktop.ini", ".ds_store", "*.lnk",
        "*.crdownload", "*.part", "*.partial", "*.crswap",
        "*.swp", "*.swo", "*.lock", "*.lck", "~*.tmp", "__autosave_*", "WRL*.tmp", "*.bak.tmp"
    };
    [JsonPropertyName("quotaRefreshIntervalSeconds")] public int QuotaRefreshIntervalSeconds { get; set; } = 300;
    // 60s keeps remote-change latency at ~1 min worst case. At single-user scale the
    // extra Graph calls are negligible; revisit for large estate rollouts.
    [JsonPropertyName("deltaQueryIntervalSeconds")] public int DeltaQueryIntervalSeconds { get; set; } = 60;
    [JsonPropertyName("uploadDebounceMs")] public int UploadDebounceMs { get; set; } = 2000;
    [JsonPropertyName("maxRetries")] public int MaxRetries { get; set; } = 5;
    [JsonPropertyName("retryBackoffBaseSeconds")] public int RetryBackoffBaseSeconds { get; set; } = 30;
    [JsonPropertyName("maxRetryAfterSeconds")] public int MaxRetryAfterSeconds { get; set; } = 600;
    [JsonPropertyName("graphRateLimit")] public GraphRateLimitConfig GraphRateLimit { get; set; } = new();
    [JsonPropertyName("maxConcurrentThumbnailFetches")] public int MaxConcurrentThumbnailFetches { get; set; } = 4;
    [JsonPropertyName("enableThumbnailPrefetch")] public bool EnableThumbnailPrefetch { get; set; } = true;

    /// <summary>If true, the local metadata cache (metadata.db + 0-byte placeholders)
    /// is wiped on startup when the previous session ended gracefully — or when the
    /// cache is older than 12h (catches ungraceful exits). Pending uploads in
    /// sync_queue.db and hydrated file content on disk are always preserved.
    /// Design intent: keep per-user disk footprint bounded on persistent profiles
    /// (school staff machines) without losing offline edits or downloaded files.</summary>
    [JsonPropertyName("sessionCacheMode")] public bool SessionCacheMode { get; set; } = true;

    /// <summary>Master switch for LRU local-disk eviction. When local NTFS free space
    /// falls below the threshold, OneSync truncates least-recently-used hydrated files
    /// back to 0-byte placeholders. The cloud copy is NEVER touched.</summary>
    [JsonPropertyName("evictionEnabled")] public bool EvictionEnabled { get; set; } = true;

    /// <summary>When local free space (on the volume containing localStorageRoot)
    /// drops below this in GB, start evicting. Default 2 GB — safe for student
    /// laptops with 64 GB SSDs.</summary>
    [JsonPropertyName("evictionFreeSpaceThresholdGB")] public int EvictionFreeSpaceThresholdGB { get; set; } = 2;

    /// <summary>Keep evicting until at least this many GB are free. Should be
    /// higher than EvictionFreeSpaceThresholdGB so we don't flap. Default 4 GB.</summary>
    [JsonPropertyName("evictionTargetFreeSpaceGB")] public int EvictionTargetFreeSpaceGB { get; set; } = 4;

    /// <summary>How often (seconds) the eviction service checks disk free space.
    /// Cheap check — DriveInfo.AvailableFreeSpace is a single syscall.</summary>
    [JsonPropertyName("evictionCheckIntervalSeconds")] public int EvictionCheckIntervalSeconds { get; set; } = 60;

    /// <summary>Don't evict files that were accessed more recently than this many
    /// minutes ago — protects files the user is currently working with.
    /// Default 15 minutes.</summary>
    [JsonPropertyName("evictionMinAgeMinutes")] public int EvictionMinAgeMinutes { get; set; } = 15;
}

internal sealed class LoggingConfig
{
    [JsonPropertyName("level")] public string Level { get; set; } = "Info";
    [JsonPropertyName("path")] public string Path { get; set; } = @"%LOCALAPPDATA%\OneSync\Logs";
    [JsonPropertyName("maxSizeMB")] public int MaxSizeMB { get; set; } = 50;
    [JsonPropertyName("maxFiles")] public int MaxFiles { get; set; } = 10;
}

/// <summary>
/// Cooperative polling — one client per shared library does the real delta
/// polling and writes results to a cache file in the library; everyone else
/// reads the cache. Scales the SharePoint delta query cost from O(users) to
/// O(1) per library. See docs spec for the leader-election + cache file design.
/// </summary>
internal sealed class CooperativePollingConfig
{
    /// <summary>Master switch. When false, every client polls Graph independently
    /// (the pre-1.3 behaviour). Default false during phased rollout — flip per
    /// drive once leader election is proven.</summary>
    [JsonPropertyName("enabled")] public bool Enabled { get; set; } = false;

    /// <summary>Folder name inside each library where OneSync stores its lock
    /// and delta cache files. Leading dot keeps it hidden on POSIX clients;
    /// the Dokan layer also reports Hidden+System on Windows. During
    /// development, set to something like ".onesync-test" to keep the
    /// production library root untouched until the protocol is stable.</summary>
    [JsonPropertyName("controlFolder")] public string ControlFolder { get; set; } = ".onesync";

    /// <summary>How long a leader's lock stays valid before another client
    /// considers it expired and takes over. Default 10 min. Should comfortably
    /// exceed RenewIntervalSeconds × 2.</summary>
    [JsonPropertyName("leaseTtlSeconds")] public int LeaseTtlSeconds { get; set; } = 600;

    /// <summary>How often the leader renews its lock and writes a fresh delta
    /// cache. Default 5 min (matches the legacy delta poll cadence).</summary>
    [JsonPropertyName("renewIntervalSeconds")] public int RenewIntervalSeconds { get; set; } = 300;

    /// <summary>How often readers pull the cache file from the library to
    /// pick up the leader's latest delta. Default 5 min — same cadence as
    /// the leader's writes, so worst-case staleness is ~one cycle.</summary>
    [JsonPropertyName("readerPollIntervalSeconds")] public int ReaderPollIntervalSeconds { get; set; } = 300;

    /// <summary>How often readers do an independent delta query against the
    /// library to catch items the leader couldn't see (permission islands).
    /// Default 60 min — cheap because each call is a delta token, not a
    /// full enumeration.</summary>
    [JsonPropertyName("selfCheckIntervalMinutes")] public int SelfCheckIntervalMinutes { get; set; } = 60;

    /// <summary>If true, Dokan FindFiles falls through to a live Graph query
    /// for folders not present in local placeholders. Catches the case where
    /// a user has access to a subfolder the leader cannot see. Cached briefly
    /// to avoid hammering on Explorer refreshes.</summary>
    [JsonPropertyName("lazyFallbackEnabled")] public bool LazyFallbackEnabled { get; set; } = true;

    /// <summary>How long lazy-fallback folder listings stay valid before the
    /// next access re-queries Graph. Default 5 min.</summary>
    [JsonPropertyName("lazyFallbackCacheSeconds")] public int LazyFallbackCacheSeconds { get; set; } = 300;

    /// <summary>Development flag: even if we're the leader, also run the reader
    /// merge path on each tick. Lets a single-user dev build exercise both code
    /// paths in one process. Production should leave this false.</summary>
    [JsonPropertyName("forceReaderRoleAlongsideLeader")] public bool ForceReaderRoleAlongsideLeader { get; set; } = false;

    /// <summary>Even when the cache contents are unchanged, the leader forces a
    /// write every N renewal cycles so the WrittenAt timestamp stays fresh —
    /// readers use it to detect a stale/dead leader. Default 12 ticks (≈1h at the
    /// 5-minute renew cadence).</summary>
    [JsonPropertyName("cacheForceWriteEveryNCycles")] public int CacheForceWriteEveryNCycles { get; set; } = 12;

    /// <summary>
    /// Sliding window for the cache: include every folder (so the directory
    /// structure stays browsable) plus only files modified within the last N
    /// days. Older files populate on demand via lazy fallback when a user
    /// navigates to their folder. Default 30 days — for a 250k-item library
    /// this drops cache size from ~156 MB to ~20 MB. Set to 0 to disable
    /// filtering and ship a full snapshot every write (the pre-1.2.25
    /// behaviour).
    /// </summary>
    [JsonPropertyName("cacheItemMaxAgeDays")] public int CacheItemMaxAgeDays { get; set; } = 30;
}
