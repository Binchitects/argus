#!/usr/bin/env bash
# A restore drill for point-in-time recovery (pitr.yml): restores the pgBackRest repository in BACKUP_DIR
# into a scratch container, to a moment (default: ten minutes ago), starts it, and checks the databases
# are there and hold people. It never touches the stack's own Postgres, its volume or its port.
#
#   ./scripts/restore-drill.sh                       to ten minutes ago
#   ./scripts/restore-drill.sh --to "2026-10-16 09:30:00+00"
#   ./scripts/restore-drill.sh --dry-run             say what it would do
#   --podman                                         Podman in place of Docker
set -uo pipefail
ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$ROOT" || exit 1
ENGINE=docker DRY=0 TARGET=""
while [[ $# -gt 0 ]]; do
  case "$1" in
    --podman) ENGINE=podman; shift ;;
    --to) TARGET="$2"; shift 2 ;;
    --dry-run) DRY=1; shift ;;
    -h|--help) awk 'NR > 1 && !/^#/ { exit } NR > 1 { print }' "$0"; exit 0 ;;
    *) printf 'restore-drill: ERROR: unknown option %s\n' "$1" >&2; exit 1 ;;
  esac
done
env_get() { grep -E "^$1=" .env 2>/dev/null | tail -n1 | cut -d= -f2- | sed -e 's/^"//' -e 's/"$//'; }
BACKUP_DIR="$(env_get BACKUP_DIR)"; BACKUP_DIR="${BACKUP_DIR:-./backups}"
case "$BACKUP_DIR" in /*) ;; *) BACKUP_DIR="$ROOT/${BACKUP_DIR#./}" ;; esac
TARGET="${TARGET:-$(date -u -d '10 minutes ago' '+%Y-%m-%d %H:%M:%S+00')}"
NAME="arena-restore-drill-$$"
IMAGE=arena-postgres:pg16
run() { if [[ $DRY == 1 ]]; then printf 'would run: %s\n' "$*"; else "$@"; fi; }

[[ -d "$BACKUP_DIR/pgbackrest" ]] || { printf 'restore-drill: ERROR: no pgBackRest repository in %s (run backup.sh --pitr full first)\n' "$BACKUP_DIR" >&2; exit 1; }
printf 'restore-drill: restoring to %s into %s (scratch; the stack is not touched)\n' "$TARGET" "$NAME"
# A scratch volume of its own, never the stack's; read-only repository; no published port.
run "$ENGINE" volume create "$NAME"
trap 'run "$ENGINE" rm -f "$NAME" > /dev/null 2>&1; run "$ENGINE" volume rm "$NAME" > /dev/null 2>&1' EXIT
run "$ENGINE" run --rm -u postgres -v "$NAME:/var/lib/postgresql/data" -v "$BACKUP_DIR:/backup:ro" \
  -v "$ROOT/config/pgbackrest/pgbackrest.conf:/etc/pgbackrest/pgbackrest.conf:ro" --entrypoint pgbackrest "$IMAGE" \
  --stanza=arena --type=time "--target=$TARGET" --target-action=promote --archive-mode=off restore || exit 1
run "$ENGINE" run -d --name "$NAME" -u postgres -v "$NAME:/var/lib/postgresql/data" -v "$BACKUP_DIR:/backup:ro" \
  -v "$ROOT/config/pgbackrest/pgbackrest.conf:/etc/pgbackrest/pgbackrest.conf:ro" "$IMAGE" postgres -c archive_mode=off || exit 1
if [[ $DRY == 1 ]]; then exit 0; fi
for _ in $(seq 1 60); do "$ENGINE" exec "$NAME" pg_isready -U arena -d litellm > /dev/null 2>&1 && break; sleep 2; done
ok=1
for db in litellm llmapp; do
  "$ENGINE" exec "$NAME" psql -U arena -d "$db" -Atc 'select 1' > /dev/null 2>&1 || { printf 'restore-drill: database %s missing\n' "$db"; ok=0; }
done
people=$("$ENGINE" exec "$NAME" psql -U arena -d llmapp -Atc 'select count(*) from "AspNetUsers"' 2>/dev/null || echo 0)
printf 'restore-drill: %s people in the restored app database\n' "$people"
[[ ${people:-0} -gt 0 ]] || ok=0
[[ $ok == 1 ]] && printf 'restore-drill: OK\n' || { printf 'restore-drill: FAILED\n' >&2; exit 1; }
