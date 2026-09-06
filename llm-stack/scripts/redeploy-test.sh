#!/usr/bin/env bash
# Deploy the whole stack from zero, repeatedly, and report whether it works.
#
# The question this answers is not "does it run on my machine" -- it is "does a
# clean checkout come up unattended, every time". That distinction matters when
# the stack ships as an offline appliance, because the first person to run it
# has no internet and no author to ask.
#
# Each cycle:
#   1. docker compose down -v          (containers AND stateful volumes)
#   2. delete generated config          (.env, certs, authelia secrets/users)
#   3. scripts/setup.sh --defaults      (unattended, with any --set you passed)
#   4. wait for the engine to be healthy
#   5. scripts/e2e-check.py             (the objective pass/fail)
#
#   ./scripts/redeploy-test.sh --runs 3 --domain llm.localhost \
#       --set VLLM_MODEL=/models/Qwen3.8-9B-AWQ \
#       --set VLLM_SERVED_MODEL_NAME=qwen3.8-9b
#
# WHAT IT DESTROYS: the LiteLLM database (API keys, users, spend history), Open
# WebUI accounts and chat history, Prometheus/Grafana/Authelia state. Take a
# backup first -- scripts/backup.sh.
#
# WHAT IT KEEPS: model weights. `models/` is a bind mount and the hf-cache /
# vllm-cache volumes are declared `external`, so `down -v` cannot remove them.
# A cycle re-downloads nothing.
set -uo pipefail
ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$ROOT"

# Git Bash rewrites anything that looks like a path, which corrupts container
# paths in -v mounts. Same reason setup.sh and fetch-model.sh set it.
export MSYS_NO_PATHCONV=1 MSYS2_ARG_CONV_EXCL='*'

RUNS=3
DOMAIN=""
SETS=()
KEEP_GOING=0
while [[ $# -gt 0 ]]; do
  case "$1" in
    --runs)   RUNS="$2"; shift 2 ;;
    --domain) DOMAIN="$2"; shift 2 ;;
    --set)    SETS+=("$2"); shift 2 ;;
    --keep-going) KEEP_GOING=1; shift ;;   # do not stop on the first failure
    -h|--help) sed -n '2,26p' "$0"; exit 0 ;;
    *) echo "unknown option: $1" >&2; exit 2 ;;
  esac
done

c_green=$'\033[32m'; c_red=$'\033[31m'; c_dim=$'\033[90m'
c_bold=$'\033[1m'; c_off=$'\033[0m'
say()  { printf '%s\n' "$*"; }
head_() { printf '\n%s=== %s ===%s\n' "$c_bold" "$1" "$c_off"; }

REPORT="$ROOT/redeploy-report.txt"
: > "$REPORT"
pass_count=0

for run in $(seq 1 "$RUNS"); do
  head_ "run $run of $RUNS"
  t_start=$(date +%s)

  # --- 1. tear down -------------------------------------------------------
  say "  tearing down (containers + stateful volumes)"
  docker compose --profile '*' down -v --remove-orphans >/dev/null 2>&1 || true

  # --- 2. remove generated config ----------------------------------------
  # This is what makes it "from zero": a fresh checkout has none of these.
  say "  removing generated config"
  rm -f  .env
  # Delete the GENERATED files, not the directories: config/traefik/certs holds
  # a tracked .gitkeep, and removing the directory deletes it from the working
  # tree -- which shows up later as a phantom deletion in git status.
  rm -f  config/traefik/certs/*.crt config/traefik/certs/*.key config/traefik/certs/*.srl
  rm -rf config/authelia/secrets
  rm -f  config/authelia/users.yml

  # --- 3. unattended setup ------------------------------------------------
  say "  running setup.sh --defaults"
  args=(--defaults)
  [[ -n "$DOMAIN" ]] && args+=(--domain "$DOMAIN")
  for kv in ${SETS+"${SETS[@]}"}; do args+=(--set "$kv"); done
  t_setup0=$(date +%s)
  if ! bash scripts/setup.sh "${args[@]}" > "$ROOT/.redeploy-setup.log" 2>&1; then
    say "  ${c_red}setup.sh FAILED${c_off} (tail below)"
    tail -15 "$ROOT/.redeploy-setup.log" | sed 's/^/      /'
    printf 'run %d: FAIL at setup\n' "$run" >> "$REPORT"
    [[ $KEEP_GOING -eq 1 ]] || exit 1
    continue
  fi
  t_setup=$(( $(date +%s) - t_setup0 ))
  say "  setup completed in ${t_setup}s"

  # --- 4. wait for the engine --------------------------------------------
  say "  waiting for vllm to report healthy"
  t_health0=$(date +%s)
  healthy=0
  for _ in $(seq 1 60); do
    s="$(docker compose ps vllm --format '{{.Status}}' 2>/dev/null)"
    case "$s" in *healthy*) healthy=1; break ;; esac
    sleep 15
  done
  t_health=$(( $(date +%s) - t_health0 ))
  if [[ $healthy -eq 0 ]]; then
    say "  ${c_red}engine never became healthy${c_off} after ${t_health}s"
    docker compose logs vllm --tail 20 2>&1 | sed 's/^/      /'
    printf 'run %d: FAIL engine unhealthy after %ds\n' "$run" "$t_health" >> "$REPORT"
    [[ $KEEP_GOING -eq 1 ]] || exit 1
    continue
  fi
  say "  engine healthy in ${t_health}s"

  # --- 5. the objective gate ---------------------------------------------
  say "  running e2e-check"
  MK="$(grep -E '^LITELLM_MASTER_KEY=' .env | cut -d= -f2-)"
  VK="$(grep -E '^VLLM_API_KEY='       .env | cut -d= -f2-)"
  e2e_out="$(docker run --rm --network llm-net \
      --add-host=host.docker.internal:host-gateway \
      -e MK="$MK" -e VLLM_API_KEY="$VK" \
      -v "$ROOT/scripts:/s:ro" python:3.13-slim python /s/e2e-check.py 2>&1)"
  verdict="$(printf '%s' "$e2e_out" | grep -oE '[0-9]+/[0-9]+ checks passed' | tail -1)"
  total=$(( $(date +%s) - t_start ))

  if printf '%s' "$e2e_out" | grep -q 'FAIL'; then
    say "  ${c_red}e2e FAILED${c_off}: ${verdict:-no verdict}"
    printf '%s' "$e2e_out" | grep -E 'FAIL' | sed 's/^/      /'
    printf 'run %d: FAIL e2e %s (setup %ds, health %ds, total %ds)\n' \
      "$run" "${verdict:-?}" "$t_setup" "$t_health" "$total" >> "$REPORT"
    [[ $KEEP_GOING -eq 1 ]] || exit 1
  else
    say "  ${c_green}PASS${c_off} ${verdict} ${c_dim}(setup ${t_setup}s, health ${t_health}s, total ${total}s)${c_off}"
    printf 'run %d: PASS %s (setup %ds, health %ds, total %ds)\n' \
      "$run" "$verdict" "$t_setup" "$t_health" "$total" >> "$REPORT"
    pass_count=$(( pass_count + 1 ))
  fi
done

head_ "summary"
cat "$REPORT" | sed 's/^/  /'
printf '\n  %s%d of %d clean deploys passed%s\n\n' \
  "$([[ $pass_count -eq $RUNS ]] && printf '%s' "$c_green" || printf '%s' "$c_red")" \
  "$pass_count" "$RUNS" "$c_off"
[[ $pass_count -eq $RUNS ]]
