using System.Runtime.CompilerServices;
using System.Xml.Linq;
using Microsoft.Extensions.Logging;
using Trinetra.Federation.Adapters.Http;
using Trinetra.Federation.Core.Abstractions;
using Trinetra.Federation.Core.Capabilities;
using Trinetra.Federation.Core.Errors;
using Trinetra.Federation.Core.Events;
using Trinetra.Federation.Core.Model;

namespace Trinetra.Federation.Adapters.Onvif;

/// <summary>
/// ONVIF Profile S/G/T adapter.
/// </summary>
/// <remarks>
/// <para>
/// Reaches devices over their HTTP/SOAP surface with no vendor SDK, so it runs in the
/// <see cref="RuntimeClass.Managed"/> class packed many-per-process.
/// </para>
/// <para>
/// <b>Events are PullPoint only.</b> ONVIF also defines push (Base) notification, but push
/// bakes a consumer address into the subscription at creation time — so when a target moves to
/// another worker the device keeps posting to the dead one. That would require a stable
/// collector in front of the fleet, reintroducing exactly the coordinator the lease design
/// removes. PullPoint is outbound-only from our side and therefore also survives the
/// departmental firewalls this platform has to work behind.
/// </para>
/// <para>
/// <b>ONVIF cannot backfill.</b> There is no historical event query in the standard, so
/// <see cref="Capability.EventsPull"/> is reported unsupported and
/// <see cref="GetEventsAsync"/> always throws. A lease handover loses up to one lease TTL of
/// events on these targets. That is inherent to ONVIF, not a defect here, and it is surfaced
/// rather than hidden.
/// </para>
/// </remarks>
public sealed partial class OnvifAdapter : IVmsAdapter
{
    private const string PullMessagesAction =
        "http://www.onvif.org/ver10/events/wsdl/PullPointSubscription/PullMessagesRequest";
    private const string RenewAction =
        "http://docs.oasis-open.org/wsn/bw-2/SubscriptionManager/RenewRequest";
    private const string UnsubscribeAction =
        "http://docs.oasis-open.org/wsn/bw-2/SubscriptionManager/UnsubscribeRequest";

    private readonly OnvifSoapClient _soap;
    private readonly ILogger<OnvifAdapter> _logger;

    private Uri _deviceService;
    private Uri? _mediaService;
    private Uri? _events;
    private CapabilitySet? _capabilities;
    private Uri? _subscription;
    private bool _disposed;

    public OnvifAdapter(
        ConnectorTarget target,
        Credential credential,
        TargetHttpClientProvider clients,
        ILogger<OnvifAdapter> logger)
        : this(target, credential, clients.For(target), logger)
    {
    }

    /// <summary>
    /// Binds the adapter to a specific <see cref="HttpClient"/>.
    /// </summary>
    /// <remarks>
    /// The adapter only ever needed a client, not the whole provider. Exposing that lets tests
    /// drive it through a stub transport without reaching into TLS-policy plumbing they are not
    /// exercising.
    /// </remarks>
    internal OnvifAdapter(
        ConnectorTarget target,
        Credential credential,
        HttpClient http,
        ILogger<OnvifAdapter> logger)
    {
        Target = target;
        _logger = logger;
        _soap = new OnvifSoapClient(http, target, credential);
        _deviceService = BuildDeviceServiceUri(target.Endpoint);
    }

    public VendorKind Vendor => VendorKind.Onvif;

    public ConnectorTarget Target { get; }

    public CapabilitySet Capabilities => _capabilities
        ?? throw new InvalidOperationException(
            "ProbeCapabilitiesAsync must run before Capabilities is read.");

    // ---- Lifecycle ---------------------------------------------------------

