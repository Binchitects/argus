#!/usr/bin/env bash
# Claude Code -> code-intel.  NOT EXECUTED on this machine (claude is not installed);
# transcribed from Anthropic's documented `claude mcp add` interface. Treat it
# as a starting point and verify against your own version.
#
# Two forms, and the choice is the deployment shape, not features: HTTP talks
# to the platform's code-intel with your Arena API key (Your account -> API key,
# sk-...; code-intel answers as your GitLab account), stdio runs code-intel locally for
# one person with your own GitLab token.
set -euo pipefail

CONFIG="${CODE_INTEL_CONFIG:-/etc/code-intel/config.yaml}"
URL="${CODE_INTEL_URL:-https://code-intel.llm.localhost/mcp}"

if [ "${1:-http}" = "stdio" ]; then
  PAT="${CODE_INTEL_TOKEN:?export CODE_INTEL_TOKEN=<your-gitlab-pat> first (a local code-intel reads GitLab as you)}"
  # code-intel resolves the credential BEFORE serving, so a bad token fails on
  # stderr where you are configuring the client -- not as a tool error inside
  # a transcript, which is where a missing credential is hardest to recognise.
  claude mcp add code-intel \
    --env "CODE_INTEL_TOKEN=$PAT" \
    -- code-intel serve --config "$CONFIG" --stdio
else
  KEY="${LLM_SERVICE_API_KEY:?export LLM_SERVICE_API_KEY=<your API key, sk-...> first}"
  # --allowed-host must name the hostname your reverse proxy forwards, or the
  # DNS-rebinding guard rejects every /mcp call with 421 while /healthz is 200.
  claude mcp add --transport http code-intel "$URL" \
    --header "Authorization: Bearer $KEY"
fi

echo "Added. Check with:  claude mcp list"
