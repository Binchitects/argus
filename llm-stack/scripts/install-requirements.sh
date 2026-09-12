#!/usr/bin/env bash
# ==============================================================================
# Install and verify everything the stack needs on a Linux server.
#
# Handles: Docker Engine + Compose plugin, the NVIDIA driver check, the NVIDIA
# Container Toolkit (without which containers cannot see the GPU), GPU
# passthrough verification, and the Linux-appropriate .env defaults.
#
#   sudo ./scripts/install-requirements.sh
#   ./scripts/install-requirements.sh --check-only      # no installs
#   sudo ./scripts/install-requirements.sh --domain llm.example.com
# ==============================================================================
set -uo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$ROOT"

CHECK_ONLY=0
DOMAIN=""
while [[ $# -gt 0 ]]; do
  case "$1" in
    --check-only) CHECK_ONLY=1; shift ;;
    --domain)     DOMAIN="$2"; shift 2 ;;
    -h|--help)    sed -n '2,12p' "$0"; exit 0 ;;
    *) echo "unknown option: $1" >&2; exit 1 ;;
  esac
done

c_cyan=$'\033[36m'; c_green=$'\033[32m'; c_yellow=$'\033[33m'; c_red=$'\033[31m'; c_off=$'\033[0m'
step() { printf '%s==> %s%s\n' "$c_cyan" "$1" "$c_off"; }
ok()   { printf '    %sOK   %s%s\n' "$c_green"  "$1" "$c_off"; }
warn() { printf '    %s!    %s%s\n' "$c_yellow" "$1" "$c_off"; }
fail() { printf '    %sX    %s%s\n' "$c_red"    "$1" "$c_off"; }

ISSUES=0
SUDO=""
# $USER is not set in every non-login shell, and `set -u` makes that fatal.
USER="${USER:-$(id -un 2>/dev/null || echo root)}"
[[ $EUID -ne 0 ]] && SUDO="sudo"

run() {  # honour --check-only for anything that mutates the system
  if [[ $CHECK_ONLY -eq 1 ]]; then
    warn "would run: $*"
    return 0
  fi
  $SUDO "$@"
}

echo
echo "  LLMService - requirements (Linux)"
echo "  ---------------------------------"
echo

# ---------------------------------------------------------------------------
step "Distribution"
if [[ -r /etc/os-release ]]; then
  . /etc/os-release
  ok "$PRETTY_NAME"
  DISTRO="$ID"
else
  warn "cannot read /etc/os-release; assuming Debian-like"
  DISTRO="debian"
fi

case "$DISTRO" in
  ubuntu|debian|linuxmint|pop) PKG="apt" ;;
  fedora|rhel|centos|rocky|almalinux) PKG="dnf" ;;
  arch|manjaro) PKG="pacman" ;;
  *) PKG="unknown"; warn "unrecognised distro '$DISTRO' - install steps may need adapting" ;;
esac
ok "package manager: $PKG"

# ---------------------------------------------------------------------------
step "Docker Engine"
if command -v docker >/dev/null 2>&1; then
  ok "$(docker --version)"
else
  warn "not installed"
  if [[ $CHECK_ONLY -eq 0 ]]; then
    # The convenience script handles every supported distro and pulls in the
    # compose plugin, which distro packages frequently omit or ship stale.
    warn "installing via get.docker.com"
    curl -fsSL https://get.docker.com -o /tmp/get-docker.sh && $SUDO sh /tmp/get-docker.sh
    if command -v docker >/dev/null 2>&1; then ok "installed"; else fail "install failed"; ISSUES=$((ISSUES+1)); fi
  else
    warn "would install Docker Engine"
  fi
fi

step "Docker Compose plugin"
if docker compose version >/dev/null 2>&1; then
  ok "$(docker compose version)"
else
  fail "compose plugin missing - install docker-compose-plugin"
  ISSUES=$((ISSUES+1))
fi

step "Docker daemon"
if docker info >/dev/null 2>&1; then
  ok "reachable"
else
  warn "not reachable - starting"
  run systemctl enable --now docker >/dev/null 2>&1
  sleep 3
  if docker info >/dev/null 2>&1; then ok "started"; else fail "daemon still down"; ISSUES=$((ISSUES+1)); fi
fi

# Running docker without sudo is a convenience, but it is equivalent to root -
# say so rather than doing it silently.
step "Docker group membership"
if id -nG "$USER" 2>/dev/null | tr ' ' '\n' | grep -qx docker; then
  ok "$USER is in the docker group"
