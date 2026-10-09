#!/usr/bin/env bash
# Code Arena's packages, one per system, to hand out or attach to a release:
#
#   dist/code-arena-<version>-<rid>.tar.gz   (a .zip for win-x64)
#   dist/SHA256SUMS                          every package of this version in dist/
#
# Each holds a folder of the same name with the program (code-arena, or
# code-arena.exe), README.txt (how to run it, sign in, the IDE, the terminal,
# the licence), LICENSE.md and LICENSING.md. The version is VERSION's.
#
#   tools/package-code-arena.sh                      every system
#   tools/package-code-arena.sh linux-x64 win-x64    some of them
#   tools/package-code-arena.sh --online [rid ...]   runtime packs from nuget.org
#
# tools/publish-code-arena.sh builds the page and the programs first (into
# dist/code-arena/<rid>/), from tools/offline-nuget only unless --online. Runs
# on Linux (GNU tar, zip, sha256sum); dotnet or tools/dn builds.
set -euo pipefail
here="$(cd "$(dirname "$0")" && pwd)"
root="$(dirname "$here")"
cd "$root"

all=(linux-x64 linux-arm64 osx-x64 osx-arm64 win-x64)
offline=--offline
rids=()
for arg in "$@"; do
  case "$arg" in
    --online) offline= ;;
    -h|--help) sed -n '2,17p' "$0" | sed 's/^# \{0,1\}//'; exit 0 ;;
    linux-x64|linux-arm64|osx-x64|osx-arm64|win-x64) rids+=("$arg") ;;
    *) echo "unknown argument: $arg (systems: ${all[*]})" >&2; exit 2 ;;
  esac
done
[ ${#rids[@]} -eq 0 ] && rids=("${all[@]}")

version="$(tr -d '[:space:]' < VERSION)"
[ -n "$version" ] || { echo "VERSION is empty" >&2; exit 1; }
for tool in tar gzip zip sha256sum; do
  command -v "$tool" >/dev/null 2>&1 || { echo "$tool is needed to package Code Arena" >&2; exit 1; }
done

"$here/publish-code-arena.sh" ${offline:+"$offline"} "${rids[@]}"

# What README.txt says, for one system.
readme() {
  local rid="$1" exe="$2" system install
  case "$rid" in
    linux-*) system="Linux (${rid#linux-})"
      install="  chmod +x code-arena && mkdir -p ~/.local/bin && mv code-arena ~/.local/bin/" ;;
    osx-*) system="macOS ($([ "$rid" = osx-arm64 ] && echo 'Apple silicon' || echo Intel))"
      install="  chmod +x code-arena && mkdir -p ~/.local/bin && mv code-arena ~/.local/bin/
  macOS marks files from the internet: xattr -d com.apple.quarantine code-arena
  (the build is not notarized by Apple)." ;;
    win-*) system="Windows (x64)"
      install="  Put code-arena.exe in a folder on your PATH. If SmartScreen stops it:
  More info, then Run anyway (or unblock it in the file's properties)." ;;
  esac
  cat <<EOF
Code Arena $version for $system

An IDE in your browser around a coding agent, on your company's Argus Arena:
your project's files, an editor, search, terminals and the agent's chat, on
your own machine. Nothing else needs to be installed.

Install
$install

Sign in, once
  $exe login --url https://DOMAIN
  It asks for your API key (in Arena: Your account, then API key), checks it
  and keeps it in its config folder, readable by you only. With a company CA,
  add --ca ca.crt. $exe logout forgets the key.

The IDE
  cd your-project
  $exe              (or: $exe web)
  It prints an address on 127.0.0.1 carrying this run's key and opens your
  browser there. Only this machine can open it, and only with that key.
  Ctrl+C in the terminal stops it. --port N picks the port, --no-open leaves
  the browser closed, --continue carries on with the folder's last session.
  On another machine over SSH: $exe --port 8765 --no-open there, then
  ssh -L 8765:127.0.0.1:8765 that-machine here, and open the address printed.

  In the IDE: the Explorer (the folder's files), editor tabs, the agent's
  changes with a diff to accept or revert, search across the files,
  terminals (your own shell, in the project folder, as powerful as any) and
  the agent's chat beside them, with its mode and model.

The agent in this terminal instead
  $exe chat                    a conversation here (/help lists the commands)
  $exe "why is the build red?" a conversation that starts with this
  $exe -p "list the TODOs"     answer once, print the answer, exit
  At its prompt the up arrow brings back what you sent in this folder, and
  Esc goes back to what you were typing.

Licence
  Copyright (C) 2026 Binchitects and contributors. There is no warranty.
  Code Arena is offered under the AGPL-3.0-only (LICENSE.md) with the
  additional terms in LICENSING.md, or under a commercial license from
  Binchitects. Source: https://github.com/Binchitects/argus
  $exe --version prints the version, the licence and the source.
EOF
}

# The same files give the same package: no owners, the commit's time on every file.
epoch="${SOURCE_DATE_EPOCH:-$(git log -1 --format=%ct 2>/dev/null || date +%s)}"
work="$(mktemp -d)"
trap 'rm -rf "$work"' EXIT
for rid in "${rids[@]}"; do
  name="code-arena-$version-$rid"
  exe=code-arena
  [ "$rid" = win-x64 ] && exe=code-arena.exe
  dir="$work/$name"
  mkdir -p "$dir"
  install -m 0755 "dist/code-arena/$rid/$exe" "$dir/$exe"
  install -m 0644 LICENSE.md LICENSING.md "$dir/"
  if [ "$rid" = win-x64 ]; then
    readme "$rid" "$exe" | sed 's/$/\r/' > "$dir/README.txt" # Notepad's line ends
  else
    readme "$rid" "$exe" > "$dir/README.txt"
  fi
  chmod 0644 "$dir/README.txt"
  touch -d "@$epoch" "$dir" "$dir"/*
  rm -f "dist/$name.tar.gz" "dist/$name.zip"
  if [ "$rid" = win-x64 ]; then
    (cd "$work" && find "$name" | LC_ALL=C sort | TZ=UTC zip -qX -9 -@ "$root/dist/$name.zip")
    echo "== dist/$name.zip"
  else
    tar -C "$work" --sort=name --owner=0 --group=0 --numeric-owner --mtime="@$epoch" -cf - "$name" | gzip -9n > "dist/$name.tar.gz"
    echo "== dist/$name.tar.gz"
  fi
done

(cd dist && find . -maxdepth 1 -type f \( -name "code-arena-$version-*.tar.gz" -o -name "code-arena-$version-*.zip" \) -printf '%f\n' \
  | LC_ALL=C sort | xargs sha256sum > SHA256SUMS)
echo
cat dist/SHA256SUMS
ls -l dist/code-arena-"$version"-*