    public async Task ConnectAsync(CancellationToken cancellationToken)
    {
        // GetSystemDateAndTime is unauthenticated by specification, and must run first: the
        // WS-Security digest embeds a timestamp the device has to consider current, so a
        // drifted device clock makes correct credentials fail indistinguishably from wrong
        // ones. Measuring the offset here is both the reachability probe and the auth bootstrap.
        var deviceTime = await ReadSystemDateAndTimeAsync(cancellationToken).ConfigureAwait(false);

        if (deviceTime is { } device)
        {
            var offset = device - DateTimeOffset.UtcNow;
            _soap.SetClockOffset(offset);

            if (Math.Abs(offset.TotalSeconds) > 60)
            {
                LogClockSkew(_logger, Target.Id, offset.TotalSeconds);
            }
        }

        await DiscoverServicesAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<CapabilitySet> ProbeCapabilitiesAsync(CancellationToken cancellationToken)
    {
        // Probed by asking the device, never inferred from vendor or firmware string:
        // capability varies by model, licence tier and configuration within one vendor.
        var supported = Capability.TimeSyncCheck;
        var notes = new Dictionary<string, string>(StringComparer.Ordinal);

        if (_mediaService is not null)
        {
            supported |= Capability.Inventory | Capability.Streams | Capability.CameraStatus
                         | Capability.Snapshot;
        }
        else
        {
            notes["Inventory"] = "Device advertised no Media service.";
        }

        if (_events is not null)
        {
            supported |= Capability.EventsSubscribe;
        }
        else
        {
            notes["EventsSubscribe"] = "Device advertised no Events service.";
        }

        // Never available on ONVIF at any firmware level: the standard defines no historical
        // event query, so there is nothing to probe for.
        notes["EventsPull"] =
            "ONVIF defines no historical event query. Events are live-only via PullPoint, so a "
            + "lease handover loses up to one lease TTL of events on this target.";

        _capabilities = new CapabilitySet
        {
            Supported = supported,
            AdapterVersion = "0.1.0",
            ProbedAt = DateTimeOffset.UtcNow,
            Notes = notes,
        };

        return _capabilities;
    }

    // ---- Inventory ---------------------------------------------------------

    /// <summary>
    /// Cameras behind this endpoint, derived from distinct video sources.
    /// </summary>
    /// <remarks>
    /// Inventory is keyed on the <b>video source token</b>, not the media profile. A device
    /// typically publishes several profiles per physical camera — a main stream and a substream
    /// are two profiles of one source. Treating profiles as cameras would inflate the estate's
    /// camera count several-fold and create duplicate registry reconciliation candidates.
    /// </remarks>
    public async Task<IReadOnlyList<FederatedCamera>> GetCamerasAsync(
        CancellationToken cancellationToken)
    {
        RequireCapability(Capability.Inventory);

        var profiles = await GetProfilesAsync(cancellationToken).ConfigureAwait(false);
        var deviceInfo = await GetDeviceInformationAsync(cancellationToken).ConfigureAwait(false);

        // The physical sensors, straight from the device. This is the authoritative camera set:
        // GetProfiles alone over-counts, because a single-sensor camera routinely publishes a
        // main + sub + third profile and cheap devices omit or vary the SourceToken that would
        // tie them back together, so keying inventory off the profiles yields 3 "cameras".
        var videoSources = await GetVideoSourcesAsync(cancellationToken).ConfigureAwait(false);

        var bySource = new Dictionary<string, List<OnvifProfile>>(StringComparer.Ordinal);
        foreach (var token in videoSources)
        {
            bySource[token] = [];
        }

        var soleSource = videoSources.Count == 1 ? videoSources[0] : null;

        foreach (var profile in profiles)
        {
            // Attach the profile to its own source when the device names one we know; otherwise,
            // if the device has exactly one sensor, everything belongs to it. Only when neither
            // holds do we fall back to treating the profile's own token as a distinct camera.
            var key =
                profile.SourceToken is { } s && bySource.ContainsKey(s) ? s
                : soleSource
                ?? profile.SourceToken
                ?? profile.Token;

            if (!bySource.TryGetValue(key, out var list))
            {
                bySource[key] = list = [];
            }

            list.Add(profile);
        }

        var cameras = new List<FederatedCamera>(bySource.Count);
        foreach (var (sourceToken, sourceProfiles) in bySource)
        {
            // A sensor the device reports but publishes no profile for is still a camera —
            // it exists, it just has no stream to hand out yet.
            var primary = sourceProfiles.Count > 0 ? sourceProfiles[0] : null;

            cameras.Add(new FederatedCamera
            {
                TargetId = Target.Id,
                NativeCameraId = sourceToken,
                OrganizationUnitId = Target.OrganizationUnitId,
                Name = primary?.Name ?? sourceToken,
                VendorModel = deviceInfo.Model,
                Firmware = deviceInfo.FirmwareVersion,
                IsEnabled = true,
                Health = HealthStatus.Healthy,
                LastSeen = DateTimeOffset.UtcNow,
                // References only. Model 3 never proxies or decodes video.
                StreamReferences = [.. sourceProfiles
                    .Select(p => p.StreamUri)
                    .Where(u => !string.IsNullOrWhiteSpace(u))
                    .Select(u => u!)],
            });
        }

        return cameras;
    }

    /// <summary>
    /// Current status for every camera on this target.
    /// </summary>
    /// <remarks>
    /// ONVIF exposes no bulk health call, so status is <i>derived</i>: the device answering at
    /// all means its cameras are reachable through it. This is deliberately coarse — reporting
    /// a confident per-camera health the protocol cannot actually supply would be worse than
    /// reporting an honest approximation.
    /// </remarks>
    public async Task<IReadOnlyList<FederatedCamera>> GetCameraStatusAsync(
        CancellationToken cancellationToken)
    {
        RequireCapability(Capability.CameraStatus);
        return await GetCamerasAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<StreamProfile>> GetStreamsAsync(
        string nativeCameraId, CancellationToken cancellationToken)
    {
        RequireCapability(Capability.Streams);

        var profiles = await GetProfilesAsync(cancellationToken).ConfigureAwait(false);

        return [.. profiles
            .Where(p => string.Equals(p.SourceToken ?? p.Token, nativeCameraId, StringComparison.Ordinal))
            .Where(p => !string.IsNullOrWhiteSpace(p.StreamUri))
            .Select((p, index) => new StreamProfile
            {
                CameraId = nativeCameraId,
                ProfileName = p.Name ?? p.Token,
                Uri = p.StreamUri!,
                Codec = p.Encoding,
                Resolution = p.Width is > 0 && p.Height is > 0 ? $"{p.Width}x{p.Height}" : null,
                Framerate = p.FrameRate,
                IsPrimary = index == 0,
            })];
    }

    public Task<IReadOnlyList<RecordingSegment>> GetRecordingsAsync(
        string nativeCameraId, DateTimeOffset rangeStart, DateTimeOffset rangeEnd,
        CancellationToken cancellationToken) =>
        throw new CapabilityException(
            "ONVIF Profile G recording search is not implemented in this adapter version.",
            nameof(Capability.Recordings), Target.Id);

    /// <summary>
    /// Always throws: ONVIF has no historical event query.
    /// </summary>
    /// <remarks>
    /// This is the one capability the standard genuinely cannot provide, so it fails loudly
    /// rather than returning an empty list. An empty list would look like "this target had no
    /// events", which is indistinguishable from a working quiet site and would let the runtime
    /// advance a cursor over events that were never fetched.
    /// </remarks>
    public Task<IReadOnlyList<NormalisedEvent>> GetEventsAsync(
        DateTimeOffset since, DateTimeOffset? until, int limit,
        CancellationToken cancellationToken) =>
        throw new CapabilityException(
            "ONVIF defines no historical event query; use SubscribeEventsAsync. Events missed "
            + "during a lease handover cannot be recovered on this target.",
            nameof(Capability.EventsPull), Target.Id);

    /// <remarks>
    /// Written as a plain async method rather than a <c>ContinueWith</c>. The earlier form used
    /// <c>TaskContinuationOptions.OnlyOnRanToCompletion</c>, which does not run the continuation
    /// when the antecedent faults — so the returned task completed as <i>cancelled</i> and the
    /// original exception was discarded. A rejected credential surfaced to the health loop as
    /// <c>TaskCanceledException</c>, missed every <c>AdapterException</c> handler, and tore down
    /// the whole target worker while logging "worker failed" instead of "credentials rejected".
    /// </remarks>
    public async Task<DateTimeOffset> GetVmsTimeAsync(CancellationToken cancellationToken)
    {
        var deviceTime = await ReadSystemDateAndTimeAsync(cancellationToken).ConfigureAwait(false);

        return deviceTime ?? throw new TransientVmsException(
            "Device did not return a usable system time.", Target.Id);
    }

    // ---- Events ------------------------------------------------------------

    /// <summary>
    /// Live event stream via a PullPoint subscription.
    /// </summary>
    /// <remarks>
    /// The subscription is worker-local and is never persisted or handed over. Two workers
    /// pulling one PullPoint would each silently receive roughly half the messages — a failure
    /// invisible in every metric, since both would look healthy and neither would report loss.
    /// </remarks>
    public async IAsyncEnumerable<NormalisedEvent> SubscribeEventsAsync(
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        RequireCapability(Capability.EventsSubscribe);

        await CreatePullPointAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var messages = await PullMessagesAsync(cancellationToken).ConfigureAwait(false);

                foreach (var message in messages)
                {
                    yield return message;
                }
            }
        }
        finally
        {
            // Best-effort release. Many devices allow only one or a few concurrent PullPoint
            // subscriptions, so an orphan left behind can block the next lease holder from
            // subscribing at all until it times out.
            await TryUnsubscribeAsync().ConfigureAwait(false);
        }
    }

