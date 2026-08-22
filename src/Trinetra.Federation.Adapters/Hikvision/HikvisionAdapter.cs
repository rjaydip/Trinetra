using System.Globalization;
using System.Net;
using System.Runtime.CompilerServices;
using System.Xml.Linq;
using Microsoft.Extensions.Logging;
using Trinetra.Federation.Adapters.Http;
using Trinetra.Federation.Core.Abstractions;
using Trinetra.Federation.Core.Capabilities;
using Trinetra.Federation.Core.Errors;
using Trinetra.Federation.Core.Events;
using Trinetra.Federation.Core.Model;

namespace Trinetra.Federation.Adapters.Hikvision;

/// <summary>
/// Hikvision ISAPI adapter — HTTP/XML with digest authentication, no native SDK.
/// </summary>
/// <remarks>
/// <para>
/// ISAPI is chosen over HCNetSDK deliberately. The documented gaps between them are recording
/// control, playback-by-filename and download-by-time — all video-plane operations that Model 3
/// declares out of scope. For the capability surface this platform actually needs, the native
/// SDK adds nothing but a P/Invoke boundary, per-architecture binaries and a restrictive licence.
/// </para>
/// <para>
/// <b>This is the best-behaved target for the scale rule.</b>
/// <c>/ISAPI/ContentMgmt/InputProxy/channels/status</c> returns per-channel online state for the
/// whole NVR in a single call, which is exactly the bulk primitive the architecture's §1 demands
/// and which ONVIF cannot provide.
/// </para>
/// </remarks>
public sealed partial class HikvisionAdapter : IVmsAdapter
{
    private readonly HttpClient _http;
    private readonly ConnectorTarget _target;
    private readonly Credential _credential;
    private readonly ILogger<HikvisionAdapter> _logger;
    private readonly Uri _root;

    private CapabilitySet? _capabilities;
    private bool _hasInputProxy;

    public HikvisionAdapter(
        ConnectorTarget target,
        Credential credential,
        TargetHttpClientProvider clients,
        ILogger<HikvisionAdapter> logger)
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
    internal HikvisionAdapter(
        ConnectorTarget target,
        Credential credential,
        HttpClient http,
        ILogger<HikvisionAdapter> logger)
    {
        _target = target;
        _credential = credential;
        _logger = logger;
        _http = http;
        _root = new Uri(target.Endpoint.StartsWith("http", StringComparison.OrdinalIgnoreCase)
            ? target.Endpoint
            : $"http://{target.Endpoint}");
    }

    public VendorKind Vendor => VendorKind.HikvisionIsapi;

    public ConnectorTarget Target => _target;

    public CapabilitySet Capabilities => _capabilities
        ?? throw new InvalidOperationException(
            "ProbeCapabilitiesAsync must run before Capabilities is read.");

    public Task ConnectAsync(CancellationToken cancellationToken) =>
        // Digest is stateless, so there is no session to establish. ISAPI does offer
        // /ISAPI/Security/sessionLogin, but a session would need cleanup on lease handover and
        // buys nothing here.
        GetAsync("/ISAPI/System/deviceInfo", cancellationToken);

    public async Task<CapabilitySet> ProbeCapabilitiesAsync(
        CancellationToken cancellationToken)
    {
        var supported = Capability.CameraStatus | Capability.Streams | Capability.Snapshot
                        | Capability.TimeSyncCheck | Capability.EventsSubscribe;
        var notes = new Dictionary<string, string>(StringComparer.Ordinal);

        // An NVR proxies IP channels via InputProxy; a standalone camera does not have it.
        // Probed rather than assumed, because the same vendor ships both.
        try
        {
            await GetXmlAsync("/ISAPI/ContentMgmt/InputProxy/channels", cancellationToken)
                .ConfigureAwait(false);
            _hasInputProxy = true;
            supported |= Capability.Inventory;
            notes["Inventory"] = "NVR: channels enumerated via ContentMgmt/InputProxy.";
        }
        catch (CapabilityException)
        {
            // Only a 404/501 means "this firmware has no InputProxy" — i.e. a standalone camera
            // rather than an NVR. An AuthException must propagate: mistaking rejected
            // credentials for a device shape would silently mis-probe the whole target.
            _hasInputProxy = false;
            supported |= Capability.Inventory;
            notes["Inventory"] = "Standalone device: channels enumerated via System/Video/inputs.";
        }

        notes["EventsPull"] =
            "ISAPI exposes no reliable time-ranged event query on all firmware; events are live "
            + "via alertStream. Gap-fill after a handover is not guaranteed on this target.";

        _capabilities = new CapabilitySet
        {
            Supported = supported,
            AdapterVersion = "0.1.0",
            ProbedAt = DateTimeOffset.UtcNow,
            Notes = notes,
        };

        return _capabilities;
    }

