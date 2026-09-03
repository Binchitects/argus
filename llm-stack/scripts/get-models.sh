#!/usr/bin/env bash
# ==============================================================================
# Download a Qwen3.6 / Qwen3.8 27B build matched to your GPU.
#
# The quantisation format is dictated by the card's compute capability, not by
# preference:
#
#   RTX 3090  Ampere   SM 8.6   no FP8, no FP4  -> AWQ / GPTQ INT4 only
#   RTX 4090  Ada      SM 8.9   FP8 ok, no FP4  -> FP8 or AWQ
#   RTX 5090  Blackwell SM 12.0 FP4 native      -> NVFP4 (best) or FP8
#
# Handing a 3090 an NVFP4 checkpoint does not fall back gracefully - vLLM
# refuses to start.
#
#   ./scripts/get-models.sh --list
#   ./scripts/get-models.sh --gpu 3090 --model 3.6
#   ./scripts/get-models.sh --gpu 5090 --model 3.8 --mtp
#   ./scripts/get-models.sh --gpu auto --model 3.6 --apply
# ==============================================================================
set -uo pipefail
UNTIL=0
ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$ROOT"

# ---------------------------------------------------------------------------
# Git Bash (MSYS) rewrites any argument that looks like a Unix path into a
# Windows one before exec'ing a native binary. That corrupts the CONTAINER side
# of a -v flag: `/out` becomes `\Program Files\Git\out`, so the bind lands
# somewhere nonsensical, /out never exists, and every write fails with
# "No such file or directory" (curl reports it as error 23). Disabling the
# rewrite means the HOST side must already be a Windows path, which cygpath
# supplies. On Linux there is no cygpath and nothing to disable, so both are
# no-ops.
# ---------------------------------------------------------------------------
if command -v cygpath >/dev/null 2>&1; then
  export MSYS_NO_PATHCONV=1
  hostpath() { cygpath -w "$1"; }
else
  hostpath() { printf '%s' "$1"; }
fi

c_cyan=$'\033[36m'; c_green=$'\033[32m'; c_yellow=$'\033[33m'; c_red=$'\033[31m'; c_dim=$'\033[90m'; c_off=$'\033[0m'
step() { printf '%s==> %s%s\n' "$c_cyan" "$1" "$c_off"; }
ok()   { printf '    %s%s%s\n' "$c_green"  "$1" "$c_off"; }
warn() { printf '    %s%s%s\n' "$c_yellow" "$1" "$c_off"; }
die()  { printf '    %s%s%s\n' "$c_red"    "$1" "$c_off"; exit 1; }

GPU=""; MODEL=""; MTP=0; APPLY=0; LIST=0; CTX_OVERRIDE=""; PREC=""; PRESET=""; OFFLOAD=0; DRYRUN=0

while [[ $# -gt 0 ]]; do
  case "$1" in
    --gpu)     GPU="$2"; shift 2 ;;
    --model)   MODEL="$2"; shift 2 ;;
    --mtp)     MTP=1; shift ;;
    --quality)  PRESET=quality; shift ;;
    --balanced) PRESET=balanced; shift ;;
    --fp8)     PREC=fp8; shift ;;
    --bf16)    PREC=bf16; shift ;;
    --apply)   APPLY=1; shift ;;
    --until-complete) UNTIL=1; shift ;;
    --list)    LIST=1; shift ;;
    --dry-run) DRYRUN=1; shift ;;
    --context) CTX_OVERRIDE="$2"; shift 2 ;;
    -h|--help) sed -n '2,24p' "$0"; exit 0 ;;
    *) die "unknown option: $1" ;;
  esac
done

