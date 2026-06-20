# sluice

[![NuGet](https://img.shields.io/nuget/v/Sluice.svg)](https://www.nuget.org/packages/Sluice/)
[![Downloads](https://img.shields.io/nuget/dt/Sluice.svg)](https://www.nuget.org/packages/Sluice/)
[![License: MIT](https://img.shields.io/badge/License-MIT-blue.svg)](LICENSE)

Self-hostable **Ark + Lightning** payment primitives. sluice is two independent
artifacts:

1. **`barkd` self-host image** (`deploy/barkd/`) — a small, multi-arch Docker image
   that runs bark's official `barkd` wallet daemon. The image contains **no bark
   source**: it downloads the official upstream release binary and verifies it by
   SHA256 at build time (see `NOTICE`). You bring a datadir/PVC and a mnemonic; the
   daemon exposes bark's REST API.

2. **`Sluice` .NET library** (`src/Sluice/`) — a dependency-light
   .NET 10 client + protocol library that talks to a running `barkd`:
   - `IBarkdClient` / `BarkdClient` — create invoices, poll settlement, read
     balance, list Lightning movements over the `barkd` REST API.
   - **BOLT11** parsing — payment-hash extraction and amount decoding.
   - **LNURL-pay** receive surface (`GatewayLnurl`) — LUD-06/16 Lightning Address
     endpoints (receive-only).
   - **NIP-47 / Nostr Wallet Connect (NWC)** bridge — exposes the wallet over
     nostr with **NIP-44 v2** crypto (BIP-340 schnorr, ChaCha20, HKDF).

   A companion `Sluice.TestKit` package ships fakes/fixtures (e.g. a fake
   `IBarkdClient`) for downstream unit and integration tests.

> The wallet runtime lives entirely in the `barkd` pod. `Sluice` never holds
> keys or signs — it only talks to the daemon's REST API.

bark / Ark is developed by **Ark Labs / Second**:
- https://second.tech
- https://gitlab.com/ark-bitcoin/bark

> **Status:** this repository is **private during verification** and is **intended
> to be made public**. Treat anything here as pre-1.0.

---

## Verified against barkd 0.2.5

The `deploy/barkd` image pins and verifies **barkd `0.2.5`** (a security release),
and the `Sluice` client + integration tests have been exercised against that
version. Newer barkd releases may change the REST surface — re-verify before
bumping the `BARKD_VERSION` pin.

---

## Quick start — barkd self-host image

Build the multi-arch image (binary is fetched + SHA256-verified from the official
GitLab release; nothing is compiled from bark source):

```sh
docker buildx build \
  --platform linux/amd64,linux/arm64 \
  -t <YOUR_REGISTRY>/barkd:0.2.5 \
  --push \
  deploy/barkd
```

Run it (mount a persistent datadir — **losing it loses the wallet**, recoverable
only from your mnemonic backup):

```sh
docker run -d --name barkd \
  -v barkd-data:/var/lib/bark \
  -p 3535:3535 \
  <YOUR_REGISTRY>/barkd:0.2.5
```

On first start `barkd` initializes the datadir and generates its bearer auth token
(read it out of the datadir). The wallet itself (network / Ark server / chain
source) is created afterwards via `POST /api/v1/wallet/create` — the daemon takes
no network flags; wallet config persists into the datadir. See bark's docs
(https://second.tech) for the full wallet-create payload.

`/ping` is unauthenticated; every other route requires the bearer token.

A local **regtest** stack (upstream `secondark/*` + `bitcoin/bitcoin` images, under
their own licenses — see `NOTICE`) lives under `deploy/barkd/regtest/` for
end-to-end testing without touching mainnet.

---

## Quick start — Sluice library

The packages publish to the gnostr-cloud (uranus) NuGet feed. Add the feed source
and reference the package:

```sh
dotnet nuget add source https://<YOUR_NUGET_FEED>/nuget/v3/index.json -n sluice
dotnet add package Sluice
```

Wire the client to a running `barkd` (placeholders — supply your own daemon URL and
bearer token, e.g. from configuration or a secret store):

```csharp
services.AddHttpClient<IBarkdClient, BarkdClient>(c =>
{
    c.BaseAddress = new Uri("<BARKD_BASE_URL>");          // e.g. http://barkd:3535
    c.DefaultRequestHeaders.Authorization =
        new AuthenticationHeaderValue("Bearer", "<BARKD_BEARER_TOKEN>");
});

// create an invoice, then poll for settlement
var invoice = await barkd.CreateInvoiceAsync(amountSat: 1000, memo: "demo", ct);
var settled = await barkd.WaitSettledAsync(invoice.PaymentHash, ct);
```

For the NWC bridge, point it at your nostr relay (placeholder) and the wallet's
NWC connection secret:

```csharp
// relay URL + NWC secret are placeholders — wire your own
var nwc = new NwcBridge("<NOSTR_RELAY_URL>", "<NWC_CONNECTION_SECRET>");
```

Build / test the libraries locally:

```sh
dotnet build Sluice.slnx -c Release
dotnet test  Sluice.slnx -c Release --filter "Speed!=Slow"   # fast unit subset
```

---

## Protocol references

sluice implements public specifications independently — see `NOTICE` for full
attribution and licensing:

- **NIP-44 v2** (encryption) — conformance is checked against the official NIP-44 v2
  test vectors.
- **NIP-47 / NWC**, **NIP-01** (nostr).
- **BOLT #11** (Lightning invoices).

---

## Security & no-warranty

- sluice is provided under the MIT License **with no warranty of any kind** (see
  `LICENSE`). It moves real Bitcoin over Ark / Lightning — **use at your own risk.**
- **Handle your mnemonic safely.** The wallet seed is the only recovery path. Keep
  the `barkd` datadir (which holds the wallet + auth secret) on durable, backed-up,
  access-controlled storage, and keep your **mnemonic backup offline**. Anyone with
  the datadir or the mnemonic can spend the funds.
- Never commit tokens, mnemonics, relay secrets, NWC connection strings, or
  registry credentials. All such values in this README are **placeholders**.
- The bundled regtest stack is for testing only — never point it at, or fund it
  from, a mainnet wallet.

bark / barkd itself is third-party software governed by its own license; sluice
packages it but does not relicense or warrant it.
