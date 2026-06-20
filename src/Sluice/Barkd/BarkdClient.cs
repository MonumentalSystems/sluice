using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Sluice.Barkd;

/// <summary>
/// barkd REST client (bark's daemon, <c>/api/v1</c>, bearer auth). Paths + field names verified against
/// the barkd 0.2.3 OpenAPI (<c>bark-rest/openapi.json</c> at tag <c>bark-0.2.3</c>; rendered at
/// https://second.tech/docs/barkd/): invoices via <c>POST /lightning/receives/invoice</c>
/// (<c>{amount_sat, description}</c> → <c>{invoice}</c> — NO payment_hash in the response, so it is
/// derived from the BOLT11 or read back via the receive-status endpoint), settlement via
/// <c>GET /lightning/receives/{identifier}</c> (identifier = payment hash | invoice | preimage; settled ⇔
/// <c>finished_at</c> AND <c>preimage_revealed_at</c> set — <c>finished_at</c> alone can mean canceled),
/// balance via <c>GET /wallet/balance</c> (<c>spendable_sat</c> et al).
/// </summary>
public sealed class BarkdClient : IBarkdClient
{
    private readonly BarkdClientOptions _options;
    private readonly IHttpClientFactory _httpFactory;
    private readonly ILogger<BarkdClient> _logger;

    public BarkdClient(IOptions<BarkdClientOptions> options, IHttpClientFactory httpFactory, ILogger<BarkdClient> logger)
    {
        _options = options.Value;
        _httpFactory = httpFactory;
        _logger = logger;
    }

    public bool IsConfigured => !string.IsNullOrWhiteSpace(_options.BaseUrl);

    private HttpClient Create()
    {
        if (!IsConfigured)
            throw new BarkdException("barkd is not configured (Barkd:BaseUrl)");
        var http = _httpFactory.CreateClient("barkd");
        http.BaseAddress = new Uri(_options.BaseUrl!.TrimEnd('/') + "/api/v1/");
        http.Timeout = TimeSpan.FromSeconds(Math.Max(5, _options.TimeoutSeconds));
        if (!string.IsNullOrWhiteSpace(_options.Token))
            http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", _options.Token);
        return http;
    }

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public async Task<BarkdInvoice> CreateInvoiceAsync(long amountSat, string description, CancellationToken ct = default)
    {
        using var http = Create();
        try
        {
            var body = new StringContent(
                JsonSerializer.Serialize(new { amount_sat = amountSat, description }),
                Encoding.UTF8, "application/json");
            using var resp = await http.PostAsync("lightning/receives/invoice", body, ct);
            var text = await resp.Content.ReadAsStringAsync(ct);
            if (!resp.IsSuccessStatusCode)
                throw new BarkdException($"barkd invoice create failed: {(int)resp.StatusCode} {Truncate(text)}");
            using var doc = JsonDocument.Parse(text);
            var invoice = doc.RootElement.TryGetProperty("invoice", out var inv) ? inv.GetString() : null;
            if (string.IsNullOrWhiteSpace(invoice))
                throw new BarkdException("barkd invoice create returned no invoice");
            // barkd 0.2.3 returns ONLY {invoice} (InvoiceInfo) — prefer an explicit payment_hash if a
            // future version adds one, else parse it out of the BOLT11, else read it back from the
            // receive-status endpoint (which accepts the invoice string as the identifier).
            var hash = doc.RootElement.TryGetProperty("payment_hash", out var ph) ? ph.GetString() : null;
            hash ??= Bolt11PaymentHash(invoice!);
            hash ??= await LookupPaymentHashByInvoiceAsync(http, invoice!, ct);
            if (string.IsNullOrWhiteSpace(hash))
                throw new BarkdException("could not determine payment_hash for the invoice");
            return new BarkdInvoice(invoice!, hash!);
        }
        catch (Exception ex) when (ex is not BarkdException and not OperationCanceledException)
        {
            throw new BarkdException("barkd invoice create failed", ex);
        }
    }

