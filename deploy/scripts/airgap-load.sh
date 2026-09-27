#!/usr/bin/env bash
# Load an offline bundle and start the stack. Runs ON THE AIRGAPPED HOST.
#
#   ./load.sh --check     verify everything, change nothing
#   ./load.sh             load the images
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
# Images load first, then the preflight runs, and only then does anything
# start. The preflight is the same one
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

# ------------------------------------------- does it cover what will run? --
# The bundle carries the images for the profiles that were ENABLED where it was
# built. Nothing stops the operator on the target from enabling another one --
# by editing COMPOSE_PROFILES, or by copying a different env sample over .env
# -- and then `docker compose up` fails on a pull that an airgapped host cannot
# do. Worse, it fails partway: the services whose images did arrive start, and
# the one that did not is the only thing that says why.
#
# Checked HERE, before anything starts, and against the .env that will actually
# be used. `docker compose config` resolves the interpolation and honours
# COMPOSE_PROFILES, so this compares real image names rather than raw `image:`
# lines, which is what makes it able to see a changed default.
step "Image coverage for the configured profiles"
compose_file="deploy/docker-compose.yml"
if [ ! -f "$compose_file" ]; then
  warn "no $compose_file in this bundle; cannot check image coverage"
else
  # The env that will be used: a real .env if one has been made, else the
  # shipped template -- `--check` is expected to run before anyone has filled
  # the secrets in, and `docker compose config` refuses to render while a `:?`
  # variable is empty. Placeholders are appended for those and nothing else,
  # and only into a throwaway copy.
  cov_env="$(mktemp)"
  env_source=""
  [ -f deploy/.env ] && env_source="deploy/.env"
  [ -n "$env_source" ] || { [ -f deploy/.env.airgap ] && env_source="deploy/.env.airgap"; }
  [ -n "$env_source" ] && cat "$env_source" > "$cov_env"
  for v in $(grep -oE '\$\{[A-Z0-9_]+:\?' "$compose_file" | sed 's/\${//; s/:?//' | sort -u); do
    grep -qE "^${v}=.+" "$cov_env" || printf '%s=placeholder-for-coverage-check\n' "$v" >> "$cov_env"
  done

  needed="$(docker compose --env-file "$cov_env" -f "$compose_file" config 2>/dev/null \
            | sed -n 's/^ *image: *//p' | tr -d '"' | sort -u)"
  rm -f "$cov_env"

  if [ -z "$needed" ]; then
    warn "could not render $compose_file; skipping the coverage check"
  else
    absent=""
    while IFS= read -r img; do
      [ -n "$img" ] || continue
      grep -qF "$(printf '\t%s' "$img")" "$IMAGES_LIST" || absent="$absent $img"
    done <<< "$needed"

    if [ -n "$absent" ]; then
      printf '  the .env asks for image(s) this bundle does not carry:\n' >&2
      for img in $absent; do printf '      %s\n' "$img" >&2; done
      die "this bundle was built for a different set of profiles.
  Its images.list covers $(wc -l < "$IMAGES_LIST" | tr -d ' ') image(s); the .env needs more.
  Either turn those profiles off in COMPOSE_PROFILES, or rebuild the bundle
  with --all-profiles on a machine that can reach a registry."
    fi
    say "  all $(echo "$needed" | wc -l | tr -d ' ') image(s) the .env needs are in this bundle"
  fi
fi


# ------------------------------------------------------------------ .env --
step "Configuration"
env_ready=1
if [ ! -f deploy/.env ]; then
  env_ready=0
  if [ "$CHECK_ONLY" = 1 ]; then
    # Verifying a bundle on arrival is the normal first thing to do, and at
    # that moment the .env has deliberately not been made yet. Refusing to
    # check would make --check useless exactly when it is wanted.
    warn "deploy/.env does not exist yet (expected before setup)"
    warn "create it with:  cp deploy/.env.airgap deploy/.env && ./fill-secrets.sh"
  elif [ -f deploy/.env.airgap ]; then
    die "deploy/.env does not exist yet. Make it from the shipped template:
      cp deploy/.env.airgap deploy/.env
      ./fill-secrets.sh
    then run this again."
  else
    die "deploy/.env does not exist and no .env.airgap was shipped"
  fi
else
  say "  deploy/.env present"
fi

# ------------------------------------------------------------- preflight --
step "Preflight"
if [ "$env_ready" = 0 ]; then
  say "  skipped: it reads .env, which is not there yet"
elif [ "$CHECK_ONLY" = 1 ]; then
  ( cd deploy && ./scripts/preflight.sh ) || exit 1
  say ""
  say "Check complete. Nothing was changed."
  exit 0
else
  ( cd deploy && ./scripts/preflight.sh ) || die "preflight failed; not starting"
fi

# -------------------------------------------------------------------- up --
if [ "$DO_UP" = 1 ]; then
  step "Starting"
  # --no-build is the airgap guarantee: if an image were missing we want a clear
  # "image not found", not a build that tries to reach a registry.
  ( cd deploy && docker compose up -d --no-build )
  say ""
  ( cd deploy && docker compose ps --format 'table {{.Name}}\t{{.Status}}' )
  say ""
  say "Watch the model come up:  cd deploy && docker logs -f model-init"
  say "Then open https://<LLM_DOMAIN> (see LLM_DOMAIN in deploy/.env) and sign in as admin."
else
  say ""
  say "Images are loaded."
  say "Start the stack with:"
  say "    ./load.sh --up"
  say "or:"
  say "    cd deploy && make up"
fi
