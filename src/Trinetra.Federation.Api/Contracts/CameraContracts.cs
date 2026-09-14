using System.Text.Json;
using System.Text.Json.Serialization;
using Trinetra.Federation.Runtime;

namespace Trinetra.Federation.Api.Contracts;

// Camera registry and GIS. Request and response shapes only; the domain record
// (Trinetra.Federation.Core.Model.Camera) is projected onto these deliberately so a new domain
// field is never exposed by accident, and so no enum-typed property reaches the serializer
// (the host registers no JsonStringEnumConverter — enums would go out as integers).

/// <summary>The full write body for <c>POST /cameras</c> and <c>PUT /cameras/{id}</c>.</summary>
public sealed record CameraWriteRequest(
    string CameraCode,
    string Name,
    Guid OrganizationUnitId,
    Guid GeographicAreaId,
    string CameraType,
    double Latitude,
    double Longitude,
    string? Manufacturer = null,
    string? Model = null,
    string? SerialNumber = null,
    double? Altitude = null,
    double? MountingHeight = null,
    double? Azimuth = null,
    double? Tilt = null,
    double? HorizontalFov = null,
    double? VerticalFov = null,
    double? EffectiveRange = null,
    string? IpAddress = null,
    int? Port = null,
    string? Protocol = null,
    Guid? VmsId = null,
    string? StreamReference = null,
    string? StreamPreference = null,
    string? NativeHlsUrl = null,
    string? NativeWebrtcUrl = null,
    string? CredentialReference = null,
    DateOnly? InstallationDate = null,
    bool RecordEvents = true,
    string? OperationalStatus = null,
    string? ConnectivityStatus = null,
    string? MaintenanceStatus = null);

/// <summary>A registered camera as the API returns it.</summary>
public sealed record CameraResponse(
    Guid Id,
    string CameraCode,
    string Name,
    Guid OrganizationUnitId,
    Guid GeographicAreaId,
    string CameraType,
    double Latitude,
    double Longitude,
    string? Manufacturer,
    string? Model,
    string? SerialNumber,
    double? Altitude,
    double? MountingHeight,
    double? Azimuth,
    double? Tilt,
    double? HorizontalFov,
    double? VerticalFov,
    double? EffectiveRange,
    string? IpAddress,
    int? Port,
    string? Protocol,
    Guid? VmsId,
    string? StreamReference,
    string StreamPreference,
    string? NativeHlsUrl,
    string? NativeWebrtcUrl,
    string? CredentialReference,
    DateOnly? InstallationDate,
    bool RecordEvents,
    string OperationalStatus,
    string ConnectivityStatus,
    string MaintenanceStatus,
    bool HasCoverage,
    DateTimeOffset? LastSeenAt,
    DateTimeOffset? LastHealthCheckAt,
    DateTimeOffset? RetiredAt);

/// <summary>One page of the registry. A null <see cref="NextCursor"/> is the end of the results.</summary>
public sealed record CameraPage(IReadOnlyList<CameraResponse> Items, string? NextCursor);

/// <summary>RFP Model 1 "ageing-infrastructure reporting" — how many in-scope cameras fall into
/// each installation-age band, plus the oldest ones by name, for prioritising replacement.
/// <c>Buckets</c> is always exactly 5 entries, in a fixed order, even when a band's count is
/// zero — a stable shape for a chart to render directly.</summary>
public sealed record AgeingInfrastructureResponse(
    int TotalCameras, IReadOnlyList<AgeingInfrastructureBucketResponse> Buckets,
    IReadOnlyList<AgeingCameraSummaryResponse> OldestCameras);

/// <summary><c>Bucket</c> is a stable machine key (<c>under_3</c>, <c>3_to_5</c>, <c>5_to_10</c>,
/// <c>10_plus</c>, <c>unknown</c>); <c>Label</c> is what to display.</summary>
public sealed record AgeingInfrastructureBucketResponse(string Bucket, string Label, int Count);

public sealed record AgeingCameraSummaryResponse(
    Guid Id, string CameraCode, string Name, DateOnly InstallationDate, int AgeYears,
    string MaintenanceStatus);

// ---- GeoJSON (RFC 7946) --------------------------------------------------
// Coordinates are polymorphic by geometry type, so they are typed as object: a Point carries a
// double[] ([lon, lat]); a Polygon carries a double[][][] (one ring of [lon, lat] pairs).
// System.Text.Json serializes both correctly with no converter.

