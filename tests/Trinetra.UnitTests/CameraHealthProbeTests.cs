using System.Net;
using System.Net.Sockets;
using System.Text;
using Shouldly;
using Trinetra.Federation.Core.Abstractions;
using Trinetra.Federation.Runtime;

namespace Trinetra.UnitTests;

/// <summary>
/// <see cref="CameraHealthProbe"/>'s mapping from <c>CameraCredentialProbe</c>'s richer
/// <c>AuthOutcome</c> onto <see cref="CameraProbeResult"/>'s reachable/errorCode shape — the
/// underlying RTSP handshake and digest-auth logic themselves are already covered by
/// <see cref="CameraCredentialProbeTests"/>; this only tests the adapter on top.
/// </summary>
public sealed class CameraHealthProbeTests
{
    [Fact]
    public async Task An_unchallenged_200_is_reachable_with_no_error_code()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var serverTask = RespondOnceAsync(listener, "RTSP/1.0 200 OK\r\nCSeq: 1\r\n\r\n");

        var probe = new CameraHealthProbe();
        var result = await probe.ProbeAsync(
            new CameraProbeTarget(Guid.NewGuid(), "RTSP", "127.0.0.1", port, null, null),
            CancellationToken.None);

        await serverTask;

        result.Reachable.ShouldBeTrue();
        result.ErrorCode.ShouldBeNull();
        result.LatencyMs.ShouldNotBeNull();
    }

    [Fact]
    public async Task A_rejected_credential_is_still_reachable_but_flagged_AUTH_FAILED()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;

        // First OPTIONS -> 401 challenge; second (authenticated) OPTIONS -> 401 again (rejected).
        var serverTask = Task.Run(async () =>
        {
            using var first = await listener.AcceptTcpClientAsync();
            await RespondAsync(first, "RTSP/1.0 401 Unauthorized\r\nCSeq: 1\r\n"
                + "WWW-Authenticate: Digest realm=\"cam\", nonce=\"abc123\"\r\n\r\n");

            using var second = await listener.AcceptTcpClientAsync();
            await RespondAsync(second, "RTSP/1.0 401 Unauthorized\r\nCSeq: 1\r\n\r\n");
        });

        var probe = new CameraHealthProbe();
        var result = await probe.ProbeAsync(
            new CameraProbeTarget(Guid.NewGuid(), "RTSP", "127.0.0.1", port, "admin", "wrong"),
            CancellationToken.None);

        await serverTask;

        result.Reachable.ShouldBeTrue();
        result.ErrorCode.ShouldBe("AUTH_FAILED");
    }

    [Fact]
    public async Task Nothing_listening_is_unreachable()
    {
        using var probeSocket = new TcpListener(IPAddress.Loopback, 0);
        probeSocket.Start();
        var port = ((IPEndPoint)probeSocket.LocalEndpoint).Port;
        probeSocket.Stop(); // freed immediately: nothing will ever answer on this port

        var probe = new CameraHealthProbe();
        var result = await probe.ProbeAsync(
            new CameraProbeTarget(Guid.NewGuid(), "RTSP", "127.0.0.1", port, null, null),
            CancellationToken.None);

        result.Reachable.ShouldBeFalse();
        result.ErrorCode.ShouldBe("UNREACHABLE");
    }

    private static async Task RespondOnceAsync(TcpListener listener, string response)
    {
        using var client = await listener.AcceptTcpClientAsync();
        await RespondAsync(client, response);
    }

    private static async Task RespondAsync(TcpClient client, string response)
    {
        using var stream = client.GetStream();
        var buffer = new byte[4096];
        _ = await stream.ReadAsync(buffer); // drain the request so the probe's write doesn't block
        await stream.WriteAsync(Encoding.ASCII.GetBytes(response));
    }
}
