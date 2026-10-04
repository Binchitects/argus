#!/usr/bin/env bash
# Rolling a release back, proven in a throwaway project. EF migrations only go
# forward: a rollback is the backup taken before the upgrade, restored, under the
# older release's images.
#
#   ./scripts/rollback-test.sh FROM_TAG TO_TAG    e.g. v4.1.0 v4.0.0: from the newer back to the older
#
#   --project NAME   the throwaway project (arena-rollback-test); it must say test
#   --port N         its HTTPS port, on 127.0.0.1 only (18444)
#   --keep           leave it running, with its images; --down removes them later
#   --down           only remove the throwaway project, its volumes and its images
#   --dry-run        the plan; nothing is built or started
#
# Each tag is checked out (a git worktree) and its app and web images are built
# from it, as arena-rollback-test-app:<tag> and arena-rollback-test-web:<tag>
# (a build fetches what any build fetches). The project starts from zero with
# secrets of its own, beside the live stack and never touching it or its volumes
# (test-project.sh: its own name, volumes, network and port; only postgres, app,
# web, traefik and litellm, on a network with no way out). Then:
#   1. TO_TAG from zero migrates an empty database; a person, a group, a chat and
#      a setting go in; the backup taken before the upgrade (the database with
#      pg_dumpall and the app's volumes, as backup.sh takes them).
#   2. FROM_TAG over it migrates the database forward and comes up at its
#      version with all of that; another person, chat and setting go in.
#   3. TO_TAG's images straight over the database FROM_TAG migrated: the older
#      app must come up on it and still show what went in at 1.
#   4. The procedure: down, scripts/backup.sh --restore of the backup from 1,
#      TO_TAG up. It comes up at its version on its own last migration, with
#      everything from 1 and nothing from 2.
set -uo pipefail
ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
REPO="$(cd "$ROOT/.." && pwd)"
TP_ROOT="$ROOT" TP_NAME=rollback-test
# shellcheck source=test-project.sh
. "$ROOT/scripts/test-project.sh"
say() { tp_say "$@"; }
die() { tp_die "$@"; }
usage_error() { printf 'rollback-test: %s (see --help)\n' "$*" >&2; exit 2; }

PROJECT=arena-rollback-test PORT=18444 KEEP=0 DOWN=0 DRY=0
TAGS=()
while [[ $# -gt 0 ]]; do
  case "$1" in
    --project) [[ $# -gt 1 ]] || usage_error "--project needs a name"; PROJECT="$2"; shift ;;
    --port) [[ $# -gt 1 ]] || usage_error "--port needs a number"; PORT="$2"; shift ;;
    --keep) KEEP=1 ;;
    --down) DOWN=1 ;;
    --dry-run) DRY=1 ;;
    -h|--help) awk 'NR > 1 && !/^#/ { exit } NR > 1 { sub(/^# ?/, ""); print }' "$0"; exit 0 ;;
    -*) usage_error "unknown option: $1" ;;
    *) TAGS+=("$1") ;;
  esac
  shift
done

tp_guard_project "$PROJECT"
# A tag as an image tag: letters, digits, . _ and - only.
slug() { printf '%s' "$1" | tr -c 'A-Za-z0-9._-' '-' | tr 'A-Z' 'a-z'; }
built_images() { docker images --format '{{.Repository}}:{{.Tag}}' 2>/dev/null | grep -E "^$PROJECT-(app|web):" ; }

if [[ $DOWN -eq 1 ]]; then
  TP_PROJECT="$PROJECT"
  say "Removing $PROJECT: its containers, network, volumes, images and checkouts"
  [[ $DRY -eq 1 ]] && exit 0
  tp_down
  for ref in $(built_images); do tp_image_rm "$ref"; done
  git -C "$REPO" worktree list --porcelain | sed -n "s|^worktree \(.*/$PROJECT\.[^/]*/src-[^/]*\)$|\1|p" \
    | while IFS= read -r wt; do git -C "$REPO" worktree remove --force "$wt"; done
  say "  done"; exit 0
