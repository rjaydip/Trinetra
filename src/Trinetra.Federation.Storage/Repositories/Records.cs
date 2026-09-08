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

/// <summary>Outcome of activating a hierarchy node (P3).</summary>
public enum ActivateResult
{
    /// <summary>The node was INACTIVE and is now ACTIVE; an audit row is expected.</summary>
    Activated,

    /// <summary>No such node (or out of the caller's reach).</summary>
    NotFound,

    /// <summary>The node's parent is itself INACTIVE — activating would recreate the orphan.</summary>
    ParentInactive,

    /// <summary>The node was already ACTIVE; nothing changed, no audit row.</summary>
    AlreadyActive,
}

/// <summary>
/// One access group whose scope would be affected by a cross-organization unit move (P2b), or
/// which references a role being soft-deleted (P8).
/// </summary>
public sealed record AffectedGroup(Guid Id, string Code, int MemberCount);

/// <summary>Why a cross-organization unit move could not be applied. <see cref="None"/> means it was.</summary>
public enum MoveOrgError
{
    /// <summary>Applied.</summary>
    None,

    /// <summary>The unit to move does not exist.</summary>
    UnitNotFound,

    /// <summary>The destination parent does not exist.</summary>
    ParentNotFound,

    /// <summary>The destination parent is not ACTIVE (or its organization is not ACTIVE).</summary>
    ParentInactive,

    /// <summary>The unit to move is not ACTIVE.</summary>
    SourceInactive,

    /// <summary>The destination parent is the unit itself or one of its descendants.</summary>
    ReparentUnderSelfOrDescendant,

    /// <summary>The destination parent is in the same organization — this is not a cross-org move.</summary>
    SameOrganization,

    /// <summary>The caller is not unscoped for <c>organization.manage</c>.</summary>
    NotUnscoped,

    /// <summary>Access-group scopes point into the subtree and the caller did not confirm the impact.</summary>
    NeedsConfirmation,
}

/// <summary>Outcome of <see cref="OrganizationRepository.MoveUnitToOrganizationAsync"/>.</summary>
public readonly record struct MoveOrgResult(
    MoveOrgError Error,
    Guid FromOrganizationId,
    Guid ToOrganizationId,
    int SubtreeSize,
    int CamerasFollowing,
    int TargetsFollowing,
    IReadOnlyList<AffectedGroup> AffectedGroups)
{
    public static MoveOrgResult Fail(MoveOrgError error) => new(error, default, default, 0, 0, 0, []);

    public static MoveOrgResult NeedsConfirmation(
        Guid fromOrg, Guid toOrg, int subtree, int cams, int targets, IReadOnlyList<AffectedGroup> groups) =>
        new(MoveOrgError.NeedsConfirmation, fromOrg, toOrg, subtree, cams, targets, groups);
}
