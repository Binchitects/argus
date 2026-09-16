#!/usr/bin/env bash
# Claude Code -> Argus.  NOT EXECUTED on this machine (claude is not installed);
# transcribed from Anthropic's documented `claude mcp add` interface. Treat it
# as a starting point and verify against your own version.
#
# Two forms, and the choice is the deployment shape, not features: stdio runs
# Argus locally for one person, HTTP talks to a shared indexed host with your
# own GitLab token.
set -euo pipefail

PAT="${ARGUS_TOKEN:?export ARGUS_TOKEN=<your-gitlab-pat> first}"
CONFIG="${ARGUS_CONFIG:-/etc/argus/config.yaml}"

if [ "${1:-http}" = "stdio" ]; then
  # Argus resolves the credential BEFORE serving, so a bad token fails on
  # stderr where you are configuring the client -- not as a tool error inside
  # a transcript, which is where a missing credential is hardest to recognise.
  claude mcp add argus \
    --env "ARGUS_TOKEN=$PAT" \
    -- argus serve --config "$CONFIG" --stdio
else
  # --allowed-host must name the hostname your reverse proxy forwards, or the
  # DNS-rebinding guard rejects every /mcp call with 421 while /healthz is 200.
  claude mcp add --transport http argus https://argus.internal/mcp \
    --header "Authorization: Bearer $PAT"
fi

echo "Added. Check with:  claude mcp list"
