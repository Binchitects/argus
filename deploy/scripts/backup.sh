#!/usr/bin/env bash
# Back up everything a restore needs, by itself, into BACKUP_DIR.
#
#   ./scripts/backup.sh                          one complete backup now (what the timer runs)
#   ./scripts/backup.sh --list                   the backups present
#   ./scripts/backup.sh --verify [DIR]           check a backup (default: latest) against its checksums
#   ./scripts/backup.sh --prune [--keep N]       remove the backups beyond the newest N (default BACKUP_KEEP) now:
#                                                never the latest, nor the newest that ended well
#   sudo ./scripts/backup.sh --install-timer     run it daily through systemd
#   ./scripts/backup.sh --restore --from DIR     put volumes and the database back (stack stopped)
#   ./scripts/backup.sh --restore --from DIR --with-config   also .env, overrides and config files
#   ./scripts/backup.sh --restore --from DIR --yes           without asking (restore-test.sh, rollback-test.sh)
#   ./scripts/backup.sh --open DIR --to OUT      an encrypted backup's plain copy in OUT (restore-test.sh reads it)
#   ./scripts/backup.sh --pitr full|diff         a pgBackRest backup (with pitr.yml: WAL archived continuously,
#                                                a restore to any second; scripts/restore-drill.sh checks one)
#
#   --podman        Podman's containers and volumes (podman compose) in place of Docker's
#   --deploy DIR    the deploy folder to back up or restore (default: the one this script is in;
#                   installer.sh runs a newer release's copy against the installed folder)
#
# Settings, all in .env:
#   BACKUP_DIR            where backups go                        default ./backups
#                         (absolute, or relative to deploy/; the app mounts it read only)
#   BACKUP_COPY_DIR       a second copy of each good backup, verified, on ANOTHER disk:
#                         then one failed disk cannot take the data and every backup   default none
#   BACKUP_KEEP           how many backups to keep                default 14
#   BACKUP_INCLUDE_LOGS   1 = also Loki logs, Prometheus metrics  default 1
#   BACKUP_TIME           when --install-timer runs it (systemd)  default 03:30
#   BACKUP_KEY_FILE       encrypt each backup with the key in this file (AES-256, its key stretched by PBKDF2):
#                         make one with  openssl rand -base64 48 > /root/arena-backup.key && chmod 600 /root/arena-backup.key
#                         and keep a copy where the backups are not: without it, they cannot be opened   default none
#
# One backup is one directory, BACKUP_DIR/<date>_<time>/:
#   postgres.sql.gz        pg_dumpall of the gateway database: people, API keys, budgets,
#                          spend. Taken from the running server, so it is consistent.
#   volumes/<name>.tar.gz  every other named volume. SQLite databases inside them (chats,
#                          the Argus index and audit) are copied
#                          with SQLite's online backup, so a write in progress cannot tear them.
#   config/                .env, every compose file named in COMPOSE_FILE, and config/
#                          (Traefik's routes, Prometheus's rules, the other files compose mounts).
#   MANIFEST, SHA256SUMS   what was taken, from which commit, when it started (UTC); a checksum
#                          for every file. The directory's name is the host's local time.
#   With BACKUP_KEY_FILE, each of them but MANIFEST and SHA256SUMS is encrypted (NAME.enc), config/
#   as config.tar.gz.enc; MANIFEST says so, and which key (its SHA-256, never the key).
#
# The backup directory holds every secret of the stack: it is created 0700 and
# its files 0600. Models are not backed up (MODELS_DIR, re-downloadable).
# A copy is a backup like any other: --verify DIR and --restore --from DIR take it.
set -uo pipefail
ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
SELF="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)/$(basename "${BASH_SOURCE[0]}")"
ENGINE=docker
# Read before anything else: they say which folder and which engine the rest is about.
ARGS=()
while [[ $# -gt 0 ]]; do
  case "$1" in
    --podman) ENGINE=podman; shift ;;
    --deploy) [[ $# -gt 1 && -d "$2" ]] || { printf 'backup: ERROR: --deploy needs a folder\n' >&2; exit 1; }; ROOT="$(cd "$2" && pwd)"; shift 2 ;;
    *) ARGS+=("$1"); shift ;;
  esac
done
set -- ${ARGS[@]+"${ARGS[@]}"}
cd "$ROOT" || exit 1
umask 077
# Podman runs compose through its own command; its banner is noise in a backup log.
export PODMAN_COMPOSE_WARNING_LOGS=false

env_get() { grep -E "^$1=" .env 2>/dev/null | tail -n1 | cut -d= -f2- | sed -e 's/^"//' -e 's/"$//'; }
say() { printf '%s\n' "$*"; }
die() { printf 'backup: ERROR: %s\n' "$*" >&2; exit 1; }
CFG=./config

# The project compose runs: from the environment, else .env (installer.sh writes one other than arena there).
PROJECT="${COMPOSE_PROJECT_NAME:-$(env_get COMPOSE_PROJECT_NAME)}"; PROJECT="${PROJECT:-arena}"
# Podman runs the stack with podman.yml on top (docs/deployment.md, "Podman"): where COMPOSE_FILE names no
# files, the same ones as installer.sh and airgap.sh, so a restore makes the volumes and Postgres as Podman
# runs them, and the backup keeps podman.yml with the compose files.
if [[ $ENGINE == podman && -z "${COMPOSE_FILE:-}" && -z "$(env_get COMPOSE_FILE)" ]]; then
  COMPOSE_FILE="docker-compose.yml:podman.yml"
  [[ -f docker-compose.override.yml ]] && COMPOSE_FILE+=":docker-compose.override.yml"
  export COMPOSE_FILE
fi
BACKUP_DIR="$(env_get BACKUP_DIR)"; BACKUP_DIR="${BACKUP_DIR:-./backups}"
KEEP="$(env_get BACKUP_KEEP)"; KEEP="${KEEP:-14}"
INCLUDE_LOGS="$(env_get BACKUP_INCLUDE_LOGS)"; INCLUDE_LOGS="${INCLUDE_LOGS:-1}"
BACKUP_TIME="$(env_get BACKUP_TIME)"; BACKUP_TIME="${BACKUP_TIME:-03:30}"
PG_USER=arena
# The image cpu-temp-exporter already runs: on an air-gapped host a backup must
# not need an image that was never pulled (or was pruned as unused). It is never pulled.
HELPER=python:3.13-slim
HELPER_MISSING="the helper image $HELPER is not on this host, and nothing is pulled: load it from the offline bundle (or $ENGINE pull $HELPER where there is a network)"
case "$BACKUP_DIR" in /*) ;; *) BACKUP_DIR="$ROOT/${BACKUP_DIR#./}" ;; esac
COPY_DIR="$(env_get BACKUP_COPY_DIR)"
KEY_FILE="$(env_get BACKUP_KEY_FILE)"
case "$COPY_DIR" in ""|/*) ;; *) COPY_DIR="$ROOT/${COPY_DIR#./}" ;; esac

# Named volumes that are caches or models, not state; media-status is what the picture and video
# servers say they do now (rewritten every 2 seconds, in memory).
SKIP_VOLUMES=" audio acme sandbox blender lean postgres media-status vllm-status valkey "
LOG_VOLUMES=" loki prometheus alertmanager "

ACTION=backup; FROM=""; WITH_CONFIG=0; VERIFY_DIR=""; YES=0; OPEN_TO=""
while [[ $# -gt 0 ]]; do
  case "$1" in
    --list) ACTION=list; shift ;;
    --verify) ACTION=verify; shift; if [[ $# -gt 0 && "${1:0:2}" != "--" ]]; then VERIFY_DIR="$1"; shift; fi ;;
    --install-timer) ACTION=timer; shift ;;
    --restore) ACTION=restore; shift ;;
    --open) ACTION=open; [[ $# -ge 2 ]] || die "--open takes a backup folder"; FROM="$2"; shift 2 ;;
    --to) [[ $# -ge 2 ]] || die "--to takes a folder"; OPEN_TO="$2"; shift 2 ;;
    --from) FROM="$2"; shift 2 ;;
    --with-config) WITH_CONFIG=1; shift ;;
    --yes) YES=1; shift ;;
    --out) BACKUP_DIR="$2"; shift 2 ;;   # kept for compatibility
    --include-model-cache) SKIP_VOLUMES="${SKIP_VOLUMES/ audio / }"; shift ;;
    --prune) ACTION=prune; shift ;;
    --pitr) ACTION=pitr; PITR_TYPE="${2:-diff}"; [[ $# -ge 2 ]] && shift; shift ;;
    --keep) [[ $# -ge 2 ]] || die "--keep takes a number"; KEEP="$2"; shift 2 ;;
    -h|--help) awk 'NR > 1 && !/^#/ { exit } NR > 1 { print }' "$SELF"; exit 0 ;;
    *) die "unknown option: $1 (see --help)" ;;
  esac
done

compose_volumes() { "$ENGINE" compose config --volumes 2>/dev/null; }

verify_dir() {   # verify_dir <backup dir>
  local dir=$1
  [[ -f "$dir/SHA256SUMS" ]] || { say "  $dir: no SHA256SUMS"; return 1; }
  (cd "$dir" && sha256sum -c --quiet SHA256SUMS) || { say "  $dir: CHECKSUM MISMATCH"; return 1; }
  local f
  if is_encrypted "$dir"; then
    # Without its key here, the checksums are what can be checked: the backup is whole, if not readable.
    [[ -n "$KEY_FILE" && -r "$KEY_FILE" ]] || { say "  $dir: encrypted; the checksums match (without BACKUP_KEY_FILE here, nothing is opened)"; return 0; }
    for f in "$dir"/postgres.sql.gz.enc "$dir"/volumes/*.tar.gz.enc "$dir"/config.tar.gz.enc; do
      [[ -f "$f" ]] || continue
      open_stream "$f" 2>/dev/null | gzip -t 2>/dev/null || { say "  $dir: $(basename "$f") does not open with BACKUP_KEY_FILE, or is corrupt"; return 1; }
    done
    if [[ -f "$dir/postgres.sql.gz.enc" ]]; then
      open_stream "$dir/postgres.sql.gz.enc" | zcat | tail -n 5 | grep -q "PostgreSQL database cluster dump complete" \
        || { say "  $dir: postgres dump is incomplete"; return 1; }
    fi
    return 0
  fi
  for f in "$dir"/postgres.sql.gz "$dir"/volumes/*.tar.gz; do
    [[ -f "$f" ]] || continue
    gzip -t "$f" 2>/dev/null || { say "  $dir: corrupt archive $(basename "$f")"; return 1; }
  done
  if [[ -f "$dir/postgres.sql.gz" ]]; then
    zcat "$dir/postgres.sql.gz" | tail -n 5 | grep -q "PostgreSQL database cluster dump complete" \
      || { say "  $dir: postgres dump is incomplete"; return 1; }
  fi
  return 0
}

# Encrypted backups (BACKUP_KEY_FILE): each file AES-256-CBC, its key stretched from the key file by PBKDF2.
CIPHER=(-aes-256-cbc -pbkdf2 -iter 200000 -md sha256)
is_encrypted() { [[ -f "$1/MANIFEST" ]] && grep -q '^encrypted: ' "$1/MANIFEST"; }
need_key() {   # need_key <why>: the key file is there, readable, and openssl to use it
  [[ -n "$KEY_FILE" ]] || die "$1: set BACKUP_KEY_FILE in .env to its key file"
  [[ -r "$KEY_FILE" ]] || die "BACKUP_KEY_FILE ($KEY_FILE) cannot be read"
  command -v openssl >/dev/null || die "openssl is needed for encrypted backups"
}
key_id() { sha256sum "$KEY_FILE" | cut -c1-16; }
seal_file() { openssl enc -e "${CIPHER[@]}" -salt -pass "file:$KEY_FILE" -in "$1" -out "$1.enc" && rm -f "$1"; }
open_stream() { openssl enc -d "${CIPHER[@]}" -pass "file:$KEY_FILE" -in "$1"; }

# A plain copy of an encrypted backup: each file opened, config/ unpacked, its own checksums (so --verify and
# --restore take it). Private (0700), like the backup.
open_backup() {   # open_backup <encrypted dir> <out>
  local from=$1 out=$2 f rel
  need_key "$(basename "$from") is encrypted"
  mkdir -p "$out" && chmod 700 "$out" || die "cannot make $out"
  while IFS= read -r f; do
    rel="${f#"$from/"}"; rel="${rel%.enc}"
    mkdir -p "$out/$(dirname "$rel")"
    open_stream "$f" > "$out/$rel" || die "$rel does not open with BACKUP_KEY_FILE (another key?)"
  done < <(find "$from" -type f -name '*.enc')
  if [[ -f "$out/config.tar.gz" ]]; then
    tar -xzf "$out/config.tar.gz" -C "$out" && rm -f "$out/config.tar.gz" || die "config.tar.gz does not unpack"
  fi
  grep -v '^encrypted: ' "$from/MANIFEST" > "$out/MANIFEST"
  (cd "$out" && find . -type f ! -name SHA256SUMS -printf '%P\n' | sort | xargs -d '\n' sha256sum > SHA256SUMS)
}

latest_backup() { ls -1d "$BACKUP_DIR"/20[0-9][0-9]-*_* 2>/dev/null | sort | tail -n1; }

# A folder the backups go to: made private when it is ours. The app mounts BACKUP_DIR,
# and Docker makes a missing folder it mounts root's: an empty one we may not write in
# is taken back through Docker (as the backup's own containers run), with no step by
# hand. One with something in it is used if we may write in it, and named otherwise.
own_dir() {   # own_dir <dir>
  local dir=$1
  mkdir -p "$dir" || die "cannot create $dir"
  if [[ ! -O "$dir" && ! -w "$dir" && -r "$dir" && -z "$(ls -A "$dir")" ]] && command -v docker >/dev/null; then
    docker run --rm --network none -v "$dir:/d" "$HELPER" chown "$(id -u):$(id -g)" /d >/dev/null \
      && [[ -O "$dir" ]] && say "  $dir was root's (Docker made it for the app's mount): now $(id -un)'s"
  fi
  if [[ -O "$dir" ]]; then
    chmod 700 "$dir" || die "cannot make $dir private"
  elif [[ ! -w "$dir" ]]; then
    die "$dir belongs to $(stat -c %U "$dir") and $(id -un) may not write in it (Docker makes a folder it mounts root's when it is missing): sudo chown $(id -un) $dir, or sudo $0 --install-timer"
  fi
}

check_keep() { [[ "$KEEP" =~ ^[1-9][0-9]*$ ]] || die "BACKUP_KEEP and --keep take a whole number, 1 or more (not '$KEEP')"; }

# ============================================================================ list
# A pgBackRest backup of the running Postgres (pitr.yml): the stanza made the first time, then a full or
# differential backup into BACKUP_DIR/pgbackrest. WAL goes there continuously by itself.
if [[ $ACTION == pitr ]]; then
  [[ ${PITR_TYPE:-} == full || ${PITR_TYPE:-} == diff ]] || die "--pitr takes full or diff"
  "$ENGINE" compose exec -T postgres sh -c 'command -v pgbackrest' > /dev/null 2>&1 \
    || die "the postgres container has no pgBackRest: run it with pitr.yml (docker compose -f docker-compose.yml -f pitr.yml up -d postgres)"
  # The repository on the backup disk is Postgres's own (the folder is the host user's): made so, the first time.
  "$ENGINE" compose exec -T -u root postgres chown postgres:postgres /pgbackrest \
    || die "could not make /pgbackrest Postgres's in the container"
  "$ENGINE" compose exec -T -u postgres postgres pgbackrest --stanza=arena stanza-create > /dev/null 2>&1 || true
  say "pitr: a $PITR_TYPE backup"
  "$ENGINE" compose exec -T -u postgres postgres pgbackrest --stanza=arena --type="$PITR_TYPE" backup || die "pgBackRest failed (its log: /var/log/pgbackrest in the container)"
  "$ENGINE" compose exec -T -u postgres postgres pgbackrest --stanza=arena info
  exit 0
fi

if [[ $ACTION == list ]]; then
  for where in "$BACKUP_DIR" ${COPY_DIR:+"$COPY_DIR"}; do
    say "Backups in $where (keeping $KEEP):"
    for d in $(ls -1d "$where"/20[0-9][0-9]-*_* 2>/dev/null | grep -v '\.part$' | sort); do
      printf '  %s  %6s  %s\n' "$(basename "$d")" "$(du -sh "$d" 2>/dev/null | cut -f1)" "$(cat "$d/RESULT" 2>/dev/null)"
    done
  done
  exit 0
fi

# ========================================================================== verify
if [[ $ACTION == verify ]]; then
  d="${VERIFY_DIR:-$(latest_backup)}"; [[ -n "$d" && -d "$d" ]] || die "no backup to verify"
  if verify_dir "$d"; then say "  $(basename "$d"): OK ($(wc -l < "$d/SHA256SUMS") files)"; exit 0; else exit 1; fi
fi

# =========================================================================== prune
# What Admin -> Storage previews as old backups (the app sees them read only).
if [[ $ACTION == prune ]]; then
  check_keep
  [[ -d "$BACKUP_DIR" ]] || die "no backups in $BACKUP_DIR"
  exec 9>"$BACKUP_DIR/.backup.lock"
  flock -n 9 || die "a backup is running; prune after it"
  for where in "$BACKUP_DIR" ${COPY_DIR:+"$COPY_DIR"}; do
    [[ -d "$where" ]] || continue
    latest="$(readlink "$where/latest" 2>/dev/null)"; latest="${latest%/}"; latest="${latest##*/}"
    mapfile -t all < <(ls -1d "$where"/20[0-9][0-9]-*_* 2>/dev/null | grep -v '\.part$' | sort -r)
    good=""
    for d in "${all[@]}"; do
      if [[ "$(head -n1 "$d/RESULT" 2>/dev/null)" == ok ]]; then good="${d##*/}"; break; fi
    done
    removed=0
    for d in "${all[@]:KEEP}"; do
      case "${d##*/}" in "$latest"|"$good") continue ;; esac
      rm -rf "$d" && say "  removed old backup $d" && removed=$((removed+1))
    done
    say "$where: kept the newest $KEEP$([[ -n "$good" ]] && echo " and $good (the newest that ended well)"), removed $removed"
  done
  exit 0
