# Models: choosing a build for your GPU

The quantisation format is decided by your card's **compute capability**, not by
preference. Getting this wrong does not degrade gracefully — vLLM refuses to
start.

| Card | Arch | SM | VRAM | FP8 | FP4 (NVFP4) | Use |
|---|---|---|---|---|---|---|
| RTX 3090 | Ampere | 8.6 | 24 GB | ✗ | ✗ | **AWQ / GPTQ INT4 only** |
| RTX 4090 | Ada | 8.9 | 24 GB | ✓ | ✗ | FP8 or AWQ |
| RTX 5090 | Blackwell | 12.0 | 32 GB | ✓ | ✓ | **NVFP4** |
| RTX PRO 6000 | Blackwell | 12.0 | 96 GB | ✓ | ✓ | **FP8** (bf16 also fits) |
| H200 | Hopper | 9.0 | 141 GB | ✓ | ✗ | **bf16** — no need to quantise |

Two directions of failure, not one. **NVFP4 will not run on a 3090 or an H200**
— FP4 tensor cores are Blackwell-only and there is no software fallback. But
the opposite mistake is just as common: quantising a 27B to 4-bit on a 141 GB
H200 throws away quality to save memory that was never scarce.

**Quantise only as far as the card forces you to.**

**An NVFP4 checkpoint will not run on a 3090.** FP4 tensor cores are a Blackwell
feature; there is no software fallback. The same is true of FP8 on Ampere.

---

## The script

```bash
./scripts/get-models.sh --list
```

```bash
./scripts/get-models.sh --gpu 3090 --model 3.6
```

```powershell
.\scripts\get-models.ps1 --gpu 5090 --model 3.8 --mtp --apply
```

| Flag | Meaning |
|---|---|
| `--gpu` | `3090`, `4090`, `5090`, `pro6000`, `h200`, or `auto` to detect from `nvidia-smi` |
| `--model` | `3.6` or `3.8` |
| `--mtp` | Prefer a Multi-Token Prediction build (3090 / 5090 only) |
| `--quality` | 5090: reference NVFP4 at 256K, via ~2 GB CPU offload |
| `--balanced` | 5090: MTP NVFP4 at 256K, no offload |
| `--dry-run` | Print the selection and stop — download nothing |
| `--fp8` | H200: use FP8 instead of bf16 — half the weights, ~2x batch room |
| `--bf16` | RTX PRO 6000: use full precision instead of FP8 |
| `--apply` | Write the settings straight into `.env` |
| `--context` | Override the context window (see below) |

It picks the checkpoint, downloads it resumably into `models/`, and prints the
matching vLLM settings.

---

## RTX 3090 — 24 GB, Ampere

Sizes below are real `.safetensors` totals from the HuggingFace API.

| Model | Repo | Weights |
|---|---|---|
| 3.6 | `shawnw3i/Qwen3.6-27B-AWQ-MTP` | 19.5 GB |
| 3.6 | `cyankiwi/Qwen3.6-27B-AWQ-INT4` | 20.4 GB |
| 3.8 | `shawnw3i/Qwen3.8-27B-AWQ-MTP` | 19.5 GB |
| 3.8 | `cyankiwi/Qwen3.8-27B-AWQ-INT4` | 21.0 GB |

> **A 27B on 24 GB is genuinely tight.** ~20 GB of weights leaves roughly 2 GB
> for the KV cache and activations, which is about **32K context** — not the
> 256K the model supports. The script sets `--kv-cache-dtype fp8` to halve KV
> cost and caps context at 32768. If the engine fails at startup with *"No
> available memory for the cache blocks"*, drop `VLLM_MAX_MODEL_LEN` further.
> The 21.9 GB `QuantTrio` build is listed for completeness but does not leave a
> usable cache.
>
> **192K context does not fit on this card** — see the arithmetic below. If you
> want long context or real concurrency on a 3090, a 14B is the comfortable
> choice; the 27B is a squeeze.

Applied settings:

