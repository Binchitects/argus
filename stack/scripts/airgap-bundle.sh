#!/usr/bin/env bash
# Build an offline deployment bundle for an airgapped host.
#
# Run this ON A CONNECTED MACHINE that already runs the stack. It produces one
# zip containing every container image, the deployment tree, and a loader.
#
#   ./scripts/airgap-bundle.sh                       # images + tree (~7 GB measured)
#   ./scripts/airgap-bundle.sh --with-models         # + the GGUF weights (~100 GB)
#   ./scripts/airgap-bundle.sh --with-models --with-packs   # + the knowledge packs
#   ./scripts/airgap-bundle.sh --all-profiles        # every profile, not just yours
#   ./scripts/airgap-bundle.sh --split 4g            # USB / FAT32 friendly parts
#   ./scripts/airgap-bundle.sh --ignore-space        # skip the free-space estimate
#   ./scripts/airgap-bundle.sh --no-images           # tree only, no container images
#   ./scripts/airgap-bundle.sh --with-env            # include the live .env, SECRETS AND ALL
#
# On the target, nothing is downloaded and nothing is built:
#
#   unzip stack-airgap-<date>.zip
#   cd stack-airgap-<date>
#   ./load.sh --up
#
# ---------------------------------------------------------------------------
# What an airgap actually breaks, and what this does about it
# ---------------------------------------------------------------------------
#
# Four things reach the network on a normal first start. Each is handled here
# rather than left to be discovered on a machine that cannot fix it:
#
#   1. Container images. Saved with `docker save`, restored with `docker load`.
#      That includes the three images built locally (argus, admin-panel,
#      identity-proxy) -- shipping them is what lets the target run with no
#      build context, no base images and no registry.
#
#   2. The model weights. `model-init` downloads them from Hugging Face. Worse:
#      it checks the remote size BEFORE deciding a local file is good, so with
#      no network it exits 1 even when every file is already on disk. And
#      `llamacpp` depends on it with `service_completed_successfully`, so that
#      exit 1 stops THE ENGINE FROM STARTING AT ALL, not just the download.
#      Clearing LLAMACPP_HF_FILES is what makes it a no-op; the bundle does
#      that in the .env it ships and says so.
#
#   3. The llama.cpp engine tarball, when LLAMACPP_ENGINE_URL is set. Same
#      reasoning, same fix.
#
#   4. Ollama's embedding model, which lives in a Docker VOLUME rather than a
#      bind mount and is therefore easy to forget. Without it `docs_search`
#      cannot embed a query. Always included; it is small (hundreds of MB).
#
# The bundle is validated before it is zipped: every bind mount the compose
# file asks for is checked to exist inside the staged tree. A missing
# placeholder directory would otherwise surface at the far end as the same
# empty-mount crash loop the preflight exists to catch.
# ---------------------------------------------------------------------------
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "$0")" && pwd)"
STACK_DIR="$(cd "$SCRIPT_DIR/.." && pwd)"
REPO_ROOT="$(cd "$STACK_DIR/.." && pwd)"

OUT_DIR="$REPO_ROOT/dist"
NAME=""
WITH_MODELS=0
WITH_PACKS=0
WITH_ENV=0
ALL_PROFILES=0
WITH_IMAGES=1
CHECKSUMS=1
COMPRESS=0
IGNORE_SPACE=0
SPLIT=""

say()  { printf '%s\n' "$*"; }
step() { printf '\n\033[36m==> %s\033[0m\n' "$*"; }
warn() { printf '\033[33mwarning: %s\033[0m\n' "$*" >&2; }
die()  { printf '\033[31merror: %s\033[0m\n' "$*" >&2; exit 1; }

usage() { sed -n '2,20p' "$0" | sed 's/^# \{0,1\}//'; }