fi

# =========================================================================== timer
if [[ $ACTION == timer ]]; then
  [[ $EUID -eq 0 ]] || die "--install-timer needs root: sudo $0 --install-timer"
  RUN_USER="${SUDO_USER:-root}"
  # The folders the timer writes, the user's: made now, or taken back from root when
  # Docker made one first (it does for a folder it mounts that is not there yet).
  if [[ $RUN_USER != root ]]; then
    RUN_GROUP="$(id -gn "$RUN_USER")"
    for dir in "$BACKUP_DIR" ${COPY_DIR:+"$COPY_DIR"}; do
      if [[ ! -e "$dir" ]]; then
        install -d -m 700 -o "$RUN_USER" -g "$RUN_GROUP" "$dir" && say "  made $dir for $RUN_USER"
      elif [[ "$(stat -c %u "$dir")" == 0 ]]; then
        chown "$RUN_USER:$RUN_GROUP" "$dir" && chmod 700 "$dir" && say "  $dir was root's (Docker made it?): now $RUN_USER's"
      fi
    done
  fi
  # Docker's daemon is a system service; rootless Podman's socket is the user's own.
  if [[ $ENGINE == podman ]]; then
    NEEDS="Environment=XDG_RUNTIME_DIR=/run/user/$(id -u "$RUN_USER")"
  else
    NEEDS=$'Requires=docker.service\nAfter=docker.service'
  fi
  case "$BACKUP_TIME" in *:*:*|*-*|*" "*) CAL="$BACKUP_TIME" ;; *) CAL="*-*-* ${BACKUP_TIME}:00" ;; esac
  cat > /etc/systemd/system/${PROJECT}-backup.service <<EOF
