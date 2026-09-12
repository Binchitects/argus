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

# --------------------------------------------------------- credential gate ----
# Refuse to start an engine whose API key is still the placeholder from
# .env.example. This is the fail-closed check, deliberately here rather than as
# a `${VAR:?}` in docker-compose.yml, for two reasons:
#
#   1. Compose interpolates EVERY service before filtering by profile, so a
#      required-variable marker in one engine breaks `docker compose` for
#      people running the other one -- and for every .env written before that
#      engine existed.
#   2. A `${VAR:?}` only checks that the variable EXISTS. .env.example ships
#      `change-me-please`, so it is satisfied by precisely the value we are
#      trying to reject. Checking the value is what actually closes the hole.
#
# scripts/bootstrap.sh generates a real key for both engines, so reaching this
# means .env was hand-assembled and bootstrap never ran.
engine_key_guard() {  # profile_name  env_var
  case ",$(get COMPOSE_PROFILES)," in *",$1,"*) ;; *) return 0 ;; esac
  local v; v="$(get "$2")"
  if [[ -z "$v" ]]; then
    echo "refusing to start '$1': $2 is empty" >&2
  elif [[ "$v" == *change-me* ]]; then
    echo "refusing to start '$1': $2 is still the placeholder from .env.example" >&2
  else
    return 0
  fi
  echo "  run ./scripts/bootstrap.sh (or ./scripts/setup.sh) to generate one" >&2
  exit 1
}
engine_key_guard vllm     VLLM_API_KEY
engine_key_guard llamacpp LLAMACPP_API_KEY

# PREFLIGHT: refuse to start against a checkout that is not really there.
#
# If this tree lives on a volume that is not mounted, the config files below are
# absent while the directories may still appear to exist -- Docker will have
# created empty stubs on a previous boot. Starting here yields a Traefik with no
# routers and an Authelia with no users, both reporting healthy.
for _need in docker-compose.yml config/traefik/traefik.yml config/traefik/dynamic; do
  if [[ ! -e "$_need" ]]; then
    echo "ERROR: $_need is missing from $(pwd)." >&2
    echo "  This checkout looks unmounted or incomplete. Nothing was started." >&2
    echo "  If it lives on a removable volume, mount it first:" >&2
    echo "      udisksctl mount -b /dev/disk/by-uuid/<uuid>" >&2
    exit 1
  fi
done
# A directory that exists but is empty is the signature of a Docker-created stub.
if [[ -d config/traefik/dynamic ]] && [[ -z "$(ls -A config/traefik/dynamic 2>/dev/null)" ]]; then
  echo "ERROR: config/traefik/dynamic exists but is EMPTY." >&2
  echo "  That is what Docker leaves behind when it bind-mounts a path on an" >&2
  echo "  unmounted volume. Remove the stubs and mount the real volume." >&2
  exit 1
fi

echo "==> Pulling images"
docker compose pull --quiet

echo "==> Starting services"
docker compose up -d --remove-orphans

if [[ $WAIT -eq 1 ]]; then
  echo
  # Whichever engine is enabled -- waiting on a hardcoded `vllm` meant a
  # llama.cpp deploy sat out the entire timeout against a container that was
  # never going to exist, and then reported a timeout rather than a mistake.
  ENGINE_CONTAINER=vllm
  case ",$(get COMPOSE_PROFILES)," in *,llamacpp,*) ENGINE_CONTAINER=llamacpp ;; esac
  echo "==> Waiting for $ENGINE_CONTAINER to finish loading the model"
  echo "    (first run downloads weights; this is the slow part)"
  deadline=$(( $(date +%s) + TIMEOUT_MIN * 60 ))
  # No published port any more - everything goes through Traefik, and the API
  # route needs auth. Docker's own healthcheck is the authoritative signal and
  # needs no credentials.
  ready=0
  while [[ $(date +%s) -lt $deadline ]]; do
    if [[ "$(docker inspect "$ENGINE_CONTAINER" --format '{{.State.Health.Status}}' 2>/dev/null)" == "healthy" ]]; then ready=1; break; fi
    # Fail fast rather than waiting out the full timeout on a crashed engine.
    if [[ "$(docker inspect -f '{{.State.Status}}' "$ENGINE_CONTAINER" 2>/dev/null || echo missing)" == "exited" ]]; then
      echo
      echo "$ENGINE_CONTAINER exited. Last 40 log lines:" >&2
      docker logs --tail 40 "$ENGINE_CONTAINER" >&2
      exit 1
    fi
    printf '\r    waiting... '
    sleep 5
  done
  printf '\r                    \r'
  if [[ $ready -eq 1 ]]; then
    echo "    $ENGINE_CONTAINER is serving."
  else
    echo "    Timed out after ${TIMEOUT_MIN}m. Check: docker logs -f $ENGINE_CONTAINER"
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
