#!/usr/bin/env bash
# Build, verify and publish an Argus release image -- to a registry, or to a
# file for a host that has no registry to pull from.
#
#   ./scripts/release.sh 2.0.0              build, verify, push to ghcr.io
#   ./scripts/release.sh --offline 2.0.0    build, verify, write dist/
#
# Publishing to a registry needs working DNS and `docker login ghcr.io` already
# done. This script never handles credentials.
#
# OFFLINE MODE, AND WHY IT IS THE SAME SCRIPT
#
# An airgapped release is not a different product -- it is the same images
# delivered differently. Keeping them in one script means the suite, the
# toolchain-drift guard and the run-it-once checks cannot drift apart between
# the two paths, which is exactly what would happen if `--offline` were a
# second file that quietly stopped being updated.
#
# `--offline` writes `dist/argus-<version>.tar.gz` plus a `.sha256`, which is
# the layout stack/scripts/package.sh already looks for when it builds a
# drop-in archive, so the two compose: package.sh ships the Argus image it
# finds in dist/.
#
# What --offline still needs: the BASE image (`python:3.13-slim-bookworm`) and
# the Debian packages cached, because `--no-cache` on the test stage forces a
# rebuild of the layers above them. On a machine that has never built this
# image, run it once while you still have a network. The script says so rather
# than failing with a raw registry error.
set -euo pipefail

VERSION=""
OFFLINE=0
REGISTRY="${ARGUS_REGISTRY:-ghcr.io/binchitects/argus}"
OUT="dist"

usage() {
  cat >&2 <<'EOF'
usage: release.sh [--offline] [--out DIR] [--no-test] VERSION

  VERSION        e.g. 2.0.0 or 2.1.0-rc1
  --offline      write dist/argus-VERSION.tar.gz instead of pushing
  --out DIR      where --offline writes (default: dist)
  --no-test      skip the in-image suite. Do not use for a real release: the
                 test stage is what proves the pinned ctags still extracts
                 symbols, and a build that skips it can ship an indexer whose
                 index is silently empty.
EOF
  exit 2
}

RUN_TEST=1
while [[ $# -gt 0 ]]; do
  case "$1" in
    --offline) OFFLINE=1; shift ;;
    --out)     OUT="${2:?--out needs a directory}"; shift 2 ;;
    --no-test) RUN_TEST=0; shift ;;
    -h|--help) usage ;;
    -*)        echo "unknown option: $1" >&2; usage ;;
    *)         VERSION="$1"; shift ;;
  esac
done
[[ -n "$VERSION" ]] || usage

cd "$(dirname "$0")/.."
ROOT="$PWD"
echo "==> Releasing Argus ${VERSION} from ${ROOT}"
[[ $OFFLINE -eq 1 ]] && echo "    mode: offline (writing ${OUT}/)" || echo "    mode: registry (${REGISTRY})"

# ---------------------------------------------------------------------------
# The one thing that must be true before a build starts, and the two modes
# have different answers.
if [[ $OFFLINE -eq 1 ]]; then
  # Deliberately NOT a hard check on `docker image inspect`, which is where
  # this started and was wrong: BuildKit keeps the base in its own cache
  # (docker buildx du), so a machine that has built this image before has no
  # `python:3.13-slim-bookworm` in `docker images` and still builds offline
  # perfectly. Failing on that rejected the exact environment this mode
  # exists for. The question is not "is the tag in the image store" but "can
  # the build resolve it without a registry", and the only honest way to ask
  # that is to try -- so the guidance below is attached to a real failure.
  echo "==> Offline mode: no registry access will be attempted"
  if ! docker image inspect python:3.13-slim-bookworm >/dev/null 2>&1; then
    echo "    note: python:3.13-slim-bookworm is not in the image store. That is"
    echo "    normal when BuildKit has it cached, which is enough. If the build"
    echo "    below fails on resolving the base, THAT is the airgap problem:"
    echo "    build this image once where you have a network, or carry the base"
    echo "    image in your bundle."
  fi
else
  echo "==> Preflight: can Docker resolve names?"
  if ! docker run --rm python:3.13-slim-bookworm \
          sh -c 'getent hosts deb.debian.org >/dev/null' 2>/dev/null; then
      echo "DNS resolution fails inside containers, so the image cannot build." >&2
      echo "This is usually the host's resolver, not Docker: check whether a VPN" >&2
      echo "tunnel is claiming DNS with a server that is not answering." >&2
      exit 1
  fi
fi

# ---------------------------------------------------------------------------
# --no-cache, because a CACHED test layer proves nothing about the code that
# is actually being shipped. This project has been bitten by exactly that.
if [[ $RUN_TEST -eq 1 ]]; then
  echo "==> Running the full suite inside the image"
  docker build --target test --no-cache -t "argus:test-${VERSION}" .
else
  echo "==> SKIPPING the in-image suite (--no-test)"
fi

echo "==> Building runtime and server images"
docker build --target runtime -t "argus:${VERSION}" .
docker build --target server  -t "argus:${VERSION}-server" .

echo "==> Verifying the built image actually runs"
docker run --rm "argus:${VERSION}" --help >/dev/null
docker run --rm --entrypoint ctags "argus:${VERSION}" --version | head -1

# ---------------------------------------------------------------------------
if [[ $OFFLINE -eq 1 ]]; then
  mkdir -p "$OUT"
  # `docker save` writes an uncompressed tar of every layer; gzip roughly
  # quarters it. The name is what package.sh globs for.
  for variant in "" "-server"; do
    tag="argus:${VERSION}${variant}"
    tar="${OUT}/argus-${VERSION}${variant}.tar.gz"
    echo "==> Saving ${tag} -> ${tar}"
    docker save "$tag" | gzip -9 > "$tar"
    sha256sum "$tar" > "${tar}.sha256"
  done

  echo
  echo "==> Offline artifacts (record these):"
  ( cd "$OUT" && ls -la argus-*.tar.gz | awk '{printf "  %-44s %6.1f MB\n", $9, $5/1048576}' )
  echo
  echo "  Carry these to the isolated host, then:"
  echo "    docker load < ${OUT}/argus-${VERSION}.tar.gz"
  echo "    docker load < ${OUT}/argus-${VERSION}-server.tar.gz"
  echo "    sha256sum -c ${OUT}/argus-${VERSION}.tar.gz.sha256"
  echo
  echo "  Or let stack/scripts/package.sh carry them for you: it picks up"
  echo "  dist/argus-*.tar.gz and refuses to build an archive without them."
  exit 0
fi

# ---------------------------------------------------------------------------
echo "==> Pushing"
docker push "${REGISTRY}:${VERSION}"
docker push "${REGISTRY}:${VERSION}-server"

# Only move `latest` for a final release. A release candidate that grabs
# `latest` is how a pilot build reaches someone who wanted a stable one.
case "${VERSION}" in
    *rc*|*alpha*|*beta*)
        echo "==> ${VERSION} is a pre-release; leaving :latest alone" ;;
    *)
        docker tag "argus:${VERSION}" "${REGISTRY}:latest"
        docker push "${REGISTRY}:latest" ;;
esac

echo
echo "==> Published digests (record these):"
docker inspect --format '{{index .RepoDigests 0}}' "${REGISTRY}:${VERSION}"
docker inspect --format '{{index .RepoDigests 0}}' "${REGISTRY}:${VERSION}-server"
