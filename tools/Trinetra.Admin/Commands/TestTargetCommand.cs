using System.Diagnostics;
using Dapper;
using Microsoft.Extensions.Logging;
using Npgsql;
using Trinetra.Federation.Adapters;
using Trinetra.Federation.Adapters.Http;
using Trinetra.Federation.Adapters.Rtsp;
using Trinetra.Federation.Core.Abstractions;
using Trinetra.Federation.Core.Capabilities;
using Trinetra.Federation.Core.Errors;
using Trinetra.Federation.Core.Model;

namespace Trinetra.Admin.Commands;

/// <summary>
/// Connects to a configured target and reports what it finds. Read-only.
/// </summary>
/// <remarks>
/// <para>
/// The command that turns "the configuration looks right" into "the device answered". It runs
/// the real adapters against real hardware without needing a worker fleet, a lease, or Kafka —
/// so a field engineer can prove a site works the moment it is cabled.
/// </para>
/// <para>
/// <b>It writes nothing.</b> No inventory, no events, no health rows. An operator must be able
/// to run it against a production target without wondering what it changed.
/// </para>
/// </remarks>
internal static class TestTargetCommand
{
    public static async Task<int> RunAsync(
        Args args,
        NpgsqlDataSource dataSource,
        IAdapterFactory adapters,
        ILoggerFactory loggerFactory,
        CancellationToken cancellationToken)
    {
        var code = args.Require("code");
        var target = await LoadTargetAsync(dataSource, code, cancellationToken);

        Out.Heading($"Testing {target.Code} — {target.DisplayName}");
        Out.Info($"  Vendor:    {target.Vendor}");
        Out.Info($"  Endpoint:  {target.Endpoint}");
        Out.Info($"  TLS:       {(target.VerifyTls ? "verified" : "NOT verified (per-target opt-out)")}");

        var problems = 0;

        // --- Credential -----------------------------------------------------
        Out.Heading("Credential");
        IVmsAdapter adapter;
        try
        {
            var started = Stopwatch.StartNew();
            adapter = await adapters.CreateAsync(target, cancellationToken);
            Out.Ok($"Resolved '{target.CredentialReference}' ({started.ElapsedMilliseconds} ms)");
        }
        catch (Exception ex)
        {
            Out.Fail($"Could not resolve '{target.CredentialReference}': {ex.Message}");
            Out.Info($"  Provision it with: trinetra-admin secret set --ref {target.CredentialReference} "
                   + "--username <user>");
            return 1;
        }

        await using (adapter)
        {
            // --- Connect ----------------------------------------------------
            Out.Heading("Connection");
            try
            {
                var started = Stopwatch.StartNew();
                await adapter.ConnectAsync(cancellationToken);
                Out.Ok($"Connected in {started.ElapsedMilliseconds} ms");
            }
            catch (AuthException ex)
            {
                Out.Fail($"Device rejected the credentials: {ex.Message}");
                Out.Info("  The stored username/password is wrong, or the account is locked.");
                return 1;
            }
            catch (AdapterException ex)
            {
                Out.Fail($"Could not connect: {ex.Message}");
                Out.Info("  Check the endpoint, the port, and that this host can route to the device.");
                return 1;
            }

            // --- Capabilities -----------------------------------------------
            Out.Heading("Capabilities");
            CapabilitySet capabilities;
            try
            {
                capabilities = await adapter.ProbeCapabilitiesAsync(cancellationToken);
            }
            catch (AdapterException ex)
            {
                Out.Fail($"Capability probe failed: {ex.Message}");
                return 1;
            }

            foreach (var capability in Enum.GetValues<Capability>().Where(c => c != Capability.None))
            {
                if (capabilities.Has(capability))
                {
                    Out.Ok(capability.ToString());
                }
            }

            foreach (var (name, note) in capabilities.Notes)
            {
                Out.Warn($"{name}: {note}");
            }

            if (capabilities.EventDelivery == EventDelivery.None)
            {
                Out.Warn("This device delivers no events. Inventory and status only.");
                problems++;
            }

            // --- Clock ------------------------------------------------------
            if (capabilities.Has(Capability.TimeSyncCheck))
            {
                Out.Heading("Clock");
                try
                {
                    var deviceTime = await adapter.GetVmsTimeAsync(cancellationToken);
                    var skew = deviceTime - DateTimeOffset.UtcNow;

                    if (Math.Abs(skew.TotalMinutes) < 1)
                    {
                        Out.Ok($"Device clock agrees with this host ({deviceTime:u})");
                    }
                    else if (Math.Abs(skew.TotalMinutes) < 5)
                    {
                        Out.Warn($"Device clock is off by {skew.TotalMinutes:F1} minutes ({deviceTime:u})");
                        problems++;
                    }
                    else
                    {
                        // Beyond ~5 minutes ONVIF authentication itself starts failing, and every
                        // time-window correlation involving this device becomes unreliable.
                        Out.Fail($"Device clock is off by {skew.TotalMinutes:F1} minutes ({deviceTime:u})");
                        Out.Info("  Set NTP on the device. Beyond a few minutes this breaks event");
                        Out.Info("  correlation, and on ONVIF it breaks authentication outright.");
                        problems++;
                    }
                }
                catch (AdapterException ex)
                {
                    Out.Warn($"Could not read the device clock: {ex.Message}");
                    problems++;
                }
            }

            // --- Inventory --------------------------------------------------
            Out.Heading("Cameras");
            IReadOnlyList<FederatedCamera> cameras = [];

            if (capabilities.Has(Capability.Inventory))
            {
                try
                {
                    var started = Stopwatch.StartNew();
                    cameras = await adapter.GetCamerasAsync(cancellationToken);
                    Out.Ok($"{cameras.Count} camera(s) in {started.ElapsedMilliseconds} ms");

                    if (target.ExpectedCameraCount is { } expected && cameras.Count != expected)
                    {
                        Out.Warn($"Expected {expected}. A device silently returning a subset is a "
                               + "common failure that otherwise looks like a healthy sync.");
                        problems++;
                    }

                    foreach (var camera in cameras.Take(10))
                    {
                        var streams = camera.StreamReferences.Count;
                        Out.Info($"    {camera.NativeCameraId,-8} {camera.Name,-28} "
                               + $"{camera.Health,-12} {streams} stream(s)");
                    }

                    if (cameras.Count > 10)
                    {
                        Out.Info($"    … and {cameras.Count - 10} more");
                    }
                }
                catch (AdapterException ex)
                {
                    Out.Fail($"Inventory failed: {ex.Message}");
                    problems++;
                }
            }
            else
            {
                Out.Warn("Device reports no inventory capability.");
                problems++;
            }

            // --- Stream reachability ----------------------------------------
            if (!args.Has("skip-stream") && cameras.Count > 0)
            {
                Out.Heading("Stream check (first camera)");
                await ProbeFirstStreamAsync(target, cameras, dataSource, cancellationToken);
            }

            // --- Events -----------------------------------------------------
            if (capabilities.EventDelivery == EventDelivery.Subscribe && args.Has("watch"))
            {
                var seconds = int.TryParse(args.Get("watch"), out var parsed) ? parsed : 30;
                await WatchEventsAsync(adapter, seconds, cancellationToken);
            }
            else if (capabilities.EventDelivery == EventDelivery.Subscribe)
            {
                Out.Info("");
                Out.Info($"  Add --watch 30 to listen for live events for 30 seconds.");
            }
        }

        Out.Heading("Result");
        if (problems == 0)
        {
            Out.Ok("Target is ready to federate.");
            return 0;
        }

        Out.Warn($"Target works, with {problems} thing(s) worth attention above.");
        return 0;
    }

