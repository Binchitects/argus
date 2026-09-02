#!/usr/bin/env bash
# Guided end-to-end setup for the LLMService stack.
#
# Asks for what it cannot safely guess, then runs the existing scripts in the
# order they have to happen. It does NOT reimplement them: bootstrap.sh,
# gen-certs, gen-auth, llm-users and up.sh are each tested on their own, and a
# second copy of that logic here would drift from them silently.
#
#   ./scripts/setup.sh              interactive
#   ./scripts/setup.sh --defaults   accept every default, ask nothing
#   ./scripts/setup.sh --dry-run    print the plan, change nothing
#
# Safe to re-run. Existing secrets are kept, and every question shows the
# current value as its default, so a second pass is a review rather than a
# reset.
set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$ROOT"

# Git Bash rewrites arguments that look like paths, which breaks openssl's
# -subj and mangles container paths. See scripts/gen-certs.sh.
export MSYS_NO_PATHCONV=1 MSYS2_ARG_CONV_EXCL='*'

DEFAULTS=0; DRYRUN=0
for arg in "$@"; do
  case "$arg" in
    --defaults) DEFAULTS=1 ;;
    --dry-run)  DRYRUN=1 ;;
    -h|--help)  sed -n '2,14p' "$0"; exit 0 ;;
    *) echo "unknown option: $arg" >&2; exit 2 ;;
  esac
done

c_cyan=$'\033[36m'; c_green=$'\033[32m'; c_yellow=$'\033[33m'
c_red=$'\033[31m'; c_dim=$'\033[90m'; c_bold=$'\033[1m'; c_off=$'\033[0m'
step()  { printf '\n%s==> %s%s\n' "$c_cyan$c_bold" "$1" "$c_off"; }
ok()    { printf '    %sOK  %s%s\n' "$c_green" "$1" "$c_off"; }
warn()  { printf '    %s!   %s%s\n' "$c_yellow" "$1" "$c_off"; }
note()  { printf '    %s%s%s\n' "$c_dim" "$1" "$c_off"; }
die()   { printf '    %sX   %s%s\n' "$c_red" "$1" "$c_off"; exit 1; }
run()   { if [[ $DRYRUN -eq 1 ]]; then note "would run: $*"; else "$@"; fi; }

ENV_FILE="$ROOT/.env"

# --------------------------------------------------------------- prompting --
# Reads the current value from .env so a re-run defaults to what is already
# configured rather than to the template.
current() { [[ -f "$ENV_FILE" ]] && grep -E "^$1=" "$ENV_FILE" | head -n1 | cut -d= -f2- || true; }

set_env() {
  local key="$1" val="$2"
  [[ $DRYRUN -eq 1 ]] && { note "would set $key=$val"; return; }
  if grep -qE "^$key=" "$ENV_FILE"; then
    # '|' as the delimiter: values contain '/' and ':' but never '|'.
    sed -i "s|^$key=.*|$key=$val|" "$ENV_FILE"
  else
    printf '%s=%s\n' "$key" "$val" >> "$ENV_FILE"
  fi
}

ask() {  # ask VAR "prompt" "default"
  local var="$1" prompt="$2" default="$3" reply
  local existing; existing="$(current "$var")"
  [[ -n "$existing" && "$existing" != *change-me* ]] && default="$existing"
  if [[ $DEFAULTS -eq 1 ]]; then
    printf '    %s: %s%s%s\n' "$prompt" "$c_dim" "$default" "$c_off"
    set_env "$var" "$default"; return
  fi
  read -r -p "    $prompt [$default]: " reply </dev/tty || reply=""
  set_env "$var" "${reply:-$default}"
}

ask_yn() {  # ask_yn "prompt" default(y/n) -> returns 0 for yes
  local prompt="$1" default="$2" reply
  if [[ $DEFAULTS -eq 1 ]]; then
    printf '    %s: %s%s%s\n' "$prompt" "$c_dim" "$default" "$c_off"
    [[ "$default" == y ]]; return
  fi
  read -r -p "    $prompt [${default}]: " reply </dev/tty || reply=""
  [[ "${reply:-$default}" =~ ^[Yy] ]]
}

