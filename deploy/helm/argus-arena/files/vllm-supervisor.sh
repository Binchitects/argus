#!/bin/sh
# vLLM and SGLang serve one model a process and have no router: this runs one server for each
# model the app wants, as the app's desired state says, with no Docker socket (the same way
# llamacpp/router.sh follows models.ini and sd-serve.sh its control file). ENGINE_SERVER says
# which: vllm (`vllm serve ARGS`) or sglang (sglang/serve.py ARGS: SGLang's launch_server).
#
#   /presets/vllm/models.json   written by the app (Admin -> Models):
#     {"models": [{"name", "port", "gpus": [0, 1], "sleep": false, "args": ["/library/hf/...", "--tensor-parallel-size", "2", ...]}]}
#
# Every few seconds it reconciles: a model wanted and not running starts (on its GPUs only:
# CUDA_VISIBLE_DEVICES and HIP_VISIBLE_DEVICES, so it works on NVIDIA and AMD alike); a model
# no longer wanted drains (SIGTERM: vLLM ends the answers it has, up to VLLM_DRAIN seconds, then
# it is killed); a model whose arguments, port or GPUs changed drains and starts again; one that
# exits on its own waits 10, 20, 40... seconds (up to 5 minutes) before it starts again, and a
# change to it starts it at once; "sleep": true puts a ready model to sleep (its weights to RAM:
# vLLM's sleep mode, SGLang's memory saver) and false wakes it.
#
#   /status/<name>.json   phase (loading, ready, sleeping, draining, failed), pid, port, gpus, since,
#                         how it last exited, its tries and next try, the last lines of its log
#   /status/<name>.log    its output (trimmed to 1 MB)
#   /status/hardware.json the GPUs it sees (gpu-probe.py: nvidia-smi or rocm-smi), for the app
#   /status/supervisor.json  its heartbeat, every tick
#
# The key (ENGINE_KEY) goes to each server by the environment (VLLM_API_KEY; SGLANG_API_KEY, which
# sglang/serve.py hands SGLang inside its process), never on a command line, where ps shows it to
# anyone in the container; an entry that puts one there is refused.
set -u
here=$(cd "$(dirname "$0")" && pwd)
presets=${VLLM_PRESETS:-/presets/vllm}
models=$presets/models.json
status=${VLLM_STATUS:-/status}
run=${VLLM_RUN:-/tmp/vllm-supervisor}
server=${ENGINE_SERVER:-vllm}
case $server in
  vllm) bin=${VLLM_BIN:-vllm} ;;
  sglang) bin=${VLLM_BIN:-python3 /sglang/serve.py} ;;
  *) echo "supervisor: ENGINE_SERVER is vllm or sglang, not $server" >&2; exit 2 ;;
esac
helper=${VLLM_HELPER:-$here/vllm-helper.py}
probe=${GPU_PROBE:-$here/../gpu-probe.py}
host=${VLLM_HOST:-0.0.0.0}
tick=${VLLM_TICK:-2}
drain=${VLLM_DRAIN:-60}
backoff=${VLLM_BACKOFF:-10}
backoff_max=${VLLM_BACKOFF_MAX:-300}
stable=${VLLM_STABLE:-300}
VLLM_API_KEY=${VLLM_API_KEY:-${ENGINE_KEY:?set ENGINE_KEY in .env}}
SGLANG_API_KEY=$VLLM_API_KEY
export VLLM_API_KEY SGLANG_API_KEY
unset ENGINE_KEY
# How each puts a model to sleep and wakes it.
if [ "$server" = sglang ]; then
  sleep_path=/release_memory_occupation wake_path=/resume_memory_occupation
else
  sleep_path=/sleep?level=1 wake_path=/wake_up
fi

mkdir -p "$status" "$run/want" "$run/state"

now() { date +%s; }
# A field of the model's state, or of what is wanted of it.
st() { cat "$run/state/$1/$2" 2>/dev/null; }
put() { printf '%s' "$3" > "$run/state/$1/$2"; }
want() { cat "$run/want/$1/$2" 2>/dev/null; }
alive() { p=$(st "$1" pid); [ -n "$p" ] && kill -0 "$p" 2>/dev/null; }
to() { put "$1" phase "$2"; put "$1" since "$(now)"; echo "$server: $1 $2${3:+ ($3)}"; }

