using Dapper;
using Npgsql;

namespace Trinetra.Federation.Storage.Repositories;

/// <summary>One camera a worker currently holds a lease on, with its scope.</summary>
public sealed record CameraLeaseAssignment
{
    /// <summary>A registry camera's own UUID as text, or <c>vms:{targetId}:{nativeCameraId}</c>
    /// for a VMS-discovered-but-unreconciled camera — see <c>db/versions/v1.27.sql</c>.</summary>
    public string CameraRef { get; init; } = "";
    public string Source { get; init; } = "";
    public Guid OrganizationUnitId { get; init; }
    public Guid? GeographicAreaId { get; init; }
}

/// <summary>Playback details for one leased camera, resolved separately per
/// <see cref="CameraLeaseAssignment.Source"/> — see <see cref="CameraLeaseRepository.ResolveDetailsAsync"/>.</summary>
public sealed record CameraLeaseDetail
{
    public string CameraRef { get; init; } = "";
    public string Source { get; init; } = "";
    public string Name { get; init; } = "";
    public string? StreamReference { get; init; }
    public string StreamPreference { get; init; } = "RTSP";
    public string? NativeHlsUrl { get; init; }
    public string? NativeWebrtcUrl { get; init; }
    public string? CredentialReference { get; init; }
}

/// <summary>One worker's currently-leased camera set, for the admin worker-health view.</summary>
public sealed record CameraLeaseWorkerSummary
{
    public Guid ApiKeyId { get; init; }
    public string WorkerId { get; init; } = "";
    public string Hostname { get; init; } = "";
    public int LeasedCameraCount { get; init; }
    public IReadOnlyList<string> CameraRefs { get; init; } = [];
    /// <summary>Display name per entry of <see cref="CameraRefs"/>, same order — the camera's own
    /// name (registry <c>cameras.name</c> or VMS <c>federated_camera.name</c>) when it resolves,
    /// falling back to the raw ref itself otherwise (an unnamed camera, or a VMS ref whose own
    /// <c>native_camera_id</c> happens to contain a colon — cosmetic-only fallback, this is an
    /// admin display field, never used to re-identify the camera).</summary>
    public IReadOnlyList<string> CameraNames { get; init; } = [];
    public DateTimeOffset EarliestLeaseExpiresAt { get; init; }
}

/// <summary>
/// Dynamic camera&lt;-&gt;AI-worker lease assignment (v1.27) — the same "claim, renew, reclaim on
/// TTL expiry" shape <see cref="LeaseStore"/> already gives <c>connector_target</c>/VMS connector
/// workers, applied to AI workers and cameras. See that migration's own doc comment for the full
/// rationale, in particular why this is a separate table rather than lease columns on
/// <c>cameras</c> itself (a VMS-discovered-but-unreconciled camera has no registry row to put
/// them on).
/// </summary>
/// <remarks>
/// <para>
/// <b>Claim is two SQL statements, not one</b> (see <see cref="ClaimAsync"/>): first, upsert a
/// lease on every candidate camera this caller may reach that is either free (no row),
/// TTL-expired, or already this worker's own — an <c>INSERT ... ON CONFLICT ... WHERE</c>, the
/// same "claim only if free" idiom <see cref="LeaseStore"/> gets from <c>FOR UPDATE SKIP
/// LOCKED</c> row locking (a plain <c>FOR UPDATE SKIP LOCKED</c> doesn't fit as naturally here,
/// since half the candidate pool — cameras with no lease row yet — has nothing to lock). Second,
/// trim this worker's own holdings back down to <c>capacity</c> if that first pass claimed more
/// than it should keep, releasing the lowest-priority excess (registry over VMS, then the
/// most-recently-claimed first) so a worker whose capacity just decreased doesn't keep
/// over-claiming forever.
/// </para>
/// <para>
/// Each half of the candidate union is scoped by its <b>own</b> natural permission — registry
/// candidates by <c>camera.read</c>, VMS-discovered candidates by <c>vms.read</c> — rather than
/// one blanket check, so a caller's actual authorized org/geo sets for each read stay exactly
/// what they already are elsewhere in the API (CLAUDE.md invariant: "the database query must
/// return only authorized rows").
/// </para>
/// </remarks>
public sealed class CameraLeaseRepository
{
    private readonly NpgsqlDataSource _dataSource;

    public CameraLeaseRepository(NpgsqlDataSource dataSource) => _dataSource = dataSource;

