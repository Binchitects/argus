#!/bin/sh
# A picture or video server (stable-diffusion.cpp) that runs while the app says so:
# /control/$NAME holds "on" or "off" (Admin -> Models: load, unload, keep loaded).
# It waits for its model files ($FILES, in the working directory); the app fetches them.
#
# VAE_GPU_MB (the video server's): the free GPU memory, in MB, that decoding on the
# GPU needs. Each time the server loads, it decodes there when the GPU has that much
# free, and on the CPU (--vae-on-cpu: slower) when the chat model holds the rest.
# VAE_GPU_FLAGS: the server's flags while it decodes there (a larger GPU budget, and
# its weights kept in RAM until needed), given after its own: the last one counts.
set -u
pid=
want() { [ "$(cat "/control/$NAME" 2>/dev/null)" = on ]; }
ready() { for f in $FILES; do [ -f "$f" ] || return 1; done; }

# The free memory of the GPU the server uses (the first, as CUDA numbers them), in MB; empty when unknown.
gpu_free_mb() { nvidia-smi --query-gpu=memory.free --format=csv,noheader,nounits 2>/dev/null | head -n 1 | tr -dc 0-9; }

# Sets $vae (the server's VAE flag, or nothing) and $where (for the log).
place_vae() {
  vae= where=
  [ -n "${VAE_GPU_MB:-}" ] || return 0
  free=$(gpu_free_mb)
  if [ -n "$free" ] && [ "$free" -ge "$VAE_GPU_MB" ]; then
    where=" (decoding on the GPU: $free MB free)"
    vae=${VAE_GPU_FLAGS:-}
  else
    vae=--vae-on-cpu
    where=" (decoding on the CPU: ${free:-unknown} MB free of the $VAE_GPU_MB it needs)"
  fi
}

# Its test sources it for the functions alone.
[ -z "${SD_SERVE_LIB:-}" ] || return 0

trap '[ -z "$pid" ] || kill "$pid" 2>/dev/null; exit 0' TERM INT
while true; do
  if want && ready; then
    if [ -z "$pid" ] || ! kill -0 "$pid" 2>/dev/null; then
      place_vae
      echo "$NAME: loading$where"
      # $vae unquoted: its flags, or none at all.
      /sd-server "$@" $vae &
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