    private async Task CreatePullPointAsync(CancellationToken cancellationToken)
    {
        var events = _events ?? throw new CapabilityException(
            "Device advertised no Events service.", nameof(Capability.EventsSubscribe),
            Target.Id);

        // Termination is kept close to the lease TTL rather than long. A crashed worker's
        // subscription survives until its termination time, and on a device with a small
        // subscription limit that orphan blocks the successor. Short termination bounds the
        // blockage to roughly the failover window we already accept.
        var body = new XElement(Ns.Events + "CreatePullPointSubscription",
            new XAttribute(XNamespace.Xmlns + "tev", Ns.Events.NamespaceName),
            new XElement(Ns.Events + "InitialTerminationTime", "PT120S"));

        var response = await _soap.InvokeAsync(events, body, authenticate: true, action: null, cancellationToken)
            .ConfigureAwait(false);

        var address = response.Descendants()
            .FirstOrDefault(e => e.Name.LocalName == "SubscriptionReference")
            ?.Descendants().FirstOrDefault(e => e.Name.LocalName == "Address")?.Value;

        if (string.IsNullOrWhiteSpace(address))
        {
            throw new NormalisationException(
                "Device returned no PullPoint subscription address.", Target.Id);
        }

        _subscription = RewriteSubscriptionAddress(address, Target.Endpoint);
    }

