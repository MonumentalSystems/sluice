using Microsoft.Extensions.Configuration;

namespace Sluice.Nwc;

/// <summary>
/// Config for the Nostr Wallet Connect (NIP-47) bridge, bound from <c>Nwc:*</c>. Default OFF. Phase 1 is
/// RECEIVE-ONLY — it advertises and serves only <c>get_info / get_balance / make_invoice / lookup_invoice</c>;
/// there is no <c>pay_invoice</c>, so a leaked connection string cannot move funds. The barkd binding comes
/// from the host wiring (<c>Sluice.Barkd.IBarkdClient</c>).
/// </summary>
public sealed class NwcConfig
{
    /// <summary>Master switch for the NWC bridge.</summary>
    public bool Enabled { get; set; }

    /// <summary>The wallet-service nostr secret key (64-hex). This identity is the <c>walletPubkey</c> in the
    /// connection URI; keep it in a secret, never a plain env literal.</summary>
    public string PrivateKeyHex { get; set; } = string.Empty;

    /// <summary>Relay(s) the bridge connects to (and that clients must share). Empty by default — the operator
    /// must supply at least one relay to enable the bridge.</summary>
    public List<string> Relays { get; set; } = new();

    /// <summary>Allowed client connection secrets (32-hex). Each grants ONE client (its derived pubkey) the
    /// right to talk to the wallet; the connection string handed to that client carries its secret. Requests
    /// from any other pubkey are ignored. Generate one per consumer; revoke by removing it here.</summary>
    public List<string> ConnectionSecrets { get; set; } = new();

    /// <summary>Human label surfaced in <c>get_info</c>.</summary>
    public string WalletName { get; set; } = "barkd";

    /// <summary>Upper bound (sat) on a single <c>make_invoice</c> — a guard even though receive can't lose funds.</summary>
    public long MaxInvoiceSat { get; set; } = 1_000_000;

    /// <summary>Daily (UTC) spend cap in sat for <c>pay_invoice</c>. <b>0 (default) ⇒ pay_invoice DISABLED</b>
    /// — the bridge stays receive-only. Any positive value enables capped spending: pay_invoice is then
    /// advertised + served, bounded to this many sat per day across this wallet.</summary>
    public long MaxDailyPaySat { get; set; }

    private static readonly string[] ReceiveMethods = { "get_info", "get_balance", "make_invoice", "lookup_invoice", "list_transactions" };

    /// <summary>The methods this bridge serves: always the receive-only four, plus <c>pay_invoice</c> when a
    /// daily cap is configured (<see cref="MaxDailyPaySat"/> &gt; 0).</summary>
    public string[] Methods() => MaxDailyPaySat > 0
        ? ReceiveMethods.Append("pay_invoice").ToArray()
        : ReceiveMethods;

    public static NwcConfig FromConfiguration(IConfiguration configuration)
    {
        var cfg = new NwcConfig();
        var section = configuration.GetSection("Nwc");
        // Drop the default relay list if the operator supplies one (avoid the bind-append footgun).
        if (section.GetSection("Relays").GetChildren().Any())
            cfg.Relays.Clear();
        ConfigurationBinder.Bind(section, cfg);
        cfg.Relays = cfg.Relays.Where(r => !string.IsNullOrWhiteSpace(r)).ToList();
        cfg.ConnectionSecrets = cfg.ConnectionSecrets.Where(s => !string.IsNullOrWhiteSpace(s)).ToList();
        return cfg;
    }
}
