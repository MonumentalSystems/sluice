using System.Net;
using System.Text.Json;
using Sluice.Barkd;

namespace Sluice.TestKit;

/// <summary>
/// An in-process fake of the barkd 0.2.3 REST surface — wired in as an <see cref="IHttpClientFactory"/> so the
/// REAL <see cref="BarkdClient"/> exercises its actual HTTP/JSON paths against it. Spec-faithful where it
/// matters: invoice create returns ONLY <c>{invoice}</c> (no payment_hash — the client must resolve it via the
/// receive-status lookup), receive status is keyed by payment hash OR invoice string, and settlement is
/// signalled by <c>finished_at</c> + <c>preimage_revealed_at</c> (a canceled receive sets only
/// <c>finished_at</c>). Controllable: call <see cref="Settle"/>/<see cref="CancelReceive"/> to flip a pending
/// invoice; set <see cref="Down"/> to simulate an unreachable daemon.
/// </summary>
public sealed class FakeBarkd : IHttpClientFactory
{
    private sealed class Receive
    {
        public required string Invoice { get; init; }
        public required string PaymentHash { get; init; }
        public long AmountSat { get; init; }
        public DateTime? FinishedAt { get; set; }
        public DateTime? PreimageRevealedAt { get; set; }
    }

    private readonly object _lock = new();
    private readonly List<Receive> _receives = new();
    private int _counter;

    /// <summary>Simulate barkd being unreachable (every call throws like a refused connection).</summary>
    public bool Down { get; set; }

    /// <summary>Spendable balance reported by GET /wallet/balance.</summary>
    public long SpendableSat { get; set; } = 123_456;

    public int InvoiceCreateCalls { get; private set; }

    /// <summary>The payment hash of the most recently created invoice.</summary>
    public string? LastPaymentHash
    {
        get { lock (_lock) { return _receives.Count > 0 ? _receives[^1].PaymentHash : null; } }
    }

    /// <summary>Mark a pending receive settled (paid): finished + preimage revealed.</summary>
    public void Settle(string paymentHash)
    {
        lock (_lock)
        {
            var r = _receives.Single(x => x.PaymentHash == paymentHash);
            r.FinishedAt = DateTime.UtcNow;
            r.PreimageRevealedAt = DateTime.UtcNow;
        }
    }

    /// <summary>Mark a pending receive canceled: finished WITHOUT a revealed preimage.</summary>
    public void CancelReceive(string paymentHash)
    {
        lock (_lock)
        {
            var r = _receives.Single(x => x.PaymentHash == paymentHash);
            r.FinishedAt = DateTime.UtcNow;
            r.PreimageRevealedAt = null;
        }
    }

    public HttpClient CreateClient(string name) => new(new Handler(this), disposeHandler: true);

    private (HttpStatusCode Status, string Body) Respond(HttpRequestMessage request, string body)
    {
        if (Down)
            throw new HttpRequestException("connection refused (fake barkd is down)");

        var path = request.RequestUri!.AbsolutePath;
        lock (_lock)
        {
            if (request.Method == HttpMethod.Post && path == "/api/v1/lightning/receives/invoice")
            {
                InvoiceCreateCalls++;
                using var doc = JsonDocument.Parse(body);
                var amount = doc.RootElement.GetProperty("amount_sat").GetInt64();
                var n = ++_counter;
                var receive = new Receive
                {
                    // Deliberately NOT a parseable BOLT11 — forces the client's receive-status lookup
                    // fallback for the payment hash, the path the 0.2.3 spec actually requires.
                    Invoice = $"lnfakeinvoice{n}x{amount}",
                    PaymentHash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
                        System.Text.Encoding.UTF8.GetBytes($"fake-preimage-{n}"))).ToLowerInvariant(),
                    AmountSat = amount,
                };
                _receives.Add(receive);
                return (HttpStatusCode.OK, JsonSerializer.Serialize(new { invoice = receive.Invoice }));
            }

            if (request.Method == HttpMethod.Get && path.StartsWith("/api/v1/lightning/receives/", StringComparison.Ordinal))
            {
                var identifier = Uri.UnescapeDataString(path["/api/v1/lightning/receives/".Length..]);
                var r = _receives.FirstOrDefault(x => x.PaymentHash == identifier || x.Invoice == identifier);
                if (r is null)
                    return (HttpStatusCode.NotFound, """{"message":"not found"}""");
                return (HttpStatusCode.OK, JsonSerializer.Serialize(new Dictionary<string, object?>
                {
                    ["amount_sat"] = r.AmountSat,
                    ["payment_hash"] = r.PaymentHash,
                    ["payment_preimage"] = "00",
                    ["invoice"] = r.Invoice,
                    ["htlc_vtxos"] = Array.Empty<object>(),
                    ["preimage_revealed_at"] = r.PreimageRevealedAt?.ToString("O"),
                    ["finished_at"] = r.FinishedAt?.ToString("O"),
                }));
            }

