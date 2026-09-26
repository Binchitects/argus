#!/usr/bin/env bash
# Claude Code -> Argus. Two forms: HTTP to the shared server with your code index
# key (Settings & keys in the app), or stdio running a local argus for one person
# with a GitLab token.
set -euo pipefail

if [ "${1:-http}" = "stdio" ]; then
  PAT="${ARGUS_TOKEN:?export ARGUS_TOKEN=<your-gitlab-token> first}"
  claude mcp add argus --env "ARGUS_TOKEN=$PAT" -- argus serve --config "${ARGUS_CONFIG:-/etc/argus/config.yaml}" --stdio
else
  URL="${ARGUS_URL:?export ARGUS_URL=http://<host>:8080/mcp first}"
  KEY="${ARGUS_KEY:?export ARGUS_KEY=ak_... (Settings & keys in the app) first}"
  claude mcp add --transport http argus "$URL" --header "Authorization: Bearer $KEY"
fi
echo "Added. Check with:  claude mcp list"
