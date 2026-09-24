#!/usr/bin/env bash
# Full authentication audit against the running stack. It performs a REAL
# sign-in at the app and a real OAuth2 authorization-code exchange for every
# OIDC client, then reports the claims actually delivered; it checks the
# machine-client token, forwardAuth for the services with no sign-in of their
# own, and the app's own protections.
#
# This catches what a config check cannot -- missing email claims, wrong
# redirect URIs, stale client secrets -- without needing a browser.
# Exit status: the number of failed checks (0 = clean).

# `python` is not a command on a python3-only distro (Ubuntu 26.04 ships no
# alias), so a bare call here dies with "command not found".
PY="${PYTHON:-python3}"

set -uo pipefail
ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"; cd "$ROOT"
export MSYS_NO_PATHCONV=1

get() { grep -E "^$1=" .env | head -n1 | cut -d= -f2- | tr -d '[:space:]'; }
CA="config/traefik/certs/tls.crt"
DOM="$(get LLM_DOMAIN)"; DOM="${DOM:-llm.localhost}"
USER_NAME="admin"
PASS="$(get ADMIN_PASSWORD)"; PASS="${PASS:-$(get AUTHELIA_ADMIN_PASSWORD)}"
APP="https://$DOM"
JAR="$(mktemp)"
trap 'rm -f "$JAR"' EXIT

RES=(--ssl-no-revoke --cacert "$CA" --resolve "$DOM:443:127.0.0.1")
for h in chat grafana traces api gateway metrics alerts admin; do RES+=(--resolve "$h.$DOM:443:127.0.0.1"); done
XRW=(-H 'X-Requested-With: audit')

FAILS=0
green() { printf '  \033[32m%-10s\033[0m %s\n' "$1" "$2"; }
red()   { printf '  \033[31m%-10s\033[0m %s\n' "$1" "$2"; FAILS=$((FAILS + 1)); }
skip()  { printf '  \033[90m%-10s\033[0m %s\n' SKIP "$1"; }
status() { curl -s -o /dev/null -w '%{http_code}' "${RES[@]}" "$@"; }
profile_on() { [[ ",$(get COMPOSE_PROFILES)," == *",$1,"* ]]; }

echo "  AUTHENTICATION AUDIT"
echo "  ===================="
echo