while [ "$#" -gt 0 ]; do
  case "$1" in
    --out)          OUT_DIR="${2:?--out needs a directory}"; shift 2 ;;
    --name)         NAME="${2:?--name needs a value}"; shift 2 ;;
    --with-models)  WITH_MODELS=1; shift ;;
    --with-packs)   WITH_PACKS=1; shift ;;
    --with-env)     WITH_ENV=1; shift ;;
    --all-profiles) ALL_PROFILES=1; shift ;;
    --no-images)    WITH_IMAGES=0; shift ;;
    --no-checksums) CHECKSUMS=0; shift ;;
    --ignore-space) IGNORE_SPACE=1; shift ;;
    --compress)     COMPRESS=1; shift ;;
    --split)        SPLIT="${2:?--split needs a size, e.g. 4g}"; shift 2 ;;
    -h|--help)      usage; exit 0 ;;
    *)              die "unknown option: $1 (try --help)" ;;
  esac
done

command -v docker >/dev/null 2>&1 || die "docker is not on PATH"
command -v python3 >/dev/null 2>&1 || die "python3 is not on PATH"
command -v zip >/dev/null 2>&1 || die "zip is not installed (apt install zip)"
[ -f "$STACK_DIR/.env" ] || die "$STACK_DIR/.env is missing. Run this on a machine that is already deployed."
[ -f "$STACK_DIR/docker-compose.yml" ] || die "docker-compose.yml not found in $STACK_DIR"

[ -n "$NAME" ] || NAME="stack-airgap-$(date +%Y-%m-%d)"
STAGE_PARENT="$OUT_DIR"
STAGE="$STAGE_PARENT/$NAME"
ZIP="$OUT_DIR/$NAME.zip"

COMPOSE=(docker compose)
if [ "$ALL_PROFILES" = 1 ]; then COMPOSE+=(--profile "*"); fi

step "Resolving what this bundle needs"
# `config` also fails loudly on a missing REQUIRED .env value, which is the
# first thing that would make a bundle wrong.
( cd "$STACK_DIR" && "${COMPOSE[@]}" config --quiet ) \
  || die "docker compose could not resolve the stack; fix .env first"

CONFIG_JSON="$(mktemp)"
trap 'rm -f "$CONFIG_JSON"' EXIT
( cd "$STACK_DIR" && "${COMPOSE[@]}" config --format json ) > "$CONFIG_JSON"

IMAGES=()
if [ "$WITH_IMAGES" = 1 ]; then
  while IFS= read -r image; do
    [ -n "$image" ] && IMAGES+=("$image")
  done < <( cd "$STACK_DIR" && "${COMPOSE[@]}" config --images | sort -u )
fi
say "  images:        ${#IMAGES[@]}"
say "  profiles:      $( [ "$ALL_PROFILES" = 1 ] && echo 'all' || echo 'from .env' )"
say "  with models:   $( [ "$WITH_MODELS" = 1 ] && echo yes || echo 'no (--with-models to include)' )"
say "  with packs:    $( [ "$WITH_PACKS" = 1 ] && echo yes || echo 'no (--with-packs to include)' )"

# --------------------------------------------------------------- the images --
if [ "$WITH_IMAGES" = 1 ]; then
  step "Making sure every image exists locally"
  # Build first: the three locally-built images cannot be pulled, and compose
  # skips the ones with no build section.
  ( cd "$STACK_DIR" && "${COMPOSE[@]}" build ) || die "docker compose build failed"

  missing=()
  for image in "${IMAGES[@]}"; do
    docker image inspect "$image" >/dev/null 2>&1 || missing+=("$image")
  done
  if [ "${#missing[@]}" -gt 0 ]; then
    say "  pulling ${#missing[@]} image(s) that are not present locally"
    for image in "${missing[@]}"; do
      say "    $image"
      docker pull "$image" || die "could not pull $image"
    done
  fi
  # Re-check. A typo in a tag, or an image a profile never pulled, must stop
  # the bundle here rather than leave a hole the target discovers later.
  for image in "${IMAGES[@]}"; do
    docker image inspect "$image" >/dev/null 2>&1 || die "image not available: $image"
  done
  say "  all ${#IMAGES[@]} images present"
