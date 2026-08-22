using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Mvc;
using Npgsql;

namespace Trinetra.Federation.Api.Auth;

/// <summary>
/// Turns a database constraint violation into the status code it actually is.
/// </summary>
/// <remarks>
/// <para>
/// Reusing a business code, referencing a unit that does not exist, or breaking a check
/// constraint are all things a <b>caller</b> did, not failures of the server. Left unhandled they
/// surfaced as 500, which tells the operator the platform is broken when the fix is to change one
/// field — and buries genuine faults in an error rate full of typos.
/// </para>
/// <para>
/// The constraint name is deliberately not echoed. It is an internal schema detail, and it tends
/// to name the columns of an index in an order that means nothing to whoever is filling in a
/// form.
/// </para>
/// </remarks>
public sealed class ConstraintViolationExceptionHandler : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(
        HttpContext httpContext, Exception exception, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(httpContext);

        if (exception is not PostgresException pg)
        {
            return false;
        }

        var (status, title, detail) = pg.SqlState switch
        {
            PostgresErrorCodes.UniqueViolation => (
                StatusCodes.Status409Conflict,
                "Already exists",
                "Another record already uses one of these values. Codes must be unique."),

            PostgresErrorCodes.ForeignKeyViolation => (
                StatusCodes.Status400BadRequest,
                "Referenced record not found",
                "This request points at a record that does not exist, or at one that still has "
                + "dependents and cannot be removed."),

            PostgresErrorCodes.CheckViolation or PostgresErrorCodes.NotNullViolation => (
                StatusCodes.Status400BadRequest,
                "Value not allowed",
                "One of the supplied values is outside what this field accepts."),

            // Raised by the cycle-prevention triggers in 0001, which is the one case where the
            // database's own message is written for an operator and worth passing through.
            PostgresErrorCodes.RaiseException => (
                StatusCodes.Status400BadRequest,
                "Rejected by the data model",
                pg.MessageText),

            // Anything else is a genuine fault. Let it be a 500, honestly.
            _ => (0, string.Empty, string.Empty),
        };

        if (status == 0)
        {
            return false;
        }

        httpContext.Response.StatusCode = status;

        await httpContext.Response.WriteAsJsonAsync(
            new ProblemDetails
            {
                Title = title,
                Status = status,
                Detail = detail,
                Extensions = { ["traceId"] = httpContext.TraceIdentifier },
            },
            cancellationToken);

        return true;
    }
}
