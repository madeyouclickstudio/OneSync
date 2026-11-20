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
/// meaning the cloud version of the file has changed since we last downloaded
/// or uploaded it.
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
/// Keeps both versions when the local and cloud copies of a file have both changed:
/// the local file becomes "&lt;name&gt; (conflict from &lt;PC&gt; &lt;time&gt;).&lt;ext&gt;" and is
/// uploaded as a new file, and the original name goes back to the cloud version.
/// </summary>
internal sealed class ConflictResolver
{
    private readonly MetadataStore _metadata;
    private readonly PlaceholderManager _placeholders;
    private readonly SyncQueue _queue;
    private readonly LocalChangeSuppressor _suppressor;
    private readonly ILogger _logger;

    public event Action<ConflictInfo>? ConflictDetected;

    public ConflictResolver(MetadataStore metadata, PlaceholderManager placeholders,
        SyncQueue queue, LocalChangeSuppressor suppressor, ILogger logger)
    {
        _metadata = metadata;
        _placeholders = placeholders;
        _queue = queue;
        _suppressor = suppressor;
        _logger = logger.ForContext("Component", "ConflictResolver");
    }

    /// <param name="remoteNow">The cloud's current copy, if known. The original name
    /// becomes a placeholder for it; with none, the name is left free.</param>
    public Task<bool> ResolveAsync(DriveConfig drive, SyncOperation op, RemoteItem? remoteNow)
    {
        var localPath = PlaceholderManager.LocalPathFor(drive, op.RelativePath);
        if (!File.Exists(localPath))
        {
            _logger.Warning("Conflict raised but local file missing - dropping op: {Path}", op.RelativePath);
            return Task.FromResult(true);
        }

        var dir = Path.GetDirectoryName(localPath) ?? drive.LocalRootPath;
        var baseName = Path.GetFileNameWithoutExtension(localPath);
        var ext = Path.GetExtension(localPath);
        var ts = DateTime.Now.ToString("yyyy-MM-dd HHmmss");
        var machine = Environment.MachineName;
        var conflictName = $"{baseName} (conflict from {machine} {ts}){ext}";
        var conflictPath = Path.Combine(dir, conflictName);
        for (int n = 2; File.Exists(conflictPath); n++)
        {
            conflictName = $"{baseName} (conflict from {machine} {ts} {n}){ext}";
            conflictPath = Path.Combine(dir, conflictName);
        }

        var conflictRel = PathUtil.NormalizeRelative(PathUtil.ParentOf(op.RelativePath).TrimEnd('/') + "/" + conflictName);

        _suppressor.Suppress(localPath);
        _suppressor.Suppress(conflictPath);
        try
        {
            File.Move(localPath, conflictPath);
            SyncStateMarker.Mark(conflictPath, SyncOverlayState.Syncing);

            _metadata.Delete(drive.ConfigId, op.RelativePath);
            if (remoteNow != null && !remoteNow.IsFolder)
            {
                remoteNow.Hydrated = false;
                remoteNow.RemoteChangedSinceBaseline = false;
                LocalState.ClearBaseline(remoteNow);
                _metadata.Upsert(remoteNow);
                _placeholders.CreateOrUpdate(drive, remoteNow);
            }

            _queue.Enqueue(new SyncOperation
            {
                Type = SyncOpType.Upload,
                DriveConfigId = drive.ConfigId,
                DriveLetter = drive.Letter,
                RelativePath = conflictRel,
                Priority = drive.Priority,
                FileSizeBytes = new FileInfo(conflictPath).Length,
            });

            _logger.Warning("Conflict on {Path}: local copy kept as {Conflict}, cloud copy kept under the original name",
                op.RelativePath, conflictRel);

            try
            {
                ConflictDetected?.Invoke(new ConflictInfo
                {
                    Drive = drive,
                    OriginalRelativePath = op.RelativePath,
                    ConflictLocalPath = conflictPath,
                    ConflictRelativePath = conflictRel,
                    MachineName = machine,
                    Timestamp = DateTime.Now,
                });
            }
            catch { }

            return Task.FromResult(true);
        }
        catch (Exception ex)
        {
            _logger.Warning(ex, "Could not set aside conflicting copy of {Path}", op.RelativePath);
            return Task.FromResult(false);
        }
        finally
        {
            _suppressor.Release(localPath);
            _suppressor.Release(conflictPath);
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
