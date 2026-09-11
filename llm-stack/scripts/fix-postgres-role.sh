#!/usr/bin/env bash
# Make the role inside an existing Postgres volume match what .env names.
#
# Postgres reads POSTGRES_USER and POSTGRES_PASSWORD on FIRST INIT ONLY. After
# the data directory exists, changing them in .env changes nothing inside the
# database -- the role keeps whatever name and password it was born with. So a
# stack can run happily for months on credentials that .env does not describe,
# and the mismatch only becomes an outage when something forces a reconnect
# under the configured name.
#
# That is exactly what happened here. Compose gives SHELL environment variables
# precedence over .env, an unrelated project had POSTGRES_USER and
# POSTGRES_PASSWORD set machine-wide, and the volume was initialised from those
# instead of from the generated secrets. The variables are now LLM_PG_USER and
# LLM_PG_PASSWORD, which collide with nothing -- and this script reconciles a
# database that predates the rename.
#
# Idempotent: if the role already exists, nothing is written.
#
#   ./scripts/fix-postgres-role.sh              # create the role, move ownership
#   ./scripts/fix-postgres-role.sh --lock-old   # also revoke the old role's login
#   ./scripts/fix-postgres-role.sh --dry-run    # print the plan, change nothing
set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$ROOT"

LOCK_OLD=0
DRY=0
while [[ $# -gt 0 ]]; do
  case "$1" in
    --lock-old) LOCK_OLD=1; shift ;;
    --dry-run)  DRY=1; shift ;;
    -h|--help)  sed -n '2,22p' "$0"; exit 0 ;;
    *) echo "unknown argument: $1" >&2; exit 2 ;;
  esac
done

ok()   { printf '  \033[32mok\033[0m    %s\n' "$*"; }
warn() { printf '  \033[33mwarn\033[0m  %s\n' "$*"; }
die()  { printf '  \033[31mfail\033[0m  %s\n' "$*" >&2; exit 1; }

[[ -f .env ]] || die ".env not found -- run scripts/bootstrap first."

# Read the target from .env DIRECTLY, not from the environment. Reading it from
# the environment would defeat the point: a stray shell variable is the thing
# this script exists to undo.
#
# tr -d $'\r' is not decoration. On Windows .env is CRLF, so the value carries a
# trailing carriage return -- Compose strips it, a naive read does not, and the
# role would be created with a password nothing else can reproduce. That failure
# looks exactly like the bug this script is meant to fix.
TARGET_USER="$(sed -n 's/^LLM_PG_USER=//p' .env | tail -1 | tr -d $'\r')"
TARGET_PASS="$(sed -n 's/^LLM_PG_PASSWORD=//p' .env | tail -1 | tr -d $'\r')"
: "${TARGET_USER:=llmservice}"

[[ -n "$TARGET_PASS" ]] || die "LLM_PG_PASSWORD is empty in .env -- run scripts/bootstrap to generate one."
[[ "$TARGET_PASS" != "change-me-please" ]] || die "LLM_PG_PASSWORD is still the placeholder -- run scripts/bootstrap."

docker compose ps --status running --format '{{.Service}}' 2>/dev/null | grep -qx postgres \
  || die "the postgres container is not running -- start it first."

# Who is the database ACTUALLY running as? This is the bootstrap superuser, the
# one name that is guaranteed to be able to log in, and it is not necessarily
# what .env or docker-compose.yml say.
CURRENT_USER="$(docker inspect postgres \
  --format '{{range .Config.Env}}{{println .}}{{end}}' \
  | sed -n 's/^POSTGRES_USER=//p' | head -1 | tr -d $'\r')"
[[ -n "$CURRENT_USER" ]] || die "could not read POSTGRES_USER from the running container."

echo
echo "  database is running as : $CURRENT_USER"
echo "  .env asks for          : $TARGET_USER"
echo

psql_q() { docker exec -i postgres psql -U "$CURRENT_USER" -d postgres -tAc "$1"; }

if [[ "$CURRENT_USER" == "$TARGET_USER" ]]; then
  ok "names already agree; nothing to rename."
else
  warn "names disagree -- this is the mismatch being fixed."
fi

EXISTS="$(psql_q "select 1 from pg_roles where rolname = '$TARGET_USER'" || true)"
if [[ "$EXISTS" == "1" ]]; then
  ok "role '$TARGET_USER' already exists; leaving it alone."
  ROLE_WORK=0
