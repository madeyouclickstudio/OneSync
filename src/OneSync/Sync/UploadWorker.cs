using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Linq;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Graph.Models.ODataErrors;
using OneSync.Config;
using OneSync.FileSystem;
using OneSync.Shell;
using OneSync.State;
using OneSync.Util;
using Serilog;

namespace OneSync.Sync;

/// <summary>
/// Drains the SyncQueue: uploads, folder creates, deletes and renames against
/// Graph. Independent operations run in parallel; the queue holds back anything
/// that has to wait for an earlier change to the same path.
/// </summary>
internal sealed class UploadWorker
{
    private const int SimpleUploadMaxBytes = 4 * 1024 * 1024;
    private const int ResumableChunkBytes = 10 * 1024 * 1024; // multiple of 320 KiB, as Graph requires

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
    /// For simple uploads (&lt;4MB) you only get Started followed by Completed.</summary>
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

        while (!cancellationToken.IsCancellationRequested)
        {
            if (_pause?.IsPaused() == true)
            {
                try { await Task.Delay(TimeSpan.FromSeconds(2), cancellationToken); }
                catch (OperationCanceledException) { break; }
                continue;
            }

            var batch = _queue.GetPending(max: degree * 4);
            if (batch.Count == 0)
            {
                try { await Task.Delay(TimeSpan.FromSeconds(2), cancellationToken); }
                catch (OperationCanceledException) { break; }
                continue;
            }

            try { await RunBatchAsync(batch, degree, cancellationToken); }
            catch (OperationCanceledException) { break; }
            catch (Exception ex) { _logger.Warning(ex, "Batch processing had exceptions"); }
        }

