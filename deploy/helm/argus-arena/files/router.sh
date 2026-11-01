#!/bin/sh
# llama.cpp in router mode: the models the app lists (Admin -> Models writes
# /presets/models.ini), loaded and unloaded live with no restart.
#
#   /presets/keep  the models kept loaded, one a line (loaded at start)
#   /presets/max   how many may be loaded at once, those kept included (2)
#   /presets/kvcache  how much the conversations' caches on disk (/kvcache, KV_CACHE_DIR) may
#                  hold (gb=) and for how long after their last use (days=; 0: for ever)
#   /presets/llamacpp.restart  an admin's Restart (Admin -> Monitoring): a new gen= in it
#                  starts llama-server again, as a change to the list does (read, never run)
#
# Presets are read only when llama-server starts, so a change to the list (or
# to max) restarts it here; keeping or unloading a model does not. A model that
# is not loaded loads when a request asks for it: the app makes room for it
# first (it unloads an idle model that may make room), so the engine never
# chooses for itself which model goes.
#
# llama-server is stopped with TERM, and killed when it has not ended within ROUTER_DRAIN
# seconds (15): one that hangs never holds the router.
set -u
key="${ENGINE_KEY:?set ENGINE_KEY in .env}"
models=/presets/models.ini
keep=/presets/keep
restart=/presets/llamacpp.restart
drain=${ROUTER_DRAIN:-15}
tick=${ROUTER_TICK:-5}

max() { m=$(cat /presets/max 2>/dev/null); echo "${m:-2}"; }
stamp() { cat "$models" /presets/max 2>/dev/null | cksum; }
asked() { sed -n 's/^gen=//p' "$restart" 2>/dev/null | head -n 1; }
kept() {
  [ -f "$keep" ] || return 0
  while IFS= read -r m; do [ -n "$m" ] && grep -qxF "[$m]" "$models" && echo "$m"; done < "$keep"
}

until [ -f "$models" ]; do echo "router: waiting for the app's model list"; sleep 10; done

# The conversations' caches on disk: within their size and age, the least lately used first out
# (a restore reads a file, which marks it used). Every ten minutes, beside the engine.
kv_keep() {
  [ -d /kvcache ] || return 0
  gb=$(sed -n 's/^gb=//p' /presets/kvcache 2>/dev/null); days=$(sed -n 's/^days=//p' /presets/kvcache 2>/dev/null)
  [ "${days:-0}" -gt 0 ] 2>/dev/null && find /kvcache -type f -name '*.bin' -mtime +"$days" -atime +"$days" -delete 2>/dev/null
  [ "${gb:-0}" -gt 0 ] 2>/dev/null || return 0
  limit=$((gb * 1024 * 1024))
  used=$(du -sk /kvcache 2>/dev/null | cut -f1)
  [ "${used:-0}" -gt "$limit" ] || return 0
  find /kvcache -type f -name '*.bin' -printf '%A@ %k %p\n' 2>/dev/null | sort -n | while read -r _ kb path; do
    [ "$used" -le "$limit" ] && break
    rm -f "$path" && used=$((used - kb))
  done
}
( while true; do kv_keep; sleep 600; done ) &

# Stops llama-server: TERM, then KILL once the drain is over.
halt() {
  kill -TERM "$pid" 2>/dev/null
  i=0
  while kill -0 "$pid" 2>/dev/null && [ "$i" -lt "$drain" ]; do sleep 1; i=$((i + 1)); done
  if kill -0 "$pid" 2>/dev/null; then
    echo "router: llama-server did not stop within ${drain} s; killing it"
    kill -KILL "$pid" 2>/dev/null
  fi
  wait "$pid" 2>/dev/null
}

pid=
trap 'if [ -n "$pid" ]; then halt; fi; exit 0' TERM INT
while true; do
  seen=$(stamp) told=$(asked)
  echo "router: $(grep -c '^\[' "$models") model(s), up to $(max) at once; kept loaded: $(kept | tr '\n' ' ')"
  # Each model's folder for its conversations' caches, which the engine writes but does not make.
  sed -n 's/^slot-save-path = //p' "$models" | while IFS= read -r d; do mkdir -p "$d"; done
  # The key goes by the environment (LLAMA_API_KEY), not the command line, where ps shows it to anyone in the container.
  LLAMA_API_KEY="$key" /app/llama-server --models-preset "$models" --models-max "$(max)" --models-autoload --host 0.0.0.0 --port 8080 &
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
    sleep "$tick"
    if [ "$(stamp)" != "$seen" ]; then
      echo "router: the model list changed; restarting llama-server"
      halt
      continue 2
    fi
    if [ "$(asked)" != "$told" ]; then
      echo "router: a restart was asked$(sed -n 's/^by=/ by /p' "$restart" 2>/dev/null | head -n 1); restarting llama-server"
      halt
      continue 2
    fi
  done
  wait "$pid"; code=$?
  echo "router: llama-server exited ($code); starting again in 10 s"
  sleep 10
done
