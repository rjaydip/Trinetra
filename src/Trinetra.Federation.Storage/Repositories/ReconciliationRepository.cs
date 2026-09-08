using Dapper;
using Npgsql;

namespace Trinetra.Federation.Storage.Repositories;

/// <summary>A VMS-reported camera that the registry has never matched.</summary>
public sealed record UnreconciledCameraRow
{
    public Guid TargetId { get; init; }
    public string NativeCameraId { get; init; } = "";
    public string? Name { get; init; }
    public string? VendorModel { get; init; }
    public string? Firmware { get; init; }
    public Guid OrganizationUnitId { get; init; }
    public Guid? GeographicAreaId { get; init; }
    public double? Latitude { get; init; }
    public double? Longitude { get; init; }
    public DateTimeOffset? LastSeen { get; init; }
    public string[] StreamReferences { get; init; } = [];
}

/// <summary>How a reconcile attempt resolved.</summary>
public enum ReconcileStatus
{
    /// <summary>The registry camera does not exist or is out of scope.</summary>
    CameraNotFound,

    /// <summary>No such <c>(targetId, nativeCameraId)</c> row, or it is out of scope.</summary>
    FederatedNotFound,

    /// <summary>That VMS camera is already linked to a different registry record.</summary>
    Conflict,

    /// <summary>Linked (or already linked to this same camera — idempotent).</summary>
    Linked,
}

/// <summary>The result of a reconcile attempt.</summary>
public sealed record ReconcileResult(
    ReconcileStatus Status, Guid CameraId, Guid TargetId, string NativeCameraId, Guid? VmsId);

/// <summary>
/// Links registry cameras to the cameras a VMS reports (<c>federated_camera</c>).
/// </summary>
/// <remarks>
/// The link is <c>federated_camera.camera_id</c>. Reconciliation is gated on
/// <c>camera.reconcile</c> and scoped through both the registry camera and the federated row.
/// </remarks>
[System.Diagnostics.CodeAnalysis.SuppressMessage(
    "Performance", "CA1822:Mark members as static",
    Justification = "Write methods take their connection from the UnitOfWork by design.",
    Scope = "type")]
public sealed class ReconciliationRepository
{
    private readonly NpgsqlDataSource _dataSource;

    public ReconciliationRepository(NpgsqlDataSource dataSource) => _dataSource = dataSource;

    /// <summary>The reconciliation backlog, keyset-ordered by <c>(target_id, native_camera_id)</c>.</summary>
    public async Task<IReadOnlyList<UnreconciledCameraRow>> UnreconciledAsync(
        Guid? targetId, Guid? cursorTarget, string? cursorNative, int limit,
        CallerContext caller, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(caller);
        caller.Require("camera.reconcile");

        await using var c = await _dataSource.OpenConnectionAsync(ct);

        var unscopedOrg = caller.IsUnscopedFor("camera.reconcile");
        var unscopedGeo = caller.IsUnscopedForGeography("camera.reconcile");

        var rows = await c.QueryAsync<UnreconciledCameraRow>(new CommandDefinition($"""
            SELECT fc.target_id, fc.native_camera_id, fc.name, fc.vendor_model, fc.firmware,
                   fc.organization_unit_id, fc.geographic_area_id, fc.latitude, fc.longitude,
                   fc.last_seen, fc.stream_references
            FROM federation.federated_camera fc
            WHERE fc.camera_id IS NULL
              AND (@TargetId::uuid IS NULL OR fc.target_id = @TargetId)
              AND (@UnscopedOrg OR fc.organization_unit_id IN (
                   SELECT organization_unit_id FROM federation.authorized_org_units(
                       p_user_id => @UserId, p_api_key_id => @ApiKeyId, p_permission => 'camera.reconcile')))
              AND (@UnscopedGeo OR fc.geographic_area_id IS NULL OR
                   fc.geographic_area_id IN (
                   SELECT geographic_area_id FROM federation.authorized_geographic_areas(
                       p_user_id => @UserId, p_api_key_id => @ApiKeyId, p_permission => 'camera.reconcile')))
              AND (@CursorTarget::uuid IS NULL
                   OR (fc.target_id, fc.native_camera_id) > (@CursorTarget, @CursorNative))
            ORDER BY fc.target_id, fc.native_camera_id
            LIMIT @Limit;
            """, new
        {
            TargetId = targetId,
            caller.UserId, caller.ApiKeyId,
            UnscopedOrg = unscopedOrg, UnscopedGeo = unscopedGeo,
            CursorTarget = cursorTarget, CursorNative = cursorNative,
            Limit = limit,
        }, cancellationToken: ct));

        return [.. rows];
    }

