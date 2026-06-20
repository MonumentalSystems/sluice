# barkd — self-host runbook (Ark/Lightning wallet daemon)

[barkd](https://second.tech/docs/barkd/) is bark's official REST wallet daemon (bearer-auth,
`:3535/api/v1`). The image in this directory wraps the **upstream official barkd release binary**
published by the bark project at https://gitlab.com/ark-bitcoin/bark — it is SHA256-pinned to a
specific release, not rebuilt from source. Use barkd as a self-hosted Ark/Lightning wallet runtime:
your application creates BOLT11 invoices on it and polls receive status; funds settle into the
daemon's Ark wallet.

The REST surface here was verified against **barkd 0.2.3** (`bark-rest/openapi.json` at the
`bark-0.2.3` tag of https://gitlab.com/ark-bitcoin/bark; the public mainnet Ark server
`ark.second.tech` has been live since 2026-06-09). Pin/verify against the release you build.

## Build & deploy

```bash
# Multi-arch image from the official GitLab release binaries (SHA256-pinned, no Rust build).
# build-push.sh wraps this; or directly:
docker buildx build --platform linux/amd64,linux/arm64 \
  -t <registry>/barkd:<version> --push deploy/barkd

# Then apply the example manifest (edit the placeholders first — namespace, image, storage class):
kubectl apply -f deploy/barkd/k8s.example.yaml   # PVC + Deployment(Recreate) + ClusterIP barkd:3535
```

Single replica, `Recreate` strategy — the wallet is a sqlite datadir on the PVC; two daemons over
one wallet database corrupt it. **Cluster-internal only**: anyone holding the bearer token controls
the funds. Never expose the Service through an ingress/tunnel.

In the examples below, `<barkd-host>` is however your client reaches the daemon (e.g. the in-cluster
Service DNS name `barkd.<namespace>.svc.cluster.local:3535`, or a port-forward to `localhost:3535`),
and `<namespace>` is the namespace you deployed into.

## Bootstrap (one-time)

barkd initializes the datadir and generates its bearer token on first start. The **wallet** is then
created over the API (the daemon has no network/server flags — wallet config persists into the
datadir's `config.toml`):

```bash
# 1. The bearer token (generated into the PVC on first start):
kubectl -n <namespace> exec deploy/barkd -- barkd --datadir /var/lib/bark secret show

# 2. Create the mainnet wallet (a public Ark server + esplora chain source):
TOKEN=...   # from step 1
curl -s -X POST http://<barkd-host>:3535/api/v1/wallet/create \
  -H "Authorization: Bearer ${TOKEN}" -H 'Content-Type: application/json' \
  -d '{
        "network": "mainnet",
        "ark_server": "https://ark.second.tech",
        "chain_source": { "esplora": { "url": "https://mempool.second.tech/api" } }
      }'
# → {"fingerprint":"…"}

# 3. BACK UP THE MNEMONIC (the only wallet recovery path if the PVC dies):
curl -s http://<barkd-host>:3535/api/v1/wallet/mnemonic -H "Authorization: Bearer ${TOKEN}"
# Store it offline. (404 here means the daemon runs with BARKD_EXPOSE_MNEMONIC=false — then read it
# from the datadir instead.) Also snapshot the PVC periodically: the mnemonic alone recovers funds,
# but the datadir also holds VTXO state, movement history and the auth secret.

# 4. Sanity:
curl -s http://<barkd-host>:3535/api/v1/wallet/balance -H "Authorization: Bearer ${TOKEN}"
# → {"spendable_sat":0,"pending_lightning_send_sat":0,...}
```

To rotate the token: `barkd --datadir /var/lib/bark secret refresh` (then update wherever your
application reads the token and restart the pod — the running daemon keeps the old token until
restart).

## Wire it into your application

Point your application at the daemon's base URL and supply the bearer token. Keep the token in a
secret store (a Kubernetes Secret, not a plain env literal):

```bash
BARKD_BASE_URL=http://<barkd-host>:3535
BARKD_TOKEN=<bearer from `secret show`>     # k8s Secret, not a plain env literal
```

Smoke-test reachability with an authenticated balance call (above) before driving real checkouts.

## Maintenance

- **Refresh received VTXOs regularly.** Lightning/arkoor receives arrive as out-of-round VTXOs
  (arkoor trust assumptions) and **expire**; refreshing swaps them for trustless in-round VTXOs with
  a fresh expiry. The background daemon participates in rounds automatically, but eager refresh
  registration is on you:

  ```bash
  curl -s -X POST http://<barkd-host>:3535/api/v1/wallet/refresh/counterparty \
    -H "Authorization: Bearer ${TOKEN}"
  ```

  An optional (and by default **suspended**) CronJob for this lives in
  `refresh-cronjob.example.yaml` — barkd's automatic round participation usually makes it
  unnecessary, and each round costs a small fee. Re-enable only if you want eager arkoor-trust
  removal.

- **Watch expiries**: `GET /api/v1/wallet/vtxos` lists VTXOs with expiry; `GET /api/v1/wallet/balance`
  breaks down spendable vs pending.
- **Hygiene**: never log invoices/tokens; the receive-status endpoint
  (`GET /api/v1/lightning/receives/{payment_hash}`) is the settlement source of truth a checkout
  poller relies on (settled ⇔ `finished_at` **and** `preimage_revealed_at` set).

## Regtest harness

`regtest/` runs the full local stack (bitcoind + captaind + CLN + two barkds) for gated
integration tests — see `regtest/README.md`.
