using System.Diagnostics;
using System.Net.Sockets;
using System.Text.Json;
using Dapper;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace Trinetra.Federation.Runtime;

/// <summary>What a plain reachability probe found.</summary>
/// <remarks>
/// Deliberately narrow. This is not <see cref="ConnectionTestReport"/> -- there is no adapter, no
/// credential, no capability discovery. It answers exactly one question: did a TCP connection to
/// <c>ipAddress:port</c> open within the timeout. Nothing here should ever be read as "the camera
/// is configured correctly" or "the credential works".
/// </remarks>
public sealed record CameraReachabilityReport
{
    public required bool Reachable { get; init; }
    public double? ConnectMs { get; init; }
    public string? Failure { get; init; }
}

/// <summary>
/// Vendor-agnostic reachability probe for a standalone camera that has not been registered yet.
/// </summary>
/// <remarks>
/// <para>
/// <b>Not a vendor adapter and never becomes one.</b> Federation.Adapters exists for VMS targets
/// that Model 3 polls and correlates events for; a registry camera being tested before it is even
/// saved is neither. This opens a plain TCP socket to the given host and port and reports whether
/// it opened -- no ONVIF, no RTSP handshake, no authentication of any kind. `protocol` is stored
/// alongside the result for the operator's own record; the probe does not branch on it.
/// </para>
/// <para>
/// Mirrors <see cref="ConnectionTester"/>'s async create-then-poll shape (deliberately, for
/// operator-experience consistency) without sharing its machinery: no <c>IAdapterFactory</c>, no
/// capability discovery, no credential resolution.
/// </para>
/// </remarks>
public sealed partial class CameraReachabilityProbe
{
    /// <summary>
    /// How long a socket is given to open. Short and fixed -- unlike <see cref="ConnectionTester"/>,
    /// nothing here waits on vendor protocol chatter, so there is no reason to allow anywhere near
    /// its 60-second ceiling.
    /// </summary>
    public static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(5);

    private readonly ILogger<CameraReachabilityProbe> _logger;

    public CameraReachabilityProbe(ILogger<CameraReachabilityProbe> logger) => _logger = logger;

    public static async Task<CameraReachabilityReport> RunAsync(
        string ipAddress, int port, CancellationToken cancellationToken)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(ConnectTimeout);

        using var client = new TcpClient();
        var started = Stopwatch.StartNew();

        try
        {
            await client.ConnectAsync(ipAddress, port, deadline.Token).ConfigureAwait(false);

            return new CameraReachabilityReport
            {
                Reachable = true,
                ConnectMs = started.Elapsed.TotalMilliseconds,
            };
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return new CameraReachabilityReport
            {
                Reachable = false,
                Failure = $"No response within {ConnectTimeout.TotalSeconds:F0} seconds.",
            };
        }
        catch (SocketException ex)
        {
            return new CameraReachabilityReport { Reachable = false, Failure = ex.Message };
        }
    }

    /// <summary>
    /// Caps how many probes run at once across the process. Cheaper than a real connection test
    /// (a closed TCP handshake fails fast), but still a socket and a thread each -- unbounded
    /// concurrency here is still a self-inflicted resource exhaustion.
    /// </summary>
    private static readonly SemaphoreSlim Concurrency = new(100, 100);

    /// <summary>Runs a queued probe and records the outcome. Same shape as
    /// <see cref="ConnectionTester.ExecuteJobAsync"/>.</summary>
    public async Task ExecuteJobAsync(
        Guid testId, string ipAddress, int port, NpgsqlDataSource dataSource,
        string executedBy, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(dataSource);

        await Concurrency.WaitAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            await MarkRunningAsync(testId, dataSource, executedBy, cancellationToken)
                .ConfigureAwait(false);

            CameraReachabilityReport report;
            try
            {
                report = await RunAsync(ipAddress, port, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                LogProbeFailed(_logger, ipAddress, port, ex);
                report = new CameraReachabilityReport { Reachable = false, Failure = ex.Message };
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
            UPDATE federation.camera_connection_test
            SET status = 'running', executed_by = @executedBy, started_at = now()
            WHERE id = @testId;
            """, new { testId, executedBy }, cancellationToken: ct)).ConfigureAwait(false);
    }

    private static async Task RecordResultAsync(
        Guid testId, CameraReachabilityReport report, NpgsqlDataSource dataSource)
    {
        // Deliberately not the caller's token: an abandoned probe must still record a terminal
        // state, or the sweeper is the only thing that ever closes it out.
        await using var connection = await dataSource.OpenConnectionAsync(CancellationToken.None)
            .ConfigureAwait(false);

        await connection.ExecuteAsync(new CommandDefinition("""
            UPDATE federation.camera_connection_test
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

    [LoggerMessage(Level = LogLevel.Error,
        Message = "Reachability probe for {IpAddress}:{Port} threw unexpectedly")]
    private static partial void LogProbeFailed(
        ILogger logger, string ipAddress, int port, Exception exception);
}
