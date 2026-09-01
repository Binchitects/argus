#!/usr/bin/env bash
# Provision gateway identities, API keys and quotas from team.yml.
#
# A wrapper so nobody has to export LITELLM_MASTER_KEY by hand -- a key typed
# at a prompt lands in shell history, and this one is the credential that can
# mint every other credential.
#
#   ./scripts/llm-users.sh                     # show the plan
#   ./scripts/llm-users.sh --apply             # create/update
#   ./scripts/llm-users.sh --rotate a@b.c --apply
set -euo pipefail
cd "$(dirname "$0")/.."

[ -f .env ] || { echo "no .env here -- copy .env.example first" >&2; exit 1; }
# Only the two variables this needs, rather than sourcing the whole file:
# .env holds every secret in the stack and most of them have no business in
# this process's environment.
LITELLM_MASTER_KEY="$(grep -E '^LITELLM_MASTER_KEY=' .env | cut -d= -f2-)"
LITELLM_PORT="$(grep -E '^LITELLM_PORT=' .env | cut -d= -f2- || echo 4000)"
export LITELLM_MASTER_KEY
export LITELLM_URL="${LITELLM_URL:-http://localhost:${LITELLM_PORT:-4000}}"

exec python3 scripts/sync-llm-users.py config/authelia/team.yml "$@"
