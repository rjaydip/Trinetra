using System.Buffers;
using System.IO.Pipelines;
using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Text;
using Trinetra.Federation.Core.Errors;

namespace Trinetra.Federation.Adapters.Http;

/// <summary>One part of a <c>multipart/x-mixed-replace</c> event stream.</summary>
/// <param name="Headers">Part headers, notably <c>Content-Type</c>.</param>
/// <param name="Body">Raw part body — XML or JSON depending on vendor and firmware.</param>
internal readonly record struct MultipartEvent(
    IReadOnlyDictionary<string, string> Headers, string Body);

/// <summary>
/// Reads a never-ending <c>multipart/x-mixed-replace</c> HTTP response incrementally.
/// </summary>
/// <remarks>
/// <para>
/// Both Hikvision (<c>/ISAPI/Event/notification/alertStream</c>) and Dahua
/// (<c>/cgi-bin/eventManager.cgi?action=attach</c>) deliver events this way: one HTTP response
/// that never completes. Several things about that are hostile to the defaults.
/// </para>
/// <list type="number">
/// <item><b><see cref="HttpCompletionOption.ResponseHeadersRead"/> is mandatory.</b> The default
/// buffers the entire response before returning — on a response that never ends, the worker
/// allocates until it dies.</item>
/// <item><b><see cref="HttpClient.Timeout"/> does not apply here.</b> Once headers are read it
/// governs nothing, so a half-open TCP connection produces silence that is indistinguishable
/// from a quiet site. The idle watchdog below is the only thing that tells them apart, and it is
/// why <c>SubscribeEventsAsync</c> can honestly promise not to swallow disconnects.</item>
/// <item><b>ASP.NET Core's <c>MultipartReader</c> is the wrong tool</b> — it assumes
/// <c>multipart/form-data</c> semantics and a body that ends.</item>
/// </list>
/// </remarks>
internal static class MultipartEventStream
{
    /// <summary>Cap on one part. A malformed or hostile part must not grow without bound.</summary>
    private const int MaxPartBytes = 512 * 1024;

    /// <summary>
    /// Streams parts until cancelled or the connection drops.
    /// </summary>
    /// <param name="response">
    /// Must have been obtained with <see cref="HttpCompletionOption.ResponseHeadersRead"/>.
    /// Ownership stays with the caller, which must dispose it — an undisposed streaming response
    /// leaks a socket for the handler's lifetime, and across reconnect churn on 200 targets that
    /// is the most likely route to socket exhaustion.
    /// </param>
    /// <param name="idleTimeout">
    /// Maximum silence before the stream is declared dead. Sized from the vendor's keepalive
    /// cadence: Dahua states it explicitly via <c>heartbeat=</c>, Hikvision is inferred from its
    /// periodic notifications.
    /// </param>
    /// <param name="targetId">For attributing failures.</param>
    /// <param name="cancellationToken">The runtime's token; the sole deadline authority.</param>
    internal static async IAsyncEnumerable<MultipartEvent> ReadAsync(
        HttpResponseMessage response,
        TimeSpan idleTimeout,
        Guid targetId,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var boundary = ExtractBoundary(response.Content.Headers.ContentType)
            ?? throw new NormalisationException(
                "Event stream response declared no multipart boundary.", targetId);

        var delimiter = Encoding.ASCII.GetBytes($"--{boundary}");

        await using var stream = await response.Content
            .ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);

        var reader = PipeReader.Create(stream,
            new StreamPipeReaderOptions(bufferSize: 16 * 1024, leaveOpen: true));

        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                ReadResult result;
                try
                {
                    result = await ReadWithIdleTimeoutAsync(
                        reader, idleTimeout, targetId, cancellationToken).ConfigureAwait(false);
                }
                catch (IOException ex)
                {
                    throw new TransientVmsException(
                        $"Event stream for {targetId} was interrupted: {ex.Message}", targetId, ex);
                }

                var buffer = result.Buffer;
                var consumed = buffer.Start;
                var examined = buffer.End;
                var parts = new List<MultipartEvent>();

                while (TryReadPart(ref buffer, delimiter, targetId, out var part))
                {
                    parts.Add(part);
                    consumed = buffer.Start;
                    examined = consumed;
                }

                reader.AdvanceTo(consumed, examined);

                // Yielding outside the parse loop keeps the ref-struct buffer manipulation and
                // the async boundary separate, which the compiler requires.
                foreach (var part in parts)
                {
                    yield return part;
                }

