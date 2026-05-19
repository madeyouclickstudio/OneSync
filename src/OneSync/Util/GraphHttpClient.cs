using System;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Threading;
using System.Threading.Tasks;
using OneSync.Auth;
using OneSync.Config;
using Serilog;

namespace OneSync.Util;

/// <summary>
/// Single point through which every Microsoft Graph HTTP request flows.
/// Enforces three throttling guarantees:
///   1. Concurrency cap - at most N requests in flight per process
///   2. Global Retry-After - on 429/503, ALL subsequent requests wait until
///      the server-specified cooldown expires (not just the one that got
///      throttled)
///   3. Exponential backoff with jitter - transient failures retry with
///      randomised waits so retries don't pile up at the same instant
///
/// This is the only HttpClient anyone in the app should use to talk to Graph.
///
/// API note: callers pass a <c>Func&lt;HttpRequestMessage&gt;</c> factory rather
/// than a pre-built message. <see cref="HttpRequestMessage"/> is single-use -
/// once sent it cannot be sent again - and this class retries on 429/503/transient
/// failure, so a fresh message must be built per attempt.
/// </summary>
internal sealed class GraphHttpClient : IDisposable
{
    private readonly HttpClient _http;
    private readonly GraphAuthProvider _auth;
    private readonly SyncSettings _settings;
    private readonly ILogger _logger;
    private readonly SemaphoreSlim _concurrencyGate;
    private readonly Random _jitter = new();

    // Single shared cooldown. When a 429/503 hits, every subsequent request
    // sleeps until this passes. Stored as ticks for atomic reads/writes.
    private long _cooldownUntilTicks;
    private long _totalThrottleEvents;
    private const string UnknownUrlSentinel = "(no-uri)";
    private readonly GraphRequestRingBuffer _ringBuffer = new(capacity: 1024);
    private readonly TokenBucket? _rateLimiter;
    private readonly int _rateWaitWarnMs;

    /// <summary>Exposed so DiagnosticExporter and CrashWriter can snapshot it.</summary>
    public GraphRequestRingBuffer RingBuffer => _ringBuffer;

    public GraphHttpClient(GraphAuthProvider auth, SyncSettings settings, ILogger logger)
    {
        _auth = auth;
        _settings = settings;
        _logger = logger.ForContext("Component", "GraphHttpClient");
        _http = new HttpClient
        {
            Timeout = TimeSpan.FromMinutes(10),
        };
        var degree = Math.Max(1, settings.MaxConcurrentGraphRequests);
        _concurrencyGate = new SemaphoreSlim(degree, degree);
        if (settings.GraphRateLimit?.Enabled == true)
        {
            var burst = Math.Max(1, settings.GraphRateLimit.BurstSize);
            var rate = Math.Max(1, settings.GraphRateLimit.RequestsPerSecond);
            _rateLimiter = new TokenBucket(capacity: burst, refillPerSecond: rate);
            _rateWaitWarnMs = Math.Max(0, settings.GraphRateLimit.WaitWarnThresholdMs);
            _logger.Information(
                "Client-side Graph rate limit active: {RPS} req/s sustained, burst {Burst}",
                rate, burst);
        }
        else
        {
            _logger.Information("Client-side Graph rate limit disabled");
        }
    }

    public long ThrottleEventCount => Interlocked.Read(ref _totalThrottleEvents);

    public DateTime CooldownUntilUtc =>
        new(Interlocked.Read(ref _cooldownUntilTicks), DateTimeKind.Utc);

    /// <summary>True if a global cooldown is currently in effect.</summary>
    public bool IsInCooldown
    {
        get
        {
            var ticks = Interlocked.Read(ref _cooldownUntilTicks);
            return ticks > 0 && new DateTime(ticks, DateTimeKind.Utc) > DateTime.UtcNow;
        }
    }

    /// <summary>
    /// Fires when a 429/503 imposes a cooldown longer than 30 seconds. The tray
    /// uses this to balloon "Microsoft is busy, your files will appear in N minutes"
    /// to the user.
    /// </summary>
    public event Action<TimeSpan>? SignificantThrottle;

    /// <summary>
    /// Sends an authenticated Graph request through the gateway. The factory is
    /// invoked once per attempt to build a fresh <see cref="HttpRequestMessage"/>
    /// (necessary because HttpRequestMessage is single-use and we retry).
    /// Caller owns the returned HttpResponseMessage.
    /// </summary>
    public Task<HttpResponseMessage> SendAsync(
        Func<HttpRequestMessage> requestFactory, CancellationToken ct = default,
        HttpCompletionOption completion = HttpCompletionOption.ResponseContentRead)
    {
        return SendCoreAsync(requestFactory, ct, completion, attachAuth: true);
    }

