#!/usr/bin/env bash
# Sourced by restore-test.sh and rollback-test.sh, not run: a throwaway compose
# project beside the live one, and the guards that keep everything they do away
# from the live project (arena) and its volumes.
#
# A throwaway project has a name of its own that says "test", its own volumes,
# network and port, no fixed container names, and only the core services:
# postgres, app, web, traefik and litellm. Every other service is left out. Its
# network has no way out (a restored copy sends no mail, calls no webhook,
# reaches no directory), and Traefik answers on 127.0.0.1 only. Nothing is
# pulled or built when it starts: the images must be on this host.
#
# The sourcing script sets TP_ROOT (this folder, deploy/), then:
#   tp_guard_project NAME, tp_guard_port N      die unless safe
#   tp_init NAME PORT WORK                      the project's files go in WORK
#   tp_use COMPOSE_FILE APP_IMAGE WEB_IMAGE     which compose file and images it runs
#   tp_compose ARGS / tp_run CMD                compose, or any command, aimed at it only
#   tp_down                                     its containers, networks and volumes, gone

TP_CORE="postgres app web traefik litellm"

tp_say() { printf '%s\n' "$*"; }
tp_die() { printf '%s: ERROR: %s\n' "${TP_NAME:-test-project}" "$*" >&2; exit 1; }
tp_env_get() { grep -E "^$1=" "$2" 2>/dev/null | tail -n1 | cut -d= -f2- | sed -e 's/^"//' -e 's/"$//'; }

# The names the live stack goes by: never a throwaway project's.
tp_live_projects() {
  echo arena
  tp_env_get COMPOSE_PROJECT_NAME "$TP_ROOT/.env"
  sed -n 's/^name:[[:space:]]*//p' "$TP_ROOT/docker-compose.yml" 2>/dev/null | head -n1
}

tp_guard_project() {   # tp_guard_project NAME
  local name=$1 live
  while IFS= read -r live; do
    [[ -n "$live" && "$name" == "$live" ]] && tp_die "\"$name\" is the live project: refusing to touch it or its volumes"
  done < <(tp_live_projects)
  [[ "$name" =~ ^[a-z0-9][a-z0-9-]*$ ]] || tp_die "project name \"$name\": lowercase letters, digits and - only"
  [[ "$name" == *test* ]] || tp_die "a throwaway project's name says test; \"$name\" does not"
  return 0
}

tp_guard_port() {   # tp_guard_port N: not one of the live stack's, and free
  local port=$1 live
  for live in 80 443 "$(tp_env_get HTTP_PORT "$TP_ROOT/.env")" "$(tp_env_get HTTPS_PORT "$TP_ROOT/.env")"; do
    [[ -n "$live" && "$port" == "$live" ]] && tp_die "port $port is the live stack's"
  done
  [[ "$port" =~ ^[0-9]+$ ]] && (( port >= 1024 && port <= 65535 )) || tp_die "port \"$port\": a number from 1024 to 65535"
  if (exec 3<>"/dev/tcp/127.0.0.1/$port") 2>/dev/null; then tp_die "port $port is in use"; fi
  return 0
}

tp_init() {   # tp_init NAME PORT WORK
  tp_guard_project "$1"
  TP_PROJECT=$1 TP_PORT=$2 TP_WORK=$3
  TP_ENV="$TP_WORK/test.env" TP_ISOLATE="$TP_WORK/isolate.yml" TP_IMAGES="$TP_WORK/images.yml"
  TP_MODELS="$TP_WORK/models"
  mkdir -p "$TP_MODELS"
}

