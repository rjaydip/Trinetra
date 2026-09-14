namespace Trinetra.Federation.Api.Auth;

/// <summary>
/// Where MediaMTX (the live-streaming media server, <c>docs/STREAMING-GATEWAY-PLAN.md</c>) is
/// reachable from this API process.
/// </summary>
/// <remarks>
/// The API proxies HLS bytes to MediaMTX itself rather than requiring a separate nginx
/// <c>auth_request</c> layer in front of it — one fewer process to deploy and operate. MediaMTX
/// still runs as its own systemd service (<c>deploy/systemd/trinetra-mediamtx.service</c>) and
/// still binds loopback-only; this API is simply the one thing allowed to reach it, the same role
/// nginx would otherwise have played.
/// </remarks>
public sealed class StreamingOptions
{
    public const string SectionName = "Streaming";
    public const string MediaMtxHttpClientName = "mediamtx";

    /// <summary>Named client for proxying a camera's own native HLS/WHEP URL directly (v1.25) —
    /// unlike <see cref="MediaMtxHttpClientName"/>, not scoped to any fixed host.</summary>
    public const string NativeStreamHttpClientName = "native-stream";

    /// <summary>MediaMTX's HLS listener (<c>hlsAddress</c> in <c>mediamtx.yml</c>).</summary>
    public string MediaMtxBaseUrl { get; set; } = "http://127.0.0.1:8888";
}
