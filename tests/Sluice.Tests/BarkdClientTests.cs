using Sluice.Barkd;
using Sluice.TestKit;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace Sluice.Tests;

/// <summary>The real <see cref="BarkdClient"/> over <see cref="FakeBarkd"/> — invoice mint (with the
/// receive-status payment-hash fallback the 0.2.3 spec requires), receive-status settled/canceled/not-found,
/// wallet info reachable/unreachable, the pay failure path, and the unconfigured fail-loud surface. Fast.</summary>
public sealed class BarkdClientTests
{
    private static BarkdClient Configured(FakeBarkd fake)
    {
        var options = new BarkdClientOptions { BaseUrl = "http://barkd.test:3535/", Token = "t", TimeoutSeconds = 1 };
        return new BarkdClient(Options.Create(options), fake, NullLogger<BarkdClient>.Instance);
    }

    private static BarkdClient Unconfigured(FakeBarkd fake)
    {
        var options = new BarkdClientOptions { BaseUrl = null };
        return new BarkdClient(Options.Create(options), fake, NullLogger<BarkdClient>.Instance);
    }

    [Fact]
    public void IsConfigured_reflects_the_base_url()
    {
        Assert.True(Configured(new FakeBarkd()).IsConfigured);
        Assert.False(Unconfigured(new FakeBarkd()).IsConfigured);
    }

    // ── CreateInvoiceAsync ──────────────────────────────────────────────────────────────────────────
    [Fact]
    public async Task CreateInvoice_returns_invoice_and_resolves_hash_via_receive_status()
    {
        var fake = new FakeBarkd();
        var inv = await Configured(fake).CreateInvoiceAsync(1000, "test");
        Assert.False(string.IsNullOrEmpty(inv.Invoice));
        // FakeBarkd's invoice isn't parseable BOLT11 ⇒ the client falls back to the receive-status lookup.
        Assert.Equal(fake.LastPaymentHash, inv.PaymentHash);
        Assert.Equal(64, inv.PaymentHash.Length);
    }

    [Fact]
    public async Task CreateInvoice_unconfigured_throws_barkd_exception()
    {
        await Assert.ThrowsAsync<BarkdException>(() => Unconfigured(new FakeBarkd()).CreateInvoiceAsync(1000, "x"));
    }

    [Fact]
    public async Task CreateInvoice_when_barkd_is_down_throws_barkd_exception()
    {
        var fake = new FakeBarkd { Down = true };
        await Assert.ThrowsAsync<BarkdException>(() => Configured(fake).CreateInvoiceAsync(1000, "x"));
    }

    // ── GetReceiveStatusAsync ───────────────────────────────────────────────────────────────────────
    [Fact]
    public async Task GetReceiveStatus_pending_then_settled()
    {
        var fake = new FakeBarkd();
        var client = Configured(fake);
        var inv = await client.CreateInvoiceAsync(1000, "x");

        var pending = await client.GetReceiveStatusAsync(inv.PaymentHash);
        Assert.True(pending.Found);
        Assert.False(pending.Settled);

        fake.Settle(inv.PaymentHash);
        var settled = await client.GetReceiveStatusAsync(inv.PaymentHash);
        Assert.True(settled.Found);
        Assert.True(settled.Settled);
        Assert.NotNull(settled.FinishedAt);
    }

    [Fact]
    public async Task GetReceiveStatus_canceled_is_found_but_not_settled()
    {
        var fake = new FakeBarkd();
        var client = Configured(fake);
        var inv = await client.CreateInvoiceAsync(1000, "x");
        fake.CancelReceive(inv.PaymentHash); // finished_at set, preimage NOT revealed
        var st = await client.GetReceiveStatusAsync(inv.PaymentHash);
        Assert.True(st.Found);
        Assert.False(st.Settled); // canceled — finished but no revealed preimage
        Assert.NotNull(st.FinishedAt);
    }

    [Fact]
    public async Task GetReceiveStatus_not_found()
    {
        var st = await Configured(new FakeBarkd()).GetReceiveStatusAsync("deadbeef");
        Assert.False(st.Found);
        Assert.False(st.Settled);
    }

    [Fact]
    public async Task GetReceiveStatus_when_down_throws()
    {
        var fake = new FakeBarkd { Down = true };
        await Assert.ThrowsAsync<BarkdException>(() => Configured(fake).GetReceiveStatusAsync("x"));
    }

    // ── GetWalletInfoAsync ──────────────────────────────────────────────────────────────────────────
    [Fact]
    public async Task GetWalletInfo_reachable_returns_balance()
    {
        var fake = new FakeBarkd { SpendableSat = 77_000 };
        var w = await Configured(fake).GetWalletInfoAsync();
        Assert.True(w.Reachable);
        Assert.Equal(77_000, w.SpendableSat);
    }

    [Fact]
    public async Task GetWalletInfo_unreachable_when_down()
    {
        var fake = new FakeBarkd { Down = true };
        var w = await Configured(fake).GetWalletInfoAsync();
        Assert.False(w.Reachable);
        Assert.NotNull(w.Raw); // carries the failure message
    }

