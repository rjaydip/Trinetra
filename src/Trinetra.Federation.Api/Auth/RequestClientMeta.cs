namespace Trinetra.Federation.Api.Auth;

/// <summary>Source IP and user-agent for an auth-audit row.</summary>
internal static class RequestClientMeta
{
    /// <remarks>
    /// <see cref="Microsoft.AspNetCore.Http.ConnectionInfo.RemoteIpAddress"/> as-is: behind the
    /// on-prem reverse proxy this is the proxy's address. Forwarded-headers / trusted-proxy
    /// hardening is a separate work item (finding 4-M3) and is intentionally out of scope here —
    /// a proxy IP on every row is still better than no IP.
    /// </remarks>
    public static (string? Ip, string? UserAgent) From(HttpContext http)
    {
        var ip = http.Connection.RemoteIpAddress?.ToString();
        var ua = http.Request.Headers.UserAgent.ToString();
        return (ip, string.IsNullOrEmpty(ua) ? null : ua);
    }
}
