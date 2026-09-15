#!/usr/bin/env bash
# Load an offline bundle and start the stack. Runs ON THE AIRGAPPED HOST.
#
#   ./load.sh --check     verify everything, change nothing
#   ./load.sh             load the images and restore Ollama's models
#   ./load.sh --up        ...and then start the stack
#   ./load.sh --force     reload images even if they are already present
#
# No network access is used or needed at any point: every image comes from a
# tarball in this bundle, and nothing is built.
#
# ---------------------------------------------------------------------------
# The order matters, and so does the .env
# ---------------------------------------------------------------------------
#
# The stack cannot start without an .env, and the .env must be made HERE rather
# than shipped -- the bundle carries .env.airgap with the secrets emptied and
# the download settings already disabled. See AIRGAP-README.md.
#
# Images load first, then Ollama's model volume is restored, then the preflight
# runs, and only then does anything start. The preflight is the same one
# `make up` runs: it checks that every bind mount resolves to real content
# before compose creates empty directories where the real ones should be.
# ---------------------------------------------------------------------------
set -euo pipefail

BUNDLE_DIR="$(cd "$(dirname "$0")" && pwd)"
cd "$BUNDLE_DIR"

AIRGAP="$BUNDLE_DIR/.airgap"
IMAGES_DIR="$AIRGAP/images"
IMAGES_LIST="$AIRGAP/images.list"
CHECKSUMS="$AIRGAP/images.sha256"
OLLAMA_TAR="$AIRGAP/ollama-models.tar"

CHECK_ONLY=0
DO_UP=0
FORCE=0
VERIFY=1

say()  { printf '%s\n' "$*"; }
step() { printf '\n\033[36m==> %s\033[0m\n' "$*"; }
warn() { printf '\033[33mwarning: %s\033[0m\n' "$*" >&2; }
die()  { printf '\033[31merror: %s\033[0m\n' "$*" >&2; exit 1; }

while [ "$#" -gt 0 ]; do
  case "$1" in
    --check)         CHECK_ONLY=1; shift ;;
    --up)            DO_UP=1; shift ;;
    --force)         FORCE=1; shift ;;
    --no-checksums)  VERIFY=0; shift ;;
    -h|--help)       sed -n '2,10p' "$0" | sed 's/^# \{0,1\}//'; exit 0 ;;
    *)               die "unknown option: $1" ;;
  esac
done

command -v docker >/dev/null 2>&1 || die "docker is not on PATH"

# ------------------------------------------------------------- checksums --
if [ "$VERIFY" = 1 ] && [ -f "$CHECKSUMS" ]; then
  step "Verifying image checksums"
  # Long, and worth it: this is the only moment at which a truncated transfer
  # can be told apart from a working bundle. `docker load` on a half-copied
  # tarball fails with a message about an unexpected EOF, not about the copy.
  ( cd "$IMAGES_DIR" && sha256sum -c --quiet ../images.sha256 ) \
    || die "checksum mismatch. The transfer is incomplete or corrupt; re-copy the bundle."
  say "  all archives match"
fi

# ---------------------------------------------------------------- images --
step "Container images"
if [ ! -d "$IMAGES_DIR" ]; then
  warn "no .airgap/images directory; this bundle was built with --no-images"
else
  loaded=0
  skipped=0
  failed=0
  while IFS=$'\t' read -r file image; do
    [ -n "${file:-}" ] || continue
    [ -f "$IMAGES_DIR/$file" ] || { warn "missing archive: $file"; failed=$((failed+1)); continue; }

    if [ "$FORCE" = 0 ] && docker image inspect "$image" >/dev/null 2>&1; then
      skipped=$((skipped+1))
      continue
    fi
    if [ "$CHECK_ONLY" = 1 ]; then
      docker image inspect "$image" >/dev/null 2>&1 || { warn "not loaded: $image"; failed=$((failed+1)); }
      continue
    fi
    printf '  loading %-58s' "$image"
    if docker load -i "$IMAGES_DIR/$file" >/dev/null 2>&1; then
      printf 'ok\n'
      loaded=$((loaded+1))
    else
      printf 'FAILED\n'
      failed=$((failed+1))
    fi
  done < "$IMAGES_LIST"

  say "  loaded $loaded, already present $skipped, failed $failed"
  [ "$failed" -eq 0 ] || die "$failed image(s) could not be loaded"
fi

# ------------------------------------------------------- ollama's volume --
# It is a VOLUME, not a bind mount, so `docker compose up` cannot create it
# from the checkout and nothing in the tree hints that it is missing. Without
# it docs_search cannot embed a query, and the embedding model cannot be
# fetched afterwards.
step "Ollama model volume"
if [ ! -f "$OLLAMA_TAR" ]; then
  warn "no ollama-models.tar in this bundle; skipping"
