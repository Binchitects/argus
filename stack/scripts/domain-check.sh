#!/usr/bin/env bash
# Prove the running stack answers for LLM_DOMAIN -- and only for it.
#
# The domain lives in .env and nowhere else: Traefik routes, the TLS
# certificate (tls-init), Authelia's issuer, cookie domain, access rules and
# OIDC redirect URIs all derive from it at startup. This checks each of those
# against the live stack, so "I changed the domain and ran up" can be verified
# rather than assumed.
#
#   ./scripts/domain-check.sh                 # the domain in .env
#   ./scripts/domain-check.sh --old llm.localhost   # also assert the old one is gone
#
# Uses curl --resolve, so it works for any domain without /etc/hosts entries.
set -uo pipefail
ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"; cd "$ROOT"
export MSYS_NO_PATHCONV=1

OLD=""
[[ "${1:-}" == "--old" ]] && OLD="${2:-}"

get() { grep -E "^$1=" .env 2>/dev/null | head -n1 | cut -d= -f2- | tr -d '\r[:space:]'; }
DOM="$(get LLM_DOMAIN)"; DOM="${DOM:-llm.localhost}"
PORT="$(get TRAEFIK_HTTPS_PORT)"; PORT="${PORT:-443}"
CRT="config/traefik/certs/tls.crt"
FAILS=0

ok()   { printf '  \033[32mOK\033[0m    %s\n' "$*"; }
bad()  { printf '  \033[31mFAIL\033[0m  %s\n' "$*"; FAILS=$((FAILS+1)); }

resolve=()
for h in "" chat admin gateway api metrics alerts argus; do
  name="${h:+$h.}$DOM"; resolve+=(--resolve "$name:$PORT:127.0.0.1")
done
C=(curl -s --max-time 20 --cacert "$CRT" "${resolve[@]}")
# As a browser navigating: the app sends a browser to sign in (302) and a program a 401.
B=("${C[@]}" -H 'Accept: text/html')
u() { local h="${1:+$1.}" p="${2:-/}"; [[ "$PORT" == 443 ]] && echo "https://$h$DOM$p" || echo "https://$h$DOM:$PORT$p"; }
APP="$(u "" "")"; APP="${APP%/}"   # https://<domain>[:port]: the app, the sign-in and the OIDC issuer

echo "domain: $DOM"

echo "1. certificate"
san="$(echo | openssl s_client -connect "127.0.0.1:$PORT" -servername "$DOM" 2>/dev/null \
       | openssl x509 -noout -ext subjectAltName 2>/dev/null | tr -d ' \n')"
[[ "$san" == *"DNS:$DOM,DNS:*.$DOM"* ]] && ok "served certificate covers $DOM and *.$DOM" || bad "served SAN: ${san:-none}"
cmp -s <(openssl x509 -in "$CRT" -noout -fingerprint 2>/dev/null) \
       <(echo | openssl s_client -connect "127.0.0.1:$PORT" -servername "$DOM" 2>/dev/null | openssl x509 -noout -fingerprint 2>/dev/null) \
  && ok "exported $CRT is the certificate being served" || bad "$CRT does not match the served certificate"

echo "2. routes (verified TLS)"
expect() {  # host path expected-codes-regex label
  local code; code="$("${B[@]}" -o /dev/null -w '%{http_code}' "$(u "$1" "$2")")"
  [[ "$code" =~ ^($3)$ ]] && ok "${1:+$1.}$DOM$2 -> $code" || bad "${1:+$1.}$DOM$2 -> $code (expected $3)"
}
expect "" /readyz 200
expect gateway /health/liveliness 200
expect chat / "200|302"
expect admin / 302   # the old admin panel address, redirected into the app
expect metrics / 302
expect alerts / 302
expect api /v1/models "302|401"
code="$("${C[@]}" -o /dev/null -w '%{http_code}' -H 'Accept: application/json' "$(u metrics /)")"
[[ "$code" == 401 ]] && ok "a program without credentials gets 401 at metrics" || bad "metrics for a program -> $code"
if docker ps --format '{{.Names}}' | grep -qx argus; then expect argus /health "200|401|404"; fi

echo "3. single sign-on"
disc="$("${C[@]}" "$(u "" /.well-known/openid-configuration)")"
iss="$(printf '%s' "$disc" | python3 -c 'import json,sys; print(json.load(sys.stdin).get("issuer",""))' 2>/dev/null)"
[[ "$iss" == "$APP/" ]] && ok "OIDC issuer $iss" || bad "OIDC issuer '${iss}'"
loc="$("${B[@]}" -o /dev/null -w '%{redirect_url}' "$(u metrics /)")"
[[ "$loc" == "$APP/login?rd="* ]] && ok "forward-auth sends the browser to sign in at $APP" || bad "forward-auth redirect: $loc"
loc="$("${C[@]}" -o /dev/null -w '%{redirect_url}' "$(u chat /oauth/oidc/login)")"
[[ "$loc" == "$APP/connect/authorize"*"redirect_uri=https%3A%2F%2Fchat.$DOM"* ]] \
  && ok "Open WebUI SSO starts at the app with its own redirect URI" || bad "Open WebUI SSO redirect: ${loc:0:140}"

echo "4. inside the network"
if docker exec open-webui python -c "import socket; socket.gethostbyname('$DOM')" 2>/dev/null; then
  ok "containers resolve $DOM (the issuer) to Traefik"
else
  bad "open-webui cannot resolve $DOM"
fi

if [[ -n "$OLD" && "$OLD" != "$DOM" ]]; then
  echo "5. old domain $OLD is gone"
  code="$(curl -sk --max-time 10 --resolve "chat.$OLD:$PORT:127.0.0.1" -o /dev/null -w '%{http_code}' "https://chat.$OLD/")"
  [[ "$code" == 404 ]] && ok "chat.$OLD -> 404 (no route)" || bad "chat.$OLD -> $code"
fi

echo
[[ $FAILS -eq 0 ]] && echo "domain-check: all passed for $DOM" || echo "domain-check: $FAILS failed for $DOM"
exit $FAILS
