#!/usr/bin/env bash
# Start Ollama with the settings this stack's context window depends on.
#
# Two of these are NOT optional and are NOT recoverable from the gateway
# config, because they are server-level environment rather than per-request
# parameters:
#
#   OLLAMA_FLASH_ATTENTION=1   required before a quantized KV cache is honoured
#   OLLAMA_KV_CACHE_TYPE=q8_0  halves KV memory, ~lossless
#
# Why it matters: measured on a 24 GB card with the 27B Q4_K_M GGUF,
#
#     f16  KV -> 65,536 tokens fits, 131,072 spills 27% to CPU
#     q8_0 KV -> 114,688 tokens fits fully (22.8 GB)
#     q4_0 KV -> 131,072 tokens fits fully (21.5 GB), but 4-bit KV
#     nothing fits the model's full 262,144 window
#
# Start Ollama WITHOUT these and the gateway still asks for num_ctx=114688.
# It does not fail -- it spills most of the model to system RAM and crawls.
# That is a silent regression, which is the reason this script exists rather
# than a line in the README.
set -euo pipefail

export OLLAMA_FLASH_ATTENTION="${OLLAMA_FLASH_ATTENTION:-1}"
export OLLAMA_KV_CACHE_TYPE="${OLLAMA_KV_CACHE_TYPE:-q8_0}"
export OLLAMA_CONTEXT_LENGTH="${OLLAMA_CONTEXT_LENGTH:-114688}"

# Ollama and vLLM both claim the whole GPU; only one can run.
if docker compose ps --status running --services 2>/dev/null | grep -qx vllm; then
    echo "vllm is running and holds the GPU; stop it first:" >&2
    echo "    docker compose stop vllm" >&2
    exit 1
fi

echo "flash_attention=$OLLAMA_FLASH_ATTENTION kv_cache=$OLLAMA_KV_CACHE_TYPE" \
     "context=$OLLAMA_CONTEXT_LENGTH"
exec ollama serve
