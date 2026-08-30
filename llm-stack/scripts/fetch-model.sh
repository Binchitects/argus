#!/usr/bin/env bash
# Download a HuggingFace model into ./models so vLLM can serve it from disk.
#
# Uses the official `hf` CLI inside a throwaway container - nothing to install
# on the host.
#
# Why not `git clone`? A repo clone pulls EVERY weight format the authors
# published (.safetensors AND .bin, often plus GGUF and ONNX). For a 7B model
# that is frequently 40+ GB to obtain the 15 GB you need, and it restarts from
# zero if the connection drops. `hf download` works per file: it skips files
# already complete and resumes partial ones.
#
#   ./scripts/fetch-model.sh Qwen/Qwen2.5-7B-Instruct
#   ./scripts/fetch-model.sh Qwen/Qwen2.5-14B-Instruct-AWQ --workers 2
#   ./scripts/fetch-model.sh meta-llama/Llama-3.1-8B-Instruct --token hf_xxx
set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$ROOT"

REPO=""
NAME=""
TOKEN=""
WORKERS=4
INCLUDE_BIN=0

while [[ $# -gt 0 ]]; do
  case "$1" in
    --name)    NAME="$2"; shift 2 ;;
    --token)   TOKEN="$2"; shift 2 ;;
    --workers) WORKERS="$2"; shift 2 ;;
    --include-bin) INCLUDE_BIN=1; shift ;;
    -*) echo "unknown option: $1" >&2; exit 1 ;;
    *) REPO="$1"; shift ;;
  esac
done

[[ -n "$REPO" ]] || { echo "usage: $0 <org/model> [--name DIR] [--workers N] [--token TOK] [--include-bin]" >&2; exit 1; }
[[ -n "$NAME" ]] || NAME="${REPO##*/}"

# Fall back to HF_TOKEN from .env for gated repos.
if [[ -z "$TOKEN" && -f .env ]]; then
  TOKEN="$(grep -E '^HF_TOKEN=' .env | head -n1 | cut -d= -f2- | tr -d '[:space:]' || true)"
fi

TARGET="$ROOT/models/$NAME"
mkdir -p "$TARGET"

# Only the files vLLM actually loads; excluding duplicate formats is where most
# of the bandwidth saving comes from.
INCLUDE=(--include '*.safetensors' --include '*.json' --include '*.txt' --include '*.model' --include '*.py')
EXCLUDE=(--exclude 'original/*' --exclude 'onnx/*' --exclude 'openvino/*' --exclude 'coreml/*'
         --exclude '*.gguf' --exclude '*.pth' --exclude '*.msgpack' --exclude '*.h5')
if [[ $INCLUDE_BIN -eq 1 ]]; then
  INCLUDE+=(--include '*.bin')
else
  EXCLUDE+=(--exclude '*.bin')
fi

echo
echo "  repo    $REPO"
echo "  target  models/$NAME"
echo
echo "  Re-run this command if it drops; completed files are skipped and"
echo "  partial ones resume."
echo

# HF_HUB_ENABLE_HF_TRANSFER is deliberately NOT set: faster on a healthy link,
# but far less forgiving of resets.
docker run --rm \
  -v "$TARGET:/out" \
  -e HF_HUB_DISABLE_TELEMETRY=1 \
  -e "HF_TOKEN=${TOKEN}" \
  python:3.12-slim \
  bash -lc "set -e
    pip install --quiet --no-cache-dir 'huggingface_hub[cli]' 2>/dev/null
    hf download '$REPO' --local-dir /out --max-workers $WORKERS ${INCLUDE[*]} ${EXCLUDE[*]}"

# vLLM needs config.json at the root of the directory it is pointed at.
if [[ ! -f "$TARGET/config.json" ]]; then
  echo
  echo "  WARNING: no config.json in models/$NAME - vLLM cannot load this path." >&2
  exit 1
fi

size=$(du -sh "$TARGET" | cut -f1)
echo
echo "  Done. $size in models/$NAME"
echo
echo "  Serve it with:"
echo "      ./scripts/switch-model.sh /models/$NAME    # or edit VLLM_MODEL in .env"
echo
