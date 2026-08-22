using System.Net;
using Microsoft.AspNetCore.HttpOverrides;

namespace Trinetra.Federation.Api.Auth;

/// <summary>How the API sits behind (or does not sit behind) a reverse proxy.</summary>
public sealed class NetworkOptions
{
    public const string SectionName = "Network";

    /// <summary>
    /// Whether to trust <c>X-Forwarded-For</c> / <c>X-Forwarded-Proto</c>.
    /// </summary>
    /// <remarks>
    /// Off by default, and deliberately not inferred. When the API is reached directly, trusting
    /// these headers lets any client claim any source address — which defeats per-IP rate
    /// limiting completely and poisons every audit record's <c>source_address</c>.
    /// </remarks>
    public bool TrustForwardedHeaders { get; set; }

    /// <summary>
    /// Addresses of the proxies allowed to set those headers.
    /// </summary>
    /// <remarks>
    /// Required whenever <see cref="TrustForwardedHeaders"/> is on. An empty list with forwarding
    /// enabled means ASP.NET trusts the header from <i>anyone</i>, which is strictly worse than
    /// leaving forwarding off: an attacker sends a fresh <c>X-Forwarded-For</c> per request and
    /// every rate-limit partition becomes a partition of one.
    /// </remarks>
    public IList<string> KnownProxies { get; } = [];

    /// <summary>Proxy subnets, as CIDR, for deployments behind a load-balancer pool.</summary>
    public IList<string> KnownNetworks { get; } = [];

    /// <summary>
    /// Builds the options, refusing configurations that would silently disable rate limiting.
    /// </summary>
    public ForwardedHeadersOptions Build()
    {
        var options = new ForwardedHeadersOptions
        {
            ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto,

            // One hop by default. A larger value means trusting entries further left in the
            // header, which are client-controlled unless every intermediate proxy is also trusted.
            ForwardLimit = 1,
        };

        // ASP.NET ships loopback pre-trusted. Cleared so the trusted set is exactly what was
        // configured — otherwise a local process could forge a source address.
        options.KnownProxies.Clear();
        options.KnownIPNetworks.Clear();

        foreach (var proxy in KnownProxies)
        {
            if (!IPAddress.TryParse(proxy, out var address))
            {
                throw new InvalidOperationException(
                    $"Network:KnownProxies contains '{proxy}', which is not an IP address.");
            }

            options.KnownProxies.Add(address);
        }

        foreach (var network in KnownNetworks)
        {
            var parts = network.Split('/', 2);

            if (parts.Length != 2
                || !IPAddress.TryParse(parts[0], out var prefix)
                || !int.TryParse(parts[1], out var length))
            {
                throw new InvalidOperationException(
                    $"Network:KnownNetworks contains '{network}', which is not CIDR notation "
                    + "such as 10.0.0.0/8.");
            }

            options.KnownIPNetworks.Add(new System.Net.IPNetwork(prefix, length));
        }

        return options;
    }

    /// <summary>
    /// Fails startup on a configuration that would leave the deployment unprotected.
    /// </summary>
    /// <remarks>
    /// Checked at startup rather than trusted, because both failure modes are invisible at
    /// runtime: rate limiting keeps returning 200s while protecting nothing.
    /// </remarks>
    public void Validate(bool isDevelopment)
    {
        if (!TrustForwardedHeaders)
        {
            return;
        }

        if (KnownProxies.Count == 0 && KnownNetworks.Count == 0)
        {
            throw new InvalidOperationException(
                "Network:TrustForwardedHeaders is enabled but no Network:KnownProxies or "
                + "Network:KnownNetworks are configured.\n\n"
                + "  That combination trusts X-Forwarded-For from any caller, so a client can "
                + "claim a new source address on every request and rate limiting stops working "
                + "entirely — while still appearing to.\n\n"
                + "  List the reverse proxy's address, or turn TrustForwardedHeaders off if the "
                + "API is reached directly.");
        }

        if (!isDevelopment && KnownProxies.Contains("127.0.0.1"))
        {
            throw new InvalidOperationException(
                "Network:KnownProxies trusts 127.0.0.1 outside Development. Any local process "
                + "could then forge a client address. List the proxy's real address.");
        }
    }
}
