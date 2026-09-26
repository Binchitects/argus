#!/usr/bin/env bash
# Is every part of the stack up and answering? Exit status = number of failures.
source "$(dirname "$0")/lib.sh"

port=$(envget ARGUS_HTTP_PORT 8080)
gw=$(envget GATEWAY_PORT 4000)
echo "containers"
for svc in llamacpp llamacpp-embed postgres litellm argus; do
  state=$(docker inspect -f '{{.State.Status}}{{if .State.Health}}/{{.State.Health.Status}}{{end}}' "$svc" 2>/dev/null || echo missing)
  case "$state" in running/healthy|running) ok "$svc ($state)" ;; *) bad "$svc ($state)" ;; esac
done
echo "endpoints"
curl -fsS -m 5 "http://127.0.0.1:$port/healthz" >/dev/null && ok "app        http://127.0.0.1:$port" || bad "app does not answer on :$port"
curl -fsS -m 5 "http://127.0.0.1:$gw/health/liveliness" >/dev/null && ok "gateway    http://127.0.0.1:$gw/v1" || bad "gateway does not answer on :$gw"
docker exec llamacpp curl -fsS -m 5 http://localhost:8080/health >/dev/null 2>&1 && ok "chat model $(envget MODEL_NAME)" \
  || bad "chat model not ready (a large model takes minutes to load: docker logs -f llamacpp)"
docker exec llamacpp-embed curl -fsS -m 5 http://localhost:8080/health >/dev/null 2>&1 && ok "embeddings" || bad "embedding server not ready"
[ "$FAILED" -eq 0 ] && echo "all healthy: open http://localhost:$port" || echo "$FAILED problem(s)"
exit "$FAILED"