```
VLLM_MAX_MODEL_LEN=32768
VLLM_GPU_MEMORY_UTILIZATION=0.92
VLLM_EXTRA_ARGS=--enable-auto-tool-choice --tool-call-parser qwen3_xml --reasoning-parser qwen3 --quantization awq --kv-cache-dtype fp8
```

---

## RTX 5090 — 32 GB, Blackwell

| Model | Repo | Weights | Note |
|---|---|---|---|
| 3.6 | `nvidia/Qwen3.6-27B-NVFP4` | 21.9 GB | **official NVIDIA build** |
| 3.6 | `sakamakismile/Qwen3.6-27B-Text-NVFP4-MTP` | 19.6 GB | MTP |
| 3.8 | `gittensor-model-hub/Qwen3.8-27B-NVFP4-RTX5090` | 18.8 GB | smallest |
| 3.8 | `sakamakismile/Qwen3.8-27B-MTP-NVFP4` | 20.6 GB | MTP |
| 3.8 | `unsloth/Qwen3.8-27B-NVFP4` | 23.4 GB | largest |

32 GB with ~19–22 GB of weights leaves 10–13 GB for the KV cache. At 32 KB per
token (fp8) that is **192K context**, which is the default here — and 256K fits
with the smaller builds. This is the card the 27B is sized for.

NVFP4 checkpoints carry their quantisation config internally, so **no
`--quantization` flag is needed** — vLLM reads it from the checkpoint. Passing
one anyway can conflict.

Prefer `nvidia/Qwen3.6-27B-NVFP4` when you want a vetted build: it is NVIDIA's
own, and the community NVFP4 repos vary in quality and in how faithfully they
carry the quantisation metadata.

### Choosing among the three NVFP4 builds

All three run at 256K. They differ only in how many tensors the quantiser left
above 4 bits — that is the entire 4.6 GB spread — and that trades directly
against how much KV cache is left.

| Preset | Build | Weights | KV pool | Aggregate | tok/s |
|---|---|---|---|---|---|
| `--quality` | `RedHatAI/Qwen3.8-27B-NVFP4` | 21.8 GiB | 8.0 GiB | 257K tok | ~11 |
| `--balanced` | `sakamakismile/…-MTP-NVFP4` | 19.2 GiB | 8.8 GiB | 281K tok | ~40 |
| *(default)* | `gittensor-model-hub/…-RTX5090` | 17.5 GiB | 10.5 GiB | 335K tok | ~43 |

`unsloth/Qwen3.8-27B-NVFP4` is byte-identical to the RedHatAI build — the same
checkpoint mirrored, not a second opinion.

**`--quality` overruns the card by just under 2 GB** and covers the gap with
`--cpu-offload-gb 2`, streaming that remainder from pinned host RAM on every
forward pass. Two consequences worth knowing before choosing it:

- **PCIe generation decides the speed.** ~11 tok/s on gen 5 x16, ~7 on gen 4.
  Confirm the slot is electrically x16 and gen 5; a chipset-fed x4 slot is
  disastrous here. Keep ~4 GB of host RAM free for the pinned buffers.
- **The offload cost is per forward step, not per user**, so batching amortises
  it exactly like the weights. Five concurrent users see ~11 tok/s each,
  ~55 tok/s aggregate — the single-stream figure overstates the penalty.

Quality also costs *capacity*, not just speed: `--quality` leaves 257K tokens of
pool against the default build's 335K. One user at full 256K, or five at ~51K.

**FP8 is not reachable on 32 GB.** It needs 8.8 GB offloaded, which lands at
~3 tok/s — a 500-token reply takes three minutes.

---

## RTX PRO 6000 Blackwell — 96 GB, SM 12.0

| Model | Repo | Weights | Note |
|---|---|---|---|
| 3.8 | `Qwen/Qwen3.8-27B-FP8` | 30.9 GB | **default** — near-lossless |
| 3.8 | `Qwen/Qwen3.8-27B` | 55.6 GB | `--bf16`, full precision |