        _logger.Information("UploadWorker stopped");
    }

    /// <summary>
    /// Sends everything still queued, ignoring retry back-off, until the queue is
    /// empty, nothing more can be sent, or the token fires. Used at shutdown.
    /// </summary>
    public async Task FlushAsync(CancellationToken cancellationToken)
    {
        var degree = Math.Max(1, _settings.MaxConcurrentUploads);
        var attempted = new HashSet<LiteDB.ObjectId>();

        while (!cancellationToken.IsCancellationRequested)
        {
            var batch = _queue.GetPending(max: degree * 4, ignoreBackoff: true)
                .FindAll(o => !attempted.Contains(o.Id));
            if (batch.Count == 0) break;
            foreach (var op in batch) attempted.Add(op.Id);

            try { await RunBatchAsync(batch, degree, cancellationToken); }
            catch { }
        }
    }

    private async Task RunBatchAsync(List<SyncOperation> batch, int degree, CancellationToken ct)
    {
        using var sem = new SemaphoreSlim(degree, degree);
        var tasks = new List<Task>(batch.Count);
        foreach (var op in batch)
        {
            if (ct.IsCancellationRequested) break;
            await sem.WaitAsync(ct);
            tasks.Add(Task.Run(async () =>
            {
                try { await ProcessAsync(op, ct); }
                finally { sem.Release(); }
            }, CancellationToken.None));
        }
        await Task.WhenAll(tasks);
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
                case SyncOpType.RemoteCreateFolder:
                    await EnsureRemoteFolderAsync(drive, op.RelativePath, ct);
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

            if (op.Status == SyncOpStatus.InProgress)
            {
                _queue.MarkCompleted(op);
                MarkOpFile(op, drive, SyncOverlayState.Synced);
            }
            _quotaCache.Invalidate(drive);
        }
        catch (PermissionDeniedException pde)
        {
            // 403 is permanent until someone grants access.
            _logger.Warning("Permission denied: {Detail}", pde.Message);
            _queue.MarkFailed(op, pde.Message);
            MarkOpFile(op, drive, SyncOverlayState.Error);
            try { UploadFailed?.Invoke(drive, op.RelativePath,
                "You don't have permission to save to this folder. Ask your administrator."); } catch { }
        }
        catch (RemoteConflictException conflict)
        {
            _logger.Warning("Remote conflict on {Path}: {Detail}", op.RelativePath, conflict.Message);
            await HandleConflictAsync(op, drive, ct);
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

    private async Task HandleConflictAsync(SyncOperation op, DriveConfig drive, CancellationToken ct)
    {
        if (_conflictResolver is null)
        {
            _queue.MarkFailed(op, "conflict");
            MarkOpFile(op, drive, SyncOverlayState.Error);
            return;
        }

        RemoteItem? remote = null;
        try { remote = await GetRemoteItemAsync(drive, op.RelativePath, ct); }
        catch (Exception ex) { _logger.Debug(ex, "Could not fetch cloud copy of {Path}", op.RelativePath); }

        if (await _conflictResolver.ResolveAsync(drive, op, remote))
            _queue.MarkCompleted(op);
        else
            _queue.MarkRetry(op, ComputeBackoff(op.RetryCount), "conflict copy could not be made (file in use?)");
    }

    private static void MarkOpFile(SyncOperation op, DriveConfig drive, SyncOverlayState state)
    {
        if (op.Type != SyncOpType.Upload) return;
        try { SyncStateMarker.Mark(PlaceholderManager.LocalPathFor(drive, op.RelativePath), state); }
        catch { }
    }

    private TimeSpan ComputeBackoff(int retryCount)
    {
        var seconds = _settings.RetryBackoffBaseSeconds * Math.Pow(2, retryCount);
        return TimeSpan.FromSeconds(Math.Min(seconds, 3600));
    }

    // --- uploads ---

    private async Task UploadAsync(SyncOperation op, DriveConfig drive, CancellationToken ct)
    {
        var localPath = PlaceholderManager.LocalPathFor(drive, op.RelativePath);
        var fi = new FileInfo(localPath);

        if (!fi.Exists)
        {
            _logger.Information("Local file gone before upload, skipping: {Path}", op.RelativePath);
            return;
        }
        if (LocalState.IsPlaceholder(fi))
        {
            _logger.Debug("Not uploading placeholder: {Path}", op.RelativePath);
            return;
        }

        var meta = _metadata.Get(op.DriveConfigId, op.RelativePath);
        if (LocalState.IsInterruptedDownload(fi, meta))
        {
            _logger.Debug("Not uploading a download in progress: {Path}", op.RelativePath);
            return;
        }
        if (meta is { IsFolder: true })
        {
            _logger.Warning("{Path} is a folder in the cloud but a file here; uploading as a conflict copy", op.RelativePath);
            throw new RemoteConflictException(op.RelativePath, meta.ETag);
        }

        // An empty, non-sparse file against a non-empty cloud file with no record of
        // being downloaded: a placeholder from an older version, not a user edit.
        if (fi.Length == 0 && meta != null && meta.Size > 0 && !meta.Hydrated && !LocalState.HasBaseline(meta))
        {
            _logger.Warning("Refusing to upload empty file over {Size}-byte cloud copy: {Path}", meta.Size, op.RelativePath);
            return;
        }

        if (fi.Length > _settings.MaxFileSizeMB * 1024L * 1024L)
        {
            _queue.MarkFailed(op, $"larger than the {_settings.MaxFileSizeMB} MB limit");
            return;
        }

        if (meta != null && meta.RemoteChangedSinceBaseline)
        {
            // We hold a local copy and the cloud copy may be different: either both
            // changed, or we have no record of where the local copy came from.
            await ReconcileWithCloudAsync(op, drive, fi, meta, ct);
            return;
        }

        if (meta != null && !LocalState.HasBaseline(meta) && meta.Hydrated && fi.Length == meta.Size
            && !string.IsNullOrEmpty(meta.ContentHash))
        {
            // Record from before baselines were kept: a hash tells us whether this
            // is really an edit.
            if (string.Equals(QuickXorHash.ComputeBase64(localPath), meta.ContentHash, StringComparison.Ordinal))
            {
                _metadata.MarkHydrated(op.DriveConfigId, op.RelativePath, null, fi);
                return;
            }
        }
        else if (meta != null && !LocalState.IsDirty(fi, meta))
        {
            _logger.Debug("Unchanged since last sync: {Path}", op.RelativePath);
            return;
        }

        await SendFileAsync(op, drive, localPath, fi, string.IsNullOrEmpty(meta?.ETag) ? null : meta!.ETag, ct);
    }

    /// <summary>
    /// Uploads the file. With an eTag the cloud rejects the upload if its copy has
    /// changed since; without one it is a new file and must not replace anything
    /// already in the cloud under that name.
    /// </summary>
    private async Task SendFileAsync(SyncOperation op, DriveConfig drive, string localPath, FileInfo fi,
        string? ifMatchETag, CancellationToken ct)
    {
        fi.Refresh();
        long length = fi.Length;
        long writeTicks = fi.LastWriteTimeUtc.Ticks;
        op.FileSizeBytes = length;

        try { UploadStarted?.Invoke(drive, op.RelativePath, length); } catch { }

        UploadResult? result;
        string localHash;
        try
        {
            if (length < SimpleUploadMaxBytes)
                (result, localHash) = await SimpleUploadAsync(op, drive, localPath, ifMatchETag, ct);
            else
                (result, localHash) = await ResumableUploadAsync(op, drive, localPath, length, ifMatchETag, ct);
        }
        catch (NameTakenException)
        {
            var meta = _metadata.Get(op.DriveConfigId, op.RelativePath);
            await ReconcileWithCloudAsync(op, drive, fi, meta, ct);
            return;
        }

        if (result is null) return;

        _metadata.RecordUpload(op.DriveConfigId, op.RelativePath, result, length, writeTicks);
        op.RemoteItemId = result.RemoteItemId;

        if (!string.IsNullOrEmpty(result.QuickXorHash) && result.Size == length &&
            !string.Equals(result.QuickXorHash, localHash, StringComparison.Ordinal))
        {
            _logger.Warning("Upload check: cloud hash of {Path} differs from what was sent; will re-check on next sync",
                op.RelativePath);
        }

        _logger.Information("Upload complete: {Drive}:{Path} -> remote {Id} ({Size} bytes)",
            drive.Letter, op.RelativePath, result.RemoteItemId, length);
        try { UploadCompleted?.Invoke(drive, op.RelativePath, length); } catch { }
    }

    /// <summary>
    /// Settles a local file whose relationship to the cloud copy is unknown. If the
    /// two are identical we just record that; otherwise the local copy goes up as a
    /// conflict copy and the cloud copy keeps the name. Nothing is overwritten.
    /// </summary>
    private async Task ReconcileWithCloudAsync(SyncOperation op, DriveConfig drive, FileInfo fi,
        RemoteItem? meta, CancellationToken ct)
    {
        var remote = await GetRemoteItemAsync(drive, op.RelativePath, ct);
        if (remote is null)
        {
            // Not in the cloud after all: an ordinary new file.
            if (meta != null) _metadata.Delete(op.DriveConfigId, op.RelativePath);
            await SendFileAsync(op, drive, fi.FullName, fi, null, ct);
            return;
        }

        if (!remote.IsFolder && remote.Size == fi.Length && !string.IsNullOrEmpty(remote.ContentHash) &&
            string.Equals(QuickXorHash.ComputeBase64(fi.FullName), remote.ContentHash, StringComparison.Ordinal))
        {
            remote.Hydrated = true;
            remote.PlaceholderCreated = true;
            LocalState.SetBaseline(remote, fi);
            _metadata.Upsert(remote);
            _logger.Information("{Path} already matches the cloud copy", op.RelativePath);
            return;
        }

        if (_conflictResolver is null)
            throw new InvalidOperationException("conflict with cloud copy and no resolver");

        if (!await _conflictResolver.ResolveAsync(drive, op, remote))
            throw new IOException("conflict copy could not be made (file in use?)");
    }

    private async Task<(UploadResult? Result, string LocalHash)> SimpleUploadAsync(
        SyncOperation op, DriveConfig drive, string localPath, string? ifMatchETag, CancellationToken ct)
    {
        var url = BuildContentUrl(drive, op.RelativePath);
        if (ifMatchETag is null) url += "?@microsoft.graph.conflictBehavior=fail";

        byte[] bytes;
        await using (var fs = new FileStream(localPath, FileMode.Open, System.IO.FileAccess.Read,
                         FileShare.ReadWrite | FileShare.Delete, 4096, useAsync: true))
        {
            bytes = new byte[fs.Length];
            int read = 0;
            while (read < bytes.Length)
            {
                int n = await fs.ReadAsync(bytes.AsMemory(read), ct);
                if (n == 0) break;
                read += n;
            }
            if (read != bytes.Length) Array.Resize(ref bytes, read);
        }

        var hasher = new QuickXorHash();
        hasher.Append(bytes);
        var localHash = Convert.ToBase64String(hasher.Finish());

        using var resp = await _graph.SendAsync(() =>
        {
            var content = new ByteArrayContent(bytes);
            content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
            var r = new HttpRequestMessage(HttpMethod.Put, url) { Content = content };
            if (!string.IsNullOrEmpty(ifMatchETag))
                r.Headers.TryAddWithoutValidation("If-Match", ifMatchETag);
            return r;
        }, ct, HttpCompletionOption.ResponseHeadersRead);

        await ThrowForUploadFailureAsync(resp, op, ifMatchETag, "Simple upload", ct);

        var json = await resp.Content.ReadAsStringAsync(ct);
        using var doc = JsonDocument.Parse(json);
        return (UploadResult.FromJson(doc.RootElement), localHash);
    }

    private async Task<(UploadResult? Result, string LocalHash)> ResumableUploadAsync(
        SyncOperation op, DriveConfig drive, string localPath, long fileSize, string? ifMatchETag, CancellationToken ct)
    {
        var sessionUrl = BuildCreateUploadSessionUrl(drive, op.RelativePath);
        var behaviour = ifMatchETag is null ? "fail" : "replace";

        using var sessionResp = await _graph.SendAsync(() =>
        {
            var r = new HttpRequestMessage(HttpMethod.Post, sessionUrl);
            if (!string.IsNullOrEmpty(ifMatchETag))
                r.Headers.TryAddWithoutValidation("If-Match", ifMatchETag);
            r.Content = new StringContent(
                "{\"item\":{\"@microsoft.graph.conflictBehavior\":\"" + behaviour + "\"}}",
                System.Text.Encoding.UTF8, "application/json");
            return r;
        }, ct);
        await ThrowForUploadFailureAsync(sessionResp, op, ifMatchETag, "createUploadSession", ct);

        var sessionJson = await sessionResp.Content.ReadAsStringAsync(ct);
        string uploadUrl;
        using (var sessionDoc = JsonDocument.Parse(sessionJson))
            uploadUrl = sessionDoc.RootElement.GetProperty("uploadUrl").GetString()
                ?? throw new InvalidOperationException("uploadUrl missing in session response");

        _logger.Information("Resumable upload session created for {Path} ({Size} bytes)", op.RelativePath, fileSize);

        var hasher = new QuickXorHash();
        await using var fs = new FileStream(localPath, FileMode.Open, System.IO.FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete, 4096, useAsync: true);
        var buffer = new byte[ResumableChunkBytes];
        long offset = 0;

        try
        {
            while (offset < fileSize)
            {
                ct.ThrowIfCancellationRequested();
                var toRead = (int)Math.Min(ResumableChunkBytes, fileSize - offset);
                int got = 0;
                while (got < toRead)
                {
                    int n = await fs.ReadAsync(buffer.AsMemory(got, toRead - got), ct);
                    if (n == 0) break;
                    got += n;
                }
                if (got != toRead)
                    throw new IOException($"{op.RelativePath} changed size while uploading");

                var chunkBytes = buffer.AsSpan(0, got).ToArray();
                hasher.Append(chunkBytes);
                var chunkOffset = offset;

                using var chunkResp = await _graph.SendPreSignedAsync(() =>
                {
                    var content = new ByteArrayContent(chunkBytes);
                    content.Headers.ContentLength = chunkBytes.Length;
                    content.Headers.ContentRange = new ContentRangeHeaderValue(
                        chunkOffset, chunkOffset + chunkBytes.Length - 1, fileSize);
                    return new HttpRequestMessage(HttpMethod.Put, uploadUrl) { Content = content };
                }, ct);

                offset += got;

                if (chunkResp.StatusCode == HttpStatusCode.Created || chunkResp.StatusCode == HttpStatusCode.OK)
                {
                    var doneJson = await chunkResp.Content.ReadAsStringAsync(ct);
                    using var doneDoc = JsonDocument.Parse(doneJson);
                    return (UploadResult.FromJson(doneDoc.RootElement), Convert.ToBase64String(hasher.Finish()));
                }
                if (chunkResp.StatusCode == HttpStatusCode.Accepted)
                {
                    try { UploadProgress?.Invoke(drive, op.RelativePath, offset, fileSize); } catch { }
                    continue;
                }
                if (chunkResp.StatusCode == HttpStatusCode.Conflict && ifMatchETag is null)
                    throw new NameTakenException();

                var body = await chunkResp.Content.ReadAsStringAsync(ct);
                throw new HttpRequestException(
                    $"chunk upload at offset {chunkOffset} failed: {(int)chunkResp.StatusCode} - {Truncate(body, 500)}",
                    inner: null, statusCode: chunkResp.StatusCode);
            }
        }
        catch
        {
            _ = CancelUploadSessionAsync(uploadUrl);
            throw;
        }

        throw new IOException("upload session ended without the server confirming the file");
    }

    private async Task CancelUploadSessionAsync(string uploadUrl)
    {
        try
        {
            using var _ = await _graph.SendPreSignedAsync(
                () => new HttpRequestMessage(HttpMethod.Delete, uploadUrl), CancellationToken.None);
        }
        catch { }
    }

    private static async Task ThrowForUploadFailureAsync(HttpResponseMessage resp, SyncOperation op,
        string? ifMatchETag, string what, CancellationToken ct)
    {
        if (resp.IsSuccessStatusCode) return;

        if (resp.StatusCode == HttpStatusCode.PreconditionFailed)
            throw new RemoteConflictException(op.RelativePath, ifMatchETag);

        var body = await resp.Content.ReadAsStringAsync(ct);
        if (resp.StatusCode == HttpStatusCode.Conflict && ifMatchETag is null)
            throw new NameTakenException();
        if (resp.StatusCode == HttpStatusCode.Forbidden)
            throw new PermissionDeniedException("upload", op.RelativePath, ExtractServerMessage(body));

        throw new HttpRequestException(
            $"{what} failed: {(int)resp.StatusCode} {resp.ReasonPhrase} - {Truncate(body, 500)}",
            inner: null, statusCode: resp.StatusCode);
    }

    private sealed class NameTakenException : Exception { }

    // --- folders, deletes, renames ---

    /// <summary>Creates a folder (and any missing parents) in the cloud.</summary>
    private async Task EnsureRemoteFolderAsync(DriveConfig drive, string relativePath, CancellationToken ct)
    {
        relativePath = PathUtil.NormalizeRelative(relativePath);
        if (relativePath == "/") return;

        var existing = await GetRemoteItemAsync(drive, relativePath, ct);
        if (existing != null)
        {
            if (!existing.IsFolder)
                throw new IOException($"{relativePath} is a file in the cloud");
            _metadata.RecordFolder(drive.ConfigId, relativePath, new UploadResult
            {
                ETag = existing.ETag, RemoteItemId = existing.RemoteItemId,
                ParentRemoteItemId = existing.ParentRemoteItemId, WebUrl = existing.WebUrl,
            });
            return;
        }

        var parent = PathUtil.ParentOf(relativePath);
        if (parent != "/" && await GetRemoteItemAsync(drive, parent, ct) is null)
            await EnsureRemoteFolderAsync(drive, parent, ct);

        var name = relativePath[(relativePath.LastIndexOf('/') + 1)..];
        var url = BuildChildrenUrl(drive, parent);
        var payload = JsonSerializer.Serialize(new Dictionary<string, object>
        {
            ["name"] = name,
            ["folder"] = new Dictionary<string, object>(),
            ["@microsoft.graph.conflictBehavior"] = "fail",
        });

        using var resp = await _graph.SendAsync(() => new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = new StringContent(payload, System.Text.Encoding.UTF8, "application/json"),
        }, ct);

        if (resp.StatusCode == HttpStatusCode.Conflict)
        {
            // Created by someone else in the meantime.
            await EnsureRemoteFolderAsync(drive, relativePath, ct);
            return;
        }
        if (resp.StatusCode == HttpStatusCode.Forbidden)
        {
            var body403 = await resp.Content.ReadAsStringAsync(ct);
            throw new PermissionDeniedException("create folder", relativePath, ExtractServerMessage(body403));
        }
        if (!resp.IsSuccessStatusCode)
        {
            var body = await resp.Content.ReadAsStringAsync(ct);
            throw new HttpRequestException(
                $"create folder failed: {(int)resp.StatusCode} - {Truncate(body, 500)}",
                inner: null, statusCode: resp.StatusCode);
        }

        var json = await resp.Content.ReadAsStringAsync(ct);
        using var doc = JsonDocument.Parse(json);
        _metadata.RecordFolder(drive.ConfigId, relativePath, UploadResult.FromJson(doc.RootElement));
        _logger.Information("Created folder in cloud: {Drive}:{Path}", drive.Letter, relativePath);
    }

    private async Task RemoteDeleteAsync(SyncOperation op, DriveConfig drive, CancellationToken ct)
    {
        // By id where we have it, so a different file that has since appeared at
        // the same path is never the one deleted.
        var url = !string.IsNullOrEmpty(op.RemoteItemId)
            ? BuildItemByIdUrl(drive, op.RemoteItemId)
            : BuildItemUrl(drive, op.RelativePath);

        using var resp = await _graph.SendAsync(() => new HttpRequestMessage(HttpMethod.Delete, url), ct);

        if (resp.IsSuccessStatusCode || resp.StatusCode == HttpStatusCode.NotFound)
        {
            _logger.Information("Remote delete OK: {Drive}:{Path}", drive.Letter, op.RelativePath);
            return;
        }
        if (resp.StatusCode == HttpStatusCode.Forbidden)
        {
            var body403 = await resp.Content.ReadAsStringAsync(ct);
            throw new PermissionDeniedException("delete", op.RelativePath, ExtractServerMessage(body403));
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
            remoteId = _metadata.Get(drive.ConfigId, op.NewRelativePath)?.RemoteItemId
                       ?? _metadata.Get(drive.ConfigId, op.RelativePath)?.RemoteItemId;

        var newLocal = PlaceholderManager.LocalPathFor(drive, op.NewRelativePath);
        if (string.IsNullOrEmpty(remoteId))
        {
            // Never reached the cloud under its old name: send it under the new one.
            if (Directory.Exists(newLocal))
                QueueLocalTree(drive, op.NewRelativePath, newLocal, op.Priority);
            else if (File.Exists(newLocal))
                QueueUpload(drive, op.NewRelativePath, new FileInfo(newLocal).Length, op.Priority);
            return;
        }

        var newName = op.NewRelativePath[(op.NewRelativePath.LastIndexOf('/') + 1)..];
        var newParent = PathUtil.ParentOf(op.NewRelativePath);
        var oldParent = PathUtil.ParentOf(op.RelativePath);

        var patch = new Dictionary<string, object> { ["name"] = newName };
        if (!newParent.Equals(oldParent, StringComparison.OrdinalIgnoreCase))
        {
            var parentItem = await GetRemoteItemAsync(drive, newParent, ct);
            if (parentItem is null)
            {
                await EnsureRemoteFolderAsync(drive, newParent, ct);
                parentItem = await GetRemoteItemAsync(drive, newParent, ct)
                    ?? throw new IOException($"destination folder {newParent} not found in the cloud");
            }
            patch["parentReference"] = new Dictionary<string, object> { ["id"] = parentItem.RemoteItemId };
        }

        var body = JsonSerializer.Serialize(patch);
        var url = BuildItemByIdUrl(drive, remoteId);
        using var resp = await _graph.SendAsync(() =>
            new HttpRequestMessage(HttpMethod.Patch, url)
            {
                Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json"),
            }, ct);

        if (resp.StatusCode == HttpStatusCode.NotFound)
        {
            // Gone from the cloud: whatever is here now goes up under the new name.
            _logger.Information("Rename source {Path} no longer in the cloud; uploading as new", op.RelativePath);
            _metadata.DeleteTree(drive.ConfigId, op.NewRelativePath);
            if (Directory.Exists(newLocal)) QueueLocalTree(drive, op.NewRelativePath, newLocal, op.Priority);
            else if (File.Exists(newLocal)) QueueUpload(drive, op.NewRelativePath, new FileInfo(newLocal).Length, op.Priority);
            return;
        }
        if (resp.StatusCode == HttpStatusCode.Forbidden)
        {
            var body403 = await resp.Content.ReadAsStringAsync(ct);
            throw new PermissionDeniedException("rename", op.RelativePath, ExtractServerMessage(body403));
        }
        if (!resp.IsSuccessStatusCode)
        {
            var respBody = await resp.Content.ReadAsStringAsync(ct);
            throw new HttpRequestException(
                $"remote rename failed: {(int)resp.StatusCode} - {Truncate(respBody, 500)}",
                inner: null, statusCode: resp.StatusCode);
        }

        var json = await resp.Content.ReadAsStringAsync(ct);
        using (var doc = JsonDocument.Parse(json))
            _metadata.UpdateRemoteFields(drive.ConfigId, op.NewRelativePath, UploadResult.FromJson(doc.RootElement));

        _logger.Information("Remote rename OK: {Drive}:{Old} -> {New}", drive.Letter, op.RelativePath, op.NewRelativePath);

        // A save made under the new name before the rename reached the cloud.
        if (File.Exists(newLocal))
        {
            var meta = _metadata.Get(drive.ConfigId, op.NewRelativePath);
            if (LocalState.IsDirty(new FileInfo(newLocal), meta))
                QueueUpload(drive, op.NewRelativePath, new FileInfo(newLocal).Length, op.Priority);
        }
    }

    private void QueueUpload(DriveConfig drive, string relativePath, long size, int priority) =>
        _queue.Enqueue(new SyncOperation
        {
            Type = SyncOpType.Upload,
            DriveConfigId = drive.ConfigId,
            DriveLetter = drive.Letter,
            RelativePath = relativePath,
            Priority = priority,
            FileSizeBytes = size,
        });

    private void QueueLocalTree(DriveConfig drive, string relativePath, string localDir, int priority)
    {
        _queue.Enqueue(new SyncOperation
        {
            Type = SyncOpType.RemoteCreateFolder,
            DriveConfigId = drive.ConfigId,
            DriveLetter = drive.Letter,
            RelativePath = relativePath,
            Priority = priority,
        });
        foreach (var entry in new DirectoryInfo(localDir).EnumerateFileSystemInfos())
        {
            var childRel = relativePath.TrimEnd('/') + "/" + entry.Name;
            if (entry is DirectoryInfo d) QueueLocalTree(drive, childRel, d.FullName, priority);
            else if (entry is FileInfo f && !LocalState.IsPlaceholder(f)) QueueUpload(drive, childRel, f.Length, priority);
        }
    }

    /// <summary>The cloud's current view of an item, or null if there's nothing at that path.</summary>
    private async Task<RemoteItem?> GetRemoteItemAsync(DriveConfig drive, string relativePath, CancellationToken ct)
    {
        var url = BuildItemUrl(drive, relativePath);
        using var resp = await _graph.SendAsync(() => new HttpRequestMessage(HttpMethod.Get, url), ct);
        if (resp.StatusCode == HttpStatusCode.NotFound) return null;
        if (!resp.IsSuccessStatusCode)
        {
            var body = await resp.Content.ReadAsStringAsync(ct);
            throw new HttpRequestException(
                $"lookup of {relativePath} failed: {(int)resp.StatusCode} - {Truncate(body, 300)}",
                inner: null, statusCode: resp.StatusCode);
        }
        var json = await resp.Content.ReadAsStringAsync(ct);
        using var doc = JsonDocument.Parse(json);
        return GraphItem.Parse(drive, doc.RootElement, PathUtil.ParentOf(relativePath));
    }

    // --- URL builders ---

    private static string DriveBase(DriveConfig drive) => drive.IsOneDrive
        ? "https://graph.microsoft.com/v1.0/me/drive"
        : "https://graph.microsoft.com/v1.0/drives/" + (drive.ResolvedDriveId
            ?? throw new InvalidOperationException("SharePoint drive missing ResolvedDriveId"));

    private static string EscapePath(string relativePath) =>
        string.Join("/", relativePath.Trim('/').Split('/').Select(Uri.EscapeDataString));

    private static string BuildItemByIdUrl(DriveConfig drive, string remoteId) =>
        $"{DriveBase(drive)}/items/{remoteId}";

    private static string BuildContentUrl(DriveConfig drive, string relativePath) =>
        $"{DriveBase(drive)}/root:/{EscapePath(relativePath)}:/content";

    private static string BuildCreateUploadSessionUrl(DriveConfig drive, string relativePath) =>
        $"{DriveBase(drive)}/root:/{EscapePath(relativePath)}:/createUploadSession";

    private static string BuildItemUrl(DriveConfig drive, string relativePath)
    {
        var rel = relativePath.Trim('/');
        return string.IsNullOrEmpty(rel) ? $"{DriveBase(drive)}/root" : $"{DriveBase(drive)}/root:/{EscapePath(rel)}";
    }

    private static string BuildChildrenUrl(DriveConfig drive, string parentRelativePath)
    {
        var rel = parentRelativePath.Trim('/');
        return string.IsNullOrEmpty(rel)
            ? $"{DriveBase(drive)}/root/children"
            : $"{DriveBase(drive)}/root:/{EscapePath(rel)}:/children";
    }

    private static string Truncate(string s, int maxLen) =>
        s.Length <= maxLen ? s : s.Substring(0, maxLen) + "...";

    private static string? ExtractServerMessage(string body)
    {
        if (string.IsNullOrWhiteSpace(body)) return null;
        try
        {
            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.TryGetProperty("error", out var err) &&
                err.TryGetProperty("message", out var msg))
                return msg.GetString();
        }
        catch { }
        return Truncate(body, 200);
    }
}