/// <summary>A GeoJSON geometry — <c>Point</c> or <c>Polygon</c> here.</summary>
public sealed record GeoJsonGeometry(
    [property: JsonPropertyName("type")] string Type,
    [property: JsonPropertyName("coordinates")] object Coordinates);

/// <summary>A GeoJSON feature: one geometry plus a free-form properties bag.</summary>
/// <remarks>
/// <para>
/// <c>Geometry</c> is nullable per RFC 7946 §3.2 — a feature that has no location yet (e.g. a
/// camera with no coverage optics) carries <c>"geometry": null</c> and describes itself in
/// <c>Properties</c>.
/// </para>
/// <para>
/// <c>Properties</c> holds raw CLR values (<c>Guid</c>, <c>string</c>, <c>double?</c>, arrays,
/// …), not <see cref="JsonElement"/> (finding 10-M8) — the wire shape is identical either way,
/// since a boxed primitive and a <c>JsonElement</c> holding the same value serialize to the same
/// JSON, but building a <see cref="JsonElement"/> per value means serializing each one to a
/// buffer and parsing it back before the whole feature collection is serialized a second time —
/// a full round trip per property, per camera, on the map's own render hot path. Plain values
/// are serialized exactly once, when the response itself is written.
/// </para>
/// </remarks>
public sealed record GeoJsonFeature(
    [property: JsonPropertyName("type")] string Type,
    [property: JsonPropertyName("geometry")] GeoJsonGeometry? Geometry,
    [property: JsonPropertyName("properties")] IReadOnlyDictionary<string, object?> Properties);

/// <summary>A GeoJSON <c>FeatureCollection</c> — the shape the map source returns.</summary>
[System.Diagnostics.CodeAnalysis.SuppressMessage(
    "Naming", "CA1711:Identifiers should not have incorrect suffix",
    Justification = "FeatureCollection is the RFC 7946 object name; renaming it would obscure the contract.")]
public sealed record GeoJsonFeatureCollection(
    [property: JsonPropertyName("type")] string Type,
    [property: JsonPropertyName("features")] IReadOnlyList<GeoJsonFeature> Features);

/// <summary>The coverage aggregate: counts grouped along several dimensions, no geometry.</summary>
public sealed record CoverageSummaryResponse(
    IReadOnlyDictionary<string, IReadOnlyDictionary<string, long>> Buckets);

// ---- Health ------------------------------------------------------------

/// <summary>A camera's current health snapshot.</summary>
public sealed record CameraHealthResponse(
    Guid CameraId,
    string OperationalStatus,
    string ConnectivityStatus,
    string MaintenanceStatus,
    DateTimeOffset? LastSeenAt,
    DateTimeOffset? LastHealthCheckAt,
    string? FailureReason);

/// <summary>One recorded health check.</summary>
public sealed record CameraHealthCheckResponse(
    string OperationalStatus,
    string ConnectivityStatus,
    DateTimeOffset CheckedAt,
    int? LatencyMs,
    string? ErrorCode,
    string? FailureReason,
    string Source);

/// <summary>A window of health checks, newest first.</summary>
public sealed record CameraHealthHistoryResponse(
    Guid CameraId, DateTimeOffset From, DateTimeOffset To,
    IReadOnlyList<CameraHealthCheckResponse> Items);

/// <summary>A manual health override. <see cref="Reason"/> is required and recorded.</summary>
public sealed record HealthOverrideRequest(
    string Reason, string? OperationalStatus = null, string? ConnectivityStatus = null);

// ---- Maintenance ------------------------------------------------------

public sealed record MaintenanceRecordResponse(
    Guid Id,
    Guid CameraId,
    string MaintenanceType,
    string Status,
    string Description,
    string? FailureReason,
    DateTimeOffset ReportedAt,
    DateTimeOffset? StartedAt,
    DateTimeOffset? CompletedAt,
    DateTimeOffset? NextDueAt,
    string? PerformedBy);

public sealed record MaintenanceCreateRequest(
    string MaintenanceType,
    string Description,
    string? Status = null,
    string? FailureReason = null,
    DateTimeOffset? ReportedAt = null,
    DateTimeOffset? StartedAt = null,
    DateTimeOffset? NextDueAt = null,
    string? PerformedBy = null);

public sealed record MaintenanceUpdateRequest(
    string? Status = null,
    string? Description = null,
    string? FailureReason = null,
    DateTimeOffset? StartedAt = null,
    DateTimeOffset? CompletedAt = null,
    DateTimeOffset? NextDueAt = null,
    string? PerformedBy = null);

// ---- Bulk import ----------------------------------------------------