    /// <summary>
    /// Claims/renews up to <paramref name="capacity"/> cameras for this worker — VMS-discovered
    /// candidates first, then registry candidates, matching the priority the worker itself asked
    /// for. Call this on every poll; it is both the initial claim and the renewal (a lease this
    /// worker already holds is refreshed, not re-claimed from scratch).
    /// </summary>
    public async Task<IReadOnlyList<CameraLeaseAssignment>> ClaimAsync(
        CallerContext caller, string workerId, string hostname, int capacity, TimeSpan leaseTtl,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(caller);
        caller.Require("worker.heartbeat");
        ArgumentOutOfRangeException.ThrowIfLessThan(capacity, 1);

        if (caller.ApiKeyId is not { } apiKeyId)
        {
            // Same posture as AiWorkerHealthRepository.UpsertHeartbeatAsync — the endpoint
            // rejects a non-key caller with 403 before this is reached.
            throw new InvalidOperationException(
                "A camera claim requires an API-key principal; caller has no ApiKeyId.");
        }

        var unscopedOrgCamera = caller.IsUnscopedFor("camera.read");
        var unscopedGeoCamera = caller.IsUnscopedForGeography("camera.read");
        var unscopedOrgVms = caller.IsUnscopedFor("vms.read");
        var unscopedGeoVms = caller.IsUnscopedForGeography("vms.read");

        await using var c = await _dataSource.OpenConnectionAsync(ct);

        var args = new
        {
            caller.UserId,
            ApiKeyId = apiKeyId,
            UnscopedOrgCamera = unscopedOrgCamera,
            UnscopedGeoCamera = unscopedGeoCamera,
            UnscopedOrgVms = unscopedOrgVms,
            UnscopedGeoVms = unscopedGeoVms,
            WorkerId = workerId,
            Hostname = hostname,
            LeaseTtl = leaseTtl,
            Capacity = capacity,
        };

        await c.ExecuteAsync(new CommandDefinition("""
            WITH vms_candidates AS (
                SELECT ('vms:' || fc.target_id::text || ':' || fc.native_camera_id) AS camera_ref,
                       'VMS' AS source, fc.organization_unit_id, fc.geographic_area_id
                FROM federation.federated_camera fc
                WHERE fc.camera_id IS NULL AND fc.is_enabled
                  AND (@UnscopedOrgVms OR fc.organization_unit_id IN (
                      SELECT organization_unit_id FROM federation.authorized_org_units(
                          p_user_id => @UserId, p_api_key_id => @ApiKeyId, p_permission => 'vms.read')))
                  AND (@UnscopedGeoVms OR fc.geographic_area_id IS NULL OR fc.geographic_area_id IN (
                      SELECT geographic_area_id FROM federation.authorized_geographic_areas(
                          p_user_id => @UserId, p_api_key_id => @ApiKeyId, p_permission => 'vms.read')))
            ),
            registry_candidates AS (
                SELECT cam.id::text AS camera_ref, 'REGISTRY' AS source,
                       cam.organization_unit_id, cam.geographic_area_id
                FROM federation.cameras cam
                WHERE cam.deleted_at IS NULL
                  AND (@UnscopedOrgCamera OR cam.organization_unit_id IN (
                      SELECT organization_unit_id FROM federation.authorized_org_units(
                          p_user_id => @UserId, p_api_key_id => @ApiKeyId, p_permission => 'camera.read')))
                  AND (@UnscopedGeoCamera OR cam.geographic_area_id IS NULL OR cam.geographic_area_id IN (
                      SELECT geographic_area_id FROM federation.authorized_geographic_areas(
                          p_user_id => @UserId, p_api_key_id => @ApiKeyId, p_permission => 'camera.read')))
            ),
            -- vms_candidates listed first: a worker asked to claim VMS-discovered cameras before
            -- standalone ones, and the second pass below (trimming to capacity) keeps that order.
            candidates AS (
                SELECT * FROM vms_candidates
                UNION ALL
                SELECT * FROM registry_candidates
            )
            INSERT INTO federation.camera_worker_lease
                (camera_ref, source, organization_unit_id, geographic_area_id,
                 worker_id, api_key_id, hostname, leased_at, lease_expires_at)
            SELECT cand.camera_ref, cand.source, cand.organization_unit_id, cand.geographic_area_id,
                   @WorkerId, @ApiKeyId, @Hostname, now(), now() + @LeaseTtl
            FROM candidates cand
            ON CONFLICT (camera_ref) DO UPDATE
            SET worker_id = EXCLUDED.worker_id,
                api_key_id = EXCLUDED.api_key_id,
                hostname = EXCLUDED.hostname,
                -- A genuine renewal (same worker) keeps its original leased_at — only a hand-off
                -- from a different/expired owner counts as freshly claimed.
                leased_at = CASE
                    WHEN camera_worker_lease.worker_id = EXCLUDED.worker_id
                     AND camera_worker_lease.api_key_id = EXCLUDED.api_key_id
                     AND camera_worker_lease.hostname = EXCLUDED.hostname
                    THEN camera_worker_lease.leased_at ELSE now() END,
                lease_expires_at = EXCLUDED.lease_expires_at
            -- The "claim only if free" guard: a fresh lease held by someone else is left alone —
            -- this row's own UPDATE becomes a no-op for that candidate rather than stealing it.
            WHERE camera_worker_lease.lease_expires_at < now()
               OR (camera_worker_lease.worker_id = EXCLUDED.worker_id
                   AND camera_worker_lease.api_key_id = EXCLUDED.api_key_id
                   AND camera_worker_lease.hostname = EXCLUDED.hostname);
            """, args, cancellationToken: ct));

        await c.ExecuteAsync(new CommandDefinition("""
            WITH ranked AS (
                SELECT camera_ref,
                       row_number() OVER (ORDER BY (source = 'VMS') DESC, leased_at ASC) AS rn
                FROM federation.camera_worker_lease
                WHERE worker_id = @WorkerId AND api_key_id = @ApiKeyId AND hostname = @Hostname
            )
            DELETE FROM federation.camera_worker_lease l
            USING ranked r
            WHERE l.camera_ref = r.camera_ref AND r.rn > @Capacity;
            """, args, cancellationToken: ct));

        var rows = await c.QueryAsync<CameraLeaseAssignment>(new CommandDefinition("""
            SELECT camera_ref AS CameraRef, source AS Source,
                   organization_unit_id AS OrganizationUnitId, geographic_area_id AS GeographicAreaId
            FROM federation.camera_worker_lease
            WHERE worker_id = @WorkerId AND api_key_id = @ApiKeyId AND hostname = @Hostname
            ORDER BY (source = 'VMS') DESC, leased_at;
            """, args, cancellationToken: ct));

        return [.. rows];
    }

