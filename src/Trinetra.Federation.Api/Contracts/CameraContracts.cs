using System.Text.Json;
using System.Text.Json.Serialization;

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
    Guid SiteId,
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
    string? CredentialReference = null,
    DateOnly? InstallationDate = null,
    string? OperationalStatus = null,
    string? ConnectivityStatus = null,
    string? MaintenanceStatus = null);

/// <summary>A registered camera as the API returns it.</summary>
public sealed record CameraResponse(
    Guid Id,
    string CameraCode,
    string Name,
    Guid OrganizationUnitId,
    Guid SiteId,
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
    string? CredentialReference,
    DateOnly? InstallationDate,
    string OperationalStatus,
    string ConnectivityStatus,
    string MaintenanceStatus,
    bool HasCoverage,
    DateTimeOffset? LastSeenAt,
    DateTimeOffset? LastHealthCheckAt,
    DateTimeOffset? RetiredAt);

/// <summary>One page of the registry. A null <see cref="NextCursor"/> is the end of the results.</summary>
public sealed record CameraPage(IReadOnlyList<CameraResponse> Items, string? NextCursor);

// ---- GeoJSON (RFC 7946) --------------------------------------------------
// Coordinates are polymorphic by geometry type, so they are typed as object: a Point carries a
// double[] ([lon, lat]); a Polygon carries a double[][][] (one ring of [lon, lat] pairs).
// System.Text.Json serializes both correctly with no converter.

/// <summary>A GeoJSON geometry — <c>Point</c> or <c>Polygon</c> here.</summary>
public sealed record GeoJsonGeometry(
    [property: JsonPropertyName("type")] string Type,
    [property: JsonPropertyName("coordinates")] object Coordinates);

/// <summary>A GeoJSON feature: one geometry plus a free-form properties bag.</summary>
public sealed record GeoJsonFeature(
    [property: JsonPropertyName("type")] string Type,
    [property: JsonPropertyName("geometry")] GeoJsonGeometry Geometry,
    [property: JsonPropertyName("properties")] IReadOnlyDictionary<string, JsonElement> Properties);

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
    Guid? SiteId,
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

/// <summary>Create a registry camera from an unreconciled federated row and link it in one call.</summary>
public sealed record CreateFromFederatedRequest(
    Guid TargetId,
    string NativeCameraId,
    string CameraCode,
    string CameraType,
    string? Name = null,
    Guid? OrganizationUnitId = null,
    Guid? SiteId = null,
    double? Latitude = null,
    double? Longitude = null,
    double? Azimuth = null,
    double? HorizontalFov = null,
    double? EffectiveRange = null,
    bool AdoptStreamReference = true,
    bool AdoptVmsId = true);
