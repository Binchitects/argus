# Argus Arena on a 4× RTX PRO 6000 Blackwell server: design and implementation plan

*Design, 2026-10-16. Sizes and versions as found that day on Hugging Face and the projects' release pages; nothing was downloaded. Section 8 is the plan the code follows; section 7 lists what the owner decides.*

---

## 0. Summary

1. **Hardware.** 4 × 96 GB gives 384 GB of VRAM. The cards connect over PCIe Gen5 only (no NVLink), and they are sm_120. They support MIG.
   - The PCIe-only link decides most of the design:
     - Use tensor parallelism (TP) over **2 or 4** GPUs.
     - Do not use expert parallelism over PCIe.
     - Prefer splitting the GPUs between models over one huge model.
   - The host already runs driver 595.99, which covers CUDA 13 and Blackwell.
2. **Models.** The 671B DeepSeek-V3/R1 family does not fit on the GPUs, even at NVFP4: `nvidia/DeepSeek-V3.1-NVFP4` is about 413 GB. These fit well:
   - Qwen3.8-Flash-Next (FP8 186 GB / NVFP4 133 GB)
   - Qwen3.5-397B-A17B NVFP4 (about 244 GB)
   - Qwen3-235B-A22B (FP8 236 GB / NVFP4 134 GB)
   - Qwen3-Coder-480B NVFP4 (273 GB)
   - DeepSeek-V4-Flash (about 160 GB)
   - gpt-oss-120b (about 65 GB, one GPU)
3. **Engine.** Add **vLLM** as a second engine kind next to llama.cpp.
   - llama.cpp keeps the GGUF models, the small models, the embedder, and anything that must spill to RAM.
   - Leave SGLang out until a model needs it. On sm_120 every extra engine is another set of fragile kernels.
   - The app drives vLLM the way it drives llama.cpp and the media servers: it writes a desired-state file, and a supervisor script reconciles it. That keeps the rule of no Docker socket.
4. **KV cache.** Start with vLLM's built-in prefix caching plus its in-tree CPU `OffloadingConnector`.
   - Add **LMCache** (CPU plus local NVMe, later Redis/Valkey) only when you need a warm cache that survives engine restarts, or sharing across replicas or servers.
   - Cache-aware routing only matters once a model has two or more replicas. On one box, the app routes by conversation affinity. On Kubernetes across servers, use **llm-d** (or the vLLM production-stack router). I don't recommend Dynamo here: it duplicates LiteLLM plus the app.
5. **Durability.**
   - Run LiteLLM as several replicas with Redis/Valkey for shared router state.
   - Postgres gets WAL archiving and point-in-time recovery (PITR): pgBackRest on compose, CloudNativePG on Kubernetes.
   - The app's fair queue extends to API keys, with vLLM priority scheduling behind it.
   - Engine drains are graceful, and LiteLLM falls back to a smaller model while the big one restarts.
   - DCGM and vLLM metrics feed the existing Prometheus.
   - Load tests run in CI against a fake vLLM.

---

## 1. Hardware facts and what they imply

| Fact | Value | Source |
|---|---|---|
| Memory | 96 GB GDDR7 ECC per card (384 GB total), 512-bit, **1597 GB/s** | NVIDIA Server Edition datasheet; PNY |
| Interconnect | PCIe Gen5 x16 (about 64 GB/s each way); **no NVLink** | datasheet; WZ-IT |
| MIG | Supported (Server Edition datasheet). Instance profiles to be confirmed in NVIDIA's MIG guide; I expect up to 4 × 24 GB. | datasheet |
| Precision | 5th-gen Tensor Cores, FP4 (NVFP4) and FP8 | datasheet |
| Compute capability | 12.0 / **sm_120** | vLLM forum SM120 report |
| Cooling | Server Edition: **passive**. It needs a certified 2U/4U GPU chassis with front-to-back airflow. The Workstation Edition (600 W, flow-through fans) does not suit a 4-up chassis; the Max-Q edition (300 W blower) does. | datasheet; PNY |
| Power | Up to 600 W per card. Sources say 400–600 W configurable for the Server Edition. | search summaries (verify with the vendor) |

**What follows from these facts**

- **Tensor parallelism over PCIe.** Every layer does an all-reduce across the TP group. Without NVLink, single-stream decode is limited by all-reduce latency, not by memory bandwidth.
  - TP=4 works. One reported run, Qwen3.5-397B NVFP4 at TP=4, got about **50 tok/s** for a single stream.
  - TP=2 combined with pipeline parallelism (PP=2) was about the same: 49 tok/s.
  - **Expert parallelism over PCIe was catastrophic**: 1.4–2.6 tok/s.
  - Rules:
    - Use TP ∈ {1, 2, 4}. TP must divide the attention-head count, so 3 is out.
    - Never use expert parallelism.
    - For small-active MoE models, prefer TP=2 with 2 replicas (data parallel) over TP=4.
- **NCCL P2P.** RTX PRO cards allow P2P over PCIe; GeForce cards do not. Google built a P2P fabric for its G4 VMs, which use these cards.
  - Put all four GPUs on one CPU socket's root complex, or behind one PCIe Gen5 switch.
  - Set `iommu=pt` and disable ACS on the switch so P2P is not routed through the CPU.
  - At bring-up, check `nvidia-smi topo -m`, `p2pBandwidthLatencyTest` and `all_reduce_perf`.
  - If P2P is missing, vLLM still works with `NCCL_P2P_DISABLE=1` and `--disable-custom-all-reduce`, but slower. The forum report needed exactly that, under WSL2.
- **Driver and CUDA.**
  - sm_120 needs CUDA ≥ 12.8 and driver ≥ R570.
  - vLLM v0.29 through v0.31 images default to **CUDA 13.0** (with `-cu129` variants), which needs driver ≥ R580.
  - The current host has **595.99**, which is fine. Pin the driver; don't let the distribution upgrade it.
  - Install nvidia-container-toolkit with CDI. Podman already uses CDI here.
- **Maturity on sm_120 is the main risk.** Many fast kernels in FlashInfer, CUTLASS, DeepGEMM and FlashMLA target sm_100/103 first.
  - Reports describe NVFP4 MoE on sm_120 that only works on the **Marlin W4A16 fallback**, and SGLang 0.5.8 producing NaN outputs.
  - The v0.31.0 release notes mention sm_120 only once (sparse MLA for GLM-5.3-Flash).
  - Therefore: pin vLLM versions, give every model a smoke test, and prefer **FP8** checkpoints where they fit. The FP8 kernels on sm_120 are older and more robust than NVFP4 MoE.
- **Power and cooling.**
  - The GPUs draw 4 × 600 W = 2.4 kW. With CPUs, RAM and NVMe the box is about 3.2–3.6 kW.
  - Buy redundant PSUs (2+2), and plan for 208–240 V circuits and about 12 kBTU/h of heat.
  - The existing `deploy/services/power-limits.sh` can cap each card (for example at 450 W). Large-MoE decode is bandwidth-bound and loses little throughput under a cap.
- **CPU RAM** is used for three things:
  1. Page cache of weights, so restarts reload from RAM instead of NVMe. A 250 GB model reloads from RAM in tens of seconds; from NVMe it takes minutes.
  2. The KV CPU tier (OffloadingConnector or LMCache).
  3. llama.cpp expert offload for anything that doesn't fit on the GPUs.
  - **Recommendation: 1 TB DDR5** (2 sockets × 12 channels × 64 GB, or 1 socket × 12 × 96 GB). Budget: about 300 GB weights cache, 400–500 GB KV tier, and the rest for OS, app, Postgres and media.
  - **768 GB** is the floor (2× VRAM).