fi

# ------------------------------------------------------------ the staging --
step "Staging into $STAGE"
rm -rf "$STAGE"
mkdir -p "$STAGE/.airgap"

if [ "$WITH_IMAGES" = 1 ]; then
  mkdir -p "$STAGE/.airgap/images"
  total_bytes=0
  for image in "${IMAGES[@]}"; do
    total_bytes=$(( total_bytes + $(docker image inspect "$image" --format '{{.Size}}') ))
  done
  need_kb=$(( total_bytes / 1024 * 3 / 2 ))
  avail_kb=$(df -Pk "$OUT_DIR" | awk 'NR==2 {print $4}')
  say "  docker reports $(numfmt --to=iec "$total_bytes") of image layers (uncompressed)"
  if [ "$IGNORE_SPACE" = 0 ] && [ "$avail_kb" -lt "$need_kb" ]; then
    # Deliberately conservative. What `docker save` actually writes depends on
    # the daemon: a classic overlay2 store writes the layers more or less as
    # they sit on disk, while Docker's containerd image store writes compressed
    # OCI layers -- measured here, 23 GB of reported layers became a 7 GB zip.
    # Erring high costs a spurious refusal; erring low fills the disk halfway
    # through a 20-minute save. --ignore-space is the way out.
    die "$(printf 'not enough space in %s: this estimate wants ~%s, you have %s.\n       Re-run with --ignore-space if you know better (the real figure is often\n       a third of this on the containerd image store).' \
          "$OUT_DIR" "$(numfmt --to=iec $((need_kb*1024)))" "$(numfmt --to=iec $((avail_kb*1024)))")"
  fi

  i=0
  : > "$STAGE/.airgap/images.list"
  for image in "${IMAGES[@]}"; do
    i=$(( i + 1 ))
    file="$(printf '%s' "$image" | tr '/:@' '___').tar"
    printf '  [%2d/%2d] %-56s' "$i" "${#IMAGES[@]}" "$image"
    docker save -o "$STAGE/.airgap/images/$file" "$image"
    printf ' %s\n' "$(numfmt --to=iec "$(stat -c %s "$STAGE/.airgap/images/$file")")"
    # archive -> image name, so the loader can skip what is already present
    # without unpacking a multi-gigabyte tar to find out what is inside it.
    printf '%s\t%s\n' "$file" "$image" >> "$STAGE/.airgap/images.list"
  done
fi

# --------------------------------------------------------------- the tree --
step "Copying the deployment tree"
# Tracked placeholder files matter as much as real ones: an empty directory
# that a compose bind mount points at is the crash loop this whole exercise is
# trying to avoid. `stack/models/.gitkeep` and the certs/auth/prometheus
# `.gitkeep`s are re-created below because their directories are excluded.
tar_excludes=(
  --exclude='./.git'
  --exclude='./.env'
  --exclude='./.remember'
  --exclude='./.claude'
  --exclude='./.superpowers'
  --exclude='./.pytest_cache'
  --exclude='./.ruff_cache'
  --exclude='./dist'
  --exclude='./scripts/test-gitlab/work'
  --exclude='./stack/.env'
  --exclude='./stack/.env.*'
  --exclude='./stack/backups'
  # The stack's own dist/ holds a previously built drop-in archive (hundreds of
  # MB) and any release tarballs. Neither is needed on the target -- the images
  # this bundle carries are in .airgap/images/ -- and shipping them put a copy
  # of one artifact inside another.
  --exclude='./stack/dist'
  --exclude='./stack/.pkgstage'
  --exclude='./stack/config/traefik/certs/tls.crt'
  --exclude='./stack/config/traefik/certs/bundle.crt'
  --exclude='./stack/config/traefik/auth/users.htpasswd'
  --exclude='./stack/config/authelia/clients.yml'
  --exclude='./stack/config/authelia/users.yml'
  --exclude='./stack/config/authelia/users.yml.bak'
  --exclude='./stack/config/authelia/secrets/*'
  --exclude='./stack/config/prometheus/secrets/llamacpp.token'
  --exclude='*/__pycache__'
  --exclude='*.pyc'
  --exclude='./stack/models/*'
)
# 3.7 GB of built packs, and nothing in the deployment needs them to start.
# Argus falls back to the code index; the docs tools simply have less to say.
# Copied in below, separately, when --with-packs asks for them.
if [ "$WITH_PACKS" = 0 ]; then
  tar_excludes+=(--exclude='./packs')