Same Blackwell silicon as the 5090, three times the memory. NVFP4 runs here,
but there is no reason to use it: at 96 GB the 4-bit build saves 12 GB you do
not need and costs quality you cannot get back. FP8 is the right default —
native on Blackwell, effectively lossless, and it still leaves ~57 GB.

That leftover buys **concurrency, not context**. 256K is the model's ceiling and
costs 8 GB, so the remaining ~49 GB goes to serving many sequences at once.

```bash
./scripts/get-models.sh --gpu pro6000 --model 3.8 --apply
./scripts/get-models.sh --gpu pro6000 --model 3.8 --bf16 --apply   # full precision
```

There is no MTP build of the FP8 or bf16 checkpoints; `--mtp` is ignored with a
warning rather than silently selecting a 4-bit build you did not ask for.

---

## H200 — 141 GB, Hopper SM 9.0

| Model | Repo | Weights | Note |
|---|---|---|---|
| 3.8 | `Qwen/Qwen3.8-27B` | 55.6 GB | **default** — bf16, no quantisation loss |
| 3.8 | `Qwen/Qwen3.8-27B-FP8` | 30.9 GB | `--fp8`, ~2x the batch room |

**Hopper has FP8 but not FP4.** NVFP4 is a Blackwell (SM 12.0) format, so the
5090 and PRO 6000 checkpoints will not run here — the script refuses them rather
than letting vLLM fail deep inside a kernel launch.

At 141 GB the full bf16 checkpoint fits with ~74 GB to spare, so the default is
**no quantisation at all**. A 27B is small for this card; the interesting
question is how many requests you serve in parallel, not whether the weights
fit. Use `--fp8` when you want to trade a little quality for roughly double the
concurrent-sequence budget.

```bash
./scripts/get-models.sh --gpu h200 --model 3.8 --apply
./scripts/get-models.sh --gpu h200 --model 3.8 --fp8 --apply
```

---

## Context window — what actually fits

These models advertise **256K** (`max_position_embeddings: 262144`). Whether you
can *serve* that is arithmetic on the VRAM left after the weights.

They use **hybrid attention**, and that is what makes long context affordable.
Of 64 layers, only **16 are full attention** — the other 48 are linear attention
with constant-size state, so they cost nothing per token. With 4 KV heads and
head_dim 256:

| Context | KV cache (bf16) | KV cache (fp8) |
|---|---|---|
| 32K | 2.1 GB | 1.1 GB |
| 128K | 8.6 GB | 4.3 GB |
| **192K** | 12.9 GB | **6.4 GB** |
| 256K | 17.2 GB | 8.6 GB |

A conventional 27B with all 64 layers doing full attention would cost roughly
four times this — about 49 GB at 192K, impossible on any single consumer card.

### Per card

| | VRAM | Weights | Left for KV | Realistic context |
|---|---|---|---|---|
| **RTX 3090** | 24 GB | 20.0 GB AWQ | 1.8 GiB | **24K** (measured) |
| RTX 4090 | 24 GB | 21.0 GB AWQ | ~1.8 GiB | 24K |
| **RTX 5090** | 32 GB | 18.8 GB NVFP4 | ~10 GB | **256K** (192K with MTP) |
| **RTX PRO 6000** | 96 GB | 30.9 GB FP8 | ~57 GB | **256K**, the cap is the model |
| **H200** | 141 GB | 55.6 GB bf16 | ~74 GB | **256K**, the cap is the model |

Above 32 GB the question stops being *"what context fits?"* and becomes
*"how many concurrent sequences fit?"* — 256K costs only 8 GB, so the PRO 6000
has room for roughly seven full-length sequences at once and the H200 nine,
before batching becomes the limit.

**192K does not fit on a 3090 *under vLLM*.** It needs 6.4 GB of KV cache and
roughly 2 GB is available after the weights — no setting closes a 4.4 GB gap.

It does not, however, mean long context on 24 GB needs a smaller model. It
means a different **build** and a different engine.

### 24 GB, long context: the GGUF route