    private static async Task ProbeFirstStreamAsync(
        ConnectorTarget target,
        IReadOnlyList<FederatedCamera> cameras,
        NpgsqlDataSource dataSource,
        CancellationToken cancellationToken)
    {
        var camera = cameras.FirstOrDefault(c => c.StreamReferences.Count > 0);
        if (camera is null)
        {
            Out.Warn("No camera reported a stream reference.");
            return;
        }

        var uri = camera.StreamReferences[0];

        // Only one camera is probed, deliberately. DESCRIBE is per-camera work and many NVRs cap
        // concurrent RTSP sessions at 4-8 — burning them here would deny them to real operators.
        try
        {
            var credential = await ResolveForStreamAsync(target, dataSource, cancellationToken);
            var result = await RtspProbe.ProbeAsync(
                uri, credential, target.Id, TimeSpan.FromSeconds(10), cancellationToken);

            if (result.Reachable)
            {
                Out.Ok($"{uri}");
                Out.Info($"    codec {result.Codec ?? "?"}, {result.Resolution ?? "resolution unknown"}, "
                       + $"{result.TrackCount} track(s), {result.Latency.TotalMilliseconds:F0} ms");
            }
            else
            {
                Out.Warn($"{uri}");
                Out.Info($"    {result.Failure}");
                Out.Info("    Hikvision and Dahua stream URLs are built from a template rather than");
                Out.Info("    discovered, so this usually means a wrong channel number or separate");
                Out.Info("    stream credentials — not a dead camera.");
            }
        }
        catch (AuthException)
        {
            Out.Warn($"{uri}");
            Out.Info("    Stream credentials were rejected. Many devices use a different account");
            Out.Info("    for RTSP than for the management API.");
        }
    }