# ---------------------------------------------------------------------------
# repo | VRAM(GB) | context that fits | notes
# Sizes are the real .safetensors totals, measured from the HF API.
# ---------------------------------------------------------------------------
print_table() {
  cat <<'TABLE'
  Qwen3.8-27B is a 64-layer hybrid: only 16 layers are full-attention, so the
  KV cache costs 32 KB/token at fp8 (256K context = 8 GB, 192K = 6 GB).
  Weight sizes below are real .safetensors totals from the HuggingFace API.

  RTX 3090 / 24 GB / Ampere SM 8.6  -- INT4 only (no FP8 compute, no FP4)
    default    shawnw3i/Qwen3.8-27B-AWQ-MTP           20.0 GB   MTP, tightest fit
               cyankiwi/Qwen3.8-27B-AWQ-INT4          21.0 GB   plain AWQ
    context    32K  -- 20 GB of weights leaves only ~2 GB of cache

  RTX 5090 / 32 GB / Blackwell SM 12.0 -- NVFP4 native
    All three are NVFP4 and all three do 256K. They differ only in how many
    tensors the quantiser left above 4 bits -- that is the whole 4.6 GB spread,
    and it trades directly against KV cache.

     --quality  RedHatAI/Qwen3.8-27B-NVFP4           23.4 GB  reference quant
                256K, but needs ~2 GB offloaded to host RAM -> ~11 tok/s
                on PCIe 5.0 x16. KV pool 8.0 GB = 257K tokens aggregate.
     --balanced sakamakismile/Qwen3.8-27B-MTP-NVFP4  20.6 GB  MTP head
                256K natively, no offload, ~40 tok/s. Pool 8.8 GB = 281K.
     (default)  gittensor-model-hub/...-NVFP4-RTX5090 18.8 GB  smallest
                256K, largest pool: 10.5 GB = 335K tokens = most concurrency.
     --mtp      as --balanced but keeps the conservative 192K default.

    unsloth/Qwen3.8-27B-NVFP4 is byte-identical to the RedHatAI build.

  RTX PRO 6000 Blackwell / 96 GB / SM 12.0 -- NVFP4 and FP8 native
    default    Qwen/Qwen3.8-27B-FP8                   30.9 GB   near-lossless
     --bf16    Qwen/Qwen3.8-27B                       55.6 GB   full precision
    context    256K, with ~57 GB left over for concurrent sequences

  H200 / 141 GB / Hopper SM 9.0 -- FP8 native, NO FP4 (Blackwell only)
    default    Qwen/Qwen3.8-27B                       55.6 GB   bf16, no quant loss
     --fp8     Qwen/Qwen3.8-27B-FP8                   30.9 GB   ~2x the batch room
    context    256K, with ~74 GB left over

  Rule of thumb: quantise only as far as the card forces you to. The 3090 has
  no choice; the H200 has no reason.
TABLE
}

if [[ $LIST -eq 1 ]]; then
  echo; print_table; echo; exit 0
fi

# ---------------------------------------------------------------------------
step "GPU"
if [[ "$GPU" == "auto" || -z "$GPU" ]]; then
  name="$(nvidia-smi --query-gpu=name --format=csv,noheader 2>/dev/null | head -1)"
  [[ -z "$name" ]] && die "cannot detect a GPU; pass --gpu 3090 or --gpu 5090"
  case "$name" in
    *H200*)          GPU=h200 ;;
    *PRO*6000*)      GPU=pro6000 ;;
    *5090*)          GPU=5090 ;;
    *4090*)          GPU=4090 ;;
    *3090*)          GPU=3090 ;;
    *) die "unrecognised GPU '$name'; pass --gpu 3090|5090|pro6000|h200" ;;
  esac
  ok "detected: $name -> treating as $GPU"
else
  ok "target: RTX $GPU"
fi

[[ -n "$MODEL" ]] || die "pass --model 3.6 or --model 3.8"
case "$MODEL" in 3.6|3.8) ;; *) die "--model must be 3.6 or 3.8" ;; esac

