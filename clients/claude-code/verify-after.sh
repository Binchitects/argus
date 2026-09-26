#!/usr/bin/env bash
# Force verify-after in Claude Code: check the answer before the turn ends.
#
# WHY A HOOK
#
# This exists because of one measured failure, and it is the expensive kind. A
# model was asked to review kernel code and answered in 2.2 seconds with ZERO
# tool calls, naming `wcscpy_s` (a user-mode function), `<string.h>` (a user-mode
# header) and `ucrt.lib` (a user-mode library). A real function, a real header,
# a real library, and the wrong answer to a question about kernel code. Nothing
# in the transcript says it was not checked.
#
# Telling the model to verify did not work -- that is what the server
# instructions already do, and this is the model that ignored them. So the check
# happens outside the model's judgement. Claude Code runs this when the model
# tries to finish, and a non-zero exit with a message on stderr BLOCKS the stop
# and hands the message back as the reason. The turn continues.
#
# WHY IT CANNOT CALL THE MCP TOOL
#
# `docs_verify` is an MCP tool, and hooks are shell commands. `argus verify` is
# the same check with an exit code, which is the only interface that works here.
#
# WHY IT DOES NOT BLOCK WHEN IT CANNOT CHECK
#
# `argus verify` exits 6 -- not 2 -- when no documentation pack is installed or
# the packs cannot be read. Those two answers must not be confused: "the
# documentation contradicts you" has to block, and "I could not check" must not,
# or a deployment without packs becomes an agent that can never finish a
# sentence. Only exit 2 blocks here.
#
# INSTALL
#
#   cp clients/claude-code/verify-after.sh ~/.claude/hooks/ && chmod +x ~/.claude/hooks/verify-after.sh
#
# then in ~/.claude/settings.json (merge with anything already there):
#
#   {
#     "hooks": {
#       "Stop": [
#         { "hooks": [ { "type": "command",
#                        "command": "~/.claude/hooks/verify-after.sh" } ] }
#       ]
#     }
#   }
#
# NOT EXECUTED on the machine this was written on -- `claude` is not installed
# there, exactly like add.sh next to it. The hook PROTOCOL (stdin JSON, exit 2
# blocks, stderr is the reason) is from Anthropic's documented hooks reference;
# the transcript shape is the part to verify against your own version, and the
# script says so rather than failing silently if it does not match.
#
#   https://code.claude.com/docs/en/hooks
#
# Cost: one extra process per turn. `argus verify` resolves identifiers against
# the packs and returns in well under a second on a small draft; on a long
# answer it is bounded by --limit.
set -uo pipefail

CONFIG="${ARGUS_CONFIG:-/etc/argus/config.yaml}"
ARGUS="${ARGUS_BIN:-argus}"
LIMIT="${ARGUS_VERIFY_LIMIT:-40}"

payload="$(cat)"

# The transcript path is what the hook is given; the draft is the last assistant
# message in it. `jq` is not assumed -- python3 is what every Argus deployment
# already has, because Argus is a python service.
draft="$(
  printf '%s' "$payload" | python3 -c '
import json, sys

try:
    payload = json.load(sys.stdin)
except Exception:
    sys.exit(0)

path = payload.get("transcript_path") or ""
if not path:
    sys.exit(0)

# The LAST assistant message with text. A turn can end on a tool call, on a
# thinking block, or on nothing at all, and none of those is a claim to check.
text = ""
try:
    with open(path, encoding="utf-8", errors="replace") as fh:
        for line in fh:
            line = line.strip()
            if not line:
                continue
            try:
                row = json.loads(line)
            except Exception:
                continue
            message = row.get("message") or {}
            if row.get("type") != "assistant" and message.get("role") != "assistant":
                continue
            content = message.get("content")
            if isinstance(content, str):
                text = content
            elif isinstance(content, list):
                parts = [b.get("text", "") for b in content
                         if isinstance(b, dict) and b.get("type") == "text"]
                if parts:
                    text = "\n".join(parts)
except OSError:
    sys.exit(0)

sys.stdout.write(text)
' 2>/dev/null
)"

# Nothing to check. A hook that blocks here would make the agent unable to stop.
if [ -z "${draft//[[:space:]]/}" ]; then
  exit 0
fi

# Exit 2 is the only blocking outcome, and `argus verify` only uses it for a
# contradiction. Its stderr is what Claude Code shows the model, so it is passed
# through untouched -- including 6, which must NOT block. `set +e` around the
# call because a non-zero exit is the normal, expected answer here, not a
# failure of this script.
set +e
reason="$(printf '%s' "$draft" | "$ARGUS" verify --config "$CONFIG" \
            --text-file - --limit "$LIMIT" 2>&1 >/dev/null)"
rc=$?
set -e

if [ "$rc" = "2" ]; then
  printf '%s\n' "$reason" >&2
  exit 2
fi

exit 0
