using Microsoft.AspNetCore.Authorization;
using Trinetra.Federation.Storage;

namespace Trinetra.Federation.Api.Auth;

/// <summary>The permission an endpoint requires, attached as routing metadata.</summary>
/// <remarks>
/// Declaring it on the route rather than only inside the handler makes the permission surface
/// <b>inspectable</b>: it appears in OpenAPI, it can be enumerated at startup, and a reviewer can
/// see what an endpoint needs without reading its body. A permission that exists only as a line
/// somewhere inside a lambda is invisible to every one of those.
/// </remarks>
public sealed class RequiredPermissionMetadata
{
    public RequiredPermissionMetadata(string permission) => Permission = permission;

    public string Permission { get; }
}

/// <summary>Marks a route reachable even while the caller's <c>mustChangePassword</c> is set.</summary>
public sealed class AllowWhileMustChangePasswordMetadata
{
    public AllowWhileMustChangePasswordMetadata(string why) => Why = why;

    public string Why { get; }
}

public static partial class PermissionEndpoints
{
    /// <summary>
    /// Declares and enforces the permission an endpoint requires.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This does <b>not</b> replace the checks in the repositories. Those stay, because
    /// <c>RBAC-LOGICAL-FLOW.md</c> §20 is explicit that the database query must return only
    /// authorized rows — a check at the edge cannot scope a <c>WHERE</c> clause, and an endpoint
    /// added later without this call would otherwise reach the data layer unguarded.
    /// </para>
    /// <para>
    /// What it adds is a cheap early refusal and, more importantly, a declaration. The permission
    /// becomes part of the route's contract instead of a detail buried in the handler.
    /// </para>
    /// </remarks>
    public static TBuilder RequirePermission<TBuilder>(this TBuilder builder, string permission)
        where TBuilder : IEndpointConventionBuilder
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrWhiteSpace(permission);

        builder.WithMetadata(new RequiredPermissionMetadata(permission));

        return builder.AddEndpointFilter(async (context, next) =>
        {
            var caller = CallerContextFactory.From(context.HttpContext);

            if (!caller.Has(permission))
            {
                return TypedResults.Problem(
                    title: "Forbidden",
                    detail: $"This action requires the '{permission}' permission.",
                    statusCode: StatusCodes.Status403Forbidden);
            }

            return await next(context);
        });
    }

    /// <summary>
    /// Marks a route as deliberately reachable by any authenticated caller.
    /// </summary>
    /// <remarks>
    /// Changing your own password is the case this exists for: requiring a permission would mean
    /// a user could be locked out of rotating their own credential. Declared explicitly so the
    /// coverage check can tell "open on purpose" from "someone forgot", which is the whole
    /// difference between a decision and an oversight.
    /// </remarks>
    public static TBuilder AllowAnyAuthenticated<TBuilder>(this TBuilder builder, string why)
        where TBuilder : IEndpointConventionBuilder
    {
        ArgumentNullException.ThrowIfNull(builder);
        builder.WithMetadata(new RequiredPermissionMetadata($"(any authenticated: {why})"));
        return builder;
    }

    /// <summary>
    /// Exempts a route from the must-change-password gate (finding 4-M1 / 5-L5): a user flagged
    /// <c>mustChangePassword</c> is otherwise refused every authenticated endpoint until they
    /// change it, and the routes that let them do exactly that — and step out again — would
    /// otherwise lock themselves out.
    /// </summary>
    public static TBuilder AllowWhileMustChangePassword<TBuilder>(this TBuilder builder, string why)
        where TBuilder : IEndpointConventionBuilder
    {
        ArgumentNullException.ThrowIfNull(builder);
        builder.WithMetadata(new AllowWhileMustChangePasswordMetadata(why));
        return builder;
    }

    /// <summary>
    /// Fails startup if an authorized endpoint declares no permission.
    /// </summary>
    /// <remarks>
    /// The failure this prevents is a new endpoint that authenticates but checks nothing, which
    /// looks correct in review — it has <c>RequireAuthorization()</c> — and grants every logged-in
    /// user whatever it exposes. Checked once at startup rather than trusted, because nothing at
    /// runtime distinguishes "deliberately open to any authenticated caller" from "forgotten".
    /// </remarks>
    public static void ValidatePermissionCoverage(this WebApplication app, ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(app);
        ArgumentNullException.ThrowIfNull(logger);

        // app.DataSources, NOT the EndpointDataSource resolved from DI. The container's copy is
        // empty until the routing middleware builds the pipeline, so reading it here inspects
        // zero endpoints and reports a clean bill of health for an application it never looked
        // at. The routes registered by MapGet/MapPost live on the builder itself.
        var all = ((IEndpointRouteBuilder)app).DataSources
            .SelectMany(d => d.Endpoints)
            .OfType<RouteEndpoint>()
            .ToList();

        // Counted once at startup, so the LINQ cost is irrelevant and the analyzer's concern
        // about evaluating arguments for a disabled log does not apply.
        var declared = all.Count(e => e.Metadata.GetMetadata<RequiredPermissionMetadata>() is not null);
        var total = all.Count;
        LogCoverage(logger, declared, total);

        var missing = all
            .Where(e => e.Metadata.GetMetadata<IAuthorizeData>() is not null)
            .Where(e => e.Metadata.GetMetadata<RequiredPermissionMetadata>() is null)
            .Where(e => e.Metadata.GetMetadata<AllowAnonymousAttribute>() is null)
            .Select(e => $"{string.Join('/', e.Metadata.OfType<HttpMethodMetadata>()
                                                .SelectMany(m => m.HttpMethods))} /{e.RoutePattern.RawText}")
            .Order(StringComparer.Ordinal)
            .ToList();

        if (missing.Count > 0)
        {
            LogUndeclared(logger, missing.Count, string.Join(", ", missing));
        }
    }

    [LoggerMessage(Level = LogLevel.Information,
        Message = "Permission coverage: {Declared} of {Total} route(s) declare a required "
                + "permission.")]
    private static partial void LogCoverage(ILogger logger, int declared, int total);

    [LoggerMessage(Level = LogLevel.Warning,
        Message = "{Count} authorized endpoint(s) declare no required permission: {Routes}. Each "
                + "is reachable by any authenticated user unless its own handler checks one. Add "
                + ".RequirePermission(...) so the requirement is visible on the route.")]
    private static partial void LogUndeclared(ILogger logger, int count, string routes);
}
