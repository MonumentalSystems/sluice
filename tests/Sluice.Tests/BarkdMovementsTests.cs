using Sluice.Barkd;
using Sluice.TestKit;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace Sluice.Tests;

/// <summary>The barkd movement → NWC list_transactions mapping: the real <see cref="BarkdClient"/> over the
/// <see cref="FakeBarkd"/> movement log. Filters to lightning, sets direction/amount/fee, and parses the
/// payment hash from the BOLT11. Fast.</summary>
public sealed class BarkdMovementsTests
{
    private static BarkdClient Client(FakeBarkd fake)
    {
        var options = new BarkdClientOptions { BaseUrl = "http://barkd.test:3535", Token = "t" };
        return new BarkdClient(Options.Create(options), fake, NullLogger<BarkdClient>.Instance);
    }

    [Fact]
    public async Task ListLightningMovements_filters_to_lightning_and_maps_direction_amount_fee_hash()
    {
        var movements = await Client(new FakeBarkd()).ListLightningMovementsAsync(100);

        // The bark.round (refresh) movement is excluded — only the receive + send remain.
        Assert.Equal(2, movements.Count);

        var send = Assert.Single(movements, m => m.Direction == "outgoing");
        Assert.Equal(1, send.AmountSat);
        Assert.Equal(20, send.FeeSat);
        Assert.NotNull(send.Invoice);
        Assert.Equal(64, send.PaymentHash!.Length); // parsed from the BOLT11

        var recv = Assert.Single(movements, m => m.Direction == "incoming");
        Assert.Equal(10, recv.AmountSat);
        Assert.Equal(0, recv.FeeSat); // fees only attributed to sends
        Assert.Equal(64, recv.PaymentHash!.Length);
        Assert.NotNull(recv.SettledAt);
    }

    [Fact]
    public async Task ListLightningMovements_honours_the_max()
    {
        var movements = await Client(new FakeBarkd()).ListLightningMovementsAsync(1);
        Assert.Single(movements);
    }
}
