using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Mvc;

namespace Trinetra.Federation.Api.Auth;

/// <summary>
/// Converts unexpected endpoint failures into a safe, traceable problem response.
/// </summary>
public sealed partial class UnhandledExceptionHandler(
    ILogger<UnhandledExceptionHandler> logger,
    IHostEnvironment environment) : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(
        HttpContext httpContext,
        Exception exception,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(httpContext);

        if (httpContext.Response.HasStarted)
        {
            return false;
        }

        if (exception is OperationCanceledException
            && httpContext.RequestAborted.IsCancellationRequested)
        {
            return false;
        }

        var traceId = httpContext.TraceIdentifier;
        LogUnhandled(
            exception,
            httpContext.Request.Method,
            httpContext.Request.Path,
            traceId);

        var problem = new ProblemDetails
        {
            Type = "https://tools.ietf.org/html/rfc9110#section-15.6.1",
            Title = "Internal server error",
            Status = StatusCodes.Status500InternalServerError,
            Detail = "The request could not be completed. Use the traceId when contacting support.",
            Extensions = { ["traceId"] = traceId },
        };

        // In Development only, hand the caller the actual exception and stack trace so a 500 is
        // debuggable from the response instead of requiring a look at the server log. Never in
        // any other environment: a stack trace leaks internal paths, library versions and
        // sometimes data values to whoever provoked the error.
        if (environment.IsDevelopment())
        {
            problem.Detail = exception.Message;
            problem.Extensions["exception"] = exception.GetType().FullName;
            problem.Extensions["stackTrace"] = exception.ToString()
                .Split('\n', StringSplitOptions.RemoveEmptyEntries)
                .Select(line => line.TrimEnd('\r'))
                .ToArray();
        }

        httpContext.Response.StatusCode = StatusCodes.Status500InternalServerError;
        await httpContext.Response.WriteAsJsonAsync(problem, cancellationToken);

        return true;
    }

    [LoggerMessage(
        EventId = 5000,
        Level = LogLevel.Error,
        Message = "Unhandled exception for {Method} {Path}. TraceId: {TraceId}")]
    private partial void LogUnhandled(
        Exception exception,
        string method,
        string path,
        string traceId);
}