else
  # From .env when it exists, else from the shipped template -- `--check` is
  # expected to run before anyone has made the .env, and the project name is
  # not a secret.
  #
  # The explicit -f guard is not decoration. `sed` on a missing file exits 2,
  # and with `set -e` plus `pipefail` that kills the whole script from inside a
  # command substitution: no error, no message, just a step heading and a
  # prompt. That is exactly what this cost the first time it was run.
  volume_source=""
  if [ -f llm-stack/.env ]; then
    volume_source="llm-stack/.env"
  elif [ -f llm-stack/.env.airgap ]; then
    volume_source="llm-stack/.env.airgap"
  fi

  volume=""
  if [ -n "$volume_source" ]; then
    volume="$(sed -n 's/^COMPOSE_PROJECT_NAME=//p' "$volume_source" | head -1)"
  fi
  volume="${volume:-llmservice}_ollama-models"

  if [ "$CHECK_ONLY" = 0 ] || [ "$FORCE" = 1 ]; then
    docker volume create "$volume" >/dev/null

    # A helper image that is IN the bundle, so this needs no pull either.
    # python:3.13-slim is the first choice because the stack already runs it;
    # alpine is the fallback for a bundle built with --no-images.
    helper=""
    for candidate in python:3.13-slim alpine:3 redis:7-alpine; do
      if docker image inspect "$candidate" >/dev/null 2>&1; then helper="$candidate"; break; fi
    done

    if [ -z "$helper" ]; then
      warn "no helper image available to unpack $OLLAMA_TAR into volume $volume"
      warn "do it by hand once any image is loaded:"
      warn "  docker volume create $volume"
      warn "  docker run --rm -v $volume:/to -v $BUNDLE_DIR/.airgap:/from:ro alpine:3 tar -xf /from/ollama-models.tar -C /to"
    else
      docker run --rm -v "$volume":/to -v "$AIRGAP":/from:ro "$helper" \
        tar -xf /from/ollama-models.tar -C /to
      say "  restored into $volume"
    fi
  else
    docker volume inspect "$volume" >/dev/null 2>&1 \
      && say "  $volume exists" || warn "$volume does not exist yet"
  fi
fi

# ------------------------------------------------------------------ .env --
step "Configuration"
env_ready=1
if [ ! -f llm-stack/.env ]; then
  env_ready=0
  if [ "$CHECK_ONLY" = 1 ]; then
    # Verifying a bundle on arrival is the normal first thing to do, and at
    # that moment the .env has deliberately not been made yet. Refusing to
    # check would make --check useless exactly when it is wanted.
    warn "llm-stack/.env does not exist yet (expected before setup)"
    warn "create it with:  cp llm-stack/.env.airgap llm-stack/.env && ./fill-secrets.sh"
  elif [ -f llm-stack/.env.airgap ]; then
    die "llm-stack/.env does not exist yet. Make it from the shipped template:
      cp llm-stack/.env.airgap llm-stack/.env
      ./fill-secrets.sh
    then run this again."
  else
    die "llm-stack/.env does not exist and no .env.airgap was shipped"
  fi
else
  say "  llm-stack/.env present"
fi

# ------------------------------------------------------------- preflight --
step "Preflight"
if [ "$env_ready" = 0 ]; then
  say "  skipped: it reads .env, which is not there yet"
elif [ "$CHECK_ONLY" = 1 ]; then
  ( cd llm-stack && ./scripts/preflight.sh ) || exit 1
  say ""
  say "Check complete. Nothing was changed."
  exit 0
else
  ( cd llm-stack && ./scripts/preflight.sh ) || die "preflight failed; not starting"
fi

# -------------------------------------------------------------------- up --
if [ "$DO_UP" = 1 ]; then
  step "Starting"
  # --no-build is the airgap guarantee: if an image were missing we want a clear
  # "image not found", not a build that tries to reach a registry.
  ( cd llm-stack && docker compose up -d --no-build )
  say ""
  ( cd llm-stack && docker compose ps --format 'table {{.Name}}\t{{.Status}}' )
  say ""
  say "Watch the model come up:  cd llm-stack && docker logs -f model-init"
  say "Then open https://admin.<LLM_DOMAIN> (see LLM_DOMAIN in llm-stack/.env)."
else
  say ""
  say "Images are loaded and Ollama's models are in place."
  say "Start the stack with:"
  say "    ./load.sh --up"
  say "or:"
  say "    cd llm-stack && make up"
fi
