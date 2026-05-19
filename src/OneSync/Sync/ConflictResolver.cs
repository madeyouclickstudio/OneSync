using System;
using System.IO;
using System.Threading.Tasks;
using OneSync.Config;
using OneSync.Shell;
using OneSync.Util;
using Serilog;

namespace OneSync.Sync;

/// <summary>
/// Thrown by UploadWorker when the server returns 412 Precondition Failed,
/// meaning the cloud version of the file has changed since we last hydrated
/// or uploaded it. The caller (ProcessAsync) handles this by calling
/// ConflictResolver, which renames the local copy and queues it as a new file.
/// </summary>
internal sealed class RemoteConflictException : Exception
{
    public string RelativePath { get; }
    public string? AttemptedETag { get; }

    public RemoteConflictException(string relativePath, string? attemptedETag)
        : base($"Remote conflict for {relativePath} (If-Match {attemptedETag ?? "(none)"})")
    {
        RelativePath = relativePath;
        AttemptedETag = attemptedETag;
    }
}

/// <summary>
/// Handles a remote conflict by:
///   1. Renaming the local file to "<base> (conflict from <machine> <ts>).<ext>"
///   2. Re-hydrating the canonical path from cloud so the user has the latest
///      version under the original name
///   3. Queueing the renamed conflict copy as a brand-new upload (no If-Match)
/// Fires <see cref="ConflictDetected"/> so the tray can balloon the user.
/// </summary>
internal sealed class ConflictResolver
{
    private readonly MetadataStore _metadata;
    private readonly HydrationService _hydration;
    private readonly SyncQueue _queue;
    private readonly LocalChangeSuppressor _suppressor;
    private readonly ILogger _logger;

    public event Action<ConflictInfo>? ConflictDetected;

    public ConflictResolver(MetadataStore metadata, HydrationService hydration,
        SyncQueue queue, LocalChangeSuppressor suppressor, ILogger logger)
    {
        _metadata = metadata;
        _hydration = hydration;
        _queue = queue;
        _suppressor = suppressor;
        _logger = logger.ForContext("Component", "ConflictResolver");
    }

    public async Task<bool> ResolveAsync(DriveConfig drive, SyncOperation op)
    {
        var winRel = PathUtil.ToWindowsRelative(op.RelativePath);
        var localPath = Path.Combine(drive.LocalRootPath, winRel);
        if (!File.Exists(localPath))
        {
            _logger.Warning("Conflict raised but local file missing - dropping op: {Path}", op.RelativePath);
            return false;
        }

        var dir = Path.GetDirectoryName(localPath) ?? drive.LocalRootPath;
        var baseName = Path.GetFileNameWithoutExtension(localPath);
        var ext = Path.GetExtension(localPath);
        var ts = DateTime.Now.ToString("yyyy-MM-dd_HHmmss");
        var machine = Environment.MachineName;
        var conflictName = $"{baseName} (conflict from {machine} {ts}){ext}";
        var conflictPath = Path.Combine(dir, conflictName);

        // Build relative paths for queue
        var parentRel = Path.GetDirectoryName(op.RelativePath.Replace('/', Path.DirectorySeparatorChar))
            ?.Replace(Path.DirectorySeparatorChar, '/') ?? "/";
        if (!parentRel.EndsWith('/')) parentRel += "/";
        if (parentRel == "//") parentRel = "/";
        var conflictRel = parentRel + conflictName;

        // Suppress watcher events during the move + re-hydrate dance
        _suppressor.Suppress(localPath);
        _suppressor.Suppress(conflictPath);
        try
        {
            File.Move(localPath, conflictPath);
            SyncStateMarker.Mark(conflictPath, SyncOverlayState.Syncing);

            _logger.Warning(
                "Conflict resolved: local edits saved as '{ConflictPath}'; canonical '{CanonicalPath}' will be re-hydrated from cloud",
                conflictPath, localPath);

            // Reset metadata for the canonical path so the next hydration runs fresh
            var meta = _metadata.Get(drive.ConfigId, op.RelativePath);
            if (meta != null)
            {
                meta.Hydrated = false;
                _metadata.Upsert(meta);
                await Task.Run(() => _hydration.HydrateIfNeeded(drive, op.RelativePath, localPath));
            }

            // Queue the conflict copy as a brand-new upload
            _queue.Enqueue(new SyncOperation
            {
                Type = SyncOpType.Upload,
                DriveConfigId = drive.ConfigId,
                DriveLetter = drive.Letter,
                RelativePath = PathUtil.NormalizeRelative(conflictRel),
                Priority = drive.Priority,
                FileSizeBytes = new FileInfo(conflictPath).Length,
            });

            ConflictDetected?.Invoke(new ConflictInfo
            {
                Drive = drive,
                OriginalRelativePath = op.RelativePath,
                ConflictLocalPath = conflictPath,
                ConflictRelativePath = conflictRel,
                MachineName = machine,
                Timestamp = DateTime.Now,
            });

            return true;
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "ConflictResolver failed for {Path}", op.RelativePath);
            return false;
        }
        finally
        {
            _ = Task.Delay(TimeSpan.FromSeconds(5)).ContinueWith(_ =>
            {
                _suppressor.Release(localPath);
                _suppressor.Release(conflictPath);
            });
        }
    }
}

internal sealed class ConflictInfo
{
    public required DriveConfig Drive { get; init; }
    public required string OriginalRelativePath { get; init; }
    public required string ConflictLocalPath { get; init; }
    public required string ConflictRelativePath { get; init; }
    public required string MachineName { get; init; }
    public required DateTime Timestamp { get; init; }
}
