#!/usr/bin/env bash
# One file per release for hosts with no network: dist/argus-arena-VERSION-offline.run, a
# shell script with the bundle after it, and its .sha256. airgap.sh packs the images, deploy/,
# the models and the speech models; this adds the installer, the docs, the licences, Code
# Arena's packages and (if asked) the knowledge packs, and makes the one file.
#
#   deploy/scripts/make-installer.sh [options]      on a host with the release's images built
#
#   --out DIR          where the .run goes (default: dist/ at the repository's root)
#   --version V        the release (default: VERSION at the repository's root)
#   --project NAME     the compose project whose images and volumes it takes (default arena):
#                      the release's own images are NAME-app, NAME-web... as compose built them
#   --podman           from Podman's store in place of Docker's
#   --models           the models too (airgap.sh --models); without it they are listed
#   --models-dir DIR   the model library (as airgap.sh: MODELS_DIR in .env, else ./models)
#   --packs            the knowledge packs in packs/ (*.arguspack)
#   --code-arena DIR   Code Arena's packages (default dist/: code-arena-*.tar.gz and .zip,
#                      which tools/package-code-arena.sh makes)
#   --leave-out A,B    services whose images are left out: a smaller bundle for hosts that
#                      leave them out (the installer then leaves them out too)
#   --dry-run          the plan; nothing is written or saved
#
# The bundle, argus-arena-VERSION/ inside the file:
#   installer.sh   the installer (scripts/installer.sh), at the top
#   MANIFEST       format, version, commit, created, upgrades-from (the oldest release it
#                  upgrades: 5.2.0), the engine it was packed with, what it holds
#   SHA256SUMS     a checksum for every other file
#   images/        every image compose names (docker save, gzipped); IMAGES: reference, file,
#                  id, bytes, bytes unpacked; RELEASE: the release's own (service, reference),
#                  tagged arena-SERVICE:VERSION
#   deploy/        as airgap.sh packs it (no .env, backups, models or keys), without an
#                  override, and only the files git has when this is a checkout
#   known/         each release's deploy files' checksums since 5.2.0, from its git tag: an
#                  upgrade tells the files a host changed from the ones it was shipped with
#   models/, audio/  as airgap.sh packs them
#   docs/, LICENSE.md, LICENSING.md, VERSION, code-arena/, packs/
#
# The file is the installer's shell header, then a small tar (the installer, MANIFEST,
# SHA256SUMS, deploy/, docs/: enough for a dry run, status and remove), then the rest.
set -uo pipefail
ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
REPO="$(cd "$ROOT/.." && pwd)"
umask 022

FORMAT="argus-arena-offline 1"
# The oldest release an upgrade starts from (installer.sh refuses older ones).
UPGRADES_FROM=5.2.0

