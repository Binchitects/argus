#!/usr/bin/env bash
# End-to-end verification: model listing, auth enforcement, a completion, a
# streaming completion, and confirmation that Prometheus saw the traffic.
#
#   ./scripts/smoke-test.sh
#   ./scripts/smoke-test.sh --gateway
set -uo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$ROOT"
command -v jq >/dev/null 2>&1 || { echo "jq is required (use smoke-test.ps1 on Windows)"; exit 1; }

get() { grep -E "^$1=" .env | head -n1 | cut -d= -f2- | tr -d '[:space:]'; }

if [ "${1:-}" = "--gateway" ]; then
  BASE="http://localhost:$(get LITELLM_PORT)"
  KEY="$(get LITELLM_MASTER_KEY)"
  MODEL="local"
else
  BASE="http://localhost:$(get VLLM_PORT)"
  KEY="$(get VLLM_API_KEY)"
  MODEL="$(get VLLM_SERVED_MODEL_NAME)"
fi
MODEL="${MODEL:-default}"

c_green=$'\033[32m'; c_red=$'\033[31m'; c_cyan=$'\033[36m'; c_dim=$'\033[90m'; c_off=$'\033[0m'
pass=0
fail=0
step() { printf '%s==> %s%s\n' "$c_cyan" "$1" "$c_off"; }
ok()   { printf '    %sPASS%s\n' "$c_green" "$c_off"; pass=$((pass + 1)); }
bad()  { printf '    %sFAIL  %s%s\n' "$c_red" "$1" "$c_off"; fail=$((fail + 1)); }

echo
echo "  Target: $BASE  (model: $MODEL)"
echo

step "GET /v1/models"
models="$(curl -sf -m 15 -H "Authorization: Bearer $KEY" "$BASE/v1/models" || true)"
if [ -n "$models" ] && printf '%s' "$models" | jq -e '.data | length > 0' >/dev/null 2>&1; then
  printf '%s' "$models" | jq -r '.data[].id' | sed 's/^/    - /'
  ok
else
  bad "no models returned"
fi

step "Rejects unauthenticated requests"
code="$(curl -s -o /dev/null -w '%{http_code}' -m 15 "$BASE/v1/models")"
if [ "$code" = "401" ] || [ "$code" = "403" ]; then
  printf '    %sHTTP %s as expected%s\n' "$c_dim" "$code" "$c_off"
  ok
else
  bad "expected 401/403 without a key, got $code"
fi

step "POST /v1/chat/completions"
body="$(jq -nc --arg m "$MODEL" '{model:$m,messages:[{role:"user",content:"In one sentence, what is a KV cache in LLM inference?"}],max_tokens:120,temperature:0.2}')"
start="$(date +%s)"
resp="$(curl -sf -m 180 -H "Authorization: Bearer $KEY" -H 'Content-Type: application/json' -d "$body" "$BASE/v1/chat/completions" || true)"
end="$(date +%s)"
if [ -n "$resp" ] && printf '%s' "$resp" | jq -e '.choices[0].message.content | length > 0' >/dev/null 2>&1; then
  ptok="$(printf '%s' "$resp" | jq -r '.usage.prompt_tokens')"
  ctok="$(printf '%s' "$resp" | jq -r '.usage.completion_tokens')"
  secs=$((end - start))
  [ "$secs" -lt 1 ] && secs=1
  printf '    %slatency      ~%ss%s\n' "$c_dim" "$secs" "$c_off"
  printf '    %sprompt tok   %s%s\n' "$c_dim" "$ptok" "$c_off"
  printf '    %soutput tok   %s%s\n' "$c_dim" "$ctok" "$c_off"
  printf '    %sthroughput   ~%s tok/s%s\n' "$c_dim" "$((ctok / secs))" "$c_off"
  echo
  printf '%s' "$resp" | jq -r '.choices[0].message.content' | sed 's/^/    /'
  echo
  ok
else
  bad "empty or failed completion"
fi

step "Streaming (SSE) completion"
sbody="$(jq -nc --arg m "$MODEL" '{model:$m,messages:[{role:"user",content:"Count from 1 to 5."}],max_tokens:40,stream:true}')"
chunks="$(curl -sN -m 120 -H "Authorization: Bearer $KEY" -H 'Content-Type: application/json' -d "$sbody" "$BASE/v1/chat/completions" 2>/dev/null | grep -c '^data: ' || true)"
chunks="${chunks:-0}"
if [ "$chunks" -ge 2 ]; then
  printf '    %schunks       %s%s\n' "$c_dim" "$chunks" "$c_off"
  ok
else
  bad "expected multiple SSE chunks, got $chunks"
fi

step "Prometheus recorded the requests"
q="$(curl -sf -m 15 --get --data-urlencode 'query=sum(vllm:request_success_total)' "http://localhost:$(get PROMETHEUS_PORT)/api/v1/query" || true)"
if [ -n "$q" ] && printf '%s' "$q" | jq -e '.data.result | length > 0' >/dev/null 2>&1; then
  printf '    %stotal successful requests: %s%s\n' "$c_dim" "$(printf '%s' "$q" | jq -r '.data.result[0].value[1]')" "$c_off"
  ok
else
  bad "no vllm:request_success_total series yet (retry ~15s after the first request)"
fi

echo
if [ "$fail" -eq 0 ]; then
  printf '  %s%s passed, %s failed%s\n' "$c_green" "$pass" "$fail" "$c_off"
else
  printf '  %s%s passed, %s failed%s\n' "$c_red" "$pass" "$fail" "$c_off"
fi
echo
exit "$fail"
