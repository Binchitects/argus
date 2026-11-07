#!/bin/sh
# Signs the Code Arena builds this stack serves, with a code-signing certificate of the stack's own
# CA (certs/ca.key, which scripts/make-cert.sh made): people's installers and `code-arena update`
# then check that a build is the one this server's CA signed, not only that its SHA-256 matches
# what the server lists; and the Windows builds carry an Authenticode signature, so Windows names
# their publisher (on a machine that trusts the CA) instead of "Unknown publisher".
#
#   scripts/sign-code-arena.sh          sign what the app's image holds (the app must be running)
#   scripts/sign-code-arena.sh --check  say whether what the app serves is the image's builds signed, and exit 1 if not
#
# Writes signing/code-signing.key and .crt (made from the CA when missing, or within 30 days of
# their end; 3 years), and, for the app to serve, $DATA_DIR/code-arena-signature/: the Windows
# builds Authenticode-signed (win-*/code-arena.exe and the IDE's launcher beside it, code-arena-ide.exe), SHA256SUMS (the builds' list as served: the
# signed Windows builds' SHA-256 in place of the image's), SHA256SUMS.sig (its RSA SHA-256
# signature), SHA256SUMS.image (the image's list it was made from) and code-signing.crt (the
# certificate, then the CA). A company's own code-signing certificate goes in signing/ as
# code-signing.crt and .key. The key stays on the host but for one container of the app's image
# that signs the Windows builds: no network, nothing mounted, the key in through a pipe and only
# the signed builds out. installer.sh runs it on install, upgrade and repair when a CA or a
# certificate is there.
set -eu
cd "$(dirname "$0")/.."

check=0
case "${1:-}" in
  --check) check=1 ;;
  -h|--help) sed -n '2,20p' "$0"; exit 0 ;;
  "") ;;
  *) echo "sign-code-arena: unknown option $1 (--help)" >&2; exit 2 ;;
esac
command -v openssl >/dev/null || { echo "sign-code-arena: openssl is not installed" >&2; exit 1; }
engine=${ENGINE:-}
[ -n "$engine" ] || { command -v docker >/dev/null && engine=docker || engine=podman; }
data=$(sed -n 's/^DATA_DIR=//p' .env 2>/dev/null | tail -n 1 | tr -d '"'"'"' ')
[ -n "$data" ] || { echo "sign-code-arena: DATA_DIR is not set in .env" >&2; exit 1; }
out="$data/code-arena-signature"
work=$(mktemp -d)
trap 'rm -rf "$work"' EXIT
windows='  win-[a-z0-9]+/code-arena(-ide)?\.exe$'

# The image's list of builds and their SHA-256.
if ! "$engine" compose cp app:/downloads/code-arena/SHA256SUMS "$work/SHA256SUMS.image" >/dev/null 2>&1; then
  echo "sign-code-arena: the app's image has no Code Arena builds (or the app is not running): nothing to sign"
  exit 0
fi

# Whether every Windows build a folder's list (DIR/SHA256SUMS) names is there in it, with that SHA-256.
windows_there() {
  grep -E "$windows" "$1/SHA256SUMS" > "$work/windows" || true
  while read -r sum name; do
    [ -f "$1/$name" ] && [ "$(openssl dgst -sha256 -r "$1/$name" | cut -d' ' -f1)" = "$sum" ] || return 1
  done < "$work/windows"
}

if [ $check -eq 1 ]; then
  if [ -f "$out/SHA256SUMS.sig" ] && [ -f "$out/SHA256SUMS" ] && cmp -s "$work/SHA256SUMS.image" "$out/SHA256SUMS.image" \
    && openssl x509 -in "$out/code-signing.crt" -pubkey -noout > "$work/pub.pem" 2>/dev/null \
    && openssl dgst -sha256 -verify "$work/pub.pem" -signature "$out/SHA256SUMS.sig" "$out/SHA256SUMS" >/dev/null 2>&1 \
    && windows_there "$out"; then
    echo "sign-code-arena: the builds are signed by $(openssl x509 -in "$out/code-signing.crt" -noout -subject | sed 's/^subject= *//')"
    exit 0
  fi
  echo "sign-code-arena: the builds the app serves are not signed (scripts/sign-code-arena.sh signs them)"
  exit 1
fi

mkdir -p signing
umask 077
# The certificate: the company's own when there; else one from this stack's CA, made again near its end.
if [ ! -f signing/code-signing.key ] || [ ! -f signing/code-signing.crt ] \
  || { [ -f certs/ca.key ] && ! openssl x509 -in signing/code-signing.crt -noout -checkend 2592000 >/dev/null 2>&1; }; then
  [ -f certs/ca.key ] && [ -f certs/ca.crt ] || { echo "sign-code-arena: no CA (certs/ca.key: scripts/make-cert.sh makes one) and no signing/code-signing.crt: nothing signs the builds"; exit 0; }
  domain=$(sed -n 's/^DOMAIN=//p' .env | tail -n 1 | tr -d '"'"'"' ')
  cat > "$work/ext" <<EOF