[Unit]
Description=${PROJECT}: complete backup of the LLM stack into ${BACKUP_DIR}
$([[ $ENGINE == docker ]] && echo "$NEEDS")
RequiresMountsFor=${BACKUP_DIR} ${COPY_DIR}

[Service]
Type=oneshot
User=${RUN_USER}
WorkingDirectory=${ROOT}
$([[ $ENGINE == podman ]] && echo "$NEEDS")
ExecStart=${ROOT}/scripts/backup.sh$([[ $ENGINE == podman ]] && echo " --podman")
Nice=10
IOSchedulingClass=idle
EOF
  cat > /etc/systemd/system/${PROJECT}-backup.timer <<EOF
[Unit]
Description=${PROJECT}: daily backup

[Timer]
OnCalendar=${CAL}
Persistent=true
RandomizedDelaySec=5min

[Install]
WantedBy=timers.target
EOF
  systemctl daemon-reload && systemctl enable --now ${PROJECT}-backup.timer >/dev/null
  say "  installed ${PROJECT}-backup.timer (${CAL}), runs as ${RUN_USER}"
  systemctl list-timers ${PROJECT}-backup.timer --no-pager | sed -n '1,2p' | sed 's/^/  /'
  say "  run one now:   sudo systemctl start ${PROJECT}-backup.service"
  say "  last result:   journalctl -u ${PROJECT}-backup.service -n 30"
  exit 0