report() {
  n=$1
  python3 "$helper" status "$status/$n.json" "name=$n" "phase=$(st "$n" phase)" "pid=$(st "$n" pid || true)" \
    "port=$(st "$n" port)" "gpus=$(st "$n" gpus)" "since=$(st "$n" since)" "at=$(now)" "exit=$(st "$n" exit || echo null)" \
    "tries=$(st "$n" tries)" "next_try=$(st "$n" next)" "hash=$(st "$n" hash)" "sleep_failed=$(st "$n" sleep_failed)" --log "$status/$n.log"
}

start() {
  n=$1
  mkdir -p "$run/state/$n"
  if [ "$server" = vllm ]; then set -- serve; else set --; fi
  while IFS= read -r a; do set -- "$@" "$a"; done < "$run/want/$n/args"
  set -- "$@" --served-model-name "$n" --host "$host" --port "$(want "$n" port)"
  gpus=$(want "$n" gpus)
  log=$status/$n.log
  echo "$server: starting $n on GPUs ${gpus:-all}, port $(want "$n" port)" >> "$log"
  (
    if [ -n "$gpus" ]; then CUDA_VISIBLE_DEVICES=$gpus HIP_VISIBLE_DEVICES=$gpus; export CUDA_VISIBLE_DEVICES HIP_VISIBLE_DEVICES; fi
    while IFS= read -r kv; do [ -n "$kv" ] && export "${kv?}"; done < "$run/want/$n/env"
    # $bin is split on purpose: "python3 /sglang/serve.py" is a command and its script.
    # shellcheck disable=SC2086
    exec $bin "$@"
  ) >> "$log" 2>&1 &
  put "$n" pid "$!"
  put "$n" port "$(want "$n" port)"
  put "$n" gpus "$gpus"
  put "$n" hash "$(want "$n" hash)"
  put "$n" exit null
  put "$n" next ""
  put "$n" sleep_failed false
  put "$n" ready_at ""
  [ -f "$run/state/$n/tries" ] || put "$n" tries 0
  to "$n" loading
}

# Drains a model (SIGTERM, then SIGKILL after VLLM_DRAIN seconds): the next ticks see it end.
stop() {
  n=$1
  [ "$(st "$n" phase)" = draining ] && return 0
  if alive "$n"; then
    kill -TERM "$(st "$n" pid)" 2>/dev/null
    put "$n" until $(($(now) + drain))
    to "$n" draining "$2"
  fi
}

# One model the app wants: started, restarted, put to sleep or woken, as it is now.
reconcile() {
  n=$1
  if [ ! -d "$run/state/$n" ]; then
    start "$n"
    return
  fi
  phase=$(st "$n" phase)
  if alive "$n"; then
    if [ "$phase" = draining ]; then
      [ "$(now)" -ge "$(st "$n" until)" ] && kill -KILL "$(st "$n" pid)" 2>/dev/null
      return
    fi
    if [ "$(st "$n" hash)" != "$(want "$n" hash)" ]; then
      put "$n" tries 0
      stop "$n" "its settings changed"
      return
    fi
    port=$(st "$n" port)
    case $phase in
      loading)
        if python3 "$helper" health "$port"; then put "$n" ready_at "$(now)"; to "$n" ready; fi ;;
      ready)
        if [ "$(want "$n" sleep)" = true ]; then
          if python3 "$helper" post "$port" "$sleep_path"; then to "$n" sleeping; put "$n" sleep_failed false
          else put "$n" sleep_failed true; fi
        fi ;;
      sleeping)
        if [ "$(want "$n" sleep)" != true ] && python3 "$helper" post "$port" "$wake_path"; then to "$n" ready; fi ;;
    esac
    return
  fi
  # Not running.
  if [ "$phase" != failed ]; then
    p=$(st "$n" pid)
    code=0
    [ -n "$p" ] && { wait "$p" 2>/dev/null; code=$?; }
    if [ "$phase" = draining ]; then
      # Stopped as asked (its settings changed): started again at once.
      start "$n"
      return
    fi
    # Ready a good while before it ended: a fresh run of tries, not the next of a crash loop.
    ready_at=$(st "$n" ready_at)
    [ -n "$ready_at" ] && [ $(($(now) - ready_at)) -ge "$stable" ] && put "$n" tries 0
    tries=$(($(st "$n" tries) + 1))
    delay=$((backoff << (tries > 6 ? 5 : tries - 1)))
    [ "$delay" -gt "$backoff_max" ] && delay=$backoff_max
    put "$n" tries "$tries"
    put "$n" exit "$code"
    put "$n" next $(($(now) + delay))
    to "$n" failed "exit $code, try $tries; again in ${delay}s"
    return
  fi
  if [ "$(st "$n" hash)" != "$(want "$n" hash)" ]; then
    put "$n" tries 0
    start "$n"
  elif [ "$(now)" -ge "$(st "$n" next)" ]; then
    start "$n"
  fi
}

