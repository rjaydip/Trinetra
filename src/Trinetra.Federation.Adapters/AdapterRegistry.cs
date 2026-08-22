using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Trinetra.Federation.Adapters.Dahua;
using Trinetra.Federation.Adapters.Hikvision;
using Trinetra.Federation.Adapters.Http;
using Trinetra.Federation.Adapters.Onvif;
using Trinetra.Federation.Core.Abstractions;
using Trinetra.Federation.Core.Errors;
using Trinetra.Federation.Core.Model;

namespace Trinetra.Federation.Adapters;

/// <summary>Creates the adapter for a connector target.</summary>
public interface IAdapterFactory
{
    /// <summary>Whether an adapter exists for this vendor.</summary>
    bool Supports(VendorKind vendor);

    /// <summary>
    /// Builds an adapter bound to one target, resolving its credential first.
    /// </summary>
    /// <remarks>
    /// The credential is fetched here and handed to the adapter, so secret resolution happens in
    /// exactly one place and is audit-logged once per connection rather than per request.
    /// </remarks>
    Task<IVmsAdapter> CreateAsync(ConnectorTarget target, CancellationToken cancellationToken);
}

/// <summary>
/// The vendor dispatch table — the one place in the platform that maps a vendor to code.
/// </summary>
/// <remarks>
/// <para>
/// Adding a vendor means implementing <see cref="IVmsAdapter"/> and adding one line here.
/// Nothing outside <c>Trinetra.Federation.Adapters</c> should need to change; if it does, the
/// abstraction has leaked and the change is wrong.
/// </para>
/// <para>
/// CP Plus, Prama and similar Indian-market brands are Dahua OEM hardware and map to
/// <see cref="VendorKind.DahuaCgi"/> rather than gaining entries of their own — the enum names
/// protocol surfaces, not brands.
/// </para>
/// </remarks>
public sealed class AdapterRegistry : IAdapterFactory
{
    private readonly TargetHttpClientProvider _clients;
    private readonly ICredentialResolver _credentials;
    private readonly ILoggerFactory _loggers;

    public AdapterRegistry(
        TargetHttpClientProvider clients,
        ICredentialResolver credentials,
        ILoggerFactory loggers)
    {
        _clients = clients;
        _credentials = credentials;
        _loggers = loggers;
    }

    public bool Supports(VendorKind vendor) => vendor is
        VendorKind.Onvif or VendorKind.HikvisionIsapi or VendorKind.DahuaCgi;

    public async Task<IVmsAdapter> CreateAsync(
        ConnectorTarget target, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(target);

        var credential = await _credentials
            .ResolveAsync(target.CredentialReference, cancellationToken).ConfigureAwait(false);

        return target.Vendor switch
        {
            VendorKind.Onvif => new OnvifAdapter(
                target, credential, _clients, _loggers.CreateLogger<OnvifAdapter>()),

            VendorKind.HikvisionIsapi => new HikvisionAdapter(
                target, credential, _clients, _loggers.CreateLogger<HikvisionAdapter>()),

            VendorKind.DahuaCgi => new DahuaAdapter(
                target, credential, _clients, _loggers.CreateLogger<DahuaAdapter>()),

            // Quarantine rather than retry: an unimplemented vendor is a configuration fact that
            // no amount of retrying will change, and a target stuck in a retry loop consumes
            // worker capacity that belongs to targets that can actually be served.
            _ => throw new ConfigurationException(
                $"No adapter is registered for vendor {target.Vendor}. "
                + $"Target {target.Id} cannot be federated.", target.Id),
        };
    }
}

/// <summary>DI registration for the adapter layer.</summary>
public static class AdapterServiceCollectionExtensions
{
    /// <summary>
    /// Registers the shared HTTP infrastructure and the adapter factory.
    /// </summary>
    /// <remarks>
    /// The two digest handlers are separate instances because each is bound into one client's
    /// handler chain, and a <see cref="DelegatingHandler"/> may not be shared across chains.
    /// </remarks>
    public static IServiceCollection AddFederationAdapters(this IServiceCollection services)
    {
        services.AddSingleton(provider => new TargetHttpClientProvider(
            new DigestAuthHandler(),
            new DigestAuthHandler(),
            provider.GetRequiredService<ILogger<TargetHttpClientProvider>>()));

        services.AddSingleton<IAdapterFactory, AdapterRegistry>();

        return services;
    }
}
