#!/usr/bin/env bash
# Fetch both 27B AWQ builds for a 24 GB card, sequentially, until complete.
#
# Sequential on purpose: parallel transfers compete for a marginal link and make
# resets more frequent, not less. get-models resumes byte-exactly via curl -C -,
# so a retry costs seconds rather than restarting a 5 GB shard.
set -uo pipefail
cd "$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
export MSYS_NO_PATHCONV=1

target_mb() { case "$1" in 3.6) echo 20400 ;; 3.8) echo 21000 ;; esac; }
dir_for()   { case "$1" in 3.6) echo models/Qwen3.6-27B-AWQ-INT4 ;; 3.8) echo models/Qwen3.8-27B-AWQ-INT4 ;; esac; }

for m in 3.6 3.8; do
  echo "########## Qwen$m-27B ##########"
  d="$(dir_for "$m")"; want="$(target_mb "$m")"
  for attempt in $(seq 1 200); do
    # Completion is judged by bytes on disk, not by exit status: the script
    # warns and continues on a per-file interruption rather than failing hard.
    have=$(du -sm "$d" 2>/dev/null | cut -f1); have=${have:-0}
    if [ "$have" -ge $((want - 400)) ]; then
      echo "COMPLETE: Qwen$m-27B  (${have} MB)"
      break
    fi
    echo "--- attempt $attempt : ${have}/${want} MB ---"
    ./scripts/get-models.sh --gpu 3090 --model "$m" >/tmp/dl-$m.log 2>&1
    sleep 5
  done
done

echo "########## FINAL ##########"
du -sh models/Qwen3.6-27B-AWQ-INT4 models/Qwen3.8-27B-AWQ-INT4 2>/dev/null