fi

tar -C "$REPO_ROOT" "${tar_excludes[@]}" -cf - . | tar -C "$STAGE" -xf -
say "  copied $(du -sh "$STAGE" | cut -f1)"

# Generated at first start on the target, but the DIRECTORIES must exist or the
# bind mount creates an empty root-owned one instead.
mkdir -p "$STAGE/stack/config/authelia/secrets"
[ -f "$REPO_ROOT/stack/models/.gitkeep" ] \
  && cp "$REPO_ROOT/stack/models/.gitkeep" "$STAGE/stack/models/.gitkeep"

# ------------------------------------------------------------ big extras --
if [ "$WITH_PACKS" = 1 ]; then
  step "Including the knowledge packs"
  # Excluded from the tree copy above unless this flag is set, because the
  # estate is ~3.7 GB and nothing needs it to start.
  if [ -d "$REPO_ROOT/packs" ] && [ -n "$(ls -A "$REPO_ROOT/packs" 2>/dev/null)" ]; then
    mkdir -p "$STAGE/packs"
    tar -C "$REPO_ROOT" --exclude='*/__pycache__' -cf - packs | tar -C "$STAGE" -xf -
    say "  packs: $(du -sh "$STAGE/packs" | cut -f1)"
  else
    warn "no packs/ content on this machine; nothing to include"
  fi
fi

if [ "$WITH_MODELS" = 1 ]; then
  step "Including the model weights"
  model_dir="$(sed -n 's/^LLAMACPP_MODEL_DIR=//p' "$STACK_DIR/.env" | head -1)"
  [ -n "$model_dir" ] || die "LLAMACPP_MODEL_DIR is not set in .env"
  [ -d "$model_dir" ] || die "LLAMACPP_MODEL_DIR=$model_dir is not a directory"
  mkdir -p "$STAGE/.airgap/models"
  say "  from $model_dir ($(du -sh "$model_dir" | cut -f1)) -- this takes a while"
  # -h/--dereference is deliberate: a model directory is very often a symlink
  # onto another disk, and tar's default would ship the LINK, which is useless
  # on a machine where the target does not exist.
  tar -C "$(dirname "$model_dir")" -h -cf - "$(basename "$model_dir")" \
    | tar -C "$STAGE/.airgap/models" -xf -
  say "  models: $(du -sh "$STAGE/.airgap/models" | cut -f1)"
fi

# ------------------------------------------------------- ollama embeddings --
# A Docker VOLUME, not a bind mount, which is exactly why it gets forgotten:
# nothing in the checkout hints that it exists. Without it docs_search cannot
# embed a query, and on an airgapped host there is no pulling it later.
step "Including Ollama's model volume"
project="$(sed -n 's/^COMPOSE_PROJECT_NAME=//p' "$STACK_DIR/.env" | head -1)"
project="${project:-llmservice}"
ollama_volume="${project}_ollama-models"
if docker volume inspect "$ollama_volume" >/dev/null 2>&1; then
  # A single tar rather than a directory of files. The loader then needs one
  # command to restore it, and can use any image the bundle already carries --
  # no network, and no dependency on a helper image being present.
  ollama_tar="$STAGE/.airgap/ollama-models.tar"
  helper=""
  for candidate in python:3.13-slim alpine:3 redis:7-alpine busybox:latest; do
    if docker image inspect "$candidate" >/dev/null 2>&1; then helper="$candidate"; break; fi
  done
  if [ -z "$helper" ]; then
    warn "no usable image to pack $ollama_volume; docs_search will not work until the embedding model is present"
  elif docker run --rm -v "$ollama_volume":/from:ro "$helper" tar -cf - -C /from . \
        > "$ollama_tar" 2>/dev/null; then
    say "  $ollama_volume: $(du -sh "$ollama_tar" | cut -f1) (via $helper)"
  else
    rm -f "$ollama_tar"
    warn "could not pack $ollama_volume; docs_search will not work until the embedding model is present"
  fi