- **NVMe.**
  - At least 4 × 7.68 TB Gen5: about 4 TB for the model library (several 150–300 GB checkpoints), 2–4 TB for the LMCache disk tier.
  - Put Postgres on its own mirrored pair.
  - Add a separate disk or NAS for backups and WAL.
- **CPU.** At least 32–64 cores (EPYC 9004/9005 or Xeon 6). That covers tokenisation and detokenisation, LiteLLM, the app, Postgres and the CPU embedder, plus enough PCIe lanes: 4 × x16 GPUs plus NVMe.

---

## 2. Which large models fit, and how

**Memory budget for a TP=4 group**

- 4 × 96 GB at `--gpu-memory-utilization 0.90` gives about **345 GB**.
- Subtract about 3 GB per GPU for activations and CUDA graphs: about **333 GB** for weights plus KV.
- For TP=2: about **166 GB**. For one GPU: about **83 GB**.

**KV per token** is computed from each model's `config.json` with an FP8 KV cache: 2 (K and V) × full-attention layers × KV heads × head dim × 1 byte.

| Model (checkpoint) | Weights (HF metadata) | Fits? / placement | KV/token (FP8) | Room for KV | Context | Concurrency (rough) |
|---|---|---|---|---|---|---|
| **Qwen3.8-Flash-Next** FP8 (`Qwen/Qwen3.8-Flash-Next-FP8`), 180B incl. n-gram embeddings, about 6B active; 12 of 48 layers full attention | **186 GB** | TP=2 → no room for KV. **TP=4 → yes.** | 12 KB | about 145 GB → about 12M tokens | 262K | Hundreds of sessions. Single stream estimated 100–200 tok/s (unmeasured). |
| same, NVFP4 (`nvidia/Qwen3.8-Flash-Next-NVFP4`) | **133 GB** | **TP=2 → yes** (about 30 GB KV, about 2.5M tokens). Two replicas on 4 GPUs. | 12 KB | 30 GB per replica | 262K | Best throughput layout if NVFP4 is stable on sm_120. |
| **Qwen3.5-397B-A17B** NVFP4 (`nvidia/Qwen3.5-397B-A17B-NVFP4-V2`), 15 of 60 layers full attention | **about 244 GB** | TP=4 | 15 KB | about 90 GB → about 6M tokens | 262K | About 190 × 32K sessions. **50 tok/s single stream reported** (TP4, Marlin). |
| **Qwen3-235B-A22B-2507** FP8 | **236 GB** | TP=4 | 94 KB (94 layers × 4 KV heads × 128) | about 97 GB → about 1M tokens | 262K | About 30 × 32K sessions |
| same, NVFP4 (`nvidia/Qwen3-235B-A22B-NVFP4`) | **134 GB** | TP=2 → about 30 GB KV (tight); TP=4 → about 200 GB KV | 94 KB | | | |
| **Qwen3-Coder-480B-A35B** NVFP4 (`nvidia/…-NVFP4`) | **273 GB** | TP=4 | 124 KB | about 60 GB → about 480K tokens | 262K | About 15 × 32K sessions. Good coding model, KV-hungry. |
| Qwen3-Coder-480B FP8 | 482 GB | **no** | | | | |
| **DeepSeek-V4-Flash** (284B / 13B active; native FP4/FP8 mix) | **about 160 GB** | TP=4 (TP=2 is too tight) | Very small (compressed sparse attention). Read vLLM's reported capacity. | about 170 GB | **1M** | Large. Its sm_120 kernels (FlashMLA, sparse) are the least proven: v0.31 makes FlashMLA default on sm_100 only. **Test first.** |
| **gpt-oss-120b** (MXFP4) | **about 65 GB** (download only the root safetensors; `original/` and `metal/` double the repo) | **1 GPU** | 36 KB in bf16, half the layers sliding-window 128 | about 18 GB | 131K | Fast single-GPU model; also a fallback while the big model restarts. |
| GLM-5.3-Flash FP8 (321B, released 2026-10-15) | 321 GB | TP=4 at about 10 GB KV: too tight | MLA, small | | 1M | Wait for an NVFP4 checkpoint (about 180 GB). |
| GLM-4.6 FP8 | 361 GB | no KV room | | | | |
| GLM-5.1 NVFP4 (community) | 466 GB | **no** (the reported 641 tok/s used **6** cards) | | | | |
| DeepSeek-V3.1 / R1 NVFP4 | 413 GB | **no** | | | | Only as GGUF Q2/Q3 in llama.cpp with experts in RAM: slow. |
| Llama-4-Maverick FP8 | 402 GB | no | | | | |
| Kimi-K2-Thinking INT4 (1T) | 594 GB | no | | | | |
| Qwen3.8-2.4T-A95B | about 4.9 TB BF16 | no | | | | |
| Qwen3.8-27B (current) | 56 GB BF16 / about 17 GB Q4 GGUF | 1 GPU, or a share of one | | | | Stays on llama.cpp. |

**Notes on the numbers**

- Sizes are read from the Hugging Face API safetensors metadata, or `usedStorage` where no metadata exists, on 2026-10-16. `usedStorage` can include extra files.
- Throughput figures without a citation are bandwidth estimates. Commit 15 measures them.
- Aggregate throughput with batching is typically 10–30× the single-stream figure. For example, a GLM-5.1 NVFP4 run on 6 cards reported 641 tok/s.

**Recommended starting layouts.** The person chooses one (see §7).

- **Layout A, flagship.**
  - GPUs 0–3: TP=4 vLLM, Qwen3.5-397B NVFP4 *or* Qwen3-235B FP8, at `gpu-memory-utilization 0.80`. That leaves about 18 GB per GPU.
  - GPU 3's spare: media servers. GPUs 2 and 3's spare: Qwen3.8-27B GGUF in llama.cpp.
  - GpuShare lends memory by putting the vLLM model to **sleep**, not by unloading it.
- **Layout B, throughput (recommended default).**
  - GPUs 0–1: Qwen3.8-Flash-Next NVFP4, TP=2 (the default chat model).
  - GPU 2: gpt-oss-120b (code and tools; fallback).
  - GPU 3: media (imagegen/videogen) plus llama.cpp small models and the embedder. Optionally split GPU 3 with MIG (for example 2 × 48 GB) for hard isolation between media and LLMs.
- **Layout C, coding.**
  - GPUs 0–3: Qwen3-Coder-480B NVFP4 at TP=4.
  - Media is lent memory through sleep.
- Any layout can be switched at runtime from Admin → Models. That is the point of the plan below.

---

## 3. Engines, and how the app's model management generalises

### 3.1 Engine choice
- **vLLM (add).**
  - Covers the features you asked about: TP, FP8/NVFP4, automatic prefix caching (on by default), chunked prefill, speculative decoding (MTP and EAGLE-3), priority scheduling, `/metrics`, sleep mode, and KV connectors (Offloading, LMCache, NIXL, Mooncake).
  - Has the widest sm_120 field experience.
  - Pin a version, for example **v0.31.0** (released 05 Oct, CUDA 13.0 default).
