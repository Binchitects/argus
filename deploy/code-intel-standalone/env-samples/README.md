# Deployment samples

Each `.env` here is a complete, working deployment for one model on one card.

| sample | model | card | status |
|---|---|---|---|
| `qwen3.8-flash-next.rtx5090.env` | Qwen3.8-Flash-Next, 177B MoE, UD-IQ4_XS | RTX 5090 32 GB + 64 GB+ RAM | measured |
| `qwen3.8-27b.rtx5090.env` | Qwen3.8-27B, dense, NVFP4 with MTP | RTX 5090 32 GB | measured |

## Using one

From `deploy/argus-standalone/`:

```bash
cp env-samples/qwen3.8-flash-next.rtx5090.env .env
```

Make the secrets. Every empty value under `SECRETS` has a comment saying how to
make it; this fills them all at once:

```bash
awk '/^# SECRETS/{s=1} /^# APP/{s=0} s && /^[A-Z0-9_]+=$/{c="openssl rand -hex 24"; c|getline r; close(c); if ($0 ~ /^LITELLM_/) r="sk-" r; $0=$0 r} {print}' .env > .env.new && mv .env.new .env && chmod 600 .env
```

Set `ARGUS_ADMIN_EMAIL`, and `LLAMACPP_MODEL_DIR` to a directory on NVMe, then:

```bash
docker compose up -d
```

No sample ever contains a secret. A sample in a public repository would give every
deployment the same passwords.

## Adding a new setup

A setup is one file; there is nothing to register.

1. **Copy the closest sample** and name it `<model>.<card>.env`, lowercase, e.g.
   `qwen3.8-27b.rtx4090.env`. Start from a MoE sample for a MoE model and a dense one
   for a dense model.

2. **Edit the header** -- these five lines are how a reader tells setups apart:

   ```
   # TITLE: <model> on <card>
   # HARDWARE: <card and VRAM>, <RAM>, NVMe
   # DOWNLOAD: <what the first start downloads, and how big>
   # MEASURED: <tok/s and context, once you have measured it; delete the line until then>
   # STATUS: <"Measured on this hardware." or what it was derived from>
   ```

3. **Edit only the MODEL block** (and HARDWARE for a different machine).

   | setting | how to choose it |
   |---|---|
   | `MODEL_NAME` | the model's official name, e.g. `Qwen3.8-27B`. Clients see only this. |
   | `LLAMACPP_HF_REPO`, `LLAMACPP_HF_FILES` | the Hugging Face repo and the file paths inside it. Split GGUFs list every part. |
   | `LLAMACPP_MODEL_FILE` | the file name; for a split GGUF, the first part. |
   | `LLAMACPP_N_CPU_MOE` | MoE only; `0` for dense. Start high (all layers in RAM), lower by 2 until loading fails with CUDA out of memory, then go back up 2. |
   | `MODEL_CONTEXT` | weights + KV cache + compute buffer must fit in VRAM. If loading fails, lower `-ub` in `LLAMACPP_EXTRA_ARGS` first, then the context. |
   | `LLAMACPP_MTP_DRAFT_MAX` | `2` for a dense model with an MTP layer (27B: +50%). `0` for a MoE model whose experts run on the CPU (Flash-Next: slower with MTP). Measure both. |
   | `LLAMACPP_PARALLEL` | people served at once. Keep `--kv-unified` so they share one context pool. |

   Leave `LLAMACPP_ENGINE_URL` empty unless the model needs a llama.cpp build newer
   than the image; it downloads and runs that build instead.

4. **Leave everything outside the MODEL block alone**, except `GPU_POWER_LIMIT_W` if
   the card needs a different cap. Never put a value under `SECRETS`.

5. **Check it resolves.** Compose names any required value that is missing:

   ```bash
   docker compose --env-file env-samples/<yours>.env config --quiet
   ```

6. **Run it and measure it** before calling it measured:

   ```bash
   docker compose up -d
   ```

   ```bash
   make smoke
   ```

   then measure tokens per second for one and for two people at once (llama.cpp
   prints `eval time ... tokens per second` for every request in `make logs S=llamacpp`),
   and a long prompt's read time. Put the result in the `MEASURED` line and change `STATUS` to
   `Measured on this hardware.`

7. **Add it to the table** at the top of this file and in the top-level README.
