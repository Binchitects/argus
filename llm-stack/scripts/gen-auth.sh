#!/usr/bin/env bash
# Generate every secret Authelia needs, plus the user database and the OIDC
# client registrations for Grafana, Open WebUI and Langfuse.
#
# Idempotent: existing values are kept unless --force is passed.
#
#   ./scripts/gen-auth.sh
#   ./scripts/gen-auth.sh --force
#   ./scripts/gen-auth.sh --add-user alice --password 's3cret' --groups admins
#   ./scripts/gen-auth.sh --sync-dry-run   # show the plan, change nothing
#   ./scripts/gen-auth.sh --sync           # make users.yml match team.yml
set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$ROOT"
IMG="authelia/authelia:4.39"
DIR="config/authelia"
SECRETS="$DIR/secrets"
FORCE=0
ADD_USER=""
SYNC=0
SYNC_APPLY=0
ADD_PASS=""
ADD_GROUPS="admins"

while [[ $# -gt 0 ]]; do
  case "$1" in
    --force) FORCE=1; shift ;;
    --add-user) ADD_USER="$2"; shift 2 ;;
    --sync) SYNC=1; SYNC_APPLY=1; shift ;;
    --sync-dry-run) SYNC=1; SYNC_APPLY=0; shift ;;
    --password) ADD_PASS="$2"; shift 2 ;;
    --groups) ADD_GROUPS="$2"; shift 2 ;;
    *) echo "unknown option: $1" >&2; exit 1 ;;
  esac
done

mkdir -p "$SECRETS"

au() { MSYS_NO_PATHCONV=1 docker run --rm "$IMG" authelia "$@"; }
rand() { au crypto rand --length "${1:-64}" --charset alphanumeric | sed 's/^Random Value: //' | tr -d '\r\n'; }
arg2() { MSYS_NO_PATHCONV=1 docker run --rm "$IMG" authelia crypto hash generate argon2 --password "$1" | sed 's/^Digest: //' | tr -d '\r\n'; }
pbk()  { MSYS_NO_PATHCONV=1 docker run --rm "$IMG" authelia crypto hash generate pbkdf2 --variant sha512 --password "$1" | sed 's/^Digest: //' | tr -d '\r\n'; }

set_env() {  # key value
  local k="$1" v="$2"
  if grep -qE "^$k=" .env; then
    python - "$k" "$v" <<'PY'
import sys, re, pathlib
k, v = sys.argv[1], sys.argv[2]
p = pathlib.Path('.env'); t = p.read_text(encoding='utf-8')
t = re.sub(r'^%s=.*$' % re.escape(k), '%s=%s' % (k, v), t, count=1, flags=re.M)
p.write_text(t, encoding='utf-8')
PY
  else
    printf '%s=%s\n' "$k" "$v" >> .env
  fi
}
get_env() { grep -E "^$1=" .env 2>/dev/null | head -n1 | cut -d= -f2- | tr -d '\r'; }

# --- add a user and exit ----------------------------------------------------
# ---------------------------------------------------------------------------
# Declarative sync: make users.yml match config/authelia/team.yml.
# Runs in a container so PyYAML and argon2-cffi are available without touching
# the host, and so the argon2 parameters are identical everywhere.
# ---------------------------------------------------------------------------
if [[ $SYNC -eq 1 ]]; then
  TEAM="$DIR/team.yml"
  [[ -f "$TEAM" ]] || { echo "missing $TEAM" >&2; exit 1; }
  [[ -f "$DIR/users.yml" ]] || { echo "run ./scripts/gen-auth.sh once first" >&2; exit 1; }

  echo "==> Reconciling users.yml against team.yml"
  APPLY_FLAG=""
  [[ $SYNC_APPLY -eq 1 ]] && APPLY_FLAG="--apply"

  MSYS_NO_PATHCONV=1 docker run --rm     -v "$ROOT/config/authelia:/auth"     -v "$ROOT/scripts/sync-users.py:/sync-users.py:ro"     python:3.12-slim bash -lc "
      pip install --quiet --no-cache-dir pyyaml argon2-cffi >/dev/null 2>&1
      python /sync-users.py /auth/team.yml /auth/users.yml $APPLY_FLAG
    "
  rc=$?
  exit $rc
fi

