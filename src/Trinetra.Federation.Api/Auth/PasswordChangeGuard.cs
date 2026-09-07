using Microsoft.AspNetCore.Http.HttpResults;
using Trinetra.Federation.Storage.Repositories;
using Trinetra.Federation.Storage.Security;

namespace Trinetra.Federation.Api.Auth;

/// <summary>
/// The password-reuse and minimum-age checks shared by self-service change
/// (<c>AuthEndpoints.ChangePasswordAsync</c>) and administrative reset
/// (<c>UserEndpoints.ResetPasswordAsync</c>) — finding 4-M5.
/// </summary>
internal static class PasswordChangeGuard
{
    /// <summary>
    /// Runs the reuse check against <c>history</c> (newest-first, from
    /// <see cref="UserRepository.LoadPasswordHistoryAsync"/>) and, when <c>enforceMinimumAge</c>
    /// is set, the minimum-age check. Returns a ready 400 on rejection, or <see langword="null"/>
    /// to proceed. Does not run <see cref="PasswordHasher.IsAcceptable"/> or the "differs from
    /// current" check — callers already do those.
    /// </summary>
    public static ProblemHttpResult? Check(
        string newPassword,
        IReadOnlyList<PasswordHistoryEntry> history,
        bool enforceMinimumAge)
    {
        // The newest history row's set_at is when the CURRENT password took effect. No history
        // row means the user has never changed their password — not age-limited.
        if (enforceMinimumAge && history.Count > 0)
        {
            var age = DateTimeOffset.UtcNow - history[0].SetAt;
            if (age < PasswordPolicy.MinimumAge)
            {
                return (ProblemHttpResult)TypedResults.Problem(
                    title: "Password changed too recently",
                    detail: "Your password was changed within the last 24 hours. Wait before "
                          + "changing it again, or ask an administrator to reset it.",
                    statusCode: StatusCodes.Status400BadRequest);
            }
        }

        // N sequential PBKDF2 verifies (~150ms each). Acceptable on an interactive, already
        // rate-limited change path; deliberately not parallelised — the cost is bounded by
        // HistoryDepth.
        foreach (var entry in history)
        {
            if (PasswordHasher.Verify(newPassword, entry.Hash))
            {
                return (ProblemHttpResult)TypedResults.Problem(
                    title: "Password rejected",
                    detail: $"This password was used recently. Choose one you have not used in "
                          + $"your last {PasswordPolicy.HistoryDepth} passwords.",
                    statusCode: StatusCodes.Status400BadRequest);
            }
        }

        return null;
    }
}
