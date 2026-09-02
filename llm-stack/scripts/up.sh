#!/usr/bin/env bash
# Start the stack and wait until vLLM is actually serving.
#
#   ./scripts/up.sh
#   ./scripts/up.sh --profiles "smi,logging,tracing"
#   ./scripts/up.sh --no-wait
set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$ROOT"

WAIT=1
TIMEOUT_MIN=30
while [[ $# -gt 0 ]]; do
  case "$1" in
    --profiles) export COMPOSE_PROFILES="$2"; shift 2 ;;
    --no-wait)  WAIT=0; shift ;;
    --timeout)  TIMEOUT_MIN="$2"; shift 2 ;;
    *) echo "unknown option: $1" >&2; exit 1 ;;
  esac
done

[[ -f .env ]] || { echo "No .env found, running bootstrap..."; ./scripts/bootstrap.sh; }

get() { grep -E "^$1=" .env | head -n1 | cut -d= -f2- | tr -d '[:space:]'; }
port() { local v; v="$(get "$1")"; echo "${v:-$2}"; }

echo "==> Pulling images"
docker compose pull --quiet

echo "==> Starting services"
docker compose up -d --remove-orphans

if [[ $WAIT -eq 1 ]]; then
  echo
  echo "==> Waiting for vLLM to finish loading the model"
  echo "    (first run downloads weights; this is the slow part)"
  deadline=$(( $(date +%s) + TIMEOUT_MIN * 60 ))
  # No published port any more - everything goes through Traefik, and the API
  # route needs auth. Docker's own healthcheck is the authoritative signal and
  # needs no credentials.
  ready=0
  while [[ $(date +%s) -lt $deadline ]]; do
    if [[ "$(docker inspect vllm --format '{{.State.Health.Status}}' 2>/dev/null)" == "healthy" ]]; then ready=1; break; fi
    # Fail fast rather than waiting out the full timeout on a crashed engine.
    if [[ "$(docker inspect -f '{{.State.Status}}' vllm 2>/dev/null || echo missing)" == "exited" ]]; then
      echo
      echo "vLLM exited. Last 40 log lines:" >&2
      docker logs --tail 40 vllm >&2
      exit 1
    fi
    printf '\r    waiting... '
    sleep 5
  done
  printf '\r                    \r'
  if [[ $ready -eq 1 ]]; then
    echo "    vLLM is serving."
  else
    echo "    Timed out after ${TIMEOUT_MIN}m. Check: docker logs -f vllm"
  fi
fi

profiles="$(get COMPOSE_PROFILES)"
dom="$(get LLM_DOMAIN)"; dom="${dom:-llm.localhost}"
echo
echo "  Endpoints (all via Traefik on :443 - no direct ports)"
echo "  ---------------------------------------------------"
echo "  Chat UI       https://chat.$dom"
echo "  Grafana       https://grafana.$dom"
echo "  Prometheus    https://metrics.$dom"
echo "  Alertmanager  https://alerts.$dom"
echo "  vLLM API      https://api.$dom/v1"
case "$profiles" in *gateway*) echo "  Gateway       https://gateway.$dom/v1";; esac
case "$profiles" in *auth*)    echo "  Login portal  https://auth.$dom";; esac
case "$profiles" in *tracing*) echo "  Langfuse      https://traces.$dom";; esac
echo
echo "  Verify with:  ./scripts/smoke-test.sh"
echo
