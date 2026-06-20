using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Sluice;
using Sluice.Barkd;
using Sluice.Nwc;
using Xunit;

// IBarkdClient/BarkdClient are exercised by AddBarkdClient below.

namespace Sluice.Tests;

/// <summary>The DI wiring: <see cref="ServiceCollectionExtensions.AddBarkdClient"/> registers the typed
/// client; <see cref="NwcServiceCollectionExtensions.AddNwcBridge(IServiceCollection, NwcConfig)"/> registers
/// the config + the hosted bridge. Fast.</summary>
public sealed class ServiceCollectionExtensionsTests
{
    [Fact]
    public void AddBarkdClient_registers_a_configured_barkd_client()
    {
        var services = new ServiceCollection();
        services.AddBarkdClient(o => { o.BaseUrl = "http://barkd:3535"; o.Token = "t"; });
        using var provider = services.BuildServiceProvider();

        var client = provider.GetRequiredService<IBarkdClient>();
        Assert.IsType<BarkdClient>(client);
        Assert.True(client.IsConfigured);
    }

    [Fact]
    public void AddBarkdClient_null_configure_throws()
    {
        var services = new ServiceCollection();
        Assert.Throws<ArgumentNullException>(() => services.AddBarkdClient(null!));
    }

    [Fact]
    public void AddNwcBridge_with_config_registers_config_and_hosted_service()
    {
        var cfg = new NwcConfig { Enabled = true, WalletName = "w" };
        var services = new ServiceCollection();
        services.AddNwcBridge(cfg);

        // The concrete config instance is registered as a singleton…
        Assert.Same(cfg, services.BuildServiceProvider().GetRequiredService<NwcConfig>());
        // …and the bridge is registered as the IHostedService implementation.
        Assert.Contains(services, d =>
            d.ServiceType == typeof(IHostedService) && d.ImplementationType == typeof(NwcBridge));
    }

    [Fact]
    public void AddNwcBridge_inline_configure_builds_a_config()
    {
        var services = new ServiceCollection();
        services.AddNwcBridge(c => { c.Enabled = true; c.WalletName = "inline"; c.MaxDailyPaySat = 100; });
        using var provider = services.BuildServiceProvider();

        var cfg = provider.GetRequiredService<NwcConfig>();
        Assert.True(cfg.Enabled);
        Assert.Equal("inline", cfg.WalletName);
        Assert.Contains("pay_invoice", cfg.Methods());
    }
}
