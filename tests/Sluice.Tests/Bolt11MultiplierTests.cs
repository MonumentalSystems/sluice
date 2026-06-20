using Sluice.Barkd;
using Xunit;

namespace Sluice.Tests;

/// <summary>Bolt11 HRP amount parsing across every multiplier branch (m/u/n/p/none/unknown) and the
/// non-mainnet network prefixes. Fast.</summary>
public sealed class Bolt11MultiplierTests
{
    [Theory]
    [InlineData("lnbc1m1pdummy", 100_000_000L)]      // milli-BTC
    [InlineData("lnbc1u1pdummy", 100_000L)]          // micro-BTC
    [InlineData("lnbc1n1pdummy", 100L)]              // nano-BTC
    [InlineData("lnbc10p1pdummy", 1L)]               // pico-BTC: 10p / 10 = 1 msat
    [InlineData("lnbc1234x1pdummy", null)]           // unknown multiplier ⇒ null
    [InlineData("lntb1u1pdummy", 100_000L)]          // testnet prefix
    [InlineData("lnbcrt1u1pdummy", 100_000L)]        // regtest prefix
    [InlineData("lnsb1u1pdummy", 100_000L)]          // signet (lnsb) prefix
    public void AmountMsat_parses_each_multiplier(string invoice, long? expected)
    {
        Assert.Equal(expected, Bolt11.AmountMsat(invoice));
    }

    [Fact]
    public void AmountMsat_whole_btc_when_no_multiplier()
    {
        // HRP "lnbc2" (amount 2, no multiplier) + separator '1' + data "pqqq" ⇒ whole BTC = 2 * 1e11 msat.
        Assert.Equal(200_000_000_000L, Bolt11.AmountMsat("lnbc2" + "1" + "pqqq"));
    }

    [Fact]
    public void AmountMsat_no_separator_is_null()
    {
        Assert.Null(Bolt11.AmountMsat("lnbcnoseparator"));
    }
}
