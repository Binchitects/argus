#!/usr/bin/env bash
# Back up everything a restore needs, by itself, into BACKUP_DIR.
#
#   ./scripts/backup.sh                          one complete backup now (what the timer runs)
#   ./scripts/backup.sh --list                   the backups present
#   ./scripts/backup.sh --verify [DIR]           check a backup (default: latest) against its checksums
#   sudo ./scripts/backup.sh --install-timer     run it daily through systemd
#   ./scripts/backup.sh --restore --from DIR     put volumes and the database back (stack stopped)
#   ./scripts/backup.sh --restore --from DIR --with-config   also .env, overrides and config files
#
# Settings, all in .env:
#   BACKUP_DIR            where backups go                        default ./backups
#   BACKUP_COPY_DIR       a second copy of each good backup, verified, on ANOTHER disk:
#                         then one failed disk cannot take the data and every backup   default none
#   BACKUP_KEEP           how many backups to keep                default 14
#   BACKUP_INCLUDE_LOGS   1 = also Loki logs, Prometheus metrics  default 1
#   BACKUP_TIME           when --install-timer runs it (systemd)  default 03:30
#
# One backup is one directory, BACKUP_DIR/<date>_<time>/:
#   postgres.sql.gz        pg_dumpall of the gateway database: people, API keys, budgets,
#                          spend. Taken from the running server, so it is consistent.
#   volumes/<name>.tar.gz  every other named volume. SQLite databases inside them (chats,
#                          Grafana, Authelia sessions, the Argus index and audit) are copied
#                          with SQLite's online backup, so a write in progress cannot tear them.
#   config/                .env, every compose file named in COMPOSE_FILE, and config/ with
#                          links FOLLOWED: users, OIDC key and client secrets, basic-auth.
#   MANIFEST, SHA256SUMS   what was taken, from which commit; a checksum for every file.
#
# The backup directory holds every secret of the stack: it is created 0700 and
# its files 0600. Models are not backed up (LLAMACPP_MODEL_DIR, re-downloadable).
# A copy is a backup like any other: --verify DIR and --restore --from DIR take it.
set -uo pipefail
ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$ROOT"
umask 077

env_get() { grep -E "^$1=" .env 2>/dev/null | tail -n1 | cut -d= -f2- | sed -e 's/^"//' -e 's/"$//'; }
say() { printf '%s\n' "$*"; }
die() { printf 'backup: ERROR: %s\n' "$*" >&2; exit 1; }