    // ---- Inventory (one call for the whole device) -------------------------

    public async Task<IReadOnlyList<FederatedCamera>> GetCamerasAsync(
        CancellationToken cancellationToken)
    {
        var path = _hasInputProxy
            ? "/ISAPI/ContentMgmt/InputProxy/channels"
            : "/ISAPI/System/Video/inputs/channels";

        var document = await GetXmlAsync(path, cancellationToken).ConfigureAwait(false);

        var cameras = new List<FederatedCamera>();

        foreach (var channel in document.Descendants()
            .Where(e => e.Name.LocalName is "InputProxyChannel" or "VideoInputChannel"))
        {
            var id = Value(channel, "id");
            if (string.IsNullOrWhiteSpace(id))
            {
                continue;
            }

            var name = Value(channel, "name");
            var online = Value(channel, "online");

            cameras.Add(new FederatedCamera
            {
                TargetId = _target.Id,
                NativeCameraId = id,
                OrganizationUnitId = _target.OrganizationUnitId,
                Name = string.IsNullOrWhiteSpace(name) ? $"Channel {id}" : name,
                IsEnabled = !string.Equals(Value(channel, "enabled"), "false",
                    StringComparison.OrdinalIgnoreCase),
                Health = ParseHealth(online),
                LastSeen = DateTimeOffset.UtcNow,
                // Hikvision stream URIs follow a fixed template rather than being discovered:
                // channel 1 main stream is 101, substream 102. Whether they actually work is
                // what the RTSP probe exists to establish.
                StreamReferences = [BuildStreamUri(id, mainStream: true), BuildStreamUri(id, false)],
            });
        }

        return cameras;
    }

    /// <summary>
    /// Per-channel online state for the whole device in one call.
    /// </summary>
    /// <remarks>
    /// The endpoint that makes Hikvision cheap to poll at scale: 128 channels cost one request,
    /// not 128. Falls back to the inventory call on firmware that lacks it.
    /// </remarks>
    public async Task<IReadOnlyList<FederatedCamera>> GetCameraStatusAsync(
        CancellationToken cancellationToken)
    {
        if (!_hasInputProxy)
        {
            return await GetCamerasAsync(cancellationToken).ConfigureAwait(false);
        }

        XDocument document;
        try
        {
            document = await GetXmlAsync("/ISAPI/ContentMgmt/InputProxy/channels/status",
                cancellationToken).ConfigureAwait(false);
        }
        catch (CapabilityException)
        {
            return await GetCamerasAsync(cancellationToken).ConfigureAwait(false);
        }

        var statuses = new List<FederatedCamera>();

        foreach (var channel in document.Descendants()
            .Where(e => e.Name.LocalName == "InputProxyChannelStatus"))
        {
            var id = Value(channel, "id");
            if (string.IsNullOrWhiteSpace(id))
            {
                continue;
            }

            statuses.Add(new FederatedCamera
            {
                TargetId = _target.Id,
                NativeCameraId = id,
                OrganizationUnitId = _target.OrganizationUnitId,
                Health = ParseHealth(Value(channel, "online")),
                IsRecording = string.Equals(Value(channel, "recordingStatus"), "true",
                    StringComparison.OrdinalIgnoreCase),
                LastSeen = DateTimeOffset.UtcNow,
            });
        }

        return statuses;
    }

    public Task<IReadOnlyList<StreamProfile>> GetStreamsAsync(
        string nativeCameraId, CancellationToken cancellationToken)
    {
        IReadOnlyList<StreamProfile> profiles =
        [
            new StreamProfile
            {
                CameraId = nativeCameraId,
                ProfileName = "main",
                Uri = BuildStreamUri(nativeCameraId, mainStream: true),
                IsPrimary = true,
            },
            new StreamProfile
            {
                CameraId = nativeCameraId,
                ProfileName = "sub",
                Uri = BuildStreamUri(nativeCameraId, mainStream: false),
            },
        ];

        return Task.FromResult(profiles);
    }

    public Task<IReadOnlyList<RecordingSegment>> GetRecordingsAsync(
        string nativeCameraId, DateTimeOffset rangeStart, DateTimeOffset rangeEnd,
        CancellationToken cancellationToken) =>
        throw new CapabilityException(
            "ISAPI ContentMgmt/search is not implemented in this adapter version.",
            nameof(Capability.Recordings), _target.Id);

