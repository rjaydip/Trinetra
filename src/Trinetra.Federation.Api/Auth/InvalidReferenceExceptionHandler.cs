using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Mvc;
using Trinetra.Federation.Storage;

namespace Trinetra.Federation.Api.Auth;

/// <summary>
/// Turns <see cref="InvalidReferenceException"/> into the 400 it is — a caller pointed a write
/// at a record that exists but is not <c>ACTIVE</c>.
/// </summary>
public sealed class InvalidReferenceExceptionHandler : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(
        HttpContext httpContext, Exception exception, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(httpContext);

        if (exception is not InvalidReferenceException invalid)
        {
            return false;
        }

        httpContext.Response.StatusCode = StatusCodes.Status400BadRequest;

        await httpContext.Response.WriteAsJsonAsync(
            new ProblemDetails
            {
                Title = "Invalid reference",
                Status = StatusCodes.Status400BadRequest,
                Detail = invalid.Message,
                Extensions = { ["traceId"] = httpContext.TraceIdentifier },
            },
            cancellationToken);

        return true;
    }
}
