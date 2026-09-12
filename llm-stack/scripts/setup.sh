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
#   ./scripts/setup.sh --domain llm.example.com
#   ./scripts/setup.sh --defaults --domain box.local --set VLLM_MAX_MODEL_LEN=65536
#
#   --domain D     set LLM_DOMAIN without being asked
#   --set K=V      set any .env key (repeatable); applied before every question
#   ./scripts/setup.sh --dry-run    print the plan, change nothing
#   ./scripts/setup.sh --no-start   write every config, start nothing
#                                   (for an appliance configured before it ships)
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

DEFAULTS=0; DRYRUN=0; NOSTART=0
PRESEED=()          # KEY=VALUE pairs written to .env BEFORE any question is asked
while [[ $# -gt 0 ]]; do
  case "$1" in
    --defaults) DEFAULTS=1; shift ;;
    --dry-run)  DRYRUN=1; shift ;;
    --no-start) NOSTART=1; shift ;;
    # --domain is the one people reach for most, so it gets its own flag.
    --domain)   PRESEED+=("LLM_DOMAIN=$2"); shift 2 ;;
    --domain=*) PRESEED+=("LLM_DOMAIN=${1#*=}"); shift ;;
    # --set is the general form, repeatable: --set VLLM_MAX_MODEL_LEN=65536
    --set)      PRESEED+=("$2"); shift 2 ;;
    --set=*)    PRESEED+=("${1#*=}"); shift ;;
    -h|--help)  sed -n '2,16p' "$0"; exit 0 ;;
    *) echo "unknown option: $1" >&2; exit 2 ;;
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
# tr -d '\r': a .env copied from a CRLF template yields values with a trailing
# carriage return, and every validation below then rejects a correct answer
# while printing it as if it were fine -- "must be a whole number 0-50, got:
# '10'". Strip it once here rather than in each caller.
current() { [[ -f "$ENV_FILE" ]] && grep -E "^$1=" "$ENV_FILE" | head -n1 | cut -d= -f2- | tr -d '\r' || true; }

