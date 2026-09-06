namespace Trinetra.Federation.Storage.Repositories;

/// <summary>
/// The one place a user's every session is ended: bump <c>token_version</c> (kills the access
/// tokens) and revoke every refresh token, on one <see cref="UnitOfWork"/>.
/// </summary>
/// <remarks>
/// A single helper so the two writes always happen together and in the same order at every call
/// site — logout, self password change, admin reset, deactivation, group removal, refresh-token
/// replay. Bump first (it locks the <c>platform_users</c> row), then revoke (it locks
/// <c>refresh_token</c> rows); a different order at some future call site could deadlock two
/// concurrent "end all sessions" for the same user.
/// </remarks>
public static class SessionRevocation
{
    /// <summary>
    /// Ends every session for <paramref name="userId"/> inside <paramref name="work"/>. Returns
    /// the new <c>token_version</c>, so a caller re-issuing a token in the same transaction can
    /// stamp it correctly.
    /// </summary>
    public static async Task<int> EndAllAsync(
        UserRepository users, RefreshTokenRepository refreshTokens,
        Guid userId, UnitOfWork work, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(users);
        ArgumentNullException.ThrowIfNull(refreshTokens);

        var newVersion = await users.BumpTokenVersionAsync(userId, work, ct);
        await refreshTokens.RevokeAllForUserAsync(userId, work, ct);
        return newVersion;
    }
}
