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
get_env_or() {  # key default
  local v; v="$(grep -E "^$1=" .env 2>/dev/null | head -n1 | cut -d= -f2- | tr -d "")"
  printf "%s" "${v:-$2}"
}

LITELLM_MASTER_KEY="$(grep -E '^LITELLM_MASTER_KEY=' .env | cut -d= -f2-)"
LITELLM_PORT="$(grep -E '^LITELLM_PORT=' .env | cut -d= -f2- || echo 4000)"
LITELLM_DEFAULT_USER_BUDGET="$(grep -E '^LITELLM_DEFAULT_USER_BUDGET=' .env | cut -d= -f2-)"
LITELLM_BUDGET_DURATION="$(grep -E '^LITELLM_BUDGET_DURATION=' .env | cut -d= -f2-)"
export LITELLM_DEFAULT_USER_BUDGET LITELLM_BUDGET_DURATION
export LITELLM_MASTER_KEY
# LiteLLM's port 4000 is EXPOSED to the compose network but never PUBLISHED to
# the host -- it is reached through Traefik. The old default of
# http://localhost:4000 therefore could not work on a stock deployment: it
# failed with "cannot reach the gateway", and provisioning users is the very
# first thing anyone does after setup.
#
# Derive the public URL from .env instead, and trust the stack's own CA so the
# self-signed wildcard verifies. Override with LITELLM_URL for odd topologies.
if [[ -z "${LITELLM_URL:-}" ]]; then
  _dom="$(get_env_or LLM_DOMAIN llm.localhost)"
  _port="$(get_env_or TRAEFIK_HTTPS_PORT 443)"
  if [[ "$_port" == "443" ]]; then
    LITELLM_URL="https://gateway.$_dom"
  else
    LITELLM_URL="https://gateway.$_dom:$_port"
  fi
  _ca="config/traefik/certs/ca.crt"
  if [[ -f "$_ca" ]]; then
    # urllib (sync-llm-users.py) reads SSL_CERT_FILE; requests reads the other.
    export SSL_CERT_FILE="$PWD/$_ca" REQUESTS_CA_BUNDLE="$PWD/$_ca"
  fi
fi
export LITELLM_URL

# PYTHON overrides the interpreter. `python3` on Windows often resolves to
# the Microsoft Store shim, which is a different install from the one with
# PyYAML on it -- and the resulting "needs PyYAML" is confusing when you can
# import yaml perfectly well in the shell you just typed it in.
PY="${PYTHON:-python3}"
"$PY" -c "import yaml" 2>/dev/null || {
  echo "$PY cannot import PyYAML." >&2
  echo "  pip install pyyaml, or point PYTHON at an interpreter that has it:" >&2
  echo "  PYTHON=/c/Python313/python.exe $0 $*" >&2
  exit 1
}
exec "$PY" scripts/sync-llm-users.py config/authelia/team.yml "$@"