say() { printf '%s\n' "$*"; }
die() { printf 'make-installer: ERROR: %s\n' "$*" >&2; exit 1; }
usage_error() { printf 'make-installer: %s (see --help)\n' "$*" >&2; exit 2; }
usage() { awk 'NR > 1 && !/^#/ { exit } NR > 1 { sub(/^# ?/, ""); print }' "$0"; exit 0; }
human() { awk -v b="${1:-0}" 'BEGIN { split("B KB MB GB TB", u); i = 1; while (b >= 1024 && i < 5) { b /= 1024; i++ } printf (i == 1 ? "%d %s" : "%.1f %s"), b, u[i] }'; }
abspath() { case "$1" in /*) printf '%s' "$1" ;; *) printf '%s/%s' "$PWD" "${1#./}" ;; esac; }
valid_version() { [[ "$1" =~ ^[0-9]+\.[0-9]+\.[0-9]+(-[0-9A-Za-z.-]+)?$ ]]; }
# vercmp A B: -1, 0 or 1, as installer.sh compares them.
vercmp() {
  local a=$1 b=$2 i x y ap="" bp=""
  local -a A B
  [[ $a == *-* ]] && ap=${a#*-}
  [[ $b == *-* ]] && bp=${b#*-}
  IFS=. read -r -a A <<<"${a%%-*}"; IFS=. read -r -a B <<<"${b%%-*}"
  for i in 0 1 2; do
    x=$((10#${A[i]:-0})); y=$((10#${B[i]:-0}))
    (( x < y )) && { echo -1; return; }
    (( x > y )) && { echo 1; return; }
  done
  if [[ "$ap" == "$bp" ]]; then echo 0; elif [[ -z "$ap" ]]; then echo 1; elif [[ -z "$bp" ]]; then echo -1
  elif [[ "$(printf '%s\n%s\n' "$ap" "$bp" | sort -V | head -n1)" == "$ap" ]]; then echo -1; else echo 1; fi
}

OUT_ARG="" VERSION_ARG="" PROJECT=arena ENGINE=docker WITH_MODELS=0 MODELS_ARG="" PACKS=0 CA_ARG="" LEAVE="" DRY=0
while [[ $# -gt 0 ]]; do
  case "$1" in
    --out) [[ $# -gt 1 ]] || usage_error "--out needs a folder"; OUT_ARG="$2"; shift ;;
    --version) [[ $# -gt 1 ]] || usage_error "--version needs a version"; VERSION_ARG="$2"; shift ;;
    --project) [[ $# -gt 1 ]] || usage_error "--project needs a name"; PROJECT="$2"; shift ;;
    --podman) ENGINE=podman ;;
    --models) WITH_MODELS=1 ;;
    --models-dir) [[ $# -gt 1 ]] || usage_error "--models-dir needs a folder"; MODELS_ARG="$2"; shift ;;
    --packs) PACKS=1 ;;
    --code-arena) [[ $# -gt 1 ]] || usage_error "--code-arena needs a folder"; CA_ARG="$2"; shift ;;
    --leave-out) [[ $# -gt 1 ]] || usage_error "--leave-out needs services"; LEAVE="${LEAVE:+$LEAVE,}$2"; shift ;;
    --dry-run) DRY=1 ;;
    -h|--help) usage ;;
    *) usage_error "unknown option: $1" ;;
  esac
  shift
done
[[ "$PROJECT" =~ ^[a-z0-9][a-z0-9_-]*$ ]] || usage_error "--project: lowercase letters, digits, - and _"
[[ -z "$LEAVE" || "$LEAVE" =~ ^[a-z0-9,-]+$ ]] || usage_error "--leave-out names services: a,b,c"
VERSION="${VERSION_ARG:-$(tr -d '[:space:]' < "$REPO/VERSION" 2>/dev/null)}"
valid_version "$VERSION" || usage_error "not a version: \"$VERSION\" (VERSION, or --version)"
[[ "$(vercmp "$VERSION" "$UPGRADES_FROM")" != -1 ]] || usage_error "$VERSION is older than $UPGRADES_FROM, the oldest release an upgrade starts from"
command -v "$ENGINE" >/dev/null || die "$ENGINE is not installed"
OUT_DIR="$(abspath "${OUT_ARG:-$REPO/dist}")"
CA_DIR="$(abspath "${CA_ARG:-$REPO/dist}")"
RUN="$OUT_DIR/argus-arena-$VERSION-offline.run"
TOP="argus-arena-$VERSION"
[[ -e "$RUN" ]] && die "$RUN is there already"
COMMIT="$(git -C "$REPO" rev-parse --short HEAD 2>/dev/null || echo unknown)"
[[ $COMMIT != unknown && -n "$(git -C "$REPO" status --porcelain -- deploy 2>/dev/null)" ]] && COMMIT="$COMMIT+changes"
export PODMAN_COMPOSE_WARNING_LOGS=false

WORK="$(mktemp -d "${TMPDIR:-/tmp}/make-installer.XXXXXX")" || die "cannot make a work folder"
CREATED_TAGS=()
cleanup() {
  local t
  for t in ${CREATED_TAGS[@]+"${CREATED_TAGS[@]}"}; do "$ENGINE" image rm "$t" >/dev/null 2>&1; done
  rm -rf "$WORK" "${STAGE:-/nonexistent-stage}"
}
trap cleanup EXIT
# Compose, for the release as it ships: docker-compose.yml alone, every profile but off, and
# placeholder secrets (an image never depends on them).
printf '%s=placeholder\n' APP_KEY DB_PASSWORD GATEWAY_KEY ENGINE_KEY ARGUS_KEY > "$WORK/placeholder.env"
PROFILES=""
rc() { (cd "$ROOT" && env -u COMPOSE_FILE COMPOSE_PROFILES="$PROFILES" COMPOSE_PROJECT_NAME="$PROJECT" "$ENGINE" compose --env-file "$WORK/placeholder.env" -f docker-compose.yml "$@"); }
PROFILES="$(rc config --profiles 2>/dev/null | grep -vx off | paste -sd, -)"
# The services compose builds: the release's own images.
mapfile -t BUILT < <(rc config 2>/dev/null | awk '
  /^services:/ { s = 1; next }
  s && /^[^ ]/ { s = 0 }
  s && /^  [a-z0-9_-]+:[[:space:]]*$/ { svc = $1; sub(/:$/, "", svc) }
  s && /^    build:/ { print svc }')
[[ ${#BUILT[@]} -gt 0 ]] || die "compose names no service it builds (is docker-compose.yml in $ROOT?)"
ALL=" $(rc config --services 2>/dev/null | tr '\n' ' ') "
for s in ${LEAVE//,/ }; do [[ "$ALL" == *" $s "* ]] || usage_error "--leave-out $s: no such service"; done
left() { [[ ",$LEAVE," == *",$1,"* ]]; }

# The release's own images are saved as arena-SERVICE:VERSION; the rest as compose names them.
{
  echo "# make-installer.sh: the release's own images by version, and the services left out."
  echo "services:"
  for s in "${BUILT[@]}"; do left "$s" || echo "  $s: { image: \"arena-$s:$VERSION\" }"; done
  for s in ${LEAVE//,/ }; do echo "  $s: { profiles: [off] }"; done
} > "$WORK/release.yml"
AIRGAP_ENV=(env COMPOSE_PROJECT_NAME="$PROJECT" COMPOSE_FILE="$ROOT/docker-compose.yml:$WORK/release.yml" COMPOSE_PROFILES="$PROFILES")
AIRGAP=(bash "$ROOT/scripts/airgap.sh" pack)
[[ $ENGINE == podman ]] && AIRGAP+=(--podman)
[[ $WITH_MODELS -eq 1 ]] && AIRGAP+=(--models)
[[ -n "$MODELS_ARG" ]] && AIRGAP+=(--models-dir "$MODELS_ARG")
CA_FILES=(); for f in "$CA_DIR"/code-arena-*.tar.gz "$CA_DIR"/code-arena-*.zip; do [[ -f "$f" ]] && CA_FILES+=("$f"); done
PACK_FILES=(); [[ $PACKS -eq 1 ]] && for f in "$REPO"/packs/*.arguspack; do [[ -f "$f" ]] && PACK_FILES+=("$f"); done

if [[ $DRY -eq 1 ]]; then
  say "Would make $RUN and $(basename "$RUN").sha256 (dry run: nothing is written or saved)"
  say "  Argus Arena $VERSION ($COMMIT), upgrading $UPGRADES_FROM or newer, from $ENGINE's store"
  say "  the release's own images, tagged arena-SERVICE:$VERSION from $PROJECT-SERVICE:"
  for s in "${BUILT[@]}"; do left "$s" && continue
    if "$ENGINE" image inspect "$PROJECT-$s" >/dev/null 2>&1; then say "    $PROJECT-$s"; else say "    $PROJECT-$s   NOT ON THIS HOST: build it first"; fi
  done
  [[ -n "$LEAVE" ]] && say "  left out: ${LEAVE//,/ }"
  "${AIRGAP_ENV[@]}" "${AIRGAP[@]}" --dry-run "$WORK/plan.tar" | sed -n '2,$p' | sed 's/^/  /'
  say "  then gzipped, with installer.sh, docs/, LICENSE.md, LICENSING.md, VERSION, known/ (the deploy files of each release since $UPGRADES_FROM)"
  say "  Code Arena's packages: ${#CA_FILES[@]} in $CA_DIR$([[ ${#CA_FILES[@]} -eq 0 ]] && echo " (none: tools/package-code-arena.sh makes them)")"
  say "  knowledge packs: $([[ $PACKS -eq 1 ]] && echo "${#PACK_FILES[@]} from packs/" || echo "none (--packs)")"
  exit 0
fi

mkdir -p "$OUT_DIR" || die "cannot make $OUT_DIR"
say "make-installer: Argus Arena $VERSION ($COMMIT), from $ENGINE's store"
say "==> the release's own images, as arena-SERVICE:$VERSION"
for s in "${BUILT[@]}"; do
  left "$s" && continue
  src="$PROJECT-$s" dst="arena-$s:$VERSION"
  # Podman: the name Docker gives it too, so either engine finds it by its short name.
  [[ $ENGINE == podman ]] && dst="docker.io/library/$dst"
  id="$("$ENGINE" image inspect --format '{{.Id}}' "$src" 2>/dev/null)" || die "$src is not on this host: build it first ($ENGINE compose build $s)"
  have="$("$ENGINE" image inspect --format '{{.Id}}' "$dst" 2>/dev/null)"
  if [[ -z "$have" ]]; then "$ENGINE" tag "$src" "$dst" || die "tagging $src as $dst failed"; CREATED_TAGS+=("$dst")
  elif [[ "$have" != "$id" ]]; then die "$dst is here already and is not $src: remove that tag first ($ENGINE image rm $dst)"; fi
  say "  $src -> arena-$s:$VERSION"
done

# The free space for the staging folder and the file: the images, twice over.
STAGE="$(mktemp -d "$OUT_DIR/.make-installer.XXXXXX")" || die "cannot make a work folder in $OUT_DIR"
"${AIRGAP_ENV[@]}" "${AIRGAP[@]}" --stage "$STAGE" || die "airgap.sh could not pack the bundle"
B="$STAGE/$TOP"
mv "$STAGE/arena-airgap" "$B" || die "cannot rename the bundle's folder"
AIRGAP_MANIFEST="$(cat "$B/MANIFEST")"
rm -f "$B/SHA256SUMS" "$B/MANIFEST"

say "==> deploy/ as the release ships it"
rm -f "$B/deploy/docker-compose.override.yml"
if git -C "$REPO" rev-parse --git-dir >/dev/null 2>&1; then
  n=0
  while IFS= read -r f; do
    git -C "$REPO" ls-files --error-unmatch "deploy/$f" >/dev/null 2>&1 || { rm -f "$B/deploy/$f"; n=$((n + 1)); }
  done < <(cd "$B/deploy" && find . -type f -printf '%P\n')
  say "  $(find "$B/deploy" -type f | wc -l) files; $n left out that git does not have"
fi

say "==> images, gzipped"
GZ=(gzip -6); command -v pigz >/dev/null && GZ=(pigz -6)
: > "$B/images/IMAGES.new"
while IFS=$'\t' read -r ref file id; do
  [[ -n "$ref" ]] || continue
  raw="$(stat -c %s "$B/images/$file")"
  "${GZ[@]}" "$B/images/$file" || die "gzipping $file failed (disk full?)"
  printf '%s\t%s\t%s\t%s\t%s\n' "$ref" "$file.gz" "$id" "$(stat -c %s "$B/images/$file.gz")" "$raw" >> "$B/images/IMAGES.new"
  printf '  %-60s %9s -> %9s\n' "$ref" "$(human "$raw")" "$(human "$(stat -c %s "$B/images/$file.gz")")"
done < "$B/images/IMAGES"
mv "$B/images/IMAGES.new" "$B/images/IMAGES"
: > "$B/images/RELEASE"
for s in "${BUILT[@]}"; do left "$s" || printf '%s\tarena-%s:%s\n' "$s" "$s" "$VERSION" >> "$B/images/RELEASE"; done

say "==> the installer, the docs, the licences"
cp -p "$ROOT/scripts/installer.sh" "$B/installer.sh" && chmod 755 "$B/installer.sh" || die "no scripts/installer.sh"
echo "$VERSION" > "$B/VERSION"
for f in LICENSE.md LICENSING.md; do cp -p "$REPO/$f" "$B/$f" || die "no $f in $REPO"; done
mkdir -p "$B/docs" && cp -a "$REPO/docs/." "$B/docs/" || die "no docs/ in $REPO"
# The project's own plans are not documentation for those who run it.
rm -f "$B/docs/plan.md"
say "  installer.sh, $(find "$B/docs" -type f | wc -l) docs, LICENSE.md, LICENSING.md, VERSION"

say "==> Code Arena's packages"
if [[ ${#CA_FILES[@]} -gt 0 ]]; then
  mkdir -p "$B/code-arena"
  for f in "${CA_FILES[@]}"; do cp -p "$f" "$B/code-arena/"; done
  (cd "$B/code-arena" && sha256sum -- * > SHA256SUMS)
  say "  ${#CA_FILES[@]} from $CA_DIR"
else
  say "  none in $CA_DIR (tools/package-code-arena.sh makes them): the app's image still carries them for download"
fi
if [[ $PACKS -eq 1 ]]; then
  say "==> knowledge packs"
  mkdir -p "$B/packs"
  for f in ${PACK_FILES[@]+"${PACK_FILES[@]}"}; do ln "$f" "$B/packs/" 2>/dev/null || cp -p "$f" "$B/packs/"; done
  say "  ${#PACK_FILES[@]} from packs/"
fi

say "==> known/: each release's deploy files since $UPGRADES_FROM"
mkdir -p "$B/known"
if git -C "$REPO" rev-parse --git-dir >/dev/null 2>&1; then
  for tag in $(git -C "$REPO" tag -l 'v[0-9]*'); do
    v="$(git -C "$REPO" show "$tag:VERSION" 2>/dev/null | tr -d '[:space:]')"
    valid_version "$v" || continue
    [[ "$(vercmp "$v" "$UPGRADES_FROM")" != -1 && "$(vercmp "$v" "$VERSION")" == -1 ]] || continue
    git -C "$REPO" ls-tree -r "$tag" -- deploy | while read -r mode type sha path; do
      [[ $type == blob && $mode != 120000 ]] || continue
      printf '%s  %s\n' "$(git -C "$REPO" cat-file blob "$sha" | sha256sum | cut -d' ' -f1)" "${path#deploy/}"
    done > "$B/known/$v.sha256"
    say "  $v ($tag): $(wc -l < "$B/known/$v.sha256") files"
  done
fi

ai() { sed -n "s/^$1: //p" <<<"$AIRGAP_MANIFEST" | head -n1; }
{
  echo "format: $FORMAT"
  echo "version: $VERSION"
  echo "commit: $COMMIT"
  echo "created: $(date -u +%Y-%m-%dT%H:%M:%SZ)"
  echo "upgrades-from: $UPGRADES_FROM"
  echo "engine: $ENGINE"
  echo "images: $(grep -c . "$B/images/IMAGES") ($(awk -F'\t' '{ s += $4 } END { printf "%d", s }' "$B/images/IMAGES") bytes, $(awk -F'\t' '{ s += $5 } END { printf "%d", s }' "$B/images/IMAGES") unpacked)"
  echo "release-images: $(cut -f1 "$B/images/RELEASE" | tr '\n' ' ' | sed 's/ $//')"
  echo "left-out: ${LEAVE//,/ }"
  echo "models: $(ai models)"
  echo "model: $(ai model)"
  echo "audio: $(ai audio)"
  echo "packs: ${#PACK_FILES[@]}"
  echo "code-arena: ${#CA_FILES[@]}"
} > "$B/MANIFEST"
(cd "$B" && find . -type f ! -name SHA256SUMS -printf '%P\n' | sort | xargs -d '\n' sha256sum > SHA256SUMS) || die "checksums failed"
(cd "$B" && sha256sum -c --quiet --strict SHA256SUMS) || die "the bundle does not verify"

say "==> $RUN"
# The small part first: the installer, what it reads to plan, and the text files.
(cd "$B" && find . -type f -printf '%P\n' | sort) > "$WORK/all"
grep -E '^(installer\.sh|MANIFEST|SHA256SUMS|VERSION|LICENSE\.md|LICENSING\.md|images/IMAGES|images/RELEASE|models/MODELS|known/.*|deploy/.*|docs/.*)$' "$WORK/all" \
  | awk '{ p = ($0 == "installer.sh") ? 0 : ($0 == "MANIFEST") ? 1 : ($0 == "SHA256SUMS") ? 2 : 3; print p "\t" $0 }' | sort -k1,1n -k2 | cut -f2 > "$WORK/head"
grep -vxF -f "$WORK/head" "$WORK/all" > "$WORK/body"
TARF=(--owner=0 --group=0 --numeric-owner --no-recursion)
(cd "$STAGE" && sed "s|^|$TOP/|" "$WORK/head" | tar -cf "$WORK/head.tar" "${TARF[@]}" -T -) || die "writing the bundle failed"
(cd "$STAGE" && sed "s|^|$TOP/|" "$WORK/body" | tar -cf "$STAGE/body.tar" "${TARF[@]}" -T -) || die "writing the bundle failed (disk full?)"
HB="$(stat -c %s "$WORK/head.tar")" BB="$(stat -c %s "$STAGE/body.tar")"
UNPACKED="$(human $(( HB + BB )))"

cat > "$WORK/header" <<'HEADER'
#!/bin/sh
# Argus Arena @VERSION@ (@COMMIT@), the offline installer: a shell script with the whole
# release after it. It unpacks itself into a work folder, checks it, runs its installer.sh
# with the arguments given, and removes the folder again. Nothing comes from the network.
#
#   sh argus-arena-@VERSION@-offline.run help
#   sh argus-arena-@VERSION@-offline.run install --dir /srv/arena
#   sh argus-arena-@VERSION@-offline.run upgrade --dir /srv/arena
#   sh argus-arena-@VERSION@-offline.run verify
#
#   --workdir DIR    where it unpacks (default: beside this file, else /var/tmp); @SIZE@ free
#   --keep-workdir   leave the unpacked bundle there: its installer.sh runs it again
#   --extract DIR    only unpack it into DIR and check it
#
# Check the copy first: sha256sum -c argus-arena-@VERSION@-offline.run.sha256
set -u
HEAD_START=@HEAD_START@
HEAD_BYTES=@HEAD_BYTES@
BODY_BYTES=@BODY_BYTES@
TOP=argus-arena-@VERSION@
die() { printf 'argus-arena: %s\n' "$*" >&2; exit 1; }
for t in bash tar tail head df mktemp; do command -v "$t" >/dev/null 2>&1 || die "$t is needed"; done
self=$0
case $self in /*) ;; *) self="$(pwd)/$self" ;; esac
WORK="" KEEP=0 EXTRACT="" CMD="" SMALL=0 SEEN=0
n=$#
while [ "$n" -gt 0 ]; do
  a=$1; shift; n=$((n - 1))
  case $a in
    --workdir) [ "$n" -gt 0 ] || die "--workdir needs a folder"; WORK=$1; shift; n=$((n - 1)) ;;
    --keep-workdir) KEEP=1 ;;
    --extract) [ "$n" -gt 0 ] || die "--extract needs a folder"; EXTRACT=$1; shift; n=$((n - 1)) ;;
    *) [ "$SEEN" = 0 ] && { CMD=$a; SEEN=1; }
       [ "$a" = --dry-run ] && SMALL=1
       set -- "$@" "$a" ;;
  esac
done
# A dry run, status, remove and help need only the small part.
case $CMD in status|remove|help|-h|--help|"") SMALL=1 ;; esac
[ -n "$EXTRACT" ] && { WORK=$EXTRACT; KEEP=1; SMALL=0; }
need=$HEAD_BYTES; [ "$SMALL" = 1 ] || need=$((HEAD_BYTES + BODY_BYTES))
if [ -z "$WORK" ]; then WORK=$(dirname "$self"); [ -w "$WORK" ] || WORK=${TMPDIR:-/var/tmp}; fi
mkdir -p "$WORK" || die "cannot make $WORK"
free=$(df -Pk "$WORK" | awk 'NR == 2 { print $4 }')
[ "${free:-0}" -ge $((need / 1024 + 1024)) ] || die "$WORK has $((${free:-0} / 1024)) MB free; unpacking needs $((need / 1048576 + 1)) MB: --workdir another folder"
if [ -n "$EXTRACT" ]; then
  dest=$(cd "$WORK" && pwd); [ -e "$dest/$TOP" ] && die "$dest/$TOP is there already"
else
  dest=$(mktemp -d "$WORK/.argus-arena-unpack.XXXXXX") || die "cannot make a folder in $WORK"
  [ "$KEEP" = 1 ] || trap 'rm -rf "$dest"' EXIT
  trap 'exit 130' INT TERM
fi
tail -c +$((HEAD_START + 1)) "$self" | head -c "$HEAD_BYTES" | tar -xf - -C "$dest" \
  || die "unpacking failed: a damaged or cut-off copy? (sha256sum -c ${self##*/}.sha256)"
