#!/usr/bin/env bash
# A backup, restored into a throwaway project and checked through the API: the
# people, the chats and the settings are there. Then the project and its volumes go.
#
#   ./scripts/restore-test.sh                   the newest backup in BACKUP_DIR
#   ./scripts/restore-test.sh --from DIR        that backup
#
#   --project NAME    the throwaway project (arena-restore-test); it must say test
#   --port N          its HTTPS port, on 127.0.0.1 only (18443)
#   --images-from P   run project P's app and web images (arena: the version deployed)
#   --user NAME       who signs in (admin), with RESTORE_TEST_PASSWORD, else the
#                     ADMIN_PASSWORD in the backup's .env (never printed)
#   --keep            leave it running to look at; --down removes it later
#   --down            only remove the throwaway project and its volumes
#   --dry-run         the plan; nothing is started
#
# It runs beside the live stack and never touches it or its volumes: the
# project's name may not be the live one's, and every volume it removes must
# carry its name (test-project.sh). Only postgres, app, web, traefik and litellm
# run, from images already here, with no fixed container names, on a network with
# no way out. The restore is the real one, scripts/backup.sh --restore, aimed at
# the throwaway project's volumes; never --with-config, which writes this
# folder's .env. The backup's own .env gives the secrets its data needs.
#
# Checked: the backup verifies; it restores; the app comes up on it; the person
# signs in; every person in the backup is there; their chats are there and the
# newest opens with its messages; every setting saved in the backup reads back
# saved, with its value; spend and credit read from the restored gateway.
set -uo pipefail
ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
TP_ROOT="$ROOT" TP_NAME=restore-test
# shellcheck source=test-project.sh
. "$ROOT/scripts/test-project.sh"
say() { tp_say "$@"; }
die() { tp_die "$@"; }
usage_error() { printf 'restore-test: %s (see --help)\n' "$*" >&2; exit 2; }

