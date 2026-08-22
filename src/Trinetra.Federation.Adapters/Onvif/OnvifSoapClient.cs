using System.Globalization;
using System.Net;
using System.Text;
using System.Xml;
using System.Xml.Linq;
using Trinetra.Federation.Adapters.Http;
using Trinetra.Federation.Core.Abstractions;
using Trinetra.Federation.Core.Errors;
using Trinetra.Federation.Core.Model;

namespace Trinetra.Federation.Adapters.Onvif;

/// <summary>
/// Minimal SOAP 1.2 client for the ONVIF operations Model 3 needs.
/// </summary>
/// <remarks>
/// <para>
/// Requests are built and responses read with <see cref="XDocument"/> rather than
/// <c>XmlSerializer</c>. That is a deliberate response to how ONVIF devices actually behave:
/// serializers are strict about element <i>sequence</i>, and cheap firmware reorders elements,
/// omits optional ones, and invents extensions freely. Namespace-aware element lookup tolerates
/// all of that; a strict schema binding turns every deviation into an unrecoverable failure.
/// Tolerating device quirks <i>is</i> the ONVIF integration problem.
/// </para>
/// <para>
/// No retries, rate limiting or circuit breaking here — the runtime owns all of that. This
/// class only translates transport and SOAP faults into the adapter exception taxonomy.
/// </para>
/// </remarks>
internal sealed class OnvifSoapClient
{
    private readonly HttpClient _http;
    private readonly ConnectorTarget _target;
    private readonly Credential _credential;

    /// <summary>
    /// Device clock minus host clock, measured once via the unauthenticated
    /// <c>GetSystemDateAndTime</c>. Applied to every WS-Security token so authentication
    /// survives a drifted device clock.
    /// </summary>
    private TimeSpan _clockOffset = TimeSpan.Zero;

    internal OnvifSoapClient(HttpClient http, ConnectorTarget target, Credential credential)
    {
        _http = http;
        _target = target;
        _credential = credential;
    }

    /// <summary>Device time as currently understood, used for the WS-Security timestamp.</summary>
    internal DateTimeOffset DeviceNow => DateTimeOffset.UtcNow + _clockOffset;

    internal TimeSpan ClockOffset => _clockOffset;

    internal void SetClockOffset(TimeSpan offset) => _clockOffset = offset;

    /// <summary>
    /// Sends a SOAP request and returns the body of the response.
    /// </summary>
    /// <param name="serviceUri">Absolute service endpoint (device, media, or events).</param>
    /// <param name="body">The operation element, e.g. <c>&lt;tds:GetDeviceInformation/&gt;</c>.</param>
    /// <param name="authenticate">
    /// False only for <c>GetSystemDateAndTime</c>, which ONVIF defines as unauthenticated and
    /// which must therefore run before a valid token can be built.
    /// </param>
    /// <param name="action">WS-Addressing action, required by the events service.</param>
    /// <param name="cancellationToken">Owned by the runtime; the sole deadline authority.</param>
    internal async Task<XElement> InvokeAsync(
        Uri serviceUri,
        XElement body,
        bool authenticate,
        string? action,
        CancellationToken cancellationToken)
    {
        var envelope = BuildEnvelope(body, authenticate, action, serviceUri);

        using var request = new HttpRequestMessage(HttpMethod.Post, serviceUri)
        {
            Content = new StringContent(
                envelope.ToString(SaveOptions.DisableFormatting), Encoding.UTF8),
        };

        request.Content.Headers.ContentType =
            new System.Net.Http.Headers.MediaTypeHeaderValue("application/soap+xml")
            {
                CharSet = "utf-8",
            };

        // Some devices reject WS-Security and demand HTTP digest instead; supplying the
        // credential lets DigestAuthHandler satisfy a 401 without the adapter knowing which
        // scheme this particular firmware wanted.
        request.Options.Set(DigestAuthHandler.CredentialOption, _credential);
        request.Options.Set(DigestAuthHandler.TargetIdOption, _target.Id);

        HttpResponseMessage response;
        try
        {
            response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
        }
        catch (HttpRequestException ex)
        {
            throw new TransientVmsException(
                $"ONVIF request to {serviceUri} failed: {ex.Message}", _target.Id, ex);
        }
        catch (OperationCanceledException ex) when (!cancellationToken.IsCancellationRequested)
        {
            // A connect or response timeout arrives as a cancellation even though the caller
            // cancelled nothing. Left unmapped it escapes the adapter exception taxonomy
            // entirely: the circuit breaker only counts TransientVmsException, so it would
            // never open for an unreachable device — the single most common failure there is.
            throw new TransientVmsException(
                $"ONVIF request to {serviceUri} timed out.", _target.Id, ex);
        }
        catch (IOException ex)
        {
            throw new TransientVmsException(
                $"ONVIF connection to {serviceUri} was interrupted: {ex.Message}",
                _target.Id, ex);
        }

        using (response)
        {
            var content = await response.Content.ReadAsStringAsync(cancellationToken)
                .ConfigureAwait(false);

            if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
            {
                throw new AuthException(
                    $"ONVIF device rejected credentials at {serviceUri}.", _target.Id);
            }

            if (response.StatusCode == HttpStatusCode.TooManyRequests)
            {
                throw new RateLimitedException(
                    "ONVIF device is rate limiting.",
                    response.Headers.RetryAfter?.Delta, _target.Id);
            }

            // A SOAP fault arrives as HTTP 500 with a fault body, so the body is parsed before
            // the status code is judged.
            XDocument document;
            try
            {
                document = XDocument.Parse(content);
            }
            catch (XmlException ex)
            {
                if (!response.IsSuccessStatusCode)
                {
                    throw new TransientVmsException(
                        $"ONVIF device returned {(int)response.StatusCode} with a non-XML body.",
                        _target.Id, ex);
                }

                throw new NormalisationException(
                    $"ONVIF response from {serviceUri} was not well-formed XML.",
                    _target.Id, ex);
            }

            var responseBody = document.Root?.Element(Ns.Soap + "Body")
                ?? throw new NormalisationException(
                    "ONVIF response contained no SOAP Body.", _target.Id);

            ThrowIfFault(responseBody, serviceUri);

            if (!response.IsSuccessStatusCode)
            {
                throw new TransientVmsException(
                    $"ONVIF device returned {(int)response.StatusCode} for {serviceUri}.",
                    _target.Id);
            }

            return responseBody;
        }
    }

