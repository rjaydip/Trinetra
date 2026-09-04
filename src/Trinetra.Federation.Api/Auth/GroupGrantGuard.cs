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
/// A scoped caller must clear three checks; a caller unscoped for the gating permission bypasses
/// them, which is what being unscoped means.
/// </para>
/// <list type="number">
/// <item>the group grants no permission the caller does not already hold;</item>
/// <item>the group declares an organization scope — an unscoped group applies across every
/// department, so granting one is granting the estate;</item>
/// <item>the group's organization scopes are within the units the caller administers.</item>
/// </list>
/// <para>
/// Shared by <c>UserEndpoints.AddToGroupAsync</c> and <c>ApiKeyEndpoints.CreateAsync</c>. An
/// earlier version guarded only the user path; the API-key path applied none of these, so
/// <c>apikey.manage</c> alone was a one-request estate takeover (mint a key in the platform-admin
/// group). Both routes hand out the same thing, so both run the same guard.
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
                statusCode: StatusCodes.Status400BadRequest);
        }

        // Unscoped for the gating permission: no ceiling to escalate past.
        if (caller.IsUnscopedFor(permission))
        {
            return null;
        }

        // 1. Cannot hand out permissions you do not hold yourself.
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

        // 2. Cannot hand out a group that is unrestricted by organization — it applies across
        //    every department, so granting one is granting the estate.
        if (!await groups.HasOrganizationScopeAsync(groupId, ct))
        {
            return TypedResults.Problem(
                title: "Group is unrestricted by organization",
                detail: $"'{target.Code}' declares no organization scope, so it applies across "
                      + "every department. Only an administrator who is already unscoped may "
                      + "grant it.",
                statusCode: StatusCodes.Status403Forbidden);
        }

        // 3. Cannot hand out a group scoped to a department you do not administer.
        if (!await groups.GroupScopesWithinReachAsync(groupId, caller, permission, ct))
        {
            return TypedResults.Problem(
                title: "Group reaches beyond your scope",
                detail: $"'{target.Code}' is scoped to organization units you do not administer.",
                statusCode: StatusCodes.Status403Forbidden);
        }

        return null;
    }
}
