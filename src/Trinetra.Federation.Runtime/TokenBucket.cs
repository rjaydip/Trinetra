using System.Diagnostics;

namespace Trinetra.Federation.Runtime;

/// <summary>
/// Async token bucket. One per connector target.
/// </summary>
/// <remarks>
/// <para>
/// Rate limits are per target, never global, because vendor tolerance varies by an order of
/// magnitude: an enterprise Milestone cluster absorbs roughly 100x what a small departmental
/// NVR does. A single shared limit is either so low it starves the large sites or so high it
/// knocks over the small ones.
/// </para>
/// <para>
/// Timing uses <see cref="Stopwatch.GetTimestamp"/> rather than wall-clock. NTP steps and DST
/// transitions on the host must not hand out a burst of free tokens, nor stall a connector
/// for an hour.
/// </para>
/// <para>
/// The lock guards only the synchronous refill-and-consume; the wait itself happens outside
/// it, so a caller queued behind a long delay never blocks another target's worker fibre.
/// </para>
/// </remarks>
public sealed class TokenBucket
{
    private readonly double _ratePerSecond;
    private readonly double _capacity;
    private readonly Lock _gate = new();
    private double _tokens;
    private long _updatedTicks;

    public TokenBucket(double ratePerSecond, int burst)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(ratePerSecond);
        ArgumentOutOfRangeException.ThrowIfLessThan(burst, 1);

        _ratePerSecond = ratePerSecond;
        _capacity = burst;
        _tokens = burst;
        _updatedTicks = Stopwatch.GetTimestamp();
    }

    /// <summary>Tokens currently available. Primarily for metrics and tests.</summary>
    public double Available
    {
        get
        {
            lock (_gate)
            {
                Refill();
                return _tokens;
            }
        }
    }

    /// <summary>Waits until <paramref name="tokens"/> are available, then consumes them.</summary>
    public async Task AcquireAsync(double tokens, CancellationToken cancellationToken)
    {
        if (tokens > _capacity)
        {
            throw new ArgumentOutOfRangeException(
                nameof(tokens), tokens, $"Request exceeds bucket capacity {_capacity}.");
        }

        while (true)
        {
            TimeSpan wait;

            lock (_gate)
            {
                Refill();
                if (_tokens >= tokens)
                {
                    _tokens -= tokens;
                    return;
                }

                wait = TimeSpan.FromSeconds((tokens - _tokens) / _ratePerSecond);
            }

            await Task.Delay(wait, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Non-blocking variant, for paths that should shed load rather than queue behind it.
    /// </summary>
    public bool TryAcquire(double tokens = 1.0)
    {
        lock (_gate)
        {
            Refill();
            if (_tokens < tokens)
            {
                return false;
            }

            _tokens -= tokens;
            return true;
        }
    }

    private void Refill()
    {
        var now = Stopwatch.GetTimestamp();
        var elapsed = Stopwatch.GetElapsedTime(_updatedTicks, now).TotalSeconds;
        if (elapsed <= 0)
        {
            return;
        }

        _tokens = Math.Min(_capacity, _tokens + (elapsed * _ratePerSecond));
        _updatedTicks = now;
    }
}
