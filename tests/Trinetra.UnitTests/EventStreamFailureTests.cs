using System.Net;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;
using Trinetra.Federation.Adapters.Dahua;
using Trinetra.Federation.Adapters.Hikvision;
using Trinetra.Federation.Core.Abstractions;
using Trinetra.Federation.Core.Errors;
using Trinetra.Federation.Core.Events;
using Trinetra.Federation.Core.Model;

namespace Trinetra.UnitTests;

/// <summary>
/// Verifies that a failure while opening an event stream is reported as an adapter fault.
/// </summary>
/// <remarks>
/// The runtime reconnects a dropped event stream only when it sees an
/// <see cref="AdapterException"/>. Anything else escapes the event loop, faults the target's
/// combined task, and tears down inventory, status and health along with events. Devices reboot
/// routinely, so "the stream dropped" must stay a recoverable event rather than a fatal one.
/// </remarks>
public sealed class EventStreamFailureTests
{
    private static readonly Guid TargetId = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid OrgUnitId = Guid.Parse("22222222-2222-2222-2222-222222222222");

    private static ConnectorTarget TargetFor(VendorKind vendor) => new()
    {
        Id = TargetId,
        Code = "TGT-1",
        OrganizationUnitId = OrgUnitId,
        DisplayName = "Test NVR",
        Vendor = vendor,
        Endpoint = "http://10.0.0.5",
        CredentialReference = "vault://test",
    };

    private static HttpClient StubClient(Func<HttpRequestMessage, HttpResponseMessage> respond) =>
        new(new StubHandler(respond)) { Timeout = Timeout.InfiniteTimeSpan };

    private static HikvisionAdapter Hikvision(Func<HttpRequestMessage, HttpResponseMessage> respond) =>
        new(TargetFor(VendorKind.HikvisionIsapi), new Credential("admin", "pw"),
            StubClient(respond), NullLogger<HikvisionAdapter>.Instance);

    private static DahuaAdapter Dahua(Func<HttpRequestMessage, HttpResponseMessage> respond) =>
        new(TargetFor(VendorKind.DahuaCgi), new Credential("admin", "pw"),
            StubClient(respond), NullLogger<DahuaAdapter>.Instance);

    private static async Task<Exception?> DrainAsync(IAsyncEnumerable<NormalisedEvent> stream)
    {
        try
        {
            await foreach (var _ in stream)
            {
                // The failures under test occur before any event is produced.
            }

            return null;
        }
        catch (Exception ex)
        {
            return ex;
        }
    }

    [Fact]
    public async Task Hikvision_UnreachableDevice_IsTransient_NotFatal()
    {
        // An NVR rebooting must reconnect the stream, not kill the whole site worker.
        var adapter = Hikvision(_ => throw new HttpRequestException("Connection refused"));

        var thrown = await DrainAsync(adapter.SubscribeEventsAsync(CancellationToken.None));

        thrown.ShouldBeOfType<TransientVmsException>();
    }

    [Fact]
    public async Task Hikvision_StreamOpenTimeout_IsTransient_NotCancellation()
    {
        // A timeout arrives as a cancellation even though the caller cancelled nothing.
        // Unmapped, the circuit breaker never counts it and the target is torn down instead.
        var adapter = Hikvision(_ => throw new TaskCanceledException("timed out"));

        var thrown = await DrainAsync(adapter.SubscribeEventsAsync(CancellationToken.None));

        thrown.ShouldBeOfType<TransientVmsException>();
        thrown!.Message.ShouldContain("timed out");
    }

    [Fact]
    public async Task Hikvision_RejectedCredentials_AreNotRetried()
    {
        // AuthException is deliberately excluded from the reconnect path: retrying rejected
        // credentials across an 80k-camera estate locks the integration account out everywhere.
        var adapter = Hikvision(_ => new HttpResponseMessage(HttpStatusCode.Unauthorized));

        var thrown = await DrainAsync(adapter.SubscribeEventsAsync(CancellationToken.None));

        thrown.ShouldBeOfType<AuthException>();
    }

    [Fact]
    public async Task Dahua_UnreachableDevice_IsTransient_NotFatal()
    {
        var adapter = Dahua(_ => throw new HttpRequestException("No route to host"));

        var thrown = await DrainAsync(adapter.SubscribeEventsAsync(CancellationToken.None));

        thrown.ShouldBeOfType<TransientVmsException>();
    }

    [Fact]
    public async Task Dahua_StreamOpenTimeout_IsTransient_NotCancellation()
    {
        var adapter = Dahua(_ => throw new TaskCanceledException("timed out"));

        var thrown = await DrainAsync(adapter.SubscribeEventsAsync(CancellationToken.None));

        thrown.ShouldBeOfType<TransientVmsException>();
    }

    [Fact]
    public async Task StreamWithoutBoundary_IsANormalisationFault_NotATransportFault()
    {
        // A device that answers 200 but sends an unparseable stream is malformed, not
        // unreachable. Counting it as transient would have the circuit breaker retry forever
        // against a device that will never produce a usable stream.
        var adapter = Hikvision(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("not multipart", Encoding.UTF8, "text/plain"),
        });

        var thrown = await DrainAsync(adapter.SubscribeEventsAsync(CancellationToken.None));

        thrown.ShouldBeOfType<NormalisationException>();
    }

    private sealed class StubHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _respond;

        public StubHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) => _respond = respond;

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(_respond(request));
    }
}
