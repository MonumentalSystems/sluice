using System.Text.Json;
using Sluice.Barkd;
using Sluice.Nostr;
using Sluice.Nwc;
using Sluice.TestKit;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace Sluice.Tests;

/// <summary>The NWC method dispatch — every <see cref="NwcBridge.DispatchAsync"/> switch branch and its
/// error sub-branches, driven through the real <see cref="BarkdClient"/> over <see cref="FakeBarkd"/>. Plus
/// the <see cref="NwcBridge.ExecuteAsync"/> early-return guards, <see cref="NwcBridge.ParseEvent"/>, and
/// <see cref="NwcBridge.InfoEvent"/>. Fast — no live relay.</summary>
public sealed class NwcBridgeTests
{
    // private key 1 / 2 — well-known vectors, valid 64-hex.
    private const string ServicePriv = "0000000000000000000000000000000000000000000000000000000000000001";
    private const string ClientSecret = "1111111111111111111111111111111111111111111111111111111111111111";

    private static IBarkdClient BarkdOver(FakeBarkd fake)
    {
        var options = new BarkdClientOptions { BaseUrl = "http://barkd.test:3535", Token = "t" };
        return new BarkdClient(Options.Create(options), fake, NullLogger<BarkdClient>.Instance);
    }

    private static NwcBridge Bridge(NwcConfig cfg, IBarkdClient barkd) =>
        new(cfg, barkd, NullLogger<NwcBridge>.Instance);

    private static NwcConfig ReceiveOnlyCfg() => new()
    {
        Enabled = true,
        PrivateKeyHex = ServicePriv,
        Relays = { "wss://relay.example" },
        ConnectionSecrets = { ClientSecret },
        WalletName = "barkd-test",
        MaxInvoiceSat = 1_000_000,
        // MaxDailyPaySat unset (0) ⇒ pay_invoice disabled
    };

    private static NwcConfig PayCfg(long capSat) =>
        new()
        {
            Enabled = true,
            PrivateKeyHex = ServicePriv,
            Relays = { "wss://relay.example" },
            ConnectionSecrets = { ClientSecret },
            MaxDailyPaySat = capSat,
        };

    private static JsonElement Params(string json) => JsonDocument.Parse(json).RootElement;
    private static JsonElement NoParams() => default;

