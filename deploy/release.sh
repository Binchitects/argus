#!/usr/bin/env bash
# Build, verify and publish an Argus release image.
#
#   ./deploy/release.sh 0.1.0-rc1
#
# Requires: working DNS, and `docker login ghcr.io` already done. This script
# never handles credentials.
#
# The registry path is lowercase on purpose. Docker rejects uppercase in a
# repository name, so `ghcr.io/aliGhadyani/argus` is not a valid target even
# though the GitHub account is spelled that way.
set -euo pipefail

VERSION="${1:?usage: release.sh VERSION   (e.g. 0.1.0-rc1)}"
REGISTRY="${ARGUS_REGISTRY:-ghcr.io/alighadyani/argus}"

cd "$(dirname "$0")/.."

echo "==> Preflight: can Docker resolve names?"
if ! docker run --rm python:3.13-slim-bookworm \
        sh -c 'getent hosts deb.debian.org >/dev/null' 2>/dev/null; then
    echo "DNS resolution fails inside containers, so the image cannot build." >&2
    echo "This is usually the host's resolver, not Docker: check whether a VPN" >&2
    echo "tunnel is claiming DNS with a server that is not answering." >&2
    exit 1
fi

# --no-cache, because a CACHED test layer proves nothing about the code that
# is actually being shipped. This project has been bitten by exactly that.
echo "==> Running the full suite inside the image"
docker build --target test --no-cache -t "argus:test-${VERSION}" .

echo "==> Building runtime and server images"
docker build --target runtime -t "${REGISTRY}:${VERSION}" .
docker build --target server  -t "${REGISTRY}:${VERSION}-server" .

echo "==> Verifying the built image actually runs"
docker run --rm "${REGISTRY}:${VERSION}" --help >/dev/null
docker run --rm --entrypoint ctags "${REGISTRY}:${VERSION}" --version | head -1

echo "==> Pushing"
docker push "${REGISTRY}:${VERSION}"
docker push "${REGISTRY}:${VERSION}-server"

# Only move `latest` for a final release. A release candidate that grabs
# `latest` is how a pilot build reaches someone who wanted a stable one.
case "${VERSION}" in
    *rc*|*alpha*|*beta*)
        echo "==> ${VERSION} is a pre-release; leaving :latest alone" ;;
    *)
        docker tag "${REGISTRY}:${VERSION}" "${REGISTRY}:latest"
        docker push "${REGISTRY}:latest" ;;
esac

echo
echo "==> Published digests (record these):"
docker inspect --format '{{index .RepoDigests 0}}' "${REGISTRY}:${VERSION}"
docker inspect --format '{{index .RepoDigests 0}}' "${REGISTRY}:${VERSION}-server"