    /// <summary>
    /// Rewrites a subscription address to the endpoint we actually reached the device on.
    /// </summary>
    /// <remarks>
    /// <b>The single most common ONVIF integration bug.</b> Devices routinely return a
    /// subscription address built from their own internal view of the network — a NAT'd or
    /// private address such as <c>http://192.168.1.64/onvif/...</c> — even when reached through
    /// a router or across a departmental VPN. Using it verbatim means every pull attempt goes to
    /// an unroutable host. Only the path and query are meaningful to us; the authority must come
    /// from the configured endpoint.
    /// </remarks>
    internal static Uri RewriteSubscriptionAddress(string returnedAddress, string configuredEndpoint)
    {
        var configured = new Uri(NormaliseEndpoint(configuredEndpoint));

        // The scheme check is load-bearing, not defensive noise: on Unix,
        // Uri.TryCreate("/onvif/Subscription", UriKind.Absolute, ...) returns TRUE, parsing the
        // leading slash as a file:// URI. Testing only IsAbsoluteUri would therefore route every
        // relative device address down the absolute path and mangle its query string.
        var isAbsoluteHttp =
            Uri.TryCreate(returnedAddress, UriKind.Absolute, out var returned)
            && (returned.Scheme == Uri.UriSchemeHttp || returned.Scheme == Uri.UriSchemeHttps);

        if (!isAbsoluteHttp)
        {
            // Path and query are split by hand rather than using new Uri(base, relative):
            // that overload percent-escapes the '?' of a relative reference, turning
            // "/onvif/Subscription?Idx=1" into ".../Subscription%3FIdx=1". The subscription's
            // reference parameters live in that query, so the corruption sends every
            // subsequent PullMessages to a URL the device does not recognise.
            var split = returnedAddress.IndexOf('?', StringComparison.Ordinal);

            return new UriBuilder(configured.Scheme, configured.Host, configured.Port)
            {
                Path = split < 0 ? returnedAddress : returnedAddress[..split],
                Query = split < 0 ? string.Empty : returnedAddress[(split + 1)..],
            }.Uri;
        }

        return new UriBuilder(returned!)
        {
            Scheme = configured.Scheme,
            Host = configured.Host,
            Port = configured.Port,
        }.Uri;
    }

