using System.Buffers;
using System.Globalization;
using System.Net.Http.Headers;
using System.Text;
using Trinetra.Federation.Adapters.Http;
using Trinetra.Federation.Core.Abstractions;
using Trinetra.Federation.Core.Errors;

namespace Trinetra.Federation.Adapters.Rtsp;

internal readonly record struct RtspResponse(int StatusCode, string Body);

/// <summary>
/// A minimal RTSP request/response exchange over an established stream.
/// </summary>
/// <remarks>
/// RTSP is a text protocol closely modelled on HTTP, and its Digest authentication is the same
/// algorithm with a different method and URI — so <see cref="DigestAuthHandler"/>'s
/// implementation is reused rather than duplicated. That shared utility is the main reason
/// hand-rolling the probe costs less than adopting a media library we would use a few percent of.
/// </remarks>
internal sealed class RtspSession
{
    private readonly Stream _stream;
    private readonly Uri _uri;
    private readonly Credential _credential;
    private int _sequence;
    private DigestChallenge? _challenge;

    internal RtspSession(Stream stream, Uri uri, Credential credential)
    {
        _stream = stream;
        _uri = uri;
        _credential = credential;
    }

    /// <summary>
    /// Sends one RTSP method, handling the 401 challenge leg.
    /// </summary>
    /// <remarks>
    /// As with HTTP digest, the re-send after a 401 is protocol handshake rather than a retry:
    /// the server must issue a nonce before any client can compute a response. A second 401 is
    /// an <see cref="AuthException"/> and is never retried.
    /// </remarks>
    internal async Task<RtspResponse> SendAsync(
        string method, int maxBodyBytes, CancellationToken cancellationToken)
    {
        var response = await ExchangeAsync(method, maxBodyBytes, cancellationToken)
            .ConfigureAwait(false);

        if (response.StatusCode != 401)
        {
            EnsureAcceptable(response, method);
            return response;
        }

        if (_challenge is null)
        {
            throw new AuthException(
                $"RTSP {method} was refused with no usable Digest challenge ({_uri.Host}).");
        }

        var authenticated = await ExchangeAsync(method, maxBodyBytes, cancellationToken)
            .ConfigureAwait(false);

        if (authenticated.StatusCode == 401)
        {
            throw new AuthException(
                $"RTSP credentials rejected for {_uri.Host}. Stream credentials often differ "
                + "from the VMS API credentials.");
        }

        EnsureAcceptable(authenticated, method);
        return authenticated;
    }

    private static void EnsureAcceptable(RtspResponse response, string method)
    {
        if (response.StatusCode is >= 200 and < 300)
        {
            return;
        }

        // 455/551 mean the server understood but will not do it — a permanent property of the
        // endpoint rather than a failure to retry.
        if (response.StatusCode is 455 or 551 or 501)
        {
            throw new CapabilityException(
                $"RTSP server does not support {method} (status {response.StatusCode}).",
                method);
        }

        throw new TransientVmsException(
            $"RTSP {method} returned status {response.StatusCode}.");
    }

    private async Task<RtspResponse> ExchangeAsync(
        string method, int maxBodyBytes, CancellationToken cancellationToken)
    {
        var request = BuildRequest(method);
        var bytes = Encoding.ASCII.GetBytes(request);

        await _stream.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
        await _stream.FlushAsync(cancellationToken).ConfigureAwait(false);

        return await ReadResponseAsync(maxBodyBytes, cancellationToken).ConfigureAwait(false);
    }

    private string BuildRequest(string method)
    {
        // Credentials must never appear in the request URI even when the configured stream URL
        // carries them; they belong in the Authorization header.
        var target = new UriBuilder(_uri) { UserName = string.Empty, Password = string.Empty }.Uri;

        var builder = new StringBuilder()
            .Append(CultureInfo.InvariantCulture, $"{method} {target} RTSP/1.0\r\n")
            .Append(CultureInfo.InvariantCulture, $"CSeq: {++_sequence}\r\n")
            .Append("User-Agent: Trinetra-Federation/0.1\r\n");

        if (method == "DESCRIBE")
        {
            builder.Append("Accept: application/sdp\r\n");
        }

        if (_challenge is not null)
        {
            var cnonce = Convert.ToHexStringLower(
                System.Security.Cryptography.RandomNumberGenerator.GetBytes(8));

            var authorization = DigestAuthHandler.BuildAuthorization(
                _challenge, _credential, method, target.ToString(), cnonce,
                _challenge.NextNonceCount());

            builder.Append(CultureInfo.InvariantCulture, $"Authorization: Digest {authorization}\r\n");
        }

        return builder.Append("\r\n").ToString();
    }

