using System.Globalization;
using System.Net.Security;
using System.Security.Authentication;
using System.Net.Sockets;
using System.Text;
using Trinetra.Federation.Core.Abstractions;
using Trinetra.Federation.Core.Errors;

namespace Trinetra.Federation.Adapters.Rtsp;

/// <summary>Outcome of a stream reachability probe.</summary>
public sealed record RtspProbeResult
{
    public required bool Reachable { get; init; }
    public required TimeSpan Latency { get; init; }
    public string? Codec { get; init; }
    public string? Resolution { get; init; }
    public int TrackCount { get; init; }
    public string? Failure { get; init; }
}

/// <summary>
/// Verifies that a stream reference actually resolves, without touching video.
/// </summary>
/// <remarks>
/// <para>
/// <b>Control plane only: <c>OPTIONS</c> then <c>DESCRIBE</c>, read the SDP, disconnect.</b>
/// No <c>SETUP</c>, no <c>PLAY</c>, therefore no RTP and no media bytes. Model 3's "never touch
/// video" rule holds — this establishes whether a reference works, which is not the same
/// question as what the video contains.
/// </para>
/// <para>
/// It matters most for Hikvision and Dahua, whose stream URIs are built from a <i>template</i>
/// rather than discovered. Without probing, "we hold a stream reference" and "we hold a stream
/// reference that works" are indistinguishable until an investigator needs the footage.
/// </para>
/// <para>
/// <b>Probing is per-camera work and must never sit on a poll loop.</b> An exhaustive sweep of
/// 80,000 cameras is 80,000 TCP handshakes — exactly the camera-count-proportional work the
/// architecture forbids. Callers must probe on inventory change, on demand, or from a rotating
/// sample, and must hold at most one probe per target: many NVRs cap concurrent RTSP sessions
/// (often 4-8), and burning those slots would deny them to actual operators.
/// </para>
/// </remarks>
public static class RtspProbe
{
    private const int MaxSdpBytes = 64 * 1024;

    public static async Task<RtspProbeResult> ProbeAsync(
        string streamUri,
        Credential credential,
        Guid targetId,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        if (!Uri.TryCreate(streamUri, UriKind.Absolute, out var uri)
            || (uri.Scheme != "rtsp" && uri.Scheme != "rtsps"))
        {
            return new RtspProbeResult
            {
                Reachable = false,
                Latency = TimeSpan.Zero,
                Failure = $"'{streamUri}' is not an RTSP URI.",
            };
        }

        var started = System.Diagnostics.Stopwatch.StartNew();

        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(timeout);

        try
        {
            using var socket = new TcpClient();
            await socket.ConnectAsync(uri.Host, uri.Port > 0 ? uri.Port : 554, deadline.Token)
                .ConfigureAwait(false);

            Stream stream = socket.GetStream();

            if (uri.Scheme == "rtsps")
            {
                var tls = new SslStream(stream, leaveInnerStreamOpen: false);
                await tls.AuthenticateAsClientAsync(uri.Host).ConfigureAwait(false);
                stream = tls;
            }

            await using (stream.ConfigureAwait(false))
            {
                var session = new RtspSession(stream, uri, credential);

                await session.SendAsync("OPTIONS", MaxSdpBytes, deadline.Token).ConfigureAwait(false);
                var describe = await session.SendAsync("DESCRIBE", MaxSdpBytes, deadline.Token)
                    .ConfigureAwait(false);

                var sdp = SdpParser.Parse(describe.Body);

                return new RtspProbeResult
                {
                    Reachable = true,
                    Latency = started.Elapsed,
                    Codec = sdp.Codec,
                    Resolution = sdp.Resolution,
                    TrackCount = sdp.TrackCount,
                };
            }
        }
        catch (AuthException)
        {
            // Surfaced, not swallowed: stream credentials often differ from the VMS API
            // credentials, and a probe is frequently the first place that divergence shows up.
            throw;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return new RtspProbeResult
            {
                Reachable = false,
                Latency = started.Elapsed,
                Failure = $"Timed out after {timeout.TotalSeconds:F0}s.",
            };
        }
        catch (Exception ex) when (ex is SocketException or IOException or AuthenticationException)
        {
            // A failed DESCRIBE usually means a wrong URI template or wrong stream credentials,
            // not a dead camera — so it is reported as a stream-level fact rather than thrown as
            // a target-level failure that would open the circuit breaker.
            return new RtspProbeResult
            {
                Reachable = false,
                Latency = started.Elapsed,
                Failure = ex.Message,
            };
        }
    }
}
