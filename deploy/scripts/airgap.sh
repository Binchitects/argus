#!/usr/bin/env bash
# One bundle for a host with no network: every image, this folder, the models and
# the speech server's models, each checked against its SHA-256 before it is used.
#
#   ./scripts/airgap.sh pack [--models] OUT.tar         on a host where the stack runs
#   ./scripts/airgap.sh load [--into DIR] BUNDLE.tar    on the host with no network
#
#   --models          pack: copy the models too (without it the bundle lists them)
#   --models-dir DIR  the model library (default: MODELS_DIR in .env, else ./models)
#   --into DIR        load: where deploy/ goes (default ./arena, so ./arena/deploy)
#   --podman          Podman's images and volumes in place of Docker's
#   --stage DIR       pack: the bundle's files in DIR/arena-airgap, not a tar (make-installer.sh)
#   --dry-run         the plan; nothing is written, loaded or run
#
# COMPOSE_FILE, when set, names the compose files (Podman too); else docker-compose.yml,
# with podman.yml for Podman, and docker-compose.override.yml when there is one.
#
# A bundle is one tar holding arena-airgap/:
#   MANIFEST         format, version, commit, when, the engine, MODEL for .env
#   SHA256SUMS       a checksum for every other file
#   images/          every image the compose files name (docker save), one file each,
#                    and IMAGES: reference, file, image id
#   deploy/          this folder without .env, backups, the models, certs/ (keys) and
#                    config/traefik/certificate.yml (it names those keys)
#   models/MODELS    the library's files the app registered (Admin -> Models) and the
#                    picture, video, embedding and decision servers read: kind, bytes, path.
#                    With --models the files themselves are in models/library/.
#   audio/           the speech server's models (the arena_audio volume)
# pack also writes OUT.tar.sha256, to check the copy that reaches the other host.
#
# load verifies every checksum before it changes anything, then loads the images
# (nothing is ever pulled), unpacks deploy/ (an .env, override, certificates or
# backups already there are kept), moves the models into the library, fills the
# arena_audio volume, and prints what to put in .env. MODEL is printed as a file
# in the library: the app adds it from there and never asks Hugging Face.
set -uo pipefail
ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
# As this folder's own files are: the containers' users read what is mounted from it.
umask 022

say() { printf '%s\n' "$*"; }
die() { printf 'airgap: ERROR: %s\n' "$*" >&2; exit 1; }
usage_error() { printf 'airgap: %s (see --help)\n' "$*" >&2; exit 2; }
usage() { awk 'NR > 1 && !/^#/ { exit } NR > 1 { sub(/^# ?/, ""); print }' "$0"; exit 0; }
env_get() { grep -E "^$1=" "${2:-$ROOT/.env}" 2>/dev/null | tail -n1 | cut -d= -f2- | sed -e 's/^"//' -e 's/"$//'; }

FORMAT="arena-airgap 1"
TOP=arena-airgap
PROJECT="${COMPOSE_PROJECT_NAME:-arena}"
# In the compose file already (cpu-temp-exporter), so it is in every bundle.
HELPER=python:3.13-slim
# The picture, video, embedding and decision (Laya) servers' files, as src/Llm.Api/Models/MediaModels.cs names them.
MEDIA_FILES="
picture image/flux2-klein-4b/flux-2-klein-4b-Q4_0.gguf
picture image/flux2-klein-4b/Qwen3-4B-Q4_K_M.gguf
picture image/flux2-klein-4b/flux2-vae.safetensors
video video/wan2.2-ti2v-5b/Wan2.2-TI2V-5B-Q4_K_M.gguf
video video/wan2.2-ti2v-5b/umt5-xxl-encoder-Q4_K_M.gguf
video video/wan2.2-ti2v-5b/wan2.2_vae.safetensors
embedding embed/nomic-embed-text-v1.5.f16.gguf
decision laya/english/rl_agent_config.json
decision laya/english/model.safetensors
decision laya/english/encoder/config.json
decision laya/english/tokenizer/tokenizer.json
decision laya/english/tokenizer/tokenizer_config.json
decision laya/multilingual/rl_agent_config.json
decision laya/multilingual/model.safetensors
decision laya/multilingual/encoder/config.json
decision laya/multilingual/tokenizer/tokenizer.json
decision laya/multilingual/tokenizer/tokenizer_config.json
"

