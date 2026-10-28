#!/bin/sh
# Signs the Code Arena builds this stack serves, with a code-signing certificate of the stack's own
# CA (certs/ca.key, which scripts/make-cert.sh made): people's installers and `code-arena update`
# then check that a build is the one this server's CA signed, not only that its SHA-256 matches
# what the server lists. Runs on the host, where the CA's key is; no container sees the signing key.
#
#   scripts/sign-code-arena.sh          sign what the app's image holds (the app must be running)
#   scripts/sign-code-arena.sh --check  say whether the signature there is the image's, and exit 1 if not
#
# Writes signing/code-signing.key and .crt (made from the CA when missing, or within 30 days of
# their end; 3 years), and, for the app to serve, $DATA_DIR/code-arena-signature/: SHA256SUMS (the
# image's), SHA256SUMS.sig (its RSA SHA-256 signature) and code-signing.crt (the certificate, then
# the CA). A company's own code-signing certificate goes in signing/ as code-signing.crt and .key.
# installer.sh runs it on install, upgrade and repair when a CA or a certificate is there.
set -eu
cd "$(dirname "$0")/.."

check=0
case "${1:-}" in
  --check) check=1 ;;
  -h|--help) sed -n '2,15p' "$0"; exit 0 ;;
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

# The image's list of builds and their SHA-256.
if ! "$engine" compose cp app:/downloads/code-arena/SHA256SUMS "$work/SHA256SUMS" >/dev/null 2>&1; then
  echo "sign-code-arena: the app's image has no Code Arena builds (or the app is not running): nothing to sign"
  exit 0
fi

if [ $check -eq 1 ]; then
  if [ -f "$out/SHA256SUMS.sig" ] && cmp -s "$work/SHA256SUMS" "$out/SHA256SUMS" \
    && openssl x509 -in "$out/code-signing.crt" -pubkey -noout > "$work/pub.pem" 2>/dev/null \
    && openssl dgst -sha256 -verify "$work/pub.pem" -signature "$out/SHA256SUMS.sig" "$out/SHA256SUMS" >/dev/null 2>&1; then
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

openssl dgst -sha256 -sign signing/code-signing.key -out "$work/SHA256SUMS.sig" "$work/SHA256SUMS"
umask 022
mkdir -p "$out"
cp "$work/SHA256SUMS" "$out/SHA256SUMS"
cp "$work/SHA256SUMS.sig" "$out/SHA256SUMS.sig"
cp signing/code-signing.crt "$out/code-signing.crt"
chmod 644 "$out/SHA256SUMS" "$out/SHA256SUMS.sig" "$out/code-signing.crt"
echo "sign-code-arena: signed $(wc -l < "$out/SHA256SUMS" | tr -d ' ') builds with $(openssl x509 -in signing/code-signing.crt -noout -subject | sed 's/^subject= *//')"