basicConstraints=critical,CA:FALSE
keyUsage=critical,digitalSignature
extendedKeyUsage=codeSigning
EOF
  openssl req -new -nodes -newkey rsa:3072 -keyout "$work/code-signing.key" -out "$work/code-signing.csr" \
    -subj "/O=Argus Arena/CN=Code Arena (${domain:-this stack})" 2>/dev/null
  openssl x509 -req -in "$work/code-signing.csr" -CA certs/ca.crt -CAkey certs/ca.key -CAcreateserial -CAserial "$work/ca.srl" \
    -out "$work/code-signing.crt" -days 1095 -sha256 -extfile "$work/ext" 2>/dev/null
  mv "$work/code-signing.key" signing/code-signing.key
  cat "$work/code-signing.crt" certs/ca.crt > signing/code-signing.crt
  echo "sign-code-arena: made signing/code-signing.crt from certs/ca.crt (3 years)"
fi
chmod 600 signing/code-signing.key

signed="$work/signed"
mkdir -p "$signed"
if grep -qE "$windows" "$work/SHA256SUMS.image"; then
  # Authenticode, by the app's own image (dotnet Llm.Api.dll sign-code-arena): the running app's image, in a container with
  # no network, no capabilities and nothing mounted; the key in on standard input, a tar of what it signed out.
  container=$("$engine" compose ps -q app 2>/dev/null | head -n 1)
  image=""
  [ -z "$container" ] || image=$("$engine" inspect --format '{{.Image}}' "$container" 2>/dev/null) || image=""
  [ -n "$image" ] || { echo "sign-code-arena: the app's container was not found: nothing signed" >&2; exit 1; }
  if ! cat signing/code-signing.key signing/code-signing.crt | "$engine" run --rm -i --network none --read-only --tmpfs /tmp \
    --cap-drop ALL --security-opt no-new-privileges "$image" sign-code-arena > "$work/signed.tar"; then
    echo "sign-code-arena: the Windows builds could not be signed (above): nothing changed" >&2
    exit 1
  fi
  tar -xf "$work/signed.tar" -C "$signed"
  if ! cmp -s "$signed/SHA256SUMS.image" "$work/SHA256SUMS.image" || ! windows_there "$signed"; then
    echo "sign-code-arena: what the app's image signed is not its list of builds: nothing changed" >&2
    exit 1
  fi
else
  cp "$work/SHA256SUMS.image" "$signed/SHA256SUMS"
  cp "$work/SHA256SUMS.image" "$signed/SHA256SUMS.image"
fi
openssl dgst -sha256 -sign signing/code-signing.key -out "$signed/SHA256SUMS.sig" "$signed/SHA256SUMS"
umask 022
mkdir -p "$out"
# The app reads it as uid 1000: a folder made 0700 by root (7.6.2's installer) hides the signature.
chmod 755 "$out"
# The folder is the app's mount: its files are replaced one by one, each by a rename, never the folder itself.
put() {
  cp "$1" "$2.new"
  chmod 644 "$2.new"
  mv -f "$2.new" "$2"
}
for exe in "$signed"/win-*/code-arena.exe "$signed"/win-*/code-arena-ide.exe; do
  [ -f "$exe" ] || continue
  rid=$(basename "$(dirname "$exe")")
  mkdir -p "$out/$rid"
  chmod 755 "$out/$rid"
  put "$exe" "$out/$rid/$(basename "$exe")"
done
# A Windows build the image no longer has: its signed copy goes.
for exe in "$out"/win-*/code-arena.exe "$out"/win-*/code-arena-ide.exe; do
  [ -f "$exe" ] || continue
  grep -qF "  $(basename "$(dirname "$exe")")/$(basename "$exe")" "$signed/SHA256SUMS" || rm -f "$exe"
done
for f in SHA256SUMS SHA256SUMS.image SHA256SUMS.sig; do
  put "$signed/$f" "$out/$f"
done
put signing/code-signing.crt "$out/code-signing.crt"
count=$(grep -cE "$windows" "$out/SHA256SUMS" || true)
echo "sign-code-arena: signed $(wc -l < "$out/SHA256SUMS" | tr -d ' ') builds ($count for Windows, with Authenticode) with $(openssl x509 -in signing/code-signing.crt -noout -subject | sed 's/^subject= *//')"
