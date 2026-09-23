using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Trinetra.Federation.Core.Abstractions;
using Trinetra.Federation.Storage;

namespace Trinetra.Federation.Worker;

/// <summary>
/// Per-camera automated health check: every tick, probes the next batch of cameras due a check
/// (oldest-checked-first) directly against their own RTSP endpoint, credential and all. Works
/// identically for a standalone registry camera and one discovered through a VMS target — see
/// <see cref="CameraHealthProbeStore"/>'s remarks. Distinct from <c>TargetWorker</c>'s own health
/// loop, which only confirms a VMS's *API* is reachable, not that any individual camera behind it
/// still answers.
/// </summary>
public sealed partial class CameraHealthCheckRunner : BackgroundService
{
    private readonly CameraHealthProbeStore _store;
    private readonly ICameraHealthProbe _probe;
    private readonly ICredentialResolver _credentials;
    private readonly CameraHealthCheckOptions _options;
    private readonly ILogger<CameraHealthCheckRunner> _logger;

    public CameraHealthCheckRunner(
        CameraHealthProbeStore store, ICameraHealthProbe probe, ICredentialResolver credentials,
        IOptions<CameraHealthCheckOptions> options, ILogger<CameraHealthCheckRunner> logger)
    {
        _store = store;
        _probe = probe;
        _credentials = credentials;
        _options = options.Value;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        LogStarting(_logger, _options.CheckInterval, _options.TickInterval, _options.BatchSize);

        using var timer = new PeriodicTimer(_options.TickInterval);

        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
            {
                try
                {
                    await RunBatchAsync(stoppingToken).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    // Same posture as CorrelationRunner: one bad tick (a DB blip) must not take
                    // down the whole worker process — the next tick just tries again.
                    LogBatchFailed(_logger, ex);
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Normal shutdown.
        }
    }

    private async Task RunBatchAsync(CancellationToken ct)
    {
        var candidates = await _store.ListDueAsync(_options.BatchSize, _options.CheckInterval, ct)
            .ConfigureAwait(false);

        if (candidates.Count == 0)
        {
            return;
        }

        using var gate = new SemaphoreSlim(_options.MaxConcurrentProbes);
        var reachable = 0;

        var tasks = candidates.Select(async candidate =>
        {
            await gate.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                if (await ProbeOneAsync(candidate, ct).ConfigureAwait(false))
                {
                    Interlocked.Increment(ref reachable);
                }
            }
            finally
            {
                gate.Release();
            }
        });

        await Task.WhenAll(tasks).ConfigureAwait(false);

        LogBatchCompleted(_logger, checkedCount: candidates.Count, reachable);
    }

    private async Task<bool> ProbeOneAsync(CameraProbeCandidate candidate, CancellationToken ct)
    {
        string? username = null;
        string? password = null;

        if (candidate.CredentialReference is not null)
        {
            try
            {
                var credential = await _credentials.ResolveAsync(candidate.CredentialReference, ct)
                    .ConfigureAwait(false);
                username = credential.Username;
                password = credential.Password;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // A credential that fails to resolve (rotated, corrupted, mis-keyed) is still a
                // real health signal worth recording — probe unauthenticated rather than skipping
                // the camera entirely; an open OPTIONS response still confirms the device itself
                // is up even if the stored credential is unusable.
                LogCredentialResolveFailed(_logger, candidate.CameraId, ex);
            }
        }

        var target = new CameraProbeTarget(
            candidate.CameraId, candidate.Protocol, candidate.IpAddress, candidate.Port, username, password);
        var result = await _probe.ProbeAsync(target, ct).ConfigureAwait(false);

        await _store.RecordAsync(
            candidate.CameraId, result.Reachable, result.LatencyMs, result.ErrorCode, result.FailureReason, ct)
            .ConfigureAwait(false);

        return result.Reachable;
    }

    [LoggerMessage(Level = LogLevel.Information,
        Message = "Camera health check runner starting. CheckInterval={CheckInterval} "
                + "TickInterval={TickInterval} BatchSize={BatchSize}")]
    private static partial void LogStarting(
        ILogger logger, TimeSpan checkInterval, TimeSpan tickInterval, int batchSize);

    [LoggerMessage(Level = LogLevel.Debug,
        Message = "Camera health check batch: {CheckedCount} probed, {Reachable} reachable")]
    private static partial void LogBatchCompleted(ILogger logger, int checkedCount, int reachable);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Camera health check batch failed; retrying next tick")]
    private static partial void LogBatchFailed(ILogger logger, Exception exception);

    [LoggerMessage(Level = LogLevel.Debug,
        Message = "Credential resolve failed for camera {CameraId}'s health probe; probing unauthenticated")]
    private static partial void LogCredentialResolveFailed(ILogger logger, Guid cameraId, Exception exception);
}
