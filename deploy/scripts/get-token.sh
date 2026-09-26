#!/usr/bin/env bash
# Fetch a short-lived access token for machine clients of the engine API
# (https://api.<LLM_DOMAIN>).
#
# The replacement for a static key: the client secret never travels to the
# service, only a token that expires within the hour. The app issues it with
# the OAuth2 client_credentials grant (client `api`, scope `api`).
#
#   export TOKEN=$(./scripts/get-token.sh)
#
# The gateway (https://gateway.<LLM_DOMAIN>) takes your personal API key
# instead; it tracks spend per key, so it needs no token.

# `python` is not a command on a python3-only distro (Ubuntu 26.04 ships no
# alias), so a bare call here dies with "command not found".
PY="${PYTHON:-python3}"

set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$ROOT"
get() { grep -E "^$1=" .env | head -n1 | cut -d= -f2- | tr -d '[:space:]'; }

if [[ $# -gt 0 ]]; then
  echo "usage: $0   (no options: the token is for https://api.<LLM_DOMAIN>)" >&2
  exit 1
fi

DOMAIN="$(get LLM_DOMAIN)"; DOMAIN="${DOMAIN:-llm.localhost}"
PORT="$(get TRAEFIK_HTTPS_PORT)"; PORT="${PORT:-443}"
SECRET="$(get API_OIDC_CLIENT_SECRET)"
[[ -n "$SECRET" ]] || { echo "API_OIDC_CLIENT_SECRET missing - set it in .env" >&2; exit 1; }

CA="config/traefik/certs/tls.crt"

# --resolve + --ssl-no-revoke are only needed because *.localhost does not
# resolve in CLI tools and Windows curl cannot check revocation for a self-signed certificate.
curl -s --ssl-no-revoke \
  --resolve "$DOMAIN:$PORT:127.0.0.1" --cacert "$CA" \
  -u "api:$SECRET" \
  -d "grant_type=client_credentials&scope=api" \
  "https://$DOMAIN:$PORT/connect/token" |
"$PY" -c '
import json, sys
d = json.load(sys.stdin)
if "access_token" not in d:
    sys.stderr.write("token request failed: %s\n" % json.dumps(d)[:300]); sys.exit(1)
print(d["access_token"])
'
