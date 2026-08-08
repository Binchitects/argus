#!/usr/bin/env bash
# Build knowledge packs ONE AT A TIME.
#
#   ./deploy/build-packs.sh                # every pack not already built
#   ./deploy/build-packs.sh wdk win32      # just these, in this order
#
# Sequential on purpose. Embedding is CPU-bound and running several builds at
# once pins every core for hours; on an air-cooled desktop that is a thermal
# problem, not a throughput win. Ollama serialises the embedding calls anyway,
# so concurrency buys almost nothing and costs heat.
#
# Resumable: a pack that already exists is skipped, so re-running after an
# interruption continues where it stopped rather than starting over. Each
# build writes to <name>.building and renames only on success, so an
# interrupted run never leaves a half-valid pack behind.
set -uo pipefail

cd "$(dirname "$0")/.."

SRC_DIR="${ARGUS_DOCSRC:-.packwork/docsrc}"
OUT_DIR="${ARGUS_PACKS_OUT:-packs}"
VERSION="${ARGUS_PACK_VERSION:-1.0}"

# pack name -> checkout directory. Ordered smallest first: a cheap pack that
# fails tells you the pipeline is broken before an eleven-hour one does.
order=(system-design algorithms scripting cpp wdk win32)
declare -A checkout=(
    [system-design]="system-design-primer"
    [algorithms]="C-Plus-Plus"
    [cpp]="cpp-docs"
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
    out="$OUT_DIR/$name-$VERSION.pack"

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
    if python -m argus.cli pack build --source "$name" --work-dir "$work" \
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
