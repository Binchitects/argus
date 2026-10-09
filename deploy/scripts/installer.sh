#!/usr/bin/env bash
# Argus Arena's offline installer: install, upgrade (from 5.2.0 or newer), repair,
# remove, status and verify, from one bundle that scripts/make-installer.sh makes,
# with Docker or rootless Podman, and nothing from the network.
#
#   sh argus-arena-VERSION-offline.run COMMAND [options]   the bundle unpacks itself and runs this
#   bash installer.sh COMMAND [options]                     in an unpacked bundle
#   DIR/deploy/scripts/installer.sh COMMAND --dir DIR       an installation's own copy: status,
#                                                           remove, verify, and repair without a bundle
#
# Commands:
#   install   a new installation: checks the host, loads the images, writes DIR/deploy, makes
#             .env (secrets generated), starts the stack and waits until it is healthy
#   upgrade   an installation of 5.2.0 or newer: a backup first, then the new images and files
#             (.env, overrides, certificates and backups kept; new .env keys added and listed),
#             started and checked. On a failure it rolls back by itself: the files, the images
#             and the data from the backup. --rollback goes back to before the last upgrade.
#   repair    checks the installation against the bundle and puts right what is wrong:
#             missing or changed files, missing images, volumes' owners, stopped or unhealthy
#             containers; each finding is reported with what was done
#   remove    stops and removes the stack's containers and images. The data stays (volumes,
#             .env, backups, models) unless --purge
#   status    what is installed: the version each service runs, health, disk and GPU
#   verify    the bundle's checksums; with an installation, its files' too
#
# Options:
#   --dir DIR            the installation: DIR/deploy holds the compose files and .env (DIR may
#                        also be the deploy folder itself). Default: /srv/arena as root, else
#                        ~/arena; an installation's own copy knows its folder
#   --docker, --podman   the engine (default: the one the installation runs on, else Docker)
#   --project NAME       the compose project (default: COMPOSE_PROJECT_NAME in .env, else arena)
#   --yes                no questions, the defaults: for unattended runs
#   --dry-run            the plan; nothing is written, loaded, started or removed
#   --log FILE           the log (default DIR/.arena-install/logs/COMMAND-TIME.log; status and
#                        verify keep one only beside an installation; a dry run, none)
#   --timeout MIN        how long to wait for the stack to be healthy, in minutes (default 15;
#                        30s: seconds)
# install:
#   --domain NAME  --admin-email ADDRESS  --models-dir DIR  --model FILE  --acme-email ADDRESS
#   --http-port N  --https-port N   (default 80 and 443; Podman 8080 and 8443)
#   --gitlab-url URL  --gitlab-token-file FILE   the GitLab Argus indexes, and its read-only
#                        token (read from the file, never from the command line). Without them
#                        Argus, which cannot start without a GitLab, is left out until they are
#                        in .env
#   --cpu-only           no NVIDIA GPU: llamacpp, imagegen, videogen, gpu-exporter and
#                        power-limits are left out (in docker-compose.override.yml)
#   --leave-out A,B      more services to leave out
#   --make-cert          a certificate of the deployment's own (scripts/make-cert.sh)
#   --hosts              the stack's names in /etc/hosts (scripts/setup-hosts.sh; needs root)
#   --skip-requirements  go on when a requirement is not met
# remove:
#   --purge              also the data: the volumes, deploy/ with .env and certificates, the
#                        backups, and the models when they are inside DIR. It asks for the word
#                        PURGE (unattended: --confirm PURGE) and offers a last backup first
#   --final-backup DIR   that last backup, into DIR, which is not removed
#
# Exit codes:
#   0  done, or nothing to do
#   1  failed: the log says what and where
#   2  a wrong command or option
#   3  refused: a requirement not met, a version it does not upgrade from, no confirmation
#   4  the upgrade failed and was rolled back: the version before it runs again
#   5  the upgrade failed and so did the rollback: the backup to restore by hand is named
#   6  checksums do not match: the bundle, or the installed files, are damaged
set -uo pipefail
# Deploy files are read by the containers' users; .env, the state and the logs are made 0600/0700.
umask 022

FORMAT="argus-arena-offline 1"
# In the compose file already (cpu-temp-exporter): every bundle has it.
HELPER=python:3.13-slim
GPU_SERVICES="llamacpp imagegen videogen gpu-exporter power-limits"
# The services' users, for the volumes they write: repair puts a wrong owner right.
VOLUME_OWNERS="engine:1000:1000 directory:1000:1000 sandbox:1000:1000 audio:1000:1000 argus:10001:10001 loki:10001:10001 prometheus:65534:65534 alertmanager:65534:65534"
# .env and this script decide these, not the caller's shell.
unset COMPOSE_FILE COMPOSE_PROFILES COMPOSE_PROJECT_NAME COMPOSE_ENV_FILES
export PODMAN_COMPOSE_WARNING_LOGS=false

SELF="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)/$(basename "${BASH_SOURCE[0]}")"

say()  { printf '%s\n' "$*"; }
step() { printf '==> %s\n' "$*"; }
ok()   { printf '  ok     %s\n' "$*"; }
note() { printf '  note   %s\n' "$*"; }
bad()  { printf '  FAIL   %s\n' "$*"; }
did()  { printf '  done   %s\n' "$*"; }
would(){ printf '  would  %s\n' "$*"; }
die()  { printf 'installer: ERROR: %s\n' "$1" >&2; exit "${2:-1}"; }
refuse() { printf 'installer: REFUSED: %s\n' "$*" >&2; exit 3; }
usage_error() { printf 'installer: %s (see: installer.sh help)\n' "$*" >&2; exit 2; }
usage() { awk 'NR > 1 && !/^#/ { exit } NR > 1 { sub(/^# ?/, ""); print }' "$SELF"; exit 0; }

abspath() { case "$1" in /*) printf '%s' "$1" ;; *) printf '%s/%s' "$PWD" "${1#./}" ;; esac; }
human() { awk -v b="${1:-0}" 'BEGIN { split("B KB MB GB TB", u); i = 1; while (b >= 1024 && i < 5) { b /= 1024; i++ } printf (i == 1 ? "%d %s" : "%.1f %s"), b, u[i] }'; }
env_get() { grep -E "^$1=" "${2:-$DEPLOY/.env}" 2>/dev/null | tail -n1 | cut -d= -f2- | sed -e 's/^"//' -e 's/"$//'; }
# A value that is never printed or logged.
is_secret() { [[ "$1" =~ (PASSWORD|KEY|TOKEN|SECRET) ]]; }
gen_secret() { openssl rand -hex 32 2>/dev/null || od -An -N32 -tx1 /dev/urandom | tr -d ' \n'; }
sha_of() { sha256sum "$1" 2>/dev/null | cut -d' ' -f1; }
free_kb() { df -Pk "$1" 2>/dev/null | awk 'NR == 2 { print $4 }'; }
# The nearest folder that exists, for df.
existing_parent() { local d=$1; while [[ ! -d "$d" ]]; do d="$(dirname "$d")"; done; printf '%s' "$d"; }

valid_version() { [[ "$1" =~ ^[0-9]+\.[0-9]+\.[0-9]+(-[0-9A-Za-z.-]+)?$ ]]; }
# vercmp A B: -1, 0 or 1. Numbers as numbers; a pre-release (5.3.0-rc1) comes before its release.
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
  if [[ "$ap" == "$bp" ]]; then echo 0
  elif [[ -z "$ap" ]]; then echo 1
  elif [[ -z "$bp" ]]; then echo -1
  elif [[ "$(printf '%s\n%s\n' "$ap" "$bp" | sort -V | head -n1)" == "$ap" ]]; then echo -1
  else echo 1
  fi
}

# Questions come from the terminal; with --yes the default is taken. No terminal and no --yes: refused.
TTY=0
if ( : </dev/tty ) 2>/dev/null; then exec 7</dev/tty && TTY=1; fi
ask() {   # ask QUESTION DEFAULT -> the answer (a dry run takes the default)
  local answer=""
  if [[ $YES -eq 1 || $DRY -eq 1 ]]; then printf '%s' "$2"; return; fi
  [[ $TTY -eq 1 ]] || refuse "no terminal to ask \"$1\": answer with options and --yes"
  printf '%s [%s]: ' "$1" "$2" >&2
  read -r answer <&7 || true
  printf '%s' "${answer:-$2}"
}
confirm() {   # confirm QUESTION: yes or no (default no)
  local answer=""
  [[ $YES -eq 1 ]] && return 0
  [[ $TTY -eq 1 ]] || refuse "no terminal to ask \"$1\": pass --yes for an unattended run"
  printf '%s [y/N]: ' "$1" >&2
  read -r answer <&7 || true
  [[ "$answer" =~ ^[Yy]([Ee][Ss])?$ ]]
}

# ============================================================== arguments
CMD="${1:-}"
[[ $# -gt 0 ]] && shift
case "$CMD" in
  install|upgrade|repair|remove|status|verify) ;;
  help|-h|--help) usage ;;
  "") usage_error "say what to do: install, upgrade, repair, remove, status or verify" ;;
  *) usage_error "unknown command: $CMD" ;;
esac
DIR="" BUNDLE_ARG="" ENGINE="" PROJECT_ARG="" YES=0 DRY=0 LOG_ARG="" TIMEOUT=15
DOMAIN_ARG="" EMAIL_ARG="" MODELS_ARG="" MODEL_ARG="" HTTP_ARG="" HTTPS_ARG="" ACME_ARG="" GITLAB_ARG="" GITLAB_TOKEN_FILE=""
CPU_ONLY=0 LEAVE_ARG="" MAKE_CERT=0 HOSTS=0 SKIP_REQ=0 PURGE=0 CONFIRM_ARG="" FINAL_BACKUP="" ROLLBACK=0
only() { local c; for c in "${@:2}"; do [[ $CMD == "$c" ]] && return 0; done; usage_error "$1 is for ${*:2}"; }
value() { [[ $2 -gt 1 ]] || usage_error "$1 needs a value"; }
while [[ $# -gt 0 ]]; do
  case "$1" in
    --dir) value "$1" $#; DIR="$2"; shift ;;
    --bundle) value "$1" $#; BUNDLE_ARG="$2"; shift ;;
    --engine) value "$1" $#; ENGINE="$2"; shift; [[ $ENGINE == docker || $ENGINE == podman ]] || usage_error "--engine is docker or podman" ;;
    --docker) ENGINE=docker ;;
    --podman) ENGINE=podman ;;
    --project) value "$1" $#; PROJECT_ARG="$2"; shift; [[ "$PROJECT_ARG" =~ ^[a-z0-9][a-z0-9_-]*$ ]] || usage_error "--project: lowercase letters, digits, - and _" ;;
    --yes|-y) YES=1 ;;
    --dry-run) DRY=1 ;;
    --log) value "$1" $#; LOG_ARG="$2"; shift ;;
    --timeout) value "$1" $#; TIMEOUT="$2"; shift; [[ "$TIMEOUT" =~ ^[1-9][0-9]*s?$ ]] || usage_error "--timeout is minutes (15), or seconds (90s)" ;;
    --domain) only "$1" install; value "$1" $#; DOMAIN_ARG="$2"; shift ;;
    --admin-email) only "$1" install; value "$1" $#; EMAIL_ARG="$2"; shift ;;
    --models-dir) only "$1" install; value "$1" $#; MODELS_ARG="$2"; shift ;;
    --model) only "$1" install; value "$1" $#; MODEL_ARG="$2"; shift ;;
    --http-port) only "$1" install; value "$1" $#; HTTP_ARG="$2"; shift ;;
    --https-port) only "$1" install; value "$1" $#; HTTPS_ARG="$2"; shift ;;
    --acme-email) only "$1" install; value "$1" $#; ACME_ARG="$2"; shift ;;
    --gitlab-url) only "$1" install; value "$1" $#; GITLAB_ARG="$2"; shift ;;
    --gitlab-token-file) only "$1" install; value "$1" $#; GITLAB_TOKEN_FILE="$2"; shift; [[ -r "$GITLAB_TOKEN_FILE" ]] || usage_error "cannot read $GITLAB_TOKEN_FILE" ;;
    --cpu-only) only "$1" install; CPU_ONLY=1 ;;
    --leave-out) only "$1" install; value "$1" $#; LEAVE_ARG="${LEAVE_ARG:+$LEAVE_ARG,}$2"; shift ;;
    --make-cert) only "$1" install; MAKE_CERT=1 ;;
    --hosts) only "$1" install; HOSTS=1 ;;
    --skip-requirements) only "$1" install; SKIP_REQ=1 ;;
    --rollback) only "$1" upgrade; ROLLBACK=1 ;;
    --purge) only "$1" remove; PURGE=1 ;;
    --confirm) only "$1" remove; value "$1" $#; CONFIRM_ARG="$2"; shift ;;
    --final-backup) only "$1" remove; value "$1" $#; FINAL_BACKUP="$2"; shift ;;
    -h|--help) usage ;;
    *) usage_error "unknown option: $1" ;;
  esac
  shift
done
for p in "$HTTP_ARG" "$HTTPS_ARG"; do
  [[ -z "$p" || ( "$p" =~ ^[0-9]+$ && $p -ge 1 && $p -le 65535 ) ]] || usage_error "a port is a number from 1 to 65535: $p"
done
[[ -z "$LEAVE_ARG" || "$LEAVE_ARG" =~ ^[a-z0-9,-]+$ ]] || usage_error "--leave-out names services: a,b,c"
[[ -n "$CONFIRM_ARG" && $PURGE -eq 0 ]] && usage_error "--confirm is for --purge"
[[ -n "$FINAL_BACKUP" && $PURGE -eq 0 ]] && usage_error "--final-backup is for --purge"

# ================================================================ the bundle
# installer.sh at the top of a bundle has its MANIFEST beside it; an installation's copy has none.
BUNDLE=""
if [[ -n "$BUNDLE_ARG" ]]; then
  BUNDLE="$(cd "$BUNDLE_ARG" 2>/dev/null && pwd)" || usage_error "no bundle folder $BUNDLE_ARG"
  grep -qx "format: $FORMAT" "$BUNDLE/MANIFEST" 2>/dev/null || usage_error "$BUNDLE is not an unpacked Argus Arena bundle"
elif grep -qx "format: $FORMAT" "$(dirname "$SELF")/MANIFEST" 2>/dev/null; then
  BUNDLE="$(dirname "$SELF")"
fi
# The .run unpacks only the small files for a dry run, status and remove (ARENA_BUNDLE_PARTIAL=1).
PARTIAL="${ARENA_BUNDLE_PARTIAL:-0}"
RUNFILE="${ARENA_BUNDLE_RUN:-}"
bundle_get() { sed -n "s/^$1: //p" "$BUNDLE/MANIFEST" 2>/dev/null | head -n1; }
BVERSION="" UPGRADES_FROM="" BLEFT=""
if [[ -n "$BUNDLE" ]]; then
  BVERSION="$(bundle_get version)"; UPGRADES_FROM="$(bundle_get upgrades-from)"; BLEFT="$(bundle_get left-out)"
  valid_version "$BVERSION" || die "the bundle's MANIFEST names no version it can use: \"$BVERSION\""
  valid_version "$UPGRADES_FROM" || die "the bundle's MANIFEST says no version it upgrades from"
fi
need_bundle() { [[ -n "$BUNDLE" ]] || die "$CMD needs the bundle: run it from argus-arena-VERSION-offline.run (or --bundle DIR)"; }

# Every file of the bundle matches SHA256SUMS and none is unlisted (partly unpacked: those there).
verify_bundle() {
  local listed present
  [[ -f "$BUNDLE/MANIFEST" && -f "$BUNDLE/SHA256SUMS" ]] || { bad "no MANIFEST or SHA256SUMS in $BUNDLE"; return 1; }
  if [[ $PARTIAL == 1 ]]; then
    (cd "$BUNDLE" && sha256sum -c --quiet --strict --ignore-missing SHA256SUMS) || { bad "CHECKSUM MISMATCH in the bundle"; return 1; }
    return 0
  fi
  (cd "$BUNDLE" && sha256sum -c --quiet --strict SHA256SUMS) || { bad "CHECKSUM MISMATCH: the bundle is damaged; copy it again"; return 1; }
  listed="$(sed -E 's/^[0-9a-f]{64} [ *]//' "$BUNDLE/SHA256SUMS" | sort)"
  present="$(cd "$BUNDLE" && find . -type f ! -name SHA256SUMS -printf '%P\n' | sort)"
  if [[ "$listed" != "$present" ]]; then
    bad "files the checksums do not name: $(comm -13 <(printf '%s\n' "$listed") <(printf '%s\n' "$present") | tr '\n' ' ')"
    return 1
  fi
  return 0
}
check_bundle() {   # check_bundle: verified, or exit 6
  step "the bundle: $BVERSION ($(bundle_get commit)), checked against its SHA256SUMS"
  verify_bundle || die "the bundle does not verify: nothing was changed" 6
  ok "$(wc -l < "$BUNDLE/SHA256SUMS") files match$([[ $PARTIAL == 1 ]] && echo " (the small ones: the rest is checked when it is unpacked)")"
}
# ref<TAB>file<TAB>id where packed<TAB>bytes<TAB>bytes unpacked
bundle_images() { grep -v '^$' "$BUNDLE/images/IMAGES" 2>/dev/null; }
# service<TAB>ref: the release's own images, which compose builds as PROJECT-service
bundle_release() { grep -v '^$' "$BUNDLE/images/RELEASE" 2>/dev/null; }

# ========================================================== the installation
if [[ -z "$DIR" ]]; then
  if [[ -z "$BUNDLE" && "$SELF" == */deploy/scripts/installer.sh ]]; then DIR="${SELF%/deploy/scripts/installer.sh}"
  elif [[ $EUID -eq 0 ]]; then DIR=/srv/arena
  else DIR="$HOME/arena"
  fi
