using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using Sluice.Barkd;
using Sluice.Nostr;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Sluice.Nwc;

/// <summary>
/// The Nostr Wallet Connect (NIP-47) bridge — a background relay client that exposes the barkd wallet over
/// nostr. On each configured relay it publishes a kind:13194 info event, subscribes to kind:23194 requests
/// addressed to the wallet pubkey, and answers with kind:23195 responses. Requests are NIP-44 v2 encrypted
/// to/from a configured client (connection-secret) pubkey; anything from an unknown pubkey, or that fails
/// to decrypt/verify, is ignored. Methods: get_info / get_balance / make_invoice / lookup_invoice always;
/// pay_invoice ONLY when a daily spend cap is configured (Nwc:MaxDailyPaySat &gt; 0), bounded to that many
/// sat/day — so with the cap unset the bridge is receive-only and a connection string can't move funds.
/// </summary>
public sealed class NwcBridge : BackgroundService
{
    private readonly NwcConfig _cfg;
    private readonly IBarkdClient _barkd;
    private readonly ILogger<NwcBridge> _log;
    private readonly JsonSerializerOptions _json = new(JsonSerializerDefaults.Web);

    private string _servicePub = string.Empty;
    private readonly HashSet<string> _allowedClients = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _handled = new(); // request event ids already processed (dedupe across relays)
    private readonly NwcSpendCap? _payCap; // null ⇒ pay_invoice disabled (receive-only)

