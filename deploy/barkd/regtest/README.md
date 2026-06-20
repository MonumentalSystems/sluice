# Ark regtest harness — payments integration tests

Runs a complete local Ark stack and two `barkd` wallets, so the gated
payment integration tests (`BARKD_TEST_*`) can drive a **real** Lightning payment end-to-end: the
checkout grain creates an invoice on the merchant `barkd`, the payer `barkd` pays it, and the
grain's settle poll flips the checkout Paid.

```bash
./regtest.sh up      # start + bootstrap + print the BARKD_TEST_* exports
# Paste the printed exports, then run the library's IntegrationTests with the BARKD_TEST_* vars set
# (the payment tests skip themselves when those vars are unset). Filter to the Barkd tests, e.g.:
#   dotnet test --filter FullyQualifiedName~Barkd
./regtest.sh down    # tear down (volumes included)
```

## What's in the stack

| service | image | role |
|---|---|---|
| `bitcoind` | `bitcoin/bitcoin:30.0` (regtest) | chain + block faucet |
| `cln` | `docker.io/secondark/cln-hold:v26.04.1` | Core Lightning + Boltz hold plugin — captaind's LN leg |
| `captaind` | `docker.io/secondark/captaind:latest` | bark's Ark server (bundles its own PostgreSQL) |
| `barkd-merchant` | built from `../Dockerfile` | the wallet the application points at (`:3635`) |
| `barkd-payer` | built from `../Dockerfile` | pays the invoices (`:3636`) |

The bitcoind/cln/captaind trio **mirrors bark's own `contrib/docker/docker-compose.yml`**
(same service names, volume layout and `second`/`ark` rpcauth — regtest-only throwaway credentials)
because the published `captaind` image ships a baked-in regtest config
(`/root/captaind/captaind.toml`) wired to exactly that layout. `regtest.sh` then follows the
upstream funding procedure (mine 106, send 1 BTC to `captaind rpc wallet → rounds.address`), creates
both barkd wallets via `POST /api/v1/wallet/create` (`network: regtest`,
`ark_server: http://captaind:3535`, bitcoind chain source), and boards faucet coins into the payer's
Ark balance.

## Verified

This harness was run **end-to-end** on 2026-06-11 (arm64 host, amd64 images under qemu): all three
`LiveBarkdPaymentTests` passed, including the full loop — checkout grain → real BOLT11 invoice on
the merchant → `POST /api/v1/lightning/pay` from the payer → settle observed by the grain's poll
timer → `Paid` + premium granted (~4 s). The self-loop Lightning payment (both wallets on the same
Ark server, hold-plugin CLN) settles fine on this single-node stack.

Also verified statically against the `bark-0.2.3` tag of https://gitlab.com/ark-bitcoin/bark and
https://second.tech/docs: the whole barkd REST surface used here and by the client
(paths + schemas from the official `bark-rest/openapi.json`), and the release-binary SHA256s in
`../Dockerfile`.

## Upstream gotchas this harness works around (keep in mind when bumping images)

- **`secondark/captaind:0.2.3` is broken**: its baked config template is missing the `[vtxopool]`
  section its own binary requires ⇒ instant crash. `:latest`'s template is *also* stale, so we
  mount our own `captaind.toml` (upstream `server/captaind.default.toml` + the compose-layout
  overrides) over `/root/captaind/captaind.toml`.
- **`bitcoind.url` needs an explicit scheme** (`http://bitcoind:18443`) — the baked template's
  bare `bitcoind:18443` fails URL parsing on current captaind.
- **`cln` must have `hostname: cln`** — the cln-grpc plugin self-signs its TLS cert for the
  container hostname, and captaind dials `https://cln:9736` (BadCertificate otherwise). Its
  `start.sh` also touches `/root/.lightning/regtest/config` before the dir exists, hence the
  `mkdir -p` entrypoint wrapper.
- **amd64-only upstream images**: on arm64 hosts install the qemu handler once:
  `docker run --privileged --rm tonistiigi/binfmt --install amd64`.
- captaind's first boot initializes its bundled PostgreSQL (slower under qemu); every `regtest.sh`
  step is idempotent — just re-run `./regtest.sh up` if a step races the boot.
- **The very first Lightning payment after a fresh bootstrap can time out** (the server still
  issuing its vtxopool / running its first rounds): observed once — the first
  `Checkout_flips_paid…` run failed at the 2-minute cap, every later run settles in ~4 s. Give a
  fresh stack a minute, or just re-run the test.

The integration tests skip (never fail) when the env vars are unset or the daemons are unreachable,
so a missing harness cannot break CI.