    private async Task<IReadOnlyList<NormalisedEvent>> PullMessagesAsync(
        CancellationToken cancellationToken)
    {
        var subscription = _subscription ?? throw new TransientVmsException(
            "PullPoint subscription is not established.", Target.Id);

        var body = new XElement(Ns.Events + "PullMessages",
            new XAttribute(XNamespace.Xmlns + "tev", Ns.Events.NamespaceName),
            new XElement(Ns.Events + "Timeout", "PT30S"),
            new XElement(Ns.Events + "MessageLimit", 100));

        var response = await _soap.InvokeAsync(
            subscription, body, authenticate: true, PullMessagesAction, cancellationToken)
            .ConfigureAwait(false);

        var results = new List<NormalisedEvent>();

        foreach (var notification in response.Descendants()
            .Where(e => e.Name.LocalName == "NotificationMessage"))
        {
            var normalised = TryNormalise(notification);
            if (normalised is not null)
            {
                results.Add(normalised);
            }
        }

        return results;
    }

    private NormalisedEvent? TryNormalise(XElement notification)
    {
        var topic = notification.Descendants()
            .FirstOrDefault(e => e.Name.LocalName == "Topic")?.Value?.Trim() ?? string.Empty;

        var message = notification.Descendants()
            .FirstOrDefault(e => e.Name.LocalName == "Message" && e.HasAttributes);

        var source = OnvifEventMapper.ReadItems(
            message?.Descendants().FirstOrDefault(e => e.Name.LocalName == "Source"));
        var data = OnvifEventMapper.ReadItems(
            message?.Descendants().FirstOrDefault(e => e.Name.LocalName == "Data"));

        // Property events fire on both edges under one topic. Publishing the "cleared" edge as
        // though it were the "raised" edge would double event volume and invert every duration.
        if (!OnvifEventMapper.IsActiveState(data))
        {
            return null;
        }

        var eventType = OnvifEventMapper.MapTopic(topic);

        var utcTime = message?.Attribute("UtcTime")?.Value;
        var timestamp = OnvifSoapClient.TryParseTimestamp(utcTime, out var parsed)
            ? parsed
            : DateTimeOffset.UtcNow;

        // The device's own clock is authoritative for when the event happened, but a device
        // with a badly wrong clock would otherwise poison every time-window correlation.
        // The measured offset is removed so timestamps land on a common timeline.
        timestamp -= _soap.ClockOffset;

        var cameraId = source.TryGetValue("VideoSourceConfigurationToken", out var configToken)
            ? configToken
            : source.TryGetValue("VideoSource", out var videoSource) ? videoSource
            : source.TryGetValue("Source", out var genericSource) ? genericSource
            : Target.Code;

        return new NormalisedEvent
        {
            SourceVmsId = Target.Id,
            // ONVIF supplies no event id, so dedup falls back to the platform event id.
            // Duplicate suppression on this target relies on the device not re-delivering,
            // which PullPoint guarantees within a subscription.
            SourceEventId = null,
            CameraId = cameraId,
            OrganizationUnitId = Target.OrganizationUnitId,
            EventType = eventType,
            VendorEventType = topic.Length > 0 ? topic : "unknown",
            Timestamp = timestamp,
            Severity = OnvifEventMapper.MapSeverity(eventType),
            ObjectReference = OnvifEventMapper.ExtractObjectReference(data),
            Confidence = OnvifEventMapper.ExtractConfidence(data),
            DeliveryMode = DeliveryMode.Live,
        };
    }

    private async Task TryUnsubscribeAsync()
    {
        if (_subscription is null)
        {
            return;
        }

        try
        {
            var body = new XElement(Ns.WsNt + "Unsubscribe",
                new XAttribute(XNamespace.Xmlns + "wsnt", Ns.WsNt.NamespaceName));

            // Not passing the caller's token: this runs during teardown, when it is cancelled.
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await _soap.InvokeAsync(_subscription, body, authenticate: true, UnsubscribeAction,
                timeout.Token).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is AdapterException or OperationCanceledException)
        {
            // A device that will not release cleanly still expires at its termination time.
            LogUnsubscribeFailed(_logger, Target.Id, ex.Message);
        }
        finally
        {
            _subscription = null;
        }
    }

