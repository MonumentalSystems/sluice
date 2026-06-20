using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using Sluice.Barkd;
using Sluice.Nostr;
using Sluice.Nwc;
using Sluice.TestKit;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace Sluice.Tests;

/// <summary>
/// Drives the live-websocket relay path of <see cref="NwcBridge"/> — ExecuteAsync → RunRelayAsync (connect,
/// publish info, send REQ) → ReadLoopAsync → OnRelayMessageAsync → HandleRequestAsync (sig-verify, NIP-44
/// decrypt, dispatch, encrypt, sign, publish) → PublishAsync/SendRawAsync — against an in-process
/// <see cref="HttpListener"/> websocket relay. The test acts as the NWC client: it NIP-44-encrypts + signs a
/// kind:23194 request, sends it over the relay, then reads + decrypts the kind:23195 response. Every receive is
/// bounded by a 10s token so a missing frame fails fast instead of hanging.
/// </summary>
public sealed class NwcBridgeRelayTests
{
    // Well-known valid 64-hex vectors (same family the dispatch tests use).
    private const string ServicePriv = "0000000000000000000000000000000000000000000000000000000000000001";
    private const string ClientPriv = "1111111111111111111111111111111111111111111111111111111111111111";
    private const string StrangerPriv = "2222222222222222222222222222222222222222222222222222222222222222";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private static IBarkdClient BarkdOver(FakeBarkd fake)
    {
        var options = new BarkdClientOptions { BaseUrl = "http://barkd.test:3535", Token = "t" };
        return new BarkdClient(Options.Create(options), fake, NullLogger<BarkdClient>.Instance);
    }

