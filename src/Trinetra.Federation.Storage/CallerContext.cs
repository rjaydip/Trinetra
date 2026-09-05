namespace Trinetra.Federation.Storage;

/// <summary>
/// Who is making a request, and what they may reach.
/// </summary>
/// <remarks>
/// <para>
/// Every repository method that touches scoped data takes one of these as a <b>required</b>
/// parameter. That is deliberate: <c>RBAC-LOGICAL-FLOW.md</c> §20 states frontend filtering is
/// not a security boundary, and neither is a handler that forgets a <c>WHERE</c>. Making the
/// context impossible to omit turns "remembered to scope the query" from a review question into
/// a compile-time one.
/// </para>
/// <para>
/// The context carries identity and permissions, not a list of permitted rows. Scope resolution
/// happens in SQL against the live hierarchy, so a unit added a second ago is already in scope.
/// </para>
/// </remarks>
public sealed record CallerContext
{
    /// <summary>Stable identifier of the acting user, or null for an API key.</summary>
    public Guid? UserId { get; init; }

    /// <summary>API key id when the caller is a service integration, otherwise null.</summary>
    public Guid? ApiKeyId { get; init; }

    /// <summary>Human-readable actor for audit records: a username, or an API key name.</summary>
    public required string Actor { get; init; }

    /// <summary>Permissions held, already unioned across the caller's groups.</summary>
    public required IReadOnlySet<string> Permissions { get; init; }

    /// <summary>
    /// The permissions this caller holds through a group that declares no organization scope,
    /// and therefore exercises across the entire estate.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Per permission, never a single flag. A single flag was a real vulnerability: it was
    /// computed for one permission at login and then consumed as a bypass for every other, so a
    /// user holding an unscoped read permission silently gained unscoped write and administration.
    /// </para>
    /// <para>
    /// Kept explicit rather than inferred from an empty scope list, because empty is ambiguous —
    /// it means either "reaches nothing" or "reaches everything", and confusing those either
    /// locks out the administrator or exposes the whole estate.
    /// </para>
    /// </remarks>
    public IReadOnlySet<string> UnscopedPermissions { get; init; } =
        new HashSet<string>(StringComparer.Ordinal);

    /// <summary>
    /// The permissions this caller exercises across the whole estate GEOGRAPHICALLY, held through
    /// a group that declares no geographic scope.
    /// </summary>
    /// <remarks>
    /// Tracked separately from <see cref="UnscopedPermissions"/>, which covers the organization
    /// dimension. RBAC-LOGICAL-FLOW.md treats the two as independent and ANDs them: a group may
    /// be confined to one department while reaching every district, or the reverse. One combined
    /// flag would let whichever dimension happened to be unconstrained override the other.
    /// </remarks>
    public IReadOnlySet<string> UnscopedGeography { get; init; } =
        new HashSet<string>(StringComparer.Ordinal);

    /// <summary>Whether this caller exercises one specific permission without scope limits.</summary>
    public bool IsUnscopedFor(string permission) =>
        IsSystem || UnscopedPermissions.Contains(permission);

    /// <summary>
    /// Whether this caller exercises one specific permission without GEOGRAPHIC scope limits.
    /// </summary>
    /// <remarks>
    /// The independent-dimension counterpart of <see cref="IsUnscopedFor"/>. ANDed with it,
    /// never substituted for it: a caller unscoped organizationally may still be confined to a
    /// district, and vice versa.
    /// </remarks>
    public bool IsUnscopedForGeography(string permission) =>
        IsSystem || UnscopedGeography.Contains(permission);

    /// <summary>Source address, recorded on audit rows.</summary>
    public string? SourceAddress { get; init; }

    /// <summary>
    /// A local process acting with full rights rather than on a person's behalf.
    /// </summary>
    /// <remarks>
    /// True only for the connector workers and the admin CLI. This is not a bypass so much as an
    /// accurate description: anything running locally already holds the database connection
    /// string and the credential encryption key, so it can do everything regardless. Modelling
    /// it as restricted would be theatre, and would hide the fact that terminal access is
    /// equivalent to full access. Actions are still audited, with the component named.
    /// </remarks>
    public bool IsSystem { get; init; }

    public bool Has(string permission) => IsSystem || Permissions.Contains(permission);

    /// <summary>Throws when the caller lacks a permission. Used before any mutation.</summary>
    public void Require(string permission)
    {
        if (!Has(permission))
        {
            throw new ForbiddenException(permission);
        }
    }

    /// <summary>A background service acting with full rights and no HTTP caller.</summary>
    /// <remarks>
    /// Used by the connector workers, which are not acting on anyone's behalf. Never constructed
    /// from a request — an HTTP path that reached this would silently gain full access.
    /// </remarks>
    public static CallerContext System(string component) => new()
    {
        Actor = $"system:{component}",
        Permissions = new HashSet<string>(StringComparer.Ordinal),
        IsSystem = true,
    };
}

/// <summary>The caller is authenticated but lacks the required permission.</summary>
public sealed class ForbiddenException : Exception
{
    public ForbiddenException(string permission)
        : base($"Permission '{permission}' is required.") => Permission = permission;

    public ForbiddenException() { }

    public ForbiddenException(string message, Exception innerException)
        : base(message, innerException) { }

    public string? Permission { get; }
}

/// <summary>
/// A write referenced another record — an organization unit, a site — that either does not
/// exist or is not <c>ACTIVE</c>.
/// </summary>
/// <remarks>
/// A foreign key alone catches "does not exist" (mapped to 400 by
/// <c>ConstraintViolationExceptionHandler</c>); it says nothing about status, so a write could
/// silently attach a row to a deactivated unit or site and drop it out of every scope query with
/// no error at all. This is the half a foreign key cannot cover.
/// </remarks>
public sealed class InvalidReferenceException : Exception
{
    public InvalidReferenceException(string field, string reason)
        : base($"'{field}' {reason}.") => Field = field;

    public InvalidReferenceException() { }

    public InvalidReferenceException(string message, Exception innerException)
        : base(message, innerException) { }

    public string? Field { get; }
}