    // ---- Device queries ----------------------------------------------------

    private async Task<DateTimeOffset?> ReadSystemDateAndTimeAsync(CancellationToken cancellationToken)
    {
        var body = new XElement(Ns.Device + "GetSystemDateAndTime",
            new XAttribute(XNamespace.Xmlns + "tds", Ns.Device.NamespaceName));

        var response = await _soap
            .InvokeAsync(_deviceService, body, authenticate: false, action: null, cancellationToken)
            .ConfigureAwait(false);

        var utc = response.Descendants().FirstOrDefault(e => e.Name.LocalName == "UTCDateTime");
        if (utc is null)
        {
            return null;
        }

        var date = utc.Descendants().FirstOrDefault(e => e.Name.LocalName == "Date");
        var time = utc.Descendants().FirstOrDefault(e => e.Name.LocalName == "Time");
        if (date is null || time is null)
        {
            return null;
        }

        if (!TryReadInt(date, "Year", out var year) || !TryReadInt(date, "Month", out var month)
            || !TryReadInt(date, "Day", out var day) || !TryReadInt(time, "Hour", out var hour)
            || !TryReadInt(time, "Minute", out var minute)
            || !TryReadInt(time, "Second", out var second))
        {
            return null;
        }

        try
        {
            return new DateTimeOffset(year, month, day, hour, minute, second, TimeSpan.Zero);
        }
        catch (ArgumentOutOfRangeException)
        {
            // An unset device clock can report an impossible date. Better to fall back to host
            // time than to fail the whole connect.
            return null;
        }

        static bool TryReadInt(XElement parent, string name, out int value)
        {
            var element = parent.Descendants().FirstOrDefault(e => e.Name.LocalName == name);
            return int.TryParse(element?.Value, out value);
        }
    }

    private async Task DiscoverServicesAsync(CancellationToken cancellationToken)
    {
        // GetServices is the modern call; GetCapabilities is the pre-2.0 one. Older devices in
        // a government estate are common, so both are attempted before giving up.
        try
        {
            var body = new XElement(Ns.Device + "GetServices",
                new XAttribute(XNamespace.Xmlns + "tds", Ns.Device.NamespaceName),
                new XElement(Ns.Device + "IncludeCapability", "false"));

            var response = await _soap.InvokeAsync(_deviceService, body, authenticate: true, action: null,
                cancellationToken).ConfigureAwait(false);

            foreach (var service in response.Descendants().Where(e => e.Name.LocalName == "Service"))
            {
                var ns = service.Descendants().FirstOrDefault(e => e.Name.LocalName == "Namespace")?.Value;
                var address = service.Descendants().FirstOrDefault(e => e.Name.LocalName == "XAddr")?.Value;

                if (ns is null || address is null)
                {
                    continue;
                }

                // XAddr suffers the same NAT problem as subscription addresses.
                var uri = RewriteSubscriptionAddress(address, Target.Endpoint);

                if (ns.Contains("/media/wsdl", StringComparison.OrdinalIgnoreCase))
                {
                    _mediaService ??= uri;
                }
                else if (ns.Contains("/events/wsdl", StringComparison.OrdinalIgnoreCase))
                {
                    _events ??= uri;
                }
                else if (ns.Contains("/device/wsdl", StringComparison.OrdinalIgnoreCase))
                {
                    _deviceService = uri;
                }
            }
        }
        catch (AdapterException ex) when (ex is CapabilityException or TransientVmsException)
        {
            LogServiceDiscoveryFallback(_logger, Target.Id, ex.Message);
        }

        // Devices that answered neither call still expose the conventional paths.
        var root = new Uri(NormaliseEndpoint(Target.Endpoint));
        _mediaService ??= new Uri(root, "/onvif/media_service");
        _events ??= new Uri(root, "/onvif/event_service");
    }

