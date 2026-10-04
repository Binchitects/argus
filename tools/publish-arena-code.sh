#!/usr/bin/env bash
# Arena Code as one self-contained file per system: dist/arena-code/<rid>/arena-code
# (arena-code.exe on Windows), with SHA256SUMS beside them. Nothing needs .NET
# installed to run them. The app's image serves them from Your account → Connect
# your tools (src/Llm.Api/Dockerfile copies dist/arena-code in).
#
#   tools/publish-arena-code.sh                     every system, packages from nuget.org
#   tools/publish-arena-code.sh --offline           every system, packages from tools/offline-nuget only
#   tools/publish-arena-code.sh --offline linux-x64 win-x64
#
# --offline needs the .NET runtime and host packs for each system in
# tools/offline-nuget/ (its README says how to fill it once). Uses dotnet when
# installed, tools/dn (the SDK in a container) otherwise.
set -euo pipefail
here="$(cd "$(dirname "$0")" && pwd)"
root="$(dirname "$here")"
cd "$root"

all=(linux-x64 linux-arm64 win-x64 osx-x64 osx-arm64)
offline=
rids=()
for arg in "$@"; do
  case "$arg" in
    --offline) offline=1 ;;
    -h|--help) sed -n '2,13p' "$0" | sed 's/^# \{0,1\}//'; exit 0 ;;
    linux-x64|linux-arm64|win-x64|osx-x64|osx-arm64) rids+=("$arg") ;;
    *) echo "unknown argument: $arg (systems: ${all[*]})" >&2; exit 2 ;;
  esac
done
[ ${#rids[@]} -eq 0 ] && rids=("${all[@]}")

if command -v dotnet >/dev/null 2>&1; then dotnet=(dotnet); else dotnet=("$here/dn"); fi

sources=()
if [ -n "$offline" ]; then
  if ! ls tools/offline-nuget/*.nupkg >/dev/null 2>&1; then
    echo "tools/offline-nuget holds no packages: see tools/offline-nuget/README.md" >&2
    exit 1
  fi
  # Only this folder: a package missing from it fails the restore instead of being downloaded.
  sources=(--source tools/offline-nuget)
fi

out=dist/arena-code
mkdir -p "$out"
for rid in "${rids[@]}"; do
  echo "== $rid"
  rm -rf "${out:?}/$rid"
  "${dotnet[@]}" publish src/ArenaCode/ArenaCode.csproj -c Release -r "$rid" -o "$out/$rid" \
    "${sources[@]}" -p:RestoreIgnoreFailedSources=false -nologo
  # Only the program: no symbols or other leftovers beside it.
  find "$out/$rid" -type f ! -name arena-code ! -name arena-code.exe -delete
done

(cd "$out" && find . -type f \( -name arena-code -o -name arena-code.exe \) | sed 's|^\./||' | sort | xargs sha256sum > SHA256SUMS)
echo
cat "$out/SHA256SUMS"
ls -l "$out"/*/arena-code*