# ------------------------------------------------------------ prerequisites --
step "Checking prerequisites"
command -v docker >/dev/null 2>&1 || die "docker not found on PATH"
docker info >/dev/null 2>&1 || die "the Docker daemon is not reachable -- start Docker Desktop"
ok "docker $(docker version --format '{{.Server.Version}}' 2>/dev/null)"
docker compose version >/dev/null 2>&1 || die "docker compose v2 is required"
ok "compose $(docker compose version --short 2>/dev/null)"

GPU_NAME=""
if command -v nvidia-smi >/dev/null 2>&1; then
  GPU_NAME="$(nvidia-smi --query-gpu=name --format=csv,noheader 2>/dev/null | head -1)"
  GPU_MB="$(nvidia-smi --query-gpu=memory.total --format=csv,noheader,nounits 2>/dev/null | head -1)"
  [[ -n "$GPU_NAME" ]] && ok "GPU: $GPU_NAME (${GPU_MB} MiB)"
fi
[[ -z "$GPU_NAME" ]] && warn "no NVIDIA GPU detected -- vLLM will not run; you can still use a hosted model"

# ------------------------------------------------------------------- .env ----
step "Base configuration"
if [[ ! -f "$ENV_FILE" ]]; then
  [[ $DRYRUN -eq 1 ]] && note "would create .env from .env.example" || cp "$ROOT/.env.example" "$ENV_FILE"
  ok "created .env from the template"
else
  ok ".env exists -- current values are offered as defaults"
fi

ask LLM_DOMAIN "Domain for the stack (services appear at *.DOMAIN)" "llm.localhost"
DOMAIN="$(current LLM_DOMAIN)"

# ------------------------------------------------------------- the engine ----
step "Inference engine"
note "vLLM serves ONE model at a time and claims most of the GPU up front."
note "A 24 GB card cannot hold an 8B and a 27B together."
ask VLLM_MODEL "HuggingFace model id to serve" "Qwen/Qwen3-8B"
ask VLLM_SERVED_MODEL_NAME "Name clients will use for it" "qwen3-8b"
ask VLLM_MAX_MODEL_LEN "Context length" "8192"
ask VLLM_GPU_MEMORY_UTILIZATION "Fraction of VRAM vLLM may claim" "0.90"

# The V2 runner needs Unified Virtual Addressing, which WSL2's GPU driver does
# not expose: the engine dies with "RuntimeError: UVA is not available" AFTER
# the container has reported healthy once, which reads like a hardware fault
# rather than a setting.
if grep -qiE "microsoft|wsl" /proc/version 2>/dev/null || [[ "$OSTYPE" == msys* || "$OSTYPE" == cygwin* ]]; then
  set_env VLLM_USE_V2_MODEL_RUNNER 0
  ok "WSL2/Windows detected -- forcing vLLM's V1 model runner (V2 needs UVA)"
else
  ask VLLM_USE_V2_MODEL_RUNNER "Use vLLM's faster V2 runner? (1 on bare-metal Linux)" "1"
fi

# A first pull of a multi-GB checkpoint over a slow link spends most of its
# time hitting the 10s default and retrying, which gets slower as it goes.
ask HF_HUB_DOWNLOAD_TIMEOUT "HuggingFace read timeout, seconds" "120"
ask HF_TOKEN "HuggingFace token (only for gated models, blank is fine)" ""

# --------------------------------------------------------------- profiles ----
step "What to run"
PROFILES="gateway"
note "gateway (LiteLLM) is always on: it is what gives each person a key and a budget."

# Each answer defaults to what is ALREADY enabled, so a re-run reviews the
# configuration rather than resetting it. Defaulting these to a fixed y/n
# would mean `--defaults` silently switched off whatever the operator had
# turned on -- the opposite of "safe to re-run".
HAVE="$(current COMPOSE_PROFILES)"
had() { case ",$HAVE," in *",$1,"*) echo y ;; *) echo "$2" ;; esac; }

