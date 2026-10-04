#!/bin/sh
# Traefik's certificate, made with openssl: a CA of this deployment's own and,
# signed by it, a certificate for DOMAIN, gateway.DOMAIN and argus.DOMAIN (and
# *.DOMAIN). Or a company's certificate, installed as it is.
#
#   scripts/make-cert.sh                          from .env's DOMAIN; again to renew (same CA)
#   scripts/make-cert.sh --domain chat.example.com
#   scripts/make-cert.sh --cert FILE --key FILE [--chain FILE]   a certificate you have
#   scripts/make-cert.sh --days 825               how long the certificate lasts (825 by default)
#
# Writes certs/tls.crt and certs/tls.key (with certs/ca.crt and certs/ca.key when
# it makes them), and config/traefik/certificate.yml, which tells Traefik to serve
# them: it picks them up at once, without a restart. None of these are committed.
#
# Then trust certs/ca.crt on the machines and in the tools that use the stack
# (it is the certificate to import; ca.key stays here, and renews the certificate).
set -eu
cd "$(dirname "$0")/.."

domain="" cert="" key="" chain="" days=825
while [ $# -gt 0 ]; do
  case "$1" in
    --domain) domain="$2"; shift 2 ;;
    --cert) cert="$2"; shift 2 ;;
    --key) key="$2"; shift 2 ;;
    --chain) chain="$2"; shift 2 ;;
    --days) days="$2"; shift 2 ;;
    -h|--help) sed -n '2,17p' "$0"; exit 0 ;;
    *) echo "make-cert: unknown option $1 (--help)" >&2; exit 2 ;;
  esac
done
if [ -z "$domain" ] && [ -f .env ]; then
  domain=$(sed -n 's/^DOMAIN=//p' .env | tail -n 1 | tr -d '"'"'"' ')
fi
domain=${domain:-llm.localhost}
command -v openssl >/dev/null || { echo "make-cert: openssl is not installed" >&2; exit 1; }

mkdir -p certs
umask 077

if [ -n "$cert" ] || [ -n "$key" ]; then
  [ -f "$cert" ] && [ -f "$key" ] || { echo "make-cert: --cert and --key must both name files" >&2; exit 2; }
  # The key must be the certificate's.
  if [ "$(openssl x509 -in "$cert" -noout -pubkey | openssl sha256)" != "$(openssl pkey -in "$key" -pubout | openssl sha256)" ]; then
    echo "make-cert: $key is not the key of $cert" >&2; exit 1
  fi
  { cat "$cert"; [ -n "$chain" ] && cat "$chain"; } > certs/tls.crt
  cp "$key" certs/tls.key
else
  if [ ! -f certs/ca.key ] || [ ! -f certs/ca.crt ]; then
    echo "make-cert: making a CA for $domain (certs/ca.crt, 10 years)"
    openssl req -x509 -new -nodes -newkey rsa:3072 -sha256 -days 3650 \
      -keyout certs/ca.key -out certs/ca.crt -subj "/O=Argus Arena/CN=Argus Arena CA ($domain)" \
      -addext "basicConstraints=critical,CA:TRUE,pathlen:0" \
      -addext "keyUsage=critical,keyCertSign,cRLSign" 2>/dev/null
  fi
  work=$(mktemp -d)
  trap 'rm -rf "$work"' EXIT
  cat > "$work/ext" <<EOF
basicConstraints=critical,CA:FALSE
keyUsage=critical,digitalSignature,keyEncipherment
extendedKeyUsage=serverAuth
subjectAltName=DNS:$domain,DNS:*.$domain,DNS:gateway.$domain,DNS:argus.$domain
EOF
  openssl req -new -nodes -newkey rsa:2048 -keyout "$work/tls.key" -out "$work/tls.csr" -subj "/O=Argus Arena/CN=$domain" 2>/dev/null
  openssl x509 -req -in "$work/tls.csr" -CA certs/ca.crt -CAkey certs/ca.key -CAcreateserial -CAserial "$work/ca.srl" \
    -out "$work/tls.crt" -days "$days" -sha256 -extfile "$work/ext" 2>/dev/null
  # The certificate, then the CA: clients that trust the CA can build the chain.
  cat "$work/tls.crt" certs/ca.crt > certs/tls.crt
  mv "$work/tls.key" certs/tls.key
fi
chmod 644 certs/tls.crt; [ -f certs/ca.crt ] && chmod 644 certs/ca.crt
chmod 600 certs/tls.key; [ -f certs/ca.key ] && chmod 600 certs/ca.key

umask 022
cat > config/traefik/certificate.yml <<'EOF'
# Written by scripts/make-cert.sh: the certificate Traefik serves (certs/, mounted at /certs).
tls:
  stores:
    default:
      defaultCertificate:
        certFile: /certs/tls.crt
        keyFile: /certs/tls.key
EOF

echo "make-cert: certs/tls.crt for:"
openssl x509 -in certs/tls.crt -noout -subject -enddate -ext subjectAltName | sed 's/^/  /'
if [ -f certs/ca.crt ] && [ -z "$cert" ]; then
  echo "make-cert: trust certs/ca.crt where the stack is used, e.g."
  echo "  Linux:   sudo cp certs/ca.crt /usr/local/share/ca-certificates/argus-arena.crt && sudo update-ca-certificates"
  echo "  Windows: certutil -addstore -f Root ca.crt   macOS: sudo security add-trusted-cert -d -r trustRoot -k /Library/Keychains/System.keychain ca.crt"
  echo "  curl:    curl --cacert certs/ca.crt https://$domain/   Node: NODE_EXTRA_CA_CERTS=certs/ca.crt"
fi
