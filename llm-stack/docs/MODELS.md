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
VLLM_EXTRA_ARGS=--enable-auto-tool-choice --tool-call-parser hermes --quantization awq --kv-cache-dtype fp8
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

**192K does not fit on a 3090.** It needs 6.4 GB of KV cache and roughly 2 GB is
available after the weights — no setting closes a 4.4 GB gap. Long context on
24 GB means a smaller model.

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
`--enable-auto-tool-choice --tool-call-parser hermes`, vLLM returns HTTP 400 and
chat breaks. The script includes both in every generated `VLLM_EXTRA_ARGS`; keep
them if you edit by hand. `hermes` is the correct parser for Qwen-family models.

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
