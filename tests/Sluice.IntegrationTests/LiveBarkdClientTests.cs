using Sluice.Barkd;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Xunit;

namespace Sluice.IntegrationTests;

/// <summary>
/// Real-barkd client integration tests — driven against a regtest stack (or any reachable barkd). Gated:
/// every test Skips when the <c>BARKD_TEST_*</c> env is unset or the daemon is unreachable (see
/// <see cref="BarkdGate"/>). This is the CLIENT-ONLY half — it exercises <see cref="BarkdClient"/> directly
/// (wallet reachability + invoice create resolving a payment hash). The Orleans grain end-to-end settlement
/// test lives in the consuming application.
/// </summary>
public sealed class LiveBarkdClientTests
{
    private static BarkdClient MerchantClient()
    {
        var options = new BarkdClientOptions
        {
            BaseUrl = BarkdGate.MerchantUrl,
            Token = BarkdGate.MerchantToken,
        };
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddHttpClient();
        var sp = services.BuildServiceProvider();
        return new BarkdClient(Options.Create(options), sp.GetRequiredService<IHttpClientFactory>(),
            sp.GetRequiredService<Microsoft.Extensions.Logging.ILogger<BarkdClient>>());
    }

    [BarkdFact]
    public async Task Wallet_info_is_reachable()
    {
        var client = MerchantClient();
        var info = await client.GetWalletInfoAsync();
        Assert.True(info.Reachable, $"wallet/balance should answer: {info.Raw}");
        Assert.True(info.SpendableSat >= 0);
    }

    [BarkdFact]
    public async Task Invoice_create_resolves_a_payment_hash_and_a_pending_receive()
    {
        var client = MerchantClient();
        var invoice = await client.CreateInvoiceAsync(1100, $"sluice itest {Guid.NewGuid():N}");

        Assert.StartsWith("ln", invoice.Invoice, StringComparison.OrdinalIgnoreCase);
        Assert.Matches("^[0-9a-f]{64}$", invoice.PaymentHash);

        var status = await client.GetReceiveStatusAsync(invoice.PaymentHash);
        Assert.True(status.Found, "the fresh receive should be queryable by its payment hash");
        Assert.False(status.Settled, "a fresh unpaid invoice must not read as settled");
    }
}
