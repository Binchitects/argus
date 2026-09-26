#!/bin/sh
# llama.cpp in router mode: one server, several models, one loaded at a time
# (LLAMACPP_MODELS_MAX). Switching between models is an API call and needs no
# restart; that is what makes Admin -> Models switch live.
#
#   - The default model's preset comes from .env (LLAMACPP_*, MODEL_*), with the
#     same arguments single-model mode used, LLAMACPP_EXTRA_ARGS translated.
#   - More models come from the app, which writes /presets/models.ini.
#   - Presets are read only when llama-server starts, so a change to that file
#     restarts it here (a few seconds, then the model loads again).
#   - At start it loads the model the app chose last (/presets/active), else
#     the default. The app keeps it loaded from then on.
#   - A model list llama-server refuses (an option it does not know stops the
#     whole router) does not stop the default model: it runs alone until the
#     app writes the list again.
#
# Router-level command-line arguments would override every model's preset,
# so everything about a model lives in its preset section.
set -u

key="${LLAMACPP_API_KEY:?set LLAMACPP_API_KEY in .env}"
name="${MODEL_NAME:?set MODEL_NAME in .env}"
extra=/presets/models.ini
active=/presets/active
presets=/tmp/presets.ini

bin=/app/llama-server
if [ -x /engine/llama-server ]; then bin=/engine/llama-server; echo "engine: $(cat /engine/.source 2>/dev/null || echo /engine)"; fi

# ---- memory: pin and/or preload the default model's weights, when that is possible
f="${LLAMACPP_MODEL_FILE:?set LLAMACPP_MODEL_FILE in .env}"
case "$f" in *-00001-of-*) files=$(ls /gguf/"${f%-00001-of-*}"-000*-of-*.gguf 2>/dev/null) ;; *) files="/gguf/$f" ;; esac
[ -z "${LLAMACPP_MTP_HEAD:-}" ] || files="$files /gguf/$LLAMACPP_MTP_HEAD"
model_kb=$(du -ckL $files 2>/dev/null | tail -1 | cut -f1)
ram_kb=$(awk '/^MemTotal:/{print $2}' /proc/meminfo)
budget_kb=$(( ram_kb - ${LLAMACPP_RAM_RESERVE_GB:-8} * 1048576 ))
on_cpu=0
[ "${LLAMACPP_N_CPU_MOE:-0}" -gt 0 ] && on_cpu=1
case "${LLAMACPP_EXTRA_ARGS:-}" in *=CPU*|*--cpu-moe*|*-ngl\ 0*) on_cpu=1 ;; esac
fits=0; [ "${model_kb:-0}" -gt 0 ] && [ "$model_kb" -le "$budget_kb" ] && fits=1
gb() { echo "$(( $1 / 1048576 )) GB"; }
mlock=0
case "${LLAMACPP_MLOCK:-auto}" in
  on)  mlock=1; [ $fits = 1 ] || echo "memory: WARNING pinning $(gb "$model_kb") with only $(gb "$budget_kb") to spare -- expect thrashing" ;;
  off) echo "memory: pinning off (LLAMACPP_MLOCK=off)" ;;
  *)   if [ $on_cpu = 0 ]; then echo "memory: weights run on the GPU; nothing in RAM to pin"
       elif [ $fits = 1 ]; then mlock=1
       else echo "memory: NOT pinning -- model $(gb "$model_kb") exceeds RAM $(gb "$ram_kb") minus the ${LLAMACPP_RAM_RESERVE_GB:-8} GB reserve; weights page in from disk on demand, so keep them on NVMe (more RAM removes the paging)"; fi ;;
esac
[ $mlock = 0 ] || echo "memory: pinning $(gb "$model_kb") of weights in RAM (mlock); $(gb $(( ram_kb - model_kb ))) left for the rest"
case "${LLAMACPP_PRELOAD:-auto}" in
  on|auto)
    if { [ $mlock = 0 ] && [ $fits = 1 ] && [ $on_cpu = 1 ]; } || [ "${LLAMACPP_PRELOAD:-}" = on ]; then
      t0=$(date +%s); cat $files > /dev/null
      echo "memory: preloaded $(gb "$model_kb") into the page cache in $(( $(date +%s) - t0 )) s"
    fi ;;
esac

# ---- command-line arguments as preset keys: --long-name value -> long-name = value
long() {
  case "$1" in
    -c) echo ctx-size ;; -t) echo threads ;; -ngl) echo n-gpu-layers ;; -b) echo batch-size ;; -ub) echo ubatch-size ;;
    -ot) echo override-tensor ;; -md) echo model-draft ;; -fa) echo flash-attn ;; -np) echo parallel ;;
    -ctk) echo cache-type-k ;; -ctv) echo cache-type-v ;; -ts) echo tensor-split ;; -sm) echo split-mode ;; -mg) echo main-gpu ;;
    -cmoe) echo cpu-moe ;; -ncmoe) echo n-cpu-moe ;; -ngld) echo n-gpu-layers-draft ;; -cd) echo ctx-size-draft ;;
    -ctkd) echo cache-type-k-draft ;; -ctvd) echo cache-type-v-draft ;; -dev) echo device ;; -tb) echo threads-batch ;;
    *) return 1 ;;
  esac
}
is_option() { case "$1" in --*|-[a-zA-Z]*) return 0 ;; *) return 1 ;; esac; }
keys() {
  while [ $# -gt 0 ]; do
    a="$1"; shift
    case "$a" in
      --*=*) k="${a#--}"; echo "${k%%=*} = ${k#*=}"; continue ;;
      --*) k="${a#--}" ;;
      -*) k=$(long "$a") || { echo "router: skipped $a: use its --long-name in LLAMACPP_EXTRA_ARGS" >&2; continue; } ;;
      *) echo "router: skipped the stray value '$a' in LLAMACPP_EXTRA_ARGS" >&2; continue ;;
    esac
    if [ $# -gt 0 ] && ! is_option "$1"; then echo "$k = $1"; shift; else echo "$k = true"; fi
  done
}

