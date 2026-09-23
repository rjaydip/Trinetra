using Dapper;
using Npgsql;

namespace Trinetra.Federation.Storage;

/// <summary>
/// Keeps a synthetic, single-camera <c>connector_target</c> in sync for every standalone (no VMS)
/// ONVIF-capable camera, so it is scheduled through the exact same lease/adapter/event pipeline a
/// real VMS-discovered camera already goes through — <c>LeaseManager</c>, <c>TargetWorker</c>,
/// <c>OnvifAdapter</c> — rather than a second, bespoke ONVIF client.
/// </summary>
/// <remarks>
/// <para>
/// Not a scoped repository — a system-internal writer with no caller, the same shape as
/// <see cref="SqlCorrelationEngine"/>, <c>EventStore</c> and <c>CameraHealthProbeStore</c>: it
/// runs on its own schedule inside <c>Federation.Worker</c>, never in response to an API request.
/// </para>
/// <para>
/// A synthetic target is identified by its <c>code</c>, <c>"standalone-camera:{cameraId}"</c> —
/// deterministic and unique per camera, so this store never needs a schema change (no new column,
/// no new table) to tell a synthetic target apart from a real VMS one: it is simply the target
/// whose code has this prefix. <c>credential_reference</c> is the camera's own — the same secret
/// already resolved for the health-check probe — so provisioning a camera's credential once
/// already covers both.
/// </para>
/// <para>
/// The one thing a real VMS onboarding does that this must not: nothing here decides which
/// <c>federated_camera</c> row belongs to which registry camera. A synthetic target has exactly
/// one camera behind it by construction, so once the existing inventory poll discovers it (via
/// <c>OnvifAdapter.GetCamerasAsync</c>, exactly like any VMS), <see cref="AutoReconcileAsync"/>
/// links it immediately — it would otherwise sit in the manual reconciliation queue alongside
/// real multi-camera VMS discoveries, which breaks "camera is always the primary entity" for
/// every standalone camera's events until an operator manually intervenes.
/// </para>
/// </remarks>
public sealed class SyntheticCameraTargetStore
{
    private const string CodePrefix = "standalone-camera:";

    private readonly NpgsqlDataSource _dataSource;

    public SyntheticCameraTargetStore(NpgsqlDataSource dataSource) => _dataSource = dataSource;

    /// <summary>
    /// Creates a synthetic target for every eligible standalone camera that does not already have
    /// one, and retires (sets <c>Disabled</c> — <c>target_state</c> has no separate "retired"
    /// value; the existing lifecycle already treats <c>Disabled</c> as "stop leasing/polling
    /// this") any synthetic target whose camera no longer qualifies — deleted, credential
    /// cleared, or protocol changed away from <c>ONVIF</c>.
    /// Idempotent: safe to call on every tick from every <c>Federation.Worker</c> instance, since
    /// the target <c>code</c> is deterministic and unique — a second instance's insert is just a
    /// harmless <c>ON CONFLICT DO NOTHING</c>.
    /// </summary>
    public async Task<(int Created, int Retired)> SyncTargetsAsync(CancellationToken ct)
    {
        await using var c = await _dataSource.OpenConnectionAsync(ct);

        // Eligible: a standalone camera (no federated_camera row references it yet, i.e. it was
        // never discovered through a real VMS either) speaking ONVIF, with a stored credential
        // and a resolvable address — the same three fields CameraHealthProbeStore already
        // requires to probe it.
        var created = await c.ExecuteAsync(new CommandDefinition("""
            INSERT INTO federation.connector_target
                (code, organization_unit_id, geographic_area_id, display_name, vendor,
                 endpoint, credential_reference, verify_tls, event_poll_seconds)
            SELECT 'standalone-camera:' || cam.id, cam.organization_unit_id, cam.geographic_area_id,
                   cam.name, 'Onvif'::federation.vendor_kind,
                   'http://' || host(cam.ip_address) || ':' || cam.port || '/',
                   cam.credential_reference, false, 15
            FROM federation.cameras cam
            WHERE cam.deleted_at IS NULL
              AND cam.protocol = 'ONVIF'
              AND cam.credential_reference IS NOT NULL
              AND cam.ip_address IS NOT NULL AND cam.port IS NOT NULL
              AND NOT EXISTS (
                  SELECT 1 FROM federation.federated_camera fc WHERE fc.camera_id = cam.id)
              AND NOT EXISTS (
                  SELECT 1 FROM federation.connector_target t
                  WHERE t.code = 'standalone-camera:' || cam.id)
            ON CONFLICT (code) DO NOTHING;
            """, cancellationToken: ct));

        var retired = await c.ExecuteAsync(new CommandDefinition($"""
            UPDATE federation.connector_target t
            SET state = 'Disabled', updated_at = now()
            WHERE t.code LIKE '{CodePrefix}%'
              AND t.state <> 'Disabled'
              AND NOT EXISTS (
                  SELECT 1 FROM federation.cameras cam
                  WHERE 'standalone-camera:' || cam.id = t.code
                    AND cam.deleted_at IS NULL
                    AND cam.protocol = 'ONVIF'
                    AND cam.credential_reference IS NOT NULL
                    AND cam.ip_address IS NOT NULL AND cam.port IS NOT NULL);
            """, cancellationToken: ct));

        return (created, retired);
    }

    /// <summary>
    /// Links every unreconciled <c>federated_camera</c> row discovered under a synthetic target
    /// straight back to the one camera it was created for — no ambiguity to resolve, unlike a
    /// real multi-camera VMS discovery, so this never touches the manual reconciliation queue.
    /// </summary>
    public async Task<int> AutoReconcileAsync(CancellationToken ct)
    {
        await using var c = await _dataSource.OpenConnectionAsync(ct);

        return await c.ExecuteAsync(new CommandDefinition($"""
            UPDATE federation.federated_camera fc
            SET camera_id = substring(t.code from length('{CodePrefix}') + 1)::uuid,
                updated_at = now()
            FROM federation.connector_target t
            WHERE fc.target_id = t.id
              AND t.code LIKE '{CodePrefix}%'
              AND fc.camera_id IS NULL;
            """, cancellationToken: ct));
    }
}
