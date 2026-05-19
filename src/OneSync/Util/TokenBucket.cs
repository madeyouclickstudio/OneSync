using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;

namespace OneSync.Util;

/// <summary>
/// Async-aware leaky-bucket rate limiter. Self-throttling for Graph calls so we
/// preempt server-side 429s instead of reacting to them.
///
/// Algorithm: bucket holds up to <c>burstSize</c> tokens. Tokens refill at
/// <c>refillPerSecond</c> Hz. <see cref="AcquireAsync"/> consumes one token;
/// blocks (awaiting Task.Delay) when the bucket is empty until the next token
/// has accrued.
///
/// Refill is computed on demand via Stopwatch elapsed time — no background
/// timer thread. Thread-safe under a single small lock; concurrent callers
/// queue inside Task.Delay, not inside the lock.
/// </summary>
internal sealed class TokenBucket
{
    private readonly double _capacity;
    private readonly double _refillPerSecond;
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private readonly object _lock = new();

    private double _tokens;
    private long _lastRefillTicks;

    public TokenBucket(int capacity, int refillPerSecond)
    {
        if (capacity <= 0) throw new ArgumentOutOfRangeException(nameof(capacity));
        if (refillPerSecond <= 0) throw new ArgumentOutOfRangeException(nameof(refillPerSecond));
        _capacity = capacity;
        _refillPerSecond = refillPerSecond;
        _tokens = capacity;  // start full so initial bursts aren't penalised
        _lastRefillTicks = _clock.ElapsedTicks;
    }

    /// <summary>
    /// Wait until a token is available, then consume one. Returns the time spent
    /// waiting (zero if a token was immediately available).
    /// </summary>
    public async Task<TimeSpan> AcquireAsync(CancellationToken ct = default)
    {
        var waitStart = _clock.Elapsed;
        while (true)
        {
            TimeSpan delay;
            lock (_lock)
            {
                Refill();
                if (_tokens >= 1.0)
                {
                    _tokens -= 1.0;
                    return _clock.Elapsed - waitStart;
                }
                // Need to wait for (1 - _tokens) more tokens to accrue.
                var needed = 1.0 - _tokens;
                var seconds = needed / _refillPerSecond;
                delay = TimeSpan.FromSeconds(seconds);
            }
            // Cap delay so we don't sleep through a cancellation for too long.
            if (delay > TimeSpan.FromSeconds(5)) delay = TimeSpan.FromSeconds(5);
            try { await Task.Delay(delay, ct); }
            catch (OperationCanceledException) { throw; }
        }
    }

    private void Refill()
    {
        var nowTicks = _clock.ElapsedTicks;
        var elapsedSec = (nowTicks - _lastRefillTicks) / (double)Stopwatch.Frequency;
        if (elapsedSec <= 0) return;
        _tokens = Math.Min(_capacity, _tokens + elapsedSec * _refillPerSecond);
        _lastRefillTicks = nowTicks;
    }
}
