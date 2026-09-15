#!/usr/bin/env bash
# Fill every secret an airgap bundle emptied with a fresh random value.
#
#   cp llm-stack/.env.airgap llm-stack/.env
#   ./fill-secrets.sh
#
# Run ON THE TARGET. Needs no network: `openssl rand` is the only tool used.
#
# ---------------------------------------------------------------------------
# Why this exists instead of the awk one-liner in the README
# ---------------------------------------------------------------------------
#
# The README's awk command fills every empty value BETWEEN the "# SECRETS" and
# "# PEOPLE" markers. That was the whole story while .env had one secrets
# section. It no longer is: eight real secrets live past the PEOPLE marker --
# ARGUS_GITLAB_TOKEN, the ClickHouse and MinIO credentials, the three Langfuse
# keys, VLLM_API_KEY -- because they belong to optional profiles and were
# documented next to those profiles.
#
# So the bundle empties secrets by NAME as well as by position, and this script
# fills exactly what was emptied, from the list the bundler wrote. Re-running
# it is safe: a value that is already set is left alone, so it cannot invalidate
# a deployment that is already running.
# ---------------------------------------------------------------------------
set -euo pipefail

BUNDLE_DIR="$(cd "$(dirname "$0")" && pwd)"
cd "$BUNDLE_DIR"

LIST=".airgap/secrets-to-fill.txt"
ENV_FILE="llm-stack/.env"

say()  { printf '%s\n' "$*"; }
warn() { printf '\033[33mwarning: %s\033[0m\n' "$*" >&2; }
die()  { printf '\033[31merror: %s\033[0m\n' "$*" >&2; exit 1; }

[ -f "$ENV_FILE" ] || die "$ENV_FILE does not exist. Run:  cp llm-stack/.env.airgap llm-stack/.env"
[ -f "$LIST" ] || die "no $LIST in this bundle; it was not built by airgap-bundle.sh"
command -v openssl >/dev/null 2>&1 || die "openssl is not on PATH; it is the only tool this needs"

# Secrets that must come from OUTSIDE this deployment. Generating a random
# value for these would produce a plausible-looking string that nothing
# accepts, and the failure would present as a permissions or auth problem
# rather than as "you skipped a step".
is_external() {
  case "$1" in
    ARGUS_GITLAB_TOKEN|ARGUS_GITLAB_PASSWORD|HF_TOKEN) return 0 ;;
    *) return 1 ;;
  esac
}

# Bytes of entropy. The default is 24 (48 hex characters), which is what the
# README's one-liner used. These two are longer because the service that reads
# them validates the LENGTH before anything else, and a shorter key is rejected
# with a message about the key rather than about its length.
bytes_for() {
  case "$1" in
    LANGFUSE_ENCRYPTION_KEY)          echo 32 ;;
    AUTHELIA_STORAGE_ENCRYPTION_KEY)  echo 32 ;;
    *)                                echo 24 ;;
  esac
}

current_value() {
  sed -n "s/^$1=//p" "$ENV_FILE" | head -1
}

generated=0
kept=0
external=()

while IFS= read -r name; do
  [ -n "$name" ] || continue

  if is_external "$name"; then
    external+=("$name")
    continue
  fi

  existing="$(current_value "$name")"
  if [ -n "$existing" ]; then
    kept=$(( kept + 1 ))
    continue
  fi

  value="$(openssl rand -hex "$(bytes_for "$name")")"
  # LiteLLM rejects a key that does not start with the prefix it was configured
  # for. This mirrors what the README's one-liner did for the same names.
  case "$name" in LITELLM_*) value="sk-$value" ;; esac

  # -E and a name-anchored pattern: the name is a plain [A-Z0-9_] string from
  # the list, and the replacement is hex, so neither can inject a delimiter.
  sed -i -E "s|^${name}=.*|${name}=${value}|" "$ENV_FILE"
  generated=$(( generated + 1 ))
done < "$LIST"

say ""
say "  generated $generated secret(s); $kept were already set and left alone"

if [ "${#external[@]}" -gt 0 ]; then
  say ""
  say "  These cannot be generated and are still empty. Fill them in by hand:"
  for name in "${external[@]}"; do
    case "$name" in
      ARGUS_GITLAB_TOKEN)
        say "    $name — a GitLab token with read_api + read_repository that can see"
        say "      every project you want indexed. See 'GitLab on a private CA' in"
        say "      the top-level README if your GitLab is not on a public CA." ;;
      HF_TOKEN)
        say "    $name — only needed to index gated Hugging Face repositories" ;;
      *)
        say "    $name" ;;
    esac
  done
fi

say ""
say "  Next:  ./load.sh --up"