if [ "$SMALL" = 0 ]; then
  printf 'argus-arena: unpacking %s into %s\n' "${self##*/}" "$dest" >&2
  tail -c +$((HEAD_START + HEAD_BYTES + 1)) "$self" | head -c "$BODY_BYTES" | tar -xf - -C "$dest" \
    || die "unpacking failed: disk full, or a damaged copy? (sha256sum -c ${self##*/}.sha256)"
fi
if [ -n "$EXTRACT" ]; then
  ARENA_BUNDLE_RUN=$self bash "$dest/$TOP/installer.sh" verify --dir "$dest/$TOP" || exit $?
  printf 'argus-arena: unpacked and checked: bash %s/%s/installer.sh COMMAND ...\n' "$dest" "$TOP" >&2
  exit 0
fi
ARENA_BUNDLE_RUN=$self ARENA_BUNDLE_PARTIAL=$SMALL bash "$dest/$TOP/installer.sh" "$@"
rc=$?
[ "$KEEP" = 1 ] && printf 'argus-arena: the unpacked bundle stays in %s/%s\n' "$dest" "$TOP" >&2
exit $rc
HEADER
sed -i -e "s|@VERSION@|$VERSION|g" -e "s|@COMMIT@|$COMMIT|g" -e "s|@SIZE@|$UNPACKED|g" \
  -e 's|@HEAD_START@|________________|' -e 's|@HEAD_BYTES@|________________|' -e 's|@BODY_BYTES@|________________|' "$WORK/header"
