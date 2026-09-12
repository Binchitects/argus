#!/usr/bin/env bash
# Tail logs from one service or the whole stack.
#
#   ./scripts/logs.sh                  # everything, following
#   ./scripts/logs.sh vllm             # just the inference engine
#   ./scripts/logs.sh vllm --tail 200
#   ./scripts/logs.sh --no-follow
set -uo pipefail
ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$ROOT"

SERVICE=""
TAIL=100
FOLLOW=1
while [[ $# -gt 0 ]]; do
  case "$1" in
    --tail) TAIL="$2"; shift 2 ;;
    --no-follow) FOLLOW=0; shift ;;
    -*) echo "unknown option: $1" >&2; exit 1 ;;
    *) SERVICE="$1"; shift ;;
  esac
done

args=(compose logs --tail "$TAIL")
if [[ $FOLLOW -eq 1 ]]; then
  args+=(--follow)
fi
if [[ -n "$SERVICE" ]]; then
  args+=("$SERVICE")
fi

exec docker "${args[@]}"
