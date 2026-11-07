#!/bin/sh
# FP4 against FP8 (and BF16) on the H200 NVL host, each copy of a model in a throwaway vLLM container on the same GPUs,
# with fp4-bench.py beside this: memory, tokens a second with 1, 8, 32 and 64 people at once, the first token, and the
# small quality check. Nothing is fetched: it runs the snapshots the library has, by their repositories, and skips the
# ones it does not have (carry them in as docs/deployment.md says, hf/OWNER/REPO@REVISION).
#
#   sh fp4-h200.sh LIBRARY [GPUS] [OUT]      # e.g. sh fp4-h200.sh /srv/arena/models 1,2 ./fp4-results
#
# GPUS are the ones it may take (default 1,2: two H200 NVL, the third left to the stack's chat model). Stop the stack's
# engines on them first (docker stop vllm sglang ..., or unload their models in Admin -> Models), and start them again
# after. The results are a JSON file a set and their table on the screen (python3 fp4-bench.py table FILE again).
#
# The sets, from the largest:
#   flash   Qwen3.8-Flash-Next: FP8 (186 GB) and NVIDIA's NVFP4 (133 GB), both on two GPUs (tensor parallel 2)
#   dense   Qwen3.8-27B: BF16, FP8 and NVFP4, each on one GPU
#   oss     gpt-oss-120b: its own MXFP4 (65 GB) on one GPU (its BF16 would take two)
#   small   Qwen3-0.6B: BF16, FP8, NVFP4 and BF16 quantised to MXFP4 as it loads, on one GPU (as measured on the 3090)
set -eu
lib=${1:?usage: fp4-h200.sh LIBRARY [GPUS] [OUT]}
gpus=${2:-1,2}
out=${3:-.}
here=$(cd "$(dirname "$0")" && pwd)
image=${FP4_IMAGE:-arena-vllm}
first=${gpus%%,*}
mkdir -p "$out"

# The newest revision of a repository in the library, as a path in it; empty: not there.
snap() { for d in "$lib"/hf/"$1"@*; do [ -f "$d/config.json" ] && echo "${d#"$lib"/}"; done | tail -n 1; }

# run SET GPUS BENCH-ARGS SERVE-ARGS NAME=REPO...: the snapshots of a set the library has, one after another.
run() {
  set_=$1 on=$2 bench=$3 serve=$4
  shift 4
  models=""
  for m in "$@"; do
    path=$(snap "${m#*=}")
    if [ -z "$path" ]; then echo "$set_: ${m#*=} is not in $lib/hf: ${m%%=*} left out"; continue; fi
    models="$models --model ${m%%=*}=$path"
  done
  [ -n "$models" ] || { echo "$set_: none of its snapshots is here"; return 0; }
  echo "== $set_ on GPUs $on"
  # shellcheck disable=SC2086
  python3 "$here/fp4-bench.py" run --image "$image" --library "$lib" --gpus "$on" --levels 1,8,32,64 --out "$out/fp4-$set_.json" $bench $models \
    ${serve:+--extra $serve} || echo "$set_: some did not run (the JSON file says why)"
}

run flash "$gpus" "" "" fp8=Qwen/Qwen3.8-Flash-Next-FP8 nvfp4=nvidia/Qwen3.8-Flash-Next-NVFP4
run dense "$first" "" "" bf16=Qwen/Qwen3.8-27B fp8=Qwen/Qwen3.8-27B-FP8 nvfp4=nvidia/Qwen3.8-27B-NVFP4
# gpt-oss reasons before it answers: room for that in the quality check.
run oss "$first" "--reasoning 1024" "" mxfp4=openai/gpt-oss-120b
run small "$first" "" "" bf16=Qwen/Qwen3-0.6B fp8=Qwen/Qwen3-0.6B-FP8 nvfp4=mgoin/Qwen3-0.6B-NVFP4
# BF16 weights quantised to MXFP4 by vLLM as they load (its online MXFP4).
run small-mxfp4 "$first" "" "--quantization mxfp4" mxfp4=Qwen/Qwen3-0.6B
