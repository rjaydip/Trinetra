namespace Trinetra.Federation.Storage.Security;

/// <summary>Reuse and age limits on password changes (finding 4-M5).</summary>
/// <remarks>
/// Not an options class: these are policy, not deployment config, and a per-host override is a
/// way to quietly weaken the weakest path — the same reason
/// <see cref="PasswordHasher.MinimumLength"/> is a const. Change them here and ship a build.
/// </remarks>
public static class PasswordPolicy
{
    /// <summary>
    /// How many prior passwords are retained and refused. 5 balances real reuse resistance
    /// against 5 PBKDF2 verifies on the change path (~0.75s total at the current cost) and caps
    /// the history table at 5 rows per user.
    /// </summary>
    public const int HistoryDepth = 5;

    /// <summary>
    /// A self-service change is refused while the current password is younger than this — it
    /// blocks cycling through <see cref="HistoryDepth"/> changes in a minute to reach an old
    /// password. Admin reset is exempt.
    /// </summary>
    public static readonly TimeSpan MinimumAge = TimeSpan.FromHours(24);
}
