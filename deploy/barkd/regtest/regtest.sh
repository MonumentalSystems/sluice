#!/usr/bin/env bash
# Bootstrap the local Ark regtest stack (docker-compose.yml beside this script) for the gated
# payments integration tests:
#
#   ./regtest.sh up        # start everything, create+fund wallets, print BARKD_TEST_* env
#   ./regtest.sh env       # re-print the env vars (stack already up)
#   ./regtest.sh down      # tear down (incl. volumes)
#
# NOTE: the "second"/"ark" bitcoind rpcauth pair used below is a REGTEST-ONLY throwaway credential
# (matches bark's upstream compose so captaind's baked config resolves) — never reuse it anywhere real.
#
# Flow: bitcoind mines a regtest chain → captaind (Ark server) gets funded → merchant + payer barkd
# wallets are created against it → the payer boards on-chain coins into Ark so it can pay Lightning
# invoices. See README.md for the verified-vs-best-effort notes.
set -euo pipefail
cd "$(dirname "$0")"

COMPOSE="docker compose -f docker-compose.yml"
MERCHANT_URL=${MERCHANT_URL:-http://localhost:3635}
PAYER_URL=${PAYER_URL:-http://localhost:3636}

bcli() {
  $COMPOSE exec -T bitcoind bitcoin-cli -regtest -rpcuser=second -rpcpassword=ark -rpcconnect=127.0.0.1 "$@"
}

barkd_secret() { # <service>
  $COMPOSE exec -T "$1" barkd --datadir /var/lib/bark secret show | tr -d '[:space:]'
}

api() { # <base-url> <token> <method> <path> [json-body]
  local url=$1 token=$2 method=$3 path=$4 body=${5:-}
  if [ -n "$body" ]; then
    curl -sf -X "$method" "$url/api/v1$path" -H "Authorization: Bearer $token" \
      -H 'Content-Type: application/json' -d "$body"
  else
    curl -sf -X "$method" "$url/api/v1$path" -H "Authorization: Bearer $token"
  fi
}

wait_http() { # <url> <label> [tries]
  local url=$1 label=$2 tries=${3:-60}
  for _ in $(seq 1 "$tries"); do
    if curl -sf -o /dev/null "$url"; then return 0; fi
    sleep 2
  done
  echo "ERROR: $label did not become reachable at $url" >&2
  return 1
}

ensure_wallet() { # <base-url> <token> <label>
  local url=$1 token=$2 label=$3
  local fp
  fp=$(api "$url" "$token" GET /wallet | python3 -c 'import json,sys; print(json.load(sys.stdin).get("fingerprint") or "")')
  if [ -n "$fp" ]; then
    echo "  $label wallet already exists ($fp)"
    return 0
  fi
  echo "  creating $label wallet (regtest, ark server captaind, bitcoind chain source)…"
  api "$url" "$token" POST /wallet/create '{
    "network": "regtest",
    "ark_server": "http://captaind:3535",
    "chain_source": { "bitcoind": { "bitcoind": "http://bitcoind:18443",
                                    "bitcoind_auth": { "user-pass": { "user": "second", "pass": "ark" } } } }
  }'
  echo
}

print_env() {
  local mtoken ptoken
  mtoken=$(barkd_secret barkd-merchant)
  ptoken=$(barkd_secret barkd-payer)
  cat <<EOF

# ── Integration-test env (paste into your shell) ────────────────────────────────
export BARKD_TEST_URL=$MERCHANT_URL
export BARKD_TEST_TOKEN=$mtoken
export BARKD_TEST_PAYER_URL=$PAYER_URL
export BARKD_TEST_PAYER_TOKEN=$ptoken
# then run the library's IntegrationTests with the BARKD_TEST_* vars set (filter ~Barkd).
EOF
}

case "${1:-up}" in
  down)
    $COMPOSE down -v
    exit 0
    ;;
  env)
    print_env
    exit 0
    ;;
  up) ;;
  *) echo "usage: $0 [up|env|down]" >&2; exit 1 ;;
esac

echo "── starting the stack…"
$COMPOSE up -d --build

echo "── waiting for the barkd daemons (unauthenticated /ping)…"
wait_http "$MERCHANT_URL/ping" "barkd-merchant"
wait_http "$PAYER_URL/ping" "barkd-payer"

MTOKEN=$(barkd_secret barkd-merchant)
PTOKEN=$(barkd_secret barkd-payer)

echo "── waiting for captaind's admin RPC (first boot initializes its bundled PostgreSQL)…"
for _ in $(seq 1 60); do
  if $COMPOSE exec -T captaind captaind --config /root/captaind/captaind.toml rpc wallet >/dev/null 2>&1; then
    break
  fi
  sleep 3
done

echo "── funding the Ark server (captaind) from the bitcoind faucet…"
bcli createwallet faucet >/dev/null 2>&1 || bcli loadwallet faucet >/dev/null 2>&1 || true
CAPTAIND_ADDRESS=$($COMPOSE exec -T captaind captaind --config /root/captaind/captaind.toml rpc wallet \
  | python3 -c 'import json,sys; print(json.load(sys.stdin)["rounds"]["address"])')
bcli -generate 106 >/dev/null
bcli sendtoaddress "$CAPTAIND_ADDRESS" 1 >/dev/null
bcli -generate 3 >/dev/null
echo "  captaind funded at $CAPTAIND_ADDRESS"

echo "── creating wallets…"
ensure_wallet "$MERCHANT_URL" "$MTOKEN" merchant
ensure_wallet "$PAYER_URL" "$PTOKEN" payer

echo "── funding the payer (faucet → on-chain → board into Ark)…"
PAYER_ADDR=$(api "$PAYER_URL" "$PTOKEN" POST /onchain/addresses/next \
  | python3 -c 'import json,sys; print(json.load(sys.stdin)["address"])')
bcli sendtoaddress "$PAYER_ADDR" 0.01 >/dev/null
bcli -generate 6 >/dev/null
# Wait until the on-chain wallet actually SEES the coins before boarding (the sync is async).
for _ in $(seq 1 30); do
  api "$PAYER_URL" "$PTOKEN" POST /onchain/sync >/dev/null || true
  ONCHAIN=$(api "$PAYER_URL" "$PTOKEN" GET /onchain/balance \
    | python3 -c 'import json,sys; print(json.load(sys.stdin)["trusted_spendable_sat"])')
  if [ "$ONCHAIN" -gt 0 ]; then break; fi
  sleep 2
done
echo "  payer on-chain: ${ONCHAIN:-0} sat"
api "$PAYER_URL" "$PTOKEN" POST /boards/board-all '{}' >/dev/null
bcli -generate 12 >/dev/null   # boards need on-chain confirmations before the VTXO is spendable
api "$PAYER_URL" "$PTOKEN" POST /wallet/sync >/dev/null || true

echo "── waiting for the payer's spendable Ark balance…"
for _ in $(seq 1 30); do
  SPENDABLE=$(api "$PAYER_URL" "$PTOKEN" GET /wallet/balance \
    | python3 -c 'import json,sys; print(json.load(sys.stdin)["spendable_sat"])')
  if [ "$SPENDABLE" -gt 0 ]; then break; fi
  bcli -generate 1 >/dev/null
  api "$PAYER_URL" "$PTOKEN" POST /wallet/sync >/dev/null || true
  sleep 2
done
echo "  payer spendable: ${SPENDABLE:-0} sat"
if [ "${SPENDABLE:-0}" -le 0 ]; then
  echo "WARNING: payer balance is still 0 — boards may need more confirmations; re-run '$0 env' later" >&2
fi

print_env