    /// <summary>
    /// Playback details for a set of already-claimed assignments — a second, deliberately
    /// separate query from <see cref="ClaimAsync"/>: registry and VMS cameras live in unrelated
    /// tables with unrelated shapes (a registry camera's own <c>credential_reference</c> versus a
    /// VMS camera's owning <c>connector_target.credential_reference</c>), so resolving both in one
    /// query would need an awkward three-way outer join for no benefit — the assignment list is
    /// already known and small (bounded by one worker's own capacity).
    /// </summary>
    public async Task<IReadOnlyList<CameraLeaseDetail>> ResolveDetailsAsync(
        IReadOnlyCollection<CameraLeaseAssignment> assignments, CancellationToken ct)
    {
        if (assignments.Count == 0) return [];

        var registryIds = assignments
            .Where(a => a.Source == "REGISTRY")
            .Select(a => Guid.Parse(a.CameraRef))
            .ToArray();
        var vmsRefs = assignments
            .Where(a => a.Source == "VMS")
            .Select(a => ParseVmsRef(a.CameraRef))
            .ToArray();

        await using var c = await _dataSource.OpenConnectionAsync(ct);

        var details = new List<CameraLeaseDetail>();

        if (registryIds.Length > 0)
        {
            var registryRows = await c.QueryAsync<CameraLeaseDetail>(new CommandDefinition("""
                SELECT id::text AS CameraRef, 'REGISTRY' AS Source, name AS Name,
                       stream_reference AS StreamReference, stream_preference AS StreamPreference,
                       native_hls_url AS NativeHlsUrl, native_webrtc_url AS NativeWebrtcUrl,
                       credential_reference AS CredentialReference
                FROM federation.cameras
                WHERE id = ANY(@RegistryIds) AND deleted_at IS NULL;
                """, new { RegistryIds = registryIds }, cancellationToken: ct));
            details.AddRange(registryRows);
        }

        if (vmsRefs.Length > 0)
        {
            var targetIds = vmsRefs.Select(r => r.TargetId).Distinct().ToArray();
            var vmsRows = await c.QueryAsync<CameraLeaseDetail>(new CommandDefinition("""
                SELECT ('vms:' || fc.target_id::text || ':' || fc.native_camera_id) AS CameraRef,
                       'VMS' AS Source,
                       COALESCE(fc.name, fc.native_camera_id) AS Name,
                       -- A federated camera can report several stream references (main/sub
                       -- stream); the first is the worker's default, same convention the RTSP
                       -- capture path already assumes elsewhere.
                       fc.stream_references[1] AS StreamReference,
                       'RTSP' AS StreamPreference,
                       NULL::text AS NativeHlsUrl, NULL::text AS NativeWebrtcUrl,
                       t.credential_reference AS CredentialReference
                FROM federation.federated_camera fc
                JOIN federation.connector_target t ON t.id = fc.target_id
                WHERE fc.target_id = ANY(@TargetIds);
                """, new { TargetIds = targetIds }, cancellationToken: ct));
            // Multiple targets can be queried at once for efficiency, but only the cameras
            // actually assigned to this worker (not every camera on those targets) belong in the
            // result — filter back down to the exact (targetId, nativeCameraId) pairs claimed.
            var wanted = vmsRefs.ToHashSet();
            details.AddRange(vmsRows.Where(r => wanted.Contains(ParseVmsRef(r.CameraRef))));
        }

        return details;
    }

