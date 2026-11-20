using System;
using LiteDB;

namespace OneSync.Sync;

internal enum SyncOpType
{
    Upload,
    Download,
    Delete,
    Rename,
    RemoteDelete,
    RemoteRename,
    RemoteCreateFolder,
}

internal enum SyncOpStatus
{
    Pending,
    InProgress,
    Completed,
    Failed,
    Retry,
}

internal sealed class SyncOperation
{
    [BsonId]
    public ObjectId Id { get; set; } = ObjectId.NewObjectId();

    public string DriveConfigId { get; set; } = string.Empty;
    public string DriveLetter { get; set; } = string.Empty;

    // POSIX-style relative path, leading slash: "/Documents/homework.docx"
    public string RelativePath { get; set; } = "/";

    // For renames: the new path
    public string? NewRelativePath { get; set; }

    public SyncOpType Type { get; set; }
    public SyncOpStatus Status { get; set; } = SyncOpStatus.Pending;
    public int Priority { get; set; } = 100;

    public DateTime QueuedAt { get; set; } = DateTime.UtcNow;
    public DateTime? StartedAt { get; set; }
    public DateTime? CompletedAt { get; set; }
    public DateTime? NextAttemptAt { get; set; }

    public int RetryCount { get; set; }
    public string? ErrorMessage { get; set; }

    /// <summary>Failed for a reason retrying won't fix (access denied, too large).
    /// Other failures are retried later.</summary>
    public bool PermanentFailure { get; set; }

    /// <summary>For a file delete: the eTag of the version deleted locally, so a
    /// newer cloud version isn't deleted with it.</summary>
    public string? ETag { get; set; }

    /// <summary>Queued to undo what a first upload still in progress creates. If
    /// that upload left no record, there is nothing of ours to delete.</summary>
    public bool FollowsUpload { get; set; }

    public long FileSizeBytes { get; set; }
    public string? ContentHash { get; set; }

    // For uploads: the remote item ID we expect to update
    public string? RemoteItemId { get; set; }

    public override string ToString() =>
        $"{Type} {DriveLetter}:{RelativePath} status={Status} retry={RetryCount}";
}