            if (request.Method == HttpMethod.Get && path == "/api/v1/wallet/balance")
            {
                return (HttpStatusCode.OK, JsonSerializer.Serialize(new
                {
                    spendable_sat = SpendableSat,
                    pending_lightning_send_sat = 0,
                    claimable_lightning_receive_sat = 0,
                    pending_in_round_sat = 0,
                    pending_board_sat = 0,
                    pending_exit_sat = 0,
                }));
            }

            if (request.Method == HttpMethod.Get && path == "/api/v1/wallet/movements")
            {
                // A lightning receive + a lightning send + a non-lightning round (the round must be filtered out).
                // Real mainnet invoices so the client's BOLT11 payment-hash parse exercises its real path.
                const string recvInv = "lnbc100n1p4rx33hsp56ac4u3msrhpjz5vsphe254ut2jhd3u6aughecu6kyaml2h3fn2zqpp544wavhsx7x8kd9ct97whv6trqr2gfel8cn9ck6v2qvyr0dzz4a0qdzqwpshjsrnvd5xummjwghx6ef6yp2x2um5yp6x7grzv9exkepqf38zqctyv3ex2umnxqy9gcqcqzxg9qyysgq0fy3fcr0ez39jr7epm82rqajqj69khd98awqx2ln8g67nagsmvkrq5tp54x7eks8t8lc30edtqcnv9msern9tuxtgf3vdxfgv9a7hwsqpfw2wm";
                const string sendInv = "lnbc10n1p4rx450pp5dqvagl28mrmaqywk4fq55cvnfh67pmdxtc9e8cjvng58zcz2q63sdqqcqzzsxqyz5vqrzjqvueefmrckfdwyyu39m0lf24sqzcr9vcrmxrvgfn6empxz7phrjxvrttncqq0lcqqyqqqqlgqqqqqqgq2qsp5xjvfchakyuj4tgutfg5yy49g86yvru96l6qd3cfn2g0vt85f5pfq9qxpqysgqy820hx9kh37mvfqe6x3zf4gt4nlfddmz8fy6nup8hqvfn6rt3cpz2avqysmfaezgk50jqw5hxgl8cdlldudw9lp8tu6ytcjd4d03g9qpkhvjnu";
                var movements = new object[]
                {
                    new { id = 6, status = "successful", subsystem = new { name = "bark.round", kind = "refresh" }, offchain_fee_sat = 3, received_on = Array.Empty<object>(), sent_to = Array.Empty<object>(), time = new { created_at = "2026-06-18T03:04:29Z", completed_at = "2026-06-18T03:09:42Z" } },
                    new { id = 5, status = "successful", subsystem = new { name = "bark.lightning_send", kind = "send" }, offchain_fee_sat = 20, received_on = Array.Empty<object>(), sent_to = new object[] { new { destination = new { type = "invoice", value = sendInv }, amount_sat = 1 } }, time = new { created_at = "2026-06-18T02:23:17Z", completed_at = "2026-06-18T02:23:19Z" } },
                    new { id = 4, status = "successful", subsystem = new { name = "bark.lightning_receive", kind = "receive" }, offchain_fee_sat = 0, received_on = new object[] { new { destination = new { type = "invoice", value = recvInv }, amount_sat = 10 } }, sent_to = Array.Empty<object>(), time = new { created_at = "2026-06-18T01:13:29Z", completed_at = "2026-06-18T01:13:29Z" } },
                };
                return (HttpStatusCode.OK, JsonSerializer.Serialize(movements));
            }

            return (HttpStatusCode.NotFound, """{"message":"no such route"}""");
        }
    }

    private sealed class Handler : HttpMessageHandler
    {
        private readonly FakeBarkd _owner;
        public Handler(FakeBarkd owner) => _owner = owner;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(cancellationToken);
            var (status, respBody) = _owner.Respond(request, body);
            return new HttpResponseMessage(status)
            {
                Content = new StringContent(respBody, System.Text.Encoding.UTF8, "application/json"),
            };
        }
    }
}
