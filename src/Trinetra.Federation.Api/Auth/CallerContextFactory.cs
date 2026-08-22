using System.Security.Claims;
using Trinetra.Federation.Storage;

namespace Trinetra.Federation.Api.Auth;

/// <summary>Builds a <see cref="CallerContext"/> from the authenticated principal.</summary>
/// <remarks>
/// The single point where an HTTP identity becomes something the data layer will act on. Keeping
/// it in one place means the JWT and API key paths cannot end up with subtly different notions
/// of who the caller is or what they may reach.
/// </remarks>
public static class CallerContextFactory
{
    public static CallerContext From(HttpContext http)
    {
        ArgumentNullException.ThrowIfNull(http);

        var principal = http.User;

        var permissions = principal.FindAll(TrinetraClaims.Permission)
            .Select(c => c.Value)
            .ToHashSet(StringComparer.Ordinal);

        var unscoped = principal.FindAll(TrinetraClaims.UnscopedPermission)
            .Select(c => c.Value)
            .ToHashSet(StringComparer.Ordinal);

        var unscopedGeo = principal.FindAll(TrinetraClaims.UnscopedGeography)
            .Select(c => c.Value)
            .ToHashSet(StringComparer.Ordinal);

        var subject = principal.FindFirst("sub")?.Value
            ?? principal.FindFirst(ClaimTypes.NameIdentifier)?.Value;

        var isApiKey = principal.HasClaim(c => c.Type == "trinetra:apikey");

        // Null, never Guid.Empty, when the subject is missing or malformed. Guid.Empty would be
        // passed into has_permission() as if it were a real user id — matching nothing today,
        // but silently becoming a real identity the moment anyone inserts that id.
        Guid? subjectId = Guid.TryParse(subject, out var parsed) ? parsed : null;

        return new CallerContext
        {
            UserId = isApiKey ? null : subjectId,
            ApiKeyId = isApiKey ? subjectId : null,
            Actor = principal.Identity?.Name ?? subject ?? "unknown",
            Permissions = permissions,
            UnscopedPermissions = unscoped,
            UnscopedGeography = unscopedGeo,
            SourceAddress = http.Connection.RemoteIpAddress?.ToString(),
        };
    }

    /// <summary>Whether the caller must rotate their password before doing anything else.</summary>
    public static bool MustChangePassword(HttpContext http) =>
        http?.User.HasClaim(TrinetraClaims.MustChangePassword, "true") ?? false;
}
