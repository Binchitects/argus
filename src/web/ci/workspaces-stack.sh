#!/bin/sh
# A throwaway stack for e2e/workspaces.spec.ts (Code Studio's workspaces in a browser): Postgres, the app, the web and
# Traefik as the CI's browser job runs them, with the workspaces service as compose runs it (its security options) and a
# code host that is GitLab's API, git over HTTP and the agent's model API (tests/Llm.Tests/Resources/real-workspace/codehost.py).
# Every container and the network are its own (named wse2e-*), so it runs beside another stack.
#
#   src/web/ci/workspaces-stack.sh up      # then: E2E_WORKSPACES=1 E2E_BASE_URL=https://llm.localhost:$E2E_PORT npx playwright test e2e/workspaces.spec.ts
#   src/web/ci/workspaces-stack.sh down
#
# APP_IMAGE, WEB_IMAGE and WS_IMAGE name the images built from this checkout (src/Llm.Api/Dockerfile, src/web/Dockerfile,
# deploy/services/workspaces/Dockerfile); E2E_PORT the HTTPS port (8443); E2E_PASSWORD the admin's password.
set -eu
here=$(cd "$(dirname "$0")" && pwd)
root=$(cd "$here/../../.." && pwd)
port=${E2E_PORT:-8443}
app=${APP_IMAGE:-llm-app:ci}
web=${WEB_IMAGE:-llm-web:ci}
ws=${WS_IMAGE:-ws-e2e}
net=wse2e
key=wse2e-workspaces-key-0123456789abcdef
run=${E2E_STACK_DIR:-${TMPDIR:-/tmp}/wse2e}

down() {
  docker rm -f wse2e-traefik wse2e-web wse2e-app wse2e-workspaces wse2e-codehost wse2e-postgres >/dev/null 2>&1 || true
  docker network rm "$net" >/dev/null 2>&1 || true
  rm -rf "$run"
}

logs() {
  for c in wse2e-app wse2e-workspaces wse2e-codehost wse2e-traefik; do echo "== $c"; docker logs --tail 150 "$c" 2>&1; done
}

up() {
  : "${E2E_PASSWORD:?set E2E_PASSWORD to the admin password the stack gets}"
  down
  mkdir -p "$run"
  openssl req -x509 -newkey rsa:2048 -nodes -days 1 -subj /CN=llm.localhost \
    -addext "subjectAltName=DNS:llm.localhost,DNS:*.llm.localhost" -keyout "$run/tls.key" -out "$run/tls.crt" 2>/dev/null
  chmod 644 "$run"/tls.*
  # The CI's routes, the forwarded ports' (ws-<workspace>-<port>.llm.localhost) to the app as the stack's own Traefik has them.
  cp "$here/traefik.yml" "$run/traefik.yml"
  cp "$here/routes.yml" "$run/routes.yml"
  docker network create "$net" >/dev/null
  docker run -d --name wse2e-postgres --network "$net" --network-alias postgres -e POSTGRES_USER=app -e POSTGRES_PASSWORD=ci \
    --health-cmd "pg_isready -U app" --health-interval 2s pgvector/pgvector:0.8.0-pg16 >/dev/null
  until [ "$(docker inspect -f '{{.State.Health.Status}}' wse2e-postgres)" = healthy ]; do sleep 1; done
  docker exec wse2e-postgres psql -U app -d postgres -qc "create database litellm"
  docker exec -i wse2e-postgres psql -U app -d litellm -q < "$root/tests/Llm.Tests/Resources/litellm-schema.sql"
  # The code host: the workspaces image's Python and git; the agent's one command is a harmless echo.
  docker run -d --name wse2e-codehost --network "$net" --network-alias codehost -e E2E_COMMAND="echo from the agent" \
    -v "$root/tests/Llm.Tests/Resources/real-workspace/codehost.py:/e2e/codehost.py:ro" --entrypoint python3 "$ws" -u /e2e/codehost.py >/dev/null
  # As compose runs it (docker-compose.yml's workspaces).
  docker run -d --name wse2e-workspaces --network "$net" --network-alias workspaces --init --cap-drop ALL \
    --cap-add SETUID --cap-add SETGID --cap-add CHOWN --cap-add DAC_OVERRIDE --cap-add FOWNER --cap-add KILL \
    --security-opt no-new-privileges:true --security-opt "seccomp=$root/deploy/config/workspaces-seccomp.json" \
    --security-opt apparmor=unconfined --security-opt systempaths=unconfined --pids-limit 4096 \
    -e WORKSPACES_KEY="$key" -e WORKSPACES_SLOTS=4 "$ws" >/dev/null
  docker run -d --name wse2e-app --network "$net" --network-alias app --read-only --tmpfs /tmp \
    -e Database__Host=postgres -e Database__Username=app -e Database__Password=ci \
    -e Auth__Domain=llm.localhost -e Auth__HttpsPort="$port" -e "Auth__AdminPassword=$E2E_PASSWORD" \
    -e Auth__DataKey=wse2e-data-key -e Gateway__Url=http://codehost:8000 -e Gateway__MasterKey=sk-wse2e-master \
    -e GitLab__Url=http://codehost:8000 -e Workspaces__Url=http://workspaces:4000 -e Workspaces__Key="$key" \
    -e Workspaces__EngineHost=workspaces -e Workspaces__ArenaUrl=http://codehost:8000 -e Modules__workspaces=true "$app" >/dev/null
  docker run -d --name wse2e-web --network "$net" --network-alias web --read-only --tmpfs /tmp --cap-drop ALL \
    --security-opt no-new-privileges:true "$web" >/dev/null
  docker run -d --name wse2e-traefik --network "$net" -p "$port:8443" -v "$run:/certs:ro" \
    -v "$run/traefik.yml:/etc/traefik/traefik.yml:ro" -v "$run/routes.yml:/etc/traefik/routes.yml:ro" traefik:v3.6.7 >/dev/null
  i=0
  while [ $i -lt 90 ]; do
    if [ "$(docker inspect -f '{{.State.Health.Status}}' wse2e-app)" = healthy ] && [ "$(docker inspect -f '{{.State.Health.Status}}' wse2e-web)" = healthy ] \
      && docker logs wse2e-workspaces 2>&1 | grep -q "The agent's commands:" \
      && curl -sfk --resolve "llm.localhost:$port:127.0.0.1" "https://llm.localhost:$port/api/info" >/dev/null; then
      echo "workspaces-stack: up at https://llm.localhost:$port"
      return 0
    fi
    i=$((i + 1))
    sleep 2
  done
  logs
  echo "workspaces-stack: it did not come up" >&2
  return 1
}

case "${1:-}" in
  up) up ;;
  down) down ;;
  logs) logs ;;
  *) echo "usage: $0 up | down | logs" >&2; exit 2 ;;
esac
