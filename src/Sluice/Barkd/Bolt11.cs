namespace Sluice.Barkd;

/// <summary>Reads the amount out of a BOLT11 invoice's human-readable prefix (no bech32 decode needed —
/// the amount lives in the HRP, e.g. <c>lnbc500n…</c> = 50 sat). Used to enforce the NWC spend cap BEFORE
/// paying. Returns null for an amountless invoice (the NWC client then supplies <c>amount</c>).</summary>
public static class Bolt11
{
    // Longest-first so lnbcrt/lntbs match before lnbc/lntb.
    private static readonly string[] Prefixes = { "lnbcrt", "lntbs", "lnbc", "lntb", "lnsb" };

    public static long? AmountMsat(string invoice)
    {
        if (string.IsNullOrWhiteSpace(invoice))
            return null;
        var s = invoice.Trim().ToLowerInvariant();
        var sep = s.LastIndexOf('1'); // '1' separates HRP from the bech32 data (which never contains '1')
        if (sep <= 0)
            return null;
        var hrp = s[..sep];
        string? amount = null;
        foreach (var p in Prefixes)
        {
            if (hrp.StartsWith(p, StringComparison.Ordinal))
            {
                amount = hrp[p.Length..];
                break;
            }
        }
        if (string.IsNullOrEmpty(amount))
            return null; // unknown network, or amountless invoice

        // amount = digits + optional multiplier (m/u/n/p). No multiplier ⇒ whole BTC.
        var end = 0;
        while (end < amount.Length && char.IsDigit(amount[end]))
            end++;
        if (end == 0)
            return null;
        if (!long.TryParse(amount[..end], out var digits))
            return null;
        var mult = end < amount.Length ? amount[end] : '\0';
        // 1 BTC = 1e11 msat. m=1e-3, u=1e-6, n=1e-9, p=1e-12 BTC.
        return mult switch
        {
            'm' => digits * 100_000_000L,
            'u' => digits * 100_000L,
            'n' => digits * 100L,
            'p' => digits / 10L, // pico→msat (digits is a multiple of 10 for a valid sub-msat-free amount)
            '\0' => digits * 100_000_000_000L,
            _ => null,
        };
    }
}