# ---------------------------------------------------------------------------
# Select the checkpoint. Compute capability decides the format.
# ---------------------------------------------------------------------------
QUANT=""; REPO=""; SIZE=""; CTX=""; EXTRA=""; UTIL="0.92"; SEQS=64
case "$GPU" in
  3090)
    QUANT="awq"
    # 24 GB total. ~19.5 GB of weights leaves barely 4 GB for the KV cache, so
    # fp8 KV storage and a modest context are not optional here.
    if [[ "$MODEL" == "3.6" ]]; then
      if [[ $MTP -eq 1 ]]; then REPO="shawnw3i/Qwen3.6-27B-AWQ-MTP"; SIZE="19.5"
      else REPO="cyankiwi/Qwen3.6-27B-AWQ-INT4"; SIZE="20.4"; fi
    else
      if [[ $MTP -eq 1 ]]; then REPO="shawnw3i/Qwen3.8-27B-AWQ-MTP"; SIZE="19.5"
      else REPO="cyankiwi/Qwen3.8-27B-AWQ-INT4"; SIZE="21.0"; fi
    fi
    # KV maths for this architecture: 16 of 64 layers are full-attention (the
    # rest are linear-attention with constant state), 4 kv heads, head_dim 256
    # -> 32 KB/token at fp8. That arithmetic alone suggests 32K fits. It does
    # not, and these numbers are measured on a 24 GB 3090 with vLLM 0.27.1
    # rather than derived:
    #
    #   ctx 32768 @ util 0.92  -> REFUSED. Needs 1.15 GiB of KV, 0.90 GiB was
    #                             available. vLLM reported 23520 as the real
    #                             ceiling.
    #   util 0.95              -> REFUSED. 0.95 x 24 = 22.8 GiB desired, only
    #                             22.75 GiB free: the Windows desktop holds
    #                             ~1.1 GiB. gpu-memory-utilization is a
    #                             fraction of TOTAL VRAM, so whatever drives
    #                             the display must be subtracted first.
    #   ctx 24576 @ util 0.93 + --enforce-eager
    #                          -> boots. 1.8 GiB KV, 46,565 tokens, 1.95x
    #                             concurrency at full length.
    #
    # Two things make the naive sum optimistic. Qwen3.x-27B is multimodal
    # (language_model_only: false), so vLLM also loads an unquantised vision
    # tower; and CUDA graph capture costs roughly a GiB this card cannot
    # spare. --enforce-eager is therefore load-bearing here, not the WSL2
    # workaround it is elsewhere. It costs throughput (~12 tok/s rather than
    # ~19) - that is the trade a 24 GB card forces for a 27B.
    CTX=24576
    UTIL="0.93"
    SEQS=8
    EXTRA="--quantization awq --kv-cache-dtype fp8 --enforce-eager"
    ;;
  4090)
    QUANT="awq"
    if [[ "$MODEL" == "3.6" ]]; then REPO="cyankiwi/Qwen3.6-27B-AWQ-INT4"; SIZE="20.4"
    else REPO="cyankiwi/Qwen3.8-27B-AWQ-INT4"; SIZE="21.0"; fi
    # Same 24 GB and the same ~21 GB of weights as the 3090, so the same
    # ceiling applies - the earlier 65536 here was arithmetic that ignored the
    # vision tower and CUDA graphs. Ada has FP8 where Ampere does not, but that
    # buys nothing while the constraint is capacity rather than compute.
    # Derived from the 3090 measurements above, not separately measured.
    CTX=24576
    UTIL="0.93"
    SEQS=8
    EXTRA="--quantization awq --kv-cache-dtype fp8 --enforce-eager"
    ;;
  5090)
    QUANT="nvfp4"
    # Three points on one curve. All are NVFP4; they differ in how many tensors
    # the quantiser refused to take down to 4 bits, which is exactly the 4.6 GB
    # spread between them. More preserved = better output, less KV cache.
    #
    #   --quality   reference quantisation, 256K, needs ~2 GB offloaded to host
    #   --balanced  MTP build, 256K, fits natively
    #   (default)   smallest build, 256K, largest KV pool -> most concurrency
    if [[ "$MODEL" == "3.6" ]]; then
      [[ $MTP -eq 1 ]] && { REPO="sakamakismile/Qwen3.6-27B-Text-NVFP4-MTP"; SIZE="19.6"; }                        || { REPO="nvidia/Qwen3.6-27B-NVFP4"; SIZE="21.9"; }
      CTX=262144
      EXTRA="--kv-cache-dtype fp8"
    elif [[ "$PRESET" == "quality" ]]; then
      # 21.8 GiB of weights + 8 GiB of KV for 256K overruns a 32 GiB card by
      # under 2 GiB. cpu-offload-gb streams that remainder from pinned host RAM
      # every forward pass: ~11 tok/s on PCIe 5.0 x16, ~7 on PCIe 4.0. The cost
      # is per STEP, not per user, so batching amortises it.
      REPO="RedHatAI/Qwen3.8-27B-NVFP4"; SIZE="23.4"
      CTX=262144
      EXTRA="--kv-cache-dtype fp8 --cpu-offload-gb 2 --enable-prefix-caching"
      OFFLOAD=1
    elif [[ "$PRESET" == "balanced" ]]; then
      REPO="sakamakismile/Qwen3.8-27B-MTP-NVFP4"; SIZE="20.6"
      CTX=262144
      EXTRA="--kv-cache-dtype fp8 --enable-prefix-caching"
    elif [[ $MTP -eq 1 ]]; then
      # --mtp on its own keeps the older, conservative 192K default.
      REPO="sakamakismile/Qwen3.8-27B-MTP-NVFP4"; SIZE="20.6"
      CTX=196608
      EXTRA="--kv-cache-dtype fp8"
    else
      REPO="gittensor-model-hub/Qwen3.8-27B-NVFP4-RTX5090"; SIZE="18.8"
      CTX=262144
      EXTRA="--kv-cache-dtype fp8 --enable-prefix-caching"
    fi
    # The KV pool holds 257K-335K tokens depending on build, so a handful of
    # concurrent sequences is the real capacity - 64 would only inflate
    # activation memory.
    SEQS=8
    # NVFP4 checkpoints carry their quantization config; vLLM reads it from the
    # checkpoint, so no --quantization flag is needed.
    ;;
  pro6000)
    # RTX PRO 6000 Blackwell: 96 GB, SM 12.0. NVFP4 and FP8 are both native,
    # but with 96 GB there is no reason to reach for 4-bit - FP8 is
    # near-lossless and still leaves ~57 GB, which buys concurrency rather
    # than context (256K only costs 8 GB).
    if [[ "$PREC" == "bf16" ]]; then
      QUANT="bf16"; REPO="Qwen/Qwen3.8-27B"; SIZE="55.6"; EXTRA="--kv-cache-dtype fp8"
    else
      QUANT="fp8";  REPO="Qwen/Qwen3.8-27B-FP8"; SIZE="30.9"; EXTRA="--kv-cache-dtype fp8"
    fi
    CTX=262144
    SEQS=16
    ;;
  h200)
    # H200: 141 GB HBM3e, Hopper SM 9.0. Hopper has FP8 but NOT FP4 - NVFP4
    # is a Blackwell (SM 12.0) format, so those checkpoints will not run here.
    # At 141 GB the full bf16 checkpoint fits with ~74 GB to spare, so the
    # default is no quantisation at all.
    if [[ "$PREC" == "fp8" ]]; then
      QUANT="fp8";  REPO="Qwen/Qwen3.8-27B-FP8"; SIZE="30.9"; EXTRA="--kv-cache-dtype fp8"
    else
      QUANT="bf16"; REPO="Qwen/Qwen3.8-27B"; SIZE="55.6"; EXTRA="--kv-cache-dtype fp8"
    fi
    CTX=262144
    SEQS=32
    ;;
  *) die "unsupported --gpu $GPU (use 3090, 4090, 5090, pro6000 or h200)" ;;