PROJECT="$(env_get COMPOSE_PROJECT_NAME)"; PROJECT="${PROJECT:-llmservice}"
BACKUP_DIR="$(env_get BACKUP_DIR)"; BACKUP_DIR="${BACKUP_DIR:-./backups}"
KEEP="$(env_get BACKUP_KEEP)"; KEEP="${KEEP:-14}"
INCLUDE_LOGS="$(env_get BACKUP_INCLUDE_LOGS)"; INCLUDE_LOGS="${INCLUDE_LOGS:-1}"
BACKUP_TIME="$(env_get BACKUP_TIME)"; BACKUP_TIME="${BACKUP_TIME:-03:30}"
PG_USER="$(env_get LLM_PG_USER)"; PG_USER="${PG_USER:-llmservice}"
# The image cpu-temp-exporter already runs: on an air-gapped host a backup must
# not need an image that was never pulled (or was pruned as unused).
HELPER=python:3.13-slim
case "$BACKUP_DIR" in /*) ;; *) BACKUP_DIR="$ROOT/${BACKUP_DIR#./}" ;; esac
COPY_DIR="$(env_get BACKUP_COPY_DIR)"
case "$COPY_DIR" in ""|/*) ;; *) COPY_DIR="$ROOT/${COPY_DIR#./}" ;; esac

# Named volumes that are caches or models, not state.
SKIP_VOLUMES=" hf-cache vllm-cache ollama-models llamacpp-engine postgres-data "
LOG_VOLUMES=" loki-data prometheus-data alertmanager-data clickhouse-logs "

ACTION=backup; FROM=""; WITH_CONFIG=0; VERIFY_DIR=""
while [[ $# -gt 0 ]]; do
  case "$1" in
    --list) ACTION=list; shift ;;
    --verify) ACTION=verify; shift; if [[ $# -gt 0 && "${1:0:2}" != "--" ]]; then VERIFY_DIR="$1"; shift; fi ;;
    --install-timer) ACTION=timer; shift ;;
    --restore) ACTION=restore; shift ;;
    --from) FROM="$2"; shift 2 ;;
    --with-config) WITH_CONFIG=1; shift ;;
    --out) BACKUP_DIR="$2"; shift 2 ;;   # kept for compatibility
    --include-model-cache) SKIP_VOLUMES="${SKIP_VOLUMES/ hf-cache / }"; shift ;;
    -h|--help) sed -n '2,31p' "$0" | grep '^#'; exit 0 ;;
    *) die "unknown option: $1 (see --help)" ;;
  esac
done

compose_volumes() { docker compose config --volumes 2>/dev/null; }

verify_dir() {   # verify_dir <backup dir>
  local dir=$1
  [[ -f "$dir/SHA256SUMS" ]] || { say "  $dir: no SHA256SUMS"; return 1; }
  (cd "$dir" && sha256sum -c --quiet SHA256SUMS) || { say "  $dir: CHECKSUM MISMATCH"; return 1; }
  local f
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

latest_backup() { ls -1d "$BACKUP_DIR"/20[0-9][0-9]-*_* 2>/dev/null | sort | tail -n1; }

# ============================================================================ list
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

# =========================================================================== timer
if [[ $ACTION == timer ]]; then
  [[ $EUID -eq 0 ]] || die "--install-timer needs root: sudo $0 --install-timer"
  RUN_USER="${SUDO_USER:-root}"
  case "$BACKUP_TIME" in *:*:*|*-*|*" "*) CAL="$BACKUP_TIME" ;; *) CAL="*-*-* ${BACKUP_TIME}:00" ;; esac
  cat > /etc/systemd/system/${PROJECT}-backup.service <<EOF
[Unit]
Description=${PROJECT}: complete backup of the LLM stack into ${BACKUP_DIR}
Requires=docker.service
After=docker.service
RequiresMountsFor=${BACKUP_DIR} ${COPY_DIR}

[Service]
Type=oneshot
User=${RUN_USER}
WorkingDirectory=${ROOT}
ExecStart=${ROOT}/scripts/backup.sh
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

# ========================================================================= restore
if [[ $ACTION == restore ]]; then
  [[ -n "$FROM" && -d "$FROM" ]] || die "pass --from <backup directory>"
  FROM="$(cd "$FROM" && pwd)"
  verify_dir "$FROM" || die "the backup does not verify; not restoring from it"
  if docker compose ps -q 2>/dev/null | grep -q .; then
    die "the stack is running. Stop it first: docker compose down   (volumes are kept)"
  fi
  say "Restoring from $FROM. This OVERWRITES the current volumes${WITH_CONFIG:+ and config}."
  read -r -p "Type RESTORE to continue: " answer; [[ "$answer" == "RESTORE" ]] || die "aborted"

  if [[ $WITH_CONFIG -eq 1 && -d "$FROM/config" ]]; then
    say "==> config (written through links, so files land where the live ones are)"
    cp "$FROM/config/.env" .env
    while IFS= read -r f; do
      rel="${f#"$FROM/config/stack-config/"}"; mkdir -p "config/$(dirname "$rel")"; cp -p "$f" "config/$rel"
    done < <(find "$FROM/config/stack-config" -type f)
    for f in "$FROM"/config/compose/*; do
      [[ -f "$f" ]] || continue
      dst="$(tr ':' '\n' <<<"$(env_get COMPOSE_FILE)" | grep -F "/$(basename "$f")" | head -n1)"
      [[ -n "$dst" ]] && cp -p "$f" "$dst" && say "  $dst"
    done
  fi

  say "==> volumes (created exactly as compose defines them, then filled)"
  docker compose up --no-start >/dev/null 2>&1 || die "docker compose up --no-start failed"
  for a in "$FROM"/volumes/*.tar.gz; do
    [[ -f "$a" ]] || continue
    vol="$(basename "$a" .tar.gz)"
    docker run --rm --network none -v "${PROJECT}_${vol}:/target" -v "$FROM/volumes:/backup:ro" "$HELPER" \
      sh -c "find /target -mindepth 1 -delete && tar -xzpf /backup/${vol}.tar.gz --numeric-owner -C /target" \
      && say "  restored  $vol" || die "restoring $vol failed"
  done

  if [[ -f "$FROM/postgres.sql.gz" ]]; then
    say "==> gateway database"
    docker run --rm --network none -v "${PROJECT}_postgres-data:/target" "$HELPER" sh -c "find /target -mindepth 1 -delete"
    docker compose up -d postgres >/dev/null 2>&1 || die "postgres did not start"
    for _ in $(seq 1 60); do docker exec postgres pg_isready -U "$PG_USER" >/dev/null 2>&1 && break; sleep 2; done
    sleep 3
    zcat "$FROM/postgres.sql.gz" | docker exec -i postgres psql -q -U "$PG_USER" -d postgres >/dev/null 2>"$FROM/.restore-psql.log" \
      || say "  psql reported errors; see $FROM/.restore-psql.log"
    docker compose stop postgres >/dev/null 2>&1
    say "  restored  postgres (people, keys, budgets, spend)"
  fi
  say ""; say "Done. Start the stack: docker compose up -d"
  exit 0
fi

# ========================================================================== backup
command -v docker >/dev/null || die "docker not found"
[[ -f .env ]] || die "no .env in $ROOT"
mkdir -p "$BACKUP_DIR" && chmod 700 "$BACKUP_DIR" || die "cannot create $BACKUP_DIR"
exec 9>"$BACKUP_DIR/.backup.lock"
flock -n 9 || die "another backup is running"

STAMP="$(date +%Y-%m-%d_%H%M%S)"
OUT="$BACKUP_DIR/$STAMP"
mkdir -p "$OUT/volumes" "$OUT/config/compose" || die "cannot create $OUT"
LOG="$OUT/backup.log"
exec > >(tee -a "$LOG") 2>&1
START=$(date +%s); FAILED=0
fail() { say "  FAILED: $*"; FAILED=$((FAILED+1)); }
UIDGID="$(id -u):$(id -g)"

say "backup $STAMP -> $OUT"

# ---- 1. configuration and credentials ---------------------------------------
say "==> config and credentials"
cp -L .env "$OUT/config/.env" || fail ".env"
IFS=':' read -r -a CFILES <<< "$(env_get COMPOSE_FILE)"
for f in "${CFILES[@]}"; do
  [[ -z "$f" ]] && continue
  [[ "$f" == /* ]] || f="$ROOT/$f"
  [[ -f "$f" ]] && cp -L "$f" "$OUT/config/compose/" || fail "compose file $f"
done
[[ ${#CFILES[@]} -eq 0 ]] && cp docker-compose.yml "$OUT/config/compose/"
cp -rL config "$OUT/config/stack-config" 2>/dev/null || fail "config/ (a link that points nowhere?)"
{ git -C "$ROOT" rev-parse HEAD; git -C "$ROOT" status --short; } > "$OUT/config/git-state.txt" 2>/dev/null || true
git -C "$ROOT" diff > "$OUT/config/git-local-changes.patch" 2>/dev/null || true
say "  $(find "$OUT/config" -type f | wc -l) files (.env, $(ls "$OUT/config/compose" | wc -l) compose file(s), config/ with links followed)"

# ---- 2. gateway database -------------------------------------------------------
say "==> postgres"
if docker exec postgres pg_isready -U "$PG_USER" >/dev/null 2>&1; then
  if docker exec postgres pg_dumpall -U "$PG_USER" --clean --if-exists | gzip -6 > "$OUT/postgres.sql.gz" \
     && zcat "$OUT/postgres.sql.gz" | tail -n 5 | grep -q "PostgreSQL database cluster dump complete"; then
    say "  postgres.sql.gz $(du -h "$OUT/postgres.sql.gz" | cut -f1) (pg_dumpall)"
  else
    fail "pg_dumpall"
  fi
else
  SKIP_VOLUMES="${SKIP_VOLUMES/ postgres-data / }"
  say "  postgres is not running: its volume is archived instead (the stack is presumably stopped, so the files are consistent)"
fi

# ---- 3. volumes -------------------------------------------------------------------
say "==> volumes"
SNAPSHOT_PY='
import os, sqlite3, sys, tarfile, tempfile
src, out = "/src", sys.argv[1]
companions = ("-wal", "-shm", "-journal")
def is_sqlite(p):
    try:
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
for vol in $(compose_volumes); do
  case "$SKIP_VOLUMES" in *" $vol "*) continue ;; esac
  if [[ "$INCLUDE_LOGS" != 1 ]]; then case "$LOG_VOLUMES" in *" $vol "*) continue ;; esac; fi
  docker volume inspect "${PROJECT}_${vol}" >/dev/null 2>&1 || continue
  n_sql=$(docker run --rm --network none -v "${PROJECT}_${vol}:/src" -v "$OUT/volumes:/out" "$HELPER" \
          sh -c "python -c '$SNAPSHOT_PY' /out/${vol}.tar.gz && chown ${UIDGID} /out/${vol}.tar.gz && chmod 600 /out/${vol}.tar.gz" 2>>"$LOG")
  if [[ $? -eq 0 && -s "$OUT/volumes/${vol}.tar.gz" ]]; then
    printf '  %-22s %7s%s\n' "$vol" "$(du -h "$OUT/volumes/${vol}.tar.gz" | cut -f1)" "$([[ "${n_sql:-0}" -gt 0 ]] && echo "  (${n_sql} SQLite database(s) via online backup)")"
  else
    fail "volume $vol"
  fi
done

# ---- 4. manifest, checksums, verify ----------------------------------------------
{
  echo "backup: $STAMP"
  echo "host: $(hostname)"
  echo "project: $PROJECT   domain: $(env_get LLM_DOMAIN)   model: $(env_get MODEL_NAME)"
  echo "commit: $(git -C "$ROOT" rev-parse --short HEAD 2>/dev/null)"
  echo "services running: $(docker compose ps --services 2>/dev/null | tr '\n' ' ')"
  echo "include logs: $INCLUDE_LOGS"
  echo "restore: $ROOT/scripts/backup.sh --restore --from $OUT [--with-config]"
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