    // Serialize the dispatch result to a JsonElement so branches can be asserted by shape.
    private static async Task<JsonElement> Dispatch(NwcBridge bridge, string method, JsonElement prm)
    {
        var result = await bridge.DispatchAsync(method, prm, default);
        var json = JsonSerializer.Serialize(result, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        return JsonDocument.Parse(json).RootElement;
    }

    private static string? ErrCode(JsonElement r) =>
        r.TryGetProperty("error", out var e) && e.ValueKind == JsonValueKind.Object && e.TryGetProperty("code", out var c)
            ? c.GetString() : null;

    private static JsonElement Result(JsonElement r) => r.GetProperty("result");

    // ── get_info ────────────────────────────────────────────────────────────────────────────────────
    [Fact]
    public async Task get_info_reports_alias_network_methods_pubkey()
    {
        // ExecuteAsync normally sets _servicePub, but dispatch must run standalone: get_info still works,
        // pubkey is whatever was derived (empty here, since ExecuteAsync hasn't run) — the shape is the point.
        var bridge = Bridge(ReceiveOnlyCfg(), BarkdOver(new FakeBarkd()));
        var r = await Dispatch(bridge, "get_info", NoParams());
        Assert.Equal("get_info", r.GetProperty("result_type").GetString());
        var res = Result(r);
        Assert.Equal("barkd-test", res.GetProperty("alias").GetString());
        Assert.Equal("mainnet", res.GetProperty("network").GetString());
        var methods = res.GetProperty("methods").EnumerateArray().Select(m => m.GetString()).ToList();
        Assert.Contains("make_invoice", methods);
        Assert.DoesNotContain("pay_invoice", methods); // receive-only
    }

    [Fact]
    public async Task get_info_advertises_pay_invoice_when_capped()
    {
        var bridge = Bridge(PayCfg(1000), BarkdOver(new FakeBarkd()));
        var r = await Dispatch(bridge, "get_info", NoParams());
        var methods = Result(r).GetProperty("methods").EnumerateArray().Select(m => m.GetString()).ToList();
        Assert.Contains("pay_invoice", methods);
    }

    // ── get_balance ─────────────────────────────────────────────────────────────────────────────────
    [Fact]
    public async Task get_balance_returns_msat_when_reachable()
    {
        var fake = new FakeBarkd { SpendableSat = 5_000 };
        var bridge = Bridge(ReceiveOnlyCfg(), BarkdOver(fake));
        var r = await Dispatch(bridge, "get_balance", NoParams());
        Assert.Equal(5_000_000, Result(r).GetProperty("balance").GetInt64()); // msat
    }

    [Fact]
    public async Task get_balance_errors_when_unreachable()
    {
        var fake = new FakeBarkd { Down = true };
        var bridge = Bridge(ReceiveOnlyCfg(), BarkdOver(fake));
        var r = await Dispatch(bridge, "get_balance", NoParams());
        Assert.Equal("INTERNAL", ErrCode(r));
    }

    // ── make_invoice ────────────────────────────────────────────────────────────────────────────────
    [Fact]
    public async Task make_invoice_mints_and_returns_the_invoice()
    {
        var fake = new FakeBarkd();
        var bridge = Bridge(ReceiveOnlyCfg(), BarkdOver(fake));
        var r = await Dispatch(bridge, "make_invoice", Params("""{"amount":21000,"description":"coffee"}"""));
        var res = Result(r);
        Assert.Equal("incoming", res.GetProperty("type").GetString());
        Assert.False(string.IsNullOrEmpty(res.GetProperty("invoice").GetString()));
        Assert.False(string.IsNullOrEmpty(res.GetProperty("payment_hash").GetString()));
        Assert.Equal(21000, res.GetProperty("amount").GetInt64()); // 21 sat → 21000 msat
        Assert.Equal("coffee", res.GetProperty("description").GetString());
        Assert.Equal(1, fake.InvoiceCreateCalls);
    }

    [Fact]
    public async Task make_invoice_defaults_description_when_blank()
    {
        var bridge = Bridge(ReceiveOnlyCfg(), BarkdOver(new FakeBarkd()));
        var r = await Dispatch(bridge, "make_invoice", Params("""{"amount":1000}"""));
        Assert.Equal("incoming", Result(r).GetProperty("type").GetString());
    }

    [Theory]
    [InlineData("""{"description":"no amount"}""")] // missing amount
    [InlineData("""{"amount":0}""")]                // zero
    [InlineData("""{"amount":-5000}""")]            // negative
    public async Task make_invoice_rejects_missing_or_nonpositive_amount(string prm)
    {
        var bridge = Bridge(ReceiveOnlyCfg(), BarkdOver(new FakeBarkd()));
        var r = await Dispatch(bridge, "make_invoice", Params(prm));
        Assert.Equal("OTHER", ErrCode(r));
    }

    [Fact]
    public async Task make_invoice_rejects_sub_sat_amount()
    {
        // 500 msat ⇒ 0 sat after integer division ⇒ "amount below 1 sat".
        var bridge = Bridge(ReceiveOnlyCfg(), BarkdOver(new FakeBarkd()));
        var r = await Dispatch(bridge, "make_invoice", Params("""{"amount":500}"""));
        Assert.Equal("OTHER", ErrCode(r));
    }

    [Fact]
    public async Task make_invoice_rejects_amount_over_the_cap()
    {
        var cfg = ReceiveOnlyCfg();
        cfg.MaxInvoiceSat = 100;
        var bridge = Bridge(cfg, BarkdOver(new FakeBarkd()));
        var r = await Dispatch(bridge, "make_invoice", Params("""{"amount":200000}""")); // 200 sat > 100
        Assert.Equal("OTHER", ErrCode(r));
    }

    // ── lookup_invoice ──────────────────────────────────────────────────────────────────────────────
    [Fact]
    public async Task lookup_invoice_returns_found_pending()
    {
        var fake = new FakeBarkd();
        var bridge = Bridge(ReceiveOnlyCfg(), BarkdOver(fake));
        await Dispatch(bridge, "make_invoice", Params("""{"amount":10000}"""));
        var hash = fake.LastPaymentHash!;

        var r = await Dispatch(bridge, "lookup_invoice", Params($$"""{"payment_hash":"{{hash}}"}"""));
        var res = Result(r);
        Assert.Equal("incoming", res.GetProperty("type").GetString());
        Assert.Equal(hash, res.GetProperty("payment_hash").GetString());
        Assert.Equal(JsonValueKind.Null, res.GetProperty("settled_at").ValueKind); // not settled yet
    }

    [Fact]
    public async Task lookup_invoice_reports_settled_at_once_paid()
    {
        var fake = new FakeBarkd();
        var bridge = Bridge(ReceiveOnlyCfg(), BarkdOver(fake));
        await Dispatch(bridge, "make_invoice", Params("""{"amount":10000}"""));
        var hash = fake.LastPaymentHash!;
        fake.Settle(hash);

        var r = await Dispatch(bridge, "lookup_invoice", Params($$"""{"payment_hash":"{{hash}}"}"""));
        Assert.True(Result(r).GetProperty("settled_at").GetInt64() > 0);
    }

    [Fact]
    public async Task lookup_invoice_not_found()
    {
        var bridge = Bridge(ReceiveOnlyCfg(), BarkdOver(new FakeBarkd()));
        var r = await Dispatch(bridge, "lookup_invoice", Params("""{"payment_hash":"deadbeef"}"""));
        Assert.Equal("NOT_FOUND", ErrCode(r));
    }

    [Theory]
    [InlineData("""{}""")]                      // missing
    [InlineData("""{"payment_hash":""}""")]     // blank
    [InlineData("""{"payment_hash":"   "}""")]  // whitespace
    public async Task lookup_invoice_requires_a_hash(string prm)
    {
        var bridge = Bridge(ReceiveOnlyCfg(), BarkdOver(new FakeBarkd()));
        var r = await Dispatch(bridge, "lookup_invoice", Params(prm));
        Assert.Equal("OTHER", ErrCode(r));
    }

    // ── list_transactions ───────────────────────────────────────────────────────────────────────────
    [Fact]
    public async Task list_transactions_returns_both_lightning_legs()
    {
        var bridge = Bridge(ReceiveOnlyCfg(), BarkdOver(new FakeBarkd()));
        var r = await Dispatch(bridge, "list_transactions", NoParams());
        var txs = Result(r).GetProperty("transactions");
        Assert.Equal(2, txs.GetArrayLength()); // round is filtered out by the client
        // amounts are surfaced as msat
        var first = txs[0];
        Assert.True(first.GetProperty("amount").GetInt64() > 0);
    }

    [Fact]
    public async Task list_transactions_filters_by_type()
    {
        var bridge = Bridge(ReceiveOnlyCfg(), BarkdOver(new FakeBarkd()));
        var r = await Dispatch(bridge, "list_transactions", Params("""{"type":"outgoing"}"""));
        var txs = Result(r).GetProperty("transactions");
        Assert.Equal(1, txs.GetArrayLength());
        Assert.Equal("outgoing", txs[0].GetProperty("type").GetString());
    }

    [Fact]
    public async Task list_transactions_honours_limit_and_offset()
    {
        var bridge = Bridge(ReceiveOnlyCfg(), BarkdOver(new FakeBarkd()));
        var limited = await Dispatch(bridge, "list_transactions", Params("""{"limit":1}"""));
        Assert.Equal(1, Result(limited).GetProperty("transactions").GetArrayLength());

        var offset = await Dispatch(bridge, "list_transactions", Params("""{"offset":1}"""));
        Assert.Equal(1, Result(offset).GetProperty("transactions").GetArrayLength());

        var past = await Dispatch(bridge, "list_transactions", Params("""{"offset":99}"""));
        Assert.Equal(0, Result(past).GetProperty("transactions").GetArrayLength());
    }

    [Fact]
    public async Task list_transactions_filters_by_from_and_until()
    {
        var bridge = Bridge(ReceiveOnlyCfg(), BarkdOver(new FakeBarkd()));
        // The fake movements are dated mid-June 2026. A from far in the future drops everything;
        // an until far in the past also drops everything.
        var future = DateTimeOffset.Parse("2030-01-01T00:00:00Z").ToUnixTimeSeconds();
        var fromFuture = await Dispatch(bridge, "list_transactions", Params($$"""{"from":{{future}}}"""));
        Assert.Equal(0, Result(fromFuture).GetProperty("transactions").GetArrayLength());

        var past = DateTimeOffset.Parse("2000-01-01T00:00:00Z").ToUnixTimeSeconds();
        var untilPast = await Dispatch(bridge, "list_transactions", Params($$"""{"until":{{past}}}"""));
        Assert.Equal(0, Result(untilPast).GetProperty("transactions").GetArrayLength());

        // A wide window keeps both.
        var wide = await Dispatch(bridge, "list_transactions", Params($$"""{"from":{{past}},"until":{{future}}}"""));
        Assert.Equal(2, Result(wide).GetProperty("transactions").GetArrayLength());
    }

    // ── pay_invoice ─────────────────────────────────────────────────────────────────────────────────
    [Fact]
    public async Task pay_invoice_disabled_when_no_cap()
    {
        var bridge = Bridge(ReceiveOnlyCfg(), BarkdOver(new FakeBarkd()));
        var r = await Dispatch(bridge, "pay_invoice", Params("""{"invoice":"lnbc10n1pdummy"}"""));
        Assert.Equal("NOT_IMPLEMENTED", ErrCode(r));
    }

    [Theory]
    [InlineData("""{}""")]                 // missing invoice
    [InlineData("""{"invoice":""}""")]     // blank
    public async Task pay_invoice_requires_an_invoice(string prm)
    {
        var bridge = Bridge(PayCfg(1_000_000), BarkdOver(new FakeBarkd()));
        var r = await Dispatch(bridge, "pay_invoice", Params(prm));
        Assert.Equal("OTHER", ErrCode(r));
    }

    [Fact]
    public async Task pay_invoice_amountless_without_amount_is_rejected()
    {
        // lnbc1p… = amountless (Bolt11.AmountMsat null) and no amount param ⇒ "amount required".
        var bridge = Bridge(PayCfg(1_000_000), BarkdOver(new FakeBarkd()));
        var r = await Dispatch(bridge, "pay_invoice", Params("""{"invoice":"lnbc1pamountlessdummy"}"""));
        Assert.Equal("OTHER", ErrCode(r));
    }

    [Fact]
    public async Task pay_invoice_over_the_daily_cap_is_quota_exceeded()
    {
        // 50-sat invoice against a 10-sat cap ⇒ reserve fails.
        var bridge = Bridge(PayCfg(10), BarkdOver(new FakeBarkd()));
        var r = await Dispatch(bridge, "pay_invoice", Params("""{"invoice":"lnbc500n1p4rx07fdummy"}"""));
        Assert.Equal("QUOTA_EXCEEDED", ErrCode(r));
    }

    [Fact]
    public async Task pay_invoice_under_cap_then_barkd_failure_refunds_and_reports_payment_failed()
    {
        // barkd rejects the pay (non-2xx) ⇒ BarkdException ⇒ the bridge refunds the reservation and returns
        // PAYMENT_FAILED. A 50-sat invoice fits the 1000-sat cap, so the reserve succeeds first.
        var fake = new FakeBarkd { ForceErrorStatus = System.Net.HttpStatusCode.BadGateway };
        var bridge = Bridge(PayCfg(1_000), BarkdOver(fake));
        var r = await Dispatch(bridge, "pay_invoice", Params("""{"invoice":"lnbc500n1p4rx07fdummy"}"""));
        Assert.Equal("PAYMENT_FAILED", ErrCode(r));
    }

    [Fact]
    public async Task pay_invoice_amountless_with_amount_uses_the_param_then_fails_on_barkd()
    {
        // Amountless invoice + explicit amount param ⇒ the cap reads the param; barkd rejects ⇒ PAYMENT_FAILED.
        var fake = new FakeBarkd { ForceErrorStatus = System.Net.HttpStatusCode.BadGateway };
        var bridge = Bridge(PayCfg(1_000), BarkdOver(fake));
        var r = await Dispatch(bridge, "pay_invoice", Params("""{"invoice":"lnbc1pamountlessdummy","amount":50000}"""));
        Assert.Equal("PAYMENT_FAILED", ErrCode(r));
    }

    [Fact]
    public async Task pay_invoice_succeeds_within_cap_and_returns_empty_preimage()
    {
        // A barkd that actually pays ⇒ the success branch: reserve, pay, return {preimage:""}.
        var stub = new PayingBarkd();
        var bridge = Bridge(PayCfg(1_000), stub);
        var r = await Dispatch(bridge, "pay_invoice", Params("""{"invoice":"lnbc500n1p4rx07fdummy"}"""));
        Assert.Equal("pay_invoice", r.GetProperty("result_type").GetString());
        Assert.Equal("", Result(r).GetProperty("preimage").GetString());
        Assert.Equal(50, stub.LastAmountSat); // 50-sat invoice; amount read from the BOLT11 (param null)
        Assert.Null(stub.LastExplicitAmountSat); // amounted invoice ⇒ barkd reads it, no explicit amount
    }

    [Fact]
    public async Task pay_invoice_amountless_with_amount_succeeds_and_passes_the_amount()
    {
        var stub = new PayingBarkd();
        var bridge = Bridge(PayCfg(1_000), stub);
        var r = await Dispatch(bridge, "pay_invoice", Params("""{"invoice":"lnbc1pamountlessdummy","amount":50000}"""));
        Assert.Equal("", Result(r).GetProperty("preimage").GetString());
        Assert.Equal(50, stub.LastExplicitAmountSat); // amountless ⇒ explicit amount forwarded to barkd
    }

    /// <summary>A minimal IBarkdClient whose pay succeeds — to reach the pay_invoice success branch (FakeBarkd
    /// has no /lightning/pay route, so the real client can only exercise the failure path).</summary>
    private sealed class PayingBarkd : IBarkdClient
    {
        public long LastAmountSat { get; private set; }
        public long? LastExplicitAmountSat { get; private set; }
        public bool IsConfigured => true;
        public Task<BarkdPayResult> PayInvoiceAsync(string destination, long? amountSat, string? comment, CancellationToken ct = default)
        {
            LastExplicitAmountSat = amountSat;
            // Track the cap-relevant sat amount: explicit when given, else parsed from the invoice.
            LastAmountSat = amountSat ?? (Bolt11.AmountMsat(destination) is { } m ? (m + 999) / 1000 : 0);
            return Task.FromResult(new BarkdPayResult("paid"));
        }
        public Task<BarkdInvoice> CreateInvoiceAsync(long a, string d, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<BarkdReceiveStatus> GetReceiveStatusAsync(string h, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<BarkdWalletInfo> GetWalletInfoAsync(CancellationToken ct = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<BarkdMovement>> ListLightningMovementsAsync(int max, CancellationToken ct = default) => throw new NotSupportedException();
    }

    // ── unknown ─────────────────────────────────────────────────────────────────────────────────────
    [Fact]
    public async Task unknown_method_is_not_implemented()
    {
        var bridge = Bridge(ReceiveOnlyCfg(), BarkdOver(new FakeBarkd()));
        var r = await Dispatch(bridge, "frobnicate", NoParams());
        Assert.Equal("NOT_IMPLEMENTED", ErrCode(r));
    }

    // ── ExecuteAsync early-return guards (StartAsync/StopAsync must not connect to a relay) ───────────
    private static async Task RunGuard(NwcConfig cfg)
    {
        var bridge = Bridge(cfg, BarkdOver(new FakeBarkd()));
        using var cts = new CancellationTokenSource();
        await bridge.StartAsync(cts.Token); // returns immediately for a disabled/invalid config
        await bridge.StopAsync(cts.Token);
    }

    [Fact]
    public async Task guard_disabled_returns_without_connecting()
    {
        var cfg = ReceiveOnlyCfg();
        cfg.Enabled = false;
        await RunGuard(cfg);
    }

    [Fact]
    public async Task guard_bad_private_key_length_returns()
    {
        var cfg = ReceiveOnlyCfg();
        cfg.PrivateKeyHex = "abcd"; // not 64 hex
        await RunGuard(cfg);
    }

    [Fact]
    public async Task guard_no_relays_or_secrets_returns()
    {
        var noRelays = ReceiveOnlyCfg();
        noRelays.Relays = new();
        await RunGuard(noRelays);

        var noSecrets = ReceiveOnlyCfg();
        noSecrets.ConnectionSecrets = new();
        await RunGuard(noSecrets);
    }

    [Fact]
    public async Task guard_bad_key_material_returns()
    {
        var cfg = ReceiveOnlyCfg();
        // 64 chars but not valid hex ⇒ PubKeyHex throws ⇒ the bad-key-material catch returns.
        cfg.PrivateKeyHex = new string('z', 64);
        await RunGuard(cfg);
    }

    // ── ParseEvent ──────────────────────────────────────────────────────────────────────────────────
    [Fact]
    public void ParseEvent_reads_a_full_event_with_tags()
    {
        var signed = NwcCrypto.Sign(ClientSecret, new NostrEvent
        {
            CreatedAt = 1_700_000_000,
            Kind = 23194,
            Tags = new List<string[]> { new[] { "p", "abc" }, new[] { "e", "def" } },
            Content = "hello",
        });
        var json = JsonSerializer.Serialize(signed, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        var el = JsonDocument.Parse(json).RootElement;

        var bridge = Bridge(ReceiveOnlyCfg(), BarkdOver(new FakeBarkd()));
        var parsed = bridge.ParseEvent(el);
        Assert.NotNull(parsed);
        Assert.Equal(signed.Id, parsed!.Id);
        Assert.Equal(signed.Pubkey, parsed.Pubkey);
        Assert.Equal(23194, parsed.Kind);
        Assert.Equal("hello", parsed.Content);
        Assert.Equal(2, parsed.Tags.Count);
        Assert.True(NwcCrypto.Verify(parsed)); // round-trips back to a verifiable event
    }

    [Fact]
    public void ParseEvent_returns_null_on_malformed_input()
    {
        var bridge = Bridge(ReceiveOnlyCfg(), BarkdOver(new FakeBarkd()));
        // Missing the required "id" property ⇒ GetProperty throws ⇒ caught ⇒ null.
        var el = JsonDocument.Parse("""{"pubkey":"x"}""").RootElement;
        Assert.Null(bridge.ParseEvent(el));
    }

    // ── InfoEvent ───────────────────────────────────────────────────────────────────────────────────
    [Fact]
    public void InfoEvent_is_a_signed_kind_13194_with_methods_content()
    {
        var bridge = Bridge(ReceiveOnlyCfg(), BarkdOver(new FakeBarkd()));
        var evt = bridge.InfoEvent();
        Assert.Equal(13194, evt.Kind);
        Assert.Equal(64, evt.Id.Length);
        Assert.Equal(128, evt.Sig.Length);
        Assert.True(NwcCrypto.Verify(evt));
        Assert.Contains("make_invoice", evt.Content);
        Assert.Contains(new[] { "encryption", "nip44_v2" }, evt.Tags);
    }

    [Fact]
    public void InfoEvent_advertises_pay_invoice_when_capped()
    {
        var bridge = Bridge(PayCfg(1000), BarkdOver(new FakeBarkd()));
        var evt = bridge.InfoEvent();
        Assert.Contains("pay_invoice", evt.Content);
    }
}
