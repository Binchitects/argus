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
for h in "" auth chat grafana admin gateway api metrics alerts argus; do
  name="${h:+$h.}$DOM"; resolve+=(--resolve "$name:$PORT:127.0.0.1")
done
C=(curl -s --max-time 20 --cacert "$CRT" "${resolve[@]}")
u() { local h="$1" p="${2:-/}"; [[ "$PORT" == 443 ]] && echo "https://$h.$DOM$p" || echo "https://$h.$DOM:$PORT$p"; }

echo "domain: $DOM"

echo "1. certificate"
san="$(echo | openssl s_client -connect "127.0.0.1:$PORT" -servername "auth.$DOM" 2>/dev/null \
       | openssl x509 -noout -ext subjectAltName 2>/dev/null | tr -d ' \n')"
[[ "$san" == *"DNS:$DOM,DNS:*.$DOM"* ]] && ok "served certificate covers $DOM and *.$DOM" || bad "served SAN: ${san:-none}"
cmp -s <(openssl x509 -in "$CRT" -noout -fingerprint 2>/dev/null) \
       <(echo | openssl s_client -connect "127.0.0.1:$PORT" -servername "auth.$DOM" 2>/dev/null | openssl x509 -noout -fingerprint 2>/dev/null) \
  && ok "exported $CRT is the certificate being served" || bad "$CRT does not match the served certificate"

echo "2. routes (verified TLS)"
expect() {  # host path expected-codes-regex label
  local code; code="$("${C[@]}" -o /dev/null -w '%{http_code}' "$(u "$1" "$2")")"
  [[ "$code" =~ ^($3)$ ]] && ok "$1.$DOM$2 -> $code" || bad "$1.$DOM$2 -> $code (expected $3)"
}
expect auth /api/health 200
expect gateway /health/liveliness 200
expect chat / "200|302"
expect grafana /login 200
expect admin / 302
expect metrics / 302
expect alerts / 302
expect api /v1/models "302|401"
if docker ps --format '{{.Names}}' | grep -qx argus; then expect argus /health "200|401|404"; fi

echo "3. single sign-on"
disc="$("${C[@]}" "$(u auth /.well-known/openid-configuration)")"
iss="$(printf '%s' "$disc" | python3 -c 'import json,sys; print(json.load(sys.stdin).get("issuer",""))' 2>/dev/null)"
[[ "$iss" == "https://auth.$DOM" ]] && ok "OIDC issuer $iss" || bad "OIDC issuer '${iss}'"
loc="$("${C[@]}" -o /dev/null -w '%{redirect_url}' "$(u metrics /)")"
[[ "$loc" == "https://auth.$DOM/"* ]] && ok "forward-auth redirects to auth.$DOM" || bad "forward-auth redirect: $loc"
loc="$("${C[@]}" -o /dev/null -w '%{redirect_url}' "$(u grafana /login/generic_oauth)")"
[[ "$loc" == "https://auth.$DOM/api/oidc/authorization"*"redirect_uri=https%3A%2F%2Fgrafana.$DOM"* ]] \
  && ok "Grafana SSO starts at auth.$DOM with its own redirect URI" || bad "Grafana SSO redirect: ${loc:0:140}"
loc="$("${C[@]}" -o /dev/null -w '%{redirect_url}' "$(u chat /oauth/oidc/login)")"
[[ "$loc" == "https://auth.$DOM/api/oidc/authorization"* ]] \
  && ok "Open WebUI SSO starts at auth.$DOM" || bad "Open WebUI SSO redirect: ${loc:0:140}"

echo "4. inside the network"
if docker exec open-webui python -c "import socket; socket.gethostbyname('auth.$DOM')" 2>/dev/null; then
  ok "containers resolve auth.$DOM to Traefik"
else
  bad "open-webui cannot resolve auth.$DOM"
fi

if [[ -n "$OLD" && "$OLD" != "$DOM" ]]; then
  echo "5. old domain $OLD is gone"
  code="$(curl -sk --max-time 10 --resolve "chat.$OLD:$PORT:127.0.0.1" -o /dev/null -w '%{http_code}' "https://chat.$OLD/")"
  [[ "$code" == 404 ]] && ok "chat.$OLD -> 404 (no route)" || bad "chat.$OLD -> $code"
fi

echo
[[ $FAILS -eq 0 ]] && echo "domain-check: all passed for $DOM" || echo "domain-check: $FAILS failed for $DOM"
exit $FAILS
