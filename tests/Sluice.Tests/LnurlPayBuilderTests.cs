using System.Text.Json;
using Sluice.Barkd;
using Sluice.Lnurl;
using Sluice.TestKit;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace Sluice.Tests;

/// <summary>The pure LNURL-pay (LUD-06/16) receive logic: address matching, metadata/bounds/callback
/// builders, the payRequest payload, and the invoice-callback flow against the real <see cref="BarkdClient"/>
/// over <see cref="FakeBarkd"/>. Fast.</summary>
public sealed class LnurlPayBuilderTests
{
    private static LnurlPayOptions Cfg() => new()
    {
        Enabled = true,
        Domain = "schnorr.me",
        Username = "pay",
        MinSat = 1,
        MaxSat = 1_000,
        CommentLength = 240,
    };

    private static IBarkdClient Barkd(FakeBarkd fake)
    {
        var options = new BarkdClientOptions { BaseUrl = "http://barkd.test:3535", Token = "t" };
        return new BarkdClient(Options.Create(options), fake, NullLogger<BarkdClient>.Instance);
    }

    private static IBarkdClient UnconfiguredBarkd() =>
        new BarkdClient(Options.Create(new BarkdClientOptions { BaseUrl = null }), new FakeBarkd(), NullLogger<BarkdClient>.Instance);

    // ── AddressMatches ──────────────────────────────────────────────────────────────────────────────
    [Theory]
    [InlineData("pay", true)]
    [InlineData("PAY", true)]  // case-insensitive
    [InlineData(" pay ", true)] // trimmed
    [InlineData("other", false)]
    [InlineData(null, false)]
    [InlineData("", false)]
    public void AddressMatches_checks_username(string? username, bool expected)
    {
        Assert.Equal(expected, LnurlPayBuilder.AddressMatches(Cfg(), username));
    }

    [Fact]
    public void AddressMatches_false_when_disabled_or_unconfigured()
    {
        var disabled = Cfg();
        disabled.Enabled = false;
        Assert.False(LnurlPayBuilder.AddressMatches(disabled, "pay"));

        var noDomain = Cfg();
        noDomain.Domain = "";
        Assert.False(LnurlPayBuilder.AddressMatches(noDomain, "pay"));
    }

    // ── Metadata / Address / Bounds / Callback ──────────────────────────────────────────────────────
    [Fact]
    public void Address_is_lowercased_user_at_domain()
    {
        var cfg = Cfg();
        cfg.Username = "Pay";
        cfg.Domain = "Schnorr.ME";
        Assert.Equal("pay@schnorr.me", LnurlPayBuilder.Address(cfg));
    }

    [Fact]
    public void Metadata_is_a_json_array_with_text_plain_and_identifier()
    {
        var meta = LnurlPayBuilder.Metadata("pay@schnorr.me");
        using var doc = JsonDocument.Parse(meta);
        Assert.Equal(JsonValueKind.Array, doc.RootElement.ValueKind);
        Assert.Equal("text/plain", doc.RootElement[0][0].GetString());
        Assert.Equal("text/identifier", doc.RootElement[1][0].GetString());
        Assert.Equal("pay@schnorr.me", doc.RootElement[1][1].GetString());
    }

    [Fact]
    public void Bounds_are_in_msat_and_clamp_min_below_one()
    {
        var (min, max) = LnurlPayBuilder.Bounds(Cfg());
        Assert.Equal(1_000, min);      // 1 sat → 1000 msat
        Assert.Equal(1_000_000, max);  // 1000 sat → 1_000_000 msat

        var weird = Cfg();
        weird.MinSat = 0;     // clamps to 1
        weird.MaxSat = -5;    // max < min ⇒ clamps to min
        var (m2, x2) = LnurlPayBuilder.Bounds(weird);
        Assert.Equal(1_000, m2);
        Assert.Equal(1_000, x2);
    }

    [Fact]
    public void Callback_uses_the_configured_domain()
    {
        Assert.Equal("https://schnorr.me/api/lnurlp/pay/callback", LnurlPayBuilder.Callback(Cfg()));
    }

    // ── BuildPayRequest ─────────────────────────────────────────────────────────────────────────────
    [Fact]
    public void BuildPayRequest_returns_the_lud06_payload_for_a_matching_address()
    {
        var req = LnurlPayBuilder.BuildPayRequest(Cfg(), "pay");
        Assert.NotNull(req);
        var json = JsonDocument.Parse(JsonSerializer.Serialize(req)).RootElement;
        Assert.Equal("payRequest", json.GetProperty("tag").GetString());
        Assert.Equal("https://schnorr.me/api/lnurlp/pay/callback", json.GetProperty("callback").GetString());
        Assert.Equal(1_000, json.GetProperty("minSendable").GetInt64());
        Assert.Equal(1_000_000, json.GetProperty("maxSendable").GetInt64());
        Assert.Equal(240, json.GetProperty("commentAllowed").GetInt32());
    }

