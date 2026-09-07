namespace Trinetra.Federation.Api.Auth;

/// <summary>
/// The <c>event_type</c> values written to <c>auth_audit</c>. Free text at the database — this
/// is the single place the vocabulary is defined.
/// </summary>
public static class AuthAuditEvents
{
    public const string LoginSuccess = "login.success";

    /// <summary>Coarse: an unknown username and a wrong password are the same event.</summary>
    public const string LoginFailure = "login.failure";

    public const string Lockout = "login.lockout";
    public const string TokenRefresh = "token.refresh";

    /// <summary>A rotated refresh token re-presented past its grace window.</summary>
    public const string TokenReplay = "token.replay";

    public const string Logout = "session.logout";

    /// <summary>Self-service change.</summary>
    public const string PasswordChange = "password.change";

    public const string PasswordAdminReset = "password.admin_reset";

    /// <summary>Silent cost upgrade on a successful login. Not a credential change.</summary>
    public const string PasswordRehash = "password.rehash";

    public const string ApiKeyAuth = "apikey.auth";
    public const string ApiKeyRateLimited = "apikey.ratelimited";
}