else
  warn "volume $ollama_volume not found; docs_search will not work until the embedding model is present"
fi

# ------------------------------------------------------------------ .env --
step "Preparing .env for the target"
# Two files, and which one is which matters:
#
#   .env.airgap  always written. The live .env with secrets EMPTIED and the
#                download settings neutralised. Safe to ship, ready to fill in.
#   .env         only with --with-env. The live .env verbatim, secrets and all.
airgap_env="$STAGE/stack/.env.airgap"
secrets_list="$STAGE/.airgap/secrets-to-fill.txt"
: > "$secrets_list"
awk -v list="$secrets_list" '
  /^# SECRETS/ { insecrets = 1 }
  /^# PEOPLE/  { insecrets = 0 }

  /^[A-Z0-9_]+=/ {
    name = $0; sub(/=.*/, "", name)
    value = substr($0, index($0, "=") + 1)

    # Secret by POSITION or by NAME. Position alone is not enough: the SECRETS
    # block ends at PEOPLE, and eight real secrets live past it -- among them
    # ARGUS_GITLAB_TOKEN, which the README calls the most sensitive string in
    # the deployment, and the ClickHouse, MinIO and Langfuse credentials that
    # only exist when those profiles are on. Emitting those into a bundle that
    # travels between sites is exactly the leak this file exists to prevent.
    #
    # `*_USER` is excluded from the position rule: PROXY_AUTH_USER lives in the
    # SECRETS block and is `admin`. It is a username, its value is not secret,
    # and regenerating it would hand the operator a random hex string as their
    # basic-auth login with nothing saying so.
    secretish = (insecrets && name !~ /_USER$/) || name ~ /(TOKEN|SECRET|PASSWORD|_KEY)$/

    # ...but a compose VARIABLE REFERENCE is wiring, not a secret.
    # WEBUI_BACKEND_KEY is literally ${LITELLM_MASTER_KEY}; emptying it would
    # break the stack in a way that reads as a wrong master key.
    if (secretish && value !~ /^\$\{/) {
      if (value != "") print name >> list
      sub(/=.*/, "=")
    }
  }
  { print }
' "$STACK_DIR/.env" > "$airgap_env"
say "  wrote stack/.env.airgap -- $(wc -l < "$secrets_list" | tr -d ' ') secret(s) emptied, downloads disabled"

# Neutralise everything that reaches the network. Done with sed on names, not
# values, so a value containing "=" or a "/" cannot confuse it.
neutralise() {
  sed -i -E "s|^($1)=.*|\1=|" "$airgap_env"
}
neutralise 'LLAMACPP_HF_REPO'
neutralise 'LLAMACPP_HF_FILES'
neutralise 'LLAMACPP_ENGINE_URL'
neutralise 'LLAMACPP_ENGINE_SHA256'
neutralise 'HF_TOKEN'

if [ "$WITH_MODELS" = 1 ]; then
  # Relative on purpose: compose resolves a relative bind source against the
  # compose file's own directory, so the bundle runs from wherever it is
  # unpacked. An absolute path baked in here would be wrong on every target
  # but the one it was built on.
  model_base="$(basename "$(sed -n 's/^LLAMACPP_MODEL_DIR=//p' "$STACK_DIR/.env" | head -1)")"
  sed -i -E "s|^LLAMACPP_MODEL_DIR=.*|LLAMACPP_MODEL_DIR=../.airgap/models/$model_base|" "$airgap_env"
fi

if [ "$WITH_ENV" = 1 ]; then
  cp "$STACK_DIR/.env" "$STAGE/stack/.env"
  chmod 600 "$STAGE/stack/.env"
  warn "--with-env copied the live .env into the bundle. It contains every secret in this deployment."
fi

# ---------------------------------------------------------------- loader --
cp "$SCRIPT_DIR/airgap-load.sh" "$STAGE/load.sh"
cp "$SCRIPT_DIR/airgap-fill-secrets.sh" "$STAGE/fill-secrets.sh"
chmod +x "$STAGE/load.sh" "$STAGE/fill-secrets.sh"

# --------------------------------------------------------- validate bundle --
step "Validating the bundle against every bind mount"
if ! python3 "$SCRIPT_DIR/lib/check_mounts.py" staged "$REPO_ROOT" "$STAGE" "$CONFIG_JSON"; then
  die "the bundle is incomplete; see above. Nothing was zipped."
fi

# -------------------------------------------------------------- checksums --
if [ "$CHECKSUMS" = 1 ] && [ "$WITH_IMAGES" = 1 ]; then
  step "Hashing the image archives (this takes a few minutes)"
  ( cd "$STAGE/.airgap/images" && sha256sum ./*.tar > ../images.sha256 )
  say "  wrote .airgap/images.sha256"
fi

# -------------------------------------------------------------- manifest --
step "Writing the manifest"
{
  echo "# $NAME"
  echo
  echo "Built $(date -u '+%Y-%m-%d %H:%M:%S UTC') on $(hostname)"
  echo
  echo "## Deployment tree"
  echo "commit: $(git -C "$REPO_ROOT" rev-parse HEAD 2>/dev/null || echo 'not a git checkout')"
  if [ -n "$(git -C "$REPO_ROOT" status --porcelain 2>/dev/null)" ]; then
    echo "working tree: DIRTY (uncommitted changes are included)"
  else
    echo "working tree: clean"
  fi
  echo
  # The images above cover exactly the profiles that were on when this ran.
  # Recorded because the failure it prevents is invisible: enable another
  # profile on the target and `docker compose up` stops on a pull that host
  # cannot make, partway through, with the other services already running.
  # load.sh --check compares the two and refuses before anything starts.
  echo "## Profiles covered"
  if [ "$ALL_PROFILES" = 1 ]; then
    echo "all (--all-profiles)"
  else
    echo "$(sed -n 's/^COMPOSE_PROFILES=//p' "$STACK_DIR/.env" | head -1)"
    echo
    echo "Only these. Another profile needs an image this bundle does not have;"
    echo "rebuild with --all-profiles if the target will enable one."
  fi
  echo
  echo "## Images (${#IMAGES[@]})"
  for image in "${IMAGES[@]:-}"; do
    [ -n "$image" ] || continue
    size="$(docker image inspect "$image" --format '{{.Size}}' 2>/dev/null || echo 0)"
    printf '%s  %s\n' "$(numfmt --to=iec "$size" 2>/dev/null || echo '?')" "$image"
  done
  echo
  echo "## Contents"
  # A loop, not `du -sh "$STAGE"/*`: an unmatched glob reaches du as a literal
  # path, du exits non-zero, and `set -o pipefail` turns that into a dead
  # script at the last step before zipping.
  for entry in "$STAGE"/* "$STAGE"/.airgap; do
    [ -e "$entry" ] || continue
    printf '%s  %s\n' "$(du -sh "$entry" 2>/dev/null | cut -f1)" "$(basename "$entry")"
  done
} > "$STAGE/.airgap/manifest.txt"

cat > "$STAGE/AIRGAP-README.md" <<EOF
# Offline deployment bundle

Built $(date -u '+%Y-%m-%d') from \`$(git -C "$REPO_ROOT" rev-parse --short HEAD 2>/dev/null || echo 'a working tree')\`.
Everything needed is in here. The target host needs **no network** after this
arrives.

## On the target

\`\`\`bash
unzip $NAME.zip
cd $NAME

# 1. Make the .env. The airgap version has the download settings already
#    disabled and every secret emptied.
cp stack/.env.airgap stack/.env
./fill-secrets.sh          # generates the ones that can be generated

# 2. Load every image and start. No pull, no build.
./load.sh --up
\`\`\`

Then open \`https://admin.<LLM_DOMAIN>\` and sign in as \`admin\` with
\`AUTHELIA_ADMIN_PASSWORD\` from the .env. The browser warns once about the
self-signed certificate; accept it.

## Secrets this bundle emptied ($(wc -l < "$secrets_list" | tr -d ' '))

\`\`\`
$(sed 's/^/  /' "$secrets_list")
\`\`\`

\`fill-secrets.sh\` generates all of them except the ones that have to come
from outside this deployment, and tells you which those are.

## What is already handled

* **Every container image**, including the three built locally (argus,
  admin-panel, identity-proxy). The target never builds and never pulls.
* **Ollama's embedding model** is in \`.airgap/ollama-models.tar\` and \`load.sh\`
  restores it. It lives in a Docker volume, so it is easy to forget -- and
  without it \`docs_search\` cannot embed a query.
* **\`LLAMACPP_HF_FILES\` and \`LLAMACPP_ENGINE_URL\` are empty** in the shipped
  .env. This is not tidiness. \`model-init\` asks Hugging Face for the file size
  before it will accept a local file, so with no network it exits 1 even when
  every weight is already on disk; and \`llamacpp\` declares it as
  \`service_completed_successfully\`, so that exit 1 means **the engine never
  starts** rather than merely skipping a download.

## What is NOT in here

* \`.git\`, and the test corpus under \`scripts/test-gitlab/work\`.
* Any **secrets** with values in them, unless this bundle was built with
  \`--with-env\` (in which case \`stack/.env\` is the live one, verbatim).
* The **knowledge packs**, unless built with \`--with-packs\`. Argus works
  without them; its docs tools simply answer from the code index alone.
* The **model weights**, unless built with \`--with-models\`. If they are not
  here, \`LLAMACPP_MODEL_DIR\` in the .env still points at the machine this
  bundle was built on, and you must set it to wherever the GGUFs live here.

## If something is wrong

\`\`\`bash
./load.sh --check      # verify checksums, images and bind mounts; change nothing
cd stack && make preflight
\`\`\`
EOF

# -------------------------------------------------------------------- zip --
# Make the payload readable before it is archived.
#
# `docker save -o` creates its output mode 0600 -- Docker's own choice, not the
# umask (measured: umask 0002 here and the tars still came out 0600). zip
# records that mode and unzip restores it, so an operator who unpacks with sudo
# and then runs ./load.sh as their normal user gets "Permission denied" on
# every image archive, on a machine that cannot re-make the bundle. Measured
# exactly that after extracting as root.
#
# The one file that stays 0600 is a live .env, which only --with-env puts
# there and which holds every secret in the deployment.
step "Making the payload readable"
chmod -R a+rX "$STAGE"
[ -f "$STAGE/stack/.env" ] && chmod 600 "$STAGE/stack/.env"
say "  payload is world-readable (a live .env, if shipped, stays 0600)"

step "Zipping"
rm -f "$OUT_DIR/$NAME".zip "$OUT_DIR/$NAME".z[0-9][0-9] "$ZIP"
# Store, not deflate, by default: docker layers are already compressed, so
# deflate burns minutes of CPU to save almost nothing.
zip_opts=(-r -q)
[ "$COMPRESS" = 1 ] || zip_opts+=(-0)
if [ -n "$SPLIT" ]; then zip_opts+=(-s "$SPLIT"); fi
# From OUT_DIR so the archive carries the bundle directory as its root.
( cd "$OUT_DIR" && zip "${zip_opts[@]}" "$NAME.zip" "$NAME" )

# ---------------------------------------------------- read it back ----------
# Prove the archive can be read BEFORE anyone carries it to a machine that
# cannot rebuild it. This is not paranoia, and it is not a formality:
#
# Measured on this project on 2026-09-16. `zip` exited 0, and wrote a 7.8 GB
# file whose central directory sat 4.3 GB from the END, with 78 MB of non-zip
# bytes before the first local header. `unzip` found no
# end-of-central-directory and refused the whole thing. Every prior step had
# reported success -- images saved, sha256s written, the tree validated against
# every bind mount -- because the corruption happens in this last step and
# nothing had ever re-read the result.
#
# An airgap bundle is the one artifact that cannot be re-made where it is
# going. Discovering this on the target, holding a disk, is the worst case
# this script exists to prevent.
step "Verifying the archive reads back"
if [ -z "$SPLIT" ]; then
  if ! unzip -t "$OUT_DIR/$NAME.zip" >/dev/null 2>&1; then
    die "$NAME.zip is NOT a valid archive, though zip exited 0.
  Do not ship this file. The usual cause is the filesystem holding
  $OUT_DIR rather than the contents -- zip reported success while
  writing it. Re-run this script; if it fails again, build the bundle on a
  local (non-network, non-FUSE) filesystem and copy the result."
  fi
  say "  unzip -t: archive is valid"
else
  # Info-ZIP cannot test a split set without joining it first, and joining
  # needs as much free space again as the parts already occupy. Say so rather
  # than implying a check happened.
  for part in "$OUT_DIR/$NAME".z[0-9][0-9] "$OUT_DIR/$NAME.zip"; do
    [ -f "$part" ] || die "missing part: $part"
    [ -s "$part" ] || die "empty part: $part"
  done
  warn "split archive: the parts exist and are non-empty, but a full read-back"
  warn "needs them joined, which needs the space again. Verify after joining:"
  warn "  zip -s 0 $NAME.zip --out ${NAME}-joined.zip && unzip -t ${NAME}-joined.zip"
  warn ""
  warn "The parts MUST be on a WRITABLE filesystem to join. Info-ZIP writes into"
  warn "that directory while joining, and on a read-only mount it exits 0 having"
  warn "produced a 0-byte file -- measured. Copy them somewhere writable first."
fi

step "Done"
# A loop, not `ls <zip> <z01>`: with no --split the .z01 pattern matches
# nothing, `ls` exits 2, and under `set -o pipefail` that turned a bundle that
# had built perfectly into a failure exit -- after the zip was already written.
for part in "$OUT_DIR/$NAME".zip "$OUT_DIR/$NAME".z[0-9][0-9]; do
  [ -f "$part" ] || continue
  printf '  %s  %s\n' "$(basename "$part")" "$(du -h "$part" | cut -f1)"
done
if [ -n "$SPLIT" ]; then
  say ""
  say "  Split into parts: the LAST is $NAME.zip, the earlier ones are .z01, .z02, ..."
  say "  Keep them together -- a missing part is undetectable until extraction."
  say ""
  say "  Info-ZIP's unzip CANNOT extract a split set: it warns about a multi-part"
  say "  archive and produces nothing. Join first, from a WRITABLE directory"
  say "  holding every part -- on a read-only mount the join exits 0 and writes a"
  say "  0-byte file:"
  say "      zip -s 0 $NAME.zip --out ${NAME}-joined.zip"
  say "      unzip -t ${NAME}-joined.zip   # verify BEFORE you trust it"
  say "      unzip ${NAME}-joined.zip"
else
  say ""
  say "  Transfer $OUT_DIR/$NAME.zip to the target and unzip it there."
fi
