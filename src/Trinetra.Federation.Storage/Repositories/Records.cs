namespace Trinetra.Federation.Storage.Repositories;

/// <summary>An owning organization — a department, institution or corporation.</summary>
public sealed record Organization
{
    public Guid Id { get; init; }
    public required string Code { get; init; }
    public required string Name { get; init; }
    public required string OrganizationType { get; init; }
    public string? Description { get; init; }
    public string Status { get; init; } = "ACTIVE";
}

/// <summary>A node in an organization's own hierarchy — commissionerate, zone, division.</summary>
public sealed record OrganizationUnit
{
    public Guid Id { get; init; }
    public required Guid OrganizationId { get; init; }
    public Guid? ParentUnitId { get; init; }
    public required string Code { get; init; }
    public required string Name { get; init; }
    public required string UnitType { get; init; }
    public string? Description { get; init; }

    /// <summary>
    /// Descriptive "home area" only — displayed and reported, never an authorization input.
    /// See the <c>COMMENT ON COLUMN organization_units.geographic_area_id</c> and invariant 12.
    /// </summary>
    public Guid? GeographicAreaId { get; init; }

    public string Status { get; init; } = "ACTIVE";
}

/// <summary>
/// A node in the geographic hierarchy. <see cref="AreaType"/> is operator-defined, so one
/// deployment's STATE/DISTRICT/TALUKA and another's CITY/ZONE/BLOCK share these tables.
/// </summary>
public sealed record GeographicArea
{
    public Guid Id { get; init; }
    public Guid? ParentAreaId { get; init; }
    public required string Code { get; init; }
    public required string Name { get; init; }
    public required string AreaType { get; init; }
    public string? Description { get; init; }
    public string Status { get; init; } = "ACTIVE";
}

/// <summary>A platform user. Never carries password material outside the repository.</summary>
public sealed record PlatformUser
{
    public Guid Id { get; init; }
    public required string Username { get; init; }
    public required string DisplayName { get; init; }
    public string? Email { get; init; }
    public bool MustChangePassword { get; init; }
    public string Status { get; init; } = "ACTIVE";
    public DateTimeOffset? LastLoginAt { get; init; }
    public bool IsSystem { get; init; }
}

/// <summary>A reusable bundle of one role and the scopes it applies within.</summary>
public sealed record AccessGroup
{
    public Guid Id { get; init; }
    public required string Code { get; init; }
    public required string Name { get; init; }
    public string? Description { get; init; }
    public required Guid RoleId { get; init; }
    public string Status { get; init; } = "DRAFT";
}

/// <summary>
/// How deactivating a hierarchy node with active children should be resolved.
/// </summary>
/// <remarks>
/// There is deliberately no default. Silently cascading can deactivate hundreds of cameras from
/// one click; silently orphaning leaves children pointing at an inactive parent so containment
/// queries stop matching and cameras fall out of scope with nothing reporting it. The operator
/// chooses — see GEOGRAPHY-SCHEMA.md.
/// </remarks>
public enum ChildStrategy
{
    /// <summary>Refuse and report what would be affected.</summary>
    Refuse,

    /// <summary>Deactivate the node and everything beneath it.</summary>
    Cascade,

    /// <summary>Move direct children to a new parent, then deactivate this node alone.</summary>
    Reparent,
}

/// <summary>What a deactivation would affect (the active child nodes), returned when it is refused.</summary>
public sealed record DeactivationConflict(IReadOnlyList<string> AffectedChildren);
