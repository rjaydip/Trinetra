using System.Net;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;
using Trinetra.Federation.Adapters.Onvif;
using Trinetra.Federation.Core.Abstractions;
using Trinetra.Federation.Core.Errors;
using Trinetra.Federation.Core.Model;

namespace Trinetra.UnitTests;

/// <summary>
/// Verifies that device faults reach the caller as the right exception type.
/// </summary>
/// <remarks>
/// The runtime routes entirely on exception type — <c>AuthException</c> quarantines,
/// <c>CapabilityException</c> is recorded as a permanent device property, and
/// <c>TransientVmsException</c> feeds the circuit breaker. An adapter that loses or mislabels an
/// exception silently sends the target down the wrong path.
/// </remarks>
public sealed class OnvifAdapterFaultTests
{
    private static readonly Guid TargetId = Guid.Parse("11111111-1111-1111-1111-111111111111");

    private static ConnectorTarget Target => new()
    {
        Id = TargetId,
        Code = "TGT-1",
        OrganizationUnitId = Guid.Parse("22222222-2222-2222-2222-222222222222"),
        DisplayName = "Test Camera",
        Vendor = VendorKind.Onvif,
        Endpoint = "http://10.0.0.1",
        CredentialReference = "vault://test",
    };

    private static OnvifAdapter BuildAdapter(Func<HttpRequestMessage, HttpResponseMessage> respond)
    {
        var http = new HttpClient(new StubHandler(respond)) { Timeout = Timeout.InfiniteTimeSpan };
        return new OnvifAdapter(Target, new Credential("admin", "pw"), http,
            NullLogger<OnvifAdapter>.Instance);
    }

    private static HttpResponseMessage SoapFault(string subcode, string reason) =>
        new(HttpStatusCode.InternalServerError)
        {
            Content = new StringContent($"""
                <s:Envelope xmlns:s="http://www.w3.org/2003/05/soap-envelope">
                  <s:Body>
                    <s:Fault>
                      <s:Code><s:Value>s:Sender</s:Value>
                        <s:Subcode><s:Value>{subcode}</s:Value></s:Subcode>
                      </s:Code>
                      <s:Reason><s:Text>{reason}</s:Text></s:Reason>
                    </s:Fault>
                  </s:Body>
                </s:Envelope>
                """, Encoding.UTF8, "application/soap+xml"),
        };

    [Fact]
    public async Task GetVmsTime_SurfacesAuthFailureAsAuthException()
    {
        // The regression this test exists for: a ContinueWith with OnlyOnRanToCompletion turned
        // a faulted task into a *cancelled* one, so the real exception was discarded. The health
        // loop then saw TaskCanceledException, matched no AdapterException handler, and tore
        // down the entire target while logging "worker failed" instead of "credentials rejected".
        var adapter = BuildAdapter(_ => SoapFault("ter:NotAuthorized", "Sender not authorized"));

        var ex = await Should.ThrowAsync<AuthException>(adapter.GetVmsTimeAsync(CancellationToken.None));

        ex.TargetId.ShouldBe(TargetId);
    }

    [Fact]
    public async Task GetVmsTime_DoesNotReportCancellationWhenNothingWasCancelled()
    {
        // Guards the shape of the bug rather than one symptom of it: any exception type must
        // arrive as itself, never as a cancellation.
        var adapter = BuildAdapter(_ => SoapFault("ter:NotAuthorized", "Sender not authorized"));

        var thrown = await Record.ExceptionAsync(() => adapter.GetVmsTimeAsync(CancellationToken.None));

        thrown.ShouldNotBeOfType<TaskCanceledException>();
        thrown.ShouldNotBeOfType<OperationCanceledException>();
    }

    [Fact]
    public async Task GetVmsTime_MapsUnsupportedOperationToCapabilityException()
    {
        // A device lacking an optional service is a permanent fact, not a failure. Treating it
        // as transient would open the circuit breaker on a perfectly healthy device.
        var adapter = BuildAdapter(_ =>
            SoapFault("ter:ActionNotSupported", "Optional action not supported"));

        await Should.ThrowAsync<CapabilityException>(adapter.GetVmsTimeAsync(CancellationToken.None));
    }

    [Fact]
    public async Task GetVmsTime_MapsUnknownFaultToTransient()
    {
        var adapter = BuildAdapter(_ => SoapFault("ter:Fault", "Internal device error"));

        await Should.ThrowAsync<TransientVmsException>(adapter.GetVmsTimeAsync(CancellationToken.None));
    }