default_preset() {
  echo "[$name]"
  echo "model = /gguf/$f"
  echo "ctx-size = ${MODEL_CONTEXT:-32768}"
  echo "threads = ${LLAMACPP_THREADS:-8}"
  echo "n-gpu-layers = ${LLAMACPP_N_GPU_LAYERS:-99}"
  echo "n-cpu-moe = ${LLAMACPP_N_CPU_MOE:-0}"
  echo "cache-type-k = ${LLAMACPP_KV_TYPE:-q8_0}"
  echo "cache-type-v = ${LLAMACPP_KV_TYPE:-q8_0}"
  echo "parallel = ${LLAMACPP_PARALLEL:-1}"
  echo "jinja = true"
  echo "metrics = true"
  # Newer builds replaced --mlock with --load-mode; ask the binary which it knows.
  if [ $mlock = 1 ]; then
    if "$bin" --help 2>/dev/null | grep -q -- '--load-mode'; then echo "load-mode = mmap+mlock"; else echo "mlock = true"; fi
  fi
  if [ "${LLAMACPP_MTP_DRAFT_MAX:-0}" -gt 0 ]; then
    echo "MTP: drafting $LLAMACPP_MTP_DRAFT_MAX token(s) per step${LLAMACPP_MTP_HEAD:+ with head /gguf/$LLAMACPP_MTP_HEAD}" >&2
    [ -z "${LLAMACPP_MTP_HEAD:-}" ] || echo "model-draft = /gguf/$LLAMACPP_MTP_HEAD"
    echo "spec-type = draft-mtp"
    echo "spec-draft-n-max = $LLAMACPP_MTP_DRAFT_MAX"
    # shellcheck disable=SC2086
    keys ${LLAMACPP_MTP_ARGS:-}
  fi
  # shellcheck disable=SC2086
  keys ${LLAMACPP_EXTRA_ARGS:-}
}

# The app's models, without a section that would redefine the default one.
app_presets() {
  [ -f "$extra" ] || return 0
  awk -v skip="[$name]" '/^\[/{ keep = ($0 != skip) } keep' "$extra"
}

stamp() { stat -c %Y "$extra" 2>/dev/null || echo none; }

pid=
# The stamp of a models.ini llama-server refused (an option it does not know stops
# the whole router): until the app writes it again, the default model runs alone.
refused=
trap 'if [ -n "$pid" ]; then kill -TERM "$pid" 2>/dev/null; wait "$pid"; fi; exit 0' TERM INT

while true; do
  seen=$(stamp)
  if [ "$seen" = "$refused" ]; then
    default_preset > "$presets"
    echo "router: serving $name alone until the app's model list changes (it was refused, above)"
  else
    { default_preset; echo; app_presets; } > "$presets"
  fi
  started=$(date +%s)
  echo "router: $(grep -c '^\[' "$presets") model(s): $(grep '^\[' "$presets" | tr -d '[]' | tr '\n' ' ')"
  "$bin" --models-preset "$presets" --models-max "${LLAMACPP_MODELS_MAX:-1}" --no-models-autoload \
    --host 0.0.0.0 --port 8080 --api-key "$key" &
  pid=$!
  (
    for _ in $(seq 1 120); do curl -fs -o /dev/null --max-time 2 http://localhost:8080/health && break; sleep 1; done
    want=$(cat "$active" 2>/dev/null || true)
    grep -qxF "[$want]" "$presets" || want="$name"
    echo "router: loading $want"
    curl -fsS -o /dev/null -X POST -H "Authorization: Bearer $key" -H 'Content-Type: application/json' \
      --data "{\"model\":\"$want\"}" http://localhost:8080/models/load || echo "router: could not ask for $want"
  ) &
  restart=0
  while kill -0 "$pid" 2>/dev/null; do
    sleep 5
    if [ "$(stamp)" != "$seen" ]; then
      echo "router: the app changed the model list; restarting llama-server"
      restart=1
      kill -TERM "$pid" 2>/dev/null
      wait "$pid"
      break
    fi
  done
  if [ $restart = 0 ]; then
    wait "$pid"; code=$?
    if [ "$code" -ne 0 ] && [ $(( $(date +%s) - started )) -lt 30 ] && [ "$seen" != "$refused" ] && grep -q '^\[' "$extra" 2>/dev/null; then
      echo "router: llama-server refused the app's model list ($code); starting again without it"
      refused=$seen
      continue
    fi
    echo "router: llama-server exited ($code)"
    exit "$code"
  fi
done
