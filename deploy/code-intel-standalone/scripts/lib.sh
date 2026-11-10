# Shared by the scripts here: run from anywhere, read .env without executing it.
set -euo pipefail
STACK_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$STACK_DIR"
[ -f .env ] || { echo "no .env in $STACK_DIR: cp env-samples/<one>.env .env first" >&2; exit 2; }

# The value of KEY in .env (last one wins), or $2 when it is unset or empty.
envget() {
  local v
  v=$(grep -E "^$1=" .env | tail -1 | cut -d= -f2- || true)
  printf '%s' "${v:-${2:-}}"
}

ok()   { printf '  \033[32mPASS\033[0m %s\n' "$*"; }
bad()  { printf '  \033[31mFAIL\033[0m %s\n' "$*"; FAILED=$((FAILED + 1)); }
FAILED=0
