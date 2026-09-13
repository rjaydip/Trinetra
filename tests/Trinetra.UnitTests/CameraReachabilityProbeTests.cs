using System.Net;
using System.Net.Sockets;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;
using Trinetra.Federation.Runtime;

namespace Trinetra.UnitTests;

/// <summary>
/// The standalone-camera reachability probe: a plain TCP connect, no vendor adapter, no
/// credential. Tested directly against real loopback sockets — no database, no Testcontainers.
/// </summary>
public sealed class CameraReachabilityProbeTests
{
    [Fact]
    public async Task Reachable_when_something_is_listening()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var acceptTask = listener.AcceptTcpClientAsync();

        try
        {
            var report = await CameraReachabilityProbe.RunAsync(
                "127.0.0.1", port, CancellationToken.None);

            report.Reachable.ShouldBeTrue();
            report.ConnectMs.ShouldNotBeNull();
            report.Failure.ShouldBeNull();

            (await acceptTask).Dispose();
        }
        finally
        {
            listener.Stop();
        }
    }

    [Fact]
    public async Task Unreachable_when_nothing_is_listening()
    {
        // Bind to find a free port, then close it immediately -- nothing listens there afterwards,
        // and a closed port refuses the connection fast instead of relying on the timeout.
        int port;
        using (var probe = new TcpListener(IPAddress.Loopback, 0))
        {
            probe.Start();
            port = ((IPEndPoint)probe.LocalEndpoint).Port;
        }

        var report = await CameraReachabilityProbe.RunAsync(
            "127.0.0.1", port, CancellationToken.None);

        report.Reachable.ShouldBeFalse();
        report.ConnectMs.ShouldBeNull();
        report.Failure.ShouldNotBeNullOrWhiteSpace();
    }

    [Fact]
    public async Task Result_carries_no_vendor_or_authentication_information()
    {
        // Deliberately narrow contract: only Reachable / ConnectMs / Failure exist on the type at
        // all -- there is no Capabilities, CameraCount or authentication outcome to accidentally
        // populate, unlike Runtime.ConnectionTestReport for VMS targets.
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var acceptTask = listener.AcceptTcpClientAsync();

        try
        {
            var report = await CameraReachabilityProbe.RunAsync(
                "127.0.0.1", port, CancellationToken.None);

            typeof(CameraReachabilityReport).GetProperties().Select(p => p.Name)
                .ShouldBe(["Reachable", "ConnectMs", "Failure"], ignoreOrder: true);

            (await acceptTask).Dispose();
        }
        finally
        {
            listener.Stop();
        }
    }
}