    private static async Task<Credential> ResolveForStreamAsync(
        ConnectorTarget target, NpgsqlDataSource dataSource, CancellationToken cancellationToken)
    {
        // The probe needs the same credential the adapter used; resolving it again here keeps
        // the adapter's copy encapsulated rather than exposing it on the interface.
        var resolver = AdminServices.CreateCredentialResolver(dataSource);
        return await resolver.ResolveAsync(target.CredentialReference, cancellationToken);
    }

    private static async Task WatchEventsAsync(
        IVmsAdapter adapter, int seconds, CancellationToken cancellationToken)
    {
        Out.Heading($"Listening for events ({seconds}s)");

        using var window = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        window.CancelAfter(TimeSpan.FromSeconds(seconds));

        var count = 0;

        try
        {
            await foreach (var e in adapter.SubscribeEventsAsync(window.Token))
            {
                count++;
                Out.Info($"    {e.Timestamp:HH:mm:ss}  {e.EventType,-20} camera {e.CameraId,-8} "
                       + $"{e.VendorEventType}");

                if (count >= 50)
                {
                    Out.Info("    … stopping after 50 events");
                    break;
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Window elapsed; expected.
        }
        catch (AdapterException ex)
        {
            Out.Warn($"Event stream ended: {ex.Message}");
        }

        if (count == 0)
        {
            Out.Warn("No events arrived. The site may simply be quiet — try walking in front of a camera.");
        }
        else
        {
            Out.Ok($"{count} event(s) received.");
        }
    }

    private static async Task<ConnectorTarget> LoadTargetAsync(
        NpgsqlDataSource dataSource, string code, CancellationToken ct)
    {
        await using var connection = await dataSource.OpenConnectionAsync(ct);

        // Looked up by the human code, because that is what an operator has in front of them.
        // Everything downstream then works from the immutable id.
        var row = await connection.QuerySingleOrDefaultAsync<TargetRow>(new CommandDefinition("""
            SELECT id, code, organization_unit_id, site_id, display_name,
                   vendor::text AS vendor, runtime_class::text AS runtime_class,
                   endpoint, credential_reference, verify_tls, state::text AS state,
                   rate_limit_per_second, rate_limit_burst, max_concurrent_requests,
                   expected_camera_count
            FROM federation.connector_target
            WHERE code = @Code;
            """, new { Code = code }, cancellationToken: ct));

        if (row is null)
        {
            throw new CommandFailedException(
                $"No target '{code}' is configured. List them with: trinetra-admin target list");
        }

        return new ConnectorTarget
        {
            Id = row.Id,
            Code = row.Code,
            OrganizationUnitId = row.OrganizationUnitId,
            SiteId = row.SiteId,
            DisplayName = row.DisplayName,
            Vendor = Enum.Parse<VendorKind>(row.Vendor),
            RuntimeClass = Enum.Parse<RuntimeClass>(row.RuntimeClass),
            Endpoint = row.Endpoint,
            CredentialReference = row.CredentialReference,
            VerifyTls = row.VerifyTls,
            State = Enum.Parse<TargetState>(row.State),
            RateLimitPerSecond = row.RateLimitPerSecond,
            RateLimitBurst = row.RateLimitBurst,
            MaxConcurrentRequests = row.MaxConcurrentRequests,
            ExpectedCameraCount = row.ExpectedCameraCount,
        };
    }

    private sealed class TargetRow
    {
        public Guid Id { get; init; }
        public string Code { get; init; } = "";
        public Guid OrganizationUnitId { get; init; }
        public Guid? SiteId { get; init; }
        public string DisplayName { get; init; } = "";
        public string Vendor { get; init; } = "";
        public string RuntimeClass { get; init; } = "";
        public string Endpoint { get; init; } = "";
        public string CredentialReference { get; init; } = "";
        public bool VerifyTls { get; init; }
        public string State { get; init; } = "";
        public double RateLimitPerSecond { get; init; }
        public int RateLimitBurst { get; init; }
        public int MaxConcurrentRequests { get; init; }
        public int? ExpectedCameraCount { get; init; }
    }
}
