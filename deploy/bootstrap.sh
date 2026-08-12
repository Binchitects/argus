#!/usr/bin/env bash
# Bring up an Argus deployment from nothing, and prove it works.
#
#   cp .env.example .env && $EDITOR .env
#   ./deploy/bootstrap.sh
#
# Idempotent: safe to re-run after fixing whatever it complained about. It
# stops at the first failure with a message that names the fix, rather than
# continuing and leaving a half-deployment that looks up.
set -euo pipefail

cd "$(dirname "$0")/.."

step() { printf '\n\033[1m==> %s\033[0m\n' "$1"; }
fail() { printf '\033[31mFAILED:\033[0m %s\n' "$1" >&2; exit 1; }

# ------------------------------------------------------------- preflight ----
step "Preflight"

command -v docker >/dev/null 2>&1 || fail "docker not on PATH."
docker compose version >/dev/null 2>&1 \
  || fail "docker compose v2 not available. Install the Compose plugin."
docker info >/dev/null 2>&1 \
  || fail "the Docker daemon is not running. Start Docker and re-run."

[ -f .env ] || fail "no .env. Run: cp .env.example .env  then edit it."

# shellcheck disable=SC1091
set -a; . ./.env; set +a

[ -n "${ARGUS_GITLAB_TOKEN:-}" ] \
  || fail "ARGUS_GITLAB_TOKEN is empty in .env. Argus cannot mirror anything without it."
[ -n "${ARGUS_GITLAB_URL:-}" ] || fail "ARGUS_GITLAB_URL is empty in .env."

echo "  docker      $(docker --version | cut -d' ' -f3 | tr -d ,)"
echo "  gitlab      ${ARGUS_GITLAB_URL}"
echo "  image tag   ${ARGUS_VERSION:-0.1.0-rc1}"

# The token is privileged enough that a typo is worth catching here rather
# than as a confusing empty index an hour later.
step "Verifying the GitLab service token"
code=$(curl -s -o /dev/null -w '%{http_code}' --max-time 20 \
  -H "PRIVATE-TOKEN: ${ARGUS_GITLAB_TOKEN}" \
  "${ARGUS_GITLAB_URL%/}/api/v4/user" || echo 000)
case "$code" in
  200) echo "  token accepted by GitLab" ;;
  401|403) fail "GitLab rejected the service token (HTTP $code). Check ARGUS_GITLAB_TOKEN." ;;
  000) fail "could not reach ${ARGUS_GITLAB_URL} at all. Check the URL, DNS and firewall." ;;
  *) fail "unexpected HTTP $code from GitLab. Check ARGUS_GITLAB_URL." ;;
esac

# ----------------------------------------------------------------- build ----
step "Building the image"
docker compose build || fail "image build failed."

step "Starting server and TLS proxy"
docker compose up -d || fail "docker compose up failed."

step "Waiting for the server to report healthy"
for i in $(seq 1 60); do
  if docker compose exec -T server curl -sf "http://127.0.0.1:${ARGUS_PORT:-7700}/healthz" >/dev/null 2>&1; then
    echo "  healthy after ${i}s"; break
  fi
  [ "$i" = 60 ] && fail "server did not become healthy in 60s. See: docker compose logs server"
  sleep 1
done

# ----------------------------------------------------------------- index ----
step "Indexing (first run; this is the long one)"
echo "  Batch job, not a service. Re-run any time to pick up new commits."
docker compose run --rm indexer index --config /etc/argus/config.yaml \
  || fail "indexing failed. See the output above."

step "Building the dependency graph"
docker compose run --rm indexer resolve --config /etc/argus/config.yaml || true

# ---------------------------------------------------------------- verify ----
step "Deployment acceptance"
echo "  Point this at a DEVELOPER token, not the service token:"
echo
echo "    python deploy/smoke_test.py \\"
echo "        --url https://${ARGUS_PUBLIC_HOST:-localhost}/mcp --token <developer-PAT>"
echo
echo "  It checks health, that a bad token is refused, the MCP handshake,"
echo "  that every tool is registered, and that the packs actually answer."

step "Done"
cat <<EOF
  Server:   https://${ARGUS_PUBLIC_HOST:-localhost}/mcp
  Logs:     docker compose logs -f server
  Re-index: docker compose run --rm indexer index --config /etc/argus/config.yaml
  Revoke:   docker compose exec server argus flush-acl \\
                --config /etc/argus/config.yaml --user <gitlab-username>

  Knowledge packs are NOT built by this script -- they are hours of CPU
  embedding and most teams should download a prebuilt pack instead. See
  docs/knowledge-packs.md.
EOF
