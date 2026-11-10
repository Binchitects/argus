#!/bin/sh
# A picture or video server (stable-diffusion.cpp) running the model the app names.
#
# The app writes /control/$NAME.conf, read line by line and never run:
#   want=on|off   whether the server runs
#   gen=N         raised by the app to start it again (a new model, new settings, a retry)
#   model=NAME    what it runs (for the log and the status)
#   gpu=N         the GPU it runs on (as nvidia-smi numbers them; empty: every GPU it sees)
#   file=PATH     each file it needs, in the library (it waits for them)
#   arg=VALUE     each of the server's arguments, in order (paths in the library)
#   vae_gpu_mb=N  decode on the GPU when it has this much free as the server loads, with
#   vae_gpu_arg=  these arguments then, and these (vae_cpu_arg=) when it decodes on the CPU
#
# Every 2 s it writes /status/$NAME.json: the gen it runs, its phase (off, waiting for its
# files, running, exited, stopping), since when, the GPU's free memory, how it exited, the
# files it lacks and where it decodes; the server's output goes to /status/$NAME.log too.
# It never starts a server that exited again on its own: the app raises gen.
set -u
conf=/control/$NAME.conf out=/status/$NAME server=${SD_SERVER:-/sd-server} tick=${SD_SERVE_TICK:-2}
pid= ran= phase=off since=$(date +%s) code=null missing= free=null vae=

get() { sed -n "s/^$1=//p" "$conf" 2>/dev/null | head -n 1; }
to() { phase=$1; since=$(date +%s); }

# The free memory of the GPU it runs on, in MB; null when it cannot be read.
read_free() {
  free=$(nvidia-smi --id="${1:-0}" --query-gpu=memory.free --format=csv,noheader,nounits 2>/dev/null | head -n 1 | tr -dc 0-9)
  free=${free:-null}
}

report() {
  printf '{"gen":"%s","phase":"%s","since":%s,"at":%s,"free_mb":%s,"exit":%s,"missing":[%s],"vae":"%s","model":"%s"}\n' \
    "$ran" "$phase" "$since" "$(date +%s)" "$free" "$code" "$missing" "$vae" "$(get model)" > "$out.json.tmp" && mv -f "$out.json.tmp" "$out.json"
}

stop() {
  [ -n "$pid" ] || return 0
  to stopping
  report
  kill "$pid" 2>/dev/null
  i=0
  while kill -0 "$pid" 2>/dev/null && [ "$i" -lt 15 ]; do sleep 1; i=$((i + 1)); done
  kill -9 "$pid" 2>/dev/null
  wait "$pid" 2>/dev/null
  pid=
  to off
}

start() {
  missing=
  while IFS= read -r l; do
    case $l in file=*) [ -f "/models/${l#file=}" ] || missing="$missing${missing:+,}\"${l#file=}\"" ;; esac
  done < "$conf"
  if [ -n "$missing" ]; then
    [ "$phase" = waiting ] || to waiting
    return
  fi
  set -- --listen-ip 0.0.0.0 --listen-port 1234
  while IFS= read -r l; do case $l in arg=*) set -- "$@" "${l#arg=}" ;; esac; done < "$conf"
  # Where it decodes: on the GPU when it has room now, else on the CPU. Read again here: a server started again (the
  # app made room for it) has just stopped and given its own memory back, and the chat models theirs.
  vae= need=$(get vae_gpu_mb) extra=
  if [ -n "$need" ]; then
    read_free "$(get gpu)"
    if [ "$free" != null ] && [ "$free" -ge "$need" ]; then vae=gpu extra=vae_gpu_arg; else vae=cpu extra=vae_cpu_arg; fi
    while IFS= read -r l; do case $l in "$extra"=*) set -- "$@" "${l#"$extra"=}" ;; esac; done < "$conf"
  fi
  rm -f "$out.fifo"
  mkfifo "$out.fifo"
  : > "$out.log"
  tee -a "$out.log" < "$out.fifo" &
  echo "$NAME: loading $(get model)${vae:+ (decoding on the ${vae}: $free MB free)}"
  gpu=$(get gpu)
  if [ -n "$gpu" ]; then
    (cd /models && CUDA_VISIBLE_DEVICES=$gpu exec "$server" "$@") > "$out.fifo" 2>&1 &
  else
    (cd /models && exec "$server" "$@") > "$out.fifo" 2>&1 &
  fi
  pid=$! code=null
  to running
}

# Its tests source it for the functions alone.
[ -z "${SD_SERVE_LIB:-}" ] || return 0

trap 'stop; report; exit 0' TERM INT
while true; do
  read_free "$(get gpu)"
  if [ -n "$pid" ] && ! kill -0 "$pid" 2>/dev/null; then
    wait "$pid"
    code=$?
    pid=
    to exited
  fi
  gen=$(get gen)
  if [ "$(get want)" = on ]; then
    if [ "$gen" != "$ran" ]; then
      stop
      ran=$gen
      start
    elif [ "$phase" = waiting ] || [ "$phase" = off ]; then
      # Waiting for its files, or turned off and on again: started (an exit waits for a new generation).
      start
    fi
  else
    stop
    [ "$phase" = off ] || to off
  fi
  # tee appends, so the log is trimmed in place.
  if [ -f "$out.log" ] && [ "$(wc -c < "$out.log")" -ge 1048576 ]; then t=$(tail -c 65536 "$out.log"); printf '%s\n' "$t" > "$out.log"; fi
  report
  sleep "$tick"
done