fi

[[ ${#TAGS[@]} -eq 2 ]] || usage_error "name two releases: FROM_TAG (the newer) and TO_TAG (the one to roll back to)"
FROM_TAG="${TAGS[0]}" TO_TAG="${TAGS[1]}"
[[ "$FROM_TAG" != "$TO_TAG" ]] || usage_error "FROM_TAG and TO_TAG are the same"
for tag in "$FROM_TAG" "$TO_TAG"; do
  git -C "$REPO" rev-parse --verify --quiet "$tag^{commit}" >/dev/null || die "no release $tag in this repository (git tag)"
done
FROM_VERSION="$(git -C "$REPO" show "$FROM_TAG:VERSION" 2>/dev/null | tr -d '[:space:]')"
TO_VERSION="$(git -C "$REPO" show "$TO_TAG:VERSION" 2>/dev/null | tr -d '[:space:]')"
[[ -n "$FROM_VERSION" && -n "$TO_VERSION" ]] || die "a release without a VERSION file: $FROM_TAG ($FROM_VERSION), $TO_TAG ($TO_VERSION)"
tp_guard_port "$PORT"

app_image() { printf '%s-app:%s' "$PROJECT" "$(slug "$1")"; }
web_image() { printf '%s-web:%s' "$PROJECT" "$(slug "$1")"; }

WORK="$(mktemp -d "${TMPDIR:-/tmp}/$PROJECT.XXXXXX")" || die "cannot make a work folder"
tp_init "$PROJECT" "$PORT" "$WORK"
STARTED=0
cleanup() {
  if [[ $STARTED -eq 1 && $KEEP -eq 1 ]]; then
    say "Kept: $PROJECT at https://$DOMAIN:$PORT (127.0.0.1), its images and $WORK. Remove them: $0 --project $PROJECT --down"
    return
  fi
  if [[ $STARTED -eq 1 ]]; then
    say "Removing $PROJECT, its volumes, its images and the checkouts"
    tp_down
    for ref in "$(app_image "$FROM_TAG")" "$(web_image "$FROM_TAG")" "$(app_image "$TO_TAG")" "$(web_image "$TO_TAG")"; do tp_image_rm "$ref"; done
    for wt in "$WORK"/src-*; do [[ -d "$wt" ]] && git -C "$REPO" worktree remove --force "$wt"; done
  fi
  rm -rf "$WORK"
}
trap cleanup EXIT

# Secrets of its own: the test starts from zero and never reads the live .env.
secret() { python3 -c 'import secrets; print(secrets.token_hex(24))'; }
DOMAIN="$PROJECT.localhost"
{
  echo "DOMAIN=$DOMAIN"
  echo "ADMIN_EMAIL=admin@example.test"
  for key in ADMIN_PASSWORD DB_PASSWORD APP_KEY ENGINE_KEY ARGUS_KEY; do echo "$key=$(secret)"; done
  echo "GATEWAY_KEY=sk-$(secret)"
  echo "HTTPS_PORT=$PORT"
  echo "MODELS_DIR=$TP_MODELS"
  echo "MODEL="
} > "$TP_ENV"
chmod 600 "$TP_ENV"
CHECK=(python3 "$ROOT/scripts/recovery-check.py")
URL="https://$DOMAIN:$PORT"
BACKUP="$WORK/backup-before-upgrade"

if [[ $DRY -eq 1 ]]; then
  tp_use "$ROOT/docker-compose.yml" "$(app_image "$TO_TAG")" "$(web_image "$TO_TAG")"
  say "Would roll $FROM_TAG ($FROM_VERSION) back to $TO_TAG ($TO_VERSION) in $PROJECT (dry run: nothing is built or started)"
  say "  live project left alone: $(tp_live_projects | sort -u | sed '/^$/d' | tr '\n' ' ')"
  say "  0. check out $TO_TAG and $FROM_TAG (git worktree) and build their app and web images:"
  say "       $(app_image "$TO_TAG") $(web_image "$TO_TAG") $(app_image "$FROM_TAG") $(web_image "$FROM_TAG")"
  say "  1. $TO_TAG from zero ($TP_CORE); a person, a group, a chat and a setting in; the backup before the upgrade"
  say "  2. $FROM_TAG over it: migrates forward, comes up at $FROM_VERSION with all of it; more goes in"
  say "  3. $TO_TAG's images straight over that database: the older app comes up on it"
  say "  4. down, scripts/backup.sh --restore --from <the backup from 1> --yes, $TO_TAG up: at $TO_VERSION, on its own last migration, with 1 and without 2"
  say "  5. $([[ $KEEP -eq 1 ]] && echo "keep it all (--keep)" || echo "remove $PROJECT, its volumes, its images and the checkouts")"
  say "  at $URL (127.0.0.1); the project's compose override:"
  sed 's/^/    /' "$TP_ISOLATE"
  exit 0
fi

command -v docker >/dev/null || die "docker not found"
docker ps -aq --filter "label=com.docker.compose.project=$PROJECT" | grep -q . \
  && die "$PROJECT exists already: $0 --project $PROJECT --down"

FAILED=0
check() {   # check NAME OK [DETAIL]
  if [[ "$2" == 1 ]]; then say "  PASS  $1${3:+  ($3)}"; else say "  FAIL  $1${3:+  ($3)}"; FAILED=$((FAILED + 1)); fi
}
api() { RECOVERY_PASSWORD="$(tp_env_get ADMIN_PASSWORD "$TP_ENV")" "${CHECK[@]}" "$1" --url "$URL" --connect 127.0.0.1 "${@:2}" || FAILED=$((FAILED + 1)); }
tree() { printf '%s/src-%s' "$WORK" "$(slug "$1")"; }

# Runs a release's compose file and images over the project's volumes, and waits for it.
release() {   # release TAG VERSION
  local wt; wt="$(tree "$1")"
  tp_use "$wt/deploy/docker-compose.yml" "$(app_image "$1")" "$(web_image "$1")"
  # shellcheck disable=SC2086
  tp_compose up -d --no-build --pull never --remove-orphans $TP_CORE >/dev/null 2>"$WORK/up.log" \
    || { sed 's/^/    /' "$WORK/up.log"; check "$1 starts" 0; return 1; }
  "${CHECK[@]}" wait --url "$URL" --connect 127.0.0.1 --version "$2" --minutes 10 || { FAILED=$((FAILED + 1)); return 1; }
}

# The database and the app's volumes, in backup.sh's layout, so backup.sh --restore takes it.
backup_before_upgrade() {
  local vol
  mkdir -p "$BACKUP/volumes" && chmod 700 "$BACKUP" || return 1
  tp_compose exec -T postgres pg_dumpall -U arena --clean --if-exists | gzip -6 > "$BACKUP/postgres.sql.gz" || return 1
  zcat "$BACKUP/postgres.sql.gz" | tail -n 5 | grep -q "PostgreSQL database cluster dump complete" || return 1
  for vol in engine directory; do
    docker run --rm --network none -v "${TP_PROJECT}_$vol:/src:ro" -v "$BACKUP/volumes:/out" python:3.13-slim \
      sh -c "tar -czf /out/$vol.tar.gz --numeric-owner -C /src . && chown $(id -u):$(id -g) /out/$vol.tar.gz" || return 1
  done
  (cd "$BACKUP" && find . -type f ! -name SHA256SUMS -printf '%P\n' | sort | xargs -d '\n' sha256sum > SHA256SUMS)
}

say "rollback-test: $FROM_TAG ($FROM_VERSION) back to $TO_TAG ($TO_VERSION), in $PROJECT"
STARTED=1
say "==> 0. the releases' images, built from their tags"
for tag in "$TO_TAG" "$FROM_TAG"; do
  wt="$(tree "$tag")"
  git -C "$REPO" worktree add --detach --quiet "$wt" "$tag" || die "cannot check out $tag"
  tp_use "$wt/deploy/docker-compose.yml" "$(app_image "$tag")" "$(web_image "$tag")"
  tp_run docker compose -f "$wt/deploy/docker-compose.yml" -f "$TP_IMAGES" build app web >"$WORK/build-$(slug "$tag").log" 2>&1 \
    || { tail -n 30 "$WORK/build-$(slug "$tag").log"; die "building $tag failed"; }
  say "  $tag: $(app_image "$tag"), $(web_image "$tag") (its last migration: $(tp_last_migration "$wt"))"
  for image in $(sed -n 's/^[[:space:]]*image:[[:space:]]*//p' "$wt/deploy/docker-compose.yml" | grep -E '^(traefik|pgvector|ghcr.io/berriai/litellm)') python:3.13-slim; do
    docker image inspect "$image" >/dev/null 2>&1 || die "the image $image ($tag) is not on this host (this test pulls nothing)"
  done
done
TO_SCHEMA="$(tp_last_migration "$(tree "$TO_TAG")")" FROM_SCHEMA="$(tp_last_migration "$(tree "$FROM_TAG")")"

say "==> 1. $TO_TAG from zero"
release "$TO_TAG" "$TO_VERSION" || die "$TO_TAG did not come up from zero"
schema="$(tp_schema)"
check "its database is at its last migration" "$([[ "$schema" == "$TO_SCHEMA" ]] && echo 1 || echo 0)" "$schema"
api seed --state "$WORK/before.json" --label before --setting Branding:SignInHeadline
backup_before_upgrade && check "the backup before the upgrade" 1 "$(du -sh "$BACKUP" | cut -f1)" || die "the backup before the upgrade failed"

say "==> 2. $FROM_TAG over it"
if release "$FROM_TAG" "$FROM_VERSION"; then
  schema="$(tp_schema)"
  check "it migrated the database forward to its last migration" "$([[ "$schema" == "$FROM_SCHEMA" ]] && echo 1 || echo 0)" "$schema"
  [[ "$FROM_SCHEMA" == "$TO_SCHEMA" ]] && say "  (the two releases have the same last migration: the schema did not change)"
  api verify --state "$WORK/before.json" --when "after the upgrade"
  api seed --state "$WORK/after.json" --label after --setting Branding:SupportContact
fi

say "==> 3. $TO_TAG's images straight over the database $FROM_TAG migrated"
if release "$TO_TAG" "$TO_VERSION"; then
  say "  the older app came up on the newer database ($(tp_schema))"
  api verify --state "$WORK/before.json" --when "the older app on the newer database"
else
  say "  the older app does not come up on the newer database: this is why a rollback restores the backup"
fi

say "==> 4. the procedure: the backup from before the upgrade, restored, under $TO_TAG"
tp_compose down >/dev/null 2>&1
tp_run "$ROOT/scripts/backup.sh" --restore --from "$BACKUP" --yes || die "scripts/backup.sh --restore failed"
if release "$TO_TAG" "$TO_VERSION"; then
  schema="$(tp_schema)"
  check "the database is back at $TO_TAG's last migration" "$([[ "$schema" == "$TO_SCHEMA" ]] && echo 1 || echo 0)" "$schema"
  absent=(); [[ -f "$WORK/after.json" ]] && absent=(--absent "$WORK/after.json")
  api verify --state "$WORK/before.json" "${absent[@]}" --when "after the rollback"
fi

say ""
if [[ $FAILED -eq 0 ]]; then
  say "rollback-test: PASS ($FROM_TAG rolls back to $TO_TAG by restoring the backup taken before the upgrade)"
else
  say "rollback-test: FAIL ($FAILED check(s))"
fi
[[ $FAILED -eq 0 ]]
