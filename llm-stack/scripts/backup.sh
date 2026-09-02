#!/usr/bin/env bash
# Back up (or restore) the stateful Docker volumes.
#
# The model cache is excluded by default: it is large and re-downloadable. What
# is NOT re-creatable is chat history, dashboards you customised, metrics
# history, and the gateway/tracing databases.
#
#   ./scripts/backup.sh
#   ./scripts/backup.sh --include-model-cache
#   ./scripts/backup.sh --restore --from backups/2026-08-21_2312
set -uo pipefail
ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$ROOT"

OUT=""
RESTORE=0
FROM=""
INCLUDE_MODELS=0
while [[ $# -gt 0 ]]; do
  case "$1" in
    --out) OUT="$2"; shift 2 ;;
    --restore) RESTORE=1; shift ;;
    --from) FROM="$2"; shift 2 ;;
    --include-model-cache) INCLUDE_MODELS=1; shift ;;
    *) echo "unknown option: $1" >&2; exit 1 ;;
  esac
done

PROJECT="llmservice"
if [[ -f .env ]]; then
  v="$(grep -E '^COMPOSE_PROJECT_NAME=' .env | head -n1 | cut -d= -f2- | tr -d '[:space:]')"
  [[ -n "$v" ]] && PROJECT="$v"
fi

VOLUMES=(open-webui-data grafana-data prometheus-data alertmanager-data
         postgres-data redis-data loki-data clickhouse-data minio-data authelia-data)
if [[ $INCLUDE_MODELS -eq 1 ]]; then
  VOLUMES+=(hf-cache)
fi

# --------------------------------------------------------------------------
if [[ $RESTORE -eq 1 ]]; then
  if [[ -z "$FROM" || ! -d "$FROM" ]]; then
    echo "pass --from <backup directory>" >&2
    exit 1
  fi
  echo "Restoring OVERWRITES current volume contents."
  echo "Stop the stack first: ./scripts/down.sh"
  read -r -p "Type RESTORE to continue: " answer
  if [[ "$answer" != "RESTORE" ]]; then
    echo "Aborted."
    exit 1
  fi
  FROM_ABS="$(cd "$FROM" && pwd)"
  for vol in "${VOLUMES[@]}"; do
    if [[ ! -f "$FROM_ABS/$vol.tar.gz" ]]; then
      echo "  skip      $vol (no archive)"
      continue
    fi
    docker volume create "${PROJECT}_$vol" >/dev/null
    docker run --rm \
      -v "${PROJECT}_$vol:/target" \
      -v "$FROM_ABS:/backup:ro" \
      alpine sh -c "find /target -mindepth 1 -delete 2>/dev/null; tar xzf /backup/$vol.tar.gz -C /target"
    echo "  restored  $vol"
  done
  echo
  echo "Done. Start with: ./scripts/up.sh"
  exit 0
fi

# --------------------------------------------------------------------------
if [[ -z "$OUT" ]]; then
  OUT="backups/$(date +%Y-%m-%d_%H%M%S)"
fi
mkdir -p "$OUT"
OUT_ABS="$(cd "$OUT" && pwd)"

echo
echo "  Backing up to $OUT_ABS"
echo
for vol in "${VOLUMES[@]}"; do
  if ! docker volume inspect "${PROJECT}_$vol" >/dev/null 2>&1; then
    printf '  %-22s skip (volume does not exist)\n' "$vol"
    continue
  fi
  # A throwaway alpine container is the portable way to read a named volume;
  # its real path on disk is inside the VM on Docker Desktop hosts.
  docker run --rm \
    -v "${PROJECT}_$vol:/source:ro" \
    -v "$OUT_ABS:/backup" \
    alpine tar czf "/backup/$vol.tar.gz" -C /source . 2>/dev/null
  if [[ -f "$OUT_ABS/$vol.tar.gz" ]]; then
    printf '  %-22s %s\n' "$vol" "$(du -h "$OUT_ABS/$vol.tar.gz" | cut -f1)"
  else
    printf '  %-22s FAILED\n' "$vol"
  fi
done

# Config and .env are the other half of a restore.
cp -f .env "$OUT_ABS/" 2>/dev/null
cp -rf config "$OUT_ABS/" 2>/dev/null

echo
echo "  Backup complete: $OUT_ABS"
echo "  NOTE: .env contains secrets - store this directory securely."
echo