    public Task<IReadOnlyList<NormalisedEvent>> GetEventsAsync(
        DateTimeOffset since, DateTimeOffset? until, int limit,
        CancellationToken cancellationToken) =>
        throw new CapabilityException(
            "ISAPI historical event query is not implemented; use SubscribeEventsAsync.",
            nameof(Capability.EventsPull), _target.Id);

    public async Task<DateTimeOffset> GetVmsTimeAsync(CancellationToken cancellationToken)
    {
        var document = await GetXmlAsync("/ISAPI/System/time", cancellationToken).ConfigureAwait(false);
        var raw = document.Descendants().FirstOrDefault(e => e.Name.LocalName == "localTime")?.Value;

        if (DateTimeOffset.TryParse(raw, CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var parsed))
        {
            return parsed;
        }

        throw new NormalisationException(
            "Device returned no parseable system time.", _target.Id);
    }

    // ---- Events ------------------------------------------------------------

    /// <summary>
    /// Live events from the ISAPI alert stream.
    /// </summary>
    /// <remarks>
    /// Push (<c>/ISAPI/Event/notification/httpHosts</c>) is deliberately not used: it bakes a
    /// consumer address into device configuration, which breaks the moment a target moves to
    /// another worker — the same objection that rules out ONVIF push notification.
    /// </remarks>
    public async IAsyncEnumerable<NormalisedEvent> SubscribeEventsAsync(
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(
            HttpMethod.Get, new Uri(_root, "/ISAPI/Event/notification/alertStream"));
        request.Options.Set(DigestAuthHandler.CredentialOption, _credential);
        request.Options.Set(DigestAuthHandler.TargetIdOption, _target.Id);

        // Opened through a helper because this is an iterator method: C# forbids try/catch
        // around a yield return, so an unguarded SendAsync here would let HttpRequestException
        // and timeout-cancellation escape the adapter taxonomy entirely. The runtime only
        // reconnects on AdapterException — anything else tears down the whole target, so a
        // routine NVR reboot would stop inventory, status and health too, not just events.
        using var response = await OpenStreamAsync(
            request, "/ISAPI/Event/notification/alertStream", cancellationToken)
            .ConfigureAwait(false);

        // Hikvision emits periodic videoloss/keepalive notifications, so prolonged silence means
        // a dead connection rather than a quiet site.
        await foreach (var part in MultipartEventStream.ReadAsync(
            response, TimeSpan.FromSeconds(90), _target.Id, cancellationToken)
            .ConfigureAwait(false))
        {
            var normalised = TryNormalise(part);
            if (normalised is not null)
            {
                yield return normalised;
            }
        }
    }

    /// <summary>
    /// Opens a streaming response, mapping transport failures onto the adapter taxonomy.
    /// </summary>
    /// <remarks>
    /// Separate from <see cref="SubscribeEventsAsync"/> because an iterator method cannot wrap
    /// a <c>yield return</c> in a try/catch. Devices reboot routinely; that must reconnect the
    /// event stream, not fail the target.
    /// </remarks>
    private async Task<HttpResponseMessage> OpenStreamAsync(
        HttpRequestMessage request, string what, CancellationToken cancellationToken)
    {
        HttpResponseMessage response;
        try
        {
            response = await _http
                .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (HttpRequestException ex)
        {
            throw new TransientVmsException(
                $"Could not open {what}: {ex.Message}", _target.Id, ex);
        }
        catch (OperationCanceledException ex) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TransientVmsException(
                $"Opening {what} timed out.", _target.Id, ex);
        }

        try
        {
            EnsureSuccess(response, what);
        }
        catch
        {
            // The response owns a live socket; a status-code failure must not leak it.
            response.Dispose();
            throw;
        }

        return response;
    }