    /// <summary>One unreconciled federated row the caller can reach, or null.</summary>
    public async Task<UnreconciledCameraRow?> GetUnreconciledAsync(
        Guid targetId, string nativeCameraId, CallerContext caller, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(caller);
        ArgumentException.ThrowIfNullOrWhiteSpace(nativeCameraId);
        caller.Require("camera.reconcile");

        await using var c = await _dataSource.OpenConnectionAsync(ct);

        return await c.QuerySingleOrDefaultAsync<UnreconciledCameraRow>(new CommandDefinition("""
            SELECT fc.target_id, fc.native_camera_id, fc.name, fc.vendor_model, fc.firmware,
                   fc.organization_unit_id, fc.geographic_area_id, fc.latitude, fc.longitude,
                   fc.last_seen, fc.stream_references
            FROM federation.federated_camera fc
            WHERE fc.target_id = @targetId AND fc.native_camera_id = @nativeCameraId
              AND fc.camera_id IS NULL
              AND (@UnscopedOrg OR fc.organization_unit_id IN (
                   SELECT organization_unit_id FROM federation.authorized_org_units(
                       p_user_id => @UserId, p_api_key_id => @ApiKeyId, p_permission => 'camera.reconcile')))
              AND (@UnscopedGeo OR fc.geographic_area_id IS NULL OR
                   fc.geographic_area_id IN (
                   SELECT geographic_area_id FROM federation.authorized_geographic_areas(
                       p_user_id => @UserId, p_api_key_id => @ApiKeyId, p_permission => 'camera.reconcile')));
            """, new
        {
            targetId, nativeCameraId,
            caller.UserId, caller.ApiKeyId,
            UnscopedOrg = caller.IsUnscopedFor("camera.reconcile"),
            UnscopedGeo = caller.IsUnscopedForGeography("camera.reconcile"),
        }, cancellationToken: ct));
    }

    /// <summary>Links a registry camera to a VMS-reported camera, adopting VMS fields if asked.</summary>
    public async Task<ReconcileResult> ReconcileAsync(
        Guid cameraId, Guid targetId, string nativeCameraId,
        bool adoptStreamReference, bool adoptVmsId,
        CallerContext caller, UnitOfWork work, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(caller);
        ArgumentNullException.ThrowIfNull(work);
        ArgumentException.ThrowIfNullOrWhiteSpace(nativeCameraId);
        caller.Require("camera.reconcile");

        var c = work.Connection;
        var tx = work.Transaction;

        var cameraReachable = await c.ExecuteScalarAsync<bool>(new CommandDefinition($"""
            SELECT EXISTS (SELECT 1 FROM federation.cameras c
                           WHERE c.id = @id AND c.deleted_at IS NULL
                             AND ({CameraScope.PredicateForC}));
            """, CameraScope.Args(cameraId, caller, "camera.reconcile"), tx, cancellationToken: ct));

        if (!cameraReachable)
        {
            return new ReconcileResult(ReconcileStatus.CameraNotFound, cameraId, targetId, nativeCameraId, null);
        }

        var fed = await c.QuerySingleOrDefaultAsync<FederatedLinkRow>(new CommandDefinition($"""
            SELECT fc.camera_id, fc.organization_unit_id, fc.geographic_area_id,
                   fc.stream_references, fc.target_id
            FROM federation.federated_camera fc
            WHERE fc.target_id = @targetId AND fc.native_camera_id = @nativeCameraId
              AND (@UnscopedOrg OR fc.organization_unit_id IN (
                   SELECT organization_unit_id FROM federation.authorized_org_units(
                       p_user_id => @UserId, p_api_key_id => @ApiKeyId, p_permission => 'camera.reconcile')))
              AND (@UnscopedGeo OR fc.geographic_area_id IS NULL OR
                   fc.geographic_area_id IN (
                   SELECT geographic_area_id FROM federation.authorized_geographic_areas(
                       p_user_id => @UserId, p_api_key_id => @ApiKeyId, p_permission => 'camera.reconcile')));
            """, new
        {
            targetId, nativeCameraId,
            caller.UserId, caller.ApiKeyId,
            UnscopedOrg = caller.IsUnscopedFor("camera.reconcile"),
            UnscopedGeo = caller.IsUnscopedForGeography("camera.reconcile"),
        }, tx, cancellationToken: ct));

        if (fed is null)
        {
            return new ReconcileResult(ReconcileStatus.FederatedNotFound, cameraId, targetId, nativeCameraId, null);
        }

        if (fed.CameraId is { } existing && existing != cameraId)
        {
            return new ReconcileResult(ReconcileStatus.Conflict, cameraId, targetId, nativeCameraId, null);
        }

        if (fed.CameraId != cameraId)
        {
            await c.ExecuteAsync(new CommandDefinition("""
                UPDATE federation.federated_camera
                SET camera_id = @cameraId, updated_at = now()
                WHERE target_id = @targetId AND native_camera_id = @nativeCameraId;
                """, new { cameraId, targetId, nativeCameraId }, tx, cancellationToken: ct));
        }

        var firstStream = fed.StreamReferences is { Length: > 0 } ? fed.StreamReferences[0] : null;

        var vmsId = await c.ExecuteScalarAsync<Guid?>(new CommandDefinition("""
            UPDATE federation.cameras
            SET vms_id = CASE WHEN @AdoptVms AND vms_id IS NULL THEN @targetId ELSE vms_id END,
                stream_reference = CASE WHEN @AdoptStream AND stream_reference IS NULL AND @FirstStream IS NOT NULL
                                        THEN @FirstStream ELSE stream_reference END,
                updated_by = @ActorId, updated_at = now()
            WHERE id = @cameraId
            RETURNING vms_id;
            """, new
        {
            cameraId, targetId, AdoptVms = adoptVmsId, AdoptStream = adoptStreamReference,
            FirstStream = firstStream, ActorId = caller.UserId,
        }, tx, cancellationToken: ct));

        return new ReconcileResult(ReconcileStatus.Linked, cameraId, targetId, nativeCameraId, vmsId);
    }

    private sealed record FederatedLinkRow
    {
        public Guid? CameraId { get; init; }
        public Guid OrganizationUnitId { get; init; }
        public Guid? GeographicAreaId { get; init; }
        public string[] StreamReferences { get; init; } = [];
        public Guid TargetId { get; init; }
    }
}
