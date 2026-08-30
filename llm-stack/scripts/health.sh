#!/usr/bin/env bash
# Check every enabled component.
#
# With the proxy-only setup there are no published ports to poll, so this works
# two ways that need no credentials:
#   1. Docker's own healthchecks / container state
#   2. an in-network probe via `docker exec`, which bypasses Traefik and so
#      needs no session or token
#
# Exit code equals the number of failing components.
set -uo pipefail
ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$ROOT"

c_green=$'\033[32m'; c_red=$'\033[31m'; c_yellow=$'\033[33m'; c_dim=$'\033[90m'; c_off=$'\033[0m'
get() { grep -E "^$1=" .env 2>/dev/null | head -n1 | cut -d= -f2- | tr -d '[:space:]'; }
PROFILES="$(get COMPOSE_PROFILES)"
DOM="$(get LLM_DOMAIN)"; DOM="${DOM:-llm.localhost}"
failures=0

# name | internal url | required-profile ("" = always on)
CHECKS=(
  "vLLM|http://vllm:8000/health|"
  "Open WebUI|http://open-webui:8080/health|"
  "Prometheus|http://prometheus:9090/-/healthy|"
  "Alertmanager|http://alertmanager:9093/-/healthy|"
  "Grafana|http://grafana:3000/api/health|"
  "node-exporter|http://node-exporter:9100/metrics|"
  "cAdvisor|http://cadvisor:8080/healthz|"
  "GPU exporter|http://nvidia-smi-exporter:9835/metrics|smi"
  "Loki|http://loki:3100/ready|logging"
  "LiteLLM|http://litellm:4000/health/liveliness|gateway"
  "Authelia|http://authelia:9091/api/health|auth"
  "Langfuse|http://langfuse:3000/api/public/health|tracing"
  "Traefik ping|http://traefik:8082/ping|proxy"
)

echo
printf '  %-20s %-8s %s\n' "COMPONENT" "STATUS" "DETAIL"
printf '  %s\n' "--------------------------------------------------------------"

# One helper container on the stack network probes everything internally.
probe() { docker run --rm --network llm-net curlimages/curl:8.11.1 -s -o /dev/null -w '%{http_code}' -m 8 "$1" 2>/dev/null || echo 000; }

for entry in "${CHECKS[@]}"; do
  name="${entry%%|*}"; rest="${entry#*|}"; url="${rest%%|*}"; profile="${rest#*|}"
  if [ -n "$profile" ]; then
    case "$PROFILES" in
      *"$profile"*) ;;
      *) printf '  %-20s %sSKIP%s     %sprofile %s off%s\n' "$name" "$c_dim" "$c_off" "$c_dim" "$profile" "$c_off"; continue ;;
    esac
  fi
  code="$(probe "$url")"
  case "$code" in
    2*|3*) printf '  %-20s %sOK%s       HTTP %s\n' "$name" "$c_green" "$c_off" "$code" ;;
    *)     failures=$((failures + 1))
           printf '  %-20s %sDOWN%s     HTTP %s\n' "$name" "$c_red" "$c_off" "$code" ;;
  esac
done

# ---------------------------------------------------------------------------
echo
echo "  Containers"
printf '  %s\n' "--------------------------------------------------------------"
docker compose ps --format '{{.Name}}\t{{.Status}}' | sed 's/^/  /'

# ---------------------------------------------------------------------------
echo
echo "  Prometheus targets"
printf '  %s\n' "--------------------------------------------------------------"
targets="$(docker run --rm --network llm-net curlimages/curl:8.11.1 -s -m 8 \
           'http://prometheus:9090/api/v1/targets' 2>/dev/null || true)"
if [ -n "$targets" ] && command -v python3 >/dev/null 2>&1; then
  printf '%s' "$targets" | python3 -c 'import json,sys
d = json.load(sys.stdin)
for t in sorted(d["data"]["activeTargets"], key=lambda x: x["labels"].get("job","")):
    print("  %-20s %-6s %s" % (t["labels"].get("job",""), t["health"].upper(),
                               t["labels"].get("tier","")))'
else
  echo "  (Prometheus unreachable, or python3 missing to format)"
fi

# ---------------------------------------------------------------------------
echo
echo "  External surface (should be Traefik only)"
printf '  %s\n' "--------------------------------------------------------------"
for p in 80 443; do
  printf '  %-20s %s\n' "port $p" "published"
done
echo "  everything else        internal to llm-net"
echo
echo "  Reach services at https://<name>.$DOM"

echo
if [ "$failures" -eq 0 ]; then
  printf '  %sAll enabled components healthy.%s\n' "$c_green" "$c_off"
else
  printf '  %s%s component(s) not responding.%s\n' "$c_red" "$failures" "$c_off"
fi
echo
exit "$failures"
