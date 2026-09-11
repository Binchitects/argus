#!/usr/bin/env bash
# Full authentication audit: performs a REAL login and OAuth2 authorization-code
# exchange for every OIDC client, then reports the claims actually delivered.
#
# This catches things a config check cannot - missing email claims, wrong
# redirect URIs, client auth method mismatches - without needing a browser.

# `python` is not a command on a python3-only distro (Ubuntu 26.04 ships no
# alias), so a bare call here dies with "command not found".
PY="${PYTHON:-python3}"

set -uo pipefail
ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"; cd "$ROOT"
export MSYS_NO_PATHCONV=1

CA="config/traefik/certs/ca.crt"
DOM="$(grep -E '^LLM_DOMAIN=' .env | cut -d= -f2- | tr -d '[:space:]')"; DOM="${DOM:-llm.localhost}"
USER_NAME="admin"
PASS="$(grep -E '^AUTHELIA_ADMIN_PASSWORD=' .env | cut -d= -f2- | tr -d '[:space:]')"
JAR="$(mktemp)"

RES=(--ssl-no-revoke --cacert "$CA")
for h in auth chat grafana traces api gateway metrics alerts; do RES+=(--resolve "$h.$DOM:443:127.0.0.1"); done

green() { printf '  \033[32m%-10s\033[0m %s\n' "$1" "$2"; }
red()   { printf '  \033[31m%-10s\033[0m %s\n' "$1" "$2"; }

echo
echo "  AUTHENTICATION AUDIT"
echo "  ===================="
echo

