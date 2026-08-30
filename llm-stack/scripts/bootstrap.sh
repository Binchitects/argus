#!/usr/bin/env bash
# One-time setup for the LLMService stack (Linux / macOS / WSL).
# Verifies Docker + GPU passthrough, creates .env, and fills in random secrets.
#
#   ./scripts/bootstrap.sh          preserve existing secrets
#   ./scripts/bootstrap.sh --force  regenerate every secret
set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
ENV_FILE="$ROOT/.env"
ENV_TEMPLATE="$ROOT/.env.example"
FORCE=0
[[ "${1:-}" == "--force" ]] && FORCE=1

c_cyan=$'\033[36m'; c_green=$'\033[32m'; c_yellow=$'\033[33m'; c_red=$'\033[31m'; c_dim=$'\033[90m'; c_off=$'\033[0m'
step() { printf '%s==> %s%s\n' "$c_cyan" "$1" "$c_off"; }
ok()   { printf '    %sOK  %s%s\n' "$c_green" "$1" "$c_off"; }
warn() { printf '    %s!   %s%s\n' "$c_yellow" "$1" "$c_off"; }
fail() { printf '    %sX   %s%s\n' "$c_red" "$1" "$c_off"; }

# 32 hex chars per 16 bytes. openssl is present on every platform we target;
# fall back to /dev/urandom if not.
gen_secret() {
  local bytes="${1:-24}"
  if command -v openssl >/dev/null 2>&1; then
    openssl rand -hex "$bytes"
  else
    head -c "$bytes" /dev/urandom | od -An -tx1 | tr -d ' \n'
  fi
}

echo
echo "  LLMService bootstrap"
echo "  --------------------"
echo

step "Checking prerequisites"
command -v docker >/dev/null 2>&1 || { fail "docker not found on PATH"; exit 1; }
ok "$(docker --version)"
docker compose version >/dev/null 2>&1 || { fail "docker compose plugin unavailable"; exit 1; }
ok "$(docker compose version)"
docker info >/dev/null 2>&1 || { fail "Docker daemon not responding"; exit 1; }
ok "Docker daemon reachable"

step "Checking GPU"
if command -v nvidia-smi >/dev/null 2>&1; then
  nvidia-smi --query-gpu=name,memory.total,driver_version --format=csv,noheader | while read -r l; do ok "$l"; done
else
  warn "nvidia-smi not found on the host. vLLM requires an NVIDIA GPU."
fi

step "Verifying GPU passthrough into containers"
if docker run --rm --gpus all nvidia/cuda:12.4.1-base-ubuntu22.04 nvidia-smi -L 2>/dev/null; then
  ok "Containers can see the GPU"
else
  warn "Containers cannot reach the GPU - vLLM will fail to start."
  warn "Install nvidia-container-toolkit and restart the Docker daemon."
fi

step "Preparing .env"
[[ -f "$ENV_TEMPLATE" ]] || { fail ".env.example missing"; exit 1; }
if [[ ! -f "$ENV_FILE" ]]; then
  cp "$ENV_TEMPLATE" "$ENV_FILE"
  ok "Created .env from .env.example"
else
  ok ".env already exists (values preserved)"
fi

# key:byte-length:prefix
SECRETS=(
  "VLLM_API_KEY:24:sk-local-"
  "GRAFANA_ADMIN_PASSWORD:12:"
  "WEBUI_SECRET_KEY:32:"
  "POSTGRES_PASSWORD:16:"
  "LITELLM_MASTER_KEY:20:sk-"
  "LITELLM_SALT_KEY:16:"
  "CLICKHOUSE_PASSWORD:16:"
  "MINIO_ROOT_PASSWORD:16:"
  "LANGFUSE_SALT:16:"
  "LANGFUSE_NEXTAUTH_SECRET:32:"
  "LANGFUSE_ENCRYPTION_KEY:32:"
  "PROXY_AUTH_PASSWORD:12:"
)

changed=0
for entry in "${SECRETS[@]}"; do
  key="${entry%%:*}"; rest="${entry#*:}"; bytes="${rest%%:*}"; prefix="${rest#*:}"
  current="$(grep -E "^${key}=" "$ENV_FILE" | head -n1 | cut -d= -f2- || true)"
  if [[ $FORCE -eq 1 || -z "$current" || "$current" == *change-me* || "$current" =~ ^0{16,}$ ]]; then
    value="${prefix}$(gen_secret "$bytes")"
    # '|' as the sed delimiter: generated values are hex, never contain '|'.
    sed -i.bak -E "s|^${key}=.*|${key}=${value}|" "$ENV_FILE"
    changed=$((changed + 1))
  fi
done
rm -f "$ENV_FILE.bak"
[[ $changed -gt 0 ]] && ok "Generated $changed secret(s)" || ok "All secrets already set"

step "Creating directories"
mkdir -p "$ROOT/models"
ok "models/  (drop local weights here to serve without HuggingFace)"

if grep -qE '^COMPOSE_PROFILES=.*proxy' "$ENV_FILE"; then
  step "Generating TLS certificates and basic-auth for Traefik"
  if [[ $FORCE -eq 1 ]]; then "$ROOT/scripts/gen-certs.sh" --force; else "$ROOT/scripts/gen-certs.sh"; fi
else
  ok "proxy profile not enabled; skipping certificate generation"
fi

step "Validating docker-compose.yml"
(cd "$ROOT" && docker compose config --quiet) && ok "Compose file is valid" || { fail "Compose validation failed"; exit 1; }

get() { grep -E "^$1=" "$ENV_FILE" | head -n1 | cut -d= -f2-; }
echo
printf '  %sReady.%s\n\n' "$c_green" "$c_off"
printf '  %sModel     %s %s\n' "$c_dim" "$c_off" "$(get VLLM_MODEL)"
printf '  %sProfiles  %s %s\n' "$c_dim" "$c_off" "$(get COMPOSE_PROFILES)"
printf '  %sAPI key   %s %s\n' "$c_dim" "$c_off" "$(get VLLM_API_KEY)"
printf '  %sGrafana   %s %s / %s\n' "$c_dim" "$c_off" "$(get GRAFANA_ADMIN_USER)" "$(get GRAFANA_ADMIN_PASSWORD)"
echo
echo "  Next:  ./scripts/up.sh"
echo
printf '  %sFirst start downloads model weights (several GB). Follow with:%s\n' "$c_dim" "$c_off"
printf '  %s    docker logs -f vllm%s\n' "$c_dim" "$c_off"
echo
