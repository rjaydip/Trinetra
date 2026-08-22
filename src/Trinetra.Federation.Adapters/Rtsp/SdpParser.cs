using System.Globalization;

namespace Trinetra.Federation.Adapters.Rtsp;

/// <summary>What the probe extracts from an SDP body.</summary>
internal readonly record struct SdpSummary(string? Codec, string? Resolution, int TrackCount);

/// <summary>
/// Reads the few SDP fields that describe a stream.
/// </summary>
/// <remarks>
/// Deliberately minimal. The SDP is read to confirm the stream exists and to enrich
/// <c>StreamProfile</c> with codec and resolution — not to prepare for playback. Anything beyond
/// that would be the first step toward decoding, which belongs to Model 2.
/// </remarks>
internal static class SdpParser
{
    internal static SdpSummary Parse(string sdp)
    {
        if (string.IsNullOrWhiteSpace(sdp))
        {
            return new SdpSummary(null, null, 0);
        }

        string? codec = null;
        string? resolution = null;
        var tracks = 0;

        foreach (var line in sdp.Split('\n', StringSplitOptions.TrimEntries
                                             | StringSplitOptions.RemoveEmptyEntries))
        {
            // "m=video 0 RTP/AVP 96" — one media section per track.
            if (line.StartsWith("m=", StringComparison.Ordinal))
            {
                tracks++;
                continue;
            }

            // "a=rtpmap:96 H264/90000"
            if (codec is null && line.StartsWith("a=rtpmap:", StringComparison.OrdinalIgnoreCase))
            {
                var space = line.IndexOf(' ', StringComparison.Ordinal);
                if (space > 0)
                {
                    var encoding = line[(space + 1)..];
                    var slash = encoding.IndexOf('/', StringComparison.Ordinal);
                    codec = slash > 0 ? encoding[..slash] : encoding;
                }

                continue;
            }

            // "a=x-dimensions:1920,1080" — non-standard but widely emitted by NVRs.
            if (resolution is null
                && line.StartsWith("a=x-dimensions:", StringComparison.OrdinalIgnoreCase))
            {
                var value = line["a=x-dimensions:".Length..].Trim();
                var parts = value.Split(',', StringSplitOptions.TrimEntries);

                if (parts.Length == 2
                    && int.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var w)
                    && int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var h))
                {
                    resolution = $"{w}x{h}";
                }
            }
        }

        return new SdpSummary(codec, resolution, tracks);
    }
}
