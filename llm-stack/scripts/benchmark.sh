#!/usr/bin/env bash
# Concurrency sweep against the local endpoint, executed inside a throwaway
# python container attached to llm-net (no host Python required).
#
#   ./scripts/benchmark.sh
#   ./scripts/benchmark.sh --concurrency 1,2,4,8,16,32 --requests 32
set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$ROOT"
get() { grep -E "^$1=" .env | head -n1 | cut -d= -f2- | tr -d '[:space:]'; }

CONC="1,4,8,16"
REQS=16
MAXTOK=256
GATEWAY=0
while [[ $# -gt 0 ]]; do
  case "$1" in
    --concurrency) CONC="$2"; shift 2 ;;
    --requests)    REQS="$2"; shift 2 ;;
    --max-tokens)  MAXTOK="$2"; shift 2 ;;
    --gateway)     GATEWAY=1; shift ;;
    *) echo "unknown option: $1" >&2; exit 1 ;;
  esac
done

if [ "$GATEWAY" -eq 1 ]; then
  BASE="http://litellm:4000"
  KEY="$(get LITELLM_MASTER_KEY)"
  MODEL="local"
else
  BASE="http://vllm:8000"
  KEY="$(get VLLM_API_KEY)"
  MODEL="$(get VLLM_SERVED_MODEL_NAME)"
fi
MODEL="${MODEL:-default}"

echo
echo "  Running benchmark inside a container on llm-net..."
echo "  Watch it live on the LLM Overview dashboard."

docker run --rm --network llm-net -v "$ROOT/scripts:/work:ro" python:3.12-slim \
  python /work/benchmark.py \
    --base-url "$BASE" --api-key "$KEY" --model "$MODEL" \
    --concurrency "$CONC" --requests "$REQS" --max-tokens "$MAXTOK"
