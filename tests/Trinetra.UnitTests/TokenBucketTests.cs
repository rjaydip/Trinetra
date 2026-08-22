using Shouldly;
using Trinetra.Federation.Runtime;

namespace Trinetra.UnitTests;

public sealed class TokenBucketTests
{
    [Fact]
    public void StartsFull()
    {
        var bucket = new TokenBucket(ratePerSecond: 10, burst: 5);
        bucket.Available.ShouldBe(5, tolerance: 0.01);
    }

    [Fact]
    public void TryAcquire_ConsumesUpToBurstThenRefuses()
    {
        var bucket = new TokenBucket(ratePerSecond: 0.001, burst: 3);

        bucket.TryAcquire().ShouldBeTrue();
        bucket.TryAcquire().ShouldBeTrue();
        bucket.TryAcquire().ShouldBeTrue();

        // Refill rate is effectively zero over the span of this test, so the fourth must fail.
        bucket.TryAcquire().ShouldBeFalse();
    }

    [Fact]
    public async Task Refills_OverTime()
    {
        var bucket = new TokenBucket(ratePerSecond: 100, burst: 2);

        bucket.TryAcquire(2).ShouldBeTrue();
        bucket.TryAcquire().ShouldBeFalse();

        await Task.Delay(50);

        // 100/s for 50ms is ~5 tokens, capped at the burst of 2.
        bucket.Available.ShouldBeGreaterThan(0.5);
    }

    [Fact]
    public void NeverExceedsCapacity()
    {
        var bucket = new TokenBucket(ratePerSecond: 1000, burst: 4);
        Thread.Sleep(20);
        bucket.Available.ShouldBeLessThanOrEqualTo(4);
    }

    [Fact]
    public async Task AcquireAsync_ThrottlesToConfiguredRate()
    {
        // Burst of 1 so only the rate governs: 5 acquisitions at 50/s must take >= ~80ms.
        var bucket = new TokenBucket(ratePerSecond: 50, burst: 1);
        var started = System.Diagnostics.Stopwatch.StartNew();

        for (var i = 0; i < 5; i++)
        {
            await bucket.AcquireAsync(tokens: 1, CancellationToken.None);
        }

        started.Stop();
        started.Elapsed.TotalMilliseconds.ShouldBeGreaterThan(60);
    }

    [Fact]
    public async Task AcquireAsync_RejectsRequestLargerThanCapacity()
    {
        var bucket = new TokenBucket(ratePerSecond: 10, burst: 2);

        // Would otherwise wait forever, which at 3am looks exactly like a hung connector.
        await Should.ThrowAsync<ArgumentOutOfRangeException>(() => bucket.AcquireAsync(3, CancellationToken.None));
    }

    [Fact]
    public void RejectsInvalidConfiguration()
    {
        Should.Throw<ArgumentOutOfRangeException>(() => new TokenBucket(0, 5));
        Should.Throw<ArgumentOutOfRangeException>(() => new TokenBucket(-1, 5));
        Should.Throw<ArgumentOutOfRangeException>(() => new TokenBucket(10, 0));
    }

    [Fact]
    public async Task IsSafeUnderConcurrentAcquisition()
    {
        // A worker holds 50-200 targets in one process; the bucket must not over-issue when
        // several of that target's pollers fire together.
        var bucket = new TokenBucket(ratePerSecond: 0.001, burst: 10);
        var granted = 0;

        await Task.WhenAll(Enumerable.Range(0, 100).Select(_ => Task.Run(() =>
        {
            if (bucket.TryAcquire())
            {
                Interlocked.Increment(ref granted);
            }
        })));

        granted.ShouldBe(10);
    }
}
