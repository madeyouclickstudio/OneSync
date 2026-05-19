using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Graph;
using Microsoft.Graph.Models;
using Microsoft.Graph.Models.ODataErrors;
using OneSync.Config;
using OneSync.FileSystem;
using OneSync.Shell;
using OneSync.State;
using OneSync.Util;
using Serilog;

namespace OneSync.Sync;

/// <summary>
/// Drains the SyncQueue: uploads, deletes, renames against Graph API.
/// Multiple workers can run concurrently for parallel uploads.
/// </summary>
internal sealed class UploadWorker
{
    private const int SimpleUploadMaxBytes = 4 * 1024 * 1024; // < 4 MB = simple PUT
    private const int ResumableChunkBytes = 10 * 1024 * 1024; // 10 MB chunks for resumable

    private readonly SyncQueue _queue;
    private readonly MetadataStore _metadata;
    private readonly GraphHttpClient _graph;
    private readonly ConflictResolver? _conflictResolver;
    private readonly Dictionary<string, DriveConfig> _drivesById;
    private readonly QuotaCache _quotaCache;
    private readonly SyncSettings _settings;
    private readonly ILogger _logger;

    /// <summary>Fires when an upload begins. (Drive, relativePath, sizeBytes).</summary>
    public event Action<DriveConfig, string, long>? UploadStarted;

    /// <summary>Fires while an upload is in progress (drive, relativePath, bytesUploaded, totalBytes).
    /// For simple uploads (<4MB) you only get one Progress event followed by Completed.</summary>
    public event Action<DriveConfig, string, long, long>? UploadProgress;

    /// <summary>Fires when an upload completes successfully.</summary>
    public event Action<DriveConfig, string, long>? UploadCompleted;

    /// <summary>Fires when an upload finally fails (after retries are exhausted).</summary>
    public event Action<DriveConfig, string, string>? UploadFailed;

    /// <summary>Fires when a non-upload sync operation (delete/rename) begins.</summary>
    public event Action<DriveConfig, string, string>? SyncOpStarted;

    /// <summary>Fires when a non-upload sync operation completes successfully.</summary>
    public event Action<DriveConfig, string, string>? SyncOpCompleted;

    private PauseStateStore? _pause;
    public void SetPauseStore(PauseStateStore pause) => _pause = pause;

    public UploadWorker(
        SyncQueue queue,
        MetadataStore metadata,
        GraphHttpClient graph,
        IEnumerable<DriveConfig> drives,
        QuotaCache quotaCache,
        SyncSettings settings,
        ILogger logger,
        ConflictResolver? conflictResolver = null)
    {
        _queue = queue;
        _metadata = metadata;
        _graph = graph;
        _conflictResolver = conflictResolver;
        _quotaCache = quotaCache;
        _settings = settings;
        _logger = logger.ForContext("Component", "UploadWorker");
        _drivesById = new Dictionary<string, DriveConfig>(StringComparer.OrdinalIgnoreCase);
        foreach (var d in drives) _drivesById[d.ConfigId] = d;
    }

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        _logger.Information("UploadWorker started");

        var degree = Math.Max(1, _settings.MaxConcurrentUploads);
        var sem = new SemaphoreSlim(degree, degree);

        while (!cancellationToken.IsCancellationRequested)
        {
            var batch = _queue.GetPending(max: degree * 4);
            if (batch.Count == 0)
            {
                try { await Task.Delay(TimeSpan.FromSeconds(2), cancellationToken); }
                catch (OperationCanceledException) { break; }
                continue;
            }

            if (_pause?.IsPaused() == true)
            {
                _logger.Debug("Upload worker skipped (paused)");
                try { await Task.Delay(TimeSpan.FromSeconds(2), cancellationToken); }
                catch (OperationCanceledException) { break; }
                continue;
            }

            var tasks = new List<Task>(batch.Count);
            foreach (var op in batch)
            {
                if (cancellationToken.IsCancellationRequested) break;
                await sem.WaitAsync(cancellationToken);
                tasks.Add(Task.Run(async () =>
                {
                    try { await ProcessAsync(op, cancellationToken); }
                    finally { sem.Release(); }
                }, cancellationToken));
            }

            try { await Task.WhenAll(tasks); }
            catch (OperationCanceledException) { /* exit */ }
            catch (Exception ex) { _logger.Warning(ex, "Batch processing had exceptions"); }
        }

