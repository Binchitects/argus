#!/bin/sh
# llama.cpp in router mode: the models the app lists (Admin -> Models writes
# /presets/models.ini), loaded and unloaded live with no restart.
#
#   /presets/keep  the models kept loaded, one a line (loaded at start)
#   /presets/max   how many may be loaded at once, those kept included (2)
#
# Presets are read only when llama-server starts, so a change to the list
# restarts it here. When a place is left beside the kept models, any other
# model loads when a request asks for it; when every place is kept, none does.
set -u
key="${ENGINE_KEY:?set ENGINE_KEY in .env}"
models=/presets/models.ini
keep=/presets/keep

max() { m=$(cat /presets/max 2>/dev/null); echo "${m:-2}"; }
stamp() { cat "$models" /presets/max 2>/dev/null | cksum; }
kept() {
  [ -f "$keep" ] || return 0
  while IFS= read -r m; do [ -n "$m" ] && grep -qxF "[$m]" "$models" && echo "$m"; done < "$keep"
}
autoload() { if [ "$(kept | wc -l)" -lt "$(max)" ]; then echo --models-autoload; else echo --no-models-autoload; fi; }

until [ -f "$models" ]; do echo "router: waiting for the app's model list"; sleep 10; done

pid=
trap 'if [ -n "$pid" ]; then kill -TERM "$pid" 2>/dev/null; wait "$pid"; fi; exit 0' TERM INT
while true; do
  seen=$(stamp)
  mode=$(autoload)
  echo "router: $(grep -c '^\[' "$models") model(s), up to $(max) at once; kept loaded: $(kept | tr '\n' ' ')($mode)"
  /app/llama-server --models-preset "$models" --models-max "$(max)" "$mode" --host 0.0.0.0 --port 8080 --api-key "$key" &
  pid=$!
  (
    for _ in $(seq 1 120); do curl -fs -o /dev/null --max-time 2 http://localhost:8080/health && break; sleep 1; done
    # One after another: each load fits itself into what the one before left free.
    kept | while IFS= read -r want; do
      echo "router: loading $want"
      curl -fsS -o /dev/null -X POST -H "Authorization: Bearer $key" -H 'Content-Type: application/json' \
        --data "{\"model\":\"$want\"}" http://localhost:8080/models/load || continue
      for _ in $(seq 1 600); do
        curl -fs -H "Authorization: Bearer $key" http://localhost:8080/models 2>/dev/null \
          | grep -q "\"id\":\"$want\"[^}]*\"value\":\"loading\"" || break
        sleep 1
      done
    done
  ) &
  while kill -0 "$pid" 2>/dev/null; do
    sleep 5
    if [ "$(stamp)" != "$seen" ] || [ "$(autoload)" != "$mode" ]; then
      echo "router: the model list changed; restarting llama-server"
      kill -TERM "$pid" 2>/dev/null
      wait "$pid"
      continue 2
    fi
  done
  wait "$pid"; code=$?
  echo "router: llama-server exited ($code); starting again in 10 s"
  sleep 10
done
