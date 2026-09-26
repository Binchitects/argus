#!/usr/bin/env bash
# Apply the .env changes saved in the app's Settings page.
#
# The app never touches .env or Docker itself (no Docker socket in any web
# container). It writes the changes it cannot apply live to
# config/app/pending.env; this script, run by you on the host:
#   1. accepts only settings the app is allowed to change (the StackEnv__ names
#      compose passes to the app) and re-checks every value,
#   2. shows each change (never a secret's value) and asks,
#   3. keeps a copy of .env, writes the changes, removes the pending file,
#   4. runs `docker compose up -d`, which recreates only what changed.
#
#   ./scripts/apply-settings.sh              # show, ask, apply
#   ./scripts/apply-settings.sh --dry-run    # show only
#   ./scripts/apply-settings.sh --yes        # no question (automation)
#   ./scripts/apply-settings.sh --no-restart # write .env, leave the services
set -euo pipefail

cd "$(dirname "$0")/.."
STACK="$PWD"
YES=0 DRY=0 RESTART=1
for arg in "$@"; do
  case "$arg" in
    --yes|-y) YES=1 ;;
    --dry-run) DRY=1 ;;
    --no-restart) RESTART=0 ;;
    -h|--help) sed -n '2,19p' "$0" | sed 's/^# \{0,1\}//'; exit 0 ;;
    *) echo "apply-settings: unknown option $arg" >&2; exit 2 ;;
  esac
done

bold=$'\e[1m' dim=$'\e[2m' red=$'\e[31m' green=$'\e[32m' off=$'\e[0m'
[[ -t 1 ]] || { bold='' dim='' red='' green='' off=''; }
die() { printf '%sapply-settings: %s%s\n' "$red" "$*" "$off" >&2; exit 1; }

[[ -f .env ]] || die "no .env in $STACK"
# A name missing from .env is empty, not an error (pipefail would end the script).
env_get() { { grep -E "^$1=" .env 2>/dev/null || true; } | tail -n1 | cut -d= -f2- | sed -e 's/^"//' -e 's/"$//'; }

CONFIG_DIR="$(env_get LLM_CONFIG_DIR)"; CONFIG_DIR="${CONFIG_DIR:-./config}"
PENDING="$CONFIG_DIR/app/pending.env"
if [[ ! -s "$PENDING" ]]; then
  echo "Nothing to apply: no settings are waiting ($PENDING does not exist)."
  exit 0
fi
[[ -r "$PENDING" ]] || die "cannot read $PENDING (it belongs to $(stat -c %U "$PENDING")); run as that user, or with sudo"

# What the app may change: exactly the names compose hands it as StackEnv__NAME
# (secrets as StackEnv__NAME_SET). Anything else in the file is refused.
mapfile -t ALLOWED < <(grep -oE 'StackEnv__[A-Z0-9_]+' docker-compose.yml | sed -E 's/^StackEnv__//; s/_SET$//' | sort -u)
mapfile -t SECRETS < <(grep -oE 'StackEnv__[A-Z0-9_]+_SET' docker-compose.yml | sed -E 's/^StackEnv__//; s/_SET$//' | sort -u)
allowed() { local k; for k in "${ALLOWED[@]}"; do [[ "$k" == "$1" ]] && return 0; done; return 1; }
secret() { local k; for k in "${SECRETS[@]}"; do [[ "$k" == "$1" ]] && return 0; done; return 1; }

declare -a KEYS=() VALUES=()
while IFS= read -r line || [[ -n "$line" ]]; do
  [[ -z "$line" || "$line" == \#* ]] && continue
  [[ "$line" == *=* ]] || die "not KEY=value: $line"
  key="${line%%=*}" value="${line#*=}"
  [[ "$key" =~ ^[A-Z][A-Z0-9_]*$ ]] || die "not a setting name: $key"
  allowed "$key" || die "$key is not a setting the app may change; nothing was applied"
  # The app refuses these characters too; checked again because this file is
  # written by a web service and read into .env.
  if [[ "$value" == *[\"\'\`\$\\#]* ]]; then
    die "$key has a character .env would misread (quote, \$, #, backslash); nothing was applied"
  fi
  KEYS+=("$key") VALUES+=("$value")
done < "$PENDING"
((${#KEYS[@]})) || { echo "Nothing to apply."; exit 0; }

echo "${bold}Settings saved in the app, to write into .env:${off}"
for i in "${!KEYS[@]}"; do
  k="${KEYS[$i]}" v="${VALUES[$i]}" old="$(env_get "${KEYS[$i]}")"
  if secret "$k"; then
    printf '  %-30s %s -> %s\n' "$k" "$([[ -n "$old" ]] && echo '(set)' || echo '(not set)')" "(a new secret value)"
  else
    printf '  %-30s %s%s%s -> %s%s%s\n' "$k" "$dim" "${old:-(empty)}" "$off" "$green" "${v:-(empty)}" "$off"
  fi
done

if ((DRY)); then echo "(dry run: nothing changed)"; exit 0; fi
if ((!YES)); then
  read -r -p "Apply these ${#KEYS[@]} change(s)$( ((RESTART)) && echo ' and recreate what changed' )? [y/N] " answer
  [[ "$answer" =~ ^[Yy] ]] || { echo "Nothing changed. The changes stay pending."; exit 1; }
fi

backup=".env.bak-$(date +%Y%m%d-%H%M%S)"
cp -p .env "$backup"
chmod 600 "$backup"

tmp="$(mktemp .env.apply.XXXXXX)"
chmod 600 "$tmp"
cp .env "$tmp"
for i in "${!KEYS[@]}"; do
  k="${KEYS[$i]}" v="${VALUES[$i]}"
  if grep -qE "^$k=" "$tmp"; then
    # awk, not sed: the value is data, never part of a pattern.
    K="$k" V="$v" awk 'BEGIN{FS=OFS="="} { if (index($0, ENVIRON["K"] "=") == 1) print ENVIRON["K"] "=" ENVIRON["V"]; else print }' "$tmp" > "$tmp.new"
    mv "$tmp.new" "$tmp"
  else
    printf '%s=%s\n' "$k" "$v" >> "$tmp"
  fi
done
chmod --reference=.env "$tmp" 2>/dev/null || chmod 600 "$tmp"
mv "$tmp" .env
rm -f "$PENDING"
echo "${green}.env updated${off} (previous version: $backup)."

if ((RESTART)); then
  echo "Recreating what changed: docker compose up -d"
  docker compose up -d
  echo "${green}Done.${off} The Settings page shows the new values once the app is back."
else
  echo "Not restarted. Run 'docker compose up -d' to apply."
fi
