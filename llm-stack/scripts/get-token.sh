#!/usr/bin/env bash
# Fetch a short-lived access token for machine clients.
#
# This is the replacement for a static API key: the secret never travels to the
# service, only a token that expires. Authelia issues it via the OAuth2
# client_credentials grant.
#
#   ./scripts/get-token.sh                       # token for the API
#   ./scripts/get-token.sh --audience gateway    # token for LiteLLM
#   export TOKEN=$(./scripts/get-token.sh)
set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$ROOT"
get() { grep -E "^$1=" .env | head -n1 | cut -d= -f2- | tr -d '[:space:]'; }

AUD="api"
while [[ $# -gt 0 ]]; do
  case "$1" in
    --audience) AUD="$2"; shift 2 ;;
    *) echo "usage: $0 [--audience api|gateway]" >&2; exit 1 ;;
  esac
done

DOMAIN="$(get LLM_DOMAIN)"; DOMAIN="${DOMAIN:-llm.localhost}"
SECRET="$(get API_OIDC_CLIENT_SECRET)"
[[ -n "$SECRET" ]] || { echo "API_OIDC_CLIENT_SECRET missing - run scripts/gen-auth.sh" >&2; exit 1; }

CA="config/traefik/certs/ca.crt"

# --resolve + --ssl-no-revoke are only needed because *.localhost does not
# resolve in CLI tools and Windows curl cannot check revocation for a private CA.
RESP="$(curl -s --ssl-no-revoke \
  --resolve "auth.$DOMAIN:443:127.0.0.1" --cacert "$CA" \
  -u "api:$SECRET" \
  -d "grant_type=client_credentials&scope=authelia.bearer.authz&audience=https://$AUD.$DOMAIN" \
  "https://auth.$DOMAIN/api/oidc/token")"

python -c "
import json, sys
d = json.loads('''$RESP''')
if 'access_token' not in d:
    sys.stderr.write('token request failed: %s\n' % json.dumps(d)[:300]); sys.exit(1)
print(d['access_token'])
"
