using Microsoft.AspNetCore.OpenApi;
using Microsoft.OpenApi;
using Trinetra.Federation.Api.Auth;

namespace Trinetra.Federation.Api.OpenApi;

/// <summary>
/// Appends each operation's required permission to its description.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="RequiredPermissionMetadata"/> exists so the permission is declared on the route
/// rather than buried in a lambda, and one of the things that buys is visibility in the published
/// contract. Routing metadata does not reach the document on its own, though, so without this the
/// permission is inspectable at startup and in source but not by the person integrating against
/// the API — who discovers it as a 403 with no way to know which grant they are missing.
/// </para>
/// <para>
/// Written by transformer rather than by hand on each route so the two cannot drift: the line in
/// the docs is generated from the same metadata the filter enforces, so changing
/// <c>.RequirePermission(...)</c> changes the documentation with it.
/// </para>
/// </remarks>
internal sealed class PermissionDocumentationTransformer : IOpenApiOperationTransformer
{
    public Task TransformAsync(
        OpenApiOperation operation,
        OpenApiOperationTransformerContext context,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(operation);
        ArgumentNullException.ThrowIfNull(context);

        var required = context.Description.ActionDescriptor.EndpointMetadata
            .OfType<RequiredPermissionMetadata>()
            .LastOrDefault();

        if (required is null)
        {
            return Task.CompletedTask;
        }

        // AllowAnyAuthenticated stores its rationale in the same field, wrapped in parentheses,
        // so that the coverage check can tell a deliberate opening from a forgotten one. Render
        // it as prose; presenting "(any authenticated: ...)" as a permission name would send an
        // integrator looking for a grant that does not exist.
        var line = required.Permission.StartsWith('(')
            ? $"**Permission:** any authenticated caller — {required.Permission.Trim('(', ')')
                .Replace("any authenticated: ", string.Empty, StringComparison.Ordinal)}."
            : $"**Requires permission:** `{required.Permission}`";

        operation.Description = string.IsNullOrWhiteSpace(operation.Description)
            ? line
            : $"{operation.Description}\n\n{line}";

        return Task.CompletedTask;
    }
}
