using Xunit;

namespace Sluice.IntegrationTests;

/// <summary>
/// Live barkd availability gate. The real-payment integration tests need one or two reachable barkd daemons —
/// typically a regtest stack. There is deliberately NO default URL: with the env unset the tests Skip (a
/// wallet daemon is never assumed to exist on a dev box).
///
/// <para>Config (env): <c>BARKD_TEST_URL</c> + <c>BARKD_TEST_TOKEN</c> (the merchant — what the checkout
/// talks to); <c>BARKD_TEST_PAYER_URL</c> + <c>BARKD_TEST_PAYER_TOKEN</c> (the wallet that pays the invoice);
/// <c>BARKD_TEST_SKIP=1</c> to force-skip. Detection runs once (lazy, cached): an unauthenticated HTTP
/// <c>GET /ping</c> per daemon.</para>
/// </summary>
public static class BarkdGate
{
    public static string? MerchantUrl => Env("BARKD_TEST_URL");
    public static string? MerchantToken => Env("BARKD_TEST_TOKEN");
    public static string? PayerUrl => Env("BARKD_TEST_PAYER_URL");
    public static string? PayerToken => Env("BARKD_TEST_PAYER_TOKEN");

    private static string? Env(string name) =>
        Environment.GetEnvironmentVariable(name) is { Length: > 0 } v ? v : null;

    private static readonly Lazy<(bool Available, string? Reason)> _merchantProbe =
        new(() => Probe(MerchantUrl, MerchantToken, "merchant (BARKD_TEST_URL/_TOKEN)"));

    private static readonly Lazy<(bool Available, string? Reason)> _payerProbe =
        new(() => Probe(PayerUrl, PayerToken, "payer (BARKD_TEST_PAYER_URL/_PAYER_TOKEN)"));

    public static bool MerchantAvailable => _merchantProbe.Value.Available;
    public static string? MerchantReason => _merchantProbe.Value.Reason;

    public static bool PayerAvailable => MerchantAvailable && _payerProbe.Value.Available;
    public static string? PayerReason => MerchantAvailable ? _payerProbe.Value.Reason : MerchantReason;

    private static (bool, string?) Probe(string? baseUrl, string? token, string label)
    {
        if (Environment.GetEnvironmentVariable("BARKD_TEST_SKIP") == "1")
            return (false, "BARKD_TEST_SKIP=1");
        if (baseUrl is null || token is null)
            return (false, $"{label} env unset");
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(4) };
            using var resp = http.GetAsync(baseUrl.TrimEnd('/') + "/ping").GetAwaiter().GetResult();
            return resp.IsSuccessStatusCode
                ? (true, null)
                : (false, $"{label}: /ping returned {(int)resp.StatusCode}");
        }
        catch (Exception ex)
        {
            return (false, $"{label}: {baseUrl} unreachable: {ex.Message}");
        }
    }
}

/// <summary>Skips unless the merchant barkd is reachable (read-only / invoice-create tests).</summary>
[AttributeUsage(AttributeTargets.Method)]
public sealed class BarkdFactAttribute : FactAttribute
{
    public BarkdFactAttribute()
    {
        if (!BarkdGate.MerchantAvailable)
            Skip = $"Live barkd unreachable — payments integration test skipped. {BarkdGate.MerchantReason}";
    }
}

/// <summary>Skips unless BOTH barkd daemons are reachable (the real end-to-end payment test).</summary>
[AttributeUsage(AttributeTargets.Method)]
public sealed class BarkdPayerFactAttribute : FactAttribute
{
    public BarkdPayerFactAttribute()
    {
        if (!BarkdGate.PayerAvailable)
            Skip = $"Live barkd merchant+payer pair unreachable — end-to-end payment test skipped. {BarkdGate.PayerReason}";
    }
}