Every figure above assumes AWQ/NVFP4 safetensors served by vLLM. The same 27B
as a **Q4_K_M GGUF is 17.5 GB rather than 19.6** — same 4-bit precision, ~2 GB
tighter packing — and Ollama can additionally quantise the **KV cache itself**,
which vLLM's `--kv-cache-dtype fp8` only halves.

Measured on a 24 GB card, largest window staying **entirely** on the GPU:

| KV cache | max context | VRAM | note |
|---|---|---|---|
| `f16` | 65,536 | 22.4 GB | 131,072 spills 27% to system RAM |
| `q8_0` | 114,688 | 22.8 GB | 122,880 already spills |
| **`q4_0`** | **131,072** | **21.9 GB** | the shipped default, ~2.6 GB spare |
| `q4_0` + `num_gpu` | **262,144** — the model's full window | 24.2 GB | ~370 MiB spare |

So a 24 GB card reaches the model's **entire 256K window**, five times what the
vLLM path manages. The costs are real and worth stating plainly:

* Ollama runs on the **host** and claims the same GPU, so vLLM must be stopped.
* vLLM batches far better. For serving several people at a 22K window it
  remains the right engine; this route is for one long context.
* Verified by **retrieval**, not by loading — the engine will happily load a
  window it then spills. Two facts at 25% and 75% depth of a 201,397-token
  prompt both came back correctly.

See [HERMES.md](HERMES.md) for the setup and the two failure modes that
degrade silently instead of erroring.

`--kv-cache-dtype fp8` is set by default on every preset: it halves KV cost for
negligible quality impact and is the difference between 32K and 16K on a 3090.

### Overriding

```bash
./scripts/get-models.sh --gpu 5090 --model 3.8 --context 262144 --apply
```

Defaults are 24576 (3090 and 4090), 262144 (5090, or 196608 with `--mtp`), and
262144 on the PRO 6000 and H200.

**The 24 GB figure is measured, not derived.** On a 3090 running vLLM 0.27.1
the naive arithmetic says 32K fits; it does not:

| Attempt | Result |
|---|---|
| `ctx 32768 @ util 0.92` | refused — needs 1.15 GiB KV, 0.90 GiB available |
| `util 0.95` | refused — wants 22.8 GiB, only 22.75 GiB free |
| `ctx 24576 @ util 0.93 + --enforce-eager` | **boots** — 1.8 GiB KV, 46,565 tokens, 1.95x |

Two things the arithmetic misses. Qwen3.x-27B is **multimodal**
(`language_model_only: false`), so vLLM also loads an unquantised vision tower;
and CUDA graph capture costs roughly a GiB, which is why `--enforce-eager` is
load-bearing on a 24 GB card rather than the WSL2 workaround it is elsewhere.
It costs throughput — ~12 tok/s instead of ~19.

Also note `--gpu-memory-utilization` is a fraction of **total** VRAM, not free
VRAM. A desktop session holding ~1.1 GiB caps the usable value near 0.94, so
0.93 is the safe ceiling on a card that also drives a display. If vLLM fails at
startup with *"No available memory for the cache blocks"*, the context is too
large for what the weights left behind — lower it.

---

## Qwen3.8-Flash-Next (177B MoE) — can this stack run it?

Short answer: **yes, on llama.cpp — which this stack can now deploy.** Not on
any released vLLM. The reasoning matters more than the verdict, because the
usual intuitions about model size do not apply, and because "does it fit"
depends entirely on which engine you ask.