if [[ -n "$ADD_USER" ]]; then
  [[ -n "$ADD_PASS" ]] || { echo "--password is required with --add-user" >&2; exit 1; }
  [[ -f "$DIR/users.yml" ]] || { echo "run without --add-user first" >&2; exit 1; }
  echo "==> Hashing password for $ADD_USER"
  H="$(arg2 "$ADD_PASS")"
  python - "$ADD_USER" "$H" "$ADD_GROUPS" "$DIR/users.yml" <<'PY'
import sys, pathlib
user, h, groups, path = sys.argv[1:5]
p = pathlib.Path(path); t = p.read_text(encoding='utf-8').rstrip('\n')
if f'\n  {user}:' in t:
    print(f'  {user} already exists; remove it from users.yml first'); raise SystemExit(1)
g = '\n'.join(f'      - {x}' for x in groups.split(','))
t += f"""
  {user}:
    disabled: false
    displayname: '{user}'
    password: '{h}'
    email: '{user}@llm.localhost'
    groups:
{g}
"""
p.write_text(t + '\n', encoding='utf-8')
print(f'  added {user} (groups: {groups})')
PY
  echo "  Authelia watches users.yml; the change is live within a minute."
  exit 0
fi

echo
echo "  Authelia setup"
echo "  --------------"

# --- core secrets -----------------------------------------------------------
echo "==> Secrets"
for k in AUTHELIA_SESSION_SECRET AUTHELIA_STORAGE_ENCRYPTION_KEY AUTHELIA_JWT_SECRET AUTHELIA_OIDC_HMAC_SECRET; do
  cur="$(get_env "$k" || true)"
  if [[ $FORCE -eq 1 || -z "$cur" || "$cur" == *change-me* ]]; then
    set_env "$k" "$(rand 64)"
    echo "    generated $k"
  else
    echo "    kept      $k"
  fi
done

# --- OIDC signing key -------------------------------------------------------
if [[ $FORCE -eq 1 || ! -f "$SECRETS/oidc.pem" ]]; then
  echo "==> OIDC signing key"
  rm -f "$SECRETS/oidc.pem" "$SECRETS/oidc.pub.pem"
  MSYS_NO_PATHCONV=1 docker run --rm -v "$ROOT/$SECRETS:/keys" "$IMG" \
    authelia crypto pair rsa generate --bits 4096 --directory /keys \
      --file.private-key oidc.pem --file.public-key oidc.pub.pem >/dev/null
  echo "    $SECRETS/oidc.pem"
else
  echo "==> OIDC signing key already exists"
fi

# --- admin user -------------------------------------------------------------
if [[ $FORCE -eq 1 || ! -f "$DIR/users.yml" ]]; then
  echo "==> Admin user"
  ADMIN_PASS="$(rand 20)"
  set_env AUTHELIA_ADMIN_PASSWORD "$ADMIN_PASS"
  H="$(arg2 "$ADMIN_PASS")"
  cat > "$DIR/users.yml" <<EOF
# Authelia user database. Passwords are argon2id hashes - never plaintext.
# Add users with:  ./scripts/gen-auth.sh --add-user NAME --password 'PASS'
users:
  admin:
    disabled: false
    displayname: 'Administrator'
    password: '$H'
    email: 'admin@llm.localhost'
    groups:
      - admins
EOF
  echo "    admin / $ADMIN_PASS   (also stored as AUTHELIA_ADMIN_PASSWORD in .env)"
else
  echo "==> users.yml already exists"
fi

# --- OIDC clients -----------------------------------------------------------
# clients.yml is ALWAYS rebuilt, but the secrets inside it are only minted when
# they do not already exist in .env. Regenerating the file must never invalidate
# the secrets already baked into running containers - that silently breaks every
# login until the apps are recreated.
keep_or_new() {  # env_key -> echoes the secret to use
  local k="$1" cur
  cur="$(get_env "$k" || true)"
  if [[ $FORCE -eq 1 || -z "$cur" || "$cur" == *change-me* ]]; then
    cur="$(rand 48)"; set_env "$k" "$cur"
  fi
  printf '%s' "$cur"
}

if true; then
  echo "==> OIDC clients"
  G_SEC="$(keep_or_new GRAFANA_OIDC_CLIENT_SECRET)"
  O_SEC="$(keep_or_new OPENWEBUI_OIDC_CLIENT_SECRET)"
  L_SEC="$(keep_or_new LANGFUSE_OIDC_CLIENT_SECRET)"
  A_SEC="$(keep_or_new API_OIDC_CLIENT_SECRET)"
  G_HASH="$(pbk "$G_SEC")"; O_HASH="$(pbk "$O_SEC")"; L_HASH="$(pbk "$L_SEC")"; A_HASH="$(pbk "$A_SEC")"

  cat > "$DIR/clients.yml" <<EOF