    private async Task<DeviceInformation> GetDeviceInformationAsync(CancellationToken cancellationToken)
    {
        try
        {
            var body = new XElement(Ns.Device + "GetDeviceInformation",
                new XAttribute(XNamespace.Xmlns + "tds", Ns.Device.NamespaceName));

            var response = await _soap.InvokeAsync(_deviceService, body, authenticate: true, action: null,
                cancellationToken).ConfigureAwait(false);

            return new DeviceInformation(
                Read(response, "Manufacturer"),
                Read(response, "Model"),
                Read(response, "FirmwareVersion"));
        }
        catch (CapabilityException)
        {
            // Informational only; its absence must not fail inventory.
            return new DeviceInformation(null, null, null);
        }

        static string? Read(XElement root, string name) =>
            root.Descendants().FirstOrDefault(e => e.Name.LocalName == name)?.Value;
    }

    /// <summary>
    /// The device's physical video sources, in declaration order.
    /// </summary>
    /// <remarks>
    /// One entry per sensor. A device that answers <c>GetProfiles</c> but not this — some very
    /// old Profile S firmware — yields an empty list, and the caller falls back to grouping by
    /// profile source token.
    /// </remarks>
    private async Task<IReadOnlyList<string>> GetVideoSourcesAsync(CancellationToken cancellationToken)
    {
        var media = _mediaService ?? throw new CapabilityException(
            "Device advertised no Media service.", nameof(Capability.Inventory), Target.Id);

        var body = new XElement(Ns.Media + "GetVideoSources",
            new XAttribute(XNamespace.Xmlns + "trt", Ns.Media.NamespaceName));

        XElement response;
        try
        {
            response = await _soap.InvokeAsync(
                media, body, authenticate: true, action: null, cancellationToken).ConfigureAwait(false);
        }
        catch (CapabilityException)
        {
            // Optional in practice: fall back to profile-derived grouping.
            return [];
        }

        var tokens = new List<string>();
        foreach (var element in response.Descendants().Where(e => e.Name.LocalName == "VideoSources"))
        {
            var token = element.Attribute("token")?.Value;
            if (!string.IsNullOrWhiteSpace(token) && !tokens.Contains(token, StringComparer.Ordinal))
            {
                tokens.Add(token);
            }
        }

        return tokens;
    }

    private async Task<IReadOnlyList<OnvifProfile>> GetProfilesAsync(CancellationToken cancellationToken)
    {
        var media = _mediaService ?? throw new CapabilityException(
            "Device advertised no Media service.", nameof(Capability.Inventory), Target.Id);

        var body = new XElement(Ns.Media + "GetProfiles",
            new XAttribute(XNamespace.Xmlns + "trt", Ns.Media.NamespaceName));

        var response = await _soap.InvokeAsync(media, body, authenticate: true, action: null, cancellationToken)
            .ConfigureAwait(false);

        var profiles = new List<OnvifProfile>();

        foreach (var element in response.Descendants().Where(e => e.Name.LocalName == "Profiles"))
        {
            var token = element.Attribute("token")?.Value;
            if (string.IsNullOrWhiteSpace(token))
            {
                continue;
            }

            var name = element.Descendants().FirstOrDefault(e => e.Name.LocalName == "Name")?.Value;

            var videoSourceConfig = element.Descendants()
                .FirstOrDefault(e => e.Name.LocalName == "VideoSourceConfiguration");
            var sourceToken = videoSourceConfig?.Descendants()
                .FirstOrDefault(e => e.Name.LocalName == "SourceToken")?.Value
                ?? videoSourceConfig?.Attribute("token")?.Value;

            var encoderConfig = element.Descendants()
                .FirstOrDefault(e => e.Name.LocalName == "VideoEncoderConfiguration");
            var encoding = encoderConfig?.Descendants()
                .FirstOrDefault(e => e.Name.LocalName == "Encoding")?.Value;
            var resolution = encoderConfig?.Descendants()
                .FirstOrDefault(e => e.Name.LocalName == "Resolution");

            // Absent or unparseable values are expected: many devices omit encoder details
            // entirely. They become null rather than zero so downstream code can distinguish
            // "device did not say" from "device said zero".
            var width = ReadInt(resolution, "Width");
            var height = ReadInt(resolution, "Height");
            var frameRate = ReadDouble(encoderConfig, "FrameRateLimit");

            var streamUri = await TryGetStreamUriAsync(media, token, cancellationToken)
                .ConfigureAwait(false);

            profiles.Add(new OnvifProfile(
                token, name, sourceToken, streamUri, encoding, width, height, frameRate));
        }

        return profiles;

        static int? ReadInt(XElement? parent, string name)
        {
            var raw = parent?.Descendants().FirstOrDefault(e => e.Name.LocalName == name)?.Value;
            return int.TryParse(raw, out var value) && value > 0 ? value : null;
        }

        static double? ReadDouble(XElement? parent, string name)
        {
            var raw = parent?.Descendants().FirstOrDefault(e => e.Name.LocalName == name)?.Value;
            return double.TryParse(raw, System.Globalization.CultureInfo.InvariantCulture, out var value)
                   && value > 0
                ? value
                : null;
        }
    }