                if (result.IsCompleted)
                {
                    // A stream that is supposed to be infinite completing means the device
                    // closed it. Transient: the runtime reconnects and gap-fills from the cursor.
                    throw new TransientVmsException(
                        $"Event stream for {targetId} closed by the device.", targetId);
                }
            }
        }
        finally
        {
            await reader.CompleteAsync().ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Reads, treating prolonged silence as a failure rather than as patience.
    /// </summary>
    /// <remarks>
    /// Without this, a device whose TCP connection has half-closed looks exactly like a site
    /// with nothing happening. The connector reports healthy indefinitely while delivering
    /// nothing — the failure mode that motivates <c>ConnectorHealth.CursorLag</c>.
    /// </remarks>
    private static async ValueTask<ReadResult> ReadWithIdleTimeoutAsync(
        PipeReader reader, TimeSpan idleTimeout, Guid targetId, CancellationToken cancellationToken)
    {
        using var idle = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        idle.CancelAfter(idleTimeout);

        try
        {
            return await reader.ReadAsync(idle.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TransientVmsException(
                $"Event stream for {targetId} produced nothing for {idleTimeout.TotalSeconds:F0}s; "
                + "treating the connection as dead rather than the site as quiet.", targetId);
        }
    }

    /// <summary>
    /// Extracts one complete part, if the buffer holds a whole one.
    /// </summary>
    /// <remarks>
    /// Parts are framed by <c>--boundary</c> markers. A part is complete only once the
    /// <i>next</i> boundary has arrived, so a partially received part stays buffered rather than
    /// being emitted truncated.
    /// </remarks>
    private static bool TryReadPart(
        ref ReadOnlySequence<byte> buffer, byte[] delimiter, Guid targetId, out MultipartEvent part)
    {
        part = default;

        var start = FindDelimiter(buffer, delimiter, buffer.Start);
        if (start is null)
        {
            DiscardIfOversized(ref buffer, targetId);
            return false;
        }

        var afterFirst = buffer.GetPosition(delimiter.Length, start.Value);
        var next = FindDelimiter(buffer, delimiter, afterFirst);
        if (next is null)
        {
            DiscardIfOversized(ref buffer, targetId);
            return false;
        }

        var partSequence = buffer.Slice(afterFirst, next.Value);
        buffer = buffer.Slice(next.Value);

        var raw = Encoding.UTF8.GetString(partSequence);
        part = ParsePart(raw);
        return true;
    }

    /// <summary>
    /// Drops a buffer that has grown past the cap without yielding a complete part.
    /// </summary>
    /// <remarks>
    /// Without this a device emitting a malformed stream — no boundaries, or a single enormous
    /// part — would grow the worker's memory until the process died, taking every other target
    /// on that worker with it.
    /// </remarks>
    private static void DiscardIfOversized(ref ReadOnlySequence<byte> buffer, Guid targetId)
    {
        if (buffer.Length > MaxPartBytes)
        {
            throw new NormalisationException(
                $"Event stream for {targetId} exceeded {MaxPartBytes} bytes without a complete "
                + "part; the stream framing is malformed.", targetId);
        }
    }

    private static SequencePosition? FindDelimiter(
        ReadOnlySequence<byte> buffer, byte[] delimiter, SequencePosition from)
    {
        var slice = buffer.Slice(from);
        var reader = new SequenceReader<byte>(slice);

        // Allocated once, outside the scan loop: a boundary scan over a busy stream can iterate
        // many thousands of times per read, and a per-iteration stackalloc would eventually
        // overflow the stack (CA2014).
        Span<byte> candidate = stackalloc byte[delimiter.Length];

        while (reader.TryAdvanceTo(delimiter[0], advancePastDelimiter: false))
        {
            if (reader.Remaining < delimiter.Length)
            {
                return null;
            }

            if (reader.TryCopyTo(candidate) && candidate.SequenceEqual(delimiter))
            {
                return reader.Position;
            }

            reader.Advance(1);
        }

        return null;
    }

    /// <summary>Splits a part into headers and body at the first blank line.</summary>
    private static MultipartEvent ParsePart(string raw)
    {
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        var separator = raw.IndexOf("\r\n\r\n", StringComparison.Ordinal);
        var separatorLength = 4;

        if (separator < 0)
        {
            // Some firmware uses bare LF rather than CRLF.
            separator = raw.IndexOf("\n\n", StringComparison.Ordinal);
            separatorLength = 2;
        }

        if (separator < 0)
        {
            return new MultipartEvent(headers, raw.Trim());
        }

        foreach (var line in raw[..separator].Split('\n', StringSplitOptions.TrimEntries
                                                          | StringSplitOptions.RemoveEmptyEntries))
        {
            var colon = line.IndexOf(':', StringComparison.Ordinal);
            if (colon > 0)
            {
                headers[line[..colon].Trim()] = line[(colon + 1)..].Trim();
            }
        }

        return new MultipartEvent(headers, raw[(separator + separatorLength)..].Trim());
    }

    private static string? ExtractBoundary(MediaTypeHeaderValue? contentType)
    {
        var boundary = contentType?.Parameters
            .FirstOrDefault(p => string.Equals(p.Name, "boundary", StringComparison.OrdinalIgnoreCase))
            ?.Value;

        return boundary?.Trim('"');
    }
}
