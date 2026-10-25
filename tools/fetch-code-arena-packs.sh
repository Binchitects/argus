#!/usr/bin/env bash
# The .NET runtime and host packs Code Arena is published with, fetched into tools/offline-nuget/ at the
# runtime version this SDK publishes with (the SDK says which), so tools/publish-code-arena.sh --offline and
# the app's image (CODE_ARENA=auto) build every system's Code Arena with nothing downloaded. Run it on a host
# with internet after .NET moves on (each monthly patch asks for newer packs): the deploy runs it before it
# builds the images, and make-installer.sh before it packs a release.
#
#   tools/fetch-code-arena-packs.sh            fetch what is missing (about 220 MB the first time), keep the rest
#   tools/fetch-code-arena-packs.sh --check    only say what is missing (exit 3 when something is)
#   tools/fetch-code-arena-packs.sh --prune    also remove the packs of other versions
#
# From nuget.org's flat container (api.nuget.org/v3-flatcontainer), each file checked as a zip before it is
# kept (NuGet checks its signature again when it restores from it). Uses dotnet when installed, tools/dn
# (the SDK in a container) otherwise, only to ask the version.
set -euo pipefail
here="$(cd "$(dirname "$0")" && pwd)"
root="$(dirname "$here")"
cd "$root"

check= prune=
for arg in "$@"; do
  case "$arg" in
    --check) check=1 ;;
    --prune) prune=1 ;;
    -h|--help) sed -n '2,15p' "$0" | sed 's/^# \{0,1\}//'; exit 0 ;;
    *) echo "unknown argument: $arg" >&2; exit 2 ;;
  esac
done

if command -v dotnet >/dev/null 2>&1; then dotnet=(dotnet); else dotnet=("$here/dn"); fi
runtime="$("${dotnet[@]}" msbuild src/CodeArena/CodeArena.csproj -getProperty:BundledNETCoreAppPackageVersion | tail -n 1 | tr -d '\r')" || runtime=
if ! [[ "$runtime" =~ ^[0-9]+\.[0-9]+\.[0-9]+(-[0-9A-Za-z.-]+)?$ ]]; then
  echo "The .NET SDK did not say which runtime it publishes with (BundledNETCoreAppPackageVersion: '$runtime')" >&2
  exit 1
fi

dir=tools/offline-nuget
mkdir -p "$dir"
missing=()
for rid in linux-x64 linux-arm64 win-x64 osx-x64 osx-arm64; do
  for pack in runtime host; do
    file="microsoft.netcore.app.$pack.$rid.$runtime.nupkg"
    [ -s "$dir/$file" ] || missing+=("$file")
  done
done

if [ ${#missing[@]} -eq 0 ]; then
  echo "Code Arena's packs: all 10 here at runtime $runtime."
elif [ -n "$check" ]; then
  echo "Code Arena's packs: ${#missing[@]} of 10 missing at runtime $runtime:"
  printf '  %s\n' "${missing[@]}"
  exit 3
else
  echo "Code Arena's packs: fetching ${#missing[@]} at runtime $runtime from api.nuget.org"
  for file in "${missing[@]}"; do
    id="${file%.$runtime.nupkg}"
    part="$dir/.$file.part"
    curl -fsSL --retry 3 --connect-timeout 20 -o "$part" "https://api.nuget.org/v3-flatcontainer/$id/$runtime/$file"
    # A .nupkg is a zip: one cut short or an error page is not kept.
    if ! python3 -c 'import sys, zipfile; z = zipfile.ZipFile(sys.argv[1]); sys.exit(1 if z.testzip() else 0)' "$part" 2>/dev/null; then
      rm -f "$part"
      echo "  $file: not a whole package; nothing kept" >&2
      exit 1
    fi
    mv "$part" "$dir/$file"
    echo "  $file ($(du -h "$dir/$file" | cut -f1))"
  done
fi

if [ -n "$prune" ]; then
  for old in "$dir"/microsoft.netcore.app.*.nupkg; do
    [ -e "$old" ] || continue
    case "$old" in *".$runtime.nupkg") ;; *) rm -f "$old"; echo "  removed $(basename "$old")" ;; esac
  done
fi
