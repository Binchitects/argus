#!/bin/sh
# A picture or video server (stable-diffusion.cpp) that runs while the app says so:
# /control/$NAME holds "on" or "off" (Admin -> Models: load, unload, keep loaded).
# It waits for its model files ($FILES, in the working directory); the app fetches them.
set -u
pid=
want() { [ "$(cat "/control/$NAME" 2>/dev/null)" = on ]; }
ready() { for f in $FILES; do [ -f "$f" ] || return 1; done; }
trap '[ -z "$pid" ] || kill "$pid" 2>/dev/null; exit 0' TERM INT
while true; do
  if want && ready; then
    if [ -z "$pid" ] || ! kill -0 "$pid" 2>/dev/null; then
      echo "$NAME: loading"
      /sd-server "$@" &
      pid=$!
    fi
  elif [ -n "$pid" ]; then
    echo "$NAME: unloading"
    kill "$pid" 2>/dev/null
    wait "$pid" 2>/dev/null
    pid=
  fi
  sleep 3
done