# GENERATED by scripts/gen-auth - do not edit by hand.
#
# Client secrets are stored as PBKDF2 hashes; the plaintext each app needs is
# in .env (GRAFANA_OIDC_CLIENT_SECRET, etc). Authelia merges this file with
# configuration.yml at startup.
identity_providers:
  oidc:
    # The OIDC issuer signing key. Authelia 4.39 has no environment-variable
    # override for this, so the PEM is embedded here rather than referenced by
    # path - which is why this file is generated and gitignored.
    jwks:
      - key_id: 'main'
        algorithm: 'RS256'
        use: 'sig'
        key: |
__JWKS_PEM__
    clients:
      - client_id: 'grafana'
        client_name: 'Grafana'
        client_secret: '$G_HASH'
        public: false
        authorization_policy: 'one_factor'
        claims_policy: 'with_profile'
        # First-party apps we own: a consent screen offers the user no real
        # choice and just adds a click on every first login.
        consent_mode: 'implicit'
        require_pkce: false
        redirect_uris:
          - 'https://grafana.llm.localhost/login/generic_oauth'
        scopes: ['openid', 'profile', 'groups', 'email']
        userinfo_signed_response_alg: 'none'
        token_endpoint_auth_method: 'client_secret_basic'

      - client_id: 'open-webui'
        client_name: 'Open WebUI'
        client_secret: '$O_HASH'
        public: false
        authorization_policy: 'one_factor'
        claims_policy: 'with_profile'
        # First-party apps we own: a consent screen offers the user no real
        # choice and just adds a click on every first login.
        consent_mode: 'implicit'
        require_pkce: false
        redirect_uris:
          - 'https://chat.llm.localhost/oauth/oidc/callback'
        scopes: ['openid', 'profile', 'groups', 'email']
        userinfo_signed_response_alg: 'none'
        # Open WebUI uses authlib, which sends credentials as HTTP Basic auth.
        # Registering this as client_secret_post makes Authelia reject the token
        # exchange with invalid_client AFTER the user has already logged in.
        token_endpoint_auth_method: 'client_secret_basic'

      # Machine-to-machine. No redirect_uris and no user: clients exchange
      # their secret directly for a short-lived access token. The special
      # authelia.bearer.authz scope is what makes that token usable at the
      # forward-auth endpoint.
      #
      # No backticks around that scope name: this heredoc is unquoted, so a
      # backticked word is run as a command. That is what printed
      # "authelia.bearer.authz: command not found" on every run -- harmless
      # to the output, and alarming enough to look like a broken generator.
      - client_id: 'api'
        client_name: 'LLM API (machine clients)'
        client_secret: '$A_HASH'
        public: false
        authorization_policy: 'one_factor'
        require_pkce: false
        redirect_uris: []
        grant_types: ['client_credentials']
        scopes: ['authelia.bearer.authz']
        audience:
          - 'https://api.llm.localhost'
          - 'https://gateway.llm.localhost'
        token_endpoint_auth_method: 'client_secret_basic'

      - client_id: 'langfuse'
        client_name: 'Langfuse'
        client_secret: '$L_HASH'
        public: false
        authorization_policy: 'one_factor'
        claims_policy: 'with_profile'
        # First-party apps we own: a consent screen offers the user no real
        # choice and just adds a click on every first login.
        consent_mode: 'implicit'
        require_pkce: false
        redirect_uris:
          - 'https://traces.llm.localhost/api/auth/callback/custom'
        scopes: ['openid', 'profile', 'email']
        userinfo_signed_response_alg: 'none'
        token_endpoint_auth_method: 'client_secret_basic'
EOF
  python - "$DIR/clients.yml" "$SECRETS/oidc.pem" <<'PYEOF'
import sys, pathlib
clients, pem = pathlib.Path(sys.argv[1]), pathlib.Path(sys.argv[2])
key = pem.read_text(encoding='utf-8').strip()
# YAML block scalar: every line indented past the `key: |` parent.
indented = chr(10).join('          ' + ln for ln in key.splitlines())
t = clients.read_text(encoding='utf-8').replace('__JWKS_PEM__', indented)
clients.write_text(t, encoding='utf-8')
print('    signing key embedded')
PYEOF
  echo "    grafana, open-webui, langfuse, api registered"
fi

echo
echo "  Done. Start with:  docker compose up -d authelia"
echo