- **llama.cpp (keep).**
  - Serves GGUF, the small models, models partly offloaded to RAM (DeepSeek-class Q2/Q3), the embedder, and router mode for many small models.
  - Already supports per-model `device = CUDA0,…` (`ModelCatalog.cs:538`) and per-GPU fit targets.
- **SGLang (later, optional).**
  - Offers HiCache (GPU, host, then L3 storage tiers) and strong MLA/DeepSeek kernels.
  - But reports show NaN outputs on sm_120 with 0.5.8, and a third engine doubles the matrix.
  - Keep the abstraction kind-agnostic, and add SGLang only when a model needs it.

### 3.2 How the app drives vLLM (no Docker socket)
vLLM serves **one model per process** and has no router mode. Keep the pattern the stack already uses for llama.cpp (`router.sh` watching `models.ini`) and for media (`sd-serve.sh` with `gpu=N` and status files):

- **One `vllm` container** (compose `count: all` GPUs, like today) runs `deploy/services/vllm/supervisor.sh`.
- **The app writes the desired state:** `/presets/vllm/models.json`, a list of `{name, port, gpus:[0,1], args:{…}, sleep:false}`.
- **The supervisor reconciles it:**
  - It starts a `vllm serve` per entry with `CUDA_VISIBLE_DEVICES` set, and stops the entries that were removed. It drains first: SIGTERM, which vLLM handles by finishing in-flight requests up to a timeout.
  - It restarts entries whose arguments changed, with backoff after a crash.
  - It writes `/status/vllm/<name>.json`: phase (starting, loading, ready, sleeping, failed), PID, port, last log lines, and GPU free memory, as `sd-serve.sh` does.
- **Load/unload** means adding or removing an entry.
  - **Sleep/wake** uses vLLM's sleep mode (`--enable-sleep-mode`, the `/sleep` and `/wake_up` dev endpoints). Weights move to CPU RAM and come back in seconds. That is how `GpuShare` lends memory without losing a several-minute reload.
  - Verify sleep mode with TP on sm_120 during bring-up; if it fails, fall back to unload.
- **Health and metrics** come from each process's `/health`, `/v1/models` and `/metrics`. The app adds each port to Prometheus `file_sd`, as it already does for `engine-sd/targets.json`.
- **Gateway:** `ModelCatalog` registers the model with LiteLLM as `api_base: http://vllm:<port>/v1`. `max_parallel_requests` equals `--max-num-seqs`, shared among replicas as today.

### 3.3 Generalising the model code (file by file)
- **`EngineOptions` / `EngineClient` (`Models/Engine.cs`)**
  - Becomes `IEngineBackend` with `ListAsync / LoadAsync / UnloadAsync / SleepAsync / CapacityAsync / HealthAsync`.
  - `LlamaCppBackend` is today's client. `VllmBackend` is the desired-state file plus status files plus HTTP.
  - `EngineOptions` gets a list of engines: `{Kind, Url or ConfigDir, StatusDir}`.
  - No backward compatibility is needed, per your stated preference.
- **`LocalModel`** (`Llm.Core/Models/LocalModels.cs`) gains `EngineKind` (llamacpp or vllm), `Source` (GGUF file vs. HF safetensors directory), `Tp`, `Quantization`, `KvTier`, `SpecDecode`, and `MaxNumSeqs`. `Devices` already exists and becomes the GPU set for both kinds.
- **`ModelAdvisor` → split per kind.**
  - The llama.cpp advisor stays as it is.
  - A new **`VllmAdvisor`** does the following:
    - Reads `config.json` (layer types, KV heads, head dim, MLA rank, sliding window) and the safetensors index (bytes per dtype).
    - Checks that TP divides the heads and that weights / TP fit.
    - Computes KV per token, and from it the maximum `max-model-len` × `max-num-seqs`.
    - Counts CPU RAM for the KV tier and for sleep (weights go to RAM).
    - Returns the same `ModelAdvice` / `MemoryEstimate` shapes, so the UI reuses its cards.
- **`Hardware` / `HardwareProbe`.** `Hardware.On(indices)` already models a subset of GPUs. Its one hard-coded assumption is that "the image server runs on GPU 0" (`ImageReserve` is charged only to that GPU). Replace it with:
- **`GpuPlan` (new).** The single owner of placement. It is the generalisation of `KeptPlan` / `GpuUse`.
  - **What it holds:** every resident thing per GPU:
    - llama.cpp models, through their `Devices`
    - vLLM processes, through `gpus` × `gpu-memory-utilization`
    - media servers, through their `gpu` and need
    - the embedder
  - **What it checks:** each GPU's sum is within its budget, before any load and on save.
  - **What Admin → Models shows:** one bar per GPU, plus why a load is refused.
- **`EngineRoute` (`Chat/EngineRoute.cs`).** Room-making becomes **per GPU set**: to load model X on {0,1}, only idle, unkept models touching {0,1} may make room.
  - For vLLM there is no slot pinning, because automatic prefix caching makes any sequence reuse cached prefixes. `SlotTable` and `TokenCache.Pinned` are false for vLLM models.
  - "Busy" comes from `vllm:num_requests_running` and `vllm:num_requests_waiting`, not `/slots`.
- **`GpuShare`.** Today one GPU is lent (the "gpu:lent" state blocks *all* chat loads). It becomes:
  - The lending is scoped to the media server's GPU.
  - It sleeps or unloads only the models on that GPU.
  - A vLLM TP group is all-or-nothing: sleeping it frees all of its GPUs.
- **`TokenCache`.** Gains the vLLM view: GPU KV blocks, CPU tier GB, disk tier GB, and the prefix hit rate, from metrics.
- **`ModelDownloads` / `ModelLibrary`.**
  - Download **HF repo snapshots** (safetensors, `config.json`, tokenizer; skipping `original/`, `metal/`, `*.gguf`) into `library/hf/<org>/<repo>@<rev>`.
  - Recognise them as vLLM-loadable. Show sizes before download, as the downloads-need-approval rule requires.
- **Admin UI (Models).**
  - New fields: engine kind, a GPU checkbox picker (TP derived and validated), quantisation (auto/fp8/nvfp4), KV dtype (fp8 default), context, max sequences, speculative decoding (none/MTP/EAGLE-3 with draft), KV tier (none/CPU GB/LMCache), sleep allowed, and fallback model.
  - Per-GPU bars from `GpuPlan`.
- **Helm** (`templates/engines.yaml`). A `vllm` Deployment like `llamacpp`: `strategy: Recreate`, `nvidia.com/gpu: N`, `/dev/shm` sized 16–64 Gi, the presets PVC, and a status emptyDir shared with the app via PVC.
  - On Kubernetes, prefer **one Deployment per engine group** later, with `nvidia.com/gpu` 2 or 4, because the device plugin, not the supervisor, should own GPU assignment. Phase this in at §5.6.

---

## 4. KV cache: LMCache and alternatives

