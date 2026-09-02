#!/usr/bin/env bash
# Generate a local CA plus a wildcard TLS certificate for the stack, and the
# htpasswd file Traefik uses to guard the services that have no auth of their own.
#
# Why a private CA instead of a bare self-signed cert? A self-signed leaf has to
# be trusted per-hostname; a CA is trusted once, and every *.llm.localhost name
# then validates cleanly, including ones you add later.
#
#   ./scripts/gen-certs.sh                     # uses LLM_DOMAIN from .env
#   ./scripts/gen-certs.sh --force             # regenerate even if present
set -euo pipefail

# Git Bash rewrites an argument that looks like a path, so `-subj "/CN=..."`
# arrives at openssl as "C:/Program Files/Git/CN=...". openssl then fails
# with "subject name is expected to be in the format /type0=value0/...", and
# because the openssl calls below send stderr to /dev/null under `set -e`,
# the script dies silently having already truncated ca.crt to one byte -- so
# the next run reports a corrupt CA rather than the real cause.
#
# MSYS_NO_PATHCONV turns that rewriting off. On Windows prefer
# scripts/gen-certs.ps1, which does not go through the translation at all.
export MSYS_NO_PATHCONV=1
export MSYS2_ARG_CONV_EXCL='*' 

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$ROOT"
CERT_DIR="config/traefik/certs"
AUTH_DIR="config/traefik/auth"
FORCE=0
[[ "${1:-}" == "--force" ]] && FORCE=1

get() { grep -E "^$1=" .env 2>/dev/null | head -n1 | cut -d= -f2- | tr -d '[:space:]'; }
DOMAIN="$(get LLM_DOMAIN)"; DOMAIN="${DOMAIN:-llm.localhost}"
AUTH_USER="$(get PROXY_AUTH_USER)"; AUTH_USER="${AUTH_USER:-admin}"
AUTH_PASS="$(get PROXY_AUTH_PASSWORD)"

command -v openssl >/dev/null 2>&1 || { echo "openssl is required" >&2; exit 1; }
mkdir -p "$CERT_DIR" "$AUTH_DIR"

# ---------------------------------------------------------------------------
# Local CA
# ---------------------------------------------------------------------------
if [[ $FORCE -eq 1 || ! -f "$CERT_DIR/ca.crt" ]]; then
  echo "==> Generating local CA"
  openssl req -x509 -newkey rsa:4096 -sha256 -days 3650 -nodes \
    -keyout "$CERT_DIR/ca.key" -out "$CERT_DIR/ca.crt" \
    -subj "/CN=LLMService Local CA/O=LLMService" \
    -addext "basicConstraints=critical,CA:TRUE,pathlen:0" \
    -addext "keyUsage=critical,keyCertSign,cRLSign" 2>/dev/null
  echo "    ca.crt"
else
  echo "==> CA already exists (use --force to regenerate)"
fi

# ---------------------------------------------------------------------------
# Wildcard leaf certificate
# ---------------------------------------------------------------------------
if [[ $FORCE -eq 1 || ! -f "$CERT_DIR/$DOMAIN.crt" ]]; then
  echo "==> Generating wildcard certificate for $DOMAIN"

  # A wildcard matches exactly one label, so *.llm.localhost does NOT cover
  # llm.localhost itself. Both names must be listed.
  cat > "$CERT_DIR/leaf.ext" <<EXT
basicConstraints=CA:FALSE
keyUsage=critical,digitalSignature,keyEncipherment
extendedKeyUsage=serverAuth
subjectAltName=DNS:$DOMAIN,DNS:*.$DOMAIN,DNS:localhost,IP:127.0.0.1
EXT

  openssl req -newkey rsa:2048 -nodes \
    -keyout "$CERT_DIR/$DOMAIN.key" -out "$CERT_DIR/leaf.csr" \
    -subj "/CN=$DOMAIN/O=LLMService" 2>/dev/null

  # 825 days is the maximum lifetime browsers accept for a server certificate.
  openssl x509 -req -in "$CERT_DIR/leaf.csr" \
    -CA "$CERT_DIR/ca.crt" -CAkey "$CERT_DIR/ca.key" -CAcreateserial \
    -out "$CERT_DIR/$DOMAIN.crt" -days 825 -sha256 \
    -extfile "$CERT_DIR/leaf.ext" 2>/dev/null

  # Traefik serves the chain, so clients that already trust the CA validate
  # without needing it presented separately.
  cat "$CERT_DIR/$DOMAIN.crt" "$CERT_DIR/ca.crt" > "$CERT_DIR/$DOMAIN.fullchain.crt"
  rm -f "$CERT_DIR/leaf.csr" "$CERT_DIR/leaf.ext"
  echo "    $DOMAIN.fullchain.crt"
  echo "    $DOMAIN.key"
else
  echo "==> Certificate for $DOMAIN already exists (use --force to regenerate)"
fi

# ---------------------------------------------------------------------------
# Basic-auth credentials for services with no login of their own
# ---------------------------------------------------------------------------
if [[ $FORCE -eq 1 || ! -f "$AUTH_DIR/users.htpasswd" ]]; then
  if [[ -z "$AUTH_PASS" || "$AUTH_PASS" == *change-me* ]]; then
    echo "!!  PROXY_AUTH_PASSWORD is not set in .env; run bootstrap first" >&2
    exit 1
  fi
  echo "==> Generating basic-auth file"
  # apr1 (Apache MD5) is one of the formats Traefik accepts and is the only one
  # plain openssl can emit without extra tooling.
  hash="$(openssl passwd -apr1 "$AUTH_PASS")"
  printf '%s:%s\n' "$AUTH_USER" "$hash" > "$AUTH_DIR/users.htpasswd"
  echo "    users.htpasswd  ($AUTH_USER)"
else
  echo "==> users.htpasswd already exists (use --force to regenerate)"
fi

echo
echo "  Trust the CA to remove browser warnings:"
echo "    Windows:  certutil -addstore -user Root \"$ROOT\$CERT_DIR\ca.crt\""
echo "    Linux:    sudo cp $CERT_DIR/ca.crt /usr/local/share/ca-certificates/llmservice.crt && sudo update-ca-certificates"
echo "    macOS:    sudo security add-trusted-cert -d -k /Library/Keychains/System.keychain $CERT_DIR/ca.crt"
echo