        _logger.Information("UploadWorker stopped");
    }

    /// <summary>
    /// Forces a drain pass with a hard timeout. Used at graceful shutdown.
    /// </summary>
    public async Task FlushAsync(CancellationToken cancellationToken)
    {
        var degree = Math.Max(1, _settings.MaxConcurrentUploads);
        var sem = new SemaphoreSlim(degree, degree);

        while (!cancellationToken.IsCancellationRequested && _queue.CountPending() > 0)
        {
            var batch = _queue.GetPending(max: degree * 4);
            if (batch.Count == 0) break;

            var tasks = new List<Task>(batch.Count);
            foreach (var op in batch)
            {
                if (cancellationToken.IsCancellationRequested) break;
                await sem.WaitAsync(cancellationToken);
                tasks.Add(Task.Run(async () =>
                {
                    try { await ProcessAsync(op, cancellationToken); }
                    finally { sem.Release(); }
                }, cancellationToken));
            }

            try { await Task.WhenAll(tasks); }
            catch { /* swallow - we still want to keep flushing */ }
        }
    }

    private async Task ProcessAsync(SyncOperation op, CancellationToken ct)
    {
        if (!_drivesById.TryGetValue(op.DriveConfigId, out var drive))
        {
            _logger.Warning("Drive '{Id}' not found - failing op", op.DriveConfigId);
            _queue.MarkFailed(op, "drive not found");
            return;
        }

        _queue.MarkInProgress(op);
        MarkOpFile(op, drive, SyncOverlayState.Syncing);

        try
        {
            switch (op.Type)
            {
                case SyncOpType.Upload:
                    await UploadAsync(op, drive, ct);
                    break;
                case SyncOpType.RemoteDelete:
                    try { SyncOpStarted?.Invoke(drive, op.RelativePath, "Deleting"); } catch { }
                    try { await RemoteDeleteAsync(op, drive, ct); }
                    finally { try { SyncOpCompleted?.Invoke(drive, op.RelativePath, "Deleting"); } catch { } }
                    break;
                case SyncOpType.RemoteRename:
                    try { SyncOpStarted?.Invoke(drive, op.RelativePath, "Renaming"); } catch { }
                    try { await RemoteRenameAsync(op, drive, ct); }
                    finally { try { SyncOpCompleted?.Invoke(drive, op.RelativePath, "Renaming"); } catch { } }
                    break;
                default:
                    _queue.MarkFailed(op, $"unsupported op type: {op.Type}");
                    MarkOpFile(op, drive, SyncOverlayState.Error);
                    return;
            }

            _queue.MarkCompleted(op);
            MarkOpFile(op, drive, SyncOverlayState.Synced);
            _quotaCache.Invalidate(drive);
        }
        catch (PermissionDeniedException pde)
        {
            // 403 is permanent - admin needs to grant access. Mark Failed without retrying.
            _logger.Warning("Permission denied: {Detail}", pde.Message);
            _queue.MarkFailed(op, pde.Message);
            MarkOpFile(op, drive, SyncOverlayState.Error);
            try { UploadFailed?.Invoke(drive, op.RelativePath,
                "You don't have permission to save to this folder. Ask your administrator."); } catch { }
            return;
        }
        catch (RemoteConflictException conflict)
        {
            // The cloud version has moved on since we last synced this file. Hand off
            // to ConflictResolver: it renames the local file to "(conflict from <machine> ...).<ext>"
            // and queues the renamed copy as a new upload, then re-hydrates the canonical path.
            _logger.Warning("Remote conflict on {Path}: {Detail}", op.RelativePath, conflict.Message);
            if (_conflictResolver != null)
            {
                var resolved = await _conflictResolver.ResolveAsync(drive, op);
                if (resolved)
                    _queue.MarkCompleted(op);
                else
                    _queue.MarkFailed(op, "conflict resolution failed");
            }
            else
            {
                _logger.Error("No ConflictResolver wired - marking op failed: {Op}", op);
                _queue.MarkFailed(op, conflict.Message);
                MarkOpFile(op, drive, SyncOverlayState.Error);
            }
        }
        catch (OperationCanceledException)
        {
            _logger.Information("Op cancelled: {Op}", op);
            _queue.MarkRetry(op, TimeSpan.FromSeconds(_settings.RetryBackoffBaseSeconds), "cancelled");
        }
        catch (ODataError ex) when (ex.ResponseStatusCode == 429)
        {
            var retryAfter = TimeSpan.FromSeconds(_settings.RetryBackoffBaseSeconds);
            _logger.Warning("Throttled (429) for {Op} - retrying in {Delay}", op, retryAfter);
            _queue.MarkRetry(op, retryAfter, "throttled (429)");
        }
        catch (ODataError ex) when (ex.ResponseStatusCode == 401 || ex.ResponseStatusCode == 403)
        {
            var status = ex.ResponseStatusCode;
            _logger.Warning("Auth/permission error {Status} for {Op}: {Msg}",
                status, op, ex.Error?.Message);

            if (op.RetryCount >= _settings.MaxRetries)
                _queue.MarkFailed(op, $"persistent {status}: {ex.Error?.Message}");
            else
                _queue.MarkRetry(op, ComputeBackoff(op.RetryCount), $"auth {status}");
        }
        catch (Exception ex)
        {
            if (op.RetryCount >= _settings.MaxRetries)
            {
                _logger.Error(ex, "Op failed after {Retries} retries: {Op}", op.RetryCount, op);
                _queue.MarkFailed(op, ex.Message);
                MarkOpFile(op, drive, SyncOverlayState.Error);
                try { UploadFailed?.Invoke(drive, op.RelativePath, ex.Message); } catch { }
            }
            else
            {
                var backoff = ComputeBackoff(op.RetryCount);
                _logger.Warning(ex, "Op failed (retry {Retry} in {Backoff}): {Op}",
                    op.RetryCount + 1, backoff, op);
                _queue.MarkRetry(op, backoff, ex.Message);
            }
        }
    }

    private static void MarkOpFile(SyncOperation op, DriveConfig drive, SyncOverlayState state)
    {
        if (op.Type != SyncOpType.Upload) return; // delete/rename ops have no local file to mark
        try
        {
            var winRel = PathUtil.ToWindowsRelative(op.RelativePath);
            var localPath = Path.Combine(drive.LocalRootPath, winRel);
            SyncStateMarker.Mark(localPath, state);
        }
        catch { /* best effort */ }
    }

    private TimeSpan ComputeBackoff(int retryCount)
    {
        var seconds = _settings.RetryBackoffBaseSeconds * Math.Pow(2, retryCount);
        return TimeSpan.FromSeconds(Math.Min(seconds, 3600));
    }

    private async Task UploadAsync(SyncOperation op, DriveConfig drive, CancellationToken ct)
    {
        var winRel = PathUtil.ToWindowsRelative(op.RelativePath);
        var localPath = Path.Combine(drive.LocalRootPath, winRel);

        if (!File.Exists(localPath))
        {
            _logger.Information("Local file missing for upload, skipping: {Path}", localPath);
            return;
        }

        var fi = new FileInfo(localPath);
        var meta = _metadata.Get(op.DriveConfigId, op.RelativePath);

        // Last-line-of-defence: never upload a placeholder (zero bytes when metadata
        // says there's remote content). This protects against:
        //   - queue entries that outlived a cleanup cycle
        //   - LRU eviction races where a watcher event slipped past the suppressor
        //   - any future code path that accidentally produces a 0-byte upload
        // for a non-empty file.
        // If this branch is ever hit, the cloud copy is preserved (we don't upload).
        if (fi.Length == 0)
        {
            if (meta != null && !meta.IsFolder && meta.Size > 0)
            {
                _logger.Warning(
                    "Refusing to upload 0-byte local against {RemoteSize}-byte remote " +
                    "(probably an evicted placeholder or stale queue entry): {Path}",
                    meta.Size, op.RelativePath);
                return;
            }
        }

        // If the local file looks identical to the known remote version, skip.
        if (fi.Length > 0)
        {
            if (meta != null && !meta.IsFolder && meta.Hydrated && fi.Length == meta.Size)
            {
                _logger.Debug("Skipping upload of unmodified hydrated file: {Path}", op.RelativePath);
                return;
            }
        }

        op.FileSizeBytes = fi.Length;

        // Look up the eTag we recorded last time we synced this file. If we have
        // one, the upload sends If-Match: <etag> so the server rejects with 412
        // if the cloud version has been updated by another machine since we last
        // saw it. That 412 triggers ConflictResolver instead of clobbering.
        var ifMatchETag = meta?.ETag;

        try { UploadStarted?.Invoke(drive, op.RelativePath, fi.Length); } catch { }

        if (fi.Length < SimpleUploadMaxBytes)
            await SimpleUploadAsync(op, drive, localPath, ifMatchETag, ct);
        else
            await ResumableUploadAsync(op, drive, localPath, fi.Length, ifMatchETag, ct);

        try { UploadCompleted?.Invoke(drive, op.RelativePath, fi.Length); } catch { }
    }

    private async Task SimpleUploadAsync(SyncOperation op, DriveConfig drive, string localPath, string? ifMatchETag, CancellationToken ct)
    {
        var url = BuildContentUrl(drive, op.RelativePath);

        // Read whole file into memory up-front so retries can rebuild the
        // request with fresh ByteArrayContent (HttpContent and HttpRequestMessage
        // are both single-use). SimpleUploadMaxBytes caps this at ~4MB.
        var bytes = await File.ReadAllBytesAsync(localPath, ct);
        var capturedIfMatch = ifMatchETag;

        using var resp = await _graph.SendAsync(() =>
        {
            var content = new ByteArrayContent(bytes);
            content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
            var r = new HttpRequestMessage(HttpMethod.Put, url) { Content = content };
            if (!string.IsNullOrEmpty(capturedIfMatch))
                r.Headers.TryAddWithoutValidation("If-Match", capturedIfMatch);
            return r;
        }, ct, HttpCompletionOption.ResponseHeadersRead);

        if (resp.StatusCode == HttpStatusCode.PreconditionFailed)
            throw new RemoteConflictException(op.RelativePath, ifMatchETag);

        if (resp.StatusCode == HttpStatusCode.Forbidden)
        {
            var body = await resp.Content.ReadAsStringAsync(ct);
            throw new PermissionDeniedException("upload", op.RelativePath, ExtractServerMessage(body));
        }

        if (!resp.IsSuccessStatusCode)
        {
            var body = await resp.Content.ReadAsStringAsync(ct);
            throw new HttpRequestException(
                $"Simple upload failed: {(int)resp.StatusCode} {resp.ReasonPhrase} - {Truncate(body, 500)}",
                inner: null, statusCode: resp.StatusCode);
        }

        var json = await resp.Content.ReadAsStringAsync(ct);
        var item = System.Text.Json.JsonDocument.Parse(json).RootElement;
        string? newId = null;
        string? newETag = null;
        if (item.TryGetProperty("id", out var idProp)) newId = idProp.GetString();
        if (item.TryGetProperty("eTag", out var etagProp)) newETag = etagProp.GetString();
        op.RemoteItemId = newId;
        if (newETag != null)
            _metadata.UpdateETag(op.DriveConfigId, op.RelativePath, newETag, newId, op.FileSizeBytes);

        _logger.Information("Upload complete: {Drive}:{Path} -> remote {Id} ({Size} bytes)",
            drive.Letter, op.RelativePath, op.RemoteItemId, op.FileSizeBytes);
    }

    private async Task ResumableUploadAsync(SyncOperation op, DriveConfig drive, string localPath,
        long fileSize, string? ifMatchETag, CancellationToken ct)
    {
        var sessionUrl = BuildCreateUploadSessionUrl(drive, op.RelativePath);

        // 1) Create upload session (authenticated). If we have an etag, ask the
        // server to fail the session creation when the cloud version differs.
        var capturedSessionIfMatch = ifMatchETag;
        using var sessionResp = await _graph.SendAsync(() =>
        {
            var r = new HttpRequestMessage(HttpMethod.Post, sessionUrl);
            if (!string.IsNullOrEmpty(capturedSessionIfMatch))
                r.Headers.TryAddWithoutValidation("If-Match", capturedSessionIfMatch);
            r.Content = new StringContent(
                "{\"item\":{\"@microsoft.graph.conflictBehavior\":\"replace\"}}",
                System.Text.Encoding.UTF8, "application/json");
            return r;
        }, ct);
        if (sessionResp.StatusCode == HttpStatusCode.PreconditionFailed)
            throw new RemoteConflictException(op.RelativePath, ifMatchETag);
        if (sessionResp.StatusCode == HttpStatusCode.Forbidden)
        {
            var body = await sessionResp.Content.ReadAsStringAsync(ct);
            throw new PermissionDeniedException("upload", op.RelativePath, ExtractServerMessage(body));
        }
        if (!sessionResp.IsSuccessStatusCode)
        {
            var body = await sessionResp.Content.ReadAsStringAsync(ct);
            throw new HttpRequestException(
                $"createUploadSession failed: {(int)sessionResp.StatusCode} {sessionResp.ReasonPhrase} - {Truncate(body, 500)}",
                inner: null, statusCode: sessionResp.StatusCode);
        }

        var sessionJson = await sessionResp.Content.ReadAsStringAsync(ct);
        var uploadUrl = System.Text.Json.JsonDocument.Parse(sessionJson)
            .RootElement.GetProperty("uploadUrl").GetString()
            ?? throw new InvalidOperationException("uploadUrl missing in session response");

        _logger.Information("Resumable upload session created for {Path} ({Size} bytes)",
            op.RelativePath, fileSize);

        // 2) Upload chunks via the pre-signed session URL (no Authorization needed)
        using var fs = new FileStream(localPath, FileMode.Open, System.IO.FileAccess.Read, FileShare.ReadWrite);
        var buffer = new byte[ResumableChunkBytes];
        long offset = 0;

        while (offset < fileSize)
        {
            ct.ThrowIfCancellationRequested();
            var toRead = (int)Math.Min(ResumableChunkBytes, fileSize - offset);
            var actuallyRead = await fs.ReadAsync(buffer.AsMemory(0, toRead), ct);
            if (actuallyRead <= 0) break;

            // Snapshot the chunk into its own buffer so retries can rebuild
            // ByteArrayContent without re-reading the file stream.
            var chunkBytes = new byte[actuallyRead];
            Buffer.BlockCopy(buffer, 0, chunkBytes, 0, actuallyRead);
            var chunkOffset = offset;
            var chunkLen = actuallyRead;

            HttpResponseMessage chunkResp;
            try
            {
                chunkResp = await _graph.SendPreSignedAsync(() =>
                {
                    var content = new ByteArrayContent(chunkBytes);
                    content.Headers.ContentLength = chunkLen;
                    content.Headers.ContentRange = new ContentRangeHeaderValue(chunkOffset, chunkOffset + chunkLen - 1, fileSize);
                    return new HttpRequestMessage(HttpMethod.Put, uploadUrl) { Content = content };
                }, ct);
            }
            catch (Exception ex)
            {
                throw new HttpRequestException(
                    $"chunk upload at offset {offset} failed: {ex.Message}", ex);
            }

            // Take ownership of the chunk response: dispose unconditionally
            // when we leave this iteration of the loop, including via throw.
            // Without this, every chunk leaks an HttpResponseMessage and the
            // connection pool can exhaust under sustained upload load.
            try
            {
                if (chunkResp.StatusCode == HttpStatusCode.Created ||
                    chunkResp.StatusCode == HttpStatusCode.OK)
                {
                    var doneJson = await chunkResp.Content.ReadAsStringAsync(ct);
                    var doneItem = System.Text.Json.JsonDocument.Parse(doneJson).RootElement;
                    string? newId = null, newETag = null;
                    if (doneItem.TryGetProperty("id", out var idProp)) newId = idProp.GetString();
                    if (doneItem.TryGetProperty("eTag", out var etagProp)) newETag = etagProp.GetString();
                    op.RemoteItemId = newId;
                    if (newETag != null)
                        _metadata.UpdateETag(op.DriveConfigId, op.RelativePath, newETag, newId, op.FileSizeBytes);
                    offset += actuallyRead;
                    _logger.Information("Resumable upload complete: {Drive}:{Path} -> remote {Id}",
                        drive.Letter, op.RelativePath, op.RemoteItemId);
                    break;
                }
                else if (chunkResp.StatusCode == HttpStatusCode.Accepted)
                {
                    offset += actuallyRead;
                    _logger.Debug("Chunk uploaded: {Offset}/{Total}", offset, fileSize);
                    try { UploadProgress?.Invoke(drive, op.RelativePath, offset, fileSize); } catch { }
                }
                else
                {
                    var body = await chunkResp.Content.ReadAsStringAsync(ct);
                    throw new HttpRequestException(
                        $"chunk upload at offset {offset} failed: {(int)chunkResp.StatusCode} - {Truncate(body, 500)}",
                        inner: null, statusCode: chunkResp.StatusCode);
                }
            }
            finally
            {
                chunkResp.Dispose();
            }
        }
    }

    private async Task RemoteDeleteAsync(SyncOperation op, DriveConfig drive, CancellationToken ct)
    {
        var url = BuildItemUrl(drive, op.RelativePath);
        using var resp = await _graph.SendAsync(
            () => new HttpRequestMessage(HttpMethod.Delete, url), ct);

        if (resp.IsSuccessStatusCode || resp.StatusCode == HttpStatusCode.NotFound)
        {
            _logger.Information("Remote delete OK: {Drive}:{Path}", drive.Letter, op.RelativePath);
            return;
        }

        var body = await resp.Content.ReadAsStringAsync(ct);
        throw new HttpRequestException(
            $"remote delete failed: {(int)resp.StatusCode} - {Truncate(body, 500)}",
            inner: null, statusCode: resp.StatusCode);
    }

    private async Task RemoteRenameAsync(SyncOperation op, DriveConfig drive, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(op.NewRelativePath))
        {
            _queue.MarkFailed(op, "rename op missing NewRelativePath");
            return;
        }

        var remoteId = op.RemoteItemId;
        if (string.IsNullOrEmpty(remoteId))
        {
            var meta = _metadata.Get(drive.ConfigId, op.RelativePath);
            remoteId = meta?.RemoteItemId;
        }

        if (string.IsNullOrEmpty(remoteId))
        {
            _logger.Information(
                "No remote item for rename source {Path} — converting to upload at new path {New}",
                op.RelativePath, op.NewRelativePath);
            var localPath = Path.Combine(drive.LocalRootPath,
                op.NewRelativePath.TrimStart('/').Replace('/', Path.DirectorySeparatorChar));
            if (File.Exists(localPath))
            {
                _queue.Enqueue(new SyncOperation
                {
                    Type = SyncOpType.Upload,
                    DriveConfigId = op.DriveConfigId,
                    DriveLetter = op.DriveLetter,
                    RelativePath = op.NewRelativePath,
                    Priority = op.Priority,
                    FileSizeBytes = new FileInfo(localPath).Length,
                });
            }
            return;
        }

        string url;
        if (drive.IsOneDrive)
            url = $"https://graph.microsoft.com/v1.0/me/drive/items/{remoteId}";
        else
            url = $"https://graph.microsoft.com/v1.0/drives/{drive.ResolvedDriveId}/items/{remoteId}";

        var newName = Path.GetFileName(op.NewRelativePath.Replace('/', Path.DirectorySeparatorChar));
        var newParentPath = PathUtil.NormalizeRelative(
            Path.GetDirectoryName(op.NewRelativePath.Replace('/', Path.DirectorySeparatorChar))?
                .Replace(Path.DirectorySeparatorChar, '/') ?? "/");

        var body = "{\"name\":\"" + System.Web.HttpUtility.JavaScriptStringEncode(newName) + "\"";
        if (newParentPath != PathUtil.NormalizeRelative(
            Path.GetDirectoryName(op.RelativePath.Replace('/', Path.DirectorySeparatorChar))?
                .Replace(Path.DirectorySeparatorChar, '/') ?? "/"))
        {
            var parentItemUrl = BuildItemUrl(drive, newParentPath);
            using var parentResp = await _graph.SendAsync(
                () => new HttpRequestMessage(HttpMethod.Get, parentItemUrl), ct);
            if (parentResp.IsSuccessStatusCode)
            {
                var parentJson = await parentResp.Content.ReadAsStringAsync(ct);
                var parentId = System.Text.Json.JsonDocument.Parse(parentJson).RootElement.GetProperty("id").GetString();
                body += ",\"parentReference\":{\"id\":\"" + parentId + "\"}";
            }
        }
        body += "}";

        var capturedBody = body;
        using var resp = await _graph.SendAsync(() =>
            new HttpRequestMessage(new HttpMethod("PATCH"), url)
            {
                Content = new StringContent(capturedBody, System.Text.Encoding.UTF8, "application/json"),
            }, ct);
        if (!resp.IsSuccessStatusCode)
        {
            var respBody = await resp.Content.ReadAsStringAsync(ct);
            throw new HttpRequestException(
                $"remote rename failed: {(int)resp.StatusCode} - {Truncate(respBody, 500)}",
                inner: null, statusCode: resp.StatusCode);
        }
        _logger.Information("Remote rename OK: {Drive}:{Old} -> {New}",
            drive.Letter, op.RelativePath, op.NewRelativePath);
    }

    // --- URL builders ---

    private string BuildItemByIdOrPath(DriveConfig drive, SyncOperation op)
    {
        var remoteId = op.RemoteItemId;
        if (string.IsNullOrEmpty(remoteId))
        {
            var meta = _metadata.Get(drive.ConfigId, op.RelativePath);
            remoteId = meta?.RemoteItemId;
        }
        if (!string.IsNullOrEmpty(remoteId))
        {
            if (drive.IsOneDrive)
                return $"https://graph.microsoft.com/v1.0/me/drive/items/{remoteId}";
            return $"https://graph.microsoft.com/v1.0/drives/{drive.ResolvedDriveId}/items/{remoteId}";
        }
        return BuildItemUrl(drive, op.RelativePath);
    }

    private string BuildContentUrl(DriveConfig drive, string relativePath)
    {
        var rel = TrimLeadingSlash(relativePath);
        if (drive.IsOneDrive)
            return string.IsNullOrEmpty(rel)
                ? "https://graph.microsoft.com/v1.0/me/drive/root/content"
                : $"https://graph.microsoft.com/v1.0/me/drive/root:/{Uri.EscapeDataString(rel).Replace("%2F", "/")}:/content";
        var driveId = drive.ResolvedDriveId
            ?? throw new InvalidOperationException("SharePoint drive missing ResolvedDriveId");
        return $"https://graph.microsoft.com/v1.0/drives/{driveId}/root:/{Uri.EscapeDataString(rel).Replace("%2F", "/")}:/content";
    }

    private string BuildCreateUploadSessionUrl(DriveConfig drive, string relativePath)
    {
        var rel = TrimLeadingSlash(relativePath);
        if (drive.IsOneDrive)
            return $"https://graph.microsoft.com/v1.0/me/drive/root:/{Uri.EscapeDataString(rel).Replace("%2F", "/")}:/createUploadSession";
        var driveId = drive.ResolvedDriveId
            ?? throw new InvalidOperationException("SharePoint drive missing ResolvedDriveId");
        return $"https://graph.microsoft.com/v1.0/drives/{driveId}/root:/{Uri.EscapeDataString(rel).Replace("%2F", "/")}:/createUploadSession";
    }

    private string BuildItemUrl(DriveConfig drive, string relativePath)
    {
        var rel = TrimLeadingSlash(relativePath);
        if (drive.IsOneDrive)
            return string.IsNullOrEmpty(rel)
                ? "https://graph.microsoft.com/v1.0/me/drive/root"
                : $"https://graph.microsoft.com/v1.0/me/drive/root:/{Uri.EscapeDataString(rel).Replace("%2F", "/")}";
        var driveId = drive.ResolvedDriveId
            ?? throw new InvalidOperationException("SharePoint drive missing ResolvedDriveId");
        return string.IsNullOrEmpty(rel)
            ? $"https://graph.microsoft.com/v1.0/drives/{driveId}/root"
            : $"https://graph.microsoft.com/v1.0/drives/{driveId}/root:/{Uri.EscapeDataString(rel).Replace("%2F", "/")}";
    }

    private static string TrimLeadingSlash(string p) => p.TrimStart('/');

    private static string Truncate(string s, int maxLen) =>
        s.Length <= maxLen ? s : s.Substring(0, maxLen) + "...";

    private static string? ExtractServerMessage(string body)
    {
        if (string.IsNullOrWhiteSpace(body)) return null;
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(body);
            if (doc.RootElement.TryGetProperty("error", out var err) &&
                err.TryGetProperty("message", out var msg))
                return msg.GetString();
        }
        catch { /* not JSON or different shape */ }
        return Truncate(body, 200);
    }
}
