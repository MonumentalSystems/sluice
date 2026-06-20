using Sluice.Nwc;
using Microsoft.Extensions.DependencyInjection;

namespace Sluice;

/// <summary>
/// DI wiring for the NIP-47 (Nostr Wallet Connect) bridge. Registers the <see cref="NwcConfig"/> and runs
/// <see cref="NwcBridge"/> as a hosted background service. The bridge needs an
/// <see cref="Sluice.Barkd.IBarkdClient"/> in the container (wire it via the barkd registration).
/// </summary>
public static class NwcServiceCollectionExtensions
{
    /// <summary>Register the NWC bridge with a concrete <see cref="NwcConfig"/>.</summary>
    public static IServiceCollection AddNwcBridge(this IServiceCollection services, NwcConfig cfg)
    {
        services.AddSingleton(cfg);
        services.AddHostedService<NwcBridge>();
        return services;
    }

    /// <summary>Register the NWC bridge, configuring <see cref="NwcConfig"/> inline.</summary>
    public static IServiceCollection AddNwcBridge(this IServiceCollection services, Action<NwcConfig> configure)
    {
        var cfg = new NwcConfig();
        configure(cfg);
        return services.AddNwcBridge(cfg);
    }
}