# ---------------------------------------------------------------------------
# The audit reads secrets from .env, while each container uses what was baked
# in when it was created. If those drift, the audit passes while every real
# sign-in fails.
echo "0. Container secrets match .env (drift check)"
for pair in "app:Oidc__GrafanaSecret:GRAFANA_OIDC_CLIENT_SECRET" \
            "app:Oidc__OpenWebUiSecret:OPENWEBUI_OIDC_CLIENT_SECRET" \
            "app:Oidc__ApiSecret:API_OIDC_CLIENT_SECRET" \
            "grafana:GF_AUTH_GENERIC_OAUTH_CLIENT_SECRET:GRAFANA_OIDC_CLIENT_SECRET" \
            "open-webui:OAUTH_CLIENT_SECRET:OPENWEBUI_OIDC_CLIENT_SECRET" \
            "langfuse:AUTH_CUSTOM_CLIENT_SECRET:LANGFUSE_OIDC_CLIENT_SECRET"; do
  c=${pair%%:*}; rest=${pair#*:}; ev=${rest%%:*}; fv=${rest#*:}
  # A container that is not running cannot hold a stale secret: its profile is off.
  if ! docker inspect "$c" >/dev/null 2>&1; then skip "$c (not running)"; continue; fi
  inc=$(docker inspect "$c" --format '{{range .Config.Env}}{{println .}}{{end}}' 2>/dev/null | grep "^$ev=" | cut -d= -f2-)
  if [ "$inc" = "$(get "$fv")" ]; then green OK "$c ($ev)"
  else red STALE "$c holds an old $fv - run: docker compose up -d --force-recreate $c"; fi
done
echo

# ---------------------------------------------------------------------------
echo "1. Sign in at the app as '$USER_NAME'"
LOGIN=$(curl -s "${RES[@]}" -c "$JAR" -b "$JAR" "${XRW[@]}" -H 'Content-Type: application/json' \
  -d "{\"userName\":\"$USER_NAME\",\"password\":\"$PASS\"}" "$APP/api/auth/login")
if echo "$LOGIN" | grep -q '"status":"ok"'; then green OK "session established"
elif echo "$LOGIN" | grep -q '"status":"2fa"'; then red FAIL "the admin has 2FA on; this audit signs in with a password only"
else red FAIL "${LOGIN:0:120}"; fi
ME=$(curl -s "${RES[@]}" -b "$JAR" "$APP/api/auth/me")
echo "$ME" | grep -q '"isAdmin":true' && green OK "the session is an admin" || red FAIL "me: ${ME:0:100}"

# ---------------------------------------------------------------------------
echo
echo "2. Authorization-code flow per client (claims actually delivered)"
flow() {
  local id="$1" uri="$2" scope="$3" secret="$4" loc code tok again
  loc=$(curl -s -o /dev/null -w '%{redirect_url}' "${RES[@]}" -b "$JAR" -c "$JAR" -G \
    --data-urlencode "client_id=$id" --data-urlencode "redirect_uri=$uri" \
    --data-urlencode "response_type=code" --data-urlencode "scope=$scope" \
    --data-urlencode "state=auditauditaudit0123" "$APP/connect/authorize")
  code=$(printf '%s' "$loc" | sed -n 's/.*[?&]code=\([^&]*\).*/\1/p')
  if [ -z "$code" ]; then red FAIL "$id: no code (redirect was: ${loc:0:100})"; return; fi
  tok=$(curl -s "${RES[@]}" -u "$id:$secret" \
    --data-urlencode "grant_type=authorization_code" --data-urlencode "code=$code" --data-urlencode "redirect_uri=$uri" \
    "$APP/connect/token")
  if ! printf '%s' "$tok" | "$PY" -c '
import json, sys, base64
cid = sys.argv[1]
d = json.load(sys.stdin)
if "id_token" not in d:
    print("  \033[31mFAIL\033[0m       %s: %s" % (cid, json.dumps(d)[:120])); sys.exit(1)
p = d["id_token"].split(".")[1]; p += "=" * (-len(p) % 4)
c = json.loads(base64.urlsafe_b64decode(p))
print("  \033[32mOK\033[0m         %-11s sub=%s" % (cid, str(c.get("sub"))[:8]))
print("             id_token claims: email=%s name=%s preferred_username=%s groups=%s"
      % (c.get("email", "MISSING"), c.get("name", "-"), c.get("preferred_username", "-"), c.get("groups", "-")))
sys.exit(0 if c.get("email") else 2)
' "$id"; then red FAIL "$id: token exchange or claims (see above)"; fi
  # The same code must never work twice.
  again=$(curl -s "${RES[@]}" -u "$id:$secret" --data-urlencode "grant_type=authorization_code" \
    --data-urlencode "code=$code" --data-urlencode "redirect_uri=$uri" "$APP/connect/token")
  echo "$again" | grep -q '"invalid_grant"' && green OK "$id: a used code is refused" || red FAIL "$id: code reuse: ${again:0:80}"
}
flow grafana    "https://grafana.$DOM/login/generic_oauth" "openid profile email groups" "$(get GRAFANA_OIDC_CLIENT_SECRET)"
flow open-webui "https://chat.$DOM/oauth/oidc/callback"    "openid profile email groups" "$(get OPENWEBUI_OIDC_CLIENT_SECRET)"
if profile_on tracing; then
  flow langfuse "https://traces.$DOM/api/auth/callback/custom" "openid email profile" "$(get LANGFUSE_OIDC_CLIENT_SECRET)"
else
  skip "langfuse (tracing profile off)"
fi
c=$(curl -s "${RES[@]}" -u "grafana:not-the-secret" -d "grant_type=client_credentials" "$APP/connect/token")
echo "$c" | grep -q '"invalid_client"' && green OK "a wrong client secret is refused" || red FAIL "wrong secret: ${c:0:80}"
loc=$(curl -s -o /dev/null -w '%{redirect_url}' "${RES[@]}" -b "$JAR" -G --data-urlencode "client_id=grafana" \
  --data-urlencode "redirect_uri=https://evil.example/cb" --data-urlencode "response_type=code" \
  --data-urlencode "scope=openid" "$APP/connect/authorize")
[[ "$loc" != https://evil.example* ]] && green OK "an unregistered redirect URI is never followed" || red FAIL "redirected to $loc"

# ---------------------------------------------------------------------------
echo
echo "3. Machine client (client_credentials) + engine API"
TOK=$(bash scripts/get-token.sh 2>/dev/null)
[ -n "$TOK" ] && green OK "token issued (scripts/get-token.sh)" || red FAIL "no token"
c=$(status -H "Authorization: Bearer $TOK" "https://api.$DOM/v1/models")
[ "$c" = "200" ] && green OK "api with token -> 200" || red FAIL "api with token -> $c"
c=$(status -H 'Accept: application/json' "https://api.$DOM/v1/models")
[ "$c" != "200" ] && green OK "api without token -> $c (denied)" || red FAIL "api unauthenticated -> 200"
c=$(status -H "Authorization: Bearer not-a-token" "https://api.$DOM/v1/models")
[ "$c" = "401" ] && green OK "api with a forged token -> 401" || red FAIL "api with a forged token -> $c"
c=$(status -H "Authorization: Bearer $TOK" -H 'Accept: application/json' "https://metrics.$DOM/-/healthy")
[ "$c" = "403" ] && green OK "the machine token opens nothing else (metrics -> 403)" || red FAIL "machine token on metrics -> $c"

# ---------------------------------------------------------------------------
echo
echo "4. forwardAuth-gated services, anonymous (must be denied)"
for h in metrics alerts; do
  loc=$(curl -s -o /dev/null -w '%{http_code} %{redirect_url}' "${RES[@]}" -H 'Accept: text/html' "https://$h.$DOM/-/healthy")
  [[ "$loc" == "302 $APP/login?rd="* ]] && green OK "$h -> 302 to sign in" || red FAIL "$h -> $loc"
done
c=$(status -H 'Accept: application/json' "https://metrics.$DOM/-/healthy")
[ "$c" = "401" ] && green OK "a program without credentials -> 401" || red FAIL "metrics for a program -> $c"

loc=$(curl -s -o /dev/null -w '%{http_code} %{redirect_url}' "${RES[@]}" "https://admin.$DOM/people?x=1")
[[ "$loc" == "302 $APP:443/admin/people" || "$loc" == "302 $APP/admin/people" ]] && green OK "the old admin address redirects into the app" || red FAIL "admin. -> $loc"

echo
echo "5. Same services with the admin's session"
for h in metrics alerts; do
  c=$(status -b "$JAR" "https://$h.$DOM/-/healthy")
  [ "$c" = "200" ] && green OK "$h -> 200" || red FAIL "$h -> $c"
done

# ---------------------------------------------------------------------------
echo
echo "6. The app's own protections"
c=$(status "$APP/api/admin/people")
[ "$c" = "401" ] && green OK "admin API, anonymous -> 401" || red FAIL "admin API anonymous -> $c"
c=$(status -b "$JAR" -X POST -H 'Content-Type: application/json' -d '{}' "$APP/api/auth/logout")
[ "$c" = "400" ] && green OK "a state change without X-Requested-With -> 400 (CSRF)" || red FAIL "CSRF guard -> $c"
hdrs=$(curl -s -D - -o /dev/null "${RES[@]}" "$APP/" | tr -d '\r')
for h in "content-security-policy: default-src 'self'" "strict-transport-security: max-age" "x-frame-options: DENY" "x-content-type-options: nosniff"; do
  printf '%s\n' "$hdrs" | grep -qi "^$h" && green OK "${h%%:*}" || red FAIL "missing ${h%%:*}"
done
cookie=$(grep -E 'llm_session' "$JAR" | head -1)
[[ "$cookie" == "#HttpOnly_"* ]] && green OK "the session cookie is HttpOnly" || red FAIL "session cookie not HttpOnly"
[[ "$(printf '%s' "$cookie" | awk '{print $4}')" == "TRUE" ]] && green OK "the session cookie is Secure" || red FAIL "session cookie not Secure"

echo
if [ "$FAILS" -eq 0 ]; then echo "  all checks passed"; else echo "  $FAILS check(s) FAILED"; fi
exit "$FAILS"
