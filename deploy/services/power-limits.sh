#!/bin/sh
# Power caps and a temperature policy for the GPUs and the CPU, from .env, so that long model runs
# never cook the machine. Checked every THERMAL_CHECK_S seconds (a reboot or a driver reload resets
# a cap: it is put back at the next check).
#
#   GPU_POWER_LIMIT_W     watts per GPU (nvidia-smi -pl). Empty: the card's default. The ceiling the
#                         policy comes back up to.
#   CPU_POWER_LIMIT_W     CPU package watts, both RAPL limits (Intel). Empty: what the firmware set,
#                         recorded on the first run. The ceiling the policy comes back up to.
#   THERMAL_POLICY        on (default) or off: off keeps only the caps above.
#   GPU_TEMP_LIMIT_C      83: at or above it, the GPU's power cap steps down; GPU_TEMP_CRITICAL_C (90):
#                         straight to the card's lowest.
#   CPU_TEMP_LIMIT_C      90: at or above it, the CPU's RAPL limit (Intel), else its highest frequency
#                         (cpufreq, any CPU), steps down; CPU_TEMP_CRITICAL_C (95): straight to the floor.
#   THERMAL_STEP_PCT      10: each step, a share of the ceiling.
#   THERMAL_FLOOR_PCT     50: the lowest a step goes (below a critical temperature), a share of the ceiling.
#   THERMAL_HYSTERESIS_C  5: back up a step each check once that far below the limit.
#   THERMAL_CHECK_S       10.
#
# Many boards ship with Intel's limits removed; an LLM whose experts run on the CPU then holds the
# package near 95 C. Only power bounds that heat.
# POWER_SYS, POWER_STATE and POWER_ONCE (one check, then exit) are for its tests.
SYS=${POWER_SYS:-/sys} STATE=${POWER_STATE:-/state}
POLICY=${THERMAL_POLICY:-on}
GPU_LIMIT=${GPU_TEMP_LIMIT_C:-83} GPU_CRIT=${GPU_TEMP_CRITICAL_C:-90}
CPU_LIMIT=${CPU_TEMP_LIMIT_C:-90} CPU_CRIT=${CPU_TEMP_CRITICAL_C:-95}
STEP=${THERMAL_STEP_PCT:-10} FLOOR=${THERMAL_FLOOR_PCT:-50} HYST=${THERMAL_HYSTERESIS_C:-5} EVERY=${THERMAL_CHECK_S:-10}
policy_on() { [ "$POLICY" != off ]; }

# The next target: down a step at the limit, to the floor (or the lowest) when critical, up a step
# once cool, never above the ceiling nor below the floor.
next_target() {   # next_target TEMP LIMIT CRIT TARGET CEILING FLOOR LOWEST STEP
  awk -v t="$1" -v lim="$2" -v crit="$3" -v cur="$4" -v ceil="$5" -v floor="$6" -v low="$7" -v step="$8" -v hyst="$HYST" 'BEGIN {
    if (t >= crit) n = low
    else if (t >= lim) { n = cur - step; if (n < floor) n = floor }
    else if (t <= lim - hyst) { n = cur + step; if (n > ceil) n = ceil }
    else n = cur
    if (n > ceil) n = ceil
    printf "%d", n
  }'
}
# A cap differs only by a watt or more (the kernel and the driver read back what was written rounded).
differs() { awk -v a="$1" -v b="$2" -v tol="$3" 'BEGIN { d = a - b; if (d < 0) d = -d; exit !(d >= tol) }'; }

gpu() {
  command -v nvidia-smi >/dev/null 2>&1 || return 0
  nvidia-smi --query-gpu=index,temperature.gpu,power.limit,power.default_limit,power.min_limit --format=csv,noheader,nounits 2>/dev/null \
  | while IFS=', ' read -r i temp cur def min; do
    [ -n "$def" ] || continue
    cur=${cur%.*} def=${def%.*} min=${min%.*} temp=${temp%.*}
    ceil=${GPU_POWER_LIMIT_W:-$def}
    if policy_on && [ -n "$temp" ] && [ "$temp" -eq "$temp" ] 2>/dev/null; then
      target=$(cat "$STATE/gpu$i.target" 2>/dev/null || echo "$ceil")
      floor=$((ceil * FLOOR / 100)); [ "$floor" -lt "$min" ] && floor=$min
      want=$(next_target "$temp" "$GPU_LIMIT" "$GPU_CRIT" "$target" "$ceil" "$floor" "$min" $((def * STEP / 100)))
      echo "$want" > "$STATE/gpu$i.target"
      why="$temp C"
    elif [ -n "$GPU_POWER_LIMIT_W" ] || [ -f "$STATE/gpu$i.applied" ]; then want=$ceil why="the cap"
    else continue; fi
    if differs "$cur" "$want" 1; then
      if nvidia-smi -i "$i" -pl "$want" >/dev/null 2>&1; then
        echo "gpu$i: $why (limit $GPU_LIMIT C, critical $GPU_CRIT C): power limit $cur W -> $want W"
      elif [ ! -f "$STATE/gpu$i.refused" ]; then
        echo "gpu$i: the driver refused a power limit of $want W (not every card lets one be set)"; touch "$STATE/gpu$i.refused"
      fi
    fi
    if [ "$want" != "$def" ]; then touch "$STATE/gpu$i.applied"; else rm -f "$STATE/gpu$i.applied"; fi
  done
}