esac

# Guard rails: a format the card cannot execute fails deep inside vLLM with an
# unhelpful kernel error, so refuse it here where the reason is obvious.
case "$GPU:$PREC" in
  3090:fp8|3090:bf16) die "RTX 3090 is Ampere SM 8.6: no FP8 compute. INT4 only." ;;
  h200:*nvfp4*)       die "H200 is Hopper SM 9.0: NVFP4 needs Blackwell SM 12.0." ;;
esac
[[ "$GPU" == "h200" && "$MTP" -eq 1 ]] && warn "no MTP build exists for FP8/bf16; ignoring --mtp"
[[ "$GPU" == "pro6000" && "$MTP" -eq 1 ]] && warn "no MTP build exists for FP8/bf16; ignoring --mtp"

if [[ -n "$PRESET" && "$GPU" != "5090" ]]; then
  warn "--$PRESET is an RTX 5090 preset; ignoring it on $GPU"
  PRESET=""
fi

[[ -n "$CTX_OVERRIDE" ]] && CTX="$CTX_OVERRIDE"

NAME="$(basename "$REPO")"
TARGET="models/$NAME"

echo
step "Selection"
ok "model      Qwen$MODEL-27B"
ok "gpu        RTX $GPU"
ok "format     $QUANT$([[ $MTP -eq 1 ]] && echo ' + MTP')"
ok "repo       $REPO"
ok "weights    ${SIZE} GB"
ok "context    $CTX  (fits alongside the weights)"

