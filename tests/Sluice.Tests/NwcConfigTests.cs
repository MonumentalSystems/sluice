using Microsoft.Extensions.Configuration;
using Sluice.Nwc;
using Xunit;

namespace Sluice.Tests;

/// <summary>Binding + the receive-only-vs-pay method set for <see cref="NwcConfig"/>. Fast.</summary>
public sealed class NwcConfigTests
{
    private static IConfiguration Config(Dictionary<string, string?> values) =>
        new ConfigurationBuilder().AddInMemoryCollection(values).Build();

    [Fact]
    public void Methods_receive_only_when_no_pay_cap()
    {
        var cfg = new NwcConfig(); // MaxDailyPaySat defaults to 0
        var methods = cfg.Methods();
        Assert.Contains("get_info", methods);
        Assert.Contains("get_balance", methods);
        Assert.Contains("make_invoice", methods);
        Assert.Contains("lookup_invoice", methods);
        Assert.Contains("list_transactions", methods);
        Assert.DoesNotContain("pay_invoice", methods);
    }

    [Fact]
    public void Methods_includes_pay_invoice_when_cap_positive()
    {
        var cfg = new NwcConfig { MaxDailyPaySat = 5_000 };
        Assert.Contains("pay_invoice", cfg.Methods());
    }

    [Fact]
    public void FromConfiguration_binds_the_nwc_section()
    {
        var cfg = NwcConfig.FromConfiguration(Config(new()
        {
            ["Nwc:Enabled"] = "true",
            ["Nwc:PrivateKeyHex"] = new string('a', 64),
            ["Nwc:WalletName"] = "my-wallet",
            ["Nwc:MaxInvoiceSat"] = "50000",
            ["Nwc:MaxDailyPaySat"] = "1000",
            ["Nwc:Relays:0"] = "wss://relay.one",
            ["Nwc:Relays:1"] = "wss://relay.two",
            ["Nwc:ConnectionSecrets:0"] = new string('b', 64),
        }));

        Assert.True(cfg.Enabled);
        Assert.Equal("my-wallet", cfg.WalletName);
        Assert.Equal(50_000, cfg.MaxInvoiceSat);
        Assert.Equal(1_000, cfg.MaxDailyPaySat);
        Assert.Equal(new[] { "wss://relay.one", "wss://relay.two" }, cfg.Relays);
        Assert.Single(cfg.ConnectionSecrets);
        Assert.Contains("pay_invoice", cfg.Methods());
    }

    [Fact]
    public void FromConfiguration_defaults_when_section_absent()
    {
        var cfg = NwcConfig.FromConfiguration(Config(new()));
        Assert.False(cfg.Enabled);
        Assert.Equal("barkd", cfg.WalletName);
        Assert.Equal(1_000_000, cfg.MaxInvoiceSat);
        Assert.Empty(cfg.Relays);
        Assert.Empty(cfg.ConnectionSecrets);
        Assert.DoesNotContain("pay_invoice", cfg.Methods());
    }

    [Fact]
    public void FromConfiguration_drops_blank_relays_and_secrets()
    {
        var cfg = NwcConfig.FromConfiguration(Config(new()
        {
            ["Nwc:Relays:0"] = "wss://good",
            ["Nwc:Relays:1"] = "   ",
            ["Nwc:Relays:2"] = "",
            ["Nwc:ConnectionSecrets:0"] = "abc",
            ["Nwc:ConnectionSecrets:1"] = "",
        }));
        Assert.Equal(new[] { "wss://good" }, cfg.Relays);
        Assert.Equal(new[] { "abc" }, cfg.ConnectionSecrets);
    }
}