    private NormalisedEvent? TryNormalise(MultipartEvent part)
    {
        if (string.IsNullOrWhiteSpace(part.Body))
        {
            return null;
        }

        XDocument document;
        try
        {
            document = XDocument.Parse(part.Body);
        }
        catch (System.Xml.XmlException)
        {
            // One malformed notification must not stall the whole stream; the runtime
            // dead-letters it and carries on.
            LogUnparseableNotification(_logger, _target.Id);
            return null;
        }

        var root = document.Root;
        if (root is null)
        {
            return null;
        }

        var eventTypeRaw = Value(root, "eventType") ?? "unknown";
        var state = Value(root, "eventState");

        // Hikvision repeats an "active" notification every second or two while a condition
        // persists. Emitting each would multiply event volume by an order of magnitude, so only
        // the transition is published.
        if (string.Equals(state, "inactive", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        // No channel on the notification means a device-level event (storage failure, reboot)
        // rather than one about a specific camera. The target's human code identifies it; the
        // Guid would be meaningless in a camera_id column holding vendor channel identifiers.
        var channelId = Value(root, "channelID") ?? Value(root, "dynChannelID")
            ?? Value(root, "channelName") ?? _target.Code;

        var eventType = HikvisionEventMapper.Map(eventTypeRaw);

        var timestamp = DateTimeOffset.TryParse(Value(root, "dateTime"),
            CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var parsed)
            ? parsed
            : DateTimeOffset.UtcNow;

        return new NormalisedEvent
        {
            SourceVmsId = _target.Id,
            SourceEventId = Value(root, "eventId"),
            CameraId = channelId,
            OrganizationUnitId = _target.OrganizationUnitId,
            EventType = eventType,
            VendorEventType = eventTypeRaw,
            Timestamp = timestamp,
            Severity = HikvisionEventMapper.MapSeverity(eventType),
            ObjectReference = Value(root, "licensePlate"),
            DeliveryMode = DeliveryMode.Live,
        };
    }

    // ---- HTTP helpers ------------------------------------------------------

    private string BuildStreamUri(string channelId, bool mainStream)
    {
        // Hikvision channel encoding: channel 1 main = 101, sub = 102.
        var stream = int.TryParse(channelId, out var n)
            ? (n * 100) + (mainStream ? 1 : 2)
            : mainStream ? 101 : 102;

        return $"rtsp://{_root.Host}:554/Streaming/Channels/{stream}";
    }

    private async Task<XDocument> GetXmlAsync(string path, CancellationToken cancellationToken)
    {
        var body = await GetAsync(path, cancellationToken).ConfigureAwait(false);

        try
        {
            return XDocument.Parse(body);
        }
        catch (System.Xml.XmlException ex)
        {
            throw new NormalisationException(
                $"ISAPI response from {path} was not well-formed XML.", _target.Id, ex);
        }
    }

    private async Task<string> GetAsync(string path, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(_root, path));
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
                $"ISAPI request to {path} failed: {ex.Message}", _target.Id, ex);
        }
        catch (OperationCanceledException ex) when (!cancellationToken.IsCancellationRequested)
        {
            // A connect or response timeout arrives as a cancellation even though the caller
            // cancelled nothing. Left unmapped it escapes the adapter exception taxonomy
            // entirely: the circuit breaker only counts TransientVmsException, so it would
            // never open for an unreachable device — the single most common failure there is.
            throw new TransientVmsException(
                $"ISAPI request to {path} timed out.", _target.Id, ex);
        }

        using (response)
        {
            EnsureSuccess(response, path);
            return await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    private void EnsureSuccess(HttpResponseMessage response, string path)
    {
        if (response.IsSuccessStatusCode)
        {
            return;
        }

        throw response.StatusCode switch
        {
            HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden =>
                new AuthException($"ISAPI rejected credentials at {path}.", _target.Id),

            // 404 on an ISAPI path means the firmware lacks that feature — a permanent fact
            // about the device, not a failure worth retrying or opening the breaker for.
            HttpStatusCode.NotFound or HttpStatusCode.NotImplemented =>
                new CapabilityException(
                    $"ISAPI endpoint {path} is not available on this firmware.", path, _target.Id),

            HttpStatusCode.TooManyRequests =>
                new RateLimitedException("Device is rate limiting.",
                    response.Headers.RetryAfter?.Delta, _target.Id),

            _ => new TransientVmsException(
                $"ISAPI returned {(int)response.StatusCode} for {path}.", _target.Id),
        };
    }

    private static string? Value(XElement parent, string localName) =>
        parent.Descendants().FirstOrDefault(e => e.Name.LocalName == localName)?.Value?.Trim();

    private static HealthStatus ParseHealth(string? online) => online?.ToUpperInvariant() switch
    {
        "TRUE" or "ONLINE" => HealthStatus.Healthy,
        "FALSE" or "OFFLINE" => HealthStatus.Unreachable,
        _ => HealthStatus.Unknown,
    };

    public ValueTask DisposeAsync() =>
        // Nothing to release: the HttpClient is shared and owned by TargetHttpClientProvider,
        // and digest auth holds no server-side session.
        ValueTask.CompletedTask;

    [LoggerMessage(Level = LogLevel.Warning,
        Message = "Hikvision target {TargetId} sent an unparseable alertStream notification; skipped")]
    private static partial void LogUnparseableNotification(ILogger logger, Guid targetId);
}
