using System;
using System.Globalization;
using System.Text.Json;
using OneSync.Config;
using OneSync.Util;

namespace OneSync.Sync;

/// <summary>What the server told us about an item after we uploaded or created it.</summary>
internal sealed class UploadResult
{
    public string? ETag { get; init; }
    public string? CTag { get; init; }
    public string? RemoteItemId { get; init; }
    public string? ParentRemoteItemId { get; init; }
    public string? WebUrl { get; init; }
    public string? QuickXorHash { get; init; }
    public long Size { get; init; } = -1;
    public DateTime LastModified { get; init; }
    public DateTime Created { get; init; }

    public static UploadResult FromJson(JsonElement item) => new()
    {
        ETag = GraphItem.Str(item, "eTag"),
        CTag = GraphItem.Str(item, "cTag"),
        RemoteItemId = GraphItem.Str(item, "id"),
        ParentRemoteItemId = item.TryGetProperty("parentReference", out var pr) ? GraphItem.Str(pr, "id") : null,
        WebUrl = GraphItem.Str(item, "webUrl"),
        QuickXorHash = GraphItem.Hash(item),
        Size = item.TryGetProperty("size", out var sz) && sz.ValueKind == JsonValueKind.Number ? sz.GetInt64() : -1,
        LastModified = GraphItem.FileTime(item, "lastModifiedDateTime"),
        Created = GraphItem.FileTime(item, "createdDateTime"),
    };
}

/// <summary>Reads Graph driveItem JSON into a <see cref="RemoteItem"/>.</summary>
internal static class GraphItem
{
    /// <summary>
    /// Parses a driveItem. <paramref name="parentRelativePath"/> is used when known
    /// (folder listings); otherwise the path comes from parentReference.path.
    /// Returns null for the drive root, the recycle bin and unnamed items.
    /// </summary>
    public static RemoteItem? Parse(DriveConfig drive, JsonElement item, string? parentRelativePath = null)
    {
        var name = Str(item, "name") ?? "";
        if (item.TryGetProperty("root", out _)) return null;
        if (string.IsNullOrEmpty(name)) return null;

        string parent = parentRelativePath ?? ParentPathFromReference(item);
        var rel = PathUtil.NormalizeRelative(parent.TrimEnd('/') + "/" + name);
        if (rel.Contains("/$RECYCLE.BIN", StringComparison.OrdinalIgnoreCase)) return null;

        return new RemoteItem
        {
            DriveConfigId = drive.ConfigId,
            RemoteItemId = Str(item, "id") ?? "",
            ParentRemoteItemId = item.TryGetProperty("parentReference", out var pr) ? Str(pr, "id") ?? "" : "",
            RelativePath = rel,
            Name = name,
            IsFolder = item.TryGetProperty("folder", out _) || item.TryGetProperty("package", out _),
            Size = item.TryGetProperty("size", out var sz) && sz.ValueKind == JsonValueKind.Number ? sz.GetInt64() : 0,
            CreatedDateTime = FileTime(item, "createdDateTime"),
            LastModifiedDateTime = FileTime(item, "lastModifiedDateTime"),
            ETag = Str(item, "eTag"),
            CTag = Str(item, "cTag"),
            ContentHash = Hash(item),
            WebUrl = Str(item, "webUrl"),
        };
    }

    private static string ParentPathFromReference(JsonElement item)
    {
        if (!item.TryGetProperty("parentReference", out var pr) ||
            !pr.TryGetProperty("path", out var p))
            return "/";

        // "/drive/root:/Documents/Sub" or "/drives/{id}/root:/Documents/Sub", percent-encoded
        var raw = p.GetString() ?? "";
        const string marker = "/root:";
        var idx = raw.IndexOf(marker, StringComparison.Ordinal);
        var parent = idx >= 0 ? raw[(idx + marker.Length)..] : "";
        parent = Uri.UnescapeDataString(parent);
        return string.IsNullOrEmpty(parent) ? "/" : parent;
    }

    internal static string? Str(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    internal static string? Hash(JsonElement item)
    {
        if (!item.TryGetProperty("file", out var file) || !file.TryGetProperty("hashes", out var hashes))
            return null;
        return Str(hashes, "quickXorHash") ?? Str(hashes, "sha256Hash");
    }

    /// <summary>
    /// The file's own time (fileSystemInfo, which is what the desktop app set and
    /// what Explorer should show), falling back to when the server last changed it.
    /// Always UTC.
    /// </summary>
    internal static DateTime FileTime(JsonElement item, string name)
    {
        if (item.TryGetProperty("fileSystemInfo", out var fsi) && TryParseUtc(Str(fsi, name), out var t))
            return t;
        return TryParseUtc(Str(item, name), out var t2) ? t2 : default;
    }

    private static bool TryParseUtc(string? s, out DateTime value)
    {
        value = default;
        if (string.IsNullOrEmpty(s)) return false;
        if (!DateTime.TryParse(s, CultureInfo.InvariantCulture,
                DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out value))
            return false;
        value = DateTime.SpecifyKind(value, DateTimeKind.Utc);
        return value.Year > 1980;
    }
}
