#!/usr/bin/env bash
#
# Build and push the multi-arch NetprobeSharp image, stamping OCI version/revision
# labels automatically from git so you never have to pass the SHA by hand.
#
# Usage:
#   ./build-push.sh            # tags :latest, version label = `git describe`
#   ./build-push.sh 0.2.0      # additionally tags :0.2.0
#
# Overridable via env: IMAGE, PLATFORMS.
set -euo pipefail

IMAGE="${IMAGE:-gggdotdev/netprobesharp}"
# Alpine base images (and .NET on musl) exist only for these three.
PLATFORMS="${PLATFORMS:-linux/amd64,linux/arm64,linux/arm/v7}"

# Revision is always the current commit; that's the whole point of the script.
REVISION="$(git rev-parse HEAD)"

# Version: explicit arg wins (also gets its own tag); otherwise derive a label-only
# value from git (nearest tag, else short sha, '-dirty' if the tree has changes).
VERSION_ARG="${1:-}"
if [[ -n "$VERSION_ARG" ]]; then
  VERSION="${VERSION_ARG#v}"
else
  VERSION="$(git describe --tags --always --dirty 2>/dev/null || echo dev)"
  VERSION="${VERSION#v}"
fi

# Dockerfile lives in src/, so that's the build context.
SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
CONTEXT="$SCRIPT_DIR/src"

TAGS=()
[[ -n "$VERSION_ARG" ]] && TAGS+=(-t "$IMAGE:$VERSION")
[[ "$VERSION_ARG" != "dev" ]] && TAGS+=(-t "$IMAGE:latest")

# Multi-platform build+push needs the docker-container driver; the default 'docker'
# driver can't handle more than one platform. Create a dedicated builder once.
BUILDER="netprobe-builder"
if ! docker buildx inspect "$BUILDER" >/dev/null 2>&1; then
  echo ">> creating buildx builder '$BUILDER' (docker-container driver)"
  docker buildx create --name "$BUILDER" --driver docker-container >/dev/null
fi

echo ">> image:     $IMAGE"
echo ">> tags:      ${TAGS[*]//-t /}"
echo ">> version:   $VERSION"
echo ">> revision:  $REVISION"
echo ">> platforms: $PLATFORMS"

docker buildx build \
  --builder "$BUILDER" \
  --platform "$PLATFORMS" \
  --build-arg "VERSION=$VERSION" \
  --build-arg "REVISION=$REVISION" \
  "${TAGS[@]}" \
  --push \
  "$CONTEXT"