# ---------------------------------------------------------------------------
# Step 0 exists because the audit reads secrets from .env, while the APPS use
# whatever was baked in when their container was created. If those drift, the
# audit passes while every real login fails - which is exactly what happened.
echo "0. Container secrets match .env (drift check)"
drift=0
for pair in "grafana:GF_AUTH_GENERIC_OAUTH_CLIENT_SECRET:GRAFANA_OIDC_CLIENT_SECRET"             "open-webui:OAUTH_CLIENT_SECRET:OPENWEBUI_OIDC_CLIENT_SECRET"             "langfuse:AUTH_CUSTOM_CLIENT_SECRET:LANGFUSE_OIDC_CLIENT_SECRET"; do
  c=${pair%%:*}; rest=${pair#*:}; ev=${rest%%:*}; fv=${rest#*:}
  # A container that is not running cannot hold a stale secret - its profile is
  # simply disabled. Reporting that as drift is a false positive.
  if ! docker inspect "$c" >/dev/null 2>&1; then
    printf '  [90m%-10s[0m %s
' "SKIP" "$c (not running)"
    continue
  fi
  inc=$(docker inspect "$c" --format '{{range .Config.Env}}{{println .}}{{end}}' 2>/dev/null | grep "^$ev=" | cut -d= -f2-)
  envv=$(grep -E "^$fv=" .env | cut -d= -f2-)
  if [ "$inc" = "$envv" ]; then green OK "$c"
  else red STALE "$c holds an old secret - run: docker compose up -d --force-recreate $c"; drift=1; fi
done
[ "$drift" = "1" ] && echo "  -> real logins WILL fail until those containers are recreated"
echo

# Whether a domain/client requires a second factor. The audit only ever
# establishes a FIRST factor, so under two_factor the correct expectation
# inverts: a 1FA session must be REFUSED. Without this the audit reports
# hardening as breakage and everyone learns to ignore it.
CONF="config/authelia/configuration.yml"
domain_policy() {  # host -> one_factor|two_factor|bypass
  tr -d '\r' < "$CONF" | awk -v h="$1.$DOM" '
    /^ *- domain:/ {inblk=1; found=0}
    inblk && $0 ~ h {found=1}
    inblk && /policy:/ && found {gsub(/.*policy: .|.$/,""); print; exit}
    /^ *$/ {inblk=0}'
}
client_policy() {  # client_id -> one_factor|two_factor
  "$PY" - "$1" <<'PYEOF'
import re, sys
cid = sys.argv[1]
s = open("config/authelia/clients.yml", encoding="utf-8").read().replace("\r\n", "\n")
blocks = re.split(r"(?m)^(?=      - client_id: )", s)
for b in blocks:
    m = re.search(r"client_id: '([^']+)'", b)
    if m and m.group(1) == cid:
        pm = re.search(r"authorization_policy: '([^']+)'", b)
        print(pm.group(1) if pm else "")
        break
PYEOF
}

echo "1. Login as '$USER_NAME' (first factor)"
LOGIN=$(curl -s "${RES[@]}" -c "$JAR" -X POST \
  -H 'Content-Type: application/json' \
  -d "{\"username\":\"$USER_NAME\",\"password\":\"$PASS\",\"keepMeLoggedIn\":true}" \
  "https://auth.$DOM/api/firstfactor")
if echo "$LOGIN" | grep -q '"status":"OK"'; then green OK "session established"; else red FAIL "$LOGIN"; fi

# ---------------------------------------------------------------------------
echo
echo "2. Authorization-code flow per client (claims actually delivered)"
flow() {
  local id="$1" uri="$2" scope="$3" secret="$4"
  # consent+authorize; Authelia redirects with ?code=... when it accepts
  local loc code
  loc=$(curl -s -o /dev/null -w '%{redirect_url}' "${RES[@]}" -b "$JAR" -c "$JAR" -G \
    --data-urlencode "client_id=$id" --data-urlencode "redirect_uri=$uri" \
    --data-urlencode "response_type=code" --data-urlencode "scope=$scope" \
    --data-urlencode "state=auditauditaudit0123" \
    "https://auth.$DOM/api/oidc/authorization")
  code=$(printf '%s' "$loc" | sed -n 's/.*[?&]code=\([^&]*\).*/\1/p')
  if [ -z "$code" ]; then
    if [ "$(client_policy "$id")" = "two_factor" ]; then
      green OK "$id: no code from a 1FA session -- two_factor enforced"
    else
      red FAIL "$id: no code (redirect was: ${loc:0:90})"
    fi
    return
  fi

  local tok
  tok=$(curl -s "${RES[@]}" -u "$id:$secret" \
    -d "grant_type=authorization_code&code=$code&redirect_uri=$uri" \
    "https://auth.$DOM/api/oidc/token")
  "$PY" - "$id" <<PY
import json,sys,base64
cid=sys.argv[1]
try: d=json.loads('''$tok''')
except Exception: print("  \033[31mFAIL\033[0m       %s: bad token response"%cid); raise SystemExit
if 'id_token' not in d:
    print("  \033[31mFAIL\033[0m       %s: %s"%(cid, json.dumps(d)[:120])); raise SystemExit
p=d['id_token'].split('.')[1]; p+='='*(-len(p)%4)
c=json.loads(base64.urlsafe_b64decode(p))
have=lambda k: c.get(k) not in (None,'',[])
em='email' if have('email') else '-'
print("  \033[32mOK\033[0m         %-11s sub=%s" % (cid, str(c.get('sub'))[:8]))
print("             id_token claims: email=%s name=%s preferred_username=%s groups=%s"
      % (c.get('email','MISSING'), c.get('name','-'), c.get('preferred_username','-'), c.get('groups','-')))
if not have('email'):
    print("  \033[31m           ^ EMAIL MISSING - client will reject login\033[0m")
PY
}
G=$(grep -E '^GRAFANA_OIDC_CLIENT_SECRET=' .env | cut -d= -f2-)
O=$(grep -E '^OPENWEBUI_OIDC_CLIENT_SECRET=' .env | cut -d= -f2-)
L=$(grep -E '^LANGFUSE_OIDC_CLIENT_SECRET=' .env | cut -d= -f2-)
flow grafana    "https://grafana.$DOM/login/generic_oauth"        "openid profile email groups" "$G"
flow open-webui "https://chat.$DOM/oauth/oidc/callback"           "openid profile email groups" "$O"
flow langfuse   "https://traces.$DOM/api/auth/callback/custom"    "openid email profile"        "$L"

# ---------------------------------------------------------------------------
echo
echo "3. Machine client (client_credentials) + API access"
A=$(grep -E '^API_OIDC_CLIENT_SECRET=' .env | cut -d= -f2-)
# `resource`, not `audience`. Authelia matches an `audience` value by EXACT
# string, so a token for 'https://api.<domain>' is refused at
# 'https://api.<domain>/v1/models' -- every real endpoint 401s while the
# token itself introspects as active with the right scope. `resource`
# (RFC 8707) is the parameter with prefix semantics, so one token covers
# the whole origin.
TOK=$(curl -s "${RES[@]}" -u "api:$A" \
  -d "grant_type=client_credentials&scope=authelia.bearer.authz&resource=https://api.$DOM" \
  "https://auth.$DOM/api/oidc/token" | "$PY" -c "import json,sys; print(json.load(sys.stdin).get('access_token',''))" 2>/dev/null)
[ -n "$TOK" ] && green OK "token issued" || red FAIL "no token"
c=$(curl -s -o /dev/null -w '%{http_code}' "${RES[@]}" -H "Authorization: Bearer $TOK" "https://api.$DOM/v1/models")
[ "$c" = "200" ] && green OK "api with token -> 200" || red FAIL "api with token -> $c"
c=$(curl -s -o /dev/null -w '%{http_code}' "${RES[@]}" "https://api.$DOM/v1/models")
[ "$c" != "200" ] && green OK "api without token -> $c (denied)" || red FAIL "api unauthenticated -> 200"

# ---------------------------------------------------------------------------
echo
echo "4. forwardAuth-gated services (anonymous must be denied)"
for h in metrics alerts; do
  c=$(curl -s -o /dev/null -w '%{http_code}' "${RES[@]}" -H 'Accept: text/html' "https://$h.$DOM/-/healthy")
  [ "$c" = "302" ] && green OK "$h -> 302 to portal" || red FAIL "$h -> $c"
done
echo
echo "5. Same services WITH a first-factor session (policy decides expectation)"
for pair in "metrics:/-/healthy" "alerts:/-/healthy"; do
  h=${pair%%:*}; path=${pair#*:}
  c=$(curl -s -o /dev/null -w '%{http_code}' "${RES[@]}" -b "$JAR" "https://$h.$DOM$path")
  if [ "$(domain_policy "$h")" = "two_factor" ]; then
    [ "$c" != "200" ] && green OK "$h -> $c (1FA refused, two_factor enforced)" \
                      || red FAIL "$h -> 200 with only a first factor, but policy is two_factor"
  else
    [ "$c" = "200" ] && green OK "$h -> 200 (session accepted)" || red FAIL "$h -> $c"
  fi
done
rm -f "$JAR"
echo
