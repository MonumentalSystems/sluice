namespace Sluice.Lnurl;

/// <summary>Lightning Address (LNURL-pay, LUD-06/16) RECEIVE surface configuration. Phase 1 is a single
/// fixed address <c>{Username}@{Domain}</c>;
/// every receive lands in the one barkd wallet (no per-user attribution yet). Receive-only — it can mint
/// invoices, never spend. Default OFF.</summary>
public sealed class LnurlPayOptions
{
    /// <summary>Master switch for the public Lightning Address surface.</summary>
    public bool Enabled { get; set; }

    /// <summary>Public domain the address lives on (e.g. <c>schnorr.me</c>) — used to build the absolute
    /// callback URL + the address in metadata. NOT derived from the request Host (which is spoofable).</summary>
    public string Domain { get; set; } = string.Empty;

    /// <summary>The single accepted username (case-insensitive) ⇒ <c>{Username}@{Domain}</c>.</summary>
    public string Username { get; set; } = "pay";

    /// <summary>Min/max receivable, in satoshis (advertised as msat in the LUD-06 response).</summary>
    public long MinSat { get; set; } = 1;

    public long MaxSat { get; set; } = 1_000_000;

    /// <summary>LUD-12 comment length advertised (0 ⇒ comments not accepted).</summary>
    public int CommentLength { get; set; } = 240;
}