    /// <summary>
    /// Sends a request to a pre-signed URL (e.g. an upload session URL or a
    /// 302 download redirect) where Authorization should NOT be attached.
    /// </summary>
    public Task<HttpResponseMessage> SendPreSignedAsync(
        Func<HttpRequestMessage> requestFactory, CancellationToken ct = default,
        HttpCompletionOption completion = HttpCompletionOption.ResponseContentRead)
    {
        return SendCoreAsync(requestFactory, ct, completion, attachAuth: false);
    }

    private async Task<HttpResponseMessage> SendCoreAsync(
        Func<HttpRequestMessage> requestFactory, CancellationToken ct,
        HttpCompletionOption completion, bool attachAuth)
    {
        int attempt = 0;
        while (true)
        {
            attempt++;
            string? lastTriggerUrl = null;
            // Preempt Graph 429s by self-throttling first. Retries (cycles 2+
            // of this while loop) also consume tokens — intentional, since a
            // retried request is still a Graph request the server has to handle.
            if (_rateLimiter is not null)
            {
                var waited = await _rateLimiter.AcquireAsync(ct);
                if (waited.TotalMilliseconds >= _rateWaitWarnMs)
                {
                    _logger.Warning("Graph rate limit wait {@RateLimitWait}", new
                    {
                        Event = "rate_limit_wait",
                        WaitMs = (int)waited.TotalMilliseconds,
                        Recent60sRequestCount = _ringBuffer.CountInLast(TimeSpan.FromSeconds(60)),
                    });
                }
            }
            await WaitForCooldownAsync(ct);
            await _concurrencyGate.WaitAsync(ct);

            HttpResponseMessage? resp = null;
            HttpRequestMessage? request = null;
            try
            {
                request = requestFactory();
                var triggerUrl = request.RequestUri?.ToString();  // capture before any dispose
                if (attachAuth)
                {
                    var token = await _auth.GetAccessTokenAsync(ct);
                    request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
                }
                resp = await _http.SendAsync(request, completion, ct);
                _ringBuffer.Record(triggerUrl ?? UnknownUrlSentinel, (int)resp.StatusCode);
                // Preserve URL for ApplyGlobalCooldown on 429/503 below; the
                // request object will be disposed before we reach that branch.
                lastTriggerUrl = triggerUrl;
            }
            catch (HttpRequestException ex) when (attempt <= _settings.MaxRetries && IsTransient(ex))
            {
                request?.Dispose();
                _concurrencyGate.Release();
                var backoff = ComputeBackoff(attempt);
                _logger.Warning(
                    "Graph request transient failure ({Type}: {Msg}) - retry {Attempt}/{Max} in {Backoff}",
                    ex.GetType().Name, ex.Message, attempt, _settings.MaxRetries, backoff);
                try { await Task.Delay(backoff, ct); }
                catch (OperationCanceledException) { throw; }
                continue;
            }
            catch
            {
                request?.Dispose();
                _concurrencyGate.Release();
                throw;
            }

            // Dispose the request we just used; the next loop iteration (if any)
            // will get a fresh one from the factory.
            request.Dispose();

            // Decide whether to retry
            if (resp.StatusCode == HttpStatusCode.TooManyRequests ||
                resp.StatusCode == HttpStatusCode.ServiceUnavailable)
            {
                var retryAfter = ParseRetryAfter(resp) ?? ComputeBackoff(attempt);
                ApplyGlobalCooldown(retryAfter, resp.StatusCode, lastTriggerUrl);
                resp.Dispose();
                _concurrencyGate.Release();

                if (attempt > _settings.MaxRetries)
                {
                    _logger.Error("Graph still throttled after {Max} retries - giving up on this request",
                        _settings.MaxRetries);
                    // Construct a synthetic response so callers can still .StatusCode check
                    return new HttpResponseMessage(HttpStatusCode.TooManyRequests)
                    {
                        ReasonPhrase = "Throttled after max retries",
                    };
                }
                continue;
            }

            _concurrencyGate.Release();
            return resp;
        }
    }

    private void LogCooldownLifted(DateTime waitStart)
    {
        var elapsed = DateTime.UtcNow - waitStart;
        _logger.Information("Graph cooldown lifted {@CooldownLifted}", new
        {
            Event = "cooldown_lifted",
            ActualDurationSeconds = (int)elapsed.TotalSeconds,
        });
    }

