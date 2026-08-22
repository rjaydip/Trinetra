using System.Text.Json;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Mvc;

namespace Trinetra.Federation.Api.Auth;

/// <summary>
/// Turns a malformed request body into a 400 that names the offending field.
/// </summary>
/// <remarks>
/// <para>
/// Minimal APIs raise <see cref="BadHttpRequestException"/> when a body cannot be bound, and that
/// exception already carries <c>StatusCode = 400</c>. The default
/// <c>UseExceptionHandler()</c> pipeline ignores it and returns 500, so a client that misspells a
/// field, omits a required one, or sends an explicit null gets an Internal Server Error.
/// </para>
/// <para>
/// That is worse than a cosmetic problem. A 500 says "the server is broken", so the caller
/// escalates it as an outage instead of correcting a payload, and it pollutes the error rate
/// that on-call alerting watches with faults that are not faults.
/// </para>
/// </remarks>
public sealed class BadRequestExceptionHandler : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(
        HttpContext httpContext, Exception exception, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(httpContext);

        if (exception is not BadHttpRequestException bad)
        {
            // Genuinely unexpected. Let the default pipeline answer 500, which is honest.
            return false;
        }

        httpContext.Response.StatusCode = bad.StatusCode;

        await httpContext.Response.WriteAsJsonAsync(
            new ProblemDetails
            {
                Type = "https://tools.ietf.org/html/rfc9110#section-15.5.1",
                Title = "Malformed request body",
                Status = bad.StatusCode,
                Detail = Describe(bad),
                Extensions = { ["traceId"] = httpContext.TraceIdentifier },
            },
            cancellationToken);

        return true;
    }

    /// <summary>
    /// Explains what was wrong with the body, without leaking anything internal.
    /// </summary>
    /// <remarks>
    /// The inner <see cref="JsonException"/> names the field and the JSON path. That is the
    /// caller's own payload described back to them, so it carries no server detail — and without
    /// it the message degrades to "the body was invalid", which is unactionable on a request with
    /// twenty fields.
    /// </remarks>
    private static string Describe(BadHttpRequestException exception)
    {
        if (exception.InnerException is not JsonException json)
        {
            return "The request body could not be read as JSON matching this endpoint's contract.";
        }

        // System.Text.Json's own wording names the CLR type and advises changing a nullability
        // annotation. That is guidance for whoever wrote the server, meaningless to the client
        // holding a bad payload, and it hands a prober the internal namespace for free. So the
        // two cases a caller can actually act on get their own message, built from the field.
        var missing = MissingFields(json.Message);

        if (missing.Count > 0)
        {
            return $"Required field(s) missing: {string.Join(", ", missing)}.";
        }

        if (json.Path is { } path && json.Message.Contains("null values", StringComparison.Ordinal))
        {
            return $"'{path}' is required and cannot be null.";
        }

        return json.Path switch
        {
            // Path "$" is the document root: the body never parsed as JSON at all, so pointing at
            // a "field" would send the caller looking for one that was never reached.
            null or "$" => "The request body is not valid JSON.",
            var p => $"The value at '{p}' is not valid for this field.",
        };
    }

    /// <summary>Pulls the field names out of the "missing required properties" message.</summary>
    private static List<string> MissingFields(string message)
    {
        const string marker = "missing required properties including: ";
        var at = message.IndexOf(marker, StringComparison.Ordinal);

        if (at < 0)
        {
            return [];
        }

        var tail = message[(at + marker.Length)..];
        var end = tail.IndexOf('.', StringComparison.Ordinal);

        return [.. (end < 0 ? tail : tail[..end])
            .Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .Select(f => f.Trim('\''))];
    }

    // System.Text.Json appends its own position details, which duplicate the path and read as
    // parser internals to anyone calling the API.
    private static string TrimJsonNoise(string message)
    {
        var cut = message.IndexOf(" Path: ", StringComparison.Ordinal);
        return cut < 0 ? message : message[..cut].TrimEnd();
    }
}
