using System.Globalization;
using System.Net;
using System.Runtime.CompilerServices;
using Microsoft.Extensions.Logging;
using Trinetra.Federation.Adapters.Http;
using Trinetra.Federation.Core.Abstractions;
using Trinetra.Federation.Core.Capabilities;
using Trinetra.Federation.Core.Errors;
using Trinetra.Federation.Core.Events;
using Trinetra.Federation.Core.Model;

namespace Trinetra.Federation.Adapters.Dahua;

/// <summary>
/// Dahua CGI adapter. Also serves CP Plus and other Dahua OEM rebrands.
/// </summary>
/// <remarks>
/// <para>
/// CP Plus, Prama and several other Indian-market brands are Dahua hardware speaking the same
/// CGI surface, so they use this adapter rather than one of their own — hence
/// <see cref="VendorKind.DahuaCgi"/> being named for the protocol rather than the brand.
/// </para>
/// <para>
/// <b>OEM firmware diverges, so nothing is assumed.</b> Rebranded units routinely strip
/// endpoints the reference firmware has. Every capability here is probed against the actual
/// device; assuming Dahua's documented surface would leave a fleet of CP Plus NVRs reporting
/// capabilities they do not have.
/// </para>
/// <para>
/// One respect in which Dahua is better than Hikvision: its event stream accepts an explicit
/// <c>heartbeat</c> parameter, which gives the idle watchdog a defined threshold instead of an
/// inferred one.
/// </para>
/// </remarks>
public sealed partial class DahuaAdapter : IVmsAdapter
{
    private const int HeartbeatSeconds = 5;

    private readonly HttpClient _http;
    private readonly ConnectorTarget _target;
    private readonly Credential _credential;
    private readonly ILogger<DahuaAdapter> _logger;
    private readonly Uri _root;

    private CapabilitySet? _capabilities;
    private bool _hasRemoteDevice;

    public DahuaAdapter(
        ConnectorTarget target,
        Credential credential,
        TargetHttpClientProvider clients,
        ILogger<DahuaAdapter> logger)
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
    internal DahuaAdapter(
        ConnectorTarget target,
        Credential credential,
        HttpClient http,
        ILogger<DahuaAdapter> logger)
    {
        _target = target;
        _credential = credential;
        _logger = logger;
        _http = http;
        _root = new Uri(target.Endpoint.StartsWith("http", StringComparison.OrdinalIgnoreCase)
            ? target.Endpoint
            : $"http://{target.Endpoint}");
    }

    public VendorKind Vendor => VendorKind.DahuaCgi;

    public ConnectorTarget Target => _target;

    public CapabilitySet Capabilities => _capabilities
        ?? throw new InvalidOperationException(
            "ProbeCapabilitiesAsync must run before Capabilities is read.");

    public Task ConnectAsync(CancellationToken cancellationToken) =>
        GetAsync("/cgi-bin/magicBox.cgi?action=getSystemInfo", cancellationToken);