elif [[ $EUID -eq 0 ]]; then
  ok "running as root"
else
  warn "$USER is not in the docker group, so docker needs sudo"
  warn "to change that (note: docker group access is equivalent to root):"
  warn "    sudo usermod -aG docker $USER && newgrp docker"
fi

# ---------------------------------------------------------------------------
step "NVIDIA driver"
if command -v nvidia-smi >/dev/null 2>&1; then
  nvidia-smi --query-gpu=name,memory.total,driver_version --format=csv,noheader 2>/dev/null | while read -r l; do ok "$l"; done
else
  fail "nvidia-smi not found - install the proprietary NVIDIA driver first"
  case "$PKG" in
    apt)    warn "  sudo apt install nvidia-driver-550 (or use 'ubuntu-drivers autoinstall')" ;;
    dnf)    warn "  enable RPM Fusion, then: sudo dnf install akmod-nvidia" ;;
    pacman) warn "  sudo pacman -S nvidia nvidia-utils" ;;
  esac
  ISSUES=$((ISSUES+1))
fi

# ---------------------------------------------------------------------------
# The container toolkit is the piece people miss: the host driver alone does
# NOT let containers see the GPU.
# ---------------------------------------------------------------------------
step "NVIDIA Container Toolkit"
if command -v nvidia-ctk >/dev/null 2>&1; then
  ok "$(nvidia-ctk --version 2>/dev/null | head -1)"
else
  warn "not installed"
  if [[ $CHECK_ONLY -eq 0 ]]; then
    case "$PKG" in
      apt)
        curl -fsSL https://nvidia.github.io/libnvidia-container/gpgkey \
          | $SUDO gpg --dearmor -o /usr/share/keyrings/nvidia-container-toolkit-keyring.gpg
        curl -fsSL https://nvidia.github.io/libnvidia-container/stable/deb/nvidia-container-toolkit.list \
          | sed 's#deb https://#deb [signed-by=/usr/share/keyrings/nvidia-container-toolkit-keyring.gpg] https://#g' \
          | $SUDO tee /etc/apt/sources.list.d/nvidia-container-toolkit.list >/dev/null
        $SUDO apt-get update -qq && $SUDO apt-get install -y nvidia-container-toolkit
        ;;
      dnf)
        curl -fsSL https://nvidia.github.io/libnvidia-container/stable/rpm/nvidia-container-toolkit.repo \
          | $SUDO tee /etc/yum.repos.d/nvidia-container-toolkit.repo >/dev/null
        $SUDO dnf install -y nvidia-container-toolkit
        ;;
      pacman)
        $SUDO pacman -S --noconfirm nvidia-container-toolkit
        ;;
      *) fail "install nvidia-container-toolkit manually for this distro"; ISSUES=$((ISSUES+1)) ;;
    esac
    if command -v nvidia-ctk >/dev/null 2>&1; then
      ok "installed"
      # Wire the runtime into the daemon and restart it.
      $SUDO nvidia-ctk runtime configure --runtime=docker >/dev/null 2>&1 && ok "docker runtime configured"
      $SUDO systemctl restart docker && sleep 4 && ok "docker restarted"
    else
      fail "toolkit install failed"; ISSUES=$((ISSUES+1))
    fi
  else
    warn "would install nvidia-container-toolkit"
  fi
fi

# ---------------------------------------------------------------------------
step "GPU passthrough into containers"
if docker run --rm --gpus all nvidia/cuda:12.4.1-base-ubuntu22.04 nvidia-smi -L 2>/dev/null | grep -q '^GPU'; then
  docker run --rm --gpus all nvidia/cuda:12.4.1-base-ubuntu22.04 nvidia-smi -L 2>/dev/null | while read -r l; do ok "$l"; done
else
  fail "containers cannot see the GPU"
  warn "usually: nvidia-ctk runtime configure --runtime=docker && systemctl restart docker"
  ISSUES=$((ISSUES+1))
fi

# ---------------------------------------------------------------------------
step "Supporting tools"
for t in curl openssl jq git; do
  if command -v "$t" >/dev/null 2>&1; then
    ok "$t"
  else
    if [[ $CHECK_ONLY -eq 1 ]]; then
      warn "$t missing - would install"
    else
      warn "$t missing - installing"
      case "$PKG" in
        apt)    run apt-get install -y "$t" >/dev/null 2>&1 ;;
        dnf)    run dnf install -y "$t" >/dev/null 2>&1 ;;
        pacman) run pacman -S --noconfirm "$t" >/dev/null 2>&1 ;;
      esac
      if command -v "$t" >/dev/null 2>&1; then
        ok "$t installed"
      else
        fail "$t install failed"; ISSUES=$((ISSUES+1))
      fi
    fi
  fi
