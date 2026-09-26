#!/usr/bin/env bash
# Fetch the sqlite-vec loadable extension the .NET Argus loads into SQLite.
#
# The version is the one the Python implementation depends on (pyproject:
# sqlite-vec), so both read and write vec0 tables with the same code: a pack or
# an index built by either is served identically by the other. The binaries are
# taken from the published PyPI wheels -- the one place every platform build of
# that exact release is available -- and every wheel is checked against the
# SHA-256 PyPI lists for it before anything is extracted.
#
#   tools/fetch-sqlite-vec.sh            # every platform
#   tools/fetch-sqlite-vec.sh linux-x64  # just one
#
# Output: native/runtimes/<rid>/native/vec0.{so,dll,dylib}, which the Argus
# project copies next to the executable.
set -euo pipefail

VERSION=0.1.9
HERE="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
OUT="$HERE/native/runtimes"
BASE=https://files.pythonhosted.org/packages

# rid | wheel path under $BASE | sha256 | file inside the wheel
WHEELS=(
  "linux-x64|6f/ad/6afd073b0f817b3e03f9e37ad626ae341805891f23c74b5292818f49ac63/sqlite_vec-0.1.9-py3-none-manylinux_2_17_x86_64.manylinux2014_x86_64.manylinux1_x86_64.whl|1515727990b49e79bcaf75fdee2ffc7d461f8b66905013231251f1c8938e7786|sqlite_vec/vec0.so"
  "linux-arm64|00/d4/f2b936d3bdc38eadcbd2a87875815db36430fab0363182ba5d12cd8e0b51/sqlite_vec-0.1.9-py3-none-manylinux_2_17_aarch64.manylinux2014_aarch64.whl|4e921e592f24a5f9a18f590b6ddd530eb637e2d474e3b1972f9bbeb773aa3cb9|sqlite_vec/vec0.so"
  "win-x64|42/89/81b2907cda14e566b9bf215e2ad82fc9b349edf07d2010756ffdb902f328/sqlite_vec-0.1.9-py3-none-win_amd64.whl|4a28dc12fa4b53d7b1dced22da2488fade444e96b5d16fd2d698cd670675cf32|sqlite_vec/vec0.dll"
  "osx-x64|68/85/9fad0045d8e7c8df3e0fa5a56c630e8e15ad6e5ca2e6106fceb666aa6638/sqlite_vec-0.1.9-py3-none-macosx_10_6_x86_64.whl|1b62a7f0a060d9475575d4e599bbf94a13d85af896bc1ce86ee80d1b5b48e5fb|sqlite_vec/vec0.dylib"
  "osx-arm64|a4/3d/3677e0cd2f92e5ebc43cd29fbf565b75582bff1ccfa0b8327c7508e1084f/sqlite_vec-0.1.9-py3-none-macosx_11_0_arm64.whl|1d52e30513bae4cc9778ddbf6145610434081be4c3afe57cd877893bad9f6b6c|sqlite_vec/vec0.dylib"
)

want="${1:-all}"
tmp="$(mktemp -d)"
trap 'rm -rf "$tmp"' EXIT

sha256() {
  if command -v sha256sum >/dev/null; then sha256sum "$1" | cut -d' ' -f1
  else shasum -a 256 "$1" | cut -d' ' -f1; fi
}

for row in "${WHEELS[@]}"; do
  IFS='|' read -r rid path digest member <<<"$row"
  [[ "$want" == all || "$want" == "$rid" ]] || continue
  dest="$OUT/$rid/native"
  target="$dest/$(basename "$member")"
  if [[ -f "$target.sha256" && "$(cat "$target.sha256")" == "$digest" && -f "$target" ]]; then
    echo "sqlite-vec $VERSION $rid: present"
    continue
  fi
  wheel="$tmp/$rid.whl"
  curl -fsSL --retry 4 -o "$wheel" "$BASE/$path"
  actual="$(sha256 "$wheel")"
  if [[ "$actual" != "$digest" ]]; then
    echo "sqlite-vec $rid: checksum mismatch (expected $digest, got $actual)" >&2
    exit 1
  fi
  mkdir -p "$dest"
  python3 - "$wheel" "$member" "$target" <<'PY' 2>/dev/null || unzip -p "$wheel" "$member" >"$target"
import sys, zipfile
with zipfile.ZipFile(sys.argv[1]) as z, open(sys.argv[3], "wb") as out:
    out.write(z.read(sys.argv[2]))
PY
  echo "$digest" >"$target.sha256"
  echo "sqlite-vec $VERSION $rid: $target"
done