| Option | What it gives | Fit here |
|---|---|---|
| **vLLM automatic prefix caching** (APC, on by default) | GPU KV reuse of shared prefixes (system prompt, tools, conversation history) | **Always on.** It replaces llama.cpp's slot pinning. |
| **vLLM `OffloadingConnector`** (in-tree) | GPU → pinned CPU RAM tier: `--kv-transfer-config '{"kv_connector":"OffloadingConnector","kv_role":"kv_both","kv_connector_extra_config":{"block_size":64,"cpu_bytes_to_use":…}}'`. A multi-tier CPU plus filesystem mode is documented; v0.31 adds back-pressure detection. | **Step 1.** No extra package, version-locked with vLLM. 200–400 GB of RAM per big model holds millions of tokens, so returning conversations skip re-prefill. |
| **LMCache** (`LMCacheConnectorV1`; latest v0.5.6 on 15 Oct, nightly 0.5.7.dev) | CPU plus local disk plus remote (Redis/Valkey, Mooncake, …) tiers; **survives engine restarts** (disk); **shares** KV across instances and servers; a controller for cache-aware routing (production-stack router) | **Step 2**, for: (a) a restart or upgrade without a cold cache, (b) two or more replicas of a model, (c) several servers. Cost: must be built against the exact vLLM/torch version, and sm_120 is unproven. Ship it as an option, not the default. |
| SGLang HiCache | GPU → host → L3 (Mooncake, 3FS, NIXL, file) | Only if SGLang is adopted. |
| NVIDIA Dynamo (v1.2 GA Jun 2026, later versions exist) | KV-aware router, KVBM, disaggregated prefill/decode, NATS+etcd | Overlaps the app plus LiteLLM. Disaggregation needs fast interconnects. **Not now.** |
| llm-d (CNCF Sandbox, 2026) | Kubernetes Gateway API inference scheduler with KV-event-aware scoring, for vLLM | **Pick this for multi-server Kubernetes.** |
| vLLM production-stack router | Session- and prefix-aware routing; works with the LMCache controller | Alternative to llm-d on Kubernetes. |
| LiteLLM routing | `least-busy` and similar; no KV awareness | Keep as the gateway. Let the app or llm-d choose replicas. |

**How it plugs in**

- **Compose (one server).**
  - The `vllm` image is `arena-vllm`, built from a pinned base, with `lmcache==<pinned>` installed by pip (PyPI is reachable from the host).
  - The app writes `kv-transfer-config` per model, and for LMCache writes `/presets/vllm/lmcache-<model>.yaml`:
    - `local_cpu: true`, `max_local_cpu_size: <GB>`
    - `local_disk: file:///kvcache/<model>`, `max_local_disk_size: <GB>`
    - optional `remote_url: redis://valkey:6379`
  - That file is passed with `LMCACHE_CONFIG_FILE`, and `/kvcache` is a bind mount on the NVMe tier.
  - Routing: one replica per model, or for two TP=2 replicas the app pins conversations by **conversation-id affinity**.
    - The two replicas register in LiteLLM under internal names `model@0` and `model@1`.
    - The chat chooses the replica itself; API keys go to LiteLLM `least-busy` over the pool.
    - This mirrors what `SlotTable` does for llama.cpp slots today.
- **Helm / Kubernetes.**
  - An `lmcache` ConfigMap per model; `/kvcache` from a local PV (an NVMe hostPath via local-path provisioner).
  - Valkey as a StatefulSet. A KV cache is disposable, so no HA is needed for it.
  - Across servers: llm-d's inference scheduler (or the production-stack router) in front of the per-node vLLM Services. LiteLLM points at that one endpoint per model.

---

## 5. "Large scale and durable"

### 5.1 App
- **What exists already:** several replicas, a Postgres advisory-lock leader, LISTEN/NOTIFY messaging, Traefik sticky cookies (`deploy/scale.yml`), and per-replica shares of model places (`AnswerGate`).
- **Gap 1: an answer in flight dies with its replica.**
  - Persist partial answers every N tokens.
  - On replica death, the page shows "interrupted, retry", or the new leader re-issues the request (APC/LMCache makes the re-prefill cheap).
- **Gap 2: PgBouncer must use session mode for the app.** It relies on advisory locks and LISTEN; transaction mode would break both.

### 5.2 LiteLLM
- Run 2–3 replicas behind Traefik.
- Shared router state (least-busy counts, `max_parallel_requests`, rate and token limits) needs **Redis/Valkey** (`router_settings.redis_host`). Without it each replica counts alone and can overload a vLLM model.
- Add `fallbacks` per model group: big model, then gpt-oss-120b, then Qwen3.8-27B. This covers engine restarts and failures.
- Keep `fail_closed` on the guardrail.

### 5.3 Postgres
- **Compose:**
  - Keep `pgvector/pgvector:pg16`, but add **WAL archiving plus PITR** with **pgBackRest** to a second disk or NAS, or to S3-compatible storage.
  - Full backup weekly, differential daily, WAL continuous. Verify restores monthly with a restore-drill script.
  - `scripts/backup.sh` keeps the logical dump as a second line.
  - Optional hot standby on a second machine later (streaming replication plus manual promote).