if [[ $OFFLOAD -eq 1 ]]; then
  warn ""
  warn "This preset offloads ~2 GB of weights to host RAM: the weights plus a"
  warn "256K KV cache overrun 32 GB by just under that. Expect ~11 tok/s on"
  warn "PCIe 5.0 x16 and ~7 on PCIe 4.0 - confirm the slot is gen 5 x16, and"
  warn "keep ~4 GB of host RAM free for the pinned buffers."
fi
if [[ $DRYRUN -eq 1 ]]; then
  echo
  step "vLLM settings this would apply"
  cat <<CONF
    VLLM_MODEL=/models/$(basename "$REPO")
    VLLM_SERVED_MODEL_NAME=$(basename "$REPO")
    VLLM_MAX_MODEL_LEN=$CTX
    VLLM_GPU_MEMORY_UTILIZATION=$UTIL
    VLLM_MAX_NUM_SEQS=$SEQS
    VLLM_EXTRA_ARGS=--enable-auto-tool-choice --tool-call-parser hermes $EXTRA
CONF
  echo
  echo "  (--dry-run: nothing downloaded, nothing written)"
  exit 0
fi

if [[ "$GPU" == "3090" ]]; then
  echo
  warn "24 GB is tight for a 27B. ~${SIZE} GB of weights leaves roughly 2 GB of"
  warn "KV cache, and this architecture costs 32 KB/token at fp8 - so about 32K"
  warn "context, not the 256K the model supports."
  warn ""
  warn "192K context needs 6.4 GB of KV cache and does NOT fit on a 3090."
  warn "For long context use the 5090, or a smaller model on this card."
fi

# ---------------------------------------------------------------------------
step "Download"
mkdir -p "$TARGET"

# Only one downloader per checkpoint. Two concurrent runs both open the same
# shard with `-C -`, each resuming from the offset IT saw at start, and they
# overwrite each other's bytes - the file oscillates in size and never
# completes. mkdir is atomic on every filesystem here, which makes it a safe
# lock primitive; a stale lock from a killed run is reported, not ignored.
LOCK="$TARGET/.download.lock"
if ! mkdir "$LOCK" 2>/dev/null; then
  die "a download for $NAME is already running (pid $(cat "$LOCK/pid" 2>/dev/null || echo '?')).
    If that is wrong: rm -rf $LOCK"
fi
echo $$ > "$LOCK/pid"
trap 'rm -rf "$LOCK"' EXIT INT TERM
echo "  target: $TARGET"
echo "  (re-run this command if it drops; completed files are skipped)"
echo

# ---------------------------------------------------------------------------
# Two-stage download, built around one constraint: this link drops large
# transfers repeatedly, and package installs are themselves unreliable
# (Debian mirrors 404ing, PyPI returning corrupt JSON). So nothing is installed
# at download time.
#
#   metadata : python:3.12-slim + urllib   (stdlib only, no pip)
#   weights  : curlimages/curl             (already present for health checks)
#
# curl -C - resumes at the byte offset already on disk and --retry-all-errors
# with a high count keeps going through resets, which is the behaviour that
# matters here. hf download gives up ("Max retries exceeded") on these files.
# ---------------------------------------------------------------------------
mkdir -p "$TARGET"

step "Resolving file list"
# -i is required: without it docker does not attach stdin, so `python3 -`
# reads an EMPTY script and exits 0 having done nothing.
docker run --rm -i python:3.12-slim python3 - "$REPO" <<'PYEOF' > "$TARGET/.filelist"
import json, sys, urllib.request
repo = sys.argv[1]
url = "https://huggingface.co/api/models/%s/tree/main?recursive=true" % repo
with urllib.request.urlopen(url, timeout=60) as r:
    files = json.load(r)
# .jinja matters: transformers >= 4.44 moved chat templates OUT of
# tokenizer_config.json into a standalone chat_template.jinja. Skip it and
# the model loads fine but rejects every chat request with "default chat
# template is no longer allowed".
keep = ('.safetensors', '.json', '.txt', '.model', '.jinja')
for f in files:
    p = f.get("path", "")
    if p.endswith(keep) and not p.startswith("original/"):
        print("%s	%s" % (p, f.get("size", 0)))