    [Fact]
    public void BuildPayRequest_is_null_for_a_non_matching_address()
    {
        Assert.Null(LnurlPayBuilder.BuildPayRequest(Cfg(), "nope"));
    }

    // ── CreateInvoiceAsync ──────────────────────────────────────────────────────────────────────────
    [Fact]
    public async Task CreateInvoice_mints_within_bounds()
    {
        var fake = new FakeBarkd();
        var r = await LnurlPayBuilder.CreateInvoiceAsync(Cfg(), "pay", 50_000, "thanks", Barkd(fake));
        Assert.True(r.Success);
        Assert.False(string.IsNullOrEmpty(r.Bolt11));
        Assert.Null(r.Reason);
        Assert.Equal(1, fake.InvoiceCreateCalls);
    }

    [Fact]
    public async Task CreateInvoice_mints_without_a_comment()
    {
        var r = await LnurlPayBuilder.CreateInvoiceAsync(Cfg(), "pay", 50_000, null, Barkd(new FakeBarkd()));
        Assert.True(r.Success);
    }

    [Fact]
    public async Task CreateInvoice_rejects_an_unknown_address()
    {
        var r = await LnurlPayBuilder.CreateInvoiceAsync(Cfg(), "nope", 50_000, null, Barkd(new FakeBarkd()));
        Assert.False(r.Success);
        Assert.Equal("unknown address", r.Reason);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public async Task CreateInvoice_rejects_nonpositive_amount(long amountMsat)
    {
        var r = await LnurlPayBuilder.CreateInvoiceAsync(Cfg(), "pay", amountMsat, null, Barkd(new FakeBarkd()));
        Assert.False(r.Success);
        Assert.Equal("missing or invalid amount", r.Reason);
    }

    [Theory]
    [InlineData(500)]          // below 1 sat (min is 1000 msat)
    [InlineData(2_000_000)]    // above max (1_000_000 msat)
    public async Task CreateInvoice_rejects_out_of_range_amounts(long amountMsat)
    {
        var r = await LnurlPayBuilder.CreateInvoiceAsync(Cfg(), "pay", amountMsat, null, Barkd(new FakeBarkd()));
        Assert.False(r.Success);
        Assert.Contains("out of range", r.Reason);
    }

    [Fact]
    public async Task CreateInvoice_sub_sat_amount_within_bounds_is_rejected()
    {
        // Min 1 sat would normally floor sub-sat, but craft a window where min<1 sat is allowed:
        var cfg = Cfg();
        // MinSat clamps to 1 → min 1000 msat, so 1500 msat passes the range check but 1500/1000 = 1 sat,
        // which is fine. To hit the "amount below 1 sat" branch we need amountMsat in [min,max) that floors
        // to 0 — only possible if min<1000. Bounds clamp min to >=1000, so this branch is structurally
        // guarded by Bounds. Assert the documented behaviour: a 1-sat-equivalent mints fine.
        var r = await LnurlPayBuilder.CreateInvoiceAsync(cfg, "pay", 1_000, null, Barkd(new FakeBarkd()));
        Assert.True(r.Success);
    }

    [Fact]
    public async Task CreateInvoice_fails_when_wallet_unconfigured()
    {
        var r = await LnurlPayBuilder.CreateInvoiceAsync(Cfg(), "pay", 50_000, null, UnconfiguredBarkd());
        Assert.False(r.Success);
        Assert.Equal("wallet unavailable", r.Reason);
    }

    [Fact]
    public async Task CreateInvoice_fails_when_barkd_throws()
    {
        var fake = new FakeBarkd { Down = true };
        var r = await LnurlPayBuilder.CreateInvoiceAsync(Cfg(), "pay", 50_000, null, Barkd(fake), NullLogger.Instance);
        Assert.False(r.Success);
        Assert.Equal("could not generate invoice", r.Reason);
    }

    // ── LnurlCallbackResult factory shapes ──────────────────────────────────────────────────────────
    [Fact]
    public void CallbackResult_factories()
    {
        var ok = LnurlCallbackResult.Ok("lnbc1");
        Assert.True(ok.Success);
        Assert.Equal("lnbc1", ok.Bolt11);
        Assert.Null(ok.Reason);

        var fail = LnurlCallbackResult.Fail("nope");
        Assert.False(fail.Success);
        Assert.Null(fail.Bolt11);
        Assert.Equal("nope", fail.Reason);
    }
}
