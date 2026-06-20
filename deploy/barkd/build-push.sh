#!/usr/bin/env bash
# Build + push the multi-arch barkd image (linux/amd64 + linux/arm64).
#
# The image wraps the upstream official barkd release binary from gitlab.com/ark-bitcoin/bark,
# SHA256-pinned in the Dockerfile — there is no source build, so the build is fast and the only
# per-arch difference is which release asset is downloaded.
#
#   Usage: deploy/barkd/build-push.sh <version> [registry]
#     e.g. deploy/barkd/build-push.sh 0.2.5 registry.example.com:5000
#
#   Registry resolution (first set wins):  $2 arg  >  $BARKD_REGISTRY env  >  built-in default.
#   The image is pushed as <registry>/barkd:<version>.
set -euo pipefail

VERSION="${1:?usage: build-push.sh <version> [registry]}"
REGISTRY="${2:-${BARKD_REGISTRY:-localhost:5000}}"
PLATFORMS="${BARKD_PLATFORMS:-linux/amd64,linux/arm64}"
DIR="$(cd "$(dirname "$0")" && pwd)"
IMAGE="$REGISTRY/barkd:$VERSION"

# A buildx builder that can produce a multi-arch manifest (the default `docker` driver cannot).
# Override the builder name with $BUILDX_BUILDER; otherwise create/reuse a local one.
BUILDER="${BUILDX_BUILDER:-barkd-multiarch}"
if ! docker buildx inspect "$BUILDER" >/dev/null 2>&1; then
  echo "creating buildx builder '$BUILDER' (docker-container driver)…"
  docker buildx create --name "$BUILDER" --driver docker-container --use >/dev/null
fi
docker buildx inspect --bootstrap "$BUILDER" >/dev/null

echo "building $IMAGE for $PLATFORMS (barkd $VERSION, upstream release binary)…"
docker buildx build --builder "$BUILDER" \
  --platform "$PLATFORMS" \
  --build-arg BARKD_VERSION="$VERSION" \
  -t "$IMAGE" --push "$DIR"

echo "pushed $IMAGE ($PLATFORMS)"
