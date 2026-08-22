using System.Diagnostics;
using System.Text.Json;
using Dapper;
using Microsoft.Extensions.Logging;
using Npgsql;
using Trinetra.Federation.Adapters;
using Trinetra.Federation.Core.Capabilities;
using Trinetra.Federation.Core.Errors;
using Trinetra.Federation.Core.Model;

namespace Trinetra.Federation.Runtime;

/// <summary>What a connection test found.</summary>
public sealed record ConnectionTestReport
{
    public required bool Reachable { get; init; }
    public double? ConnectMs { get; init; }
    public string? Capabilities { get; init; }
    public IReadOnlyDictionary<string, string>? CapabilityNotes { get; init; }
    public int? CameraCount { get; init; }
    public double? ClockSkewSeconds { get; init; }
    public IReadOnlyList<string> Warnings { get; init; } = [];
    public string? Failure { get; init; }
    public string? FailureKind { get; init; }
}

/// <summary>
/// Connects to a target and reports what it finds. Read-only.
/// </summary>
/// <remarks>
/// Shared by the CLI and the API so both run the identical check — a field engineer at a
/// terminal and an operator in the browser must not get different answers about the same device.
/// </remarks>
public sealed partial class ConnectionTester
{
    /// <summary>
    /// Hard ceiling on one test.
    /// </summary>
    /// <remarks>
    /// A black-holed device would otherwise pin the job open indefinitely and leave the caller's
    /// UI spinning with no explanation.
    /// </remarks>
    public static readonly TimeSpan MaxDuration = TimeSpan.FromSeconds(60);

    private readonly IAdapterFactory _adapters;
    private readonly ILogger<ConnectionTester> _logger;

    public ConnectionTester(IAdapterFactory adapters, ILogger<ConnectionTester> logger)
    {
        _adapters = adapters;
        _logger = logger;
    }

    public async Task<ConnectionTestReport> RunAsync(
        ConnectorTarget target, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(target);

        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(MaxDuration);

        var warnings = new List<string>();

        try
        {
            await using var adapter = await _adapters.CreateAsync(target, deadline.Token);

            var started = Stopwatch.StartNew();
            await adapter.ConnectAsync(deadline.Token);
            var connectMs = started.Elapsed.TotalMilliseconds;

            var capabilities = await adapter.ProbeCapabilitiesAsync(deadline.Token);

            if (capabilities.EventDelivery == EventDelivery.None)
            {
                warnings.Add("This device delivers no events. Inventory and status only.");
            }

            double? skewSeconds = null;
            if (capabilities.Has(Capability.TimeSyncCheck))
            {
                try
                {
                    var deviceTime = await adapter.GetVmsTimeAsync(deadline.Token);
                    skewSeconds = (deviceTime - DateTimeOffset.UtcNow).TotalSeconds;

                    // Beyond a few minutes this breaks time-window correlation, and on ONVIF it
                    // breaks authentication outright — the digest embeds a timestamp the device
                    // must consider current.
                    if (Math.Abs(skewSeconds.Value) > 300)
                    {
                        warnings.Add(
                            $"Device clock differs from platform time by "
                            + $"{skewSeconds.Value / 60:F1} minutes. Set NTP on the device.");
                    }
                }
                catch (AdapterException ex)
                {
                    warnings.Add($"Could not read the device clock: {ex.Message}");
                }
            }

            int? cameraCount = null;
            if (capabilities.Has(Capability.Inventory))
            {
                var cameras = await adapter.GetCamerasAsync(deadline.Token);
                cameraCount = cameras.Count;

                if (target.ExpectedCameraCount is { } expected && cameras.Count != expected)
                {
                    warnings.Add(
                        $"Reported {cameras.Count} cameras but {expected} were expected. A device "
                        + "silently returning a subset looks identical to a healthy sync.");
                }
            }

            return new ConnectionTestReport
            {
                Reachable = true,
                ConnectMs = connectMs,
                Capabilities = capabilities.Supported.ToString(),
                CapabilityNotes = capabilities.Notes,
                CameraCount = cameraCount,
                ClockSkewSeconds = skewSeconds,
                Warnings = warnings,
            };
        }
        catch (AuthException ex)
        {
            return Failed(ex.Message, "authentication", warnings);
        }
        catch (ConfigurationException ex)
        {
            return Failed(ex.Message, "configuration", warnings);
        }
        catch (AdapterException ex)
        {
            return Failed(ex.Message, "unreachable", warnings);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return Failed(
                $"The device did not respond within {MaxDuration.TotalSeconds:F0} seconds.",
                "timeout", warnings);
        }
    }

