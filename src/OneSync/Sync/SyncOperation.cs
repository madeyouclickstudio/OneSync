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

    public long FileSizeBytes { get; set; }
    public string? ContentHash { get; set; }

    // For uploads: the remote item ID we expect to update
    public string? RemoteItemId { get; set; }

    public override string ToString() =>
        $"{Type} {DriveLetter}:{RelativePath} status={Status} retry={RetryCount}";
}
