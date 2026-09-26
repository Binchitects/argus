#!/usr/bin/env bash
# Force verify-after in Claude Code: check the answer against the documentation
# packs before the turn ends. Claude Code runs this when the model tries to
# finish; exit 2 with a reason on stderr blocks the stop and hands the reason
# back to the model. `argus verify --claude-hook` reads the hook payload itself,
# finds the last answer in the transcript, and exits 2 ONLY when the
# documentation contradicts it -- "could not check" (no packs) never blocks.
#
# Install:
#   cp clients/claude-code/verify-after.sh ~/.claude/hooks/ && chmod +x ~/.claude/hooks/verify-after.sh
# and in ~/.claude/settings.json:
#   { "hooks": { "Stop": [ { "hooks": [ { "type": "command", "command": "~/.claude/hooks/verify-after.sh" } ] } ] } }
#
# Needs a local argus with the packs installed (argus pack install ...).
exec "${ARGUS_BIN:-argus}" verify --config "${ARGUS_CONFIG:-/etc/argus/config.yaml}" --claude-hook