PYEOF
count=$(wc -l < "$TARGET/.filelist" 2>/dev/null || echo 0)
[[ "$count" -gt 0 ]] || die "could not list files for $REPO"
ok "$count file(s) to fetch"

# Everything a file needs to resume is already on disk, so "continue" is just
# "run the loop again". The inner per-file loop gives up after 5 attempts that
# add no bytes, which is right for a dead link and wrong for a flaky one -- on
# an unreliable connection that is a pause, not a failure. --until-complete
# keeps re-entering the pass until every file matches the size the API
# reported, with a short backoff so a genuinely offline link is not hammered.
download_pass() {
step "Downloading (resumable; re-run any time to continue)"
TOKEN="$(grep -E '^HF_TOKEN=' .env 2>/dev/null | cut -d= -f2- | tr -d '[:space:]')"
while IFS=$'	' read -r path size; do
  [[ -z "$path" ]] && continue
  local_size=$(stat -c %s "$TARGET/$path" 2>/dev/null || echo 0)
  # OVERSIZE means corrupt, not finished. Some proxies answer a ranged
  # request with 200 and the whole file, and curl -C - appends it to the
  # partial already on disk -- so the file grows past its true size with
  # duplicated bytes interleaved. Measured here: every shard of a 19.6 GB
  # download ended 5-20% too large, and neither the first nor the last
  # `size` bytes hashed correctly, so there was nothing to truncate back to.
  # `-ge` treated that as complete and reported 15/15 on unusable weights.
  if [[ "$size" -gt 0 && "$local_size" -gt "$size" ]]; then
    warn "$path is $((local_size/1048576)) MB but should be $((size/1048576)) MB - refetching"
    rm -f "$TARGET/$path"
    local_size=0
  fi
  if [[ "$local_size" -eq "$size" && "$size" -gt 0 ]]; then
    printf '    %-46s %s
' "$path" "already complete"
    continue
  fi
  printf '    %-46s %s / %s MB
' "$path" "$((local_size/1048576))" "$((size/1048576))"
  mkdir -p "$TARGET/$(dirname "$path")"
  AUTH=()
  [[ -n "$TOKEN" ]] && AUTH=(-H "Authorization: Bearer $TOKEN")
  # -C - resume, --retry-all-errors covers the connection resets seen here.
  # curl's own --retry re-requests from the byte offset captured when the
  # PROCESS started, so on a link that dies mid-transfer it rewrites the same
  # bytes forever and the file never grows. Re-invoking curl is what actually
  # makes progress: a fresh process re-evaluates `-C -` against the file that
  # is now longer. The stall counter gives up only after several consecutive
  # attempts add nothing, so a slow link is not mistaken for a dead one.
  # --speed-limit/--speed-time are what make that loop reachable: without
  # them a connection that dies mid-stream (common through the Docker
  # Desktop proxy) leaves curl blocked forever on a socket that will never
  # deliver another byte. Aborting under 10 KB/s for 30s hands control back
  # to the loop, which resumes from the bytes already on disk.
  #
  # --retry 0 is deliberate and load-bearing. `-C -` resolves the resume
  # offset ONCE, when the process starts; an internal retry then rewrites
  # from that stale offset and TRUNCATES everything fetched since - a shard
  # at 1574 MB dropped back to 867 MB this way. Retry and resume must not be
  # nested: the retry has to be the process restart, which is this loop.
  stall=0
  while :; do
    before=$(stat -c %s "$TARGET/$path" 2>/dev/null || echo 0)
    docker run --rm -v "$(hostpath "$ROOT/$TARGET"):/out" curlimages/curl:8.11.1       -sSL -C - --retry 0       --connect-timeout 30 --speed-limit 10240 --speed-time 30 "${AUTH[@]}"       -o "/out/$path" "https://huggingface.co/$REPO/resolve/main/$path" && break
    after=$(stat -c %s "$TARGET/$path" 2>/dev/null || echo 0)
    # Caught the instant it happens, so one bad response costs one file
    # rather than silently poisoning the whole download.
    if [[ "$size" -gt 0 && "$after" -gt "$size" ]]; then
      warn "  $path overshot ($((after/1048576)) > $((size/1048576)) MB) - the server ignored Range; starting it over"
      rm -f "$TARGET/$path"
      stall=0
      continue
    fi
    [[ "$size" -gt 0 && "$after" -eq "$size" ]] && break
    if [[ "$after" -gt "$before" ]]; then
      stall=0
      printf '      resumed, now %s / %s MB
' "$((after/1048576))" "$((size/1048576))"
    else
      stall=$((stall+1))
      if [[ $stall -ge 5 ]]; then
        warn "  $path stalled at $((after/1048576)) MB - re-run to resume"
        break
      fi
      sleep 5
    fi
  done
done < "$TARGET/.filelist"
}