# Tighten every file that holds a secret. Defined as a function because it has
# to run AFTER each step that writes one: gen-auth regenerates users.yml and
# clients.yml late in the run, so a single early pass left client-secret and
# password hashes at 0644 on a fresh install.
harden_secrets() {
  local _s
  for _s in "$ENV_FILE" \
            "$ROOT/config/authelia/users.yml" \
            "$ROOT/config/authelia/clients.yml" \
            "$ROOT/config/authelia/secrets/oidc.pem" \
            "$ROOT/config/traefik/auth/users.htpasswd" \
            "$ROOT"/config/traefik/certs/*.key; do
    [[ -f "$_s" ]] && chmod 600 "$_s" 2>/dev/null || true
  done
  chmod 700 "$ROOT/config/traefik/certs" "$ROOT/config/authelia/secrets" 2>/dev/null || true
  # 711, not 700: Prometheus runs as uid 65534 and must traverse this to read
  # the scrape token. See the note where the token is written.
  chmod 711 "$ROOT/config/prometheus/secrets" 2>/dev/null || true
}

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

# Values supplied on the command line are written first. `ask` treats an
# existing .env entry as its default, so these are accepted silently under
# --defaults and pre-filled when prompting -- which is what makes an
# unattended, repeatable deploy possible.
for _kv in ${PRESEED+"${PRESEED[@]}"}; do
  [[ "$_kv" == *=* ]] || die "--set expects KEY=VALUE, got: $_kv"
  # No note here on purpose: set_env already reports, and under --dry-run it
  # reports "would set". Announcing "preset X" unconditionally would claim a
  # write that --dry-run never performs.
  set_env "${_kv%%=*}" "${_kv#*=}"
done

ask LLM_DOMAIN "Domain for the stack (services appear at *.DOMAIN)" "llm.localhost"
DOMAIN="$(current LLM_DOMAIN)"

# ------------------------------------------------------------- the engine ----
step "Inference engine"
note "Two engines ship with this stack, and EXACTLY ONE runs: both take the"
note "whole GPU, so enabling both means one of them dies with a CUDA OOM."
note ""
note "  vllm      safetensors, much better batching. Use it whenever the whole"
note "            model fits in VRAM -- which is the usual case."
note "  llamacpp  GGUF, and can keep mixture-of-experts weights in system RAM."
note "            Use it when the model does NOT fit: it is what lets a 177B"
note "            MoE like Qwen3.8-Flash-Next serve from a 24 GB card."
ask LLM_ENGINE "Engine to run (vllm | llamacpp)" "vllm"
ENGINE="$(current LLM_ENGINE)"
case "$ENGINE" in
  vllm|llamacpp) ;;
  # Fail here rather than later: an unrecognised value would otherwise reach
  # COMPOSE_PROFILES, match no service, and the stack would come up with no
  # engine at all and no error saying why.
  *) die "LLM_ENGINE must be 'vllm' or 'llamacpp', got: '$ENGINE'" ;;
esac

if [[ "$ENGINE" == "vllm" ]]; then
  note "vLLM serves ONE model at a time and claims most of the GPU up front."
  note "A 24 GB card cannot hold an 8B and a 27B together."
  ask VLLM_MODEL "HuggingFace model id to serve" "Qwen/Qwen3-8B"
  ask VLLM_SERVED_MODEL_NAME "Name clients will use for it" "qwen3-8b"
  ask VLLM_MAX_MODEL_LEN "Context length" "8192"
  ask VLLM_GPU_MEMORY_UTILIZATION "Fraction of VRAM vLLM may claim" "0.90"
else
  note "llama.cpp reads GGUF from a directory you already have -- it does not"
  note "download anything. Fetch the weights first, e.g. with huggingface-cli,"
  note "then point LLAMACPP_MODEL_DIR at where they landed."
  note "For a split GGUF give only the FIRST shard; the rest are found for you."
  ask LLAMACPP_MODEL_DIR "Host directory holding the .gguf files (use NVMe)" "./models"
  ask LLAMACPP_MODEL_FILE "GGUF file name inside that directory" ""
  ask LLAMACPP_SERVED_MODEL_NAME "Name clients will use for it" "local"
  ask LLAMACPP_CONTEXT "Context length" "32768"
  note "n-cpu-moe keeps the routed experts of the last N layers in system RAM."
  note "Raise it if the server dies with a CUDA OOM while loading weights;"
  note "lower it for speed once you know it fits. 48 covers every layer of"
  note "Qwen3.8-Flash-Next."
  ask LLAMACPP_N_CPU_MOE "Layers whose experts stay in system RAM" "48"
  ask LLAMACPP_KV_TYPE "KV cache type (f16 | q8_0 | q5_1 | q4_0)" "q8_0"

  # ---- keep a floor of RAM for everything that is not the model ----------
  #
  # An unlimited container lets mmap'd model pages fill the page cache to the
  # last byte. Linux reclaims them under pressure so it does not crash, but
  # nothing is guaranteed to anyone else -- and on Windows the WSL VM growing
  # into its ceiling is what took Docker Desktop down earlier in this stack's
  # life (56 GB of a 63.7 GB host, against ~11 GB Windows actually needed).
  #
  # RAM is read from INSIDE a container on purpose. That is the number the
  # engine can really use, and it is correct on both targets without a special
  # case: on Linux it is the host's RAM, on Windows it is the WSL VM's ceiling
  # from .wslconfig rather than the host's 64 or 128 GB.
  ask LLM_MEM_RESERVE_PCT "Percent of RAM to keep free for the system" "10"
  _pct="$(current LLM_MEM_RESERVE_PCT)"
  if ! [[ "$_pct" =~ ^[0-9]+$ ]] || [[ "$_pct" -lt 0 || "$_pct" -gt 50 ]]; then
    die "LLM_MEM_RESERVE_PCT must be a whole number 0-50, got: '$_pct'"
  fi
  _tot_mb="$(docker run --rm alpine sh -c "awk '/MemTotal/ {print int(\$2/1024)}' /proc/meminfo" 2>/dev/null || echo 0)"
  if [[ "$_tot_mb" =~ ^[0-9]+$ ]] && [[ "$_tot_mb" -gt 0 ]]; then
    # Other stack services (gateway, database, monitoring) need their share
    # too, and they are not covered by the engine's own limit.
    _others_mb=6144
    _limit_mb=$(( _tot_mb * (100 - _pct) / 100 - _others_mb ))
    # Whether the cap COSTS anything depends entirely on whether the model
    # fits underneath it. Measured on a 24 GB / 50 GB box with a 93.7 GB model
    # -- which cannot fit either way -- reserving 10% took warm decode from
    # 8.02 to 5.46 tok/s, a 32% loss, because every GB taken from the page
    # cache becomes disk I/O. On a box where the model DOES fit, the cap sits
    # above the working set and costs nothing at all.
    #
    # So: size it, compare, and say which case this is instead of applying a
    # number silently.
    _model_mb=0
    _dir="$(current LLAMACPP_MODEL_DIR)"; _file="$(current LLAMACPP_MODEL_FILE)"
    if [[ -n "$_dir" && -n "$_file" && -d "$_dir" ]]; then
      # Multi-part GGUF: the named shard is only the first of N.
      _stem="${_file%-*-of-*.gguf}"
      _model_mb=$(du -cm "$_dir/$_stem"*.gguf 2>/dev/null | tail -1 | cut -f1)
      [[ "$_model_mb" =~ ^[0-9]+$ ]] || _model_mb=0
    fi

    if [[ $_limit_mb -lt 8192 ]]; then
      warn "only ${_tot_mb} MB visible; leaving the engine unlimited rather than starving it"
      set_env LLAMACPP_MEM_LIMIT "0"
    else
      set_env LLAMACPP_MEM_LIMIT "${_limit_mb}m"
      ok "engine capped at ${_limit_mb} MB of ${_tot_mb} MB (${_pct}% reserved for the system)"
      if [[ $_model_mb -gt 0 && $_model_mb -gt $_limit_mb ]]; then
        warn "the model is ${_model_mb} MB and the cap is ${_limit_mb} MB, so it CANNOT be"
        warn "fully cached -- expect roughly a third less throughput than uncapped."
        warn "On Windows the WSL ceiling in .wslconfig already reserves RAM for the"
        warn "host, so this second reservation inside the VM may be redundant:"
        warn "set LLAMACPP_MEM_LIMIT=0 to spend it on cache instead."
      elif [[ $_model_mb -gt 0 ]]; then
        ok "the ${_model_mb} MB model fits under that cap -- the reserve costs nothing"
      fi
    fi
  else
    warn "could not read RAM from a container -- leaving the engine unlimited"
    set_env LLAMACPP_MEM_LIMIT "0"
  fi

  # A missing file is the single most likely mistake here, and the symptom
  # otherwise is a container that restarts forever with the reason buried in
  # its logs. Warn now; do not die, because the weights may still be
  # downloading and setup is worth finishing anyway.
  _dir="$(current LLAMACPP_MODEL_DIR)"; _file="$(current LLAMACPP_MODEL_FILE)"
  if [[ -z "$_file" ]]; then
    warn "no GGUF file set -- llama.cpp will not start until LLAMACPP_MODEL_FILE is filled in"
  elif [[ -n "$_dir" && ! -e "$_dir/$_file" ]]; then
    warn "not found yet: $_dir/$_file (fine if it is still downloading)"
  else
    ok "found $_dir/$_file"
  fi
fi

# The V2 runner needs Unified Virtual Addressing, which WSL2's GPU driver does
# not expose: the engine dies with "RuntimeError: UVA is not available" AFTER
# the container has reported healthy once, which reads like a hardware fault
# rather than a setting.
if [[ "$ENGINE" != "vllm" ]]; then
  : # llama.cpp has no V2 runner and downloads nothing -- skip both questions.
elif grep -qiE "microsoft|wsl" /proc/version 2>/dev/null || [[ "$OSTYPE" == msys* || "$OSTYPE" == cygwin* ]]; then
  set_env VLLM_USE_V2_MODEL_RUNNER 0
  ok "WSL2/Windows detected -- forcing vLLM's V1 model runner (V2 needs UVA)"
else
  ask VLLM_USE_V2_MODEL_RUNNER "Use vLLM's faster V2 runner? (1 on bare-metal Linux)" "1"
fi

# A first pull of a multi-GB checkpoint over a slow link spends most of its
# time hitting the 10s default and retrying, which gets slower as it goes.
if [[ "$ENGINE" == "vllm" ]]; then
  ask HF_HUB_DOWNLOAD_TIMEOUT "HuggingFace read timeout, seconds" "120"
  ask HF_TOKEN "HuggingFace token (only for gated models, blank is fine)" ""
fi

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
ask_yn "Code index + documentation server (argus)?" "$(had argus y)"   && PROFILES="$PROFILES,argus"
ask_yn "Log aggregation (loki + promtail)?"         "$(had logging n)" && PROFILES="$PROFILES,logging"
ask_yn "Request tracing (langfuse)?"                "$(had tracing n)" && PROFILES="$PROFILES,tracing"

# The engine is a profile too, and it is NOT asked about again -- it was chosen
# above. Appending it unconditionally is also the upgrade path: an .env written
# when vLLM was always-on has no engine profile at all, and would otherwise come
# up with no engine and no explanation.
PROFILES="$PROFILES,$ENGINE"
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
    if [[ -z "$(current ARGUS_GITLAB_TOKEN)" ]]; then
      # argus is a DEFAULT profile now, so an unattended deploy would
      # otherwise ship a container that EXITS at startup with
      #   config error: no GitLab credential: set gitlab.token
      # and is restarted forever. Every other service reports healthy and
      # the only symptom is argus.<domain> serving Traefik's default
      # certificate, because no router appears for a container that never
      # stays up long enough to be discovered.
      #
      # Shipping nothing beats shipping something broken: drop the profile
      # and say so. Add the token and re-run to turn it back on.
      warn "no GitLab token -- argus would crash-loop, so it is being left OUT"
      note "Set ARGUS_GITLAB_TOKEN in .env and re-run setup to enable it."
      PROFILES="$(printf %s "$PROFILES" | sed -e "s/,argus//" -e "s/^argus,//" -e "s/^argus$//")"
      set_env COMPOSE_PROFILES "${PROFILES#,}"
    fi
fi

# ------------------------------------------------------------------ people ---
step "Secrets and certificates"
run bash "$ROOT/scripts/bootstrap.sh"

# ------------------------------------------------- point the gateway at it ----
# AFTER bootstrap, deliberately: bootstrap is what generates the engine API
# keys, so before this line VLLM_API_KEY/LLAMACPP_API_KEY may still hold the
# placeholder from .env.example. Deriving these earlier would copy the
# placeholder and every gateway request would come back 401.
#
# LiteLLM resolves these through `os.environ/` in config/litellm/config.yaml,
# which is why that file names no engine and needs no edit to switch.
if [[ "$ENGINE" == "vllm" ]]; then
  set_env ENGINE_API_BASE "http://vllm:8000/v1"
  set_env ENGINE_MODEL    "openai/$(current VLLM_SERVED_MODEL_NAME)"
  set_env ENGINE_API_KEY  "$(current VLLM_API_KEY)"
  _ctx="$(current VLLM_MAX_MODEL_LEN)"
else
  set_env ENGINE_API_BASE "http://llamacpp:8080/v1"
  set_env ENGINE_MODEL    "openai/$(current LLAMACPP_SERVED_MODEL_NAME)"
  set_env ENGINE_API_KEY  "$(current LLAMACPP_API_KEY)"
  _ctx="$(current LLAMACPP_CONTEXT)"
fi
ok "gateway -> $ENGINE ($(current ENGINE_MODEL) at $(current ENGINE_API_BASE))"

# Prometheus scrapes llama.cpp's /metrics, and llama.cpp protects every path
# except /health -- so the scrape needs a bearer token. Kept in a file rather
# than in prometheus.yml so the key is not committed.
_ptok="$ROOT/config/prometheus/secrets/llamacpp.token"
if [[ $DRYRUN -eq 0 ]]; then
  mkdir -p "$(dirname "$_ptok")"
  # 0700/0600, and the umask is set BEFORE the redirection so the file is never
  # briefly world-readable between creation and chmod. This is a real API key on
  # a machine that may have other local accounts.
  chmod 700 "$(dirname "$_ptok")" 2>/dev/null || true
  ( umask 077 && printf '%s' "$(current LLAMACPP_API_KEY)" > "$_ptok" )
  chmod 600 "$_ptok" 2>/dev/null || true

  # a9f87ac tightened the Prometheus token and stopped there, but it is not the
  # only secret written here: .env holds every credential in the stack, and the
  # Authelia files hold password and client-secret hashes. All were left 0644,
  # i.e. readable by any account on the box.
  harden_secrets
  chmod 700 "$ROOT/config/traefik/certs" "$ROOT/config/authelia/secrets" 2>/dev/null || true
  # 711, NOT 700, for the Prometheus secrets directory. Prometheus runs as uid
  # 65534 inside its container and must TRAVERSE this directory to read the
  # scrape token; 700 makes that "unable to read" and the llamacpp target goes
  # down while everything else looks healthy. 711 grants traversal without
  # listing, and the token itself stays 0640 plus an ACL for that uid.
  chmod 711 "$ROOT/config/prometheus/secrets" 2>/dev/null || true
  if command -v setfacl >/dev/null 2>&1; then
    setfacl -m u:65534:r "$_ptok" 2>/dev/null || true
  else
    # No ACL support: fall back to group-readable rather than silently
    # shipping a token Prometheus cannot read.
    chmod 644 "$_ptok" 2>/dev/null || true
    echo "    note: setfacl unavailable; scrape token left world-readable so Prometheus can read it"
  fi
  ok "prometheus can scrape the engine (token written)"
fi

# Advertise the REAL window. model_info is not part of litellm_params, so it
# cannot use os.environ/ -- LiteLLM would hand a string where an int belongs.
# Rewriting the literals is the same thing scripts/switch-model does.
#
# This is not cosmetic. Clients cache what /model/info reports: Hermes wrote
# 24,576 into its context_length_cache.yaml and then kept refusing to start
# against an engine that had long since grown, because nothing invalidated it.
if [[ -n "${_ctx:-}" && "$_ctx" =~ ^[0-9]+$ ]] && [[ $DRYRUN -eq 0 ]]; then
  _cfg="$ROOT/config/litellm/config.yaml"
  sed -i -E "s/^      max_input_tokens: [0-9]+/      max_input_tokens: $_ctx/; s/^      max_tokens: [0-9]+/      max_tokens: $_ctx/" "$_cfg"
  ok "gateway advertises a ${_ctx}-token window"
fi

# Authelia keeps its state in SQLite inside the authelia-data volume, encrypted
# with AUTHELIA_STORAGE_ENCRYPTION_KEY. The key lives in .env; the database
# lives in a Docker volume. Delete .env (or start from a fresh checkout) while
# that volume survives and gen-auth mints a NEW key against the OLD database --
# Authelia then crash-loops on every start, unable to decrypt, and the message
# names the encryption key rather than the volume nobody thought to remove.
#
# Detect it here rather than let it surface as a crash-loop: remember the key
# before gen-auth runs, and compare afterwards.
_key_before="$(current AUTHELIA_STORAGE_ENCRYPTION_KEY)"

if [[ "$PROFILES" == *auth* ]]; then
  step "Single sign-on"
  note "Edit config/authelia/team.yml to add people, then re-run with --sync."
  run bash "$ROOT/scripts/gen-auth.sh"
  harden_secrets   # gen-auth just rewrote users.yml and clients.yml

  _key_after="$(current AUTHELIA_STORAGE_ENCRYPTION_KEY)"
  # An EMPTY key before also counts: deleting .env without removing the
  # volume is the commonest way to reach this state.
  if [[ $DRYRUN -eq 0 && "$_key_before" != "$_key_after" ]]; then
    # Same precedence Docker Compose itself uses: an exported
    # COMPOSE_PROJECT_NAME overrides the one in .env. Reading only .env would
    # make this guard inspect a volume belonging to a different project.
    _vol="${COMPOSE_PROJECT_NAME:-$(current COMPOSE_PROJECT_NAME)}"
    _vol="${_vol:-llmservice}_authelia-data"
    if docker volume inspect "$_vol" >/dev/null 2>&1 &&        docker run --rm -v "$_vol":/d alpine:3 test -f /d/db.sqlite3 2>/dev/null; then
      warn "the Authelia encryption key changed, but $_vol still holds a database"
      note "Authelia cannot decrypt a database written with the previous key; it"
      note "would crash-loop on every start. The volume holds SESSIONS only --"
      note "never user accounts, which live in config/authelia/users.yml."
      note "Reset it with:  docker volume rm $_vol"
      die "refusing to start Authelia against an undecryptable database"
    fi
  fi
fi

# ------------------------------------------------------------------ start ----
# ---------------------------------------------------------------- preflight ---
# Traefik binds the host's HTTP/HTTPS ports. If anything else already has them,
# `docker compose up` fails with "Bind for 0.0.0.0:80 failed: port is already
# allocated" -- a daemon-level message that names no culprit and sends people
# looking for a fault in this stack. Measured here: an unrelated project's
# Traefik held 80/443 and three clean deploys failed with no usable diagnosis.
step "Checking host ports"
_http="$(current TRAEFIK_HTTP_PORT)";  _http="${_http:-80}"
_https="$(current TRAEFIK_HTTPS_PORT)"; _https="${_https:-443}"
_conflict=0
for _port in "$_http" "$_https"; do
  # A container from another project is the common case, and the only one we
  # can name precisely.
  _owner="$(docker ps --format '{{.Names}}	{{.Ports}}' 2>/dev/null             | grep -E ":$_port->" | cut -f1 | grep -v '^traefik$' | head -n1 || true)"
  if [[ -n "$_owner" ]]; then
    warn "port $_port is already published by container '$_owner'"
    _conflict=1
  fi
done
if [[ $_conflict -eq 1 ]]; then
  note "Either stop that container, or serve this stack on different ports:"
  note "    ./scripts/setup.sh --set TRAEFIK_HTTP_PORT=8080 --set TRAEFIK_HTTPS_PORT=8443"
  note "URLs then carry the port, e.g. https://chat.$DOMAIN:8443"
  die "host port conflict -- nothing was started"
fi
ok "ports $_http and $_https are free"

step "Starting the stack"
if [[ $NOSTART -eq 1 ]]; then
  note "--no-start: configuration written, nothing started"
  note "start it later with: docker compose up -d"
elif [[ $DRYRUN -eq 1 ]]; then
  note "would run: docker compose up -d"
else
  docker compose up -d
fi

# --------------------------------------------------------------- provision ---
if [[ $DRYRUN -eq 0 && $NOSTART -eq 0 ]]; then
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