    public NwcBridge(NwcConfig cfg, IBarkdClient barkd, ILogger<NwcBridge> log)
    {
        _cfg = cfg;
        _barkd = barkd;
        _log = log;
        _payCap = cfg.MaxDailyPaySat > 0 ? new NwcSpendCap(cfg.MaxDailyPaySat) : null;
    }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        if (!_cfg.Enabled)
            return;
        if (_cfg.PrivateKeyHex.Length != 64)
        {
            _log.LogWarning("[nwc] disabled: Nwc:PrivateKeyHex must be 64 hex chars");
            return;
        }
        if (_cfg.Relays.Count == 0 || _cfg.ConnectionSecrets.Count == 0)
        {
            _log.LogWarning("[nwc] disabled: need at least one relay and one connection secret");
            return;
        }
        try
        {
            _servicePub = NwcCrypto.PubKeyHex(_cfg.PrivateKeyHex);
            foreach (var s in _cfg.ConnectionSecrets)
                _allowedClients.Add(NwcCrypto.PubKeyHex(s));
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "[nwc] disabled: bad key material");
            return;
        }

        _log.LogInformation("[nwc] starting — walletPubkey {Pub}, {Clients} client(s), relays [{Relays}]",
            _servicePub, _allowedClients.Count, string.Join(",", _cfg.Relays));

        await Task.WhenAll(_cfg.Relays.Select(r => RunRelayAsync(r, ct)));
    }

    private async Task RunRelayAsync(string url, CancellationToken ct)
    {
        var sub = "nwc-" + _servicePub[..8];
        while (!ct.IsCancellationRequested)
        {
            try
            {
                using var ws = new ClientWebSocket();
                await ws.ConnectAsync(new Uri(url), ct);
                _log.LogInformation("[nwc] connected to {Relay}", url);

                // Publish the info event (replaceable), then subscribe to fresh requests addressed to us.
                await PublishAsync(ws, InfoEvent(), ct);
                var since = DateTimeOffset.UtcNow.ToUnixTimeSeconds() - 5;
                var req = JsonSerializer.Serialize(new object[]
                {
                    "REQ", sub, new Dictionary<string, object>
                    {
                        ["kinds"] = new[] { 23194 },
                        ["#p"] = new[] { _servicePub },
                        ["since"] = since,
                    },
                });
                await SendRawAsync(ws, req, ct);

                await ReadLoopAsync(ws, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "[nwc] relay {Relay} dropped — reconnecting in 5s", url);
            }
            try
            { await Task.Delay(TimeSpan.FromSeconds(5), ct); }
            catch (OperationCanceledException) { return; }
        }
    }

    private async Task ReadLoopAsync(ClientWebSocket ws, CancellationToken ct)
    {
        var buf = new byte[64 * 1024];
        using var msg = new MemoryStream();
        while (ws.State == WebSocketState.Open && !ct.IsCancellationRequested)
        {
            WebSocketReceiveResult res;
            do
            {
                res = await ws.ReceiveAsync(buf, ct);
                if (res.MessageType == WebSocketMessageType.Close)
                    return;
                msg.Write(buf, 0, res.Count);
                if (msg.Length > 2 * 1024 * 1024)
                { msg.SetLength(0); break; } // drop oversized frames
            } while (!res.EndOfMessage);

            if (msg.Length == 0)
                continue;
            var text = Encoding.UTF8.GetString(msg.ToArray());
            msg.SetLength(0);
            await OnRelayMessageAsync(ws, text, ct);
        }
    }

    private async Task OnRelayMessageAsync(ClientWebSocket ws, string text, CancellationToken ct)
    {
        try
        {
            using var doc = JsonDocument.Parse(text);
            if (doc.RootElement.ValueKind != JsonValueKind.Array || doc.RootElement.GetArrayLength() < 2)
                return;
            var type = doc.RootElement[0].GetString();
            // ["EVENT", subId, event]
            if (type == "EVENT" && doc.RootElement.GetArrayLength() >= 3)
            {
                var evt = ParseEvent(doc.RootElement[2]);
                if (evt is not null)
                    await HandleRequestAsync(ws, evt, ct);
            }
            // OK / EOSE / NOTICE — nothing to do.
        }
        catch (Exception ex)
        {
            _log.LogDebug(ex, "[nwc] bad relay message");
        }
    }

    private async Task HandleRequestAsync(ClientWebSocket ws, NostrEvent evt, CancellationToken ct)
    {
        if (evt.Kind != 23194 || !_allowedClients.Contains(evt.Pubkey))
            return;
        lock (_handled)
        {
            if (!_handled.Add(evt.Id))
                return; // already processed (another relay delivered it)
            if (_handled.Count > 4096)
                _handled.Clear();
        }
        if (!NwcCrypto.Verify(evt))
        {
            _log.LogDebug("[nwc] dropping request with bad signature from {Pub}", evt.Pubkey);
            return;
        }

        var plain = Nip44.Decrypt(_cfg.PrivateKeyHex, evt.Pubkey, evt.Content);
        if (plain is null)
            return; // can't decrypt ⇒ not really from this client (or not nip44_v2)

        string method = "unknown";
        object payload;
        try
        {
            using var reqDoc = JsonDocument.Parse(plain);
            method = reqDoc.RootElement.TryGetProperty("method", out var m) ? m.GetString() ?? "" : "";
            var prm = reqDoc.RootElement.TryGetProperty("params", out var p) ? p : default;
            payload = await DispatchAsync(method, prm, ct);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "[nwc] request handling failed ({Method})", method);
            payload = new { result_type = method, error = new { code = "INTERNAL", message = "internal error" } };
        }

        var responseContent = Nip44.Encrypt(_cfg.PrivateKeyHex, evt.Pubkey, JsonSerializer.Serialize(payload, _json));
        var response = new NostrEvent
        {
            CreatedAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            Kind = 23195,
            Tags = new List<string[]> { new[] { "p", evt.Pubkey }, new[] { "e", evt.Id } },
            Content = responseContent,
        };
        await PublishAsync(ws, NwcCrypto.Sign(_cfg.PrivateKeyHex, response), ct);
        _log.LogInformation("[nwc] {Method} ← {Pub}", method, evt.Pubkey[..8]);
    }

    private async Task<object> DispatchAsync(string method, JsonElement prm, CancellationToken ct)
    {
        switch (method)
        {
            case "get_info":
                return Ok(method, new
                {
                    alias = _cfg.WalletName,
                    network = "mainnet",
                    methods = _cfg.Methods(),
                    pubkey = _servicePub,
                });

            case "get_balance":
                {
                    var w = await _barkd.GetWalletInfoAsync(ct);
                    if (!w.Reachable)
                        return Err(method, "INTERNAL", "wallet unreachable");
                    return Ok(method, new { balance = w.SpendableSat * 1000 }); // msat
                }

            case "make_invoice":
                {
                    var amountMsat = prm.ValueKind == JsonValueKind.Object && prm.TryGetProperty("amount", out var a) && a.TryGetInt64(out var am) ? am : 0;
                    if (amountMsat <= 0)
                        return Err(method, "OTHER", "missing or invalid amount");
                    var amountSat = amountMsat / 1000;
                    if (amountSat <= 0)
                        return Err(method, "OTHER", "amount below 1 sat");
                    if (amountSat > _cfg.MaxInvoiceSat)
                        return Err(method, "OTHER", $"amount exceeds the {_cfg.MaxInvoiceSat} sat cap");
                    var description = prm.TryGetProperty("description", out var d) && d.ValueKind == JsonValueKind.String
                        ? d.GetString() ?? "" : "";
                    var inv = await _barkd.CreateInvoiceAsync(amountSat, description.Length > 0 ? description : "nwc invoice", ct);
                    return Ok(method, new
                    {
                        type = "incoming",
                        invoice = inv.Invoice,
                        payment_hash = inv.PaymentHash,
                        amount = amountSat * 1000,
                        created_at = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                        description,
                    });
                }

            case "lookup_invoice":
                {
                    var hash = prm.ValueKind == JsonValueKind.Object && prm.TryGetProperty("payment_hash", out var h) && h.ValueKind == JsonValueKind.String
                        ? h.GetString() : null;
                    if (string.IsNullOrWhiteSpace(hash))
                        return Err(method, "OTHER", "payment_hash required");
                    var st = await _barkd.GetReceiveStatusAsync(hash!, ct);
                    if (!st.Found)
                        return Err(method, "NOT_FOUND", "invoice not found");
                    return Ok(method, new
                    {
                        type = "incoming",
                        payment_hash = hash,
                        settled_at = st.Settled && st.FinishedAt is { } f ? new DateTimeOffset(f, TimeSpan.Zero).ToUnixTimeSeconds() : (long?)null,
                    });
                }

            case "list_transactions":
                {
                    var moves = await _barkd.ListLightningMovementsAsync(500, ct);
                    long? from = TryLong(prm, "from");
                    long? until = TryLong(prm, "until");
                    var typeFilter = prm.ValueKind == JsonValueKind.Object && prm.TryGetProperty("type", out var tf) && tf.ValueKind == JsonValueKind.String
                        ? tf.GetString() : null;
                    var limit = (int)Math.Clamp(TryLong(prm, "limit") ?? 20, 1, 200);
                    var offset = (int)Math.Max(0, TryLong(prm, "offset") ?? 0);

                    static long? Unix(DateTime? d) => d is { } v ? new DateTimeOffset(v, TimeSpan.Zero).ToUnixTimeSeconds() : null;
                    var txs = moves
                        .Where(m => typeFilter is null || m.Direction == typeFilter)
                        .Where(m =>
                        {
                            var u = Unix(m.SettledAt ?? m.CreatedAt) ?? 0;
                            return (from is null || u >= from) && (until is null || u <= until);
                        })
                        .Skip(offset).Take(limit)
                        .Select(m => (object)new
                        {
                            type = m.Direction,
                            invoice = m.Invoice,
                            payment_hash = m.PaymentHash,
                            amount = m.AmountSat * 1000,
                            fees_paid = m.FeeSat * 1000,
                            created_at = Unix(m.CreatedAt),
                            settled_at = Unix(m.SettledAt),
                            description = (string?)null,
                            preimage = (string?)null,
                        })
                        .ToList();
                    return Ok(method, new { transactions = txs });
                }

            case "pay_invoice":
                {
                    if (_payCap is null)
                        return Err(method, "NOT_IMPLEMENTED", "pay_invoice is disabled");
                    var invoice = prm.ValueKind == JsonValueKind.Object && prm.TryGetProperty("invoice", out var iv) && iv.ValueKind == JsonValueKind.String
                        ? iv.GetString() : null;
                    if (string.IsNullOrWhiteSpace(invoice))
                        return Err(method, "OTHER", "invoice required");
                    var invoiceMsat = Bolt11.AmountMsat(invoice!);
                    long? paramAmt = prm.TryGetProperty("amount", out var am) && am.TryGetInt64(out var a) ? a : null;
                    var amountMsat = invoiceMsat ?? paramAmt;
                    if (amountMsat is null || amountMsat <= 0)
                        return Err(method, "OTHER", "amount required for an amountless invoice");
                    var amountSat = (amountMsat.Value + 999) / 1000; // ceil to sat for the cap
                    if (!_payCap.TryReserve(amountSat))
                        return Err(method, "QUOTA_EXCEEDED", $"daily {_payCap.CapSat} sat cap reached ({_payCap.SpentToday}/{_payCap.CapSat} used)");
                    try
                    {
                        // amountless ⇒ tell barkd the amount; else barkd reads it from the invoice.
                        await _barkd.PayInvoiceAsync(invoice!, invoiceMsat is null ? amountSat : null, null, ct);
                        _log.LogInformation("[nwc] PAID {Sat} sat ({Spent}/{Cap} today)", amountSat, _payCap.SpentToday, _payCap.CapSat);
                        return Ok(method, new { preimage = "" }); // barkd returns only a success message, no preimage
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        _payCap.Refund(amountSat);
                        _log.LogWarning(ex, "[nwc] pay_invoice failed — refunded {Sat} sat to the cap", amountSat);
                        return Err(method, "PAYMENT_FAILED", "payment failed");
                    }
                }

            default:
                return Err(method, "NOT_IMPLEMENTED", $"method '{method}' is not supported");
        }
    }

    private static object Ok(string method, object result) => new { result_type = method, result };
    private static object Err(string method, string code, string message) => new { result_type = method, error = new { code, message } };

    private static long? TryLong(JsonElement prm, string name) =>
        prm.ValueKind == JsonValueKind.Object && prm.TryGetProperty(name, out var v) && v.TryGetInt64(out var n) ? n : null;

    private NostrEvent InfoEvent() => NwcCrypto.Sign(_cfg.PrivateKeyHex, new NostrEvent
    {
        CreatedAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
        Kind = 13194,
        Tags = new List<string[]> { new[] { "encryption", "nip44_v2" } },
        Content = string.Join(" ", _cfg.Methods()),
    });

    private Task PublishAsync(ClientWebSocket ws, NostrEvent evt, CancellationToken ct) =>
        SendRawAsync(ws, JsonSerializer.Serialize(new object[] { "EVENT", evt }, _json), ct);

    private static async Task SendRawAsync(ClientWebSocket ws, string text, CancellationToken ct) =>
        await ws.SendAsync(Encoding.UTF8.GetBytes(text), WebSocketMessageType.Text, endOfMessage: true, ct);

    private NostrEvent? ParseEvent(JsonElement e)
    {
        try
        {
            var evt = new NostrEvent
            {
                Id = e.GetProperty("id").GetString() ?? "",
                Pubkey = e.GetProperty("pubkey").GetString() ?? "",
                CreatedAt = e.GetProperty("created_at").GetInt64(),
                Kind = e.GetProperty("kind").GetInt32(),
                Content = e.TryGetProperty("content", out var c) ? c.GetString() ?? "" : "",
                Sig = e.TryGetProperty("sig", out var s) ? s.GetString() ?? "" : "",
            };
            if (e.TryGetProperty("tags", out var tags) && tags.ValueKind == JsonValueKind.Array)
                foreach (var tag in tags.EnumerateArray())
                    evt.Tags.Add(tag.EnumerateArray().Select(t => t.GetString() ?? "").ToArray());
            return evt;
        }
        catch
        {
            return null;
        }
    }
}
