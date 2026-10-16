#!/bin/sh
# Bring-up check of a GPU server for the engines (docs/deployment.md, "Multi-GPU server"): what
# vLLM and llama.cpp need of the host, before any model is placed on it. Read only: it changes
# nothing, needs no root, and pulls nothing.
#
#   ./scripts/gpu-check.sh                   NVIDIA (nvidia-smi) or AMD (rocm-smi), whichever is here
#   ./scripts/gpu-check.sh --nccl PATH       also NCCL's all_reduce_perf (nccl-tests, built beforehand)
#
# Each line is OK, WARN (works, slower or less safe) or FAIL (an engine will not run as planned);
# the exit code is 1 when anything failed. What it checks:
#   the driver          NVIDIA >= R580 (CUDA 13: vLLM 0.31's images; sm_120 needs >= R570)
#   the GPUs            their count, names and memory; ECC and MIG state
#   peer to peer        each pair's P2P (nvidia-smi topo -p2p r); a pair through the CPU socket (SYS) is slow for TP
#   all-reduce          NCCL's bus bandwidth across all GPUs (with --nccl)
#   memlock and shm     unlimited locked memory and a /dev/shm of 16 GB or more (vLLM's tensor parallel ranks)
#   power caps          each GPU's limit against its default (power-limits.sh may lower it)
set -u
NCCL=""
while [ $# -gt 0 ]; do
  case "$1" in
    --nccl) NCCL="$2"; shift 2 ;;
    -h|--help) sed -n '2,20p' "$0"; exit 0 ;;
    *) echo "gpu-check: unknown option $1" >&2; exit 2 ;;
  esac
done
failed=0
ok() { echo "OK    $*"; }
warn() { echo "WARN  $*"; }
fail() { echo "FAIL  $*"; failed=1; }

if command -v nvidia-smi > /dev/null 2>&1 && nvidia-smi -L > /dev/null 2>&1; then
  driver=$(nvidia-smi --query-gpu=driver_version --format=csv,noheader | head -n 1 | tr -d ' ')
  major=${driver%%.*}
  if [ "${major:-0}" -ge 580 ] 2>/dev/null; then ok "driver $driver (CUDA 13 images run)"
  elif [ "${major:-0}" -ge 570 ] 2>/dev/null; then warn "driver $driver: sm_120 runs, but vLLM's CUDA 13 images need R580 or newer (use a -cu129 image)"
  else fail "driver $driver: too old for these GPUs' engines (R570 at least, R580 for CUDA 13)"; fi

  count=$(nvidia-smi -L | grep -c '^GPU ')
  ok "$count GPU(s):"
  nvidia-smi --query-gpu=index,name,memory.total,ecc.mode.current,mig.mode.current --format=csv,noheader | while IFS=, read -r i name mem ecc mig; do
    echo "        GPU$i ·$name ·$mem · ECC$ecc · MIG$mig"
  done
  nvidia-smi --query-gpu=index,mig.mode.current --format=csv,noheader | grep -qi "enabled" \
    && warn "MIG is on for some GPU: each instance is a GPU of its own size to the engines (vLLM's tensor parallel needs whole GPUs)"

  if [ "$count" -gt 1 ]; then
    p2p=$(nvidia-smi topo -p2p r 2>/dev/null)
    if [ -n "$p2p" ]; then
      bad=$(printf '%s\n' "$p2p" | grep -E '^ *GPU[0-9]' | grep -c -E 'NS|CNS|TNS')
      if [ "$bad" -eq 0 ]; then ok "peer-to-peer reads between every pair of GPUs"
      else fail "peer-to-peer is not supported between some GPUs: tensor parallel falls back through host memory (set NCCL_P2P_DISABLE=1 and --disable-custom-all-reduce, or fix the PCIe topology: one root complex or switch, iommu=pt, ACS off)"; fi
    else
      warn "nvidia-smi cannot say the peer-to-peer matrix"
    fi
    topo=$(nvidia-smi topo -m 2>/dev/null)
    if printf '%s\n' "$topo" | grep -E '^GPU[0-9]' | grep -q ' SYS'; then
      warn "some GPUs connect through the CPU sockets' interconnect (SYS): keep a tensor parallel group on GPUs of one socket"
    else
      ok "every GPU pair is under one PCIe root or switch (no SYS link)"
    fi
  fi

  nvidia-smi --query-gpu=index,power.limit,power.default_limit --format=csv,noheader,nounits | while IFS=, read -r i limit default; do
    l=${limit%%.*}; d=${default%%.*}
    if [ "${l:-0}" -lt "$(( ${d:-0} * 6 / 10 ))" ] 2>/dev/null; then echo "WARN  GPU$i is capped at$limit W of its$default W: decode slows when all of a model is on it"
    else echo "OK    GPU$i power limit$limit W (default$default W)"; fi
  done
elif command -v rocm-smi > /dev/null 2>&1; then
  ok "AMD GPUs (rocm-smi):"
  rocm-smi --showproductname --showmeminfo vram 2>/dev/null | grep -E 'Card series|Total Memory' | sed 's/^/        /'
  rocm-smi --showdriverversion 2>/dev/null | grep -i version | sed 's/^/        /'
  if rocm-smi --showtopotype 2>/dev/null | grep -q XGMI; then ok "GPUs linked by Infinity Fabric (XGMI)"
  else warn "no XGMI links between the GPUs: tensor parallel goes over PCIe"; fi
else
  warn "no GPU tool (nvidia-smi, rocm-smi): the engines run on the CPU here (LLAMACPP_BACKEND=cpu)"
fi

if [ -n "$NCCL" ]; then
  if [ -x "$NCCL" ]; then
    gpus=$(nvidia-smi -L 2>/dev/null | grep -c '^GPU ')
    bus=$("$NCCL" -b 256M -e 1G -f 2 -g "${gpus:-1}" 2>/dev/null | awk '/Avg bus bandwidth/ { print $NF }')
    if [ -n "$bus" ]; then ok "NCCL all-reduce: $bus GB/s average bus bandwidth over ${gpus:-1} GPUs (PCIe Gen5 P2P: about 40-50)"
    else fail "NCCL all-reduce did not run: see $NCCL's own output"; fi
  else
    fail "$NCCL is not an executable all_reduce_perf"
  fi
fi

lock=$(ulimit -l 2>/dev/null)
if [ "$lock" = unlimited ]; then ok "locked memory: unlimited"
else warn "locked memory is $lock KB here: compose gives the engines memlock -1 (rootless Podman cannot)"; fi

shm_kb=$(df -k /dev/shm 2>/dev/null | awk 'NR == 2 { print $2 }')
if [ -n "$shm_kb" ] && [ "$shm_kb" -ge $((16 * 1024 * 1024)) ]; then ok "/dev/shm: $((shm_kb / 1024 / 1024)) GB"
else warn "/dev/shm on the host is $(( ${shm_kb:-0} / 1024 / 1024 )) GB: the vllm service sets its own (VLLM_SHM, 16gb)"; fi

[ "$failed" -eq 0 ] && echo "gpu-check: nothing failed" || echo "gpu-check: something failed (above)"
exit "$failed"