ACTION="${1:-}"
[[ $# -gt 0 ]] && shift
case "$ACTION" in
  pack|load) ;;
  -h|--help) usage ;;
  "") usage_error "say pack or load" ;;
  *) usage_error "unknown action: $ACTION" ;;
esac
ENGINE=docker; WITH_MODELS=0; DRY=0; INTO=""; MODELS_ARG=""; TARGET=""; STAGE_ARG=""
while [[ $# -gt 0 ]]; do
  case "$1" in
    --models) WITH_MODELS=1 ;;
    --podman) ENGINE=podman ;;
    --dry-run) DRY=1 ;;
    --into) [[ $# -gt 1 ]] || usage_error "--into needs a directory"; INTO="$2"; shift ;;
    --models-dir) [[ $# -gt 1 ]] || usage_error "--models-dir needs a directory"; MODELS_ARG="$2"; shift ;;
    --stage) [[ $# -gt 1 ]] || usage_error "--stage needs a directory"; STAGE_ARG="$2"; shift ;;
    -h|--help) usage ;;
    -*) usage_error "unknown option: $1" ;;
    *) [[ -z "$TARGET" ]] || usage_error "one bundle at a time"; TARGET="$1" ;;
  esac
  shift
done
if [[ -n "$STAGE_ARG" ]]; then
  [[ $ACTION == pack ]] || usage_error "--stage is for pack"
  [[ -z "$TARGET" ]] || usage_error "--stage writes a folder, not $TARGET"
  [[ -d "$STAGE_ARG" ]] || usage_error "no folder $STAGE_ARG"
  [[ -e "$STAGE_ARG/arena-airgap" ]] && usage_error "$STAGE_ARG/arena-airgap is there already"
  # Only for the plan's and the messages' sake: nothing is written there.
  TARGET="$STAGE_ARG/arena-airgap.tar"
fi
[[ -n "$TARGET" ]] || usage_error "$ACTION needs a bundle: $ACTION ... FILE.tar"
[[ "$TARGET" == *.tar ]] || usage_error "the bundle is a .tar: $TARGET"
[[ $ACTION == pack && -n "$INTO" ]] && usage_error "--into is for load"
[[ $ACTION == load && $WITH_MODELS -eq 1 ]] && usage_error "--models is for pack"
command -v "$ENGINE" >/dev/null || die "$ENGINE is not installed"

abspath() { case "$1" in /*) printf '%s' "$1" ;; *) printf '%s/%s' "$PWD" "${1#./}" ;; esac; }
human() { awk -v b="${1:-0}" 'BEGIN { split("B KB MB GB TB", u); i = 1; while (b >= 1024 && i < 5) { b /= 1024; i++ } printf (i == 1 ? "%d %s" : "%.1f %s"), b, u[i] }'; }
# An image reference as a file name.
image_file() { printf '%s.tar' "$(printf '%s' "$1" | tr '/:@' '___')"; }

# Every file of a bundle is listed in SHA256SUMS and matches it; nothing else is there.
verify_bundle() {   # verify_bundle <bundle dir>
  local dir=$1 listed present
  [[ -f "$dir/MANIFEST" && -f "$dir/SHA256SUMS" ]] || { say "  no MANIFEST or SHA256SUMS: not a bundle"; return 1; }
  grep -qx "format: $FORMAT" "$dir/MANIFEST" || { say "  $(grep '^format:' "$dir/MANIFEST" || echo 'no format line'): not a bundle this script reads"; return 1; }
  (cd "$dir" && sha256sum -c --quiet --strict SHA256SUMS) || { say "  CHECKSUM MISMATCH: the bundle is damaged"; return 1; }
  listed="$(sed -E 's/^[0-9a-f]{64} [ *]//' "$dir/SHA256SUMS" | sort)"
  present="$(cd "$dir" && find . -type f ! -name SHA256SUMS -printf '%P\n' | sort)"
  if [[ "$listed" != "$present" ]]; then
    say "  files not in SHA256SUMS: $(comm -13 <(printf '%s\n' "$listed") <(printf '%s\n' "$present") | tr '\n' ' ')"
    return 1
  fi
  return 0
}

# ============================================================================ pack
if [[ $ACTION == pack ]]; then
  OUT="$(abspath "$TARGET")"
  [[ -d "$(dirname "$OUT")" ]] || die "no folder $(dirname "$OUT")"
  [[ -e "$OUT" ]] && die "$OUT is there already"
  cd "$ROOT"
  MODELS_DIR="${MODELS_ARG:-$(env_get MODELS_DIR)}"; MODELS_DIR="${MODELS_DIR:-./models}"
  case "$MODELS_DIR" in /*) ;; *) MODELS_DIR="$ROOT/${MODELS_DIR#./}" ;; esac
  BACKUP_DIR="$(env_get BACKUP_DIR)"; BACKUP_DIR="${BACKUP_DIR:-./backups}"
  case "$BACKUP_DIR" in /*) ;; *) BACKUP_DIR="$ROOT/${BACKUP_DIR#./}" ;; esac

  # The images the stack runs, as compose resolves them (modules left out are not named).
  # The secrets only fill the file in: an image never depends on them.
  list_images() {
    local -a cmd envf=()
    if [[ $ENGINE == podman && -z "${COMPOSE_FILE:-}" ]]; then
      cmd=(podman compose -f docker-compose.yml -f podman.yml)
      [[ -f docker-compose.override.yml ]] && cmd+=(-f docker-compose.override.yml)
    elif [[ $ENGINE == podman ]]; then
      cmd=(podman compose)
    else
      cmd=(docker compose)
    fi
    if [[ ! -f .env ]]; then
      PLACEHOLDER_ENV="$(mktemp)"
      printf '%s=placeholder\n' APP_KEY DB_PASSWORD GATEWAY_KEY ENGINE_KEY ARGUS_KEY > "$PLACEHOLDER_ENV"
      envf=(--env-file "$PLACEHOLDER_ENV")
    fi
    "${cmd[@]}" "${envf[@]}" config --images | sed '/^$/d' | sort -u
    local rc=${PIPESTATUS[0]}
    [[ -n "${PLACEHOLDER_ENV:-}" ]] && rm -f "$PLACEHOLDER_ENV"
    return "$rc"
  }

  # This folder's files, relative to it, without secrets, backups, models or keys.
  deploy_files() {
    local -a prune=(-name backups -o -name certs -o -name .deploytest -o -name __pycache__ -o -name .cache -o -name .git -o -name '.airgap-*')
    local d
    for d in "$BACKUP_DIR" "$MODELS_DIR"; do
      [[ "$d" == "$ROOT"/* ]] && prune+=(-o -path "./${d#"$ROOT"/}")
    done
    {
      # Through links: config files may be links to where the live ones are.
      find -L . \( "${prune[@]}" \) -prune -o -type f -print
      [[ -f certs/README.md ]] && echo ./certs/README.md
      [[ -f models/.gitkeep ]] && echo ./models/.gitkeep
    } | sed 's|^\./||' | awk -F/ '
      $NF == ".env" { next }
      $NF ~ /^\.env\./ && $NF != ".env.example" { next }
      $0 == "config/traefik/certificate.yml" { next }
      $NF ~ /\.py[co]$/ || $NF ~ /\.tar(\.sha256|\.part)?$/ { next }
      { print }' | sort -u
  }

  # A file the app writes into the engine volume (models.ini: the models it registered;
  # keep: those kept loaded), read through a container that is removed at once.
  # Only when the volume is there: a run would make it.
  engine_file() {
    "$ENGINE" volume inspect "${PROJECT}_engine" >/dev/null 2>&1 || return 1
    "$ENGINE" run --rm --network none -v "${PROJECT}_engine:/engine:ro" "$HELPER" cat "/engine/$1" 2>/dev/null
  }

  # kind<TAB>path for each file of the library the stack reads, the chat models' from models.ini.
  library_files() {
    local ini=$1 key path base
    while IFS= read -r line; do
      key="${line%%=*}"; key="${key//[[:space:]]/}"
      path="${line#*=}"; path="${path#"${path%%[![:space:]]*}"}"; path="${path%"${path##*[![:space:]]}"}"
      case "$path" in /library/*) path="${path#/library/}" ;; *) say "  skipped $path: not in the library" >&2; continue ;; esac
      case "$key" in model) kind=chat ;; mmproj) kind=projector ;; *) kind=draft ;; esac
      if [[ "$path" =~ ^(.*)-00001-of-([0-9]{5})\.gguf$ ]]; then
        # A model in parts: the engine is given the first and reads the others beside it.
        base="${BASH_REMATCH[1]}"
        for p in $(seq -f '%05g' 1 "$((10#${BASH_REMATCH[2]}))"); do printf '%s\t%s\n' "$kind" "$base-$p-of-${BASH_REMATCH[2]}.gguf"; done
      else
        printf '%s\t%s\n' "$kind" "$path"
      fi
    done < <(grep -E '^[[:space:]]*(model|mmproj|model-draft)[[:space:]]*=' <<<"$ini")
    while read -r kind path; do
      [[ -n "$path" && -f "$MODELS_DIR/$path" ]] && printf '%s\t%s\n' "$kind" "$path"
    done <<<"$MEDIA_FILES"
  }

  # MODEL for the other host's .env: the first model kept loaded, else the first listed.
  first_model() {
    local ini=$1 kept=$2
    awk -v want="$kept" '
      /^\[/ { name = substr($0, 2, length($0) - 2); next }
      /^[[:space:]]*model[[:space:]]*=/ { sub(/^[^=]*=[[:space:]]*/, ""); sub(/[[:space:]]+$/, ""); sub(/^\/library\//, "")
        if (first == "") first = $0
        if (want != "" && name == want && chosen == "") chosen = $0 }
      END { print (chosen != "" ? chosen : first) }' <<<"$ini"
  }

  mapfile -t IMAGES < <(list_images)
  [[ ${#IMAGES[@]} -gt 0 ]] || die "compose names no images (is docker-compose.yml here?)"
  # backup.sh's helper and this script's, even with cpu-temp-exporter left out.
  printf '%s\n' "${IMAGES[@]}" | grep -qxF "$HELPER" || IMAGES+=("$HELPER")
  mapfile -t FILES < <(deploy_files)

  if [[ $DRY -eq 1 ]]; then
    say "Would pack ${STAGE_ARG:+into }$([[ -n "$STAGE_ARG" ]] && echo "$STAGE_ARG/arena-airgap" || echo "$OUT") (dry run: nothing is written)"
    say "  images (${#IMAGES[@]}), each saved with $ENGINE save:"
    for ref in "${IMAGES[@]}"; do
      if "$ENGINE" image inspect "$ref" >/dev/null 2>&1; then say "    $ref"; else say "    $ref   NOT ON THIS HOST: build or pull it first"; fi
    done
    say "  deploy/: ${#FILES[@]} files (left out: .env, backups, models, certs/ and config/traefik/certificate.yml)"
    say "  models: the files in $MODELS_DIR the app registered (the ${PROJECT}_engine volume's models.ini) and the picture, video, embedding and decision files there,"
    if [[ $WITH_MODELS -eq 1 ]]; then say "    copied into the bundle (--models)"; else say "    listed in models/MODELS (--models copies them)"; fi
    if "$ENGINE" volume inspect "${PROJECT}_audio" >/dev/null 2>&1; then say "  audio: the ${PROJECT}_audio volume (the speech server's models)"; else say "  audio: no ${PROJECT}_audio volume: the bundle will have no speech models"; fi
    say "  then MANIFEST, SHA256SUMS and $(basename "$OUT").sha256"
    exit 0
  fi

  if [[ -n "$STAGE_ARG" ]]; then
    STAGE="$(cd "$STAGE_ARG" && pwd)"
  else
    STAGE="$(mktemp -d "$(dirname "$OUT")/.airgap-pack.XXXXXX")" || die "cannot make a work folder beside $OUT"
    trap 'rm -rf "$STAGE"' EXIT
  fi
  B="$STAGE/$TOP"
  mkdir -p "$B/images" "$B/deploy" "$B/models" "$B/audio" || die "cannot write in $STAGE"
  if [[ -n "$STAGE_ARG" ]]; then say "airgap: packing into $B"; else say "airgap: packing $OUT"; fi

  say "==> images"
  : > "$B/images/IMAGES"
  # Podman's progress lines are noise in a log; Docker's save prints none.
  QUIET=(); [[ $ENGINE == podman ]] && QUIET=(-q)
  for ref in "${IMAGES[@]}"; do
    id="$("$ENGINE" image inspect --format '{{.Id}}' "$ref" 2>/dev/null)" || die "$ref is not on this host: build or pull it first (this script pulls nothing)"
    file="$(image_file "$ref")"
    "$ENGINE" save ${QUIET[@]+"${QUIET[@]}"} -o "$B/images/$file" "$ref" || die "$ENGINE save $ref failed"
    printf '%s\t%s\t%s\n' "$ref" "$file" "$id" >> "$B/images/IMAGES"
    printf '  %-60s %8s\n' "$ref" "$(human "$(stat -c %s "$B/images/$file")")"
  done

  say "==> deploy/"
  for f in "${FILES[@]}"; do
    mkdir -p "$B/deploy/$(dirname "$f")"
    cp -pL "$ROOT/$f" "$B/deploy/$f" || die "copying $f failed"
  done
  say "  $(find "$B/deploy" -type f | wc -l) files (no .env, backups, models or certificate keys)"

  say "==> models"
  INI="$(engine_file models.ini)" || INI=""
  KEPT="$(engine_file keep | head -n1)" || KEPT=""
  [[ -n "$INI" ]] || say "  the app has registered no chat model (no models.ini in ${PROJECT}_engine)"
  FIRST="$(first_model "$INI" "$KEPT")"
  printf 'kind\tbytes\tpath\n' > "$B/models/MODELS"
  n=0; total=0; missing=0
  while IFS=$'\t' read -r kind path; do
    [[ -n "$path" ]] || continue
    if [[ -f "$MODELS_DIR/$path" ]]; then
      size="$(stat -L -c %s "$MODELS_DIR/$path")"
      n=$((n + 1)); total=$((total + size))
      if [[ $WITH_MODELS -eq 1 ]]; then
        mkdir -p "$B/models/library/$(dirname "$path")"
        # A link where the bundle is on the library's disk; a copy elsewhere.
        ln "$MODELS_DIR/$path" "$B/models/library/$path" 2>/dev/null || cp -L "$MODELS_DIR/$path" "$B/models/library/$path" || die "copying $path failed"
      fi
    else
      size=missing; missing=$((missing + 1))
      say "  missing from $MODELS_DIR: $path"
    fi
    printf '%s\t%s\t%s\n' "$kind" "$size" "$path" >> "$B/models/MODELS"
  done < <(library_files "$INI" | awk '!seen[$2]++')
  say "  $n file(s), $(human "$total"), $([[ $WITH_MODELS -eq 1 ]] && echo "copied" || echo "listed (--models copies them)")$([[ $missing -gt 0 ]] && echo "; $missing missing")"

  say "==> audio"
  AUDIO=no
  if "$ENGINE" volume inspect "${PROJECT}_audio" >/dev/null 2>&1; then
    owner=""; [[ $ENGINE == docker ]] && owner=" && chown $(id -u):$(id -g) /out/audio.tar.gz"
    "$ENGINE" run --rm --network none -v "${PROJECT}_audio:/src:ro" -v "$B/audio:/out" "$HELPER" \
      sh -c "tar -czf /out/audio.tar.gz --numeric-owner -C /src .$owner" || die "archiving the ${PROJECT}_audio volume failed"
    AUDIO=yes; say "  the speech server's models: $(human "$(stat -c %s "$B/audio/audio.tar.gz")")"
  else
    say "  no ${PROJECT}_audio volume: the speech server will fetch its models, which needs the network"
  fi

  {
    echo "format: $FORMAT"
    echo "created: $(date -u +%Y-%m-%dT%H:%M:%SZ)"
    echo "version: $(cat "$ROOT/../VERSION" 2>/dev/null || echo unknown)"
    echo "commit: $(git -C "$ROOT" rev-parse --short HEAD 2>/dev/null || echo unknown)"
    echo "engine: $ENGINE"
    echo "project: $PROJECT"
    echo "images: ${#IMAGES[@]}"
    echo "models: $([[ $WITH_MODELS -eq 1 ]] && echo copied || echo listed) ($n files, $total bytes)"
    echo "model: $FIRST"
    echo "audio: $AUDIO"
  } > "$B/MANIFEST"
  (cd "$B" && find . -type f ! -name SHA256SUMS -printf '%P\n' | sort | xargs -d '\n' sha256sum > SHA256SUMS) || die "checksums failed"
  verify_bundle "$B" || die "the bundle does not verify"
  if [[ -n "$STAGE_ARG" ]]; then
    say "==> $B ($(du -sh "$B" | cut -f1))"
    exit 0
  fi

  say "==> $OUT"
  # MANIFEST first: load --dry-run reads it without reading the whole bundle.
  (cd "$STAGE" && { echo "$TOP/MANIFEST"; echo "$TOP/SHA256SUMS"; find "$TOP" -type f ! -path "$TOP/MANIFEST" ! -path "$TOP/SHA256SUMS" | sort; } \
    | tar -cf "$OUT.part" --no-recursion -T -) || die "writing the bundle failed"
  mv "$OUT.part" "$OUT" && chmod 600 "$OUT"
  (cd "$(dirname "$OUT")" && sha256sum "$(basename "$OUT")" > "$(basename "$OUT").sha256")
  say "  $(human "$(stat -c %s "$OUT")"), $(($(wc -l < "$B/SHA256SUMS"))) files checked by SHA256SUMS"
  say ""
  say "Take $(basename "$OUT") and $(basename "$OUT").sha256 to the other host, then:"
  say "  sha256sum -c $(basename "$OUT").sha256"
  say "  tar -xOf $(basename "$OUT") $TOP/deploy/scripts/airgap.sh > airgap.sh"
  say "  bash airgap.sh load$([[ $ENGINE == podman ]] && echo " --podman") $(basename "$OUT")"
  exit 0
fi

# ============================================================================ load
BUNDLE="$(abspath "$TARGET")"
[[ -f "$BUNDLE" ]] || die "no bundle at $BUNDLE"
INTO="$(abspath "${INTO:-arena}")"
if [[ -f "$BUNDLE.sha256" ]]; then
  say "airgap: checking $(basename "$BUNDLE") against $(basename "$BUNDLE").sha256"
  (cd "$(dirname "$BUNDLE")" && sha256sum -c --quiet "$(basename "$BUNDLE").sha256") || die "the bundle is not the one that was packed: copy it again"
fi
MANIFEST="$(tar -xOf "$BUNDLE" --occurrence=1 "$TOP/MANIFEST" 2>/dev/null)" || die "$BUNDLE is not an airgap bundle (no MANIFEST)"
grep -qx "format: $FORMAT" <<<"$MANIFEST" || die "$BUNDLE is not a bundle this script reads"
manifest() { sed -n "s/^$1: //p" <<<"$MANIFEST" | head -n1; }
FIRST="$(manifest model)"

models_dir_for() {   # the library on this host
  local d="${MODELS_ARG:-$(env_get MODELS_DIR "$INTO/deploy/.env")}"
  d="${d:-./models}"
  case "$d" in /*) printf '%s' "$d" ;; *) printf '%s' "$INTO/deploy/${d#./}" ;; esac
}
MODELS_DIR="$(models_dir_for)"

env_advice() {
  say "What to put in $INTO/deploy/.env$([[ -f "$INTO/deploy/.env" ]] || echo " (cp .env.example .env first)"):"
  say "  MODELS_DIR=$MODELS_DIR"
  if [[ -n "$FIRST" ]]; then
    say "  MODEL=$FIRST"
    say "      a file in the library: the app adds it from there and never asks Hugging Face"
  else
    say "  MODEL=<a .gguf file in MODELS_DIR, as a path inside it>   (never repo:quant: that is fetched from Hugging Face)"
  fi
  if [[ $ENGINE == podman ]]; then say "  HTTP_PORT=8080"; say "  HTTPS_PORT=8443"; fi
  say "  DOMAIN, ADMIN_EMAIL and the six secrets, as in .env.example (openssl rand -hex 32)"
  say "And in docker-compose.override.yml, so the speech server never looks for Hugging Face:"
  say "  services: { audio: { environment: { HF_HUB_OFFLINE: \"1\" } } }"
  say "Then:"
  say "  cd $INTO/deploy"
  if [[ $ENGINE == podman ]]; then
    say "  podman compose -f docker-compose.yml -f podman.yml up -d --pull never"
  else
    say "  docker compose up -d --pull never"
  fi
  say "  scripts/make-cert.sh     (optional: a certificate of your own, made with openssl, no network)"
}

if [[ $DRY -eq 1 ]]; then
  say "Would load $BUNDLE (dry run: nothing is unpacked, loaded or written)"
  say "  made $(manifest created) from $(manifest version) ($(manifest commit)) with $(manifest engine)"
  say "  first: every file checked against SHA256SUMS; one mismatch and nothing is loaded"
  say "  images, each with $ENGINE load (none pulled):"
  tar -xOf "$BUNDLE" "$TOP/images/IMAGES" 2>/dev/null | cut -f1 | sed 's/^/    /'
  say "  deploy/ into $INTO/deploy (an .env, docker-compose.override.yml, certificates or backups there are kept)"
  if tar -tf "$BUNDLE" 2>/dev/null | grep -q "^$TOP/models/library/"; then
    say "  models into $MODELS_DIR:"
  else
    say "  models: not in the bundle; bring these into $MODELS_DIR:"
  fi
  tar -xOf "$BUNDLE" "$TOP/models/MODELS" 2>/dev/null | tail -n +2 | awk -F'\t' '{ printf "    %-10s %s\n", $1, $3 }'
  [[ "$(manifest audio)" == yes ]] && say "  the speech server's models into the ${PROJECT}_audio volume"
  say ""
  env_advice
  exit 0
fi

WORK="$INTO/.airgap-load"
mkdir -p "$INTO" || die "cannot make $INTO"
rm -rf "$WORK" && mkdir -p "$WORK" || die "cannot make $WORK"
say "airgap: loading $(basename "$BUNDLE") ($(manifest version), $(manifest commit))"
say "==> unpacking and checking every file"
tar -xf "$BUNDLE" -C "$WORK" || die "unpacking failed (disk full?); $WORK is left to look at"
B="$WORK/$TOP"
verify_bundle "$B" || { rm -rf "$WORK"; die "the bundle does not verify: nothing was loaded"; }
say "  $(wc -l < "$B/SHA256SUMS") files match their checksums"

say "==> images ($ENGINE load; nothing is pulled)"
while IFS=$'\t' read -r ref file _; do
  [[ -n "$ref" ]] || continue
  "$ENGINE" load -i "$B/images/$file" >/dev/null || die "$ENGINE load of $ref failed; $WORK is left to look at"
  say "  $ref"
done < "$B/images/IMAGES"

say "==> deploy/ into $INTO/deploy"
mkdir -p "$INTO/deploy" "$INTO/packs" || die "cannot write $INTO/deploy"
# This host's own choices stay: its .env (never in a bundle) and its override.
kept=""
[[ -f "$INTO/deploy/.env" ]] && kept=".env"
if [[ -f "$INTO/deploy/docker-compose.override.yml" ]]; then
  rm -f "$B/deploy/docker-compose.override.yml"; kept="${kept:+$kept and }docker-compose.override.yml"
fi
cp -a "$B/deploy/." "$INTO/deploy/" || die "copying deploy/ failed"
say "  $(find "$B/deploy" -type f | wc -l) files${kept:+; the $kept there kept}"

say "==> models into $MODELS_DIR"
mkdir -p "$MODELS_DIR" || die "cannot make $MODELS_DIR"
if [[ -d "$B/models/library" ]]; then
  while IFS= read -r f; do
    mkdir -p "$MODELS_DIR/$(dirname "$f")" && mv -f "$B/models/library/$f" "$MODELS_DIR/$f" || die "moving $f failed"
    say "  $f"
  done < <(cd "$B/models/library" && find . -type f -printf '%P\n' | sort)
else
  absent=0
  while IFS=$'\t' read -r kind _ path; do
    [[ -f "$MODELS_DIR/$path" ]] && continue
    absent=$((absent + 1)); say "  bring $path ($kind)"
  done < <(tail -n +2 "$B/models/MODELS")
  [[ $absent -eq 0 ]] && say "  every listed model is there already"
fi

if [[ -f "$B/audio/audio.tar.gz" ]]; then
  say "==> the speech server's models into ${PROJECT}_audio"
  vol="${PROJECT}_audio"
  # Labelled as compose labels its own, so compose takes it for the project's.
  "$ENGINE" volume inspect "$vol" >/dev/null 2>&1 \
    || "$ENGINE" volume create --label "com.docker.compose.project=$PROJECT" --label com.docker.compose.volume=audio "$vol" >/dev/null \
    || die "cannot make the $vol volume"
  "$ENGINE" run --rm --network none -v "$vol:/target" -v "$B/audio:/bundle:ro" "$HELPER" \
    tar -xzf /bundle/audio.tar.gz --numeric-owner -C /target || die "filling $vol failed"
  say "  done"
fi

rm -rf "$WORK"
say ""
env_advice