    public async Task<BarkdReceiveStatus> GetReceiveStatusAsync(string paymentHash, CancellationToken ct = default)
    {
        using var http = Create();
        try
        {
            using var resp = await http.GetAsync($"lightning/receives/{Uri.EscapeDataString(paymentHash)}", ct);
            if (resp.StatusCode == System.Net.HttpStatusCode.NotFound)
                return new BarkdReceiveStatus(false, false, null);
            var text = await resp.Content.ReadAsStringAsync(ct);
            if (!resp.IsSuccessStatusCode)
                throw new BarkdException($"barkd receive status failed: {(int)resp.StatusCode} {Truncate(text)}");
            using var doc = JsonDocument.Parse(text);
            var finishedAt = ReadTimestamp(doc.RootElement, "finished_at");
            var preimageRevealedAt = ReadTimestamp(doc.RootElement, "preimage_revealed_at");
            // finished_at alone covers BOTH settled and canceled receives (per the 0.2.3 docs); only a
            // revealed preimage proves the payment actually settled.
            return new BarkdReceiveStatus(true, finishedAt is not null && preimageRevealedAt is not null, finishedAt);
        }
        catch (Exception ex) when (ex is not BarkdException and not OperationCanceledException)
        {
            throw new BarkdException("barkd receive status failed", ex);
        }
    }

    private static DateTime? ReadTimestamp(JsonElement root, string name) =>
        root.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String
            && DateTime.TryParse(v.GetString(), null, System.Globalization.DateTimeStyles.AdjustToUniversal | System.Globalization.DateTimeStyles.AssumeUniversal, out var at)
            ? at : null;

    /// <summary>Read the payment hash back from <c>GET lightning/receives/{invoice}</c> (the identifier
    /// accepts the invoice string). Null when the receive isn't found or the call fails.</summary>
    private async Task<string?> LookupPaymentHashByInvoiceAsync(HttpClient http, string invoice, CancellationToken ct)
    {
        try
        {
            using var resp = await http.GetAsync($"lightning/receives/{Uri.EscapeDataString(invoice)}", ct);
            if (!resp.IsSuccessStatusCode)
                return null;
            using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
            return doc.RootElement.TryGetProperty("payment_hash", out var ph) ? ph.GetString() : null;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogDebug(ex, "barkd payment-hash lookup by invoice failed");
            return null;
        }
    }

    public async Task<BarkdPayResult> PayInvoiceAsync(string destination, long? amountSat, string? comment, CancellationToken ct = default)
    {
        using var http = Create();
        try
        {
            var body = new StringContent(
                JsonSerializer.Serialize(new { destination, amount_sat = amountSat, comment }),
                Encoding.UTF8, "application/json");
            using var resp = await http.PostAsync("lightning/pay", body, ct);
            var text = await resp.Content.ReadAsStringAsync(ct);
            if (!resp.IsSuccessStatusCode)
                throw new BarkdException($"barkd pay failed: {(int)resp.StatusCode} {Truncate(text)}");
            string? message = null;
            try
            {
                using var doc = JsonDocument.Parse(text);
                message = doc.RootElement.TryGetProperty("message", out var m) ? m.GetString() : null;
            }
            catch { /* non-JSON 2xx — treat as success */ }
            return new BarkdPayResult(message ?? "paid");
        }
        catch (Exception ex) when (ex is not BarkdException and not OperationCanceledException)
        {
            throw new BarkdException("barkd pay failed", ex);
        }
    }

    public async Task<IReadOnlyList<BarkdMovement>> ListLightningMovementsAsync(int max, CancellationToken ct = default)
    {
        using var http = Create();
        try
        {
            using var resp = await http.GetAsync("wallet/movements", ct);
            var text = await resp.Content.ReadAsStringAsync(ct);
            if (!resp.IsSuccessStatusCode)
                throw new BarkdException($"barkd movements failed: {(int)resp.StatusCode} {Truncate(text)}");
            using var doc = JsonDocument.Parse(text);
            // The body may be a bare array or wrapped ({movements:[…]} / {items:[…]}).
            var root = doc.RootElement;
            JsonElement arr = root.ValueKind == JsonValueKind.Array ? root
                : root.TryGetProperty("movements", out var mv) ? mv
                : root.TryGetProperty("items", out var it) ? it : default;
            if (arr.ValueKind != JsonValueKind.Array)
                return Array.Empty<BarkdMovement>();

            var list = new List<BarkdMovement>();
            foreach (var m in arr.EnumerateArray())
            {
                var subsystem = m.TryGetProperty("subsystem", out var ss) && ss.TryGetProperty("name", out var sn)
                    ? sn.GetString() : null;
                string? direction = subsystem switch
                {
                    "bark.lightning_receive" => "incoming",
                    "bark.lightning_send" => "outgoing",
                    _ => null,
                };
                if (direction is null)
                    continue; // skip rounds/boards/exits — NWC is lightning-only

                var legs = direction == "incoming" ? "received_on" : "sent_to";
                if (!m.TryGetProperty(legs, out var legArr) || legArr.ValueKind != JsonValueKind.Array || legArr.GetArrayLength() == 0)
                    continue;
                var leg = legArr[0];
                var amountSat = leg.TryGetProperty("amount_sat", out var a) && a.TryGetInt64(out var av) ? av : 0;
                string? invoice = leg.TryGetProperty("destination", out var dst) && dst.TryGetProperty("value", out var dv)
                    ? dv.GetString() : null;
                var feeSat = direction == "outgoing" && m.TryGetProperty("offchain_fee_sat", out var f) && f.TryGetInt64(out var fv) ? fv : 0;
                var id = m.TryGetProperty("id", out var idEl) && idEl.TryGetInt64(out var idv) ? idv : 0;
                var status = m.TryGetProperty("status", out var st) ? st.GetString() ?? "" : "";
                DateTime? created = null, settled = null;
                if (m.TryGetProperty("time", out var tm))
                {
                    created = ReadTimestamp(tm, "created_at");
                    settled = ReadTimestamp(tm, "completed_at") ?? ReadTimestamp(tm, "updated_at");
                }
                var hash = invoice is not null ? Bolt11PaymentHash(invoice) : null;
                list.Add(new BarkdMovement(id, direction, amountSat, feeSat, invoice, hash, created, settled, status));
                if (list.Count >= max)
                    break;
            }
            return list;
        }
        catch (Exception ex) when (ex is not BarkdException and not OperationCanceledException)
        {
            throw new BarkdException("barkd movements failed", ex);
        }
    }