    /// <summary>
    /// Maps a SOAP fault onto the adapter taxonomy.
    /// </summary>
    /// <remarks>
    /// The distinction matters operationally: an <c>ActionNotSupported</c> fault is a permanent
    /// fact about the device that capability probing should record once, whereas treating it as
    /// a transient failure would have the circuit breaker open on a device that is working
    /// perfectly well and simply lacks an optional service.
    /// </remarks>
    private void ThrowIfFault(XElement body, Uri serviceUri)
    {
        var fault = body.Element(Ns.Soap + "Fault");
        if (fault is null)
        {
            return;
        }

        var subcode = fault.Element(Ns.Soap + "Code")?.Element(Ns.Soap + "Subcode")
            ?.Element(Ns.Soap + "Value")?.Value ?? string.Empty;
        var reason = fault.Element(Ns.Soap + "Reason")?.Element(Ns.Soap + "Text")?.Value
            ?? "unspecified SOAP fault";

        // ONVIF also nests a more specific subcode one level deeper.
        var detailSubcode = fault.Element(Ns.Soap + "Code")?.Element(Ns.Soap + "Subcode")
            ?.Element(Ns.Soap + "Subcode")?.Element(Ns.Soap + "Value")?.Value ?? string.Empty;

        var combined = $"{subcode}|{detailSubcode}";

        if (combined.Contains("NotAuthorized", StringComparison.OrdinalIgnoreCase)
            || combined.Contains("NotAuthorized", StringComparison.OrdinalIgnoreCase)
            || reason.Contains("not authorized", StringComparison.OrdinalIgnoreCase))
        {
            throw new AuthException(
                $"ONVIF device rejected credentials: {reason}", _target.Id);
        }

        if (combined.Contains("ActionNotSupported", StringComparison.OrdinalIgnoreCase)
            || combined.Contains("OperationProhibited", StringComparison.OrdinalIgnoreCase)
            || reason.Contains("not supported", StringComparison.OrdinalIgnoreCase)
            || reason.Contains("not implemented", StringComparison.OrdinalIgnoreCase))
        {
            throw new CapabilityException(
                $"ONVIF device does not support this operation: {reason}",
                detailSubcode.Length > 0 ? detailSubcode : subcode,
                _target.Id);
        }

        throw new TransientVmsException(
            $"ONVIF fault from {serviceUri}: {reason} ({combined})", _target.Id);
    }

    private XDocument BuildEnvelope(
        XElement body, bool authenticate, string? action, Uri serviceUri)
    {
        var header = new XElement(Ns.Soap + "Header");

        if (action is not null)
        {
            // The events service requires WS-Addressing; the device and media services do not
            // and some firmware faults if it is present, so it is added only on request.
            header.Add(
                new XElement(Ns.Wsa + "Action",
                    new XAttribute(XNamespace.Xmlns + "wsa", Ns.Wsa.NamespaceName), action),
                new XElement(Ns.Wsa + "To", serviceUri.ToString()),
                new XElement(Ns.Wsa + "MessageID", $"urn:uuid:{Guid.NewGuid()}"),
                new XElement(Ns.Wsa + "ReplyTo",
                    new XElement(Ns.Wsa + "Address",
                        "http://www.w3.org/2005/08/addressing/anonymous")));
        }

        if (authenticate)
        {
            header.Add(WsUsernameToken.Create(_credential, DeviceNow));
        }

        return new XDocument(
            new XDeclaration("1.0", "utf-8", null),
            new XElement(Ns.Soap + "Envelope",
                new XAttribute(XNamespace.Xmlns + "s", Ns.Soap.NamespaceName),
                header.HasElements ? header : null,
                new XElement(Ns.Soap + "Body", body)));
    }

    /// <summary>Parses an ONVIF <c>xs:duration</c> such as <c>PT30S</c>.</summary>
    internal static TimeSpan ParseDuration(string? value, TimeSpan fallback)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return fallback;
        }

        try
        {
            return XmlConvert.ToTimeSpan(value);
        }
        catch (FormatException)
        {
            return fallback;
        }
    }

    /// <summary>
    /// Parses an ONVIF timestamp, tolerating the several shapes devices emit.
    /// </summary>
    /// <remarks>
    /// Returns <see cref="DateTimeOffset"/> in UTC. A value without an offset is assumed UTC,
    /// which is what ONVIF specifies; assuming local time here would silently shift every event
    /// by the host's timezone.
    /// </remarks>
    internal static bool TryParseTimestamp(string? value, out DateTimeOffset result)
    {
        result = default;
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        if (DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture,
                DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out result))
        {
            return true;
        }

        return false;
    }
}