- **Kubernetes:** **CloudNativePG**.
  - 3 instances, synchronous quorum.
  - Barman-cloud backups to S3 with PITR.
  - A pgvector-enabled image (CNPG's ghcr images; confirm pgvector is included, or build FROM it).
  - Its read-write Service feeds both the app and LiteLLM.
- **Valkey/Redis:** a single instance with AOF on compose; Sentinel or replicas on Kubernetes. It is a cache and router state, so losing it is survivable.

### 5.4 Queueing, backpressure, fair scheduling
- The chat already has a fair queue (`AnswerGate`: priority, then fewest-running, then served longest ago).
- **Extend the same gate to API keys** through the existing pre-call guardrail (`/internal/guardrail`). The gate:
  - holds a request until a place is free, up to a limit;
  - answers **429 with Retry-After** when the line is too long;
  - counts places per model from `max-num-seqs`.
- Pass the group priority to vLLM (`--scheduling-policy priority`, request `priority` field). Inside the engine, admins and interactive chat then go ahead of batch API traffic.
- **Admission control on engine signals.** When `vllm:num_requests_waiting > 0` for a while, or KV usage stays above 95%, shrink the gate's places for that model. Queue in the app, where it is fair and visible, not inside vLLM.
- Per-person and per-group token-rate limits stay in LiteLLM, which uses Redis for them.

### 5.5 Engines: health and graceful restarts
- The supervisor drains on SIGTERM; the app marks the model "draining" so `EngineRoute` stops sending to it; LiteLLM's fallback takes new requests.
- Fast reload:
  - weights kept in page cache (enough RAM);
  - `--load-format` options for faster loading, where they work on sm_120;
  - **sleep mode** for planned GPU lending.
- Health:
  - The supervisor watches `/health` and restarts with backoff.
  - The app's `EngineWatcher` reads the status files and `/health`, and Admin shows the phase.
  - Alert on restart loops.
  - Watch GPU Xid and ECC errors through DCGM, and fence a GPU whose errors repeat. `GpuPlan` then marks it unavailable.

### 5.6 Several servers later (Helm/Kubernetes)
- **Operators:** NVIDIA GPU Operator (driver, toolkit, device plugin, DCGM, MIG manager) from nvcr.io.
- **One Deployment per engine group** (`nvidia.com/gpu: 2|4`) with node affinity to a server that has the weights on local NVMe.
  - Weights are pre-staged by a DaemonSet or by the app's downloads per node. Never load 250 GB over a network filesystem.
- **Autoscaling:** KEDA or HPA on `vllm:num_requests_waiting` and KV usage. Scale-up takes minutes, so keep a minimum of one replica for each kept model.
- **Routing:** llm-d inference scheduler (KV-aware), with LiteLLM in front of it.
- **Postgres:** CloudNativePG. **Valkey:** replicated. **App:** a Deployment with N replicas behind an ingress with a sticky cookie.
- **Multi-node TP is not needed:** every model in §2 fits in one node.

### 5.7 Observability
- Add Prometheus targets for each vLLM process (written by the app, as for llama.cpp).
- Swap `nvidia_gpu_exporter` for, or add beside it, **DCGM exporter** (PCIe throughput, Xid, ECC, power and thermals per GPU).
- **Metrics to graph:**
  - vLLM:
    - `vllm:num_requests_running` and `vllm:num_requests_waiting`
    - KV cache usage
    - prefix-cache hit rate
    - TTFT, inter-token latency and end-to-end latency histograms
    - preemptions
  - LMCache: hit tokens, tier sizes, store/retrieve latency.
- **Alert rules:**
  - KV usage above 95% for 5 minutes
  - requests waiting for more than 2 minutes
  - TTFT p95 over its SLO
  - an engine restart loop
  - a GPU Xid, ECC error, or temperature/power throttling
  - PCIe P2P falling back
  - Postgres WAL archive failures, and backup age over 26 h
- Admin → Models cards show per-GPU use and each model's cache hit rate; `audit-dashboards.py` covers the new panels.

### 5.8 Load testing plan
- **`deploy/scripts/multiuser-bench.py`** already has single, parallel, staggered, long-paste, overflow, isolation and prefix scenarios. Add:
  - a `--ramp 1..N` concurrency sweep with tok/s, TTFT p50/p95 and queue wait;
  - `--prefix-share` (many users, one long system or tool prompt) to measure APC;
  - `--return-after` (a conversation resumes after its KV was evicted) to measure the CPU or LMCache tier;
  - `--kill-engine` (restart mid-run) to measure fallback and recovery time.
- **`deploy/scripts/scale-test.py`**: chat fair queue plus API keys through the new API-key gate (429/Retry-After behaviour, no cross-user leaks), at 50, 100 and 300 users.
- Both run in CI against the **fake vLLM** (§6) for behaviour, and on the real box for numbers, with JSON results stored per run to compare settings (KV tier, TP=2×2 vs. TP=4, FP8 vs. NVFP4).

### 5.9 Failure modes and recovery
| Failure | Effect | Recovery |
|---|---|---|
| vLLM process crash or hang | That model is down | Supervisor restarts it; LiteLLM falls back; app shows "loading"; alert on loops. |
| One GPU fails (Xid, ECC) | Its TP group is down | Fence the GPU in `GpuPlan`; re-place onto the remaining GPUs (for example TP=2 flagship on NVFP4); fallback model meanwhile. |
| P2P unavailable after a BIOS or driver change | TP slow | `gpu-check` script at boot; supervisor adds `NCCL_P2P_DISABLE` and alerts. |
| Out of memory at load | Load refused | `GpuPlan` and the advisor refuse before load; status file carries the vLLM error. |
| KV pressure | Preemption, slow answers | Fair gate shrinks admission; CPU or LMCache tier; alert. |
| App replica dies | Its in-flight answers stop | Other replicas take the lead; partial answers persisted; retry. |
| LiteLLM replica dies | None (other replicas) | Traefik health check. |
| Valkey lost | Router counts reset, KV tier lost | Recreated; degraded, not down. |
| Postgres disk lost | Outage | Restore with pgBackRest PITR (RPO is seconds to minutes), or promote the standby / CNPG failover. |
| Power or cooling event | Throttling | DCGM alerts; power caps (`power-limits.sh`). |

---

## 6. What must be downloaded (for your approval)

Docker Hub is blocked here. The registry probe (`/v2/`, headers only) gave:

- ghcr.io answers.
- public.ecr.aws and quay.io answer with an auth challenge, which is normal.
- **nvcr.io returned 403 anonymously.** That may be normal for nvcr.io; verify with one approved pull.
- **PyPI and the Hugging Face API answer.**

### Images and packages
| Item | Source (Docker Hub alternatives) | Approx. size | Notes |
|---|---|---|---|
| vLLM base (option 1) | `nvcr.io/nvidia/vllm:26.09-py3` (NGC, built 2026-09-23) | about 10–15 GB (estimate) | NVIDIA-built, CUDA 13. Check the vLLM version inside. |
| vLLM (option 2, recommended if nvcr fails) | Build `arena-vllm`: `nvcr.io/nvidia/cuda:13.0.x-runtime-ubuntu24.04` (or a cached base), then `pip install vllm==0.31.0` (pulls torch cu130, flashinfer, xformers) | about 8–12 GB built (about 3–5 GB of wheels) | Docker Hub `vllm/vllm-openai:v0.31.0` is blocked. vLLM's CI images on public.ecr.aws are a possible mirror (verify). |
| LMCache | `pip install lmcache==0.5.6` into the same image (Docker Hub `lmcache/vllm-openai` blocked) | about 0.2–0.5 GB | Must match the torch/vLLM ABI; build and smoke-test together. |
| SGLang (optional, later) | `pip install "sglang[all]"` (Docker Hub `lmsysorg/sglang` blocked) | about 5–8 GB | Not in the first phase. |
| DCGM exporter | `nvcr.io/nvidia/k8s/dcgm-exporter:<pinned>` | about 0.5–1 GB | Replaces or joins `nvidia_gpu_exporter`. |
| Valkey/Redis | `public.ecr.aws/docker/library/valkey` or `/redis:7.x-alpine` (ECR mirror of Docker official images) | about 15–40 MB | For LiteLLM shared state and the optional LMCache remote tier. |
| pgBackRest | apt inside a Postgres image FROM `pgvector/pgvector:0.8.0-pg16` (cached) | about 50 MB | Compose PITR. |
| Kubernetes later | GPU Operator (nvcr.io), CloudNativePG `ghcr.io/cloudnative-pg/*`, llm-d images (ghcr/quay, verify), KEDA (ghcr) | about 1–3 GB together | Only when going multi-server. |
| NCCL tests (bring-up) | build `nccl-tests` from GitHub source in the vLLM image | small | For `all_reduce_perf`. |

### Model weights (pick from these; sizes from HF metadata, 2026-10-16)
| Repo | Approx. download |
|---|---|
| `Qwen/Qwen3.8-Flash-Next-FP8` | 186 GB |
| `nvidia/Qwen3.8-Flash-Next-NVFP4` | 133 GB |
| `nvidia/Qwen3.5-397B-A17B-NVFP4-V2` | 244 GB |
| `Qwen/Qwen3-235B-A22B-Instruct-2507-FP8` (or `-Thinking-2507-FP8`) | 236 GB |
| `nvidia/Qwen3-235B-A22B-NVFP4` | 134 GB |
| `nvidia/Qwen3-Coder-480B-A35B-Instruct-NVFP4` | 273 GB |
| `deepseek-ai/DeepSeek-V4-Flash` | about 160 GB |
| `openai/gpt-oss-120b` (root safetensors only) | about 65 GB (whole repo 196 GB) |
| Keep: Qwen3.8-27B GGUF, FLUX.2 klein, Wan2.2, embedder (already present) | 0 |

A realistic first set is Flash-Next NVFP4, gpt-oss-120b, and one flagship. That is about **450–500 GB**.

---

## 7. Decisions you need to make

1. **Card edition and chassis.**
   - Server Edition (passive, 600 W) in a certified GPU server, or Max-Q (300 W blower) in a workstation chassis.
   - Both have the same 96 GB, but Max-Q gives roughly 15–25% less sustained throughput (estimate).
   - Confirm the PCIe topology: one socket, or a Gen5 switch, so P2P works.
2. **RAM:** 768 GB (floor) or **1 TB (recommended)** or 1.5 TB (heavy LMCache CPU tier).
3. **NVMe:** at least 4 × 7.68 TB Gen5 (library about 4 TB, KV disk tier 2–4 TB), plus a separate mirrored pair for Postgres, plus a backup target (NAS or S3).
4. **Layout and models** (§2): A (one flagship at TP=4), **B (throughput: Flash-Next TP=2 + gpt-oss + media/small; recommended)**, or C (coding).
   - Pick the flagship: Qwen3.5-397B NVFP4, Qwen3-235B FP8, Qwen3-Coder-480B NVFP4, or DeepSeek-V4-Flash.
   - DeepSeek-V3/R1-671B, GLM-5.1, Kimi and Maverick do not fit on 4 × 96 GB at usable speed.
5. **Precision policy:** FP8 first (robust), NVFP4 only after it passes the smoke test on your vLLM pin.
6. **KV tier:** vLLM CPU offload only (simple), or LMCache with a disk tier (warm cache across restarts, more moving parts).
7. **Single node vs. cluster:** compose on one box now, with Helm/Kubernetes (llm-d, CloudNativePG, GPU Operator) when a second server arrives. This decides how much of §5.6 to build now.
8. **Image source:** NGC `nvcr.io/nvidia/vllm` vs. a self-built image from PyPI (Docker Hub is blocked).
9. **MIG on one GPU** (hard isolation between media and LLMs) vs. GpuShare lending through sleep.
10. **SLOs:** target TTFT p95 and tok/s per user, and how many concurrent active users. These set `max-num-seqs`, the gate places, and the alert thresholds.

---

## 8. Ordered implementation plan (each commit shippable; tests use fakes, no new GPU needed)

1. **Fake vLLM for tests and dev.**
   - `tools/fake-vllm/` (Python stdlib): `/health`, `/v1/models`, streaming `/v1/chat/completions` with configurable TTFT and tok/s, `/metrics` using the real `vllm:*` names, `/sleep` and `/wake_up`, a load delay, and a `--fail` mode.
   - Plus `deploy/fake-gpu.yml`, a compose overlay that runs it as `vllm`.
   - Tests: a script self-test.
   - **Done**: `tools/fake-vllm/fake_vllm.py` (and a `vllm` wrapper with the same command line), `deploy/fake-gpu.yml`, `tests/deploy/test_fake_vllm.py`.
2. **Hardware fake and `GpuPlan`.**
   - Replace `Hardware.ImageReserve` (the GPU-0 assumption) with `GpuPlan`: everything resident per GPU (llama.cpp `Devices`, media `gpu`, embedder, future vLLM groups).
   - `KeptPlan` / `GpuUse` derive from it.
   - Tests: a fake 4 × 96 GB `Hardware`; layouts A, B and C validate, and over-commit is refused.
   - **Done**, vendor-neutral: `Models/GpuPlan.cs`; `GpuDevice` has a vendor, a backend and free memory; `Hardware.Reserves` puts the picture server on its own GPU (its model's, else `MEDIA_GPU`, else the first) and the embedder on `EMBED_GPU`; `HardwareProbe` reads Prometheus, else the hardware file `deploy/services/gpu-probe.py` writes (`nvidia-smi`, `rocm-smi`, or none), and `LLAMACPP_BACKEND=cpu` is a machine without GPUs; llama.cpp's presets name GPUs for its backend (`CUDA0`, `ROCm0`, `Vulkan0`, `SYCL0`, `MTL0`). Tests: `GpuPlanTests` over six fake machines (`FakeHardware`: CPU only, 1 × 24 GB, 2 mixed NVIDIA, 4 × 96 GB, 8 × 80 GB, AMD 2 × 192 GB), `tests/deploy/test_gpu_probe.py`.
3. **`EngineKind` on `LocalModel`** (EF migration; per your preference, no compatibility shim).
   - `ModelCatalog` writes `models.ini` only for llamacpp models. The other fields are listed in §3.3.
   - Tests: the catalog output for llama.cpp is unchanged.
   - **Done**: migration `EngineKinds`; `LocalModel` has `EngineKind`, `Engine` (the instance, for several machines), `Replicas`, `GpuMemoryUtilization`, `Quantization`, `SpecDecode`, `KvCpuGb`, `Sleep`, `Fallback`; `Parallel` is vLLM's `--max-num-seqs`, `KvType` its `--kv-cache-dtype`, `Devices` its GPU set. `Llm.Core/Models/EngineKinds.cs` says what each kind can do (llama.cpp, vLLM, and external servers, which the app registers but does not run); the API refuses a setting the kind lacks (`ModelAdvisor.KindProblems`). Tests: `EngineKindTests`.
4. **HF snapshot models in the library.**
   - `ModelDownloads` fetches safetensors, config and tokenizer (skipping `original/`, `metal/`, GGUF), showing the size first.
   - `ModelLibrary` recognises `hf/<org>/<repo>@rev`.
   - Tests: a fake HF server (the existing HuggingFace test pattern).
   - **Done**: `HuggingFace.SnapshotOf` (safetensors, JSON, tokenizer and template files; never `original/`, `metal/`, GGUF or other formats), the repository view's `snapshot` (size first), `snapshot: true` downloads into `hf/<org>/<repo>@<rev>`, `ModelLibrary.Snapshots()` / `Snapshot()` (whole or why not), `GET /api/admin/models/library/snapshots`. Tests: `HuggingFaceTests` with `FakeHuggingFace.SnapshotRepo`.
5. **`VllmAdvisor`.**
   - Weights bytes from the safetensors index; KV per token from `config.json` (full vs. linear vs. sliding layers, KV heads, head dim, MLA).
   - TP validity, recommended `max-model-len` and `max-num-seqs`, `gpu-memory-utilization`, and the RAM for the KV tier and sleep.
   - Tests: fixtures are the real `config.json` files of Qwen3.8-Flash-Next, Qwen3.5-397B, Qwen3-235B, gpt-oss-120b and DeepSeek-V4-Flash, with expected numbers from §2.
   - **Done**: `Models/VllmAdvisor.cs` reads full, sliding, linear (gated delta net state), MLA and compressed (DeepSeek V4, marked approximate) attention; checks TP against the attention and KV heads; keeps 3 GiB a GPU for vLLM itself and at least an eighth of a copy's share for its cache; **proposes GPUs for whatever machine it finds** (the smallest TP that fits, alike GPUs of a backend vLLM runs on, the most room first beside what is kept there), which a save keeps when the admin chose none; refuses GPUs that cannot hold it, a context that does not fit, memory kept for others; counts RAM for sleep and the CPU tier. Kept vLLM models are strict residents in the kept plan. Fixtures in `tests/Llm.Tests/Resources/vllm` (the six configs, with each index's total size). On four 96 GB cards: Flash-Next FP8 at TP=4 about 14M tokens, NVFP4 at TP=2 about 3.7M, Qwen3.5-397B at TP=4 about 7M and about 160 answers of 32K, Qwen3-235B at TP=4 about 1.2M, gpt-oss-120b on one GPU about 22 GiB of cache, DeepSeek-V4-Flash proposed at TP=4. (Figures are in GiB of the cards' 97,887 MiB; §2's are rounder.)
6. **vLLM supervisor.**
   - `deploy/services/vllm/supervisor.sh`: reconcile `/presets/vllm/models.json`; per-entry `CUDA_VISIBLE_DEVICES` and port; status files; drain on SIGTERM; backoff; secrets passed by environment (`VLLM_API_KEY`), never on the command line.
   - Tests: run it with `vllm` pointed at fake-vllm; add, remove, change and crash cases.
   - **Done**: `deploy/services/vllm/supervisor.sh` with `vllm-helper.py` (JSON and HTTP, standard library): per-model `CUDA_VISIBLE_DEVICES` and `HIP_VISIBLE_DEVICES`, non-blocking drains, backoff that grows through a crash loop and resets after five stable minutes or a change, sleep and wake from the desired state, a heartbeat and `hardware.json` (gpu-probe.py), and entries that put a key or the supervisor's own options on the command line refused. Runtimes: compose `vllm` service (profile `vllm`, `VLLM_IMAGE`), `podman.yml` (CDI), `rocm.yml` (AMD for both), `fake-gpu.yml` (the supervisor with fake-vllm, no GPU), Helm `vllm.enabled` / `vllm.instances` / `vllm.fake` / `gpuResource`, the `vllm-status` volume or claim, and the chart's copies (`check-chart.py`). Tests: `tests/deploy/test_vllm_supervisor.py`, `test_runtime_parity.py` with the vLLM profile and `vllm.enabled`.
7. **`IEngineBackend`.**
   - Extract `LlamaCppBackend` from `EngineClient`; add `VllmBackend` (desired state, status, health, metrics-based capacity).
   - `EngineWatcher` iterates the backends.
   - Tests: Testcontainers with fake-vllm.
   - **Done**: `IEngineBackend` (list, load, unload, sleep, capacity, health) with `LlamaCppBackend` (the router API, as before), `VllmBackend` (presets.json the app writes per instance, models.json the desired state, the supervisor's status and heartbeat, `/metrics`) and `ExternalBackend` (Other GPU servers: registered and checked, never managed). `EngineClient` is every engine as one: each model goes to the engine that lists it; llama.cpp failing fails the whole as before, a vLLM instance down only lists its models unloaded and says why. `Engine:Vllm` lists the vLLM instances (one named `vllm` by default; another machine is another entry with its host and shared folders); `Engine:LlamaCpp` false runs vLLM alone. The watcher writes each instance's presets (ports kept stable), Prometheus targets per vLLM process, and each model's place on the GPUs; vLLM models take none of llama.cpp's places (Models loaded at once). Tests: `VllmBackendTests` (unit, the engines as one, and the real supervisor with fake-vllm in a `python:3.13-slim` container).
8. **`EngineRoute` per GPU set.**
   - Room is made only among models sharing the target GPUs.
   - vLLM is "busy" from metrics, with no slot pinning (`TokenCache` for vLLM).
   - Tests: the existing EngineRoute tests, parameterised, plus multi-GPU cases.
   - **Done**: a vLLM model asked for is started by the app (its processes into the desired state, woken if asleep, waited for up to `Engine:VllmStartWait`, the chat told why it waits); its room is memory on its GPUs (`EngineRoute.Fits`, GpuPlan over the places the watcher keeps), made only by idle spare models on the GPUs it is short on, never from one busy by its metrics; llama.cpp's places (Models loaded at once) count llama.cpp's models only, in the route, the policy, an admin's Load and Keep. `TokenCache.OfVllm`: prefix caching, no pinned slots, the CPU tier, hit rate, cache use and queue from metrics. The existing llama.cpp route tests pass unchanged. Tests: `EngineRouteGpuTests`.
9. **Gateway registration.**
   - vLLM models go to LiteLLM with `api_base` per port, `max_parallel_requests` from `max-num-seqs`, and **fallbacks** per model.
   - Two replicas register as `name@i` with app-side conversation affinity.
   - Tests: the generated LiteLLM config, and a fallback when fake-vllm is down.
   - **Done**: each copy registers at `http://HOST:PORT/v1` with `max_parallel_requests` = its `--max-num-seqs`, the copies a pool under the model's name, and each copy under `NAME@i` too (`llm_app_replica_of`: in no list, not the chat's, not the keys' pickers); `llm_app_fallbacks` names the fallback. The chat keeps a conversation on one copy (`EngineRoute.Copy`, by its id, the same on every app replica), sends `fallbacks` with each request (LiteLLM's own, for a failure mid-answer), and goes to the fallback at once when the model cannot answer (its engine down, failed, no room); an API key is told which model answers in its place. LiteLLM router-level fallbacks for API keys (`router_settings.fallbacks`) are not written by the app: that needs LiteLLM's config API, left for the LiteLLM HA step. Tests: `VllmGatewayTests`, `LiteLlmClientTests`.
10. **`GpuShare` per GPU with sleep.**
    - Lending is scoped to the media server's GPU. A vLLM model on it **sleeps**, or unloads when sleep is off or fails; it wakes afterwards.
    - Tests: fake-vllm `/sleep` and `/wake_up`, and the media status fakes.
    - **Done**: a lending carries its GPUs (`GpuShare.GpusOf`: the media model's GPU, else `MEDIA_GPU`, else the first; every GPU when the machine is not known, as before); only models on them make room: a vLLM model that may sleep sleeps (kept loaded or not; a TP group sleeps whole), one that may not or whose sleep fails unloads unless kept, a llama.cpp model unloads unless kept; models on other GPUs keep loading and answering (`EngineState.Blocks`); when the job ends the sleepers wake and the unloaded kept or default ones load again. Tests: `GpuShareTests` (four GPUs, video on GPU 3).
11. **Admin → Models UI.**
    - Engine kind, GPU picker, vLLM options, per-GPU bars, cache hit rate, phase (loading, sleeping, draining).
    - Tests: Playwright against fake 4-GPU hardware and fake-vllm.
12. **Compose and Helm.**
    - `vllm` service (profile `vllm`, `shm_size`, `ipc: host` or a large shm, memlock, the `engine` and `vllm-status` volumes), and the `arena-vllm` Dockerfile with pinned vLLM (plus optional LMCache).
    - Helm `vllm` Deployment(s) with values `gpus`, `nodeSelector`, `shmSize`, `kvcache` PV; update `check-chart.py`.
    - Tests: `check-chart.py` and compose config lint.
13. **Metrics and alerts.**
    - The app writes vLLM targets to `file_sd`; optional DCGM exporter; alert rules (§5.7); dashboard panels.
    - Tests: `audit-dashboards.py`, and rule unit tests with `promtool test rules` if available.
14. **KV tier.**
    - `KvTier = none | cpu(GB) | lmcache(cpu GB, disk GB, remote)`. The app writes the `OffloadingConnector` config or the `lmcache-<model>.yaml`, and the advisor counts RAM and disk.
    - Tests: generated arguments and files; fake-vllm accepts them.
15. **Fair queue for API keys and engine priority.**
    - The guardrail admits through the gate; 429 with Retry-After; group priority passed to vLLM `priority`; shrink admission on waiting or KV pressure.
    - Tests: `scale-test.py` scenarios against fake-vllm.
16. **LiteLLM HA.**
    - Valkey service (ECR mirror image); `router_settings.redis_*`; LiteLLM replicas in `scale.yml`; Traefik health checks.
    - Tests: `scale-test.py` with 2 LiteLLM replicas; the limits stay global.
17. **Postgres PITR.**
    - pgBackRest in the Postgres image, WAL archive to `BACKUP_DIR`/NAS/S3, `backup.sh --pitr`, and a restore drill (`scripts/restore-drill.sh` restores to a timestamp in a scratch container and checks it).
    - Docs in `deployment.md` "Backups".
    - Tests: the drill runs in CI.
18. **Load-test extensions.**
    - `multiuser-bench.py` `--ramp`, `--prefix-share`, `--return-after`, `--kill-engine`; `scale-test.py` at 50, 100 and 300 users; JSON baselines.
    - Tests: CI on fake-vllm. Real numbers come on the hardware.
19. **Hardware bring-up runbook and check.**
    - `deploy/scripts/gpu-check.sh`: driver ≥ R580, `nvidia-smi topo -m`, a P2P test, NCCL `all_reduce_perf`, memlock and shm, power caps, MIG state.
    - `docs/deployment.md` "Multi-GPU server" section; `docs/architecture.md` engines and GpuPlan.
    - **Needs the hardware:** run it, then a smoke test of each chosen model on the pinned vLLM (FP8 first, then NVFP4), then step 18 for real numbers. Tune TP=2×2 vs. TP=4 and the KV tier from those results.
20. **Later, multi-server.**
    - Helm: one Deployment per engine group, GPU Operator, CloudNativePG, llm-d scheduler, KEDA autoscaling, per-node weight staging; SGLang backend if a model needs it.

Commits 1–18 can be built and tested on today's 3090 box: llama.cpp keeps running there, and vLLM is the fake. Commit 19 is the first that needs the new server.

---

## Repo facts this design builds on
- Multi-GPU support already in the code:
  - `src/Llm.Api/Models/ModelAdvisor.cs`: `Hardware.On`, `ParseDevices`, `GpuUse`, `KeptPlan`.
  - `src/Llm.Api/Models/ModelCatalog.cs:538`: `device = CUDA…` per model.
  - `deploy/services/sd-serve.sh`: `gpu=N`.
- Assumptions that change:
  - **GPU 0 holds the media servers**: `Hardware.ImageReserve` in `ModelAdvisor.cs`.
  - **One engine URL**: `Models/Engine.cs` `EngineOptions.Url`.
  - **One GPU lent for media**: `Models/GpuShare.cs`.
- Engine and gateway control is done through files, with no Docker socket: `deploy/services/llamacpp/router.sh`, and `EngineOptions.ConfigDir` holding the presets, keep list, max and Prometheus targets.
- Fair queue: `src/Llm.Api/Chat/AnswerGate.cs`. Replicas: `src/Llm.Api/Operations/Replicas.cs` (advisory lock and LISTEN/NOTIFY). Gateway: `deploy/config/litellm.yaml` (`least-busy`, guardrail `fail_closed`, no Redis today).
- Compose: `deploy/docker-compose.yml` (no Redis, `nvidia_gpu_exporter`, `pgvector:0.8.0-pg16`), `deploy/scale.yml`. Helm: `deploy/helm/argus-arena/templates/engines.yaml` and `values.yaml`.
- Load tests: `deploy/scripts/scale-test.py` and `deploy/scripts/multiuser-bench.py`.
- Live host: RTX 3090 24 GB, driver 595.99.02, 61 GB RAM, 24 threads, 1.3 TB free on NVMe.

## Sources
- [NVIDIA RTX PRO 6000 Blackwell Server Edition datasheet](https://www.akamai.com/site/en/documents/corporate/nvidia-rtx-pro-6000-blackwell-server-edition-datasheet.pdf) · [PNY RTX PRO 6000](https://www.pny.com/nvidia-rtx-pro-6000-blackwell) · [IT Creations spec page](https://local.itcreations.com/nvidia-gpu/nvidia-rtx-pro-6000-blackwell-server-edition)
- [vLLM forum: SM120 (RTX PRO 6000) NVFP4 MoE report, Qwen3.5-397B, 2026-04-11](https://discuss.vllm.ai/t/sm120-rtx-pro-6000-nvfp4-moe-performance-report-qwen3-5-397b/2536)
- [GLM-5.1 NVFP4 on 6× RTX PRO 6000 (HF discussion)](https://huggingface.co/lukealonso/GLM-5.1-NVFP4/discussions/4) · [Laguna NVFP4 on RTX PRO 6000 issues](https://huggingface.co/poolside/Laguna-S-2.1-NVFP4/discussions/3)
- [vLLM releases (v0.31.0, 05 Oct; CUDA 13.0 images)](https://github.com/vllm-project/vllm/releases) · [vLLM KV connectors / OffloadingConnector](https://docs.vllm.ai/en/latest/features/disagg_prefill.html) · [vLLM + llm-d](https://docs.vllm.ai/en/latest/deployment/integrations/llm-d.html)
- [LMCache releases (v0.5.6, 15 Oct)](https://github.com/LMCache/LMCache/releases) · [vLLM production-stack KV offloading tutorial](https://docs.vllm.ai/projects/production-stack/en/vllm-stack-0.1.6/tutorials/kv_cache.html)
- [SGLang HiCache blog](https://lmsys.org/blog/2025-09-10-sglang-hicache) · [HiCache design docs](https://docs.sglang.io/docs/advanced_features/hicache_design)
- [NVIDIA Dynamo native KV offloading with vLLM](https://docs.nvidia.com/dynamo/knowledge-base/modular-components/backends/v-llm/native-kv-offloading) · [Dynamo v1.2.0 release](https://docs.nvidia.com/dynamo/reference/releases/v1-2-0)
- [llm-d CNCF sandbox (2026)](https://toolhalla.ai/blog/llm-d-cncf-sandbox-kubernetes-llm-inference-2026)
- [NGC vLLM container 26.09-py3](https://catalog.ngc.nvidia.com/orgs/nvidia/-/containers/vllm/26.09-py3) · [NGC vLLM release notes](https://docs.nvidia.com/deeplearning/frameworks/vllm-release-notes/)
- [Google G4 VMs: P2P fabric for RTX PRO 6000](https://cloud.google.com/blog/products/compute/g4-vms-p2p-fabric-boosts-multi-gpu-workloads/) · [Multi-GPU inference without NVLink (WZ-IT)](https://wz-it.com/en/knowledge/ki/multi-gpu-llm-inference/)
- [DeepSeek-V4-Flash specs (CometAPI)](https://www.cometapi.com/models/deepseek/deepseek-v4-flash/) · [GLM-5.1 vs Kimi K3 (llm-stats)](https://llm-stats.com/models/compare/kimi-k3-vs-glm-5.1) · [Qwen3.8 lineup (codersera)](https://codersera.com/blog/qwen-3-8-model-lineup-2026/amp/) · [nvidia/Qwen3.5-397B-A17B-NVFP4](https://huggingface.co/nvidia/Qwen3.5-397B-A17B-NVFP4)
- Model sizes and architectures: Hugging Face API metadata and `config.json` of the repos named in §2 and §6, queried 2026-10-16. These are secondary-source caveats: the aggregator sites above are not official model cards.