HS="$(stat -c %s "$WORK/header")"
# Each number in a field of the placeholder's width, so the header's length stays what it was.
sed -i -e "s|^HEAD_START=________________|HEAD_START=$(printf '%-16s' "$HS")|" \
  -e "s|^HEAD_BYTES=________________|HEAD_BYTES=$(printf '%-16s' "$HB")|" \
  -e "s|^BODY_BYTES=________________|BODY_BYTES=$(printf '%-16s' "$BB")|" "$WORK/header"
[[ "$(stat -c %s "$WORK/header")" == "$HS" ]] || die "the header's length changed"
cat "$WORK/header" "$WORK/head.tar" "$STAGE/body.tar" > "$RUN.part" || die "writing $RUN failed (disk full?)"
rm -f "$STAGE/body.tar"
chmod 755 "$RUN.part" && mv "$RUN.part" "$RUN" || die "writing $RUN failed"
(cd "$OUT_DIR" && sha256sum "$(basename "$RUN")" > "$(basename "$RUN").sha256")
say "  $(human "$(stat -c %s "$RUN")") ($(grep -c . "$B/SHA256SUMS") files, $UNPACKED unpacked), and $(basename "$RUN").sha256"
say ""
say "On the host with no network:"
say "  sha256sum -c $(basename "$RUN").sha256"
say "  sh $(basename "$RUN") install --dir /srv/arena                (a new installation)"
say "  sh $(basename "$RUN") upgrade --dir /srv/arena                (from $UPGRADES_FROM or newer)"
say "  sh $(basename "$RUN") help"