    /// <summary>Releases every lease this worker holds — call on clean shutdown so its cameras
    /// are picked up immediately instead of waiting out the TTL (same reasoning as
    /// <see cref="LeaseStore.ReleaseAllAsync"/>).</summary>
    public async Task<int> ReleaseAllAsync(
        CallerContext caller, string workerId, string hostname, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(caller);
        caller.Require("worker.heartbeat");
        if (caller.ApiKeyId is not { } apiKeyId)
        {
            throw new InvalidOperationException(
                "Releasing leases requires an API-key principal; caller has no ApiKeyId.");
        }

        await using var c = await _dataSource.OpenConnectionAsync(ct);
        return await c.ExecuteAsync(new CommandDefinition("""
            DELETE FROM federation.camera_worker_lease
            WHERE api_key_id = @ApiKeyId AND worker_id = @WorkerId AND hostname = @Hostname;
            """, new { ApiKeyId = apiKeyId, WorkerId = workerId, Hostname = hostname }, cancellationToken: ct));
    }

    /// <summary>Every worker currently holding at least one lease, for the admin worker-health
    /// view — "worker X is monitoring cameras [...]" alongside its heartbeat. Not scoped: same
    /// posture as <see cref="AiWorkerHealthRepository.ListAsync"/>, an AI worker isn't an org/geo
    /// entity.</summary>
    public async Task<IReadOnlyList<CameraLeaseWorkerSummary>> ListWorkerSummariesAsync(
        CallerContext caller, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(caller);
        caller.Require("worker.read");

        await using var c = await _dataSource.OpenConnectionAsync(ct);
        var rows = await c.QueryAsync<CameraLeaseWorkerSummary>(new CommandDefinition("""
            WITH lease_names AS (
                SELECT l.api_key_id, l.worker_id, l.hostname, l.camera_ref, l.lease_expires_at,
                       -- native_camera_id can itself contain ':' (ParseVmsRef in the .NET code
                       -- splits into exactly 3 parts for this reason); split_part's fixed 3rd
                       -- token can miss that rare case, which just falls through to the
                       -- camera_ref fallback below — acceptable for an admin display field.
                       COALESCE(cam.name, fc.name, l.camera_ref) AS camera_name
                FROM federation.camera_worker_lease l
                LEFT JOIN federation.cameras cam
                    ON l.source = 'REGISTRY' AND cam.id::text = l.camera_ref
                LEFT JOIN federation.federated_camera fc
                    ON l.source = 'VMS'
                   AND fc.target_id::text = split_part(l.camera_ref, ':', 2)
                   AND fc.native_camera_id = split_part(l.camera_ref, ':', 3)
            )
            SELECT api_key_id AS ApiKeyId, worker_id AS WorkerId, hostname AS Hostname,
                   count(*)::int AS LeasedCameraCount,
                   array_agg(camera_ref ORDER BY camera_ref) AS CameraRefs,
                   array_agg(camera_name ORDER BY camera_ref) AS CameraNames,
                   min(lease_expires_at) AS EarliestLeaseExpiresAt
            FROM lease_names
            GROUP BY api_key_id, worker_id, hostname
            ORDER BY api_key_id, worker_id, hostname;
            """, cancellationToken: ct));

        return [.. rows];
    }

    private static (Guid TargetId, string NativeCameraId) ParseVmsRef(string cameraRef)
    {
        // "vms:{targetId}:{nativeCameraId}" — native_camera_id itself may contain ':', so split
        // into exactly 3 parts and let the third capture everything remaining.
        var parts = cameraRef.Split(':', 3);
        return (Guid.Parse(parts[1]), parts[2]);
    }
}