    private async Task<RtspResponse> ReadResponseAsync(
        int maxBodyBytes, CancellationToken cancellationToken)
    {
        var buffer = ArrayPool<byte>.Shared.Rent(16 * 1024);

        try
        {
            var total = 0;
            var headerEnd = -1;

            // Read until the blank line that ends the headers.
            while (headerEnd < 0)
            {
                if (total == buffer.Length)
                {
                    throw new NormalisationException("RTSP response headers exceeded the buffer.");
                }

                var read = await _stream
                    .ReadAsync(buffer.AsMemory(total, buffer.Length - total), cancellationToken)
                    .ConfigureAwait(false);

                if (read == 0)
                {
                    throw new TransientVmsException("RTSP connection closed while reading headers.");
                }

                total += read;
                headerEnd = FindHeaderEnd(buffer.AsSpan(0, total));
            }

            var headerText = Encoding.ASCII.GetString(buffer, 0, headerEnd);
            var status = ParseStatus(headerText);
            var contentLength = ParseContentLength(headerText);

            if (status == 401)
            {
                _challenge = ParseRtspChallenge(headerText);
            }

            if (contentLength <= 0)
            {
                return new RtspResponse(status, string.Empty);
            }

            // The SDP is capped: it is metadata, and an unbounded read here would let a
            // misbehaving server allocate freely inside a worker holding many targets.
            contentLength = Math.Min(contentLength, maxBodyBytes);

            var bodyStart = headerEnd + 4;
            var body = new byte[contentLength];
            var already = Math.Min(total - bodyStart, contentLength);

            if (already > 0)
            {
                buffer.AsSpan(bodyStart, already).CopyTo(body);
            }

            while (already < contentLength)
            {
                var read = await _stream
                    .ReadAsync(body.AsMemory(already, contentLength - already), cancellationToken)
                    .ConfigureAwait(false);

                if (read == 0)
                {
                    break;
                }

                already += read;
            }

            return new RtspResponse(status, Encoding.UTF8.GetString(body, 0, already));
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private static int FindHeaderEnd(ReadOnlySpan<byte> span)
    {
        ReadOnlySpan<byte> marker = "\r\n\r\n"u8;
        return span.IndexOf(marker);
    }

    private static int ParseStatus(string headerText)
    {
        // "RTSP/1.0 200 OK"
        var firstLine = headerText.AsSpan();
        var lineEnd = firstLine.IndexOf('\r');
        if (lineEnd > 0)
        {
            firstLine = firstLine[..lineEnd];
        }

        var space = firstLine.IndexOf(' ');
        if (space < 0)
        {
            return 0;
        }

        var rest = firstLine[(space + 1)..];
        var nextSpace = rest.IndexOf(' ');
        var code = nextSpace < 0 ? rest : rest[..nextSpace];

        return int.TryParse(code, out var status) ? status : 0;
    }

    private static int ParseContentLength(string headerText)
    {
        foreach (var line in headerText.Split("\r\n", StringSplitOptions.RemoveEmptyEntries))
        {
            if (line.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase)
                && int.TryParse(line["Content-Length:".Length..].Trim(), out var length))
            {
                return length;
            }
        }

        return 0;
    }

    private static DigestChallenge? ParseRtspChallenge(string headerText)
    {
        foreach (var line in headerText.Split("\r\n", StringSplitOptions.RemoveEmptyEntries))
        {
            if (!line.StartsWith("WWW-Authenticate:", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var value = line["WWW-Authenticate:".Length..].Trim();
            if (!value.StartsWith("Digest", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            // Reuse the HTTP digest parser: RTSP's challenge syntax is identical.
            var header = new AuthenticationHeaderValue("Digest", value["Digest".Length..].Trim());
            var collection = new HttpResponseMessage();
            collection.Headers.WwwAuthenticate.Add(header);

            return DigestAuthHandler.ParseChallenge(collection.Headers.WwwAuthenticate);
        }

        return null;
    }
}
