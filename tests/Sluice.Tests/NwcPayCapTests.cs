using Sluice.Barkd;
using Sluice.Nwc;
using Xunit;

namespace Sluice.Tests;

/// <summary>The NWC pay_invoice guardrails: BOLT11 amount parsing (drives the cap check) and the rolling
/// daily spend cap (reserve / refund / UTC day rollover). Pure — fast.</summary>
public sealed class NwcPayCapTests
{
    [Theory]
    [InlineData("lnbc500n1p4rx07fdummy", 50_000)] // 50 sat (a real receive earlier)
    [InlineData("lnbc210n1p4rx33xdummy", 21_000)] // 21 sat
    [InlineData("lnbc10n1pdummy", 1_000)]         // 1 sat
    [InlineData("lnbc1u1pdummy", 100_000)]        // 100 sat
    [InlineData("lnbc1m1pdummy", 100_000_000)]    // 0.001 BTC = 100k sat
    [InlineData("LNBC500N1P4RX07FDUMMY", 50_000)] // case-insensitive
    public void Bolt11_amount_is_parsed_from_the_hrp(string invoice, long expectedMsat)
    {
        Assert.Equal(expectedMsat, Bolt11.AmountMsat(invoice));
    }

    [Theory]
    [InlineData("lnbc1pamountlessdummy")] // amountless (no digits before the separator)
    [InlineData("not-an-invoice")]
    [InlineData("")]
    public void Bolt11_amountless_or_garbage_is_null(string invoice)
    {
        Assert.Null(Bolt11.AmountMsat(invoice));
    }

    [Fact]
    public void SpendCap_reserves_up_to_the_cap_then_refuses()
    {
        var cap = new NwcSpendCap(10);
        Assert.True(cap.TryReserve(7));      // 7/10
        Assert.False(cap.TryReserve(4));     // would be 11 > 10 → refused, no reservation
        Assert.Equal(7, cap.SpentToday);
        Assert.True(cap.TryReserve(3));      // 10/10
        Assert.False(cap.TryReserve(1));     // full
        Assert.False(cap.TryReserve(0));     // non-positive never reserves
        Assert.Equal(10, cap.SpentToday);
    }

    [Fact]
    public void SpendCap_refund_frees_budget_for_a_failed_payment()
    {
        var cap = new NwcSpendCap(10);
        Assert.True(cap.TryReserve(8));
        cap.Refund(8);                       // payment failed
        Assert.Equal(0, cap.SpentToday);
        Assert.True(cap.TryReserve(10));     // full budget available again
    }

    [Fact]
    public void SpendCap_resets_at_the_utc_day_boundary()
    {
        var now = new DateTime(2026, 6, 17, 23, 59, 0, DateTimeKind.Utc);
        var cap = new NwcSpendCap(10, () => now);
        Assert.True(cap.TryReserve(10));
        Assert.False(cap.TryReserve(1));     // capped for the day
        now = now.AddMinutes(2);             // crosses UTC midnight → new day
        Assert.True(cap.TryReserve(10));     // budget reset
        Assert.Equal(10, cap.SpentToday);
    }
}