tp_env_set() {   # tp_env_set KEY VALUE: in the project's env file (no value: the line goes)
  local key=$1 tmp
  tmp="$(mktemp "$TP_WORK/env.XXXXXX")"
  grep -v -E "^$key=" "$TP_ENV" > "$tmp" 2>/dev/null
  [[ $# -gt 1 ]] && printf '%s=%s\n' "$key" "$2" >> "$tmp"
  mv "$tmp" "$TP_ENV" && chmod 600 "$TP_ENV"
}

# Which compose file the project runs (a release's, for a rollback) and the app's and web's images.
tp_use() {   # tp_use COMPOSE_FILE APP_IMAGE WEB_IMAGE
  TP_COMPOSE=$1
  {
    echo "# The images $TP_PROJECT runs (test-project.sh)."
    echo "services:"
    echo "  app: { image: \"$2\" }"
    echo "  web: { image: \"$3\" }"
  } > "$TP_IMAGES"
  tp_write_isolate
}

# Every service of the compose file: no fixed name, no restart; the core runs from
# images already here, on a network with no way out; the rest is left out.
tp_write_isolate() {
  local svc all
  all="$(env -u COMPOSE_PROFILES -u COMPOSE_FILE -u COMPOSE_PROJECT_NAME COMPOSE_ENV_FILES="$TP_ENV" \
    docker compose -p "$TP_PROJECT" -f "$TP_COMPOSE" config --services 2>/dev/null)"
  [[ -n "$all" ]] || all="$(sed -n '/^services:/,/^[^ ]/s/^  \([a-z0-9-]*\):.*/\1/p' "$TP_COMPOSE")"
  {
    echo "# Written by test-project.sh: $TP_PROJECT, a throwaway project beside the live one."
    echo "networks:"
    echo "  # No way out: a restored copy sends no mail, calls no webhook, reaches no directory."
    echo "  default: { internal: true }"
    echo "  edge: {}"
    echo "services:"
    for svc in $all; do
      echo "  $svc:"
      echo "    container_name: !reset null"
      echo "    restart: \"no\""
      case " $TP_CORE " in
        *" $svc "*)
          echo "    pull_policy: never"
          case "$svc" in app|web) echo "    build: !reset null" ;; esac
          if [[ $svc == traefik ]]; then
            echo "    networks: [default, edge]"
            echo "    ports: !override [\"127.0.0.1:$TP_PORT:443\"]"
          fi
          ;;
        *) echo "    profiles: [off]" ;;
      esac
    done
  } > "$TP_ISOLATE"
}

# Compose, or any command (backup.sh), aimed at the throwaway project and nothing else.
tp_run() {
  tp_guard_project "$TP_PROJECT"
  env -u COMPOSE_PROFILES COMPOSE_PROJECT_NAME="$TP_PROJECT" COMPOSE_FILE="$TP_COMPOSE:$TP_IMAGES:$TP_ISOLATE" \
    COMPOSE_ENV_FILES="$TP_ENV" HTTPS_PORT="$TP_PORT" MODELS_DIR="$TP_MODELS" MODEL= ACME_EMAIL= "$@"
}
tp_compose() { tp_run docker compose "$@"; }

tp_volume_rm() {   # tp_volume_rm NAME: only ever one of the throwaway project's
  tp_guard_project "$TP_PROJECT"
  [[ "$1" == "${TP_PROJECT}_"* ]] || tp_die "refusing to remove the volume $1: it is not $TP_PROJECT's"
  docker volume rm -f "$1" >/dev/null 2>&1 || tp_say "  could not remove the volume $1"
}

tp_image_rm() {   # tp_image_rm REF: only images the test built, named for it
  tp_guard_project "$TP_PROJECT"
  [[ "$1" == "${TP_PROJECT}-"* ]] || tp_die "refusing to remove the image $1: $TP_PROJECT did not build it"
  docker image rm "$1" >/dev/null 2>&1 || true
}

# Its containers, networks and volumes, found by the project's label and its
# volumes' prefix (backup.sh --restore makes some with docker run, unlabelled).
tp_down() {
  local label="label=com.docker.compose.project=$TP_PROJECT" ids v n
  tp_guard_project "$TP_PROJECT"
  ids="$(docker ps -aq --filter "$label")"
  [[ -n "$ids" ]] && docker rm -f $ids >/dev/null 2>&1
  for n in $(docker network ls -q --filter "$label"); do docker network rm "$n" >/dev/null 2>&1; done
  for v in $( { docker volume ls -q --filter "$label"; docker volume ls -q | grep -F "${TP_PROJECT}_"; } | sort -u); do
    [[ "$v" == "${TP_PROJECT}_"* ]] && tp_volume_rm "$v"
  done
  return 0
}

# The newest migration a checkout's app knows (EF's id: time stamp and name).
tp_last_migration() {   # tp_last_migration REPO_DIR
  find "$1/src/Llm.Core/Data/Migrations" -maxdepth 1 -name '[0-9]*_*.cs' ! -name '*.Designer.cs' -printf '%f\n' 2>/dev/null \
    | sed 's/\.cs$//' | sort | tail -n1
}

# The newest migration applied to the project's app database.
tp_schema() {
  tp_compose exec -T postgres psql -U arena -d llmapp -Atc 'select max("MigrationId") from "__EFMigrationsHistory"' 2>/dev/null | tr -d '\r'
}
