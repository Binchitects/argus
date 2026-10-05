#!/usr/bin/env bash
# Code Arena as one self-contained file per system: dist/code-arena/<rid>/code-arena
# (code-arena.exe on Windows), with SHA256SUMS beside them. Nothing needs .NET
# installed to run them. The app's image serves them from Your account → Connect
# your tools (src/Llm.Api/Dockerfile copies dist/code-arena in).
#
#   tools/publish-code-arena.sh                     every system, packages from nuget.org
#   tools/publish-code-arena.sh --offline           every system, packages from tools/offline-nuget only
#   tools/publish-code-arena.sh --offline linux-x64 win-x64
#   tools/publish-code-arena.sh --no-web            without building the web interface's page
#
# --offline needs the .NET runtime and host packs for each system in
# tools/offline-nuget/ (its README says how to fill it once), at the runtime
# version the SDK publishes with: without them it builds nothing and exits
# with 3. Uses dotnet when installed, tools/dn (the SDK in a container) otherwise.
#
# First it builds the web interface's page (code-arena web) from src/web into
# src/CodeArena/web, which the program embeds: with Node.js and src/web's own
# node_modules (npm ci there once), nothing downloaded. Without Node, a page
# built already (the app's image builds it in a stage of its own) is used.
set -euo pipefail
here="$(cd "$(dirname "$0")" && pwd)"
root="$(dirname "$here")"
cd "$root"

all=(linux-x64 linux-arm64 win-x64 osx-x64 osx-arm64)
offline=
web=1
rids=()
for arg in "$@"; do
  case "$arg" in
    --offline) offline=1 ;;
    --no-web) web= ;;
    -h|--help) sed -n '2,20p' "$0" | sed 's/^# \{0,1\}//'; exit 0 ;;
    linux-x64|linux-arm64|win-x64|osx-x64|osx-arm64) rids+=("$arg") ;;
    *) echo "unknown argument: $arg (systems: ${all[*]})" >&2; exit 2 ;;
  esac
done
[ ${#rids[@]} -eq 0 ] && rids=("${all[@]}")

if command -v dotnet >/dev/null 2>&1; then dotnet=(dotnet); else dotnet=("$here/dn"); fi

sources=()
if [ -n "$offline" ]; then
  # The packs at the runtime version this SDK publishes with, for every system asked for, checked before
  # anything is built: one missing would fail the restore halfway through. The SDK says which version (the
  # project evaluated, nothing restored): other runtimes installed beside it, a newer .NET's, do not count.
  runtime="$("${dotnet[@]}" msbuild src/CodeArena/CodeArena.csproj -getProperty:BundledNETCoreAppPackageVersion | tail -n 1)" || runtime=
  if ! [[ "$runtime" =~ ^[0-9]+\.[0-9]+\.[0-9]+(-[0-9A-Za-z.-]+)?$ ]]; then
    echo "The .NET SDK did not say which runtime it publishes with (BundledNETCoreAppPackageVersion: '$runtime')" >&2
    exit 1
  fi
  missing=()
  for rid in "${rids[@]}"; do
    for pack in runtime host; do
      file="microsoft.netcore.app.$pack.$rid.$runtime.nupkg"
      [ -n "$(find tools/offline-nuget -maxdepth 1 -iname "$file" -print -quit 2>/dev/null)" ] || missing+=("$file")
    done
  done
  if [ ${#missing[@]} -gt 0 ]; then
    echo "tools/offline-nuget lacks ${#missing[@]} of the $((2 * ${#rids[@]})) .NET packs this SDK publishes with (runtime $runtime):" >&2
    printf '  %s\n' "${missing[@]}" >&2
    echo "Fetch them at version $runtime as tools/offline-nuget/README.md says." >&2
    exit 3
  fi
  # Only this folder: a package missing from it fails the restore instead of being downloaded.
  sources=(--source tools/offline-nuget)
fi

page=src/CodeArena/web
if [ -n "$web" ]; then
  if command -v node >/dev/null 2>&1 && [ -x src/web/node_modules/.bin/vite ]; then
    echo "== the web interface (src/web -> $page)"
    # The page, then the diagram runner its answers draw with, into the same folder.
    (cd src/web \
      && node_modules/.bin/vite build --config vite.code-arena.config.ts --logLevel warn \
      && node_modules/.bin/vite build --config vite.preview.config.ts --outDir ../CodeArena/web --logLevel warn)
  elif [ -f "$page/code-arena.html" ]; then
    echo "== the web interface: built already ($page)"
  else
    echo "The web interface needs Node.js and src/web/node_modules (npm ci in src/web) to build." >&2
    echo "Build without it with --no-web: code-arena web then says its page is missing." >&2
    exit 1
  fi
else
  # Left out on purpose: none embedded, even if one was built before.
  rm -rf "$page"
fi

out=dist/code-arena
mkdir -p "$out"
for rid in "${rids[@]}"; do
  echo "== $rid"
  rm -rf "${out:?}/$rid"
  "${dotnet[@]}" publish src/CodeArena/CodeArena.csproj -c Release -r "$rid" -o "$out/$rid" \
    "${sources[@]}" -p:RestoreIgnoreFailedSources=false -nologo
  # Only the program: no symbols or other leftovers beside it.
  find "$out/$rid" -type f ! -name code-arena ! -name code-arena.exe -delete
done

(cd "$out" && find . -type f \( -name code-arena -o -name code-arena.exe \) | sed 's|^\./||' | sort | xargs sha256sum > SHA256SUMS)
echo
cat "$out/SHA256SUMS"
ls -l "$out"/*/code-arena*
