#!/usr/bin/env bash
# Refuse to start against a bind mount that resolves to nothing.
#
# Run before `docker compose up`; `make up` runs it automatically.
#
# Two checks, because they catch different things:
#
#   planned    -- the mounts the config in THIS directory asks for. Catches a
#                 checkout missing files, e.g. a clone that dropped a config.
#   containers -- the mounts this stack's EXISTING containers actually hold.
#                 This is the one that catches a MOVED CHECKOUT, because those
#                 containers keep the OLD absolute paths no matter which
#                 directory you run from, and Docker has already created empty
#                 root-owned directories there to satisfy them.
#
# The failure is silent and misdiagnoses itself as four unrelated problems --
# the identity provider "read-only file system", Alertmanager missing its alertmanager.yml,
# the temperature exporter missing exporter.py, Traefik exiting 127 -- every one
# of which names a file that plainly exists on the host. The files exist. The
# MOUNT points somewhere else.
#
# scripts/lib/check_mounts.py holds the checks and explains them at length.
set -euo pipefail

cd "$(dirname "$0")/.."

fail() { printf '\033[31m%s\033[0m\n' "$*" >&2; }

if ! command -v docker >/dev/null 2>&1; then
  fail "preflight: docker is not on PATH."
  exit 1
fi

if [ ! -f .env ]; then
  # Several variables are required with `:?` in the compose file, and the
  # failure is a wall of interpolation text that never says ".env is missing".
  fail "preflight: .env is missing."
  printf '           cp env-samples/<pick-one>.env .env   then edit it.\n'
  exit 1
fi

# `config` also validates that every REQUIRED variable is set, so this is the
# first real check -- and its own message is already specific, so pass it
# through rather than paraphrasing it.
if ! rendered="$(docker compose config --format json 2>&1)"; then
  fail "preflight: docker compose could not resolve this stack:"
  printf '%s\n' "$rendered" >&2
  exit 1
fi

tmp_config="$(mktemp)"
tmp_inspect="$(mktemp)"
trap 'rm -f "$tmp_config" "$tmp_inspect"' EXIT

printf '%s' "$rendered" > "$tmp_config"
python3 scripts/lib/check_mounts.py planned "$PWD" "$tmp_config" || exit 1

# `-a` on purpose: a container that cannot start is exactly the one this is
# looking for, and it is the one a plain `ps` would hide.
ids="$(docker compose ps -aq 2>/dev/null || true)"
containers=0
if [ -n "$ids" ]; then
  # Word splitting is the point: `docker inspect` takes a list of ids.
  # shellcheck disable=SC2086
  containers="$(printf '%s\n' $ids | wc -l)"
  docker inspect $ids > "$tmp_inspect" 2>/dev/null || true
  if [ -s "$tmp_inspect" ]; then
    python3 scripts/lib/check_mounts.py containers "$PWD" "$tmp_inspect" || exit 1
  fi
fi

printf 'preflight: ok (%s container(s), all bind mounts resolve to real content)\n' \
  "$containers"