    private static int FreePort()
    {
        var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        var port = ((IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        return port;
    }

    /// <summary>A signed kind:23194 request event from <paramref name="clientPriv"/> to the service pubkey,
    /// NIP-44-encrypting the inner <paramref name="payloadJson"/>.</summary>
    private static NostrEvent ClientRequest(string clientPriv, string servicePub, string payloadJson)
    {
        var cipher = Nip44.Encrypt(clientPriv, servicePub, payloadJson);
        var evt = new NostrEvent
        {
            CreatedAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            Kind = 23194,
            Tags = new List<string[]> { new[] { "p", servicePub } },
            Content = cipher,
        };
        return NwcCrypto.Sign(clientPriv, evt);
    }

    private static string FrameEvent(string sub, NostrEvent evt) =>
        JsonSerializer.Serialize(new object[] { "EVENT", sub, evt }, Json);

    private static Task SendTextAsync(WebSocket ws, string text, CancellationToken ct) =>
        ws.SendAsync(Encoding.UTF8.GetBytes(text), WebSocketMessageType.Text, endOfMessage: true, ct);

    /// <summary>Read one full text frame (bounded by <paramref name="ct"/>).</summary>
    private static async Task<string> ReceiveTextAsync(WebSocket ws, CancellationToken ct)
    {
        var buf = new byte[64 * 1024];
        using var msg = new MemoryStream();
        WebSocketReceiveResult res;
        do
        {
            res = await ws.ReceiveAsync(buf, ct);
            if (res.MessageType == WebSocketMessageType.Close)
                throw new InvalidOperationException("relay peer closed");
            msg.Write(buf, 0, res.Count);
        } while (!res.EndOfMessage);
        return Encoding.UTF8.GetString(msg.ToArray());
    }

    /// <summary>Read frames until one parses as a kind:<paramref name="kind"/> EVENT response, or the token
    /// fires. Non-matching frames (info publish, OK, etc.) are skipped.</summary>
    private static async Task<NostrEvent> ReadResponseEventAsync(WebSocket ws, int kind, CancellationToken ct)
    {
        while (true)
        {
            var text = await ReceiveTextAsync(ws, ct);
            using var doc = JsonDocument.Parse(text);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Array || root.GetArrayLength() < 2)
                continue;
            if (root[0].GetString() != "EVENT")
                continue;
            // The bridge publishes ["EVENT", event] (2 elements); the info event is kind 13194.
            var eventEl = root[root.GetArrayLength() - 1];
            var evt = JsonSerializer.Deserialize<NostrEvent>(eventEl.GetRawText(), Json)!;
            if (evt.Kind == kind)
                return evt;
        }
    }

    private static NwcConfig RelayCfg(int port, long maxDailyPaySat = 0) => new()
    {
        Enabled = true,
        PrivateKeyHex = ServicePriv,
        Relays = { $"ws://localhost:{port}/" },
        ConnectionSecrets = { ClientPriv },
        WalletName = "barkd-relay-test",
        MaxInvoiceSat = 1_000_000,
        MaxDailyPaySat = maxDailyPaySat,
    };

    /// <summary>Accept ONE server-side websocket from the bridge, bounded by <paramref name="ct"/>.</summary>
    private static async Task<WebSocket> AcceptAsync(HttpListener listener, CancellationToken ct)
    {
        var ctxTask = listener.GetContextAsync();
        var ctx = await ctxTask.WaitAsync(ct);
        var wsCtx = await ctx.AcceptWebSocketAsync(subProtocol: null).WaitAsync(ct);
        return wsCtx.WebSocket;
    }

    [Fact]
    public async Task RelayRoundTrip_get_balance_returns_msat()
    {
        var port = FreePort();
        using var listener = new HttpListener();
        listener.Prefixes.Add($"http://localhost:{port}/");
        listener.Start();

        var servicePub = NwcCrypto.PubKeyHex(ServicePriv);
        var sub = "client-sub";

        var fake = new FakeBarkd { SpendableSat = 7_000 };
        var bridge = new NwcBridge(RelayCfg(port), BarkdOver(fake), NullLogger<NwcBridge>.Instance);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        try
        {
            await bridge.StartAsync(cts.Token); // kicks ExecuteAsync → connects to our relay
            using var server = await AcceptAsync(listener, cts.Token);

            // Drain the bridge's startup publish (info EVENT) and REQ — exercises PublishAsync/SendRawAsync.
            // Then send a request; an EOSE before it exercises the non-EVENT no-op path.
            await SendTextAsync(server, JsonSerializer.Serialize(new object[] { "EOSE", sub }), cts.Token);
            await SendTextAsync(server, "this is not json", cts.Token); // OnRelayMessageAsync catch branch

            var req = ClientRequest(ClientPriv, servicePub, """{"method":"get_balance"}""");
            await SendTextAsync(server, FrameEvent(sub, req), cts.Token);

            var resp = await ReadResponseEventAsync(server, 23195, cts.Token);
            Assert.Equal(23195, resp.Kind);
            Assert.Equal(servicePub, resp.Pubkey);
            Assert.True(NwcCrypto.Verify(resp)); // bridge produced a valid signed event

            var plain = Nip44.Decrypt(ClientPriv, servicePub, resp.Content);
            Assert.NotNull(plain);
            using var rd = JsonDocument.Parse(plain!);
            Assert.Equal("get_balance", rd.RootElement.GetProperty("result_type").GetString());
            Assert.Equal(7_000_000, rd.RootElement.GetProperty("result").GetProperty("balance").GetInt64());

            // The response tags reference our request (p=client, e=requestId).
            Assert.Contains(resp.Tags, t => t.Length == 2 && t[0] == "e" && t[1] == req.Id);
        }
        finally
        {
            await bridge.StopAsync(CancellationToken.None);
            listener.Stop();
        }
    }

    [Fact]
    public async Task RelayRoundTrip_get_info_returns_alias_and_pubkey()
    {
        var port = FreePort();
        using var listener = new HttpListener();
        listener.Prefixes.Add($"http://localhost:{port}/");
        listener.Start();

        var servicePub = NwcCrypto.PubKeyHex(ServicePriv);
        var bridge = new NwcBridge(RelayCfg(port), BarkdOver(new FakeBarkd()), NullLogger<NwcBridge>.Instance);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        try
        {
            await bridge.StartAsync(cts.Token);
            using var server = await AcceptAsync(listener, cts.Token);

            var req = ClientRequest(ClientPriv, servicePub, """{"method":"get_info"}""");
            await SendTextAsync(server, FrameEvent(sub: "s", evt: req), cts.Token);

            var resp = await ReadResponseEventAsync(server, 23195, cts.Token);
            var plain = Nip44.Decrypt(ClientPriv, servicePub, resp.Content);
            Assert.NotNull(plain);
            using var rd = JsonDocument.Parse(plain!);
            var result = rd.RootElement.GetProperty("result");
            Assert.Equal("barkd-relay-test", result.GetProperty("alias").GetString());
            Assert.Equal(servicePub, result.GetProperty("pubkey").GetString());
        }
        finally
        {
            await bridge.StopAsync(CancellationToken.None);
            listener.Stop();
        }
    }

    [Fact]
    public async Task RelayRoundTrip_ignores_a_request_from_an_unknown_client()
    {
        var port = FreePort();
        using var listener = new HttpListener();
        listener.Prefixes.Add($"http://localhost:{port}/");
        listener.Start();

        var servicePub = NwcCrypto.PubKeyHex(ServicePriv);
        var bridge = new NwcBridge(RelayCfg(port), BarkdOver(new FakeBarkd { SpendableSat = 7_000 }),
            NullLogger<NwcBridge>.Instance);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        try
        {
            await bridge.StartAsync(cts.Token);
            using var server = await AcceptAsync(listener, cts.Token);

            // A request signed by a key NOT in ConnectionSecrets → HandleRequestAsync early-returns; no response.
            var stranger = ClientRequest(StrangerPriv, servicePub, """{"method":"get_balance"}""");
            await SendTextAsync(server, FrameEvent("s", stranger), cts.Token);

            // Then a legit request from the allowed client. If the stranger had been answered, we'd read its
            // response first; instead the FIRST 23195 we see must be the reply to the allowed request.
            var allowed = ClientRequest(ClientPriv, servicePub, """{"method":"get_balance"}""");
            await SendTextAsync(server, FrameEvent("s", allowed), cts.Token);

            var resp = await ReadResponseEventAsync(server, 23195, cts.Token);
            // The single response must reference the ALLOWED request id, never the stranger's.
            Assert.Contains(resp.Tags, t => t.Length == 2 && t[0] == "e" && t[1] == allowed.Id);
            Assert.DoesNotContain(resp.Tags, t => t.Length == 2 && t[0] == "e" && t[1] == stranger.Id);
        }
        finally
        {
            await bridge.StopAsync(CancellationToken.None);
            listener.Stop();
        }
    }
}