    [Fact]
    public async Task GetWalletInfo_unconfigured_is_unreachable_without_calling()
    {
        var w = await Unconfigured(new FakeBarkd()).GetWalletInfoAsync();
        Assert.False(w.Reachable);
        Assert.Equal(0, w.SpendableSat);
    }

    // ── PayInvoiceAsync ─────────────────────────────────────────────────────────────────────────────
    [Fact]
    public async Task PayInvoice_success_returns_the_barkd_message()
    {
        var fake = new FakeBarkd();
        var result = await Configured(fake).PayInvoiceAsync("lnbc500n1pdummy", null, "tip");
        Assert.Equal("payment sent", result.Message);
        Assert.Equal(1, fake.PayCalls);
    }

    [Fact]
    public async Task PayInvoice_non_json_2xx_is_treated_as_success()
    {
        var fake = new FakeBarkd { PayReturnsNonJson = true };
        var result = await Configured(fake).PayInvoiceAsync("lnbc500n1pdummy", 50, null);
        Assert.Equal("paid", result.Message); // default success text when the body isn't JSON
    }

    [Fact]
    public async Task PayInvoice_non_2xx_throws()
    {
        var fake = new FakeBarkd { ForceErrorStatus = System.Net.HttpStatusCode.BadGateway };
        await Assert.ThrowsAsync<BarkdException>(() =>
            Configured(fake).PayInvoiceAsync("lnbc500n1pdummy", null, null));
    }

    [Fact]
    public async Task PayInvoice_when_down_throws()
    {
        var fake = new FakeBarkd { Down = true };
        await Assert.ThrowsAsync<BarkdException>(() =>
            Configured(fake).PayInvoiceAsync("lnbc500n1pdummy", null, null));
    }

    [Fact]
    public async Task PayInvoice_unconfigured_throws()
    {
        await Assert.ThrowsAsync<BarkdException>(() =>
            Unconfigured(new FakeBarkd()).PayInvoiceAsync("lnbc500n1pdummy", null, null));
    }

    // ── HTTP error branches (non-2xx from barkd, transport OK) ───────────────────────────────────────
    [Fact]
    public async Task CreateInvoice_non_2xx_throws()
    {
        var fake = new FakeBarkd { ForceErrorStatus = System.Net.HttpStatusCode.InternalServerError };
        await Assert.ThrowsAsync<BarkdException>(() => Configured(fake).CreateInvoiceAsync(1000, "x"));
    }

    [Fact]
    public async Task GetReceiveStatus_non_2xx_non_404_throws()
    {
        var fake = new FakeBarkd { ForceErrorStatus = System.Net.HttpStatusCode.BadGateway };
        await Assert.ThrowsAsync<BarkdException>(() => Configured(fake).GetReceiveStatusAsync("hash"));
    }

    [Fact]
    public async Task ListMovements_non_2xx_throws()
    {
        var fake = new FakeBarkd { ForceErrorStatus = System.Net.HttpStatusCode.ServiceUnavailable };
        await Assert.ThrowsAsync<BarkdException>(() => Configured(fake).ListLightningMovementsAsync(100));
    }

    [Fact]
    public async Task GetWalletInfo_non_2xx_is_unreachable_with_raw()
    {
        var fake = new FakeBarkd { ForceErrorStatus = System.Net.HttpStatusCode.Unauthorized };
        var w = await Configured(fake).GetWalletInfoAsync();
        Assert.False(w.Reachable);
        Assert.NotNull(w.Raw);
    }

    // ── ListLightningMovements (down path) ──────────────────────────────────────────────────────────
    [Fact]
    public async Task ListMovements_when_down_throws()
    {
        var fake = new FakeBarkd { Down = true };
        await Assert.ThrowsAsync<BarkdException>(() => Configured(fake).ListLightningMovementsAsync(100));
    }

    // ── Bolt11PaymentHash (the internal BOLT11 'p' field parser) ─────────────────────────────────────
    [Fact]
    public void Bolt11PaymentHash_parses_a_real_invoice()
    {
        // A real mainnet invoice (same one FakeBarkd's movement log uses) — the 'p' field yields a 64-hex hash.
        const string inv = "lnbc100n1p4rx33hsp56ac4u3msrhpjz5vsphe254ut2jhd3u6aughecu6kyaml2h3fn2zqpp544wavhsx7x8kd9ct97whv6trqr2gfel8cn9ck6v2qvyr0dzz4a0qdzqwpshjsrnvd5xummjwghx6ef6yp2x2um5yp6x7grzv9exkepqf38zqctyv3ex2umnxqy9gcqcqzxg9qyysgq0fy3fcr0ez39jr7epm82rqajqj69khd98awqx2ln8g67nagsmvkrq5tp54x7eks8t8lc30edtqcnv9msern9tuxtgf3vdxfgv9a7hwsqpfw2wm";
        var hash = BarkdClient.Bolt11PaymentHash(inv);
        Assert.NotNull(hash);
        Assert.Equal(64, hash!.Length);
    }

    [Theory]
    [InlineData("not-an-invoice")]
    [InlineData("lnbc1")]
    [InlineData("")]
    public void Bolt11PaymentHash_returns_null_on_garbage(string inv)
    {
        Assert.Null(BarkdClient.Bolt11PaymentHash(inv));
    }
}
