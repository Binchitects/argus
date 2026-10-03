#!/bin/sh
# GPU and CPU power caps from .env, applied every minute (a reboot or a driver
# reload resets them).
#
#   GPU_POWER_LIMIT_W  watts per GPU (nvidia-smi -pl). Empty: the card's default.
#   CPU_POWER_LIMIT_W  CPU package watts, both RAPL limits. Empty: what the
#                      firmware set, recorded on the first run.
#
# Many boards ship with Intel's limits removed; an LLM whose experts run on the
# CPU then holds the package near 95 C. Only power bounds that heat. AMD CPUs
# have no writable RAPL limit: the CPU part logs and skips.
apply() {
  if command -v nvidia-smi >/dev/null 2>&1; then
    for i in $(nvidia-smi --query-gpu=index --format=csv,noheader); do
      cur=$(nvidia-smi -i "$i" --query-gpu=power.limit --format=csv,noheader,nounits | cut -d. -f1)
      if [ -n "$GPU_POWER_LIMIT_W" ]; then want=$GPU_POWER_LIMIT_W
      elif [ -f "/state/gpu$i.applied" ]; then want=$(nvidia-smi -i "$i" --query-gpu=power.default_limit --format=csv,noheader,nounits | cut -d. -f1)
      else want=""; fi
      if [ -n "$want" ] && [ "$cur" != "$want" ]; then
        nvidia-smi -i "$i" -pl "$want" >/dev/null && echo "gpu$i: power limit $cur W -> $want W"
        if [ -n "$GPU_POWER_LIMIT_W" ]; then touch "/state/gpu$i.applied"; else rm -f "/state/gpu$i.applied"; fi
      fi
    done
  fi
  for d in /sys/class/powercap/intel-rapl:[0-9]; do
    if [ ! -w "$d/constraint_0_power_limit_uw" ]; then
      [ -n "$CPU_POWER_LIMIT_W" ] && [ ! -f /state/cpu-skip ] && echo "cpu: no writable RAPL limit at $d; skipping" && touch /state/cpu-skip
      continue
    fi
    name=$(basename "$d")
    for c in 0 1; do
      f="$d/constraint_${c}_power_limit_uw"; orig="/state/$name.c$c.orig"
      [ -f "$orig" ] || cat "$f" > "$orig"
      if [ -n "$CPU_POWER_LIMIT_W" ]; then want=$((CPU_POWER_LIMIT_W * 1000000)); else want=$(cat "$orig"); fi
      if [ "$(cat "$f")" != "$want" ]; then
        echo "$want" > "$f" && echo "cpu $name: $(( $(cat "$orig") / 1000000 )) W originally -> $(( want / 1000000 )) W"
      fi
    done
  done
}
echo "power-limits: GPU=${GPU_POWER_LIMIT_W:-default} W, CPU=${CPU_POWER_LIMIT_W:-firmware} W"
while true; do apply; sleep 60; done
