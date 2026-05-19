using System;
using LiteDB;

namespace OneSync.Sync;

internal sealed class DeletedItem
{
    [BsonId]
    public string Key { get; set; } = string.Empty;
    public string RemoteItemId { get; set; } = string.Empty;
    public string DriveConfigId { get; set; } = string.Empty;
    public string DriveLetter { get; set; } = string.Empty;
    public string RelativePath { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public bool IsFolder { get; set; }
    public long Size { get; set; }
    public DateTime DeletedAtUtc { get; set; }
    public DateTime LastModifiedDateTime { get; set; }

    public static string MakeKey(string driveConfigId, string remoteItemId) =>
        $"{driveConfigId}::{remoteItemId}";
}