done

# ---------------------------------------------------------------------------
step "Ports 80 / 443 (needed by the proxy profile)"
for p in 80 443; do
  if command -v ss >/dev/null 2>&1 && ss -ltn "( sport = :$p )" 2>/dev/null | grep -q LISTEN; then
    warn "port $p already in use - change TRAEFIK_HTTP_PORT / TRAEFIK_HTTPS_PORT in .env"
  else
    ok "port $p free"
  fi
done

# ---------------------------------------------------------------------------
# Linux-specific configuration. These defaults differ from Windows.
# ---------------------------------------------------------------------------
step "Linux defaults in .env"
if [[ ! -f .env ]]; then
  warn ".env not present yet - run ./scripts/bootstrap.sh, then re-run this"
else
  set_env() {
    # Honour --check-only: this function is the only thing that writes .env.
    if [[ $CHECK_ONLY -eq 1 ]]; then return 0; fi
    if grep -qE "^$1=" .env; then
      sed -i "s|^$1=.*|$1=$2|" .env
    else
      printf '%s=%s\n' "$1" "$2" >> .env
    fi
  }

  # WSL2 has no Unified Virtual Addressing, so on Windows the engine must fall
  # back to the V1 model runner. Linux has UVA, and V2 is the faster path.
  set_env VLLM_USE_V2_MODEL_RUNNER 1
  ok "VLLM_USE_V2_MODEL_RUNNER=1 (V2 runner works on Linux; WSL2 cannot)"

  # node-exporter measures the real machine here, so DCGM is worth having and
  # windows_exporter is meaningless.
  if grep -qE '^COMPOSE_PROFILES=.*\bsmi\b' .env && command -v nvidia-smi >/dev/null 2>&1; then
    ok "keeping the 'smi' GPU exporter (works everywhere)"
    warn "on bare metal you can additionally enable 'dcgm' for SM/PCIe/NVLink detail"
  fi

  if [[ -n "$DOMAIN" ]]; then
    set_env LLM_DOMAIN "$DOMAIN"
    ok "LLM_DOMAIN=$DOMAIN"
    warn "remember: certificate paths in config/traefik/dynamic/tls.yml reference"
    warn "the domain name, and a public domain wants a real CA - see docs/LINUX.md"
  fi

  # A server is usually reached from elsewhere; loopback-only would lock you out
  # of the direct ports, but Traefik on 80/443 still serves everything.
  if grep -qE '^BIND_ADDRESS=127.0.0.1' .env; then
    ok "BIND_ADDRESS=127.0.0.1 (direct ports stay local; reach services via Traefik)"
  fi
fi

# ---------------------------------------------------------------------------
step "Prometheus: Windows-only scrape job"
if grep -q 'host.docker.internal' config/prometheus/prometheus.yml 2>/dev/null; then
  # host.docker.internal does not resolve on Linux without an explicit
  # host-gateway mapping, and windows_exporter does not exist here anyway.
  if [[ $CHECK_ONLY -eq 0 ]]; then
    python3 - <<'PY'
import re, pathlib
p = pathlib.Path('config/prometheus/prometheus.yml')
t = p.read_text(encoding='utf-8')
t = re.sub(r'\n  # ---- Windows host metrics.*?component: host\n',
           '\n  # ---- Windows host metrics (removed: this is a Linux host) -------------------\n'
           '  # node-exporter measures the real machine here, so windows_exporter is not\n'
           '  # needed. host.docker.internal also does not resolve on Linux by default.\n',
           t, flags=re.S)
p.write_text(t, encoding='utf-8')
PY
    ok "removed (node-exporter already measures this host directly)"
  else
    warn "would remove the windows_exporter job"
  fi
else
  ok "not present"
fi

# ---------------------------------------------------------------------------
echo
if [[ $ISSUES -eq 0 ]]; then
  printf '  %sAll requirements satisfied.%s\n' "$c_green" "$c_off"
else
  printf '  %s%d problem(s) above need attention.%s\n' "$c_red" "$ISSUES" "$c_off"
fi
echo
echo "  Next:"
echo "    ./scripts/bootstrap.sh     # .env, secrets, certificates"
echo "    ./scripts/up.sh            # start the stack"
echo
exit $ISSUES