# Files still short of the size the API reported.
missing_count() {
  local n=0 path size have
  while IFS=$'	' read -r path size; do
    [[ -z "$path" ]] && continue
    have=$(stat -c %s "$TARGET/$path" 2>/dev/null || echo 0)
    [[ "$size" -gt 0 && "$have" -ne "$size" ]] && n=$((n+1))
  done < "$TARGET/.filelist"
  echo "$n"
}

download_pass
if [[ $UNTIL -eq 1 ]]; then
  pass=1
  while :; do
    left="$(missing_count)"
    [[ "$left" -eq 0 ]] && { ok "all files complete after $pass pass(es)"; break; }
    pass=$((pass+1))
    if [[ $pass -gt 200 ]]; then
      warn "$left file(s) still incomplete after 200 passes - giving up"
      break
    fi
    warn "$left file(s) incomplete - pass $pass in 30s (Ctrl-C is safe; bytes are kept)"
    sleep 30
    download_pass
  done
fi

if [[ ! -f "$TARGET/config.json" ]]; then
  die "no config.json in $TARGET - download incomplete, re-run to resume"
fi
shards=$(ls "$TARGET"/*.safetensors 2>/dev/null | wc -l)
[[ "$shards" -gt 0 ]] || die "no .safetensors in $TARGET - re-run to resume"
ok "$shards shard(s), $(du -sh "$TARGET" | cut -f1) on disk"

# ---------------------------------------------------------------------------
echo
step "vLLM settings for this checkpoint"
cat <<CONF
    VLLM_MODEL=/models/$NAME
    VLLM_SERVED_MODEL_NAME=default
    VLLM_MAX_MODEL_LEN=$CTX
    VLLM_GPU_MEMORY_UTILIZATION=$UTIL
    VLLM_MAX_NUM_SEQS=$SEQS
    VLLM_EXTRA_ARGS=--enable-auto-tool-choice --tool-call-parser hermes $EXTRA
CONF

if [[ $APPLY -eq 1 ]]; then
  echo
  step "Applying to .env"
  set_env() {
    if grep -qE "^$1=" .env; then sed -i "s|^$1=.*|$1=$2|" .env
    else printf '%s=%s\n' "$1" "$2" >> .env; fi
    printf '    %-30s %s\n' "$1" "$2"
  }
  set_env VLLM_MODEL "/models/$NAME"
  set_env VLLM_SERVED_MODEL_NAME "$NAME"
  # Keep the gateway advertising the same name (LiteLLM has no ${VAR} support).
  if [[ -f config/litellm/config.yaml ]]; then
    sed -i "0,/^  - model_name: .*/s||  - model_name: $NAME|" config/litellm/config.yaml
    sed -i "s|^      model: openai/.*|      model: openai/$NAME|" config/litellm/config.yaml
    printf '    %-30s %s
' "gateway config" "$NAME"
  fi
  set_env VLLM_MAX_MODEL_LEN "$CTX"
  set_env VLLM_GPU_MEMORY_UTILIZATION "$UTIL"
  set_env VLLM_MAX_NUM_SEQS "$SEQS"
  set_env VLLM_EXTRA_ARGS "--enable-auto-tool-choice --tool-call-parser hermes $EXTRA"
  echo
  ok "restart:  docker compose up -d --force-recreate vllm litellm"
else
  echo
  echo "  Apply automatically with --apply, or:"
  echo "    ./scripts/switch-model.sh /models/$NAME --max-model-len $CTX \\"
  echo "        --extra-args \"--enable-auto-tool-choice --tool-call-parser hermes $EXTRA\""
fi
echo