# The CPU's package temperature: the hottest of coretemp's, k10temp's or zenpower's sensors, else the
# x86 package thermal zone, in whole degrees.
cpu_temp() {
  t=""
  for h in $SYS/class/hwmon/hwmon*; do
    case "$(cat "$h/name" 2>/dev/null)" in coretemp|k10temp|zenpower) ;; *) continue ;; esac
    for f in "$h"/temp*_input; do [ -r "$f" ] && t="$t $(cat "$f")"; done
  done
  if [ -z "$t" ]; then
    for z in $SYS/class/thermal/thermal_zone*; do
      [ "$(cat "$z/type" 2>/dev/null)" = x86_pkg_temp ] && t="$t $(cat "$z/temp")"
    done
  fi
  [ -n "$t" ] && printf '%s\n' $t | awk 'BEGIN { m = -1 } $1 > m { m = $1 } END { if (m >= 0) printf "%d", m / 1000 }'
}

cpu() {
  temp=$(cpu_temp)
  rapl=0
  for d in $SYS/class/powercap/intel-rapl:[0-9]; do [ -w "$d/constraint_0_power_limit_uw" ] && rapl=1; done
  if [ $rapl -eq 1 ]; then
    for d in $SYS/class/powercap/intel-rapl:[0-9]; do
      [ -w "$d/constraint_0_power_limit_uw" ] || continue
      name=$(basename "$d")
      for c in 0 1; do
        f="$d/constraint_${c}_power_limit_uw"; orig="$STATE/$name.c$c.orig"
        [ -f "$orig" ] || cat "$f" > "$orig"
        if [ -n "$CPU_POWER_LIMIT_W" ]; then ceil=$((CPU_POWER_LIMIT_W * 1000000)); else ceil=$(cat "$orig"); fi
        want=$ceil
        if policy_on && [ -n "$temp" ]; then
          target=$(cat "$STATE/$name.c$c.target" 2>/dev/null || echo "$ceil")
          floor=$((ceil / 100 * FLOOR))
          want=$(next_target "$temp" "$CPU_LIMIT" "$CPU_CRIT" "$target" "$ceil" "$floor" "$floor" $((ceil / 100 * STEP)))
          echo "$want" > "$STATE/$name.c$c.target"
        fi
        cur=$(cat "$f")
        if differs "$cur" "$want" 1000000; then
          echo "$want" > "$f" && echo "cpu $name: ${temp:-?} C (limit $CPU_LIMIT C, critical $CPU_CRIT C): limit $((cur / 1000000)) W -> $((want / 1000000)) W (the firmware's: $(( $(cat "$orig") / 1000000 )) W)"
        fi
      done
    done
    return 0
  fi
  [ -n "$CPU_POWER_LIMIT_W" ] && [ ! -f $STATE/cpu-skip ] && echo "cpu: no writable RAPL limit (not an Intel CPU, or the firmware locks it): CPU_POWER_LIMIT_W does nothing here; the temperature policy uses the CPU's frequency" && touch $STATE/cpu-skip
  # Any CPU: its highest frequency (cpufreq), every core alike.
  policy_on && [ -n "$temp" ] || return 0
  top=$SYS/devices/system/cpu/cpu0/cpufreq
  [ -w "$top/scaling_max_freq" ] || { [ -f $STATE/cpufreq-skip ] || { echo "cpu: no writable cpufreq: the CPU's temperature is only watched"; touch $STATE/cpufreq-skip; }; return 0; }
  ceil=$(cat "$top/cpuinfo_max_freq") low=$(cat "$top/cpuinfo_min_freq")
  floor=$((ceil / 100 * FLOOR)); [ "$floor" -lt "$low" ] && floor=$low
  target=$(cat $STATE/cpufreq.target 2>/dev/null || echo "$ceil")
  want=$(next_target "$temp" "$CPU_LIMIT" "$CPU_CRIT" "$target" "$ceil" "$floor" "$floor" $((ceil / 100 * STEP)))
  echo "$want" > $STATE/cpufreq.target
  cur=$(cat "$top/scaling_max_freq")
  if differs "$cur" "$want" 1000; then
    for f in $SYS/devices/system/cpu/cpu[0-9]*/cpufreq/scaling_max_freq; do echo "$want" > "$f" 2>/dev/null; done
    echo "cpu: $temp C (limit $CPU_LIMIT C, critical $CPU_CRIT C): highest frequency $((cur / 1000)) MHz -> $((want / 1000)) MHz"
  fi
}

echo "power-limits: GPU cap ${GPU_POWER_LIMIT_W:-the card's own} W, CPU cap ${CPU_POWER_LIMIT_W:-the firmware's} W; temperature policy $POLICY (GPU $GPU_LIMIT/$GPU_CRIT C, CPU $CPU_LIMIT/$CPU_CRIT C, steps of $STEP%, floor $FLOOR%, every $EVERY s)"
while true; do gpu; cpu; [ -n "${POWER_ONCE:-}" ] && exit 0; sleep "$EVERY"; done