fi
DIR="$(abspath "$DIR")"; DIR="${DIR%/}"
[[ -n "$DIR" && "$DIR" != / ]] || usage_error "--dir cannot be /"
# DIR may be the deploy folder itself (a checkout's deploy/, or one airgap.sh loaded).
if [[ -f "$DIR/docker-compose.yml" && ! -d "$DIR/deploy" ]]; then DEPLOY="$DIR"; DIR="$(dirname "$DIR")"; else DEPLOY="$DIR/deploy"; fi
STATE="$DIR/.arena-install"
state_get() { sed -n "s/^$1=//p" "$STATE/state" 2>/dev/null | tail -n1; }
state_set() {   # state_set KEY VALUE ...
  local tmp
  mkdir -p "$STATE" && chmod 700 "$STATE" || die "cannot write $STATE"
  tmp="$(mktemp "$STATE/state.XXXXXX")"
  cp "$STATE/state" "$tmp" 2>/dev/null || : > "$tmp"
  while [[ $# -gt 1 ]]; do
    grep -v -E "^$1=" "$tmp" > "$tmp.n"; printf '%s=%s\n' "$1" "$2" >> "$tmp.n"; mv "$tmp.n" "$tmp"; shift 2
  done
  mv "$tmp" "$STATE/state"
}

PROJECT="${PROJECT_ARG:-$(env_get COMPOSE_PROJECT_NAME)}"; PROJECT="${PROJECT:-$(state_get project)}"; PROJECT="${PROJECT:-arena}"

# The engine: as asked; else the one the installation runs on; else Docker, then Podman.
has_containers() { "$1" ps -aq --filter "label=com.docker.compose.project=$PROJECT" 2>/dev/null | grep -q .; }
engine_works() { command -v "$1" >/dev/null && "$1" info >/dev/null 2>&1; }
pick_engine() {
  [[ -n "$ENGINE" ]] && return 0
  ENGINE="$(state_get engine)"
  [[ -n "$ENGINE" ]] && return 0
  local in_docker=0 in_podman=0
  engine_works docker && has_containers docker && in_docker=1
  engine_works podman && has_containers podman && in_podman=1
  if [[ $in_docker -eq 1 && $in_podman -eq 1 ]]; then usage_error "the project $PROJECT has containers in Docker and in Podman: say which, --docker or --podman"; fi
  if [[ $in_podman -eq 1 ]]; then ENGINE=podman; elif [[ $in_docker -eq 1 ]]; then ENGINE=docker
  elif engine_works docker; then ENGINE=docker; elif engine_works podman; then ENGINE=podman
  else ENGINE=docker
  fi
}
pick_engine
E() { "$ENGINE" "$@"; }

# Compose for this project in DEPLOY. Podman: podman.yml on top (and the override, as -f drops it).
compose_files() {
  [[ $ENGINE == podman && -z "$(env_get COMPOSE_FILE)" ]] || return 0
  local f="docker-compose.yml:podman.yml"
  [[ -f "$DEPLOY/docker-compose.override.yml" ]] && f+=":docker-compose.override.yml"
  printf '%s' "$f"
}
dc() {
  local cf; cf="$(compose_files)"
  (cd "$DEPLOY" && env COMPOSE_PROJECT_NAME="$PROJECT" ${cf:+COMPOSE_FILE="$cf"} "$ENGINE" compose "$@")
}
# Down, once more if the first fails (rootless Podman's network teardown sometimes does).
dc_down() { dc down "$@" >/dev/null 2>&1 || { sleep 3; dc down "$@" >/dev/null 2>&1; }; }
# The environment backup.sh needs to aim at this project with these compose files.
backup_env() { local cf; cf="$(compose_files)"; env COMPOSE_PROJECT_NAME="$PROJECT" ${cf:+COMPOSE_FILE="$cf"} "$@"; }
BACKUP_SCRIPT=""
backup_sh() {
  local -a eng=(); [[ $ENGINE == podman ]] && eng=(--podman)
  backup_env bash "${BACKUP_SCRIPT:-$DEPLOY/scripts/backup.sh}" --deploy "$DEPLOY" "${eng[@]}" "$@"
}

image_id() { E image inspect --format '{{.Id}}' "$1" 2>/dev/null | head -n1 | sed 's/^sha256://'; }
# How compose names the image it builds for a service (Podman keeps it as localhost/NAME).
compose_image() { printf '%s-%s:latest' "$PROJECT" "$1"; }
# service|name|state|health|image id|image reference, for each of the project's containers.
container_rows() {
  local ids
  ids="$(E ps -aq --filter "label=com.docker.compose.project=$PROJECT" 2>/dev/null)"
  [[ -n "$ids" ]] || return 0
  # shellcheck disable=SC2086
  E inspect --format '{{index .Config.Labels "com.docker.compose.service"}}|{{.Name}}|{{.State.Status}}|{{if .State.Health}}{{.State.Health.Status}}{{end}}|{{.Image}}|{{.Config.Image}}' $ids 2>/dev/null \
    | sed -e 's/|\//|/' -e 's/|sha256:/|/'
}
volume_exists() { E volume inspect "$1" >/dev/null 2>&1; }
in_helper() { E run --rm --network none "$@"; }

# Settings from .env, with the stack's defaults.
read_settings() {
  DOMAIN="$(env_get DOMAIN)"; DOMAIN="${DOMAIN:-llm.localhost}"
  HTTP_PORT="$(env_get HTTP_PORT)"; HTTP_PORT="${HTTP_PORT:-80}"
  HTTPS_PORT="$(env_get HTTPS_PORT)"; HTTPS_PORT="${HTTPS_PORT:-443}"
  MODELS_DIR="$(env_get MODELS_DIR)"; MODELS_DIR="${MODELS_DIR:-./models}"
  case "$MODELS_DIR" in /*) ;; *) MODELS_DIR="$DEPLOY/${MODELS_DIR#./}" ;; esac
  BACKUP_DIR="$(env_get BACKUP_DIR)"; BACKUP_DIR="${BACKUP_DIR:-./backups}"
  case "$BACKUP_DIR" in /*) ;; *) BACKUP_DIR="$DEPLOY/${BACKUP_DIR#./}" ;; esac
}

# What the app and Argus say of themselves, through Traefik on this host.
fetch() {   # fetch HOST PATH [HEADER_FILE]
  command -v curl >/dev/null || return 1
  curl -sk --max-time 10 --resolve "$1:$HTTPS_PORT:127.0.0.1" ${3:+-H "@$3"} "https://$1:$HTTPS_PORT$2" 2>/dev/null
}
app_version() { fetch "$DOMAIN" /api/info | sed -n 's/.*"version" *: *"\([^"]*\)".*/\1/p' | head -n1; }
argus_version() {
  local hdr v
  hdr="$(mktemp)" && chmod 600 "$hdr" || return 1
  printf 'x-argus-admin-token: %s\n' "$(env_get ARGUS_KEY)" > "$hdr"
  v="$(fetch "argus.$DOMAIN" /admin/metrics "$hdr" | sed -n 's/^argus_index_build_info{version="\([^"]*\)"}.*/\1/p' | head -n1)"
  rm -f "$hdr"
  printf '%s' "$v"
}

# The version installed: the installer's record, a release's VERSION beside deploy/, or the app's word.
installed_version() {
  local v
  v="$(state_get version)"; [[ -n "$v" ]] && { printf '%s' "$v"; return; }
  [[ -f "$DIR/VERSION" ]] && { tr -d '[:space:]' < "$DIR/VERSION"; return; }
  read_settings
  app_version
}
VERSION_SOURCE=""
version_source() {
  if [[ -n "$(state_get version)" ]]; then echo "this installer's record"
  elif [[ -f "$DIR/VERSION" ]]; then echo "$DIR/VERSION"
  else echo "the running app (/api/info)"
  fi
}

# ===================================================================== logging
LOG="" TEE_PID=""
start_log() {   # start_log [soft]: soft, a log that cannot be written is left out (status, verify)
  [[ $DRY -eq 1 ]] && return 0
  LOG="${LOG_ARG:-$STATE/logs/$CMD-$(date +%Y%m%d-%H%M%S).log}"
  LOG="$(abspath "$LOG")"
  if ! (umask 077; mkdir -p "$(dirname "$LOG")" && : >> "$LOG") 2>/dev/null; then
    [[ ${1:-} == soft ]] && { LOG=""; return 0; }
    die "cannot write the log $LOG"
  fi
  [[ -d "$STATE" ]] && chmod 700 "$STATE" "$STATE/logs" 2>/dev/null
  # fd 3: the terminal alone, for what a person must see once and no log may keep.
  exec 3>&1 4>&2
  exec > >(tee -a "$LOG") 2>&1
  TEE_PID=$!
  say "installer.sh $CMD, $(date '+%Y-%m-%d %H:%M:%S'), log: $LOG"
}
LOCKED=0
lock() {
  mkdir -p "$STATE" && chmod 700 "$STATE" || die "cannot write $STATE"
  exec 9>"$STATE/lock"
  if command -v flock >/dev/null; then flock -n 9 || die "another installer run holds $STATE/lock"; fi
  LOCKED=1
}
TMPFILES=()
finish() {
  local rc=$?
  [[ ${#TMPFILES[@]} -gt 0 ]] && rm -rf "${TMPFILES[@]}"
  if [[ -n "$TEE_PID" ]]; then exec 1>&3 2>&4; wait "$TEE_PID" 2>/dev/null; fi
  exit "$rc"
}
trap finish EXIT

# ================================================================== requirements
REQ_FAIL=0 REQ_MODE=fresh
req_bad() { bad "$*"; REQ_FAIL=$((REQ_FAIL + 1)); }
req_host() { if [[ $REQ_MODE == fresh ]]; then req_bad "$*"; else note "$*"; fi; }
port_free() {   # nothing listens on it
  if command -v ss >/dev/null; then ! ss -Htln "sport = :$1" 2>/dev/null | grep -q .
  else ! (exec 3<>"/dev/tcp/127.0.0.1/$1") 2>/dev/null
  fi
}
gpu_ready() {   # an NVIDIA GPU, and the engine can hand it to containers
  command -v nvidia-smi >/dev/null && nvidia-smi -L 2>/dev/null | grep -q '^GPU' || return 1
  if [[ $ENGINE == podman ]]; then
    grep -qs 'nvidia.com/gpu' /etc/cdi/*.yaml /etc/cdi/*.json /var/run/cdi/*.yaml /var/run/cdi/*.json
  else
    E info --format '{{json .Runtimes}}' 2>/dev/null | grep -q nvidia || command -v nvidia-container-runtime-hook >/dev/null
  fi
}
engine_root() {
  if [[ $ENGINE == podman ]]; then E info --format '{{.Store.GraphRoot}}' 2>/dev/null; else E info --format '{{.DockerRootDir}}' 2>/dev/null; fi
}
images_raw_kb() { bundle_images | awk -F'\t' '{ s += $5 } END { printf "%d", s / 1024 }'; }
check_requirements() {   # check_requirements fresh|upgrade
  local cores ram_kb root free need p
  step "requirements"
  REQ_MODE=$1
  cores="$(nproc 2>/dev/null || echo 0)"
  if [[ $cores -ge 4 ]]; then ok "CPU: $cores cores"; else req_host "CPU: $cores cores; the stack needs 4 or more"; fi
  ram_kb="$(awk '/^MemTotal:/ { print $2 }' /proc/meminfo 2>/dev/null)"; ram_kb="${ram_kb:-0}"
  if [[ $ram_kb -ge $((15 * 1024 * 1024)) ]]; then ok "memory: $(human $((ram_kb * 1024)))"; else req_host "memory: $(human $((ram_kb * 1024))); the stack needs 16 GB or more"; fi
  if engine_works "$ENGINE"; then ok "$ENGINE: $(E --version 2>/dev/null | head -n1)"; else req_bad "$ENGINE does not answer ($ENGINE info): install it or start it"; fi
  if E compose version >/dev/null 2>&1; then ok "compose: $(E compose version 2>/dev/null | tail -n1)"; else req_bad "$ENGINE compose does not run: Docker's compose plugin is needed (Podman uses it too)"; fi
  if [[ $ENGINE == podman ]]; then
    if [[ -S "${XDG_RUNTIME_DIR:-/run/user/$(id -u)}/podman/podman.sock" || $EUID -eq 0 ]]; then ok "Podman's socket"
    else req_bad "Podman's socket is off: systemctl --user enable --now podman.socket"; fi
  fi
  if [[ $CPU_ONLY -eq 1 ]]; then note "GPU: none asked for (--cpu-only): $GPU_SERVICES left out"
  elif [[ $1 == upgrade ]]; then :
  elif gpu_ready; then ok "GPU: $(nvidia-smi -L 2>/dev/null | head -n1 | cut -d'(' -f1)"
  else req_bad "no NVIDIA GPU that $ENGINE can hand to containers (driver, nvidia-smi, the container toolkit$([[ $ENGINE == podman ]] && echo ", a CDI spec: sudo nvidia-ctk cdi generate --output=/etc/cdi/nvidia.yaml")): install them, or --cpu-only"
  fi
  root="$(engine_root)"
  if [[ -n "$root" ]]; then
    free="$(free_kb "$(existing_parent "$root")")"; need=$(( $(images_raw_kb) * 11 / 10 ))
    if [[ -n "$free" && $free -ge $need ]]; then ok "disk for the images: $(human $((free * 1024))) free in $root, $(human $((need * 1024))) needed"
    else req_bad "disk for the images: $(human $((${free:-0} * 1024))) free in $root, $(human $((need * 1024))) needed"; fi
  fi
  need=$(( ( $(du -sk "$BUNDLE/models" "$BUNDLE/packs" "$BUNDLE/code-arena" "$BUNDLE/docs" 2>/dev/null | awk '{ s += $1 } END { print s + 0 }') ) * 11 / 10 + 1024 * 1024 ))
  free="$(free_kb "$(existing_parent "$DIR")")"
  if [[ -n "$free" && $free -ge $need ]]; then ok "disk for $DIR: $(human $((free * 1024))) free, $(human $((need * 1024))) needed"
  else req_bad "disk for $DIR: $(human $((${free:-0} * 1024))) free, $(human $((need * 1024))) needed"; fi
  # Taken by this stack's own Traefik when install runs again: not a problem then.
  if [[ $1 == fresh ]] && ! E ps -q --filter "label=com.docker.compose.project=$PROJECT" 2>/dev/null | grep -q .; then
    for p in "$HTTP_PORT" "$HTTPS_PORT"; do
      if port_free "$p"; then ok "port $p is free"; else req_bad "port $p is taken: --http-port / --https-port another"; fi
    done
  fi
  for p in tar sha256sum gzip; do command -v "$p" >/dev/null || req_bad "$p is not installed"; done
  command -v curl >/dev/null || note "curl is not installed: the app's own version check is left out"
}

# ======================================================================== images
# The state's image list: reference<TAB>image id here<TAB>1 when it was on this host before
# Argus Arena was installed (remove leaves those).
state_image_id() { awk -F'\t' -v r="$1" '$1 == r { print $2 }' "$STATE/IMAGES" 2>/dev/null | tail -n1; }
state_image_pre() { awk -F'\t' -v r="$1" '$1 == r { print $3 }' "$STATE/IMAGES" 2>/dev/null | tail -n1; }
# The bundle's images into the engine, each only when it is not there already; the release's own
# images tagged as compose names them. fresh: an image already here is recorded as not ours.
load_images() {   # load_images fresh|upgrade|repair
  local ref file have pre n=0 skipped=0 tmp svc id out
  step "images ($ENGINE load; nothing is pulled)"
  tmp="$STATE/IMAGES.new"; : > "$tmp" || die "cannot write $tmp"
  while IFS=$'\t' read -r ref file _ _ _; do
    [[ -n "$ref" ]] || continue
    have="$(image_id "$ref")"
    pre="$(state_image_pre "$ref")"
    if [[ -z "$pre" ]]; then pre=0; [[ $1 == fresh && -n "$have" ]] && pre=1; fi
    if [[ -n "$have" && "$have" == "$(state_image_id "$ref")" ]]; then
      skipped=$((skipped + 1))
    else
      [[ -f "$BUNDLE/images/$file" ]] || die "the bundle has no images/$file (unpacked only in part?)"
      if ! out="$(E load -q -i "$BUNDLE/images/$file" 2>&1)"; then
        printf '%s\n' "$out" | tail -n 5 | sed 's/^/    /'
        die "$ENGINE load of $ref failed (disk full? see $ENGINE system df)"
      fi
      have="$(image_id "$ref")"
      [[ -n "$have" ]] || die "$ref is not in $ENGINE after loading images/$file"
      n=$((n + 1)); say "  loaded $ref$([[ $pre -eq 1 && "$have" != "$(state_image_id "$ref")" ]] && echo " (it was here before the install: remove keeps it)")"
    fi
    printf '%s\t%s\t%s\n' "$ref" "$have" "$pre" >> "$tmp"
  done < <(bundle_images)
  while IFS=$'\t' read -r svc ref; do
    [[ -n "$svc" ]] || continue
    id="$(image_id "$ref")"
    [[ -n "$id" ]] || die "the release's image $ref is not in $ENGINE"
    if [[ "$(image_id "$(compose_image "$svc")")" != "$id" ]]; then
      E tag "$ref" "$(compose_image "$svc")" || die "tagging $ref as $(compose_image "$svc") failed"
    fi
    printf '%s\t%s\t0\n' "$(compose_image "$svc")" "$id" >> "$tmp"
  done < <(bundle_release)
  ok "$n loaded, $skipped there already; the release's own tagged for $PROJECT ($(bundle_release | cut -f1 | paste -sd' ' -))"
}
record_images() {   # what load_images found and loaded, as the state's image list
  [[ -s "$STATE/IMAGES.new" ]] || return 0
  (umask 077; awk -F'\t' '!seen[$1]++' "$STATE/IMAGES.new" > "$STATE/IMAGES") && rm -f "$STATE/IMAGES.new"
}

# Every image compose would run is here (it pulls and builds nothing).
images_present() {
  local ref missing=()
  while IFS= read -r ref; do
    [[ -n "$ref" ]] || continue
    [[ -n "$(image_id "$ref")" ]] || missing+=("$ref")
  done < <(dc config --images 2>/dev/null | sort -u)
  if [[ ${#missing[@]} -gt 0 ]]; then bad "images compose needs that are not in $ENGINE: ${missing[*]}"; return 1; fi
  return 0
}

# =================================================================== deploy/ files
# The host's own files: never written or removed by an upgrade or a repair.
is_user_path() {
  case "$1" in
    .env.example|certs/README.md|models/.gitkeep) return 1 ;;
    .env|.env.*|docker-compose.override.yml|certs/*|config/traefik/certificate.yml|backups/*|models/*|*.before-*|.airgap-*|*/__pycache__/*|__pycache__/*) return 0 ;;
  esac
  local rel
  for rel in "${MODELS_DIR#"$DEPLOY"/}" "${BACKUP_DIR#"$DEPLOY"/}"; do
    [[ "$rel" != /* && -n "$rel" && "$1" == "$rel"/* ]] && return 0
  done
  return 1
}
# path<TAB>sha of the bundle's deploy files (its SHA256SUMS, without the deploy/ prefix).
bundle_deploy_sums() { sed -n -E 's|^([0-9a-f]{64}) [ *]deploy/(.*)$|\2\t\1|p' "$BUNDLE/SHA256SUMS" | sort; }
# path<TAB>sha of the release installed, when it is known: this installer's record, else the
# bundle's list for that release (made from its git tag).
old_deploy_sums() {   # old_deploy_sums VERSION
  if [[ -f "$STATE/deploy.sha256" ]]; then sed -E 's/^([0-9a-f]{64}) [ *](.*)$/\2\t\1/' "$STATE/deploy.sha256" | sort
  elif [[ -f "$BUNDLE/known/$1.sha256" ]]; then sed -E 's/^([0-9a-f]{64}) [ *](.*)$/\2\t\1/' "$BUNDLE/known/$1.sha256" | sort
  fi
}
installed_files() {   # the files in DEPLOY that are not the host's own
  (cd "$DEPLOY" 2>/dev/null && find . -type f -printf '%P\n' | sort) | while IFS= read -r f; do is_user_path "$f" || printf '%s\n' "$f"; done
}
# Brings DEPLOY to the bundle's files. The host's own (.env, overrides, certificates, backups,
# models) are never touched. A release file someone changed is replaced, their copy kept beside it
# as FILE.before-VERSION; a file the new release dropped is removed only when it is the old
# release's own, unchanged; every other file stays. Each is listed. plan: only says so.
# Writes $SYNC_LOG: one line per file it added (to undo a failed upgrade).
SYNC_ADDED=""
sync_deploy() {   # sync_deploy install|upgrade|repair OLD_VERSION plan|do
  local mode=$1 oldv=$2 act=$3 f sum cur old n_add=0 n_upd=0 n_mine=0 n_rm=0 n_keep=0
  local -A OLD=() NEW=()
  while IFS=$'\t' read -r f sum; do [[ -n "$f" ]] && NEW["$f"]=$sum; done < <(bundle_deploy_sums)
  while IFS=$'\t' read -r f sum; do [[ -n "$f" ]] && OLD["$f"]=$sum; done < <(old_deploy_sums "$oldv")
  [[ $act == do ]] && mkdir -p "$DEPLOY"
  for f in $(printf '%s\n' "${!NEW[@]}" | sort); do
    is_user_path "$f" && continue
    if [[ ! -e "$DEPLOY/$f" ]]; then
      n_add=$((n_add + 1))
      [[ $mode == install ]] || { [[ $act == do ]] && did "added $f" || would "add $f"; }
      [[ $act == do ]] && { mkdir -p "$DEPLOY/$(dirname "$f")" && cp -p "$BUNDLE/deploy/$f" "$DEPLOY/$f" || die "writing $DEPLOY/$f failed"; [[ -n "$SYNC_ADDED" ]] && echo "$f" >> "$SYNC_ADDED"; }
      continue
    fi
    cur="$(sha_of "$DEPLOY/$f")"
    [[ "$cur" == "${NEW[$f]}" ]] && continue
    old="${OLD[$f]:-}"
    if [[ $mode == install || ( -n "$old" && "$cur" == "$old" ) ]]; then
      n_upd=$((n_upd + 1))
      [[ $act == do ]] && did "updated $f" || would "update $f"
    else
      n_mine=$((n_mine + 1))
      if [[ $act == do ]]; then
        cp -p "$DEPLOY/$f" "$DEPLOY/$f.before-$BVERSION" || die "keeping $f failed"
        [[ -n "$SYNC_ADDED" ]] && echo "$f.before-$BVERSION" >> "$SYNC_ADDED"
        did "updated $f; the copy you had is kept as $f.before-$BVERSION"
      else
        would "update $f, which was changed here: that copy kept as $f.before-$BVERSION"
      fi
    fi
    [[ $act == do ]] && { cp -p "$BUNDLE/deploy/$f" "$DEPLOY/$f.installer-new" && mv -f "$DEPLOY/$f.installer-new" "$DEPLOY/$f" || die "writing $DEPLOY/$f failed"; }
  done
  if [[ $mode != install ]]; then
    while IFS= read -r f; do
      [[ -n "$f" && -z "${NEW[$f]:-}" ]] || continue
      old="${OLD[$f]:-}"
      if [[ $mode == upgrade && -n "$old" && "$(sha_of "$DEPLOY/$f")" == "$old" ]]; then
        n_rm=$((n_rm + 1))
        if [[ $act == do ]]; then rm -f "$DEPLOY/$f" && did "removed $f: not in $BVERSION (it was $oldv's, unchanged)"; else would "remove $f: not in $BVERSION (it is $oldv's, unchanged)"; fi
      else
        n_keep=$((n_keep + 1))
        note "kept $f: not in $BVERSION$([[ -n "$old" ]] && echo ", and changed here" || echo "; yours, or from a release before")"
      fi
    done < <(installed_files)
  fi
  SYNC_SUMMARY="$n_add added, $n_upd updated, $n_mine updated with your copy kept beside, $n_rm removed, $n_keep kept"
  SYNC_CHANGES=$((n_add + n_upd + n_mine + n_rm))
}
SYNC_SUMMARY="" SYNC_CHANGES=0
record_deploy() {   # what this installer wrote, to tell later what was changed
  (umask 077; bundle_deploy_sums | awk -F'\t' '{ print $2 "  " $1 }' > "$STATE/deploy.sha256")
}

# Beside deploy/: the docs, the licences, Code Arena's packages, the knowledge packs, VERSION.
write_extras() {   # write_extras plan|do
  local f
  if [[ $1 == plan ]]; then
    would "write docs/, LICENSE.md, LICENSING.md and VERSION ($BVERSION) in $DIR"
    [[ -d "$BUNDLE/code-arena" ]] && would "put Code Arena's packages in $DIR/code-arena (the earlier ones replaced)"
    [[ -n "$(ls -A "$BUNDLE/packs" 2>/dev/null)" ]] && would "add the bundle's knowledge packs to $DIR/packs (others there kept)"
    return 0
  fi
  mkdir -p "$DIR/packs" || die "cannot write $DIR"
  rm -rf "$DIR/docs.installer-new" && cp -a "$BUNDLE/docs" "$DIR/docs.installer-new" 2>/dev/null \
    && rm -rf "$DIR/docs" && mv "$DIR/docs.installer-new" "$DIR/docs"
  for f in LICENSE.md LICENSING.md VERSION; do [[ -f "$BUNDLE/$f" ]] && cp -p "$BUNDLE/$f" "$DIR/$f"; done
  if [[ -d "$BUNDLE/code-arena" ]]; then
    rm -rf "$DIR/code-arena" && mkdir -p "$DIR/code-arena" && cp -p "$BUNDLE/code-arena/." "$DIR/code-arena/" 2>/dev/null \
      || cp -a "$BUNDLE/code-arena/." "$DIR/code-arena/"
  fi
  if [[ -d "$BUNDLE/packs" ]]; then
    for f in "$BUNDLE"/packs/*; do [[ -f "$f" ]] && { ln -f "$f" "$DIR/packs/" 2>/dev/null || cp -p "$f" "$DIR/packs/"; }; done
  fi
  ok "docs/, the licences, VERSION$([[ -d "$BUNDLE/code-arena" ]] && echo ", code-arena/")$([[ -n "$(ls -A "$BUNDLE/packs" 2>/dev/null)" ]] && echo ", packs/") in $DIR"
}

# ============================================================================ .env
# KEY<TAB>default for each setting .env.example names (commented ones too: they are known).
example_keys() { sed -n -E 's/^#?[[:space:]]*([A-Z][A-Z0-9_]*)=([^[:space:]]*).*/\1\t\2/p' "$1" | awk -F'\t' '!seen[$1]++'; }
example_set_keys() { sed -n -E 's/^([A-Z][A-Z0-9_]*)=(.*)$/\1\t\2/p' "$1"; }
# Keys compose cannot start without (${KEY:?...}).
required_keys() { grep -ohE '\$\{[A-Z][A-Z0-9_]*:\?' "${1:-$DEPLOY}"/docker-compose.yml 2>/dev/null | sed -E 's/\$\{([A-Z0-9_]+):\?/\1/' | sort -u; }
compose_keys() { grep -ohE '\$\{[A-Z][A-Z0-9_]*' "${1:-$DEPLOY}"/*.yml 2>/dev/null | cut -c3- | sort -u; }
# Adds to .env every key the release's .env.example sets that .env lacks (its default; a
# required secret generated), and lists them. Nothing is removed; keys no longer used are named.
ENV_ADDED=() ENV_ADDED_FILE=""
merge_env() {   # merge_env plan|do (the release's .env.example and compose files: the bundle's)
  local env="$DEPLOY/.env" example="$BUNDLE/deploy/.env.example" key def val tmp msg unused=()
  local -a add=()
  local -A have=() known=() req=()
  while IFS= read -r key; do have["$key"]=1; done < <(sed -n -E 's/^([A-Z][A-Z0-9_]*)=.*/\1/p' "$env")
  while IFS= read -r key; do req["$key"]=1; done < <(required_keys "$BUNDLE/deploy")
  while IFS=$'\t' read -r key def; do
    [[ -n "$key" && -z "${have[$key]:-}" ]] || continue
    if [[ -z "$def" && -n "${req[$key]:-}" ]]; then add+=("$key"$'\t'"@secret"); else add+=("$key"$'\t'"$def"); fi
  done < <(example_set_keys "$example")
  ENV_ADDED=()
  if [[ ${#add[@]} -gt 0 ]]; then
    if [[ $1 == do ]]; then
      tmp="$env.installer-new"
      (umask 077; cat "$env" > "$tmp" && { echo ""; echo "# Added by installer.sh for $BVERSION, $(date +%Y-%m-%d): new in this release."; } >> "$tmp") || die "cannot write $tmp"
    fi
    for key in "${add[@]}"; do
      def="${key#*$'\t'}"; key="${key%%$'\t'*}"
      if [[ "$def" == @secret ]]; then val="$(gen_secret)"; [[ $key == GATEWAY_KEY ]] && val="sk-$val"; else val="$def"; fi
      [[ $1 == do ]] && printf '%s=%s\n' "$key" "$val" >> "$tmp"
      ENV_ADDED+=("$key")
      [[ $1 == do && -n "$ENV_ADDED_FILE" ]] && echo "$key" >> "$ENV_ADDED_FILE"
      if [[ "$def" == @secret ]]; then msg="$key (a new secret, generated)"
      elif is_secret "$key"; then msg="$key (its default)"
      else msg="$key=$val (the default)"; fi
      if [[ $1 == do ]]; then did ".env: added $msg"; else would "add to .env: $msg"; fi
    done
    if [[ $1 == do ]]; then chmod --reference="$env" "$tmp" 2>/dev/null; chown --reference="$env" "$tmp" 2>/dev/null; mv -f "$tmp" "$env" || die "writing $env failed"; fi
  else
    ok ".env has every key $BVERSION's .env.example sets"
  fi
  # Named, never removed: what this release no longer reads.
  while IFS=$'\t' read -r key def; do known["$key"]=1; done < <(example_keys "$example")
  while IFS= read -r key; do known["$key"]=1; done < <(compose_keys "$BUNDLE/deploy")
  while IFS= read -r key; do
    [[ -n "${known[$key]:-}" || "$key" == COMPOSE_* || "$key" == BACKUP_* ]] && continue
    unused+=("$key")
  done < <(sed -n -E 's/^([A-Z][A-Z0-9_]*)=.*/\1/p' "$env" | sort -u)
  [[ ${#unused[@]} -gt 0 ]] && note ".env: not read by $BVERSION, kept: ${unused[*]}"
  return 0
}

# A new .env from .env.example: the answers, and secrets generated (none of them printed or logged).
ADMIN_PASSWORD_NEW="" NEW_GITLAB="" NEW_GITLAB_TOKEN="" NO_GITLAB=0
make_env() {
  local env="$DEPLOY/.env" tmp key line val model example="$DEPLOY/.env.example"
  local -A V=()
  V[DOMAIN]="$NEW_DOMAIN"; V[ADMIN_EMAIL]="$NEW_EMAIL"; V[MODELS_DIR]="$NEW_MODELS_DIR"; V[MODEL]="$NEW_MODEL"
  # Every key compose cannot start without gets a secret of its own (the gateway's starts with sk-).
  for key in DB_PASSWORD APP_KEY ENGINE_KEY ARGUS_KEY $(required_keys "$DEPLOY"); do [[ -n "${V[$key]:-}" ]] || V[$key]="$(gen_secret)"; done
  V[GATEWAY_KEY]="sk-$(gen_secret)"
  ADMIN_PASSWORD_NEW="$(gen_secret | cut -c1-24)"; V[ADMIN_PASSWORD]="$ADMIN_PASSWORD_NEW"
  tmp="$env.installer-new"
  (
    umask 077
    while IFS= read -r line || [[ -n "$line" ]]; do
      key="${line%%=*}"
      if [[ "$line" =~ ^[A-Z][A-Z0-9_]*= && -n "${V[$key]+x}" ]]; then
        printf '%s=%s\n' "$key" "${V[$key]}"
        [[ $key == MODEL && -z "${V[$key]}" ]] && echo "# (none yet: put a .gguf in MODELS_DIR and add it under Admin -> Models; offline, never repo:quant)"
      else
        printf '%s\n' "$line"
      fi
    done < "$example"
    echo ""
    echo "# Written by installer.sh ($BVERSION), $(date +%Y-%m-%d)."
    [[ "$NEW_HTTP" != 80 ]] && echo "HTTP_PORT=$NEW_HTTP"
    [[ "$NEW_HTTPS" != 443 ]] && echo "HTTPS_PORT=$NEW_HTTPS"
    [[ -n "$ACME_ARG" ]] && echo "ACME_EMAIL=$ACME_ARG"
    [[ -n "$NEW_GITLAB" ]] && { echo "GITLAB_URL=$NEW_GITLAB"; echo "GITLAB_TOKEN=$NEW_GITLAB_TOKEN"; }
    if [[ "$PROJECT" != arena ]]; then echo "COMPOSE_PROJECT_NAME=$PROJECT"; fi
  ) > "$tmp" || die "cannot write $tmp"
  mv -f "$tmp" "$env" && chmod 600 "$env" || die "writing $env failed"
  did ".env written (0600): DOMAIN=$NEW_DOMAIN, MODELS_DIR=$NEW_MODELS_DIR, MODEL=${NEW_MODEL:-none yet}${NEW_GITLAB:+, GITLAB_URL=$NEW_GITLAB and its token}; the secrets generated, none printed"
}

# docker-compose.override.yml for this host, when it has none: what it leaves out, and the
# speech server kept off the network. Yours afterwards: upgrades and repairs keep it.
write_override() {   # write_override plan|do SERVICES...
  local act=$1 svc f="$DEPLOY/docker-compose.override.yml" audio_on=1
  shift
  for svc in "$@"; do [[ $svc == audio ]] && audio_on=0; done
  if [[ -f "$f" ]]; then
    note "docker-compose.override.yml is there: kept as it is"
    return 0
  fi
  if [[ $act == plan ]]; then
    would "write docker-compose.override.yml: ${*:+$* left out; }the speech server offline (HF_HUB_OFFLINE=1)"
    return 0
  fi
  {
    echo "# Written by installer.sh ($BVERSION) for this host. Yours to change: upgrades and repairs keep it."
    echo "services:"
    # !override: a module with a profile of its own (Laya) would otherwise still run when asked for.
    for svc in "$@"; do echo "  $svc: { profiles: !override [off] }"; done
    if [[ $NO_GITLAB -eq 1 ]]; then
      echo "  # Argus waits for a GitLab: put GITLAB_URL and GITLAB_TOKEN in .env, take out the argus line above, then up -d."
    fi
    if [[ $audio_on -eq 1 ]]; then
      echo "  # No network here: the speech server uses the models it has and never looks for others."
      echo "  audio: { environment: { HF_HUB_OFFLINE: \"1\" } }"
    fi
  } > "$f" || die "cannot write $f"
  did "docker-compose.override.yml: ${*:+$* left out; }the speech server offline"
}

# ========================================================================= models
place_models() {   # place_models plan|do
  local f n=0 absent=0 kind path
  if [[ -d "$BUNDLE/models/library" ]]; then
    if [[ $1 == plan ]]; then would "put the bundle's models in $MODELS_DIR ($(find "$BUNDLE/models/library" -type f | wc -l) files; those there already kept)"; return 0; fi
    mkdir -p "$MODELS_DIR" || die "cannot make $MODELS_DIR"
    while IFS= read -r f; do
      [[ -f "$MODELS_DIR/$f" && "$(stat -c %s "$MODELS_DIR/$f")" == "$(stat -c %s "$BUNDLE/models/library/$f")" ]] && continue
      mkdir -p "$MODELS_DIR/$(dirname "$f")" || die "cannot write $MODELS_DIR"
      ln -f "$BUNDLE/models/library/$f" "$MODELS_DIR/$f" 2>/dev/null || cp "$BUNDLE/models/library/$f" "$MODELS_DIR/$f" || die "copying $f into $MODELS_DIR failed (disk full?)"
      n=$((n + 1)); say "  model $f"
    done < <(cd "$BUNDLE/models/library" && find . -type f -printf '%P\n' | sort)
    ok "$n model file(s) put in $MODELS_DIR"
  else
    [[ $1 == do ]] && mkdir -p "$MODELS_DIR"
    while IFS=$'\t' read -r kind _ path; do
      [[ -n "$path" && ! -f "$MODELS_DIR/$path" ]] || continue
      absent=$((absent + 1)); note "bring $path ($kind) into $MODELS_DIR: the bundle has no models"
    done < <(tail -n +2 "$BUNDLE/models/MODELS" 2>/dev/null)
    [[ $absent -eq 0 ]] && ok "models: every model the bundle lists is in $MODELS_DIR"
  fi
  # Docker runs the app as uid 1000: it writes there. (Rootless Podman runs it as you.)
  if [[ $1 == do && $ENGINE == docker && $EUID -eq 0 && "$(stat -c %u "$MODELS_DIR")" != 1000 ]]; then
    chown -R 1000:1000 "$MODELS_DIR" && did "$MODELS_DIR is uid 1000's, the app's"
  fi
  return 0
}

# The speech server's models into the audio volume, when it has none.
fill_audio() {   # fill_audio plan|do
  local vol="${PROJECT}_audio"
  [[ -f "$BUNDLE/audio/audio.tar.gz" || ( $PARTIAL == 1 && "$(bundle_get audio)" == yes ) ]] || return 0
  if volume_exists "$vol" && [[ -n "$(in_helper -v "$vol:/v:ro" "$HELPER" sh -c 'ls -A /v | head -n1' 2>/dev/null)" ]]; then
    ok "the speech server's models: $vol has them, kept"; return 0
  fi
  if [[ $1 == plan ]]; then would "fill $vol with the speech server's models"; return 0; fi
  volume_exists "$vol" || E volume create --label "com.docker.compose.project=$PROJECT" --label com.docker.compose.volume=audio "$vol" >/dev/null \
    || die "cannot make the $vol volume"
  in_helper -v "$vol:/target" -v "$BUNDLE/audio:/bundle:ro" "$HELPER" tar -xzf /bundle/audio.tar.gz --numeric-owner -C /target \
    || die "filling $vol failed"
  did "the speech server's models in $vol"
}

# ============================================================== start and check
start_stack() {
  step "starting $PROJECT ($ENGINE compose up; nothing pulled or built)"
  images_present || return 1
  dc up -d --no-build --pull never --remove-orphans > "$STATE/up.log" 2>&1 || { tail -n 20 "$STATE/up.log" | sed 's/^/    /'; bad "$ENGINE compose up failed"; return 1; }
  ok "started"
}
# Every service compose runs: up, and healthy where it has a health check. Those in EXCUSED
# (not up before an upgrade either) are named, and do not hold it up.
EXCUSED=""
# The services not up now, while the stack runs: an upgrade does not answer for them.
not_up_now() {
  local svc st health
  local -A good=() seen=()
  container_rows | grep -q '|running|' || return 0
  while IFS='|' read -r svc _ st health _ _; do
    [[ -n "$svc" ]] || continue
    seen[$svc]=1
    [[ $st == running && ( -z "$health" || $health == healthy ) ]] && good[$svc]=1
  done < <(container_rows)
  for svc in $(dc config --services 2>/dev/null); do [[ -n "${good[$svc]:-}" ]] || printf '%s ' "$svc"; done
}
wait_healthy() {
  local secs=$(( ${TIMEOUT%s} * 60 )) last=0 services svc waiting n m deadline
  [[ $TIMEOUT == *s ]] && secs=${TIMEOUT%s}
  deadline=$(( $(date +%s) + secs ))
  services="$(dc config --services 2>/dev/null | sort -u)"
  step "waiting for every service to be up and healthy (up to $(( secs / 60 )) min $(( secs % 60 )) s)"
  while :; do
    local -A STATEOF=()
    while IFS='|' read -r svc _ st health _ _; do
      [[ -n "$svc" ]] || continue
      if [[ $st == running && ( -z "$health" || $health == healthy ) ]]; then STATEOF[$svc]="${STATEOF[$svc]:-ok}"
      else STATEOF[$svc]="$st${health:+ ($health)}"; fi
    done < <(container_rows)
    waiting=(); n=0; m=0
    local excused=()
    for svc in $services; do
      m=$((m + 1))
      if [[ "${STATEOF[$svc]:-}" == ok ]]; then n=$((n + 1))
      elif [[ " $EXCUSED " == *" $svc "* ]]; then excused+=("$svc: ${STATEOF[$svc]:-no container}")
      else waiting+=("$svc: ${STATEOF[$svc]:-no container}"); fi
    done
    if [[ ${#waiting[@]} -eq 0 ]]; then
      ok "$n of $m services up"
      [[ ${#excused[@]} -gt 0 ]] && note "not up, as before the upgrade: ${excused[*]} (status, repair)"
      return 0
    fi
    if [[ $(date +%s) -ge $deadline ]]; then
      bad "after $(( secs / 60 )) min $(( secs % 60 )) s, $n of $m services are up; not: ${waiting[*]}"
      say "    their logs: $ENGINE logs --tail 50 CONTAINER (the names: $ENGINE ps -a)"
      return 1
    fi
    if [[ $(( $(date +%s) - last )) -ge 30 ]]; then say "  $n of $m up; waiting for: ${waiting[*]}"; last=$(date +%s); fi
    sleep $(( secs < 30 ? 1 : 5 ))
  done
}
# The version each service runs: the app and Argus say theirs; every container runs the image
# its reference names now, and the release's own run this bundle's.
check_versions() {   # check_versions VERSION
  local want=$1 v failed=0 svc name img ref id
  step "versions"
  read_settings
  v="$(app_version)"
  if [[ "$v" == "$want" ]]; then ok "app: $v (its own /api/info)"
  elif [[ -z "$v" ]]; then bad "app: no answer at https://$DOMAIN:$HTTPS_PORT/api/info"; failed=1
  else bad "app: it says $v, not $want"; failed=1
  fi
  if dc config --services 2>/dev/null | grep -qx argus; then
    v="$(argus_version)"
    if [[ "$v" == "$want" ]]; then ok "argus: $v (its own metrics)"
    elif [[ -z "$v" ]]; then note "argus: no answer at argus.$DOMAIN (its image is checked below)"
    else bad "argus: it says $v, not $want"; failed=1
    fi
  fi
  while IFS='|' read -r svc name _ _ img ref; do
    [[ -n "$svc" ]] || continue
    id="$(image_id "$ref")"
    if [[ -n "$id" && "$img" != "$id" ]]; then bad "$svc ($name) runs an older image than $ref has now"; failed=1; continue; fi
    if [[ "$want" == "$BVERSION" ]] && v="$(bundle_release | awk -F'\t' -v s="$svc" '$1 == s { print $2 }')" && [[ -n "$v" ]]; then
      if [[ "$img" == "$(image_id "$v")" ]]; then ok "$svc: $v"; else bad "$svc: not $v"; failed=1; fi
    fi
  done < <(container_rows | sort)
  return $failed
}
record_state() {   # what is installed now
  (umask 077; cp "$BUNDLE/MANIFEST" "$STATE/MANIFEST")
  record_deploy; record_images
  state_set version "$BVERSION" engine "$ENGINE" project "$PROJECT" installed "$(date '+%Y-%m-%d %H:%M:%S')" status installed
}

# =================================================================== permissions
# The volumes the services write, owned by the services' users; Docker's MODELS_DIR by uid 1000.
fix_permissions() {   # fix_permissions plan|do -> FIX_FAILED
  local entry vol uid gid have
  FIX_FAILED=0
  for entry in $VOLUME_OWNERS; do
    IFS=: read -r vol uid gid <<<"$entry"
    volume_exists "${PROJECT}_$vol" || continue
    have="$(in_helper -v "${PROJECT}_$vol:/v:ro" "$HELPER" stat -c %u:%g /v 2>/dev/null)"
    if [[ -z "$have" ]]; then bad "the $vol volume: cannot read its owner"; FIX_FAILED=1; continue; fi
    [[ "$have" == "$uid:$gid" ]] && continue
    if [[ $1 == plan ]]; then would "give the $vol volume to $uid:$gid (it is $have's)"; continue; fi
    if in_helper -v "${PROJECT}_$vol:/v" "$HELPER" chown -R "$uid:$gid" /v; then did "the $vol volume was $have's: now $uid:$gid's, its service's"
    else bad "the $vol volume is $have's, not $uid:$gid's, and chown failed"; FIX_FAILED=1; fi
  done
  if [[ $ENGINE == docker && -d "$MODELS_DIR" && "$(stat -c %u "$MODELS_DIR")" != 1000 ]]; then
    if [[ $1 == plan ]]; then would "give $MODELS_DIR to uid 1000 (the app writes there)"
    elif [[ $EUID -eq 0 ]] && chown -R 1000:1000 "$MODELS_DIR"; then did "$MODELS_DIR is now uid 1000's (the app writes there)"
    else bad "$MODELS_DIR is not uid 1000's and the app writes there: sudo chown -R 1000:1000 $MODELS_DIR"; FIX_FAILED=1; fi
  fi
  [[ $FIX_FAILED -eq 0 ]] && ok "the volumes' owners are their services' users"
  return 0
}

# ================================================================== the summary
print_access() {
  local port=""
  read_settings
  [[ "$HTTPS_PORT" != 443 ]] && port=":$HTTPS_PORT"
  say ""
  say "Argus Arena $BVERSION runs: https://$DOMAIN$port"
  say "  the API:  https://gateway.$DOMAIN$port/v1     Argus:  https://argus.$DOMAIN$port/mcp"
  say "  sign in as admin$([[ -n "$(env_get ADMIN_EMAIL)" ]] && echo " ($(env_get ADMIN_EMAIL))")"
  say "  in $DEPLOY: $ENGINE compose$([[ -n "$(compose_files)" ]] && echo " -f ${COMPOSE_FILES_HINT:-$(compose_files | sed 's/:/ -f /g')}") ps"
}
show_password_once() {
  [[ -n "$ADMIN_PASSWORD_NEW" ]] || return 0
  if [[ -n "$TEE_PID" && -t 3 ]]; then
    # To the terminal only: never into the log.
    printf '\n  The admin'"'"'s first password, shown once and kept nowhere but .env:\n\n      %s\n\n  Sign in and change it (Settings -> Account).\n' "$ADMIN_PASSWORD_NEW" >&3
    say "  (the admin's first password was shown on the terminal, not logged; it is ADMIN_PASSWORD in $DEPLOY/.env)"
  else
    say "  the admin's first password is ADMIN_PASSWORD in $DEPLOY/.env (0600): not printed, as this run has no terminal"
  fi
}

# ======================================================================= install
cmd_install() {
  local iv leave=() svc s again=0
  need_bundle
  iv="$(state_get version)"
  if [[ -n "$iv" && "$iv" != "$BVERSION" ]]; then refuse "$DIR has $iv installed: upgrade it (or remove it first)"; fi
  # Again over the same version, or carrying on one that was cut off: each step checks first.
  [[ -n "$iv" || "$(state_get target)" == "$BVERSION" ]] && again=1
  if [[ $again -eq 0 ]]; then
    [[ -f "$DEPLOY/docker-compose.yml" ]] && refuse "$DEPLOY holds an installation this installer did not make: upgrade it"
    if E ps -aq --filter "label=com.docker.compose.project=$PROJECT" 2>/dev/null | grep -q .; then refuse "a stack named $PROJECT runs in $ENGINE already: upgrade it, or --project another name"; fi
    if E volume ls -q 2>/dev/null | grep -q "^${PROJECT}_postgres\$"; then refuse "the volume ${PROJECT}_postgres holds data from an earlier installation, whose .env opens it: restore that .env and upgrade, or remove those volumes (remove --purge)"; fi
  fi
  # What this host leaves out: the bundle's own, no GPU, and as asked.
  for svc in $BLEFT; do leave+=("$svc"); done
  [[ $CPU_ONLY -eq 1 ]] && for svc in $GPU_SERVICES; do leave+=("$svc"); done
  for svc in ${LEAVE_ARG//,/ }; do leave+=("$svc"); done
  mapfile -t leave < <(printf '%s\n' ${leave[@]+"${leave[@]}"} | sed '/^$/d' | sort -u)
  if [[ -n "$BLEFT" && $CPU_ONLY -eq 0 ]]; then
    for svc in $GPU_SERVICES; do [[ " $BLEFT " == *" $svc "* ]] && { CPU_ONLY=1; break; }; done
  fi

  local podman=0; [[ $ENGINE == podman ]] && podman=1
  # Only services the release has (a GPU service a release lacks needs no leaving out).
  local known; known=" $(sed -n '/^services:/,/^[^ ]/s/^  \([a-z0-9-]*\):.*/\1/p' "$BUNDLE/deploy/docker-compose.yml" | tr '\n' ' ') "
  for svc in ${LEAVE_ARG//,/ }; do [[ "$known" == *" $svc "* ]] || usage_error "--leave-out $svc: no such service (they are:${known% })"; done
  mapfile -t leave < <(for svc in ${leave[@]+"${leave[@]}"}; do [[ "$known" == *" $svc "* ]] && echo "$svc"; done)

  [[ $YES -eq 1 || $DRY -eq 1 || $TTY -eq 1 ]] || refuse "no terminal to ask the domain, ports and models: give them as options, with --yes"
  if [[ $again -eq 1 && -f "$DEPLOY/.env" ]]; then
    read_settings
    NEW_DOMAIN="$DOMAIN" NEW_HTTP="$HTTP_PORT" NEW_HTTPS="$HTTPS_PORT" NEW_MODELS_DIR="$(env_get MODELS_DIR)" NEW_MODEL="$(env_get MODEL)" NEW_EMAIL="$(env_get ADMIN_EMAIL)"
  else
    NEW_DOMAIN="$(ask "The address people open (DOMAIN)" "${DOMAIN_ARG:-llm.localhost}")"
    NEW_EMAIL="$(ask "The admin's e-mail" "${EMAIL_ARG:-admin@example.com}")"
    NEW_MODELS_DIR="$(ask "Where models live (MODELS_DIR)" "${MODELS_ARG:-./models}")"
    s="$(bundle_get model)"; [[ -d "$BUNDLE/models/library" || $PARTIAL == 1 && "$(bundle_get models)" == copied* ]] || s=""
    NEW_MODEL="$(ask "The first chat model, a .gguf in MODELS_DIR (empty: none yet)" "${MODEL_ARG:-$s}")"
    NEW_HTTP="$(ask "HTTP port" "${HTTP_ARG:-$([[ $podman -eq 1 ]] && echo 8080 || echo 80)}")"
    NEW_HTTPS="$(ask "HTTPS port" "${HTTPS_ARG:-$([[ $podman -eq 1 ]] && echo 8443 || echo 443)}")"
    NEW_GITLAB="$(ask "The GitLab Argus indexes, its address (empty: none yet)" "$GITLAB_ARG")"
    NEW_GITLAB_TOKEN=""
    if [[ -n "$GITLAB_TOKEN_FILE" ]]; then NEW_GITLAB_TOKEN="$(head -n1 "$GITLAB_TOKEN_FILE" | tr -d '[:space:]')"
    elif [[ -n "$NEW_GITLAB" && $YES -eq 0 && $DRY -eq 0 ]]; then
      printf 'Its read-only token (read_api, read_repository; not shown): ' >&2; read -r -s NEW_GITLAB_TOKEN <&7 || true; echo >&2
    fi
    if [[ -z "$NEW_GITLAB" || -z "$NEW_GITLAB_TOKEN" ]]; then
      [[ -n "$NEW_GITLAB" && $DRY -eq 0 ]] && refuse "--gitlab-url needs its token: --gitlab-token-file FILE"
      NEW_GITLAB=""
      # Argus cannot start without a GitLab: left out until .env names one.
      [[ "$known" == *" argus "* && " ${leave[*]} " != *" argus "* ]] && { leave+=(argus); NO_GITLAB=1; }
    fi
  fi
  [[ "$NEW_DOMAIN" =~ ^[A-Za-z0-9.-]+$ ]] || usage_error "the domain is a host name: $NEW_DOMAIN"
  [[ "$NEW_MODEL" == *:* ]] && refuse "MODEL=$NEW_MODEL is a Hugging Face name (repo:quant), which needs the network: name a .gguf file in MODELS_DIR"
  HTTP_PORT="$NEW_HTTP" HTTPS_PORT="$NEW_HTTPS" DOMAIN="$NEW_DOMAIN"
  MODELS_DIR="$NEW_MODELS_DIR"; case "$MODELS_DIR" in /*) ;; *) MODELS_DIR="$DEPLOY/${MODELS_DIR#./}" ;; esac
  BACKUP_DIR="$DEPLOY/backups"

  say "Install Argus Arena $BVERSION in $DIR ($ENGINE, project $PROJECT)$([[ $DRY -eq 1 ]] && echo " -- dry run: nothing is done")"
  say "  https://$NEW_DOMAIN$([[ "$NEW_HTTPS" != 443 ]] && echo ":$NEW_HTTPS"), models in $MODELS_DIR${leave[*]:+, left out: ${leave[*]}}"
  [[ $NO_GITLAB -eq 1 ]] && note "no GitLab given (--gitlab-url, --gitlab-token-file): Argus is left out; to add it later, GITLAB_URL and GITLAB_TOKEN in .env, its line out of docker-compose.override.yml, then up -d"
  if [[ $DRY -eq 1 ]]; then
    [[ -n "$BUNDLE" ]] && check_bundle
    check_requirements fresh
    [[ $REQ_FAIL -gt 0 ]] && note "$REQ_FAIL requirement(s) not met: install would stop here$([[ $SKIP_REQ -eq 1 ]] && echo " (not with --skip-requirements)")"
    step "the plan"
    would "load $(bundle_images | wc -l) images into $ENGINE (those there already kept) and tag the release's own for $PROJECT"
    would "write $(bundle_deploy_sums | wc -l) files into $DEPLOY"
    write_extras plan
    [[ -f "$DEPLOY/.env" ]] && would "keep the .env there, adding what it lacks" || would "write $DEPLOY/.env: these answers, the secrets generated (none printed; the admin's first password shown once, on the terminal)"
    write_override plan ${leave[@]+"${leave[@]}"}
    place_models plan
    fill_audio plan
    [[ $MAKE_CERT -eq 1 ]] && would "make a certificate for $NEW_DOMAIN (scripts/make-cert.sh)"
    [[ $HOSTS -eq 1 ]] && would "add the names to /etc/hosts (scripts/setup-hosts.sh)"
    would "start it ($ENGINE compose up -d --pull never), wait until every service is healthy and check the versions"
    return 0
  fi
  confirm "Install Argus Arena $BVERSION in $DIR?" || refuse "not confirmed"
  mkdir -p "$DIR" || die "cannot make $DIR"
  lock; start_log
  check_bundle
  check_requirements fresh
  if [[ $REQ_FAIL -gt 0 ]]; then
    [[ $SKIP_REQ -eq 1 ]] && note "$REQ_FAIL requirement(s) not met; going on (--skip-requirements)" || refuse "$REQ_FAIL requirement(s) not met (above); --skip-requirements goes on anyway"
  fi
  state_set status installing target "$BVERSION" engine "$ENGINE" project "$PROJECT"
  load_images fresh
  record_images
  step "files in $DIR"
  sync_deploy install "" do
  ok "deploy/: $SYNC_SUMMARY"
  write_extras do
  step ".env"
  if [[ -f "$DEPLOY/.env" ]]; then note ".env is there: kept"; merge_env do; else make_env; fi
  write_override do ${leave[@]+"${leave[@]}"}
  step "models"
  place_models do
  fill_audio do
  if [[ $MAKE_CERT -eq 1 ]]; then
    step "certificate"
    if [[ -f "$DEPLOY/certs/tls.crt" ]]; then note "certs/tls.crt is there: kept"
    else (cd "$DEPLOY" && sh scripts/make-cert.sh --domain "$NEW_DOMAIN") || die "scripts/make-cert.sh failed"; fi
  fi
  if [[ $HOSTS -eq 1 ]]; then
    step "/etc/hosts"
    if [[ $EUID -eq 0 ]]; then (cd "$DEPLOY" && bash scripts/setup-hosts.sh) || bad "scripts/setup-hosts.sh failed"
    else note "it needs root: sudo $DEPLOY/scripts/setup-hosts.sh"; fi
  fi
  step "permissions"
  fix_permissions do
  start_stack || die "the stack did not start; fix what is named above and run install again"
  wait_healthy || die "the stack is not healthy; repair (or install again) after the cause is fixed"
  check_versions "$BVERSION" || die "the stack does not run $BVERSION as it should; see above"
  record_state
  state_set target ""
  print_access
  show_password_once
  say "Installed. Status: $DEPLOY/scripts/installer.sh status --dir $DIR"
}

# ======================================================================= upgrade
SNAP="" FROM="" BACKUP=""
# The images the stack runs now, tagged so that a rollback finds them after the new ones load.
keep_old_images() {
  local ref id slug n=0
  : > "$SNAP/IMAGES"
  while IFS= read -r ref; do
    [[ -n "$ref" ]] || continue
    id="$(image_id "$ref")"; [[ -n "$id" ]] || continue
    slug="$(printf '%s' "$FROM-$ref" | tr 'A-Z' 'a-z' | tr -c 'a-z0-9._\n-' '-' | cut -c1-120)"
    E tag "$ref" "$PROJECT-rollback:$slug" >/dev/null 2>&1 || die "tagging $ref for the rollback failed"
    printf '%s\t%s\t%s\n' "$ref" "$id" "$PROJECT-rollback:$slug" >> "$SNAP/IMAGES"
    n=$((n + 1))
  done < <(dc config --images 2>/dev/null | sort -u)
  ok "$n images kept for a rollback (tagged $PROJECT-rollback:$FROM-...)"
}
snapshot_deploy() {
  local -a ex=(--exclude=./.airgap-load --exclude='./*.installer-new')
  local rel
  for rel in "${BACKUP_DIR#"$DEPLOY"/}" "${MODELS_DIR#"$DEPLOY"/}"; do [[ "$rel" != /* ]] && ex+=("--exclude=./$rel"); done
  (umask 077; tar -cpf "$SNAP/deploy.tar" "${ex[@]}" -C "$DEPLOY" .) || die "keeping a copy of $DEPLOY for a rollback failed (disk full?)"
  # VERSION beside deploy/ says which release this is: the upgrade writes the new one there.
  rm -f "$SNAP/VERSION"; [[ -f "$DIR/VERSION" ]] && cp -p "$DIR/VERSION" "$SNAP/VERSION"
  ok "a copy of $DEPLOY for a rollback ($(human "$(stat -c %s "$SNAP/deploy.tar")"))"
}
# Back to FROM: its files, its images, and (once the new version had started) the data
# of the backup taken before the upgrade, after keeping what the failed version left.
rollback() {   # rollback STARTED -> 0 when FROM runs again
  local started=$1 ref id tag f rc=0
  step "rolling back to $FROM"
  dc_down --remove-orphans
  if [[ -f "$SNAP/added" ]]; then while IFS= read -r f; do [[ -n "$f" ]] && rm -f "$DEPLOY/$f"; done < "$SNAP/added"; fi
  tar -xpf "$SNAP/deploy.tar" -C "$DEPLOY" || { bad "putting $DEPLOY back from $SNAP/deploy.tar failed"; return 1; }
  if [[ -f "$SNAP/VERSION" ]]; then cp -p "$SNAP/VERSION" "$DIR/VERSION"; else rm -f "$DIR/VERSION"; fi
  ok "$DEPLOY as it was (with its .env)"
  while IFS=$'\t' read -r ref id tag; do
    [[ -n "$ref" ]] || continue
    E tag "$tag" "$ref" >/dev/null 2>&1 || { bad "the old image of $ref ($tag) is gone"; rc=1; }
  done < "$SNAP/IMAGES"
  # The release's own as compose names them, from the old ones.
  ok "the old images tagged back"
  if [[ $started -eq 1 ]]; then
    if [[ -n "$BACKUP" && -d "$BACKUP" ]]; then
      say "  keeping what the failed version left, then restoring the backup from before it"
      backup_sh --out "$SNAP/after-failed-upgrade" >/dev/null 2>&1 && ok "what it left: $SNAP/after-failed-upgrade" || note "could not keep what it left"
      backup_sh --restore --from "$BACKUP" --yes || { bad "restoring $BACKUP failed"; return 1; }
      ok "the data from $BACKUP"
    else
      bad "no backup to restore"; return 1
    fi
  fi
  start_stack || return 1
  wait_healthy || return 1
  local v; read_settings; v="$(app_version)"
  if [[ -n "$v" && "$v" != "$FROM" ]]; then bad "the app says $v after the rollback, not $FROM"; return 1; fi
  ok "$FROM runs again${v:+ (the app says $v)}"
  return $rc
}
# Disk for the backup: the volumes it takes (an estimate, before compression).
backup_need_kb() {
  local vol total=0 kb
  for vol in $(dc config --volumes 2>/dev/null); do
    case " audio acme sandbox " in *" $vol "*) continue ;; esac
    volume_exists "${PROJECT}_$vol" || continue
    kb="$(in_helper -v "${PROJECT}_$vol:/v:ro" "$HELPER" du -sk /v 2>/dev/null | cut -f1)"
    total=$((total + ${kb:-0}))
  done
  echo "$total"
}

cmd_upgrade() {
  local iv src cmp marker started=0 need free
  need_bundle
  [[ -f "$DEPLOY/docker-compose.yml" ]] || refuse "no installation in $DIR (no deploy/docker-compose.yml): install instead"
  [[ -f "$DEPLOY/.env" ]] || refuse "$DEPLOY has no .env: its secrets open the data; put it back first"
  read_settings
  marker="$STATE/upgrade"
  if [[ $ROLLBACK -eq 1 ]]; then cmd_rollback; return; fi
  if [[ -f "$marker" ]]; then
    iv="$(sed -n 's/^from=//p' "$marker")"
    [[ "$(sed -n 's/^to=//p' "$marker")" == "$BVERSION" ]] || refuse "an upgrade to $(sed -n 's/^to=//p' "$marker") was interrupted here: run that bundle's upgrade again (it carries on)"
    src="the interrupted upgrade's record"
  else
    iv="$(installed_version)"; src="$(version_source)"
  fi
  if [[ -z "$iv" ]]; then
    refuse "the version installed is not known: no record of this installer, no VERSION beside deploy/, and the app does not answer at https://$DOMAIN:$HTTPS_PORT/api/info. Start the stack ($ENGINE compose up -d in $DEPLOY) and run upgrade again"
  fi
  valid_version "$iv" || refuse "the version installed, \"$iv\" ($src), is not one this installer reads"
  if [[ "$(vercmp "$iv" "$UPGRADES_FROM")" == -1 ]]; then
    refuse "$iv is installed ($src); this bundle upgrades $UPGRADES_FROM or newer. Upgrade to $UPGRADES_FROM first (its own way, docs/deployment.md), then run this"
  fi
  cmp="$(vercmp "$iv" "$BVERSION")"
  if [[ $cmp == 0 && ! -f "$marker" ]]; then say "Argus Arena $iv is installed in $DIR already ($src): nothing to upgrade. (repair checks it against this bundle.)"; return 0; fi
  [[ $cmp == 1 ]] && refuse "$iv is installed ($src), newer than this bundle's $BVERSION: going back is a restore of the backup taken before that upgrade (upgrade --rollback with its bundle)"
  FROM="$iv"
  SNAP="$STATE/rollback/$FROM"
  # This release's backup.sh (5.2.0's takes neither --deploy nor --podman), for the backup and a rollback.
  BACKUP_SCRIPT="$BUNDLE/deploy/scripts/backup.sh"

  say "Upgrade Argus Arena in $DIR from $FROM ($src) to $BVERSION ($ENGINE, project $PROJECT)$([[ $DRY -eq 1 ]] && echo " -- dry run: nothing is done")"
  # The services this bundle has no image for must be left out here.
  local svc active
  active=" $(dc config --services 2>/dev/null | tr '\n' ' ') "
  for svc in $BLEFT; do
    [[ "$active" == *" $svc "* ]] && refuse "this bundle has no image for $svc, which runs here: leave it out in docker-compose.override.yml (services: { $svc: { profiles: [off] } }), or use a bundle made without --leave-out $svc"
  done
  if [[ $DRY -eq 1 ]]; then
    check_bundle
    step "the plan"
    [[ -f "$marker" ]] && would "carry on the upgrade that was interrupted (its backup: $(sed -n 's/^backup=//p' "$marker"))" \
      || would "back up first: scripts/backup.sh (the database, the volumes, .env and config) into $BACKUP_DIR"
    would "keep the images $FROM runs (tagged $PROJECT-rollback:$FROM-...) and a copy of $DEPLOY, for a rollback"
    would "load $(bundle_images | wc -l) images into $ENGINE (those there already kept)"
    step "deploy/ ($FROM -> $BVERSION; .env, overrides, certificates, backups and models kept)"
    sync_deploy upgrade "$FROM" plan
    [[ $SYNC_CHANGES -eq 0 ]] && ok "no file changes"
    step ".env"
    merge_env plan
    write_extras plan
    place_models plan
    fill_audio plan
    would "start $BVERSION (the app and Argus migrate their data as they start), wait until every service is healthy, check every service's version"
    would "on a failure: roll back by itself to $FROM (its files, its images, and the data from the backup)"
    return 0
  fi
  confirm "Upgrade from $FROM to $BVERSION? (a backup is taken first)" || refuse "not confirmed"
  lock; start_log
  check_bundle
  check_requirements upgrade
  [[ $REQ_FAIL -gt 0 ]] && refuse "$REQ_FAIL requirement(s) not met (above): nothing was changed"

  if [[ -f "$marker" ]]; then
    BACKUP="$(sed -n 's/^backup=//p' "$marker")"; started="$(sed -n 's/^started=//p' "$marker")"; started="${started:-0}"
    EXCUSED="$(sed -n 's/^excused=//p' "$marker")"
    note "carrying on the upgrade from $FROM that was interrupted: its backup $BACKUP, its rollback copy $SNAP"
    [[ -f "$SNAP/deploy.tar" && -f "$SNAP/IMAGES" ]] || die "the interrupted upgrade's rollback copy is not in $SNAP"
  else
    step "backup (scripts/backup.sh: the database, the volumes, .env and config)"
    need="$(backup_need_kb)"; mkdir -p "$BACKUP_DIR" 2>/dev/null; free="$(free_kb "$(existing_parent "$BACKUP_DIR")")"
    if [[ -n "$free" && $free -lt $((need / 2 + 1024 * 1024)) ]]; then
      refuse "$(human $((free * 1024))) free in $BACKUP_DIR, and the volumes hold $(human $((need * 1024))): make room (or BACKUP_DIR elsewhere in .env); nothing was changed"
    fi
    [[ -n "$free" && $free -lt $need ]] && note "$(human $((free * 1024))) free in $BACKUP_DIR for volumes of $(human $((need * 1024))): tight, if they do not compress"
    backup_sh || die "the backup failed: nothing was changed (its log is in $BACKUP_DIR)"
    BACKUP="$(ls -1d "$BACKUP_DIR"/20[0-9][0-9]-*_* 2>/dev/null | grep -v '\.part$' | sort | tail -n1)"
    [[ -n "$BACKUP" && "$(cat "$BACKUP/RESULT" 2>/dev/null)" == ok ]] || die "the backup did not finish ok: nothing was changed"
    ok "backup: $BACKUP"
    step "a rollback point"
    mkdir -p "$SNAP" && chmod 700 "$STATE/rollback" "$SNAP" || die "cannot write $SNAP"
    rm -f "$SNAP/added"
    keep_old_images
    snapshot_deploy
    EXCUSED="$(not_up_now)"
    [[ -n "$EXCUSED" ]] && note "not up before the upgrade: $EXCUSED(the upgrade does not wait for them)"
    (umask 077; printf 'from=%s\nto=%s\nbackup=%s\nstarted=0\nexcused=%s\n' "$FROM" "$BVERSION" "$BACKUP" "$EXCUSED" > "$marker")
    printf 'from=%s\nto=%s\nbackup=%s\nwhen=%s\n' "$FROM" "$BVERSION" "$BACKUP" "$(date '+%Y-%m-%d %H:%M:%S')" > "$SNAP/info"
  fi
  state_set status upgrading target "$BVERSION"

  # From here a failure rolls back.
  local failed=""
  upgrade_steps() {
    load_images upgrade || return 1
    step "deploy/ ($FROM -> $BVERSION; .env, overrides, certificates, backups and models kept)"
    SYNC_ADDED="$SNAP/added"; touch "$SYNC_ADDED"
    sync_deploy upgrade "$FROM" do
    ok "$SYNC_SUMMARY"
    step ".env"
    ENV_ADDED_FILE="$SNAP/env-added"; : > "$ENV_ADDED_FILE"
    merge_env do
    write_extras do
    step "models"
    place_models do
    fill_audio do
    step "permissions"
    fix_permissions do
    sed -i 's/^started=.*/started=1/' "$marker"; started=1
    start_stack || return 1
    wait_healthy || return 1
    check_versions "$BVERSION" || return 1
  }
  if ( upgrade_steps ); then :; else failed=1; fi
  started="$(sed -n 's/^started=//p' "$marker")"; started="${started:-0}"
  if [[ -n "$failed" ]]; then
    say ""
    say "The upgrade to $BVERSION failed (above). Rolling back to $FROM."
    if rollback "$started"; then
      rm -f "$marker"; state_set status installed target ""
      [[ -n "$(state_get version)" ]] || state_set version "$FROM" engine "$ENGINE" project "$PROJECT"
      say ""; say "Rolled back: $FROM runs again, with the data of $BACKUP. The log: $LOG"
      exit 4
    fi
    say ""
    say "The rollback failed too. To put $FROM back by hand, in $DEPLOY:"
    say "  $ENGINE compose down; tar -xpf $SNAP/deploy.tar -C $DEPLOY"
    say "  the old images: each line of $SNAP/IMAGES is REFERENCE ID TAG: $ENGINE tag TAG REFERENCE"
    say "  scripts/backup.sh$([[ $ENGINE == podman ]] && echo " --podman") --restore --from $BACKUP --yes; $ENGINE compose up -d"
    exit 5
  fi
  record_state
  state_set target "" previous "$FROM"
  rm -f "$marker"
  say ""
  say "Upgraded from $FROM to $BVERSION. The backup from before it: $BACKUP"
  say "  .env: $(grep -c . "$SNAP/env-added" 2>/dev/null || echo 0) key(s) added$([[ -s "$SNAP/env-added" ]] && echo " ($(tr '\n' ' ' < "$SNAP/env-added"))")"
  say "  $FROM's images stay for a rollback (upgrade --rollback): $(wc -l < "$SNAP/IMAGES") tags $PROJECT-rollback:$FROM-...;"
  say "  to free their space: $ENGINE image rm \$(cut -f3 $SNAP/IMAGES)"
  print_access
}

# upgrade --rollback: back to the version before the last upgrade, with its backup's data.
cmd_rollback() {
  local prev
  prev="$(state_get previous)"
  [[ -n "$prev" && -f "$STATE/rollback/$prev/info" ]] || refuse "no upgrade of this installer to roll back here"
  FROM="$prev"; SNAP="$STATE/rollback/$prev"; BACKUP="$(sed -n 's/^backup=//p' "$SNAP/info")"
  [[ -d "$BACKUP" ]] || refuse "the backup from before the upgrade, $BACKUP, is gone"
  say "Roll back $DIR from $(state_get version) to $FROM: the data goes back to $BACKUP ($(sed -n 's/^when=//p' "$SNAP/info")); what was written since is lost (a backup of it is kept)"
  [[ $DRY -eq 1 ]] && { would "stop it, put back $FROM's files and images, restore $BACKUP, start $FROM"; return 0; }
  confirm "Roll back to $FROM?" || refuse "not confirmed"
  lock; start_log
  # The one installed now: the rollback puts the older one back.
  BACKUP_SCRIPT="$SNAP/backup.sh"; cp "$DEPLOY/scripts/backup.sh" "$BACKUP_SCRIPT" || die "cannot copy backup.sh"
  if rollback 1; then
    state_set version "$FROM" previous "" status installed
    rm -f "$STATE/deploy.sha256" "$STATE/MANIFEST"
    say "Rolled back: $FROM runs, with the data of $BACKUP."
    return 0
  fi
  die "the rollback failed: see above" 5
}

# ======================================================================== repair
cmd_repair() {
  local iv f sum cur problems=0 svc name st health ref act=do pre
  [[ -f "$DEPLOY/docker-compose.yml" ]] || refuse "no installation in $DIR"
  read_settings
  iv="$(installed_version)"
  [[ $DRY -eq 1 ]] && act=plan
  if [[ -n "$BUNDLE" && -n "$iv" && "$iv" != "$BVERSION" ]]; then refuse "$iv is installed, and this bundle is $BVERSION: repair with $iv's bundle, or upgrade"; fi
  say "Repair Argus Arena ${iv:-(version unknown)} in $DIR ($ENGINE, project $PROJECT)$([[ $DRY -eq 1 ]] && echo " -- dry run: nothing is done")"
  [[ -z "$BUNDLE" ]] && note "no bundle: files and images are only checked; run repair from the bundle to restore them"
  if [[ $DRY -eq 0 ]]; then lock; start_log; fi
  [[ -n "$BUNDLE" ]] && check_bundle

  step "deploy/ files"
  if [[ -n "$BUNDLE" ]]; then
    local -a missing=() changed=()
    while IFS=$'\t' read -r f sum; do
      is_user_path "$f" && continue
      if [[ ! -f "$DEPLOY/$f" ]]; then missing+=("$f"); elif [[ "$(sha_of "$DEPLOY/$f")" != "$sum" ]]; then changed+=("$f"); fi
    done < <(bundle_deploy_sums)
    for f in ${missing[@]+"${missing[@]}"}; do
      problems=$((problems + 1))
      if [[ $act == plan ]]; then would "restore $f (missing)"; else mkdir -p "$DEPLOY/$(dirname "$f")" && cp -p "$BUNDLE/deploy/$f" "$DEPLOY/$f" && did "restored $f (it was missing)" || bad "restoring $f failed"; fi
    done
    if [[ ${#changed[@]} -gt 0 ]]; then
      problems=$((problems + ${#changed[@]}))
      for f in "${changed[@]}"; do note "changed here: $f"; done
      if [[ $act == plan ]]; then would "put back the ${#changed[@]} changed file(s), each changed copy kept beside as FILE.changed-DATE (asked first)"
      elif confirm "Put back the ${#changed[@]} changed file(s) above? (each changed copy is kept beside it)"; then
        for f in "${changed[@]}"; do
          cp -p "$DEPLOY/$f" "$DEPLOY/$f.changed-$(date +%Y%m%d-%H%M%S)" && cp -p "$BUNDLE/deploy/$f" "$DEPLOY/$f" && did "put back $f (the changed copy kept beside it)" || bad "putting back $f failed"
        done
      else
        note "the changed files stay as they are"
      fi
    fi
    [[ ${#missing[@]} -eq 0 && ${#changed[@]} -eq 0 ]] && ok "every file of $BVERSION is there, as it was shipped"
  elif [[ -f "$STATE/deploy.sha256" ]]; then
    if (cd "$DEPLOY" && sha256sum -c --quiet "$STATE/deploy.sha256" 2>&1 | sed 's/^/  FAIL   /'; exit "${PIPESTATUS[0]}"); then ok "every file installed is as it was written"; else problems=$((problems + 1)); fi
  fi
  [[ -f "$DEPLOY/.env" ]] || { bad ".env is missing: its secrets open the data; restore it (scripts/backup.sh --restore --with-config, or by hand)"; problems=$((problems + 1)); }
  local -A have=()
  if [[ -f "$DEPLOY/.env" ]]; then
    while IFS= read -r f; do have["$f"]=1; done < <(sed -n -E 's/^([A-Z][A-Z0-9_]*)=.+$/\1/p' "$DEPLOY/.env")
    for f in $(required_keys "$DEPLOY"); do [[ -n "${have[$f]:-}" ]] || { bad ".env has no value for $f, which the stack needs (never changed without asking: set it, or restore .env)"; problems=$((problems + 1)); }; done
  fi

  step "images"
  if [[ -n "$BUNDLE" ]]; then
    local -a absent=()
    while IFS=$'\t' read -r ref _ _ _ _; do [[ -n "$ref" && -z "$(image_id "$ref")" ]] && absent+=("$ref"); done < <(bundle_images)
    while IFS=$'\t' read -r svc ref; do
      [[ -n "$svc" ]] || continue
      [[ -n "$(image_id "$ref")" && "$(image_id "$(compose_image "$svc")")" == "$(image_id "$ref")" ]] || absent+=("$(compose_image "$svc")")
    done < <(bundle_release)
    if [[ ${#absent[@]} -gt 0 ]]; then
      problems=$((problems + ${#absent[@]}))
      for ref in "${absent[@]}"; do note "missing or not the release's: $ref"; done
      if [[ $act == plan ]]; then would "load them again from the bundle"
      else load_images repair && record_images; fi
    else
      ok "every image of the bundle is in $ENGINE"
    fi
  else
    images_present && ok "every image compose names is in $ENGINE" || problems=$((problems + 1))
  fi

  step "permissions"
  fix_permissions "$act"
  [[ $FIX_FAILED -eq 1 ]] && problems=$((problems + 1))

  step "containers"
  local -a sick=()
  local -A seen=()
  while IFS='|' read -r svc name st health _ _; do
    [[ -n "$svc" ]] || continue
    seen[$svc]=1
    [[ $st == running && $health != unhealthy ]] || sick+=("$svc")
    [[ $st == running && $health != unhealthy ]] || note "$svc ($name): $st${health:+, $health}"
  done < <(container_rows)
  for svc in $(dc config --services 2>/dev/null); do [[ -n "${seen[$svc]:-}" ]] || { note "$svc: no container"; sick+=("$svc"); }; done
  mapfile -t sick < <(printf '%s\n' ${sick[@]+"${sick[@]}"} | sed '/^$/d' | sort -u)
  if [[ ${#sick[@]} -gt 0 ]]; then
    problems=$((problems + ${#sick[@]}))
    if [[ $act == plan ]]; then would "start or recreate: ${sick[*]}"
    else
      images_present || die "images are missing (above): repair from the bundle"
      dc up -d --no-build --pull never --remove-orphans > "$STATE/up.log" 2>&1 || { tail -n 20 "$STATE/up.log" | sed 's/^/    /'; bad "$ENGINE compose up failed"; }
      dc up -d --no-build --pull never --force-recreate "${sick[@]}" >> "$STATE/up.log" 2>&1 && did "recreated: ${sick[*]}" || bad "recreating ${sick[*]} failed"
    fi
  else
    ok "every service has a running container"
  fi

  if [[ $act == plan ]]; then
    say ""; say "$problems finding(s); the repair would act on them as above."
    return 0
  fi
  if wait_healthy && check_versions "${iv:-$BVERSION}"; then
    [[ -n "$BUNDLE" && -n "$(state_get version)" ]] && record_images
    say ""; say "Repaired: $problems finding(s) dealt with; every service is up and healthy."
    return 0
  fi
  say ""; say "Not everything could be repaired: see the FAIL lines above. The log: $LOG"
  return 1
}

# ======================================================================== remove
# The stack's images: what compose names, the release's own, the rollback tags; never one a
# container of another project uses, nor one that was on this host before the install.
images_to_remove() {
  local ref id user
  {
    dc config --images 2>/dev/null
    cut -f1 "$STATE/IMAGES" 2>/dev/null
    cut -f3 "$STATE"/rollback/*/IMAGES 2>/dev/null
  } | sed '/^$/d' | sort -u | while IFS= read -r ref; do
    id="$(image_id "$ref")"; [[ -n "$id" ]] || continue
    if [[ "$(state_image_pre "$ref")" == 1 ]]; then printf 'keep\t%s\t%s\n' "$ref" "on this host before Argus Arena was installed"; continue; fi
    user="$(E ps -a --filter "ancestor=$id" --format '{{.Names}}' 2>/dev/null | while IFS= read -r n; do
      [[ "$(E inspect --format '{{index .Config.Labels "com.docker.compose.project"}}' "$n" 2>/dev/null)" == "$PROJECT" ]] || echo "$n"; done | head -n1)"
    if [[ -n "$user" ]]; then printf 'keep\t%s\t%s\n' "$ref" "the container $user (not $PROJECT's) uses it"; continue; fi
    printf 'remove\t%s\t\n' "$ref"
  done
}
project_volumes() {
  local vol
  {
    E volume ls -q --filter "label=com.docker.compose.project=$PROJECT" 2>/dev/null
    for vol in $(dc config --volumes 2>/dev/null); do volume_exists "${PROJECT}_$vol" && echo "${PROJECT}_$vol"; done
  } | sed '/^$/d' | sort -u | grep "^${PROJECT}_" || true
}
cmd_remove() {
  local ids other list vols f n own=0
  # A folder this installer made, and not a git checkout: purge may take it whole.
  [[ -f "$STATE/MANIFEST" && ! -e "$DIR/.git" ]] && own=1
  if [[ ! -f "$DEPLOY/docker-compose.yml" && ! -d "$STATE" ]]; then say "Nothing installed in $DIR: nothing to remove."; return 0; fi
  [[ -f "$DEPLOY/docker-compose.yml" ]] || { say "No deploy/ in $DIR (removed already?)"; }
  read_settings
  ids="$(E ps -aq --filter "label=com.docker.compose.project=$PROJECT" 2>/dev/null)"
  # Only this folder's stack: compose labels each container with the folder it was started from.
  if [[ -n "$ids" ]]; then
    # shellcheck disable=SC2086
    other="$(E inspect --format '{{index .Config.Labels "com.docker.compose.project.working_dir"}}' $ids 2>/dev/null | sort -u \
      | grep -v -x -F -e "$DEPLOY" -e "$(cd "$DEPLOY" 2>/dev/null && pwd -P)" | head -n1)"
    [[ -n "$other" ]] && refuse "the project $PROJECT in $ENGINE was started from $other, not $DEPLOY: not this installation's; nothing removed"
  fi
  list="$(images_to_remove)"
  vols="$(project_volumes)"
  say "Remove Argus Arena from $DIR ($ENGINE, project $PROJECT)$([[ $DRY -eq 1 ]] && echo " -- dry run: nothing is done")"
  say "  containers: $(printf '%s' "$ids" | grep -c . ) (stopped and removed, with the project's network)"
  say "  images: $(grep -c '^remove' <<<"$list") removed$(grep -q '^keep' <<<"$list" && echo "; kept:")"
  grep '^keep' <<<"$list" | while IFS=$'\t' read -r _ f why; do say "    $f: $why"; done
  if [[ $PURGE -eq 1 ]]; then
    say "  PURGE, which cannot be undone:"
    say "    volumes: $(tr '\n' ' ' <<<"$vols")"
    if [[ $own -eq 1 ]]; then say "    $DEPLOY with .env and the certificates; $DIR's docs, packs, code-arena; the backups in $BACKUP_DIR"
    else say "    $DEPLOY/.env and the certificates (the rest of $DEPLOY was not made by this installer: kept); the backups in $BACKUP_DIR"; fi
    if [[ "$MODELS_DIR" == "$DIR"/* ]]; then say "    the models in $MODELS_DIR"; else say "    (the models in $MODELS_DIR are outside $DIR: kept)"; fi
  else
    say "  kept: the volumes (the data), .env, the backups, the models; --purge removes them"
  fi
  if [[ $DRY -eq 1 ]]; then return 0; fi
  if [[ $PURGE -eq 1 ]]; then
    if [[ -n "$CONFIRM_ARG" ]]; then
      [[ "$CONFIRM_ARG" == PURGE ]] || refuse "--confirm takes the word PURGE: nothing removed"
    else
      [[ $TTY -eq 1 ]] || refuse "--purge deletes the data: unattended, it needs --confirm PURGE; nothing removed"
      local word=""
      printf 'This deletes the data of %s for good. Type PURGE to go on: ' "$PROJECT" >&2
      read -r word <&7 || true
      [[ "$word" == PURGE ]] || refuse "not PURGE: nothing removed"
    fi
    if [[ -z "$FINAL_BACKUP" && $YES -eq 0 && $TTY -eq 1 ]] && confirm "Take a last backup first (into a folder outside $DIR)?"; then
      FINAL_BACKUP="$(ask "Where" "$(dirname "$DIR")/$(basename "$DIR")-final-backup")"
    fi
  else
    confirm "Remove the stack's containers and images? (the data stays)" || refuse "not confirmed"
  fi
  if [[ -n "$FINAL_BACKUP" ]]; then
    FINAL_BACKUP="$(abspath "$FINAL_BACKUP")"
    [[ "$FINAL_BACKUP" == "$DIR" || "$FINAL_BACKUP" == "$DIR"/* || "$FINAL_BACKUP" == "$BACKUP_DIR"* ]] && refuse "the last backup must go outside $DIR and $BACKUP_DIR, which are removed"
  fi
  mkdir -p "$STATE" 2>/dev/null; lock; start_log
  if [[ -n "$FINAL_BACKUP" ]]; then
    step "a last backup into $FINAL_BACKUP"
    mkdir -p "$FINAL_BACKUP" || die "cannot make $FINAL_BACKUP"
    backup_sh --out "$FINAL_BACKUP" || die "the last backup failed: nothing removed"
    ok "kept: $(ls -1d "$FINAL_BACKUP"/20* 2>/dev/null | tail -n1)"
  fi
  step "containers"
  if [[ -f "$DEPLOY/docker-compose.yml" ]]; then dc_down --remove-orphans; fi
  ids="$(E ps -aq --filter "label=com.docker.compose.project=$PROJECT" 2>/dev/null)"
  # shellcheck disable=SC2086
  [[ -n "$ids" ]] && E rm -f $ids >/dev/null 2>&1
  for f in $(E network ls -q --filter "label=com.docker.compose.project=$PROJECT" 2>/dev/null); do E network rm "$f" >/dev/null 2>&1; done
  if E ps -aq --filter "label=com.docker.compose.project=$PROJECT" 2>/dev/null | grep -q .; then bad "some containers of $PROJECT remain: $ENGINE ps -a"; else ok "none of $PROJECT's left"; fi
  step "images"
  while IFS=$'\t' read -r what f why; do
    [[ -n "$f" ]] || continue
    if [[ $what == remove ]]; then
      [[ -n "$(image_id "$f")" ]] || continue
      # Podman may hold the name twice, under localhost/ (as the installer tagged it) and
      # docker.io/library/ (as it was loaded): the one it finds first goes, then the other.
      n=0
      while [[ -n "$(image_id "$f")" && $n -lt 3 ]] && E image rm "$f" >/dev/null 2>&1; do n=$((n + 1)); done
      if [[ -z "$(image_id "$f")" ]]; then did "removed $f"; else note "could not remove $f (in use?)"; fi
    else note "kept $f: $why"; fi
  done <<<"$list"
  state_set status removed
  if [[ $PURGE -eq 1 ]]; then
    step "the data (--purge)"
    for f in $vols; do [[ "$f" == "${PROJECT}_"* ]] && { E volume rm -f "$f" >/dev/null 2>&1 && did "volume $f" || bad "could not remove the volume $f"; }; done
    if [[ -d "$BACKUP_DIR" ]]; then
      rm -rf "$BACKUP_DIR"/20[0-9][0-9]-*_* "$BACKUP_DIR"/latest "$BACKUP_DIR"/history.log "$BACKUP_DIR"/.backup.lock && did "the backups in $BACKUP_DIR"
      rmdir "$BACKUP_DIR" 2>/dev/null
    fi
    if [[ "$MODELS_DIR" == "$DIR"/* && -d "$MODELS_DIR" ]]; then rm -rf "$MODELS_DIR" && did "the models in $MODELS_DIR"; fi
    if [[ $own -eq 1 ]]; then
      rm -rf "$DEPLOY" "$DIR/docs" "$DIR/packs" "$DIR/code-arena" "$DIR/LICENSE.md" "$DIR/LICENSING.md" "$DIR/VERSION" && did "$DEPLOY (.env, certificates), docs, packs, code-arena"
    else
      # Not a folder this installer made (a checkout, or one airgap.sh loaded): its files stay.
      rm -f "$DEPLOY/.env" "$DEPLOY/config/traefik/certificate.yml"
      find "$DEPLOY/certs" -mindepth 1 ! -name README.md -delete 2>/dev/null
      did "$DEPLOY/.env and the certificates (the other files there were not made by this installer: kept)"
    fi
    find "$STATE" -mindepth 1 -maxdepth 1 ! -name logs ! -name lock -exec rm -rf {} + 2>/dev/null
    did "the installer's record (its logs stay in $STATE/logs)"
    say ""; say "Purged: Argus Arena and its data are gone from this host$([[ -n "$FINAL_BACKUP" ]] && echo "; the last backup is in $FINAL_BACKUP")."
  else
    say ""; say "Removed the containers and images. Kept: the volumes ($(wc -w <<<"$vols")), $DEPLOY/.env, the backups and the models. install (or upgrade) brings it back; remove --purge deletes the data."
  fi
  return 0
}

# ======================================================================== status
cmd_status() {
  local iv svc name st health img ref problems=0 v root free left
  [[ -f "$DEPLOY/docker-compose.yml" ]] || { say "Nothing installed in $DIR."; [[ -n "$BUNDLE" ]] && say "The bundle: Argus Arena $BVERSION ($(bundle_get commit))."; return 1; }
  # A log beside the installation's others, when it can be written (status changes nothing).
  [[ -n "$LOG_ARG" || -d "$STATE/logs" ]] && start_log soft
  read_settings
  iv="$(installed_version)"
  say "Argus Arena in $DIR ($ENGINE, project $PROJECT)"
  say "  installed: ${iv:-unknown} ($(version_source))$([[ -n "$(state_get status)" && "$(state_get status)" != installed ]] && echo "; status: $(state_get status)")"
  [[ -f "$STATE/upgrade" ]] && say "  an upgrade to $(sed -n 's/^to=//p' "$STATE/upgrade") was interrupted: run that bundle's upgrade again"
  [[ -n "$BUNDLE" ]] && say "  this bundle: $BVERSION ($(bundle_get commit)), upgrades $UPGRADES_FROM or newer"
  say "  https://$DOMAIN$([[ "$HTTPS_PORT" != 443 ]] && echo ":$HTTPS_PORT")"
  step "services"
  local -A seen=()
  while IFS='|' read -r svc name st health img ref; do
    [[ -n "$svc" ]] || continue
    seen[$svc]=1
    v=""
    [[ $svc == app ]] && v="$(app_version)" && v="${v:+says $v}"
    [[ $svc == argus ]] && v="$(argus_version)" && v="${v:+says $v}"
    printf '  %-18s %-10s %-10s %s%s\n' "$svc" "$st" "${health:--}" "$ref" "${v:+  ($v)}"
    [[ $st == running && $health != unhealthy && $health != starting ]] || problems=$((problems + 1))
  done < <(container_rows | sort)
  for svc in $(dc config --services 2>/dev/null); do [[ -n "${seen[$svc]:-}" ]] || { printf '  %-18s %s\n' "$svc" "no container"; problems=$((problems + 1)); }; done
  left="$(comm -13 <(dc config --services 2>/dev/null | sort) <(sed -n '/^services:/,/^[^ ]/s/^  \([a-z0-9-]*\):.*/\1/p' "$DEPLOY/docker-compose.yml" | sort) | tr '\n' ' ')"
  [[ -n "${left// /}" ]] && say "  left out: $left"
  step "disk and GPU"
  say "  $DIR: $(human $(( $(free_kb "$DIR") * 1024 ))) free"
  [[ -d "$MODELS_DIR" ]] && say "  models: $(du -sh "$MODELS_DIR" 2>/dev/null | cut -f1) in $MODELS_DIR"
  root="$(engine_root)"; [[ -n "$root" ]] && free="$(free_kb "$(existing_parent "$root")")" && say "  $ENGINE's store ($root): $(human $((free * 1024))) free"
  if command -v nvidia-smi >/dev/null && nvidia-smi -L >/dev/null 2>&1; then
    nvidia-smi --query-gpu=name,memory.used,memory.total --format=csv,noheader 2>/dev/null | sed 's/^/  GPU: /'
  else
    say "  GPU: none (nvidia-smi does not answer)"
  fi
  step "backups"
  v="$(ls -1d "$BACKUP_DIR"/20[0-9][0-9]-*_* 2>/dev/null | grep -v '\.part$' | sort | tail -n1)"
  if [[ -n "$v" ]]; then say "  latest: $(basename "$v") $(cat "$v/RESULT" 2>/dev/null) ($(ls -1d "$BACKUP_DIR"/20[0-9][0-9]-*_* | wc -l) in $BACKUP_DIR)"; else say "  none in $BACKUP_DIR (scripts/backup.sh)"; fi
  [[ -n "$(state_get previous)" ]] && say "  rollback: to $(state_get previous) (upgrade --rollback), its images kept as $PROJECT-rollback:$(state_get previous)-..."
  say ""
  if [[ $problems -eq 0 ]]; then say "Every service is up."; return 0; fi
  say "$problems service(s) not up or not healthy: repair puts them right."
  return 1
}

# ======================================================================== verify
cmd_verify() {
  local failed=0
  [[ -n "$LOG_ARG" || -d "$STATE/logs" ]] && start_log soft
  if [[ -n "$BUNDLE" ]]; then
    if [[ -n "$RUNFILE" && -f "$RUNFILE.sha256" ]]; then
      step "$(basename "$RUNFILE") against $(basename "$RUNFILE").sha256"
      if (cd "$(dirname "$RUNFILE")" && sha256sum -c --quiet "$(basename "$RUNFILE").sha256"); then ok "the file is the one that was made"
      else bad "$(basename "$RUNFILE") is not the file that was made: copy it again"; failed=1; fi
    fi
    if [[ $DRY -eq 1 ]]; then would "check every file of the bundle against its SHA256SUMS"; else
      step "the bundle: $BVERSION ($(bundle_get commit)), against its SHA256SUMS"
      if verify_bundle; then ok "$(wc -l < "$BUNDLE/SHA256SUMS") files match"; else failed=1; fi
    fi
  fi
  if [[ -f "$STATE/deploy.sha256" && -d "$DEPLOY" ]]; then
    step "the files installed in $DEPLOY"
    if (cd "$DEPLOY" && sha256sum -c --quiet "$STATE/deploy.sha256" 2>&1 | sed 's/^/  FAIL   /'; exit "${PIPESTATUS[0]}"); then ok "every file is as it was installed ($(wc -l < "$STATE/deploy.sha256"))"
    else failed=1; note "repair (from the bundle) puts them back"; fi
  elif [[ -z "$BUNDLE" ]]; then
    die "nothing to verify: no bundle, and no installation of this installer in $DIR"
  fi
  [[ $failed -eq 0 ]] && return 0
  exit 6
}

case "$CMD" in
  install) cmd_install ;;
  upgrade) cmd_upgrade ;;
  repair) cmd_repair ;;
  remove) cmd_remove ;;
  status) cmd_status ;;
  verify) cmd_verify ;;
esac