    public async Task<CapabilitySet> ProbeCapabilitiesAsync(
        CancellationToken cancellationToken)
    {
        var supported = Capability.Inventory | Capability.Streams | Capability.Snapshot
                        | Capability.TimeSyncCheck | Capability.EventsSubscribe;
        var notes = new Dictionary<string, string>(StringComparer.Ordinal);

        // RemoteDevice exists on NVRs that proxy IP channels, not on standalone cameras.
        try
        {
            await GetAsync("/cgi-bin/configManager.cgi?action=getConfig&name=RemoteDevice",
                cancellationToken).ConfigureAwait(false);
            _hasRemoteDevice = true;
            notes["Inventory"] = "NVR: channels enumerated via RemoteDevice.";
        }
        catch (CapabilityException)
        {
            _hasRemoteDevice = false;
            notes["Inventory"] = "Standalone device: single channel assumed.";
        }

        // Bulk video-loss status. Present on most firmware but stripped by some OEM builds, so
        // it is probed rather than assumed.
        try
        {
            await GetAsync("/cgi-bin/eventManager.cgi?action=getEventIndexes&code=VideoLoss",
                cancellationToken).ConfigureAwait(false);
            supported |= Capability.CameraStatus;
        }
        catch (CapabilityException)
        {
            notes["CameraStatus"] =
                "Firmware exposes no getEventIndexes; status falls back to configured enable flags.";
        }

        notes["EventsPull"] =
            "Dahua CGI offers event attach only, with no time-range query, so events missed "
            + "during a lease handover cannot be recovered on this target.";

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

    public async Task<IReadOnlyList<FederatedCamera>> GetCamerasAsync(
        CancellationToken cancellationToken)
    {
        if (!_hasRemoteDevice)
        {
            // A standalone camera is one channel; still returned as a collection so callers
            // never need to special-case device shape.
            return
            [
                new FederatedCamera
                {
                    TargetId = _target.Id,
                    NativeCameraId = "0",
                    OrganizationUnitId = _target.OrganizationUnitId,
                    Name = _target.DisplayName,
                    Health = HealthStatus.Healthy,
                    LastSeen = DateTimeOffset.UtcNow,
                    StreamReferences = [BuildStreamUri("0", true), BuildStreamUri("0", false)],
                },
            ];
        }

        var body = await GetAsync(
            "/cgi-bin/configManager.cgi?action=getConfig&name=RemoteDevice", cancellationToken)
            .ConfigureAwait(false);

        var devices = DahuaResponseParser.ParseIndexedTable(body, "table.RemoteDevice");

        // Channel titles come from a separate table; fetched once for the whole device rather
        // than per channel.
        var titles = await TryGetChannelTitlesAsync(cancellationToken).ConfigureAwait(false);

        var cameras = new List<FederatedCamera>(devices.Count);

        foreach (var (index, fields) in devices)
        {
            var channelId = index.ToString(CultureInfo.InvariantCulture);

            fields.TryGetValue("Name", out var name);
            if (string.IsNullOrWhiteSpace(name))
            {
                titles.TryGetValue(index, out var title);
                name = title?.GetValueOrDefault("Name");
            }

            var enabled = !string.Equals(fields.GetValueOrDefault("Enable"), "false",
                StringComparison.OrdinalIgnoreCase);

            cameras.Add(new FederatedCamera
            {
                TargetId = _target.Id,
                NativeCameraId = channelId,
                OrganizationUnitId = _target.OrganizationUnitId,
                Name = string.IsNullOrWhiteSpace(name) ? $"Channel {index + 1}" : name,
                VendorModel = fields.GetValueOrDefault("DeviceType"),
                IsEnabled = enabled,
                Health = enabled ? HealthStatus.Unknown : HealthStatus.Unreachable,
                LastSeen = DateTimeOffset.UtcNow,
                // Dahua stream URIs are templated, never discovered. Whether they actually
                // resolve is what the RTSP probe establishes.
                StreamReferences = [BuildStreamUri(channelId, true), BuildStreamUri(channelId, false)],
            });
        }

        return cameras;
    }

    /// <summary>
    /// Bulk status derived from the channels currently reporting video loss.
    /// </summary>
    /// <remarks>
    /// One call covers the whole device, keeping status polling proportional to devices rather
    /// than cameras. Where the firmware lacks <c>getEventIndexes</c>, this degrades to the
    /// configured enable flags — coarser, but honest about what the device will tell us.
    /// </remarks>
    public async Task<IReadOnlyList<FederatedCamera>> GetCameraStatusAsync(
        CancellationToken cancellationToken)
    {
        var cameras = await GetCamerasAsync(cancellationToken).ConfigureAwait(false);

        if (!Capabilities.Has(Capability.CameraStatus))
        {
            return cameras;
        }

        HashSet<string> lost;
        try
        {
            var body = await GetAsync(
                "/cgi-bin/eventManager.cgi?action=getEventIndexes&code=VideoLoss", cancellationToken)
                .ConfigureAwait(false);
            lost = DahuaResponseParser.ParseChannelIndexes(body);
        }
        catch (CapabilityException)
        {
            return cameras;
        }

        return [.. cameras.Select(camera => camera with
        {
            Health = lost.Contains(camera.NativeCameraId)
                ? HealthStatus.Unreachable
                : camera.IsEnabled ? HealthStatus.Healthy : HealthStatus.Unreachable,
        })];
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
            "Dahua mediaFileFind is not implemented in this adapter version. Note that it is a "
            + "stateful server-side handle which must be closed even on cancellation.",
            nameof(Capability.Recordings), _target.Id);

    public Task<IReadOnlyList<NormalisedEvent>> GetEventsAsync(
        DateTimeOffset since, DateTimeOffset? until, int limit,
        CancellationToken cancellationToken) =>
        throw new CapabilityException(
            "Dahua CGI defines no time-ranged event query; use SubscribeEventsAsync.",
            nameof(Capability.EventsPull), _target.Id);

    public async Task<DateTimeOffset> GetVmsTimeAsync(CancellationToken cancellationToken)
    {
        var body = await GetAsync("/cgi-bin/global.cgi?action=getCurrentTime", cancellationToken)
            .ConfigureAwait(false);

        var fields = DahuaResponseParser.ParseFlat(body);
        var raw = fields.GetValueOrDefault("result") ?? fields.Values.FirstOrDefault();

        if (DateTime.TryParse(raw, CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var parsed))
        {
            return new DateTimeOffset(parsed, TimeSpan.Zero);
        }

        throw new NormalisationException(
            $"Device returned no parseable time: '{raw}'.", _target.Id);
    }

    // ---- Events ------------------------------------------------------------

    public async IAsyncEnumerable<NormalisedEvent> SubscribeEventsAsync(
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var uri = new Uri(_root,
            $"/cgi-bin/eventManager.cgi?action=attach&codes=%5BAll%5D&heartbeat={HeartbeatSeconds}");

        using var request = new HttpRequestMessage(HttpMethod.Get, uri);
        request.Options.Set(DigestAuthHandler.CredentialOption, _credential);
        request.Options.Set(DigestAuthHandler.TargetIdOption, _target.Id);

        // Opened through a helper because this is an iterator method: C# forbids try/catch
        // around a yield return, so an unguarded SendAsync here would let HttpRequestException
        // and timeout-cancellation escape the adapter taxonomy entirely. The runtime only
        // reconnects on AdapterException — anything else tears down the whole target.
        using var response = await OpenStreamAsync(request, "eventManager attach", cancellationToken)
            .ConfigureAwait(false);

        // The device promised a keepalive every HeartbeatSeconds, so three missed intervals is
        // a dead connection rather than a quiet site. Unlike Hikvision, this threshold is
        // stated by the device rather than guessed.
        var idleTimeout = TimeSpan.FromSeconds(HeartbeatSeconds * 3);

        await foreach (var part in MultipartEventStream.ReadAsync(
            response, idleTimeout, _target.Id, cancellationToken).ConfigureAwait(false))
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
        var fields = DahuaResponseParser.ParseEventPayload(part.Body);

        if (!fields.TryGetValue("Code", out var code) || string.IsNullOrWhiteSpace(code))
        {
            return null;
        }

        // Heartbeats keep the connection alive but are not events.
        if (string.Equals(code, "Heartbeat", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        // Dahua reports Start and Stop for the same condition under one code. Publishing both
        // would double event volume and make every duration calculation wrong.
        var action = fields.GetValueOrDefault("action");
        if (string.Equals(action, "Stop", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var channel = fields.GetValueOrDefault("index") ?? "0";
        var eventType = DahuaEventMapper.Map(code);

        return new NormalisedEvent
        {
            SourceVmsId = _target.Id,
            // Dahua supplies no stable per-event id, so dedup falls back to the platform id.
            SourceEventId = null,
            CameraId = channel,
            OrganizationUnitId = _target.OrganizationUnitId,
            EventType = eventType,
            VendorEventType = code,
            Timestamp = DateTimeOffset.UtcNow,
            Severity = DahuaEventMapper.MapSeverity(eventType),
            ObjectReference = DahuaEventMapper.ExtractPlate(fields),
            DeliveryMode = DeliveryMode.Live,
        };
    }

    // ---- Helpers -----------------------------------------------------------

    private async Task<SortedDictionary<int, Dictionary<string, string>>> TryGetChannelTitlesAsync(
        CancellationToken cancellationToken)
    {
        try
        {
            var body = await GetAsync(
                "/cgi-bin/configManager.cgi?action=getConfig&name=ChannelTitle", cancellationToken)
                .ConfigureAwait(false);
            return DahuaResponseParser.ParseIndexedTable(body, "table.ChannelTitle");
        }
        catch (AdapterException)
        {
            // Cosmetic only. A missing title must never fail inventory.
            return [];
        }
    }

    private string BuildStreamUri(string channelId, bool mainStream)
    {
        // Dahua channels are 1-based in RTSP but 0-based in configuration.
        var channel = int.TryParse(channelId, out var n) ? n + 1 : 1;
        var subtype = mainStream ? 0 : 1;

        return $"rtsp://{_root.Host}:554/cam/realmonitor?channel={channel}&subtype={subtype}";
    }

    private async Task<string> GetAsync(string pathAndQuery, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(_root, pathAndQuery));
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
                $"Dahua request to {pathAndQuery} failed: {ex.Message}", _target.Id, ex);
        }
        catch (OperationCanceledException ex) when (!cancellationToken.IsCancellationRequested)
        {
            // A connect or response timeout arrives as a cancellation even though the caller
            // cancelled nothing. Left unmapped it escapes the adapter exception taxonomy
            // entirely: the circuit breaker only counts TransientVmsException, so it would
            // never open for an unreachable device — the single most common failure there is.
            throw new TransientVmsException(
                $"Dahua request to {pathAndQuery} timed out.", _target.Id, ex);
        }

        using (response)
        {
            EnsureSuccess(response, pathAndQuery);
            var body = await response.Content.ReadAsStringAsync(cancellationToken)
                .ConfigureAwait(false);

            // Dahua answers 200 with an "Error" body for unsupported operations on some
            // firmware, so the body is inspected rather than trusting the status code alone.
            if (body.StartsWith("Error", StringComparison.OrdinalIgnoreCase))
            {
                throw new CapabilityException(
                    $"Device rejected {pathAndQuery}: {body.Trim()}", pathAndQuery, _target.Id);
            }

            return body;
        }
    }

    private void EnsureSuccess(HttpResponseMessage response, string what)
    {
        if (response.IsSuccessStatusCode)
        {
            return;
        }

        throw response.StatusCode switch
        {
            HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden =>
                new AuthException($"Dahua rejected credentials at {what}.", _target.Id),

            HttpStatusCode.NotFound or HttpStatusCode.BadRequest or HttpStatusCode.NotImplemented =>
                new CapabilityException(
                    $"Dahua endpoint {what} is unavailable on this firmware.", what, _target.Id),

            HttpStatusCode.TooManyRequests =>
                new RateLimitedException("Device is rate limiting.",
                    response.Headers.RetryAfter?.Delta, _target.Id),

            _ => new TransientVmsException(
                $"Dahua returned {(int)response.StatusCode} for {what}.", _target.Id),
        };
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