    public async Task<BarkdWalletInfo> GetWalletInfoAsync(CancellationToken ct = default)
    {
        if (!IsConfigured)
            return new BarkdWalletInfo(false, 0, 0, 0, null);
        using var http = Create();
        try
        {
            using var resp = await http.GetAsync("wallet/balance", ct);
            var text = await resp.Content.ReadAsStringAsync(ct);
            if (!resp.IsSuccessStatusCode)
                return new BarkdWalletInfo(false, 0, 0, 0, Truncate(text));
            using var doc = JsonDocument.Parse(text);
            long Read(string name) =>
                doc.RootElement.TryGetProperty(name, out var v) && v.TryGetInt64(out var n) ? n : 0;
            return new BarkdWalletInfo(true, Read("spendable_sat"), Read("pending_lightning_send_sat"), Read("pending_exit_sat"), text);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "barkd wallet info failed");
            return new BarkdWalletInfo(false, 0, 0, 0, ex.Message);
        }
    }

    private static string Truncate(string s) => s.Length > 300 ? s[..300] : s;

    /// <summary>Extract the payment hash from a BOLT11 invoice (the 52-char `p` tagged field). Returns null
    /// when parsing fails — callers treat the explicit field as authoritative.</summary>
    internal static string? Bolt11PaymentHash(string invoice)
    {
        try
        {
            var lower = invoice.ToLowerInvariant();
            var sep = lower.LastIndexOf('1');
            if (sep <= 0 || sep + 1 >= lower.Length)
                return null;
            var data = lower[(sep + 1)..^6]; // strip the 6-char checksum
            const string charset = "qpzry9x8gf2tvdw0s3jn54khce6mua7l";
            // Skip the timestamp (7 chars = 35 bits), then walk tagged fields: tag(1) len(2) payload(len).
            var i = 7;
            while (i + 3 <= data.Length)
            {
                var tag = charset.IndexOf(data[i]);
                var len = charset.IndexOf(data[i + 1]) * 32 + charset.IndexOf(data[i + 2]);
                if (tag < 0 || len < 0 || i + 3 + len > data.Length)
                    return null;
                if (tag == 1 && len == 52) // 'p' = payment hash, 52×5 bits = 260 bits → 256-bit hash
                {
                    var bits = new List<bool>(260);
                    for (var j = 0; j < 52; j++)
                    {
                        var v = charset.IndexOf(data[i + 3 + j]);
                        if (v < 0)
                            return null;
                        for (var b = 4; b >= 0; b--)
                            bits.Add(((v >> b) & 1) == 1);
                    }
                    var bytes = new byte[32];
                    for (var j = 0; j < 256; j++)
                    {
                        if (bits[j])
                            bytes[j / 8] |= (byte)(1 << (7 - j % 8));
                    }
                    return Convert.ToHexString(bytes).ToLowerInvariant();
                }
                i += 3 + len;
            }
            return null;
        }
        catch
        {
            return null;
        }
    }
}
