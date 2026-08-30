#!/usr/bin/env bash
# Stop the stack.
#
# By default containers are removed but named volumes are kept, so the model
# cache, metrics history and chat history all survive.
#
#   ./scripts/down.sh
#   ./scripts/down.sh --purge                 # delete ALL volumes
#   ./scripts/down.sh --purge --keep-models   # delete data, keep model weights
set -uo pipefail
ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$ROOT"

PURGE=0
KEEP_MODELS=0
while [[ $# -gt 0 ]]; do
  case "$1" in
    --purge) PURGE=1; shift ;;
    --keep-models) KEEP_MODELS=1; shift ;;
    *) echo "unknown option: $1" >&2; exit 1 ;;
  esac
done

PROJECT="llmservice"
if [[ -f .env ]]; then
  v="$(grep -E '^COMPOSE_PROJECT_NAME=' .env | head -n1 | cut -d= -f2- | tr -d '[:space:]')"
  [[ -n "$v" ]] && PROJECT="$v"
fi

# --profile "*" reaches containers from profiles that are not currently
# enabled; without it, disabling a profile orphans its containers.
if [[ $PURGE -eq 0 ]]; then
  docker compose --profile "*" down --remove-orphans
  echo
  echo "Volumes preserved. Restart with: ./scripts/up.sh"
  exit 0
fi

echo "This deletes volumes: metrics, dashboards, chat history, databases."
if [[ $KEEP_MODELS -eq 1 ]]; then
  echo "Model caches will be KEPT."
fi
read -r -p "Type PURGE to confirm: " answer
if [[ "$answer" != "PURGE" ]]; then
  echo "Aborted."
  exit 1
fi

if [[ $KEEP_MODELS -eq 1 ]]; then
  docker compose --profile "*" down --remove-orphans
  for vol in $(docker volume ls -q --filter "name=${PROJECT}_"); do
    if [[ "$vol" == "${PROJECT}_hf-cache" || "$vol" == "${PROJECT}_vllm-cache" ]]; then
      echo "  keeping  $vol"
    else
      docker volume rm "$vol" >/dev/null && echo "  removed  $vol"
    fi
  done
else
  docker compose --profile "*" down --volumes --remove-orphans
fi