    private async Task WaitForCooldownAsync(CancellationToken ct)
    {
        DateTime? waitStart = null;
        while (true)
        {
            var ticks = Interlocked.Read(ref _cooldownUntilTicks);
            if (ticks == 0)
            {
                if (waitStart.HasValue) LogCooldownLifted(waitStart.Value);
                return;
            }

            var until = new DateTime(ticks, DateTimeKind.Utc);
            var now = DateTime.UtcNow;
            if (until <= now)
            {
                Interlocked.CompareExchange(ref _cooldownUntilTicks, 0, ticks);
                if (waitStart.HasValue) LogCooldownLifted(waitStart.Value);
                return;
            }

            waitStart ??= now;
            var wait = until - now;
            if (wait > TimeSpan.FromMinutes(15)) wait = TimeSpan.FromMinutes(15);
            try { await Task.Delay(wait, ct); }
            catch (OperationCanceledException) { throw; }
        }
    }

    private void ApplyGlobalCooldown(TimeSpan delta, HttpStatusCode status, string? triggerUrl)
    {
        // Cap at configured maximum so a misconfigured Retry-After header (e.g.
        // "Retry-After: 86400") doesn't lock us out for the rest of the day.
        var capped = delta > TimeSpan.FromSeconds(_settings.MaxRetryAfterSeconds)
            ? TimeSpan.FromSeconds(_settings.MaxRetryAfterSeconds) : delta;

        // Add small jitter so a fleet of machines that all hit 429 at the same
        // moment don't all wake up at the same moment.
        capped += TimeSpan.FromMilliseconds(_jitter.Next(0, 2000));

        var newUntil = (DateTime.UtcNow + capped).Ticks;
        var current = Interlocked.Read(ref _cooldownUntilTicks);
        if (newUntil > current)
            Interlocked.Exchange(ref _cooldownUntilTicks, newUntil);

        Interlocked.Increment(ref _totalThrottleEvents);
        var resumeAt = new DateTime(newUntil, DateTimeKind.Utc);
        _logger.Warning("Graph cooldown applied {@CooldownContext}", new
        {
            Event = "cooldown_applied",
            TriggerUrl = triggerUrl ?? UnknownUrlSentinel,
            StatusCode = (int)status,
            RetryAfterSeconds = (int)capped.TotalSeconds,
            AppliedAtUtc = DateTime.UtcNow,
            ExpectedResumeAtUtc = resumeAt,
            Recent60sRequestCount = _ringBuffer.CountInLast(TimeSpan.FromSeconds(60)),
            Recent60sTopUrls = _ringBuffer.TopUrlsInLast(TimeSpan.FromSeconds(60), top: 10),
            ThrottleEventCount = Interlocked.Read(ref _totalThrottleEvents),
        });

        // Only surface to the user for noticeable cooldowns - we get small ones (a few seconds)
        // regularly and balloon-spamming would be annoying.
        if (capped >= TimeSpan.FromSeconds(30))
        {
            try { SignificantThrottle?.Invoke(capped); } catch { /* swallow */ }
        }
    }

    private TimeSpan? ParseRetryAfter(HttpResponseMessage resp)
    {
        var ra = resp.Headers.RetryAfter;
        if (ra is null) return null;

        if (ra.Delta.HasValue) return ra.Delta.Value;
        if (ra.Date.HasValue)
        {
            var delta = ra.Date.Value - DateTimeOffset.UtcNow;
            return delta > TimeSpan.Zero ? delta : TimeSpan.FromSeconds(1);
        }
        return null;
    }

    private TimeSpan ComputeBackoff(int attempt)
    {
        var baseSec = Math.Max(1, _settings.RetryBackoffBaseSeconds);
        var pow = Math.Min(8, attempt); // cap at 256x base
        var seconds = baseSec * Math.Pow(2, pow - 1);
        // Add up to ±25% jitter
        var jitter = _jitter.NextDouble() * 0.5 - 0.25;
        seconds *= (1.0 + jitter);
        var capped = Math.Min(seconds, _settings.MaxRetryAfterSeconds);
        return TimeSpan.FromSeconds(capped);
    }

    private static bool IsTransient(Exception ex)
    {
        if (ex is HttpRequestException hre)
        {
            return hre.StatusCode is null
                || hre.StatusCode == HttpStatusCode.RequestTimeout
                || hre.StatusCode == HttpStatusCode.BadGateway
                || hre.StatusCode == HttpStatusCode.GatewayTimeout
                || hre.StatusCode == HttpStatusCode.ServiceUnavailable;
        }
        return ex is TaskCanceledException || ex is System.IO.IOException;
    }

    public void Dispose() => _http?.Dispose();
}
