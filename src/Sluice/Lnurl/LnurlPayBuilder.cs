using System.Text.Json;
using Sluice.Barkd;
using Microsoft.Extensions.Logging;

namespace Sluice.Lnurl;

/// <summary>Outcome of an LNURL-pay callback. On success <see cref="Bolt11"/> holds the minted invoice; on
/// failure <see cref="Reason"/> holds the LNURL error reason (the ASP.NET layer serializes
/// <c>{status:"ERROR", reason}</c> with HTTP 200, per the LNURL convention).</summary>
public sealed record LnurlCallbackResult(bool Success, string? Bolt11, string? Reason)
{
    public static LnurlCallbackResult Ok(string bolt11) => new(true, bolt11, null);
    public static LnurlCallbackResult Fail(string reason) => new(false, null, reason);
}

/// <summary>
/// Pure LNURL-pay (LUD-06/16) RECEIVE logic decoupled from any web framework.
/// No ASP.NET dependency: builds the payRequest metadata/bounds and runs the invoice-callback flow against
/// <see cref="IBarkdClient"/>. The HTTP wiring (route mapping, request parsing, JSON writing) stays in the
/// host.
///
/// On <c>description_hash</c>: the latest LUD-06 DROPPED the rule that the invoice's description_hash equal
/// <c>sha256(metadata)</c> (wallets no longer verify it), so barkd's plain-text-description invoices are
/// spec-aligned — we use a clean human-readable description for nicer sender-wallet display.
/// </summary>
public static class LnurlPayBuilder
{
    /// <summary>The LUD-06 metadata string — a JSON-encoded array, surfaced verbatim in the payRequest.</summary>
    public static string Metadata(string address) => JsonSerializer.Serialize(new[]
    {
        new[] { "text/plain", $"Pay to {address}" },
        new[] { "text/identifier", address },
    });

    public static bool AddressMatches(LnurlPayOptions cfg, string? username) =>
        cfg.Enabled
        && !string.IsNullOrWhiteSpace(cfg.Domain)
        && !string.IsNullOrWhiteSpace(cfg.Username)
        && string.Equals(username?.Trim(), cfg.Username.Trim(), StringComparison.OrdinalIgnoreCase);

    public static string Address(LnurlPayOptions cfg) =>
        $"{cfg.Username.Trim().ToLowerInvariant()}@{cfg.Domain.Trim().ToLowerInvariant()}";

    /// <summary>Advertised min/max sendable, in millisatoshis.</summary>
    public static (long minMsat, long maxMsat) Bounds(LnurlPayOptions cfg)
    {
        var min = Math.Max(1, cfg.MinSat);
        var max = Math.Max(min, cfg.MaxSat);
        return (min * 1000, max * 1000);
    }

    /// <summary>The absolute LNURL-pay callback URL (built from the configured domain, NOT the request Host).</summary>
    public static string Callback(LnurlPayOptions cfg) =>
        $"https://{cfg.Domain.Trim().ToLowerInvariant()}/api/lnurlp/{cfg.Username.Trim().ToLowerInvariant()}/callback";

    /// <summary>The LUD-06 payRequest payload (serialize to JSON in the host). Returns null when the username
    /// does not match the configured address.</summary>
    public static object? BuildPayRequest(LnurlPayOptions cfg, string? username)
    {
        if (!AddressMatches(cfg, username))
            return null;
        var (minMsat, maxMsat) = Bounds(cfg);
        return new
        {
            tag = "payRequest",
            callback = Callback(cfg),
            minSendable = minMsat,
            maxSendable = maxMsat,
            metadata = Metadata(Address(cfg)),
            commentAllowed = Math.Max(0, cfg.CommentLength),
        };
    }

    /// <summary>Run the LNURL-pay callback: validate the address + amount, then mint a barkd invoice.
    /// <paramref name="amountMsat"/> is the raw <c>amount</c> query param (msat); <paramref name="comment"/>
    /// is the optional LUD-12 comment.</summary>
    public static async Task<LnurlCallbackResult> CreateInvoiceAsync(
        LnurlPayOptions cfg,
        string? username,
        long amountMsat,
        string? comment,
        IBarkdClient barkd,
        ILogger? logger = null,
        CancellationToken ct = default)
    {
        if (!AddressMatches(cfg, username))
            return LnurlCallbackResult.Fail("unknown address");
        if (amountMsat <= 0)
            return LnurlCallbackResult.Fail("missing or invalid amount");
        var (minMsat, maxMsat) = Bounds(cfg);
        if (amountMsat < minMsat || amountMsat > maxMsat)
            return LnurlCallbackResult.Fail($"amount out of range ({minMsat}-{maxMsat} msat)");
        var amountSat = amountMsat / 1000; // barkd invoices are denominated in sat (sub-sat precision dropped)
        if (amountSat <= 0)
            return LnurlCallbackResult.Fail("amount below 1 sat");

        if (!barkd.IsConfigured)
            return LnurlCallbackResult.Fail("wallet unavailable");

        var address = Address(cfg);
        var trimmed = comment?.Trim();
        var description = cfg.CommentLength > 0 && !string.IsNullOrWhiteSpace(trimmed)
            ? $"{address}: {trimmed[..Math.Min(trimmed.Length, cfg.CommentLength)]}"
            : $"Pay to {address}";
        try
        {
            var invoice = await barkd.CreateInvoiceAsync(amountSat, description, ct);
            return LnurlCallbackResult.Ok(invoice.Invoice);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger?.LogWarning(ex, "lnurlp invoice create failed for {Address}", address);
            return LnurlCallbackResult.Fail("could not generate invoice");
        }
    }
}