/// <summary>A bulk import batch. <see cref="Items"/> is 1..500 rows.</summary>
public sealed record BulkImportRequest(string Mode, IReadOnlyList<CameraWriteRequest> Items);

/// <summary>The outcome of one row in a bulk import.</summary>
public sealed record BulkRowResult(
    int Index, string CameraCode, string Status, Guid? CameraId, string? Error);

/// <summary>The report for a whole bulk import — always 200, even with per-row failures.</summary>
public sealed record BulkImportResult(
    int Created, int Updated, int Failed, IReadOnlyList<BulkRowResult> Rows);

// ---- Reconciliation ------------------------------------------------

/// <summary>A camera a VMS reports that the registry has never matched.</summary>
public sealed record UnreconciledCameraResponse(
    Guid TargetId,
    string NativeCameraId,
    string? Name,
    string? VendorModel,
    string? Firmware,
    Guid OrganizationUnitId,
    Guid? GeographicAreaId,
    double? Latitude,
    double? Longitude,
    DateTimeOffset? LastSeen,
    IReadOnlyList<string> StreamReferences);

public sealed record UnreconciledPage(
    IReadOnlyList<UnreconciledCameraResponse> Items, string? NextCursor);

public sealed record ReconcileRequest(
    Guid TargetId,
    string NativeCameraId,
    bool AdoptStreamReference = true,
    bool AdoptVmsId = true);

public sealed record ReconcileResponse(
    Guid CameraId, Guid TargetId, string NativeCameraId, Guid? VmsId);

// ---- Standalone reachability probe (pre-save) -----------------------

/// <summary>
/// What to test — host:port reachability only. No camera id (the camera may not exist yet as a
/// row) and no credential (this never authenticates).
/// </summary>
public sealed record CameraConnectionTestRequest(string Protocol, string IpAddress, int Port);

/// <summary>Accepted a reachability test; poll <see cref="StatusUrl"/> for the result.</summary>
public sealed record CameraConnectionTestAccepted(Guid TestId, string Status, string StatusUrl);

/// <summary>A camera reachability test as stored. <see cref="Result"/> is
/// <see cref="CameraReachabilityReport"/> (Trinetra.Federation.Runtime) — deliberately narrow: a
/// plain TCP connect either opened within the timeout or it did not, with no vendor smarts to
/// report capabilities, camera counts or authentication outcomes.</summary>
public sealed record CameraConnectionTestResult(
    Guid Id,
    string Protocol,
    string IpAddress,
    int Port,
    string Status,
    DateTimeOffset RequestedAt,
    DateTimeOffset? CompletedAt,
    string? FailureReason,
    CameraReachabilityReport? Result);

/// <summary>Create a registry camera from an unreconciled federated row and link it in one call.</summary>
public sealed record CreateFromFederatedRequest(
    Guid TargetId,
    string NativeCameraId,
    string CameraCode,
    string CameraType,
    string? Name = null,
    Guid? OrganizationUnitId = null,
    Guid? GeographicAreaId = null,
    double? Latitude = null,
    double? Longitude = null,
    double? Azimuth = null,
    double? HorizontalFov = null,
    double? EffectiveRange = null,
    bool AdoptStreamReference = true,
    bool AdoptVmsId = true);

// ---- Authenticated credential test (post-save) -----------------------

/// <summary>Accepted a credential test; poll <see cref="StatusUrl"/> for the result.</summary>
public sealed record CameraCredentialTestAccepted(Guid TestId, string Status, string StatusUrl);

/// <summary>An authenticated camera credential test as stored. <see cref="Result"/> is
/// <see cref="CameraCredentialTestReport"/> (Trinetra.Federation.Runtime) — an
/// <c>authOutcome</c> of <c>authenticated</c> or <c>credential_rejected</c> means a real
/// protocol-appropriate handshake ran; <c>not_verifiable</c> means only reachability was
/// checked, honestly reported as such rather than silently skipped.</summary>
public sealed record CameraCredentialTestResult(
    Guid Id,
    Guid CameraId,
    string Protocol,
    string IpAddress,
    int Port,
    string Status,
    DateTimeOffset RequestedAt,
    DateTimeOffset? CompletedAt,
    string? FailureReason,
    CameraCredentialTestReport? Result);

/// <summary>One entry in a camera's credential-test history.</summary>
public sealed record CameraCredentialTestSummary(
    Guid Id,
    string Status,
    DateTimeOffset RequestedAt,
    DateTimeOffset? CompletedAt,
    string? FailureReason);
