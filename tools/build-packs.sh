#!/usr/bin/env bash
# Build knowledge packs ONE AT A TIME.
#
#   ./tools/build-packs.sh                # every pack not already built
#   ./tools/build-packs.sh wdk win32      # just these, in this order
#
# Sequential on purpose. Embedding is CPU-bound and running several builds at
# once pins every core for hours; on an air-cooled desktop that is a thermal
# problem, not a throughput win. The embedding server serialises the calls
# anyway, so concurrency buys almost nothing and costs heat.
#
# Uses the code-intel CLI (CODE_INTEL_BIN, default `code-intel` on PATH -- or
# src/CodeIntel/bin/Release/net10.0/code-intel after a build) and the embedding
# server at CODE_INTEL_EMBED_URL (the stack's llamacpp-embed, e.g.
# http://localhost:8081 when its port is published).
#
# Resumable: a pack that already exists is skipped, so re-running after an
# interruption continues where it stopped rather than starting over. Each
# build writes to <name>.building and renames only on success, so an
# interrupted run never leaves a half-valid pack behind.
set -uo pipefail

cd "$(dirname "$0")/.."

SRC_DIR="${CODE_INTEL_DOCSRC:-.packwork/docsrc}"
OUT_DIR="${CODE_INTEL_PACKS_OUT:-packs}"
VERSION="${CODE_INTEL_PACK_VERSION:-1.0}"
CODE_INTEL="${CODE_INTEL_BIN:-code-intel}"

# pack name -> checkout directory. Ordered smallest first: a cheap pack that
# fails tells you the pipeline is broken before an eleven-hour one does.
order=(system-design algorithms scripting cpp qt wdk win32)
declare -A checkout=(
    [system-design]="system-design-primer"
    [algorithms]="C-Plus-Plus"
    [cpp]="cpp-docs"
    # Qt's offline documentation, unpacked as qt/qt4, qt/qt5 and qt/qt6 (not a
    # git checkout: the pack records the three releases as its provenance).
    [qt]="qt"
    # Also a composite: PowerShell + windows-commands + tldr.
    [scripting]="."
    # wdk and win32 are composites: reference AND samples in one pack. A
    # composite is pointed at the PARENT holding both checkouts, which is why
    # these map to "." rather than to a single repository directory.
    [wdk]="."
    [win32]="."
    # The halves on their own, for anyone who wants just one.
    [wdk-docs]="windows-driver-docs-ddi"
    [win32-docs]="sdk-api"
    [wdk-samples]="Windows-driver-samples"
    [win32-samples]="Windows-classic-samples"
)

wanted=("$@")
[ ${#wanted[@]} -eq 0 ] && wanted=("${order[@]}")

mkdir -p "$OUT_DIR"
failed=()

for name in "${wanted[@]}"; do
    dir="${checkout[$name]:-}"
    if [ -z "$dir" ]; then
        echo "unknown pack '$name'; known: ${order[*]}" >&2
        failed+=("$name"); continue
    fi
    work="$SRC_DIR/$dir"
    out="$OUT_DIR/$name-$VERSION.kpack"

    if [ -f "$out" ]; then
        echo "== $name: already built, skipping ($(du -h "$out" | cut -f1))"
        continue
    fi
    if [ ! -d "$work" ]; then
        echo "== $name: SKIP, no checkout at $work" >&2
        failed+=("$name"); continue
    fi

    echo
    echo "== $name: building from $work"
    started=$(date +%s)
    if "$CODE_INTEL" pack build --source "$name" --work-dir "$work" \
            --out "$out" --version "$VERSION"; then
        echo "== $name: done in $(( ($(date +%s) - started) / 60 )) min"
    else
        echo "== $name: FAILED" >&2
        failed+=("$name")
    fi
done

echo
if [ ${#failed[@]} -eq 0 ]; then
    echo "all requested packs built"
else
    echo "failed: ${failed[*]}" >&2
    exit 1
fi