else
  ROLE_WORK=1
fi

DBS="$(psql_q "select string_agg(datname, ' ') from pg_database
               where datname in ('litellm','langfuse')
                 and pg_get_userbyid(datdba) <> '$TARGET_USER'" || true)"

# Ask the DATABASE which other superusers can still log in, rather than reading
# it off the container. Once postgres has been recreated under the new name the
# container no longer remembers the old one, and comparing the two names finds
# nothing -- while the stale role, and the foreign password on it, are still
# there. The database is the only honest source for this.
STALE="$(psql_q "select string_agg(rolname, ' ') from pg_roles
                  where rolsuper and rolcanlogin
                    and rolname <> '$TARGET_USER'
                    and rolname not like 'pg_%'" || true)"

if [[ $DRY -eq 1 ]]; then
  echo "  --- plan ---"
  [[ $ROLE_WORK -eq 1 ]] && echo "  create role $TARGET_USER (superuser, login, password from .env)"
  [[ -n "$DBS" ]]        && echo "  transfer ownership to $TARGET_USER: $DBS"
  if [[ -n "$STALE" ]]; then
    if [[ $LOCK_OLD -eq 1 ]]; then echo "  revoke login from: $STALE"
    else                           echo "  leave able to log in: $STALE  (--lock-old revokes)"; fi
  fi
  echo "  (nothing written)"
  exit 0
fi

# Always dump before touching roles. A wrong move here costs every API key,
# every spend record and the whole Langfuse history.
BACKUP="backups/pg-role-fix-$(date +%Y%m%d-%H%M%S).sql"
mkdir -p backups
docker exec postgres pg_dumpall -U "$CURRENT_USER" > "$BACKUP"
[[ -s "$BACKUP" ]] || die "the pre-change dump is empty -- refusing to continue."
ok "backed up to $BACKUP ($(wc -c < "$BACKUP") bytes)"

# The password goes in on STDIN, never argv: arguments are visible to anything
# that can list processes in the container.
{
  if [[ $ROLE_WORK -eq 1 ]]; then
    printf "CREATE ROLE %s WITH LOGIN SUPERUSER PASSWORD '%s';\n" "$TARGET_USER" "$TARGET_PASS"
  else
    printf "ALTER ROLE %s WITH LOGIN SUPERUSER PASSWORD '%s';\n" "$TARGET_USER" "$TARGET_PASS"
  fi
  for db in $DBS; do
    printf 'ALTER DATABASE %s OWNER TO %s;\n' "$db" "$TARGET_USER"
  done
} | docker exec -i postgres psql -U "$CURRENT_USER" -d postgres -v ON_ERROR_STOP=1 -q

ok "role '$TARGET_USER' present with the password from .env"
[[ -n "$DBS" ]] && ok "ownership transferred: $DBS"

# Prove it before claiming it. A role that exists but cannot authenticate is
# the same outage in a different disguise.
# Note -e PGPASSWORD is NOT used: that would put the password in the docker
# command's own argv, readable by anything listing processes ON THE HOST. It
# arrives on stdin instead and never leaves this pipe.
if printf '%s' "$TARGET_PASS" | docker exec -i postgres sh -c \
     'read -r pw; PGPASSWORD="$pw" psql -U "$1" -h 127.0.0.1 -d litellm -tAc "select 1"' \
     _ "$TARGET_USER" >/dev/null 2>&1; then
  ok "verified: '$TARGET_USER' can authenticate over TCP against the litellm database"
else
  die "'$TARGET_USER' still cannot log in -- restore with: docker exec -i postgres psql -U $CURRENT_USER -d postgres < $BACKUP"
fi

if [[ -z "$STALE" ]]; then
  ok "no other superuser can log in."
elif [[ $LOCK_OLD -eq 1 ]]; then
  for r in $STALE; do
    psql_q "ALTER ROLE \"$r\" NOLOGIN" >/dev/null
    ok "revoked login from '$r' (undo: ALTER ROLE \"$r\" LOGIN)"
  done
else
  warn "still able to log in, on passwords that are not in .env: $STALE"
  warn "re-run with --lock-old once the stack is confirmed healthy."
fi

echo
echo "  Recreate the services that hold a connection string, so they pick up"
echo "  the new credentials. A restart is not enough -- it reuses the old"
echo "  environment:"
echo
echo "    docker compose up -d --force-recreate litellm grafana postgres"
echo
