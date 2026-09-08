using System.Buffers;

namespace Trinetra.Federation.Api.Endpoints;

/// <summary>
/// Validates and decodes the optional base64 snapshot on a detection ingest before it reaches
/// the evidence store.
/// </summary>
/// <remarks>
/// The blob is caller-supplied and lands on disk, so its <b>size</b> is checked from the encoded
/// string length — before a decode buffer is allocated — and its <b>encoding</b> is checked into
/// a fixed-size buffer. An oversized or malformed value is a 400, never a 500 or an unbounded
/// allocation and write (finding 15-H2). The request body as a whole is capped separately, at
/// the route, via <c>RequestSizeLimitAttribute</c>.
/// </remarks>
internal static class DetectionEvidence
{
    /// <summary>
    /// Snapshot ceiling when <c>Evidence:MaxSnapshotBytes</c> is not configured. A single JPEG
    /// frame from a camera is comfortably under this.
    /// </summary>
    public const int DefaultMaxSnapshotBytes = 4 * 1024 * 1024;

    /// <summary>
    /// Attempts to decode <paramref name="base64"/> into at most <paramref name="maxBytes"/>
    /// bytes. Returns <see langword="false"/> with a caller-safe <paramref name="error"/> when the
    /// value is empty, longer than the encoded form of <paramref name="maxBytes"/>, or not valid
    /// base64 (or would decode to more than the limit).
    /// </summary>
    public static bool TryDecode(string? base64, int maxBytes, out byte[] bytes, out string? error)
    {
        bytes = [];
        error = null;

        if (string.IsNullOrEmpty(base64))
        {
            error = "snapshotBase64 is empty.";
            return false;
        }

        // base64 is ~4/3 the decoded size plus padding. Reject on the STRING length first, so a
        // multi-MB value is turned away before Convert allocates anything for it.
        var maxEncodedLength = (4L * ((maxBytes + 2L) / 3L)) + 4L;
        if (base64.Length > maxEncodedLength)
        {
            error = $"snapshotBase64 is larger than the {maxBytes:N0}-byte evidence limit.";
            return false;
        }

        var buffer = ArrayPool<byte>.Shared.Rent(maxBytes);
        try
        {
            // The destination span is exactly the limit: a value that decodes to more than
            // maxBytes fails here just as a malformed one does — both are a 400.
            if (!Convert.TryFromBase64String(base64, buffer.AsSpan(0, maxBytes), out var written))
            {
                error = "snapshotBase64 is not valid base64, or decodes to more than the evidence limit.";
                return false;
            }

            bytes = buffer[..written];
            return true;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }
}