PROJECT=arena-restore-test PORT=18443 FROM="" IMAGES_FROM=arena USER_NAME=admin KEEP=0 DOWN=0 DRY=0
while [[ $# -gt 0 ]]; do
  case "$1" in
    --from) [[ $# -gt 1 ]] || usage_error "--from needs a backup directory"; FROM="$2"; shift ;;
    --project) [[ $# -gt 1 ]] || usage_error "--project needs a name"; PROJECT="$2"; shift ;;
    --port) [[ $# -gt 1 ]] || usage_error "--port needs a number"; PORT="$2"; shift ;;
    --images-from) [[ $# -gt 1 ]] || usage_error "--images-from needs a project"; IMAGES_FROM="$2"; shift ;;
    --user) [[ $# -gt 1 ]] || usage_error "--user needs a name"; USER_NAME="$2"; shift ;;
    --keep) KEEP=1 ;;
    --down) DOWN=1 ;;
    --dry-run) DRY=1 ;;
    -h|--help) awk 'NR > 1 && !/^#/ { exit } NR > 1 { sub(/^# ?/, ""); print }' "$0"; exit 0 ;;
    *) usage_error "unknown option: $1" ;;
  esac
  shift
done

tp_guard_project "$PROJECT"
if [[ $DOWN -eq 1 ]]; then
  TP_PROJECT="$PROJECT"
  say "Removing $PROJECT: its containers, network and volumes"
  [[ $DRY -eq 1 ]] && exit 0
  tp_down; say "  done"; exit 0
fi
tp_guard_port "$PORT"

BACKUP_DIR="$(tp_env_get BACKUP_DIR "$ROOT/.env")"; BACKUP_DIR="${BACKUP_DIR:-./backups}"
case "$BACKUP_DIR" in /*) ;; *) BACKUP_DIR="$ROOT/${BACKUP_DIR#./}" ;; esac
if [[ -z "$FROM" ]]; then
  FROM="$(ls -1d "$BACKUP_DIR"/20[0-9][0-9]-*_* 2>/dev/null | grep -v '\.part$' | sort | tail -n1)"
  [[ -n "$FROM" ]] || die "no backup in $BACKUP_DIR (pass --from DIR)"
fi
[[ -d "$FROM" ]] || die "no backup at $FROM"
FROM="$(cd "$FROM" && pwd)"
[[ -f "$FROM/config/.env" ]] || die "$FROM has no config/.env: its secrets are needed to open its data"
[[ -f "$FROM/postgres.sql.gz" ]] || die "$FROM has no postgres.sql.gz: nothing to check people and chats against"
APP_IMAGE="$IMAGES_FROM-app" WEB_IMAGE="$IMAGES_FROM-web"
IMAGES="$APP_IMAGE $WEB_IMAGE $(sed -n 's/^[[:space:]]*image:[[:space:]]*//p' "$ROOT/docker-compose.yml" | grep -E '^(traefik|pgvector|ghcr.io/berriai/litellm)' | tr '\n' ' ')python:3.13-slim"

WORK="$(mktemp -d "${TMPDIR:-/tmp}/$PROJECT.XXXXXX")" || die "cannot make a work folder"
tp_init "$PROJECT" "$PORT" "$WORK"
STARTED=0
cleanup() {
  if [[ $STARTED -eq 1 && $KEEP -eq 1 ]]; then
    say "Kept: $PROJECT at https://$DOMAIN:$PORT (127.0.0.1). Remove it: $0 --project $PROJECT --down"
  elif [[ $STARTED -eq 1 ]]; then
    say "Removing $PROJECT and its volumes"; tp_down
  fi
  rm -rf "$WORK"
}
trap cleanup EXIT
# The backup's .env: the same secrets its data was written with. The rest is the throwaway's own.
cp "$FROM/config/.env" "$TP_ENV" && chmod 600 "$TP_ENV"
tp_env_set HTTPS_PORT "$PORT"
tp_env_set MODELS_DIR "$TP_MODELS"
tp_env_set MODEL ""
tp_env_set ACME_EMAIL ""
tp_env_set COMPOSE_PROJECT_NAME
tp_env_set COMPOSE_FILE
tp_env_set COMPOSE_PROFILES
DOMAIN="$(tp_env_get DOMAIN "$TP_ENV")"; DOMAIN="${DOMAIN:-llm.localhost}"
tp_use "$ROOT/docker-compose.yml" "$APP_IMAGE" "$WEB_IMAGE"
CHECK=(python3 "$ROOT/scripts/recovery-check.py")
URL="https://$DOMAIN:$PORT"

if [[ $DRY -eq 1 ]]; then
  say "Would restore $FROM into $PROJECT (dry run: nothing is started)"
  say "  live project left alone: $(tp_live_projects | sort -u | sed '/^$/d' | tr '\n' ' ')"
  say "  1. check the backup: scripts/backup.sh --verify $FROM"
  say "  2. restore it: scripts/backup.sh --restore --from $FROM --yes, aimed at $PROJECT's volumes"
  say "  3. start $TP_CORE from these images (none pulled or built): $IMAGES"
  say "  4. sign in as $USER_NAME at $URL (127.0.0.1) and compare with the backup: people, chats, settings, spend"
  say "  5. $([[ $KEEP -eq 1 ]] && echo "keep it running (--keep)" || echo "remove $PROJECT, its network and its volumes")"
  say "  the project's compose override:"
  sed 's/^/    /' "$TP_ISOLATE"
  exit 0
fi

command -v docker >/dev/null || die "docker not found"
for image in $IMAGES; do
  docker image inspect "$image" >/dev/null 2>&1 || die "the image $image is not on this host (this test pulls and builds nothing)"
done
docker ps -aq --filter "label=com.docker.compose.project=$PROJECT" | grep -q . \
  && die "$PROJECT exists already: $0 --project $PROJECT --down"

say "restore-test: $(basename "$FROM") into $PROJECT"
say "==> the backup verifies"
"$ROOT/scripts/backup.sh" --verify "$FROM" || die "the backup does not verify"
"${CHECK[@]}" facts "$FROM/postgres.sql.gz" > "$WORK/facts.json" || die "cannot read the backup's database dump"
say "  it holds $(python3 -c 'import json, sys; f = json.load(open(sys.argv[1])); print("%d people, %d chats, %d saved settings, schema %s" % (len(f["people"]), sum(len(c) for c in f["chats"].values()), len(f["settings"]), f["migration"]))' "$WORK/facts.json")"

say "==> restored with scripts/backup.sh --restore, into $PROJECT"
STARTED=1
tp_run "$ROOT/scripts/backup.sh" --restore --from "$FROM" --yes || die "the restore failed"

say "==> $TP_CORE up"
# shellcheck disable=SC2086
tp_compose up -d --no-build --pull never $TP_CORE >/dev/null 2>"$WORK/up.log" || { cat "$WORK/up.log" >&2; die "it did not start"; }

say "==> checked through the API"
FAILED=0
"${CHECK[@]}" wait --url "$URL" --connect 127.0.0.1 --minutes 10 || FAILED=1
PASSWORD="${RESTORE_TEST_PASSWORD:-$(tp_env_get ADMIN_PASSWORD "$TP_ENV")}"
if [[ $FAILED -eq 0 ]]; then
  RECOVERY_PASSWORD="$PASSWORD" "${CHECK[@]}" restored --url "$URL" --connect 127.0.0.1 --facts "$WORK/facts.json" --user "$USER_NAME" || FAILED=1
fi
if [[ $FAILED -ne 0 ]]; then
  say "  the app's last lines:"
  tp_compose logs --tail 30 app 2>&1 | sed 's/^/    /'
fi
say ""
if [[ $FAILED -eq 0 ]]; then say "restore-test: PASS ($(basename "$FROM") restores, and the app shows what it holds)"; else say "restore-test: FAIL"; fi
exit "$FAILED"
