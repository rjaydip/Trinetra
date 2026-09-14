namespace Trinetra.Federation.Api.Contracts;

/// <summary>
/// A user's saved video-wall layout. <c>cameraIds</c> is the wall in grid order (left-to-right,
/// wrapping every <c>columnCount</c> tiles) — its length is the tile count; there are no empty
/// tile slots to represent.
/// </summary>
public sealed record VideoWallPreferenceResponse(int ColumnCount, IReadOnlyList<string> CameraIds);

/// <summary>The full write body for <c>PUT /video-wall/preferences</c>. Always a full replace.</summary>
public sealed record VideoWallPreferenceRequest(int ColumnCount, IReadOnlyList<string> CameraIds);