Select it with `scripts/setup.sh` and answer `llamacpp` at the engine prompt;
see [SETUP.md](SETUP.md#choosing-an-inference-engine).

### Why a 177B model is even a candidate

Read from `Qwen/Qwen3.8-Flash-Next`'s own `config.json`, not from claims:

| | |
|---|---|
| architecture | `Qwen4ExpForConditionalGeneration` (`qwen4_exp`), multimodal |
| layers | 48 |
| experts | **512**, with **10 active per token** plus 1 shared |
| total parameters | ~177 B (121 B of it in experts) |
| **active parameters** | **2.66 B per token** |
| attention | hybrid: **12 full-attention layers of 48**, the rest linear |
| `max_position_embeddings` | **262,144** |

That 2.66 B is the whole story. Only ten small experts fire per token, so the
*compute* is that of a ~3 B model even though the *weights* are 177 B. Keep the
experts in system RAM and the active path on the GPU and it runs quickly on
hardware that could never hold it densely.

### The build you need depends on the engine

This is the correction most worth internalising: the three quantisations below
are not interchangeable, and only one of them is small enough to be interesting.

| build | size | engine that reads it | 3090 24 GB | 5090 32 GB |
|---|---|---|---|---|
| **GGUF UD-IQ4_XS** | **93.7 GB** | **llama.cpp** | **yes, measured — 3.4-4.3 tok/s** | yes, +128 GB RAM |
| NVFP4 | 135.2 GB | vLLM, Blackwell only | no | no |
| W4A16 | 179.8 GB | vLLM | no | no |

An earlier version of this document said a 5090 could serve this model and
implied vLLM would do it. That was wrong, and the error is instructive: the
93.7 GB figure is the **GGUF**, which vLLM cannot read for this architecture.
The smallest vLLM-loadable build is 135.2 GB, which does not fit 32 GB + 128 GB
either. On vLLM the answer is no on both cards.

Full GGUF ladder, measured from `unsloth/Qwen3.8-Flash-Next-GGUF`, weights only:

| quant | size | 32 GB + 128 GB RAM (~150 usable) |
|---|---|---|
| UD-Q2_K_XL | 78.9 GB | yes |
| UD-IQ3_XXS | 82.0 GB | yes |
| UD-Q3_K_XL | 90.0 GB | yes |
| **UD-IQ4_XS** | **93.7 GB** | **yes** |
| UD-Q4_K_XL | 111.3 GB | yes |

### The 24 GB box: it works

93.7 GB exceeds 24 GB of VRAM plus ~50 GB of usable RAM, so the model cannot be
*resident* on the test box. That is not the same as "cannot run", because
llama.cpp mmaps the GGUF rather than reading it whole: pages arrive on demand
and the kernel evicts what is cold.

Whether that is usable rather than merely possible turns on a property of this
specific architecture. **10 of 512 experts fire per token**, so the working set
is a small fraction of the file — but *which* ten changes every token, so the
miss rate depends on routing locality, which is an empirical question and not
one to answer from a spreadsheet.

The disk therefore decides it. On the NVMe measured here (Samsung 990 PRO,
5,411 MB/s sequential read) paging has a chance; on a SATA HDD it does not.

### Measured on the 3090

It runs. `UD-IQ4_XS`, 24 GB RTX 3090, 48 GB of RAM available to Docker, GGUF on
a Samsung 990 PRO reached through a Docker bind mount:

| | |
|---|---|
| load to healthy | **185–202 s** |
| generation, warm | **3.4 – 4.3 tok/s** |
| first request, cold | 0.30 tok/s |
| VRAM at `--n-cpu-moe 48` | 8.4 GB |
| VRAM at `--n-cpu-moe 38` | 20.5 GB |
| container RAM | 29–34 GB |

Output is correct, not degraded word-salad: it answers factual questions and
follows instructions normally.

**What the bottleneck is not.** Two intuitions were wrong here, and both were
cheap to test:

- *Not the disk.* The mount reads at 321 MB/s inside a container versus
  5,411 MB/s natively on the same NVMe — a 17x drvfs penalty, which looked
  damning. But block reads during generation were ~5 MB for a 150-token reply.
  The working set stays resident; it is not paging per token.
- *Not GPU starvation.* Moving ten layers of experts onto the card
  (`--n-cpu-moe` 48 -> 38, VRAM 8.4 -> 20.5 GB) changed throughput by nothing
  measurable: 3.5 -> 4.3 tok/s, inside run-to-run noise.

During generation the CPU sits at ~800% with the GPU at 25–35%, so the expert
matmuls on the CPU are what set the pace.

**Threads: leave them alone.** llama.cpp chose `n_threads = 10`, matching the
container's ten *physical* cores. Forcing `-t 20` to use all twenty logical
CPUs made it **2.5x slower** — 1.35 tok/s against 3.37. Hyperthreads contend
for the same memory bandwidth rather than adding throughput. This is the one
knob most likely to be tuned in the wrong direction.

**What this means for the 32 GB / 128 GB box.** Do not read 3.4 tok/s as the
deployment number. Two things change: 128 GB of RAM holds the whole 93.7 GB
without paging, and 32 GB of VRAM takes far more experts than 24 GB can. But
since the CPU is what limits this box, the deploy box's gain depends on its CPU
and memory bandwidth too — measure it there rather than extrapolating from
here.

### Context is NOT the constraint here

Only 12 of 48 layers keep a KV cache, with 2 KV heads at head_dim 256:

| context | KV fp16 | KV fp8 |
|---|---|---|
| 262,144 | 6.4 GB | **3.2 GB** |
| 1,000,000 | 24.6 GB | **12.3 GB** |

A million tokens costs ~12 GB of cache — comfortable on a 32 GB card. **The
weights are the limit, never the window.**

**But this checkpoint is not a 1M model.** `max_position_embeddings` is
**262,144** and `rope_scaling` is `null`. 256K is what it is trained and
configured for; anything beyond needs an explicit YaRN-style extension, with
the quality loss that implies. Do not plan on 1M without measuring it.

### No released vLLM can serve it yet

| where | `Qwen4Exp` registered? |
|---|---|
| vLLM **main** | **yes** — `vllm/models/qwen4_exp` |
| vLLM v0.28.0 (latest release) | no |
| vLLM v0.27.1 (this stack) | no |

Forty PRs have merged upstream and ~148 remain open, including PLE-offload and
sparse-attention kernels, so this is actively landing rather than speculative.
Until it appears in a release, serving it needs a nightly or a `main` build.

llama.cpp already supports it, and **this stack now ships llama.cpp as a
selectable engine** for exactly this case. `scripts/setup.sh` asks which engine
to run; picking `llamacpp` enables the `llamacpp` compose profile and points the
LiteLLM gateway at it. Everything above the engine — keys, budgets, per-user
attribution, dashboards, Open WebUI — is unchanged, because the gateway reads
its backend from `.env` rather than naming one.

Exactly one engine runs at a time. Both claim the whole GPU, so enabling both
profiles means one of them dies with a CUDA OOM that names neither.

### What to do

1. **Use llama.cpp, not vLLM.** No released vLLM registers `Qwen4Exp`, and even
   when one does, its smallest build is 135.2 GB. `setup.sh` -> engine
   `llamacpp`.
2. **Deploy on the 32 GB / 128 GB box** with `UD-IQ4_XS`. Budget ~94 GB of
   weights plus ~3 GB of KV cache at 256K. Set `LLAMACPP_N_CPU_MOE` high enough
   that the GPU-side footprint fits — 48 covers every layer — then lower it
   while it still loads, since each layer moved back to the GPU is faster.
3. **Keep the model on NVMe.** The design assumes mmap reads are cheap. On a
   spinning disk this is not slow, it is unusable.
4. **Keep vLLM for models that fit.** It batches far better; llama.cpp is the
   answer to "too big", not the better engine.
5. Re-measure the window before promising 1M to anyone.

## MTP (Multi-Token Prediction)

MTP checkpoints ship extra draft-head weights so the model can propose several
tokens per forward pass, which vLLM verifies in one go — speculative decoding
without a separate draft model. When it lands, decode gets meaningfully faster;
when predictions miss, it costs a little.

Two caveats worth knowing before you rely on it:

- **vLLM must be told to use it.** The weights alone do nothing; speculative
  decoding needs a `--speculative-config` entry naming the method and the number
  of speculative tokens. Support varies by vLLM version and model architecture,
  so verify with `docker logs vllm` that a speculative decoder actually
  initialised rather than assuming it did.
- **On a 3090 the draft heads compete for the memory you do not have.** The MTP
  builds are smaller (19.5 GB) than the plain AWQ ones, which helps — but if
  speculation does not initialise you have simply chosen a different quant.

If throughput matters more than certainty, start with the plain build, measure
with `./scripts/benchmark.sh`, then try MTP and compare. That is the only way to
know whether it helps your workload.

---

## Formats you will see, and what they mean

| Format | Bits | Runs on | Notes |
|---|---|---|---|
| **NVFP4** | 4 | Blackwell only | Best quality-per-byte at 4-bit; hardware-accelerated |
| **AWQ** | 4 | Any CUDA | Activation-aware; the practical choice on Ampere |
| **GPTQ** | 4 | Any CUDA | Similar to AWQ; pick whichever has a good build |
| **FP8** | 8 | Ada / Hopper / Blackwell | Near-bf16 quality, half the size |
| **GGUF** | varies | llama.cpp | **Not for vLLM** — different runtime |
| **MLX** | varies | Apple Silicon | Irrelevant here |

GGUF and MLX repos appear prominently in HuggingFace search results for these
models. Neither works with vLLM; ignore them.

---

## After downloading

```bash
docker compose up -d --force-recreate vllm
```

```bash
docker logs -f vllm
```

Watch for the KV-cache line — it tells you how much context actually fits. Then:

```bash
./scripts/smoke-test.sh
```

```bash
./scripts/benchmark.sh --concurrency 1,4,8
```

The benchmark is how you compare two builds honestly: same prompt, same
concurrency, compare TTFT and output tokens/sec.

---

## Keeping the tool-calling flags

Open WebUI sends `tool_choice: "auto"` on every request. Without
`--enable-auto-tool-choice`, vLLM returns HTTP 400 and chat breaks. The script
includes it in every generated `VLLM_EXTRA_ARGS`; keep it if you edit by hand.

**The parser must match the format the model emits, and getting it wrong fails
SILENTLY.** This stack shipped `--tool-call-parser hermes` for a long time. The
Qwen3.5 family does not emit Hermes-style JSON inside `<tool_call>` -- it emits
XML:

```xml
<tool_call><function=search_files><parameter=pattern> *.c </parameter></function></tool_call>
```

The `hermes` parser never matched, so vLLM returned `tool_calls: null` and put
that raw XML in the message **content**. No error, no warning, HTTP 200 -- tool
calling simply never worked, and an agent driving the model would loop forever
without a single failed request to point at.

The correct pair for this family:

```
--tool-call-parser qwen3_xml   # matches <function=…><parameter=…>
--reasoning-parser  qwen3      # otherwise <think> blocks leak into content
```

Verify after any model change rather than trusting the flag -- ask for two tool
calls and confirm you get two, with parseable arguments. `scripts/e2e-check.py`
asserts exactly this.

---

## Model names

Whatever name vLLM is started with is what clients see, so it is derived from
the checkpoint rather than left as a generic alias:

```
VLLM_MODEL=Qwen/Qwen2.5-7B-Instruct
VLLM_SERVED_MODEL_NAME=Qwen2.5-7B-Instruct
```

That name appears in the Open WebUI model picker, in `/v1/models`, in LiteLLM's
spend logs, and as the `model_name` label on every Grafana panel.

The gateway must be told the same name. **LiteLLM does not expand `${VAR}` in
its YAML**, so `config/litellm/config.yaml` carries the literal string —
`scripts/switch-model` and `get-models --apply` rewrite it for you:

```
VLLM_MODEL              -> Qwen/Qwen2.5-14B-Instruct-AWQ
VLLM_SERVED_MODEL_NAME  -> Qwen2.5-14B-Instruct-AWQ
gateway config          -> Qwen2.5-14B-Instruct-AWQ
```

A second entry named `local` points at the same engine, so scripts and SDK
clients can hardcode one name that survives checkpoint changes.

> **LiteLLM caches its model list at startup.** Editing `config.yaml` alone does
> nothing — the container must be recreated. `switch-model` does that
> automatically when the `gateway` profile is enabled.