# A model the app no longer wants: drained, then forgotten (its status file goes; its log stays).
retire() {
  n=$1
  if alive "$n"; then
    if [ "$(st "$n" phase)" = draining ]; then
      [ "$(now)" -ge "$(st "$n" until)" ] && kill -KILL "$(st "$n" pid)" 2>/dev/null
    else
      stop "$n" "no longer wanted"
    fi
    return
  fi
  p=$(st "$n" pid)
  [ -n "$p" ] && wait "$p" 2>/dev/null
  rm -rf "${run:?}/state/$n"
  rm -f "$status/$n.json"
  echo "$server: $n stopped"
}

shutdown() {
  for d in "$run"/state/*/; do
    [ -d "$d" ] || continue
    n=$(basename "$d")
    alive "$n" && kill -TERM "$(st "$n" pid)" 2>/dev/null
  done
  end=$(($(now) + drain))
  for d in "$run"/state/*/; do
    [ -d "$d" ] || continue
    n=$(basename "$d")
    while alive "$n" && [ "$(now)" -lt "$end" ]; do sleep 1; done
    alive "$n" && kill -KILL "$(st "$n" pid)" 2>/dev/null
    rm -f "$status/$n.json"
  done
  exit 0
}

# Its tests source it for the functions alone.
[ -z "${VLLM_SUPERVISOR_LIB:-}" ] && trap shutdown TERM INT || return 0

seen= ticks=0
while true; do
  # No models.json yet (no model on this engine): nothing to run, and nothing to say.
  if [ -f "$models" ]; then stamp=$(cksum < "$models"); else stamp=none; fi
  if [ "$stamp" != "$seen" ]; then
    if [ -f "$models" ]; then
      python3 "$helper" plan "$models" "$run" > /dev/null
    else
      rm -rf "${run:?}/want" && mkdir -p "$run/want"
    fi
    seen=$stamp
  fi
  for d in "$run"/want/*/; do
    [ -d "$d" ] && reconcile "$(basename "$d")"
  done
  for d in "$run"/state/*/; do
    [ -d "$d" ] || continue
    n=$(basename "$d")
    [ -d "$run/want/$n" ] || retire "$n"
  done
  for d in "$run"/state/*/; do
    [ -d "$d" ] || continue
    n=$(basename "$d")
    report "$n"
    # tee-less logs are appended to: trimmed in place.
    if [ -f "$status/$n.log" ] && [ "$(wc -c < "$status/$n.log")" -ge 1048576 ]; then t=$(tail -c 65536 "$status/$n.log"); printf '%s\n' "$t" > "$status/$n.log"; fi
  done
  # Its heartbeat: the app takes the engine for down when this is old.
  printf '{"at":%s,"pid":%s}\n' "$(now)" "$$" > "$status/supervisor.json.tmp" && mv -f "$status/supervisor.json.tmp" "$status/supervisor.json"
  if [ $((ticks % 15)) -eq 0 ] && [ -f "$probe" ]; then python3 "$probe" --out "$status/hardware.json" 2>/dev/null || true; fi
  ticks=$((ticks + 1))
  sleep "$tick" &
  wait $!
done