ask_yn "Reverse proxy + TLS (traefik)?"             "$(had proxy y)"   && PROFILES="$PROFILES,proxy"
ask_yn "Single sign-on (authelia)?"                 "$(had auth y)"    && PROFILES="$PROFILES,auth"
ask_yn "GPU metrics exporter (nvidia-smi)?"         "$(had smi y)"     && PROFILES="$PROFILES,smi"
ask_yn "Code index + documentation server (argus)?" "$(had argus n)"   && PROFILES="$PROFILES,argus"
ask_yn "Log aggregation (loki + promtail)?"         "$(had logging n)" && PROFILES="$PROFILES,logging"
ask_yn "Request tracing (langfuse)?"                "$(had tracing n)" && PROFILES="$PROFILES,tracing"
set_env COMPOSE_PROFILES "$PROFILES"
ok "profiles: $PROFILES"

# ---------------------------------------------------------------- budgets ----
step "Per-person usage limits"
note "Counted across the API and the web UI together, per person."
note "NOT 0 -- LiteLLM reads 0 as a budget of zero and refuses every request."
ask LITELLM_DEFAULT_USER_BUDGET "Default ceiling per person (USD of model spend)" "50"
ask LITELLM_BUDGET_DURATION "How often that resets (1mo, 30d, 24h)" "1mo"

# ------------------------------------------------------------------ argus ----
if [[ "$PROFILES" == *argus* ]]; then
  step "Argus (code index)"
  note "Argus resolves every request's identity against GitLab, so it needs one."
  ask ARGUS_GITLAB_URL "GitLab URL Argus should use" "http://host.docker.internal:8929"
  note "The token is written to .env, which is gitignored. Needs read_api + read_repository."
  ask ARGUS_GITLAB_TOKEN "GitLab access token for Argus" ""
  [[ -z "$(current ARGUS_GITLAB_TOKEN)" ]] && \
    warn "no token set -- argus will start but reject every request until you add one"
fi

# ------------------------------------------------------------------ people ---
step "Secrets and certificates"
run bash "$ROOT/scripts/bootstrap.sh"

if [[ "$PROFILES" == *auth* ]]; then
  step "Single sign-on"
  note "Edit config/authelia/team.yml to add people, then re-run with --sync."
  run bash "$ROOT/scripts/gen-auth.sh"
fi

# ------------------------------------------------------------------ start ----
step "Starting the stack"
if [[ $DRYRUN -eq 1 ]]; then
  note "would run: docker compose up -d"
else
  docker compose up -d
fi

# --------------------------------------------------------------- provision ---
if [[ $DRYRUN -eq 0 ]]; then
  step "Provisioning people on the gateway"
  note "Each person in team.yml gets an API key, a ceiling, and one usage total"
  note "spanning the API and the web UI. Keys are printed once."
  if ask_yn "Provision now? (the gateway must be healthy)" "y"; then
    for i in $(seq 1 30); do
      docker compose ps litellm --format '{{.Status}}' 2>/dev/null | grep -q healthy && break
      sleep 5
    done
    bash "$ROOT/scripts/llm-users.sh" --apply || \
      warn "provisioning failed -- run ./scripts/llm-users.sh --apply once the gateway is up"
  fi
fi

# ------------------------------------------------------------------- next ----
step "Done"
cat <<NEXT
    Reach the stack at:
      chat      https://chat.$DOMAIN
      gateway   https://gateway.$DOMAIN/v1
      grafana   https://grafana.$DOMAIN
      auth      https://auth.$DOMAIN
$([[ "$PROFILES" == *argus* ]] && echo "      argus     https://argus.$DOMAIN/mcp")

    Still to do by hand, because each needs a decision or a credential:

      1. Trust the local CA, or browsers will warn on every page:
             scripts/gen-certs.ps1 -Trust        (Windows)
             sudo scripts/gen-certs.sh --trust   (Linux/macOS)

      2. *.$DOMAIN resolves in browsers but NOT in curl or SDK clients:
             ./scripts/setup-hosts.sh

      3. The first vLLM start downloads the model. It can take an hour on a
         slow link. Do NOT recreate the container while it runs -- HuggingFace
         uses a fresh temp file per attempt, so a restart abandons the partial
         download rather than resuming it.

      4. Open WebUI stores its backend connection in its OWN database after
         first boot and ignores the environment variable from then on. If the
         model list is empty, fix it under Admin -> Settings -> Connections.

    Watch it come up:  ./scripts/health.sh
NEXT