    [Fact]
    public async Task GetVmsTime_MapsUnreachableDeviceToTransient()
    {
        var adapter = BuildAdapter(_ => throw new HttpRequestException("Connection refused"));

        await Should.ThrowAsync<TransientVmsException>(adapter.GetVmsTimeAsync(CancellationToken.None));
    }

    [Fact]
    public async Task GetVmsTime_ReadsTheDeviceClock()
    {
        var adapter = BuildAdapter(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("""
                <s:Envelope xmlns:s="http://www.w3.org/2003/05/soap-envelope">
                  <s:Body>
                    <tds:GetSystemDateAndTimeResponse xmlns:tds="http://www.onvif.org/ver10/device/wsdl">
                      <tds:SystemDateAndTime>
                        <tt:UTCDateTime xmlns:tt="http://www.onvif.org/ver10/schema">
                          <tt:Time><tt:Hour>14</tt:Hour><tt:Minute>32</tt:Minute><tt:Second>15</tt:Second></tt:Time>
                          <tt:Date><tt:Year>2026</tt:Year><tt:Month>8</tt:Month><tt:Day>19</tt:Day></tt:Date>
                        </tt:UTCDateTime>
                      </tds:SystemDateAndTime>
                    </tds:GetSystemDateAndTimeResponse>
                  </s:Body>
                </s:Envelope>
                """, Encoding.UTF8, "application/soap+xml"),
        });

        var deviceTime = await adapter.GetVmsTimeAsync(CancellationToken.None);

        deviceTime.ShouldBe(new DateTimeOffset(2026, 8, 19, 14, 32, 15, TimeSpan.Zero));
    }

    [Fact]
    public async Task GetVmsTime_TreatsAnImpossibleDeviceClockAsUnusable()
    {
        // An unset device clock can report month 0. Better a clear transient failure than a
        // crash inside date construction on a routine health check.
        var adapter = BuildAdapter(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("""
                <s:Envelope xmlns:s="http://www.w3.org/2003/05/soap-envelope">
                  <s:Body>
                    <tds:GetSystemDateAndTimeResponse xmlns:tds="http://www.onvif.org/ver10/device/wsdl">
                      <tt:UTCDateTime xmlns:tt="http://www.onvif.org/ver10/schema">
                        <tt:Time><tt:Hour>0</tt:Hour><tt:Minute>0</tt:Minute><tt:Second>0</tt:Second></tt:Time>
                        <tt:Date><tt:Year>0</tt:Year><tt:Month>0</tt:Month><tt:Day>0</tt:Day></tt:Date>
                      </tt:UTCDateTime>
                    </tds:GetSystemDateAndTimeResponse>
                  </s:Body>
                </s:Envelope>
                """, Encoding.UTF8, "application/soap+xml"),
        });

        await Should.ThrowAsync<TransientVmsException>(adapter.GetVmsTimeAsync(CancellationToken.None));
    }

    [Fact]
    public async Task GetVmsTime_MapsATimeoutToTransient_NotCancellation()
    {
        // SocketsHttpHandler.ConnectTimeout surfaces as an OperationCanceledException even
        // though the caller cancelled nothing. Unmapped, it escapes the adapter taxonomy: the
        // circuit breaker counts only TransientVmsException, so it would never open for an
        // unreachable device — the most common failure mode there is.
        var adapter = BuildAdapter(_ => throw new TaskCanceledException(
            "The request was canceled due to the configured HttpClient.Timeout"));

        var ex = await Should.ThrowAsync<TransientVmsException>(adapter.GetVmsTimeAsync(CancellationToken.None));

        ex.TargetId.ShouldBe(TargetId);
        ex.Message.ShouldContain("timed out");
    }

    [Fact]
    public async Task GetVmsTime_StillPropagatesRealCancellation()
    {
        // The other half of the contract: when the caller genuinely cancels — lease lost, worker
        // shutting down — that must stay a cancellation and not be mistaken for a device fault
        // that feeds the circuit breaker.
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();

        var adapter = BuildAdapter(_ => throw new TaskCanceledException());

        await Should.ThrowAsync<OperationCanceledException>(
            adapter.GetVmsTimeAsync(cancelled.Token));
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
