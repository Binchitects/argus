#!/usr/bin/env bash
# Add the stack's hostnames to /etc/hosts.
#
# Browsers resolve any *.localhost name to 127.0.0.1 by specification, but
# curl, Python and most SDKs do NOT. With the proxy-only setup there are no
# direct ports to fall back on, so command-line tools need real entries.
#
# On a real server with a real domain and DNS records you do not need this at
# all - it exists for the *.localhost development case.
#
#   sudo ./scripts/setup-hosts.sh
#   sudo ./scripts/setup-hosts.sh --remove
set -uo pipefail
ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$ROOT"

REMOVE=0
[[ "${1:-}" == "--remove" ]] && REMOVE=1

HOSTS=/etc/hosts
MARKER='# --- LLMService ---'
END_MARKER='# --- end LLMService ---'

DOMAIN="$(grep -E '^LLM_DOMAIN=' .env 2>/dev/null | head -n1 | cut -d= -f2- | tr -d '[:space:]')"
DOMAIN="${DOMAIN:-llm.localhost}"

SUDO=""
[[ $EUID -ne 0 ]] && SUDO="sudo"

NAMES=("$DOMAIN")
for n in auth chat api grafana metrics alerts gateway traces logs cadvisor node gpu s3 argus; do
  NAMES+=("$n.$DOMAIN")
done

# Always strip the old block first, so this is idempotent.
tmp="$(mktemp)"
$SUDO sed "/$(printf '%s' "$MARKER" | sed 's/[]\/$*.^[]/\\&/g')/,/$(printf '%s' "$END_MARKER" | sed 's/[]\/$*.^[]/\\&/g')/d" "$HOSTS" > "$tmp" 2>/dev/null \
  || cp "$HOSTS" "$tmp"

if [[ $REMOVE -eq 0 ]]; then
  {
    echo ""
    echo "$MARKER"
    for n in "${NAMES[@]}"; do printf '127.0.0.1\t%s\n' "$n"; done
    echo "$END_MARKER"
  } >> "$tmp"
fi

$SUDO cp "$tmp" "$HOSTS"
rm -f "$tmp"

if [[ $REMOVE -eq 1 ]]; then
  echo "  Removed LLMService entries from $HOSTS"
else
  echo "  Added ${#NAMES[@]} entries to $HOSTS"
  for n in "${NAMES[@]}"; do echo "    127.0.0.1  $n"; done
  echo
  echo "  curl and SDK clients can now reach the stack by hostname."
fi

# Flush whatever resolver cache this distro runs, if any.
if command -v systemd-resolve >/dev/null 2>&1; then
  $SUDO systemd-resolve --flush-caches 2>/dev/null && echo "  resolver cache flushed"
elif command -v resolvectl >/dev/null 2>&1; then
  $SUDO resolvectl flush-caches 2>/dev/null && echo "  resolver cache flushed"
fi
