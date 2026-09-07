using Microsoft.AspNetCore.Http.HttpResults;
using Trinetra.Federation.Storage;
using Trinetra.Federation.Storage.Repositories;

namespace Trinetra.Federation.Api.Auth;

/// <summary>
/// The single gate for touching one specific user account — read or write.
/// </summary>
/// <remarks>
/// <para>
/// Holding <c>user.read</c> or <c>user.manage</c> says the caller may work with user accounts;
/// it does not say <i>which</i> ones. Without this check a department-scoped administrator could
/// read — or reset the password of — the bootstrap administrator, and a takeover follows from
/// the lowest privilege that touches users at all.
/// </para>
/// <para>
/// Returns 404, never 403: confirming that a user id exists is itself a disclosure to someone
/// with no authority over it. A hidden account is indistinguishable from one that does not exist,
/// which matches the list endpoints (they simply omit it) and <c>CameraRepository.GetAsync</c>.
/// </para>
/// <para>
/// Extracted from <c>UserEndpoints</c> (where it guarded only the write paths) so the read
/// single-fetch routes — <c>GET /users/{id}</c>, <c>/{id}/groups</c>, <c>/{id}/permissions</c> —
/// run the identical check. Invariant 11's compile-time enforcement covers the row-returning
/// repository queries (<c>ListAsync</c> takes a <see cref="CallerContext"/>); the single-row
/// primary-key fetches are guarded here instead, through one path that cannot be forgotten.
/// </para>
/// </remarks>
internal static class UserAuthorityGuard
{
    /// <summary>
    /// Returns a <see cref="ProblemHttpResult"/> (404) to short-circuit with, or
    /// <see langword="null"/> if <paramref name="caller"/> may act on
    /// <paramref name="targetUserId"/> while exercising <paramref name="permission"/>
    /// (<c>user.read</c> for reads, <c>user.manage</c> for writes).
    /// </summary>
    /// <remarks>
    /// The read path (<c>user.read</c>) checks organizational reach only — the same rule
    /// <see cref="UserRepository.ListAsync"/> filters by, so a listed account and its detail route
    /// never disagree. The write path (<c>user.manage</c>) additionally checks that the target
    /// holds no permission the caller lacks (<see cref="UserRepository.CanAdministerAsync"/>
    /// condition 1) — an escalation guard that matters only when the caller can change something.
    /// </remarks>
    public static async Task<ProblemHttpResult?> RefuseAsync(
        Guid targetUserId, CallerContext caller, string permission,
        UserRepository users, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(caller);
        ArgumentNullException.ThrowIfNull(users);

        var unscoped = caller.IsUnscopedFor(permission);

        // The seeded account is the one every other was created from. Only an unscoped caller
        // may touch it — or even learn it exists — whatever their permissions otherwise say.
        if (!unscoped && await users.IsSystemAccountAsync(targetUserId, ct))
        {
            return NotFound();
        }

        var allowed = permission == "user.read"
            ? await users.CanReadAsync(targetUserId, caller, ct)
            : await users.CanAdministerAsync(targetUserId, caller.UserId, unscoped, permission, ct);

        if (!allowed)
        {
            return NotFound();
        }

        return null;

        static ProblemHttpResult NotFound() => TypedResults.Problem(
            title: "Not found", statusCode: StatusCodes.Status404NotFound);
    }
}
