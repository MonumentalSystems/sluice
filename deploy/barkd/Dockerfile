# barkd — bark's official REST wallet daemon (https://second.tech/docs/barkd/).
#
# Multi-arch (linux/amd64 + linux/arm64) from the OFFICIAL GitLab release binaries (no Rust build stage
# needed — the bark-0.2.3 release publishes linux-x86_64 / linux-arm64 / linux-armv7 barkd binaries),
# pinned by SHA256 from the release's SHA256SUMS asset.
#
#   docker buildx build --platform linux/amd64,linux/arm64 -t <registry>/barkd:0.2.3 deploy/barkd
#
# Runtime: the wallet lives in the datadir (/var/lib/bark — mount a PVC). On first start barkd
# initializes the datadir and generates the bearer auth token; the wallet itself (network/ark
# server/chain source) is created afterwards via POST /api/v1/wallet/create (see README.md — the
# daemon takes no network flags; wallet config persists into the datadir).
FROM debian:bookworm-slim

ARG BARKD_VERSION=0.2.5
# Set by buildx; for a plain single-arch `docker build` BuildKit also provides it.
ARG TARGETARCH

# SHA256SUMS from https://gitlab.com/ark-bitcoin/bark/-/releases/bark-0.2.5 (asset: SHA256SUMS).
# 0.2.5 is a SECURITY release (panic-resistant daemon, hardened VTXO decode, round nonce-reuse guard,
# LN-receive HTLC-expiry validation) + 0.2.4's no-auto-exit-on-failed-LN-claim. Behavior change: bark
# must stay in memory across a round — fine here (barkd is a persistent always-on pod).
ARG SHA256_AMD64=e9a3464cbd91b8b4fb42ee36aafed70f59fbb143189f0c368c3efa4f06d4490e
ARG SHA256_ARM64=778e6aa353fc9a32b51e4ac36c03d945c7288942294f04a918199015517b9255

RUN set -eux; \
    apt-get update; \
    apt-get install -y --no-install-recommends ca-certificates curl; \
    rm -rf /var/lib/apt/lists/*; \
    arch="${TARGETARCH:-}"; \
    if [ -z "$arch" ]; then \
      case "$(uname -m)" in x86_64) arch=amd64 ;; aarch64) arch=arm64 ;; *) echo "unsupported arch $(uname -m)"; exit 1 ;; esac; \
    fi; \
    case "$arch" in \
      amd64) asset="barkd-${BARKD_VERSION}-linux-x86_64"; sha="${SHA256_AMD64}" ;; \
      arm64) asset="barkd-${BARKD_VERSION}-linux-arm64";  sha="${SHA256_ARM64}" ;; \
      *) echo "unsupported TARGETARCH ${arch}"; exit 1 ;; \
    esac; \
    curl -fL "https://gitlab.com/ark-bitcoin/bark/-/releases/bark-${BARKD_VERSION}/downloads/${asset}" \
      -o /usr/local/bin/barkd; \
    echo "${sha}  /usr/local/bin/barkd" | sha256sum -c -; \
    chmod +x /usr/local/bin/barkd; \
    /usr/local/bin/barkd --version

# Wallet data + the daemon's auth secret. Mount persistent storage here — losing it loses the wallet
# (recoverable only from the mnemonic backup).
VOLUME /var/lib/bark

# REST API (default 3535; /ping is unauthenticated, everything else needs the bearer token).
EXPOSE 3535
ENV BARKD_BIND_HOST=0.0.0.0 \
    BARKD_BIND_PORT=3535

ENTRYPOINT ["barkd", "--datadir", "/var/lib/bark"]
