using Microsoft.AspNetCore.Http.HttpResults;
using Trinetra.Federation.Storage;
using Trinetra.Federation.Storage.Repositories;

namespace Trinetra.Federation.Api.Auth;

/// <summary>
/// The privilege-escalation chokepoint for conferring an access group's grant on a caller — or
/// on a key the caller mints, which acts through a group exactly as a user does.
/// </summary>
/// <remarks>
/// <para>
/// The group must be <c>ACTIVE</c>: a <c>DRAFT</c> or <c>INACTIVE</c> group grants nothing today,
/// but binding a membership or a key to one anyway leaves a grant that springs to full life with
/// no re-review the moment someone else activates it (finding 8-H3).
/// </para>
/// <para>
/// A caller unscoped for the gating permission on <b>both</b> dimensions has no ceiling to
/// escalate past. Otherwise, each dimension the caller is scoped on is checked independently
/// (invariant 12 — never let one dimension's unscoped answer stand in for the other):
/// </para>
/// <list type="number">
/// <item>the group grants no permission the caller does not already hold;</item>
/// <item>if the caller is organization-scoped: the group must declare an organization scope
/// (an unrestricted one applies across every department, i.e. granting it grants the estate),
/// and that scope must be within the units the caller administers;</item>
/// <item>if the caller is geography-scoped: the same two checks, on the group's geographic
/// scope and the areas the caller administers.</item>
/// </list>
/// <para>
/// Shared by <c>UserEndpoints.AddToGroupAsync</c> and <c>ApiKeyEndpoints.CreateAsync</c>. An
/// earlier version guarded only the user path and the organization dimension; the API-key path
/// applied none of it, so <c>apikey.manage</c> alone was a one-request estate takeover (mint a
/// key in the platform-admin group). Both routes hand out the same thing, so both run the same
/// guard.
/// </para>
/// </remarks>
internal static class GroupGrantGuard
{
    /// <summary>
    /// Returns a <see cref="ProblemHttpResult"/> to short-circuit the request with, or
    /// <see langword="null"/> if <paramref name="caller"/> may confer <paramref name="groupId"/>
    /// while exercising <paramref name="permission"/>.
    /// </summary>
    public static async Task<ProblemHttpResult?> CheckAsync(
        AccessGroupRepository groups,
        CallerContext caller,
        Guid groupId,
        string permission,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(groups);
        ArgumentNullException.ThrowIfNull(caller);

        var target = await groups.GetAsync(groupId, ct);
        if (target is null)
        {
            return TypedResults.Problem(
                title: "Unknown group",
                detail: "No such access group.",
                statusCode: StatusCodes.Status404NotFound);
        }

        if (target.Status != "ACTIVE")
        {
            return TypedResults.Problem(
                title: "Group is not active",
                detail: $"'{target.Code}' is {target.Status}, not ACTIVE, and grants nothing "
                      + "yet. Binding a membership or a key to it now would spring to full life "
                      + "with no re-review the moment it is activated.",
                statusCode: StatusCodes.Status409Conflict);
        }

        var unscopedOrg = caller.IsUnscopedFor(permission);
        var unscopedGeo = caller.IsUnscopedForGeography(permission);

        // Unscoped on both dimensions for the gating permission: no ceiling to escalate past.
        if (unscopedOrg && unscopedGeo)
        {
            return null;
        }

        // 1. Cannot hand out permissions you do not hold yourself — dimension-independent.
        var granted = await groups.GetGroupPermissionsAsync(groupId, ct);
        var exceeding = granted
            .Where(p => !caller.Has(p))
            .OrderBy(p => p, StringComparer.Ordinal)
            .ToList();

        if (exceeding.Count > 0)
        {
            return TypedResults.Problem(
                title: "Would grant more than you hold",
                detail: $"'{target.Code}' grants permissions you do not have: "
                      + $"{string.Join(", ", exceeding)}. You cannot give away access you were "
                      + "not given.",
                statusCode: StatusCodes.Status403Forbidden);
        }

        if (!unscopedOrg)
        {
            // 2. Cannot hand out a group that is unrestricted by organization — it applies
            //    across every department, so granting one is granting the estate.
            if (!await groups.HasOrganizationScopeAsync(groupId, ct))
            {
                return TypedResults.Problem(
                    title: "Group is unrestricted by organization",
                    detail: $"'{target.Code}' declares no organization scope, so it applies "
                          + "across every department. Only an administrator who is already "
                          + "unscoped may grant it.",
                    statusCode: StatusCodes.Status403Forbidden);
            }

            // 3. Cannot hand out a group scoped to a department you do not administer.
            if (!await groups.GroupScopesWithinReachAsync(groupId, caller, permission, ct))
            {
                return TypedResults.Problem(
                    title: "Group reaches beyond your scope",
                    detail: $"'{target.Code}' is scoped to organization units you do not "
                          + "administer.",
                    statusCode: StatusCodes.Status403Forbidden);
            }
        }

        if (!unscopedGeo)
        {
            // The geography-dimension counterpart of 2 and 3. A caller confined to one district
            // must not be able to hand out a group that is geographically unrestricted, or
            // scoped to a district they do not administer — otherwise a geo-restricted admin
            // could mint a key or membership reaching further than they themselves can.
            if (!await groups.HasGeographyScopeAsync(groupId, ct))
            {
                return TypedResults.Problem(
                    title: "Group is unrestricted by geography",
                    detail: $"'{target.Code}' declares no geographic scope, so it applies "
                          + "across every area. Only an administrator who is already unscoped "
                          + "for geography may grant it.",
                    statusCode: StatusCodes.Status403Forbidden);
            }

            if (!await groups.GroupGeographyScopesWithinReachAsync(groupId, caller, permission, ct))
            {
                return TypedResults.Problem(
                    title: "Group reaches beyond your scope",
                    detail: $"'{target.Code}' is scoped to geographic areas you do not "
                          + "administer.",
                    statusCode: StatusCodes.Status403Forbidden);
            }
        }

        return null;
    }
}
