#!/usr/bin/env bash
# Change the served model and restart only the inference container.
#
# Everything else - metrics, dashboards, chat history - stays up.
#
# Sizing on a 24 GB card, weights only (the KV cache needs whatever is left):
#     7-8B    fp16   ~15 GB   comfortable
#     7-8B    AWQ    ~5  GB   lots of headroom
#     13-14B  AWQ    ~8  GB   comfortable
#     32B     AWQ    ~19 GB   tight; lower --max-model-len
#     70B     AWQ    ~38 GB   needs two GPUs
#
#   ./scripts/switch-model.sh Qwen/Qwen2.5-14B-Instruct-AWQ --extra-args "--quantization awq"
#   ./scripts/switch-model.sh /models/Qwen2.5-7B-Instruct
set -uo pipefail
ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$ROOT"

MODEL=""
SERVED=""
MAXLEN=""
GPUUTIL=""
EXTRA=""
HAVE_EXTRA=0
RESTART=1

while [[ $# -gt 0 ]]; do
  case "$1" in
    --served-name) SERVED="$2"; shift 2 ;;
    --max-model-len) MAXLEN="$2"; shift 2 ;;
    --gpu-memory-utilization) GPUUTIL="$2"; shift 2 ;;
    --extra-args) EXTRA="$2"; HAVE_EXTRA=1; shift 2 ;;
    --no-restart) RESTART=0; shift ;;
    -*) echo "unknown option: $1" >&2; exit 1 ;;
    *) MODEL="$1"; shift ;;
  esac
done

if [[ -z "$MODEL" ]]; then
  echo "usage: $0 <model-id-or-/models/path> [--extra-args '...'] [--max-model-len N]" >&2
  exit 1
fi

set_env() {
  # '|' as the sed delimiter: model ids contain '/', but never '|'.
  if grep -qE "^$1=" .env; then
    sed -i "s|^$1=.*|$1=$2|" .env
  else
    printf '%s=%s\n' "$1" "$2" >> .env
  fi
  printf '  %-28s -> %s\n' "$1" "$2"
}

set_env VLLM_MODEL "$MODEL"
[[ -n "$SERVED" ]]  && set_env VLLM_SERVED_MODEL_NAME "$SERVED"
[[ -n "$MAXLEN" ]]  && set_env VLLM_MAX_MODEL_LEN "$MAXLEN"
[[ -n "$GPUUTIL" ]] && set_env VLLM_GPU_MEMORY_UTILIZATION "$GPUUTIL"
[[ $HAVE_EXTRA -eq 1 ]] && set_env VLLM_EXTRA_ARGS "$EXTRA"

# ---------------------------------------------------------------------------
# The served name is what every client, the Open WebUI picker and every Grafana
# `model_name` label displays, so derive it from the checkpoint rather than
# leaving a meaningless alias. Then keep the gateway in step: LiteLLM does NOT
# expand ${VAR} in its YAML, so the name has to be written out literally.
# ---------------------------------------------------------------------------
if [[ -z "$SERVED" ]]; then
  SERVED="$(basename "${MODEL%/}")"
  set_env VLLM_SERVED_MODEL_NAME "$SERVED"
fi

LITELLM_CFG="config/litellm/config.yaml"
if [[ -f "$LITELLM_CFG" ]]; then
  # Rewrite the first model_name (the real-name entry) and every
  # `model: openai/...` target. The `local` alias keeps its own name.
  sed -i "0,/^  - model_name: .*/s||  - model_name: $SERVED|" "$LITELLM_CFG"
  sed -i "s|^      model: openai/.*|      model: openai/$SERVED|" "$LITELLM_CFG"
  echo "  gateway config              -> $SERVED"
fi

if [[ $RESTART -eq 0 ]]; then
  echo
  echo ".env updated. Apply with:"
  echo "    docker compose up -d --force-recreate vllm litellm"
  exit 0
fi

echo
echo "==> Recreating the inference container"
TARGETS=(vllm)
# The gateway caches the model list at startup, so it needs recreating too.
if grep -qE '^COMPOSE_PROFILES=.*gateway' .env 2>/dev/null; then
  TARGETS+=(litellm)
fi
docker compose up -d --force-recreate "${TARGETS[@]}" || exit 1

echo
echo "Weights for a new model download on first use. Follow progress with:"
echo "    docker logs -f vllm"
echo
echo "Then verify:  ./scripts/smoke-test.sh"