fi

# ============================================================================ open
if [[ $ACTION == open ]]; then
  [[ -d "$FROM" ]] || die "no backup at $FROM"
  is_encrypted "$FROM" || die "$FROM is not encrypted: use it as it is"
  [[ -n "$OPEN_TO" ]] || die "--open needs --to OUT"
  [[ ! -e "$OPEN_TO" || -z "$(ls -A "$OPEN_TO" 2>/dev/null)" ]] || die "$OPEN_TO is not empty"
  verify_dir "$FROM" || die "the backup does not verify"
  open_backup "$FROM" "$OPEN_TO"
  say "  opened: $OPEN_TO ($(wc -l < "$OPEN_TO/SHA256SUMS") files); remove it when done, it holds every secret of the stack"
  exit 0
fi

# ========================================================================= restore
if [[ $ACTION == restore ]]; then
  [[ -n "$FROM" && -d "$FROM" ]] || die "pass --from <backup directory>"
  FROM="$(cd "$FROM" && pwd)"
  verify_dir "$FROM" || die "the backup does not verify; not restoring from it"
  if is_encrypted "$FROM"; then
    # Restored from a plain copy beside it, removed however the restore ends.
    PLAIN="$(mktemp -d "$(dirname "$FROM")/.restore-XXXXXX")" || die "cannot make a folder to open the backup in"
    trap 'rm -rf "$PLAIN"' EXIT
    rmdir "$PLAIN" && open_backup "$FROM" "$PLAIN"
    FROM="$PLAIN"
  fi
  "$ENGINE" image inspect "$HELPER" >/dev/null 2>&1 || die "$HELPER_MISSING"
  if "$ENGINE" compose ps -q 2>/dev/null | grep -q .; then
    die "the stack is running. Stop it first: $ENGINE compose down   (volumes are kept)"
  fi
  say "Restoring from $FROM. This OVERWRITES the current volumes${WITH_CONFIG:+ and config}."
  if [[ $YES -eq 0 ]]; then
    read -r -p "Type RESTORE to continue: " answer; [[ "$answer" == "RESTORE" ]] || die "aborted"
  fi

  if [[ $WITH_CONFIG -eq 1 && -d "$FROM/config" ]]; then
    say "==> config (written through links, so files land where the live ones are)"
    cp "$FROM/config/.env" .env
    while IFS= read -r f; do
      rel="${f#"$FROM/config/stack-config/"}"; mkdir -p "$CFG/$(dirname "$rel")"; cp -p "$f" "$CFG/$rel"
    done < <(find "$FROM/config/stack-config" -type f)
    for f in "$FROM"/config/compose/*; do
      [[ -f "$f" ]] || continue
      dst="$(tr ':' '\n' <<<"$(env_get COMPOSE_FILE)" | grep -F "/$(basename "$f")" | head -n1)"
      [[ -n "$dst" ]] && cp -p "$f" "$dst" && say "  $dst"
    done
  fi

  say "==> volumes (created exactly as compose defines them, then filled)"
  # A volume made otherwise than compose defines it now (in the engine's storage before DATA_DIR, or
  # bound elsewhere) would stop compose at its question: its data is replaced anyway, so it goes first.
  # A bound volume's folder stays; it is emptied and filled below.
  for a in "$FROM"/volumes/*.tar.gz "$FROM/postgres.sql.gz"; do
    [[ -f "$a" ]] || continue
    vol="$(basename "$a" .tar.gz)"; [[ "$a" == */postgres.sql.gz ]] && vol=postgres
    "$ENGINE" volume rm "${PROJECT}_${vol}" >/dev/null 2>&1 || :
  done
  # Bound volumes need their folders before compose makes them.
  data_dir="$(env_get DATA_DIR)"
  if [[ -n "$data_dir" ]]; then
    for vol in $(sed -n 's/.*device: "\${DATA_DIR[^}]*}\/\([a-z0-9-]*\)".*/\1/p' docker-compose.yml 2>/dev/null); do mkdir -p "$data_dir/$vol" || die "cannot make $data_dir/$vol"; done
  fi
  "$ENGINE" compose up --no-start --no-build --pull never >/dev/null 2>&1 || die "$ENGINE compose up --no-start failed"
  for a in "$FROM"/volumes/*.tar.gz; do
    [[ -f "$a" ]] || continue
    vol="$(basename "$a" .tar.gz)"
    "$ENGINE" run --rm --pull never --network none -v "${PROJECT}_${vol}:/target" -v "$FROM/volumes:/backup:ro" "$HELPER" \
      sh -c "find /target -mindepth 1 -delete && tar -xzpf /backup/${vol}.tar.gz --numeric-owner -C /target" \
      && say "  restored  $vol" || die "restoring $vol failed"
  done

  if [[ -f "$FROM/postgres.sql.gz" ]]; then
    say "==> gateway database"
    "$ENGINE" run --rm --pull never --network none -v "${PROJECT}_postgres:/target" "$HELPER" sh -c "find /target -mindepth 1 -delete"
    "$ENGINE" compose up -d --no-build --pull never postgres >/dev/null 2>&1 || die "postgres did not start"
    # Through compose: the project's own postgres, whatever the project is called.
    for _ in $(seq 1 60); do "$ENGINE" compose exec -T postgres pg_isready -U "$PG_USER" -d litellm >/dev/null 2>&1 && break; sleep 2; done
    sleep 3
    zcat "$FROM/postgres.sql.gz" | "$ENGINE" compose exec -T postgres psql -q -U "$PG_USER" -d postgres >/dev/null 2>"$FROM/.restore-psql.log" \
      || say "  psql reported errors; see $FROM/.restore-psql.log"
    "$ENGINE" compose stop postgres >/dev/null 2>&1
    say "  restored  postgres (people, keys, budgets, spend)"
  fi
  say ""; say "Done. Start the stack: $ENGINE compose up -d"
  exit 0
fi

# ========================================================================== backup
command -v "$ENGINE" >/dev/null || die "$ENGINE not found"
[[ -f .env ]] || die "no .env in $ROOT"
check_keep
own_dir "$BACKUP_DIR"
if [[ -n "$KEY_FILE" ]]; then
  need_key "BACKUP_KEY_FILE is set"
  # Inside deploy/ (config/ is copied) or BACKUP_DIR, the key would travel with the backups it opens.
  key_at="$(readlink -f "$KEY_FILE")"
  for apart in "$ROOT" "$BACKUP_DIR" ${COPY_DIR:+"$COPY_DIR"}; do
    [[ "$key_at" == "$(readlink -f "$apart")"/* ]] && die "BACKUP_KEY_FILE ($KEY_FILE) is inside $apart: it would travel with the backups it opens. Keep it apart (/root, a vault)."
  done
fi
exec 9>"$BACKUP_DIR/.backup.lock"
flock -n 9 || die "another backup is running"

# The folder is named in the host's local time; MANIFEST says when in UTC as well.
START=$(date +%s)
STAMP="$(date -d "@$START" +%Y-%m-%d_%H%M%S)"
OUT="$BACKUP_DIR/$STAMP"
mkdir -p "$OUT/volumes" "$OUT/config/compose" || die "cannot create $OUT"
LOG="$OUT/backup.log"
exec > >(tee -a "$LOG") 2>&1
FAILED=0
fail() { say "  FAILED: $*"; FAILED=$((FAILED+1)); }
UIDGID="$(id -u):$(id -g)"
# Rootless Podman: the container's root is this user here (its uid 1000 is another host uid).
[[ $ENGINE == podman ]] && UIDGID="0:0"

say "backup $STAMP -> $OUT"

# ---- 1. configuration and credentials ---------------------------------------
say "==> config and credentials"
cp -L .env "$OUT/config/.env" || fail ".env"
IFS=':' read -r -a CFILES <<< "${COMPOSE_FILE:-$(env_get COMPOSE_FILE)}"
for f in "${CFILES[@]}"; do
  [[ -z "$f" ]] && continue
  [[ "$f" == /* ]] || f="$ROOT/$f"
  [[ -f "$f" ]] && cp -L "$f" "$OUT/config/compose/" || fail "compose file $f"
done
[[ ${#CFILES[@]} -eq 0 ]] && cp docker-compose.yml "$OUT/config/compose/"
cp -rL "$CFG" "$OUT/config/stack-config" 2>/dev/null || fail "config/ (a link that points nowhere?)"
{ git -C "$ROOT" rev-parse HEAD; git -C "$ROOT" status --short; } > "$OUT/config/git-state.txt" 2>/dev/null || true
git -C "$ROOT" diff > "$OUT/config/git-local-changes.patch" 2>/dev/null || true
say "  $(find "$OUT/config" -type f | wc -l) files (.env, $(ls "$OUT/config/compose" | wc -l) compose file(s), config/ with links followed)"

# ---- 2. gateway database -------------------------------------------------------
say "==> postgres"
if "$ENGINE" exec postgres pg_isready -U "$PG_USER" -d litellm >/dev/null 2>&1; then
  if "$ENGINE" exec postgres pg_dumpall -U "$PG_USER" --clean --if-exists | gzip -6 > "$OUT/postgres.sql.gz" \
     && zcat "$OUT/postgres.sql.gz" | tail -n 5 | grep -q "PostgreSQL database cluster dump complete"; then
    say "  postgres.sql.gz $(du -h "$OUT/postgres.sql.gz" | cut -f1) (pg_dumpall)"
  else
    fail "pg_dumpall"
  fi
else
  SKIP_VOLUMES="${SKIP_VOLUMES/ postgres / }"
  say "  postgres is not running: its volume is archived instead (the stack is presumably stopped, so the files are consistent)"
fi

# ---- 3. volumes -------------------------------------------------------------------
say "==> volumes"
SNAPSHOT_PY='
import os, sqlite3, stat, sys, tarfile, tempfile
src, out = "/src", sys.argv[1]
companions = ("-wal", "-shm", "-journal")
def is_sqlite(p):
    # Only a regular file is opened: a pipe (the log of a server) would wait for ever for a writer.
    try:
        if not stat.S_ISREG(os.lstat(p).st_mode): return False
        with open(p, "rb") as fh: return fh.read(16) == b"SQLite format 3\x00"
    except OSError: return False
n_sql = 0
with tarfile.open(out, "w:gz", compresslevel=6) as tar:
    for dirpath, dirnames, filenames in os.walk(src):
        dirnames.sort()
        rel = os.path.relpath(dirpath, src)
        if rel != ".":
            tar.add(dirpath, arcname=rel, recursive=False)
        for name in sorted(filenames):
            path = os.path.join(dirpath, name)
            arc = os.path.normpath(os.path.join(rel, name))
            if name.endswith(companions) and is_sqlite(path[: -len(next(c for c in companions if name.endswith(c)))]):
                continue  # folded into the snapshot of its database
            if not os.path.islink(path) and is_sqlite(path):
                st = os.stat(path)
                with tempfile.NamedTemporaryFile(dir="/tmp", delete=False) as tmp:
                    tmp_path = tmp.name
                s = sqlite3.connect(f"file:{path}?mode=ro", uri=True, timeout=30)
                d = sqlite3.connect(tmp_path)
                s.backup(d); d.close(); s.close()
                info = tar.gettarinfo(path, arcname=arc)
                info.size = os.path.getsize(tmp_path)
                with open(tmp_path, "rb") as fh: tar.addfile(info, fh)
                os.unlink(tmp_path); n_sql += 1
            else:
                tar.add(path, arcname=arc, recursive=False)
print(n_sql)
'
HAVE_HELPER=1
"$ENGINE" image inspect "$HELPER" >/dev/null 2>&1 || { HAVE_HELPER=0; fail "$HELPER_MISSING"; }
for vol in $(compose_volumes); do
  [[ $HAVE_HELPER -eq 1 ]] || break
  case "$SKIP_VOLUMES" in *" $vol "*) continue ;; esac
  if [[ "$INCLUDE_LOGS" != 1 ]]; then case "$LOG_VOLUMES" in *" $vol "*) continue ;; esac; fi
  "$ENGINE" volume inspect "${PROJECT}_${vol}" >/dev/null 2>&1 || continue
  n_sql=$("$ENGINE" run --rm --pull never --network none -v "${PROJECT}_${vol}:/src" -v "$OUT/volumes:/out" "$HELPER" \
          sh -c "python -c '$SNAPSHOT_PY' /out/${vol}.tar.gz && chown ${UIDGID} /out/${vol}.tar.gz && chmod 600 /out/${vol}.tar.gz" 2>>"$LOG")
  if [[ $? -eq 0 && -s "$OUT/volumes/${vol}.tar.gz" ]]; then
    printf '  %-22s %7s%s\n' "$vol" "$(du -h "$OUT/volumes/${vol}.tar.gz" | cut -f1)" "$([[ "${n_sql:-0}" -gt 0 ]] && echo "  (${n_sql} SQLite database(s) via online backup)")"
  else
    fail "volume $vol"
  fi
done

# ---- 3b. encrypted, with BACKUP_KEY_FILE ---------------------------------------------
ENCRYPTED=""
if [[ -n "$KEY_FILE" ]]; then
  say "==> encrypting each file (AES-256, key $(key_id))"
  if tar -czf "$OUT/config.tar.gz" -C "$OUT" config && rm -rf "$OUT/config"; then
    while IFS= read -r f; do seal_file "$f" || fail "encrypting ${f#"$OUT/"}"; done < <(find "$OUT" -type f -name '*.gz')
    ENCRYPTED="aes-256-cbc, pbkdf2 sha256 200000 rounds; key BACKUP_KEY_FILE, sha256 $(key_id)"
  else
    fail "config/ into config.tar.gz"
  fi
fi

# ---- 4. manifest, checksums, verify ----------------------------------------------
{
  echo "backup: $STAMP"
  echo "started: $(date -u -d "@$START" +%Y-%m-%dT%H:%M:%SZ)"
  echo "host: $(hostname)"
  echo "project: $PROJECT   domain: $(env_get DOMAIN)"
  echo "commit: $(git -C "$ROOT" rev-parse --short HEAD 2>/dev/null)"
  echo "services running: $("$ENGINE" compose ps --services 2>/dev/null | tr '\n' ' ')"
  echo "include logs: $INCLUDE_LOGS"
  echo "engine: $ENGINE"
  echo "restore: $ROOT/scripts/backup.sh$([[ $ENGINE == podman ]] && echo " --podman") --restore --from $OUT [--with-config]"
  [[ -n "$ENCRYPTED" ]] && echo "encrypted: $ENCRYPTED"
} > "$OUT/MANIFEST"
(cd "$OUT" && find . -type f ! -name SHA256SUMS ! -name backup.log ! -name RESULT ! -name .restore-psql.log -printf '%P\n' | sort | xargs -d '\n' sha256sum > SHA256SUMS)
if verify_dir "$OUT"; then say "==> verified: $(wc -l < "$OUT/SHA256SUMS") files match their checksums"; else fail "verification"; fi
chmod -R go-rwx "$OUT"

RESULT="ok"; [[ $FAILED -gt 0 ]] && RESULT="FAILED ($FAILED problem(s))"
echo "$RESULT" > "$OUT/RESULT"

# ---- 5. second copy on another disk, verified there ---------------------------------
# Copied under a .part name and renamed only once its checksums match, so a
# half-written copy is never taken for a backup. A failed copy fails the run
# (the timer shows it) but does not taint the backup itself.
COPY="none"
if [[ -n "$COPY_DIR" && $FAILED -eq 0 ]]; then
  say "==> second copy -> $COPY_DIR"
  if mkdir -p "$COPY_DIR" && chmod 700 "$COPY_DIR" && rm -rf "$COPY_DIR/$STAMP.part" \
     && cp -a "$OUT" "$COPY_DIR/$STAMP.part" && verify_dir "$COPY_DIR/$STAMP.part" \
     && mv "$COPY_DIR/$STAMP.part" "$COPY_DIR/$STAMP"; then
    COPY="ok"; say "  verified: $COPY_DIR/$STAMP"
  else
    COPY="FAILED"; say "  FAILED: second copy to $COPY_DIR"
  fi
fi
printf '%s  %-8s %6s  %ss  %s%s\n' "$STAMP" "$RESULT" "$(du -sh "$OUT" | cut -f1)" "$(( $(date +%s) - START ))" "$OUT" \
  "$([[ $COPY != none ]] && echo "  copy $COPY")" >> "$BACKUP_DIR/history.log"

# ---- 6. retention: only after a good backup ----------------------------------------
prune() {   # prune <dir>: point latest at this backup and keep the newest KEEP
  local dir=$1 old
  local -a all
  ln -sfn "$STAMP" "$dir/latest"
  mapfile -t all < <(ls -1d "$dir"/20[0-9][0-9]-*_* 2>/dev/null | grep -v '\.part$' | sort)
  if (( ${#all[@]} > KEEP )); then
    for old in "${all[@]:0:${#all[@]}-KEEP}"; do rm -rf "$old" && say "  removed old backup $old"; done
  fi
}
if [[ $FAILED -eq 0 ]]; then
  prune "$BACKUP_DIR"
  [[ $COPY == ok ]] && prune "$COPY_DIR"
fi
say "backup $RESULT: $OUT ($(du -sh "$OUT" | cut -f1), $(( $(date +%s) - START ))s)"
[[ $COPY != none ]] && say "second copy $COPY: $COPY_DIR/$STAMP"
[[ $FAILED -eq 0 && $COPY != FAILED ]]