    private static ConnectionTestReport Failed(string message, string kind, List<string> warnings) =>
        new()
        {
            Reachable = false,
            Failure = message,
            FailureKind = kind,
            Warnings = warnings,
        };

    /// <summary>
    /// Caps how many device tests run at once across the process.
    /// </summary>
    /// <remarks>
    /// Each test can occupy a full minute of device I/O. Without a cap, a caller can start
    /// hundreds and exhaust worker threads and vendor session slots at the same time.
    /// </remarks>
    private static readonly SemaphoreSlim Concurrency = new(20, 20);

    /// <summary>Runs a queued test and records the outcome.</summary>
    /// <remarks>
    /// <para>
    /// The result is written to the database rather than held in memory: several API instances
    /// may run, and a caller polling for the result may reach a different one than started it.
    /// </para>
    /// <para>
    /// <b>A connection is opened per statement and never held across <see cref="RunAsync"/>.</b>
    /// Holding one across the device call pins a pooled connection for up to
    /// <see cref="MaxDuration"/>; with enough concurrent tests that alone exhausts the pool and
    /// takes the whole API down — from what is nominally a read operation.
    /// </para>
    /// </remarks>
    public async Task ExecuteJobAsync(
        Guid testId, ConnectorTarget target, NpgsqlDataSource dataSource,
        string executedBy, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(dataSource);

        await Concurrency.WaitAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            await MarkRunningAsync(testId, dataSource, executedBy, cancellationToken)
                .ConfigureAwait(false);

            ConnectionTestReport report;
            try
            {
                // No database connection is held here. This is the long part.
                report = await RunAsync(target, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // A test that throws unexpectedly must still reach a terminal state, or the
                // caller polls a job that never finishes.
                LogTestFailed(_logger, target.Code, ex);
                report = new ConnectionTestReport
                {
                    Reachable = false, Failure = ex.Message, FailureKind = "error",
                };
            }

            await RecordResultAsync(testId, report, dataSource).ConfigureAwait(false);
        }
        finally
        {
            Concurrency.Release();
        }
    }

    private static async Task MarkRunningAsync(
        Guid testId, NpgsqlDataSource dataSource, string executedBy, CancellationToken ct)
    {
        await using var connection = await dataSource.OpenConnectionAsync(ct).ConfigureAwait(false);

        await connection.ExecuteAsync(new CommandDefinition("""
            UPDATE federation.connection_test
            SET status = 'running', executed_by = @executedBy, started_at = now()
            WHERE id = @testId;
            """, new { testId, executedBy }, cancellationToken: ct)).ConfigureAwait(false);
    }

    private static async Task RecordResultAsync(
        Guid testId, ConnectionTestReport report, NpgsqlDataSource dataSource)
    {
        // Deliberately not the caller's token: a cancelled or abandoned test must still record a
        // terminal state, or the sweeper is the only thing that ever closes it out.
        await using var connection = await dataSource.OpenConnectionAsync(CancellationToken.None)
            .ConfigureAwait(false);

        await connection.ExecuteAsync(new CommandDefinition("""
            UPDATE federation.connection_test
            SET status = @status, completed_at = now(),
                result = @result::jsonb, failure_reason = @failure
            WHERE id = @testId;
            """, new
        {
            testId,
            status = report.Reachable ? "completed" : "failed",
            result = JsonSerializer.Serialize(report),
            failure = report.Failure,
        }, cancellationToken: CancellationToken.None)).ConfigureAwait(false);
    }

    [LoggerMessage(Level = LogLevel.Error, Message = "Connection test for {Code} threw unexpectedly")]
    private static partial void LogTestFailed(ILogger logger, string code, Exception exception);
}