    private async Task<string?> TryGetStreamUriAsync(
        Uri media, string profileToken, CancellationToken cancellationToken)
    {
        try
        {
            var body = new XElement(Ns.Media + "GetStreamUri",
                new XAttribute(XNamespace.Xmlns + "trt", Ns.Media.NamespaceName),
                new XElement(Ns.Media + "StreamSetup",
                    new XElement(Ns.Schema + "Stream",
                        new XAttribute(XNamespace.Xmlns + "tt", Ns.Schema.NamespaceName),
                        "RTP-Unicast"),
                    new XElement(Ns.Schema + "Transport",
                        new XElement(Ns.Schema + "Protocol", "RTSP"))),
                new XElement(Ns.Media + "ProfileToken", profileToken));

            var response = await _soap.InvokeAsync(media, body, authenticate: true, action: null, cancellationToken)
                .ConfigureAwait(false);

            return response.Descendants().FirstOrDefault(e => e.Name.LocalName == "Uri")?.Value;
        }
        catch (AdapterException)
        {
            // A profile without a resolvable stream URI is still a real camera; record it
            // without the reference rather than dropping it from inventory.
            return null;
        }
    }

    // ---- Helpers -----------------------------------------------------------

    private void RequireCapability(Capability capability)
    {
        if (!Capabilities.Has(capability))
        {
            throw new CapabilityException(
                $"ONVIF target {Target.Id} does not support {capability}.",
                capability.ToString(), Target.Id);
        }
    }

    private static string NormaliseEndpoint(string endpoint) =>
        endpoint.StartsWith("http", StringComparison.OrdinalIgnoreCase)
            ? endpoint
            : $"http://{endpoint}";

    internal static Uri BuildDeviceServiceUri(string endpoint)
    {
        var root = new Uri(NormaliseEndpoint(endpoint));
        return root.AbsolutePath is "/" or "" ? new Uri(root, "/onvif/device_service") : root;
    }

    public ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return ValueTask.CompletedTask;
        }

        _disposed = true;
        _subscription = null;

        // HttpClient instances are owned by TargetHttpClientProvider and shared across targets,
        // so they are deliberately not disposed here.
        return ValueTask.CompletedTask;
    }

    private sealed record OnvifProfile(
        string Token, string? Name, string? SourceToken, string? StreamUri,
        string? Encoding, int? Width, int? Height, double? FrameRate);

    private sealed record DeviceInformation(string? Manufacturer, string? Model, string? FirmwareVersion);

    [LoggerMessage(Level = LogLevel.Warning,
        Message = "ONVIF target {TargetId} clock differs from host by {OffsetSeconds:F0}s; "
                + "WS-Security timestamps are adjusted to device time")]
    private static partial void LogClockSkew(ILogger logger, Guid targetId, double offsetSeconds);

    [LoggerMessage(Level = LogLevel.Debug,
        Message = "ONVIF target {TargetId} service discovery fell back to conventional paths: {Reason}")]
    private static partial void LogServiceDiscoveryFallback(ILogger logger, Guid targetId, string reason);

    [LoggerMessage(Level = LogLevel.Debug,
        Message = "ONVIF target {TargetId} PullPoint unsubscribe failed: {Reason}. "
                + "The subscription will lapse at its termination time.")]
    private static partial void LogUnsubscribeFailed(ILogger logger, Guid targetId, string reason);
}
