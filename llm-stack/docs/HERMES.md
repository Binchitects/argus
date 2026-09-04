# Pointing Hermes at this stack

Two independent halves: **the model**, served by the gateway, and **Argus**,
an MCP server for code and documentation lookup. Either works without the
other.

Hermes' config lives at:

| | |
|---|---|
| Windows | `%LOCALAPPDATA%\hermes\config.yaml` |
| Linux/macOS | `~/.config/hermes/config.yaml` |

## The model

```yaml
model:
  base_url: https://gateway.llm.localhost/v1
  api_key: sk-...                 # YOUR key, from llm-users.sh
  name: local                     # or the served name, e.g. qwen3.8-27b
  context_length: 131072          # see "How much context you can have" below
```

**Use your own key, not `LITELLM_MASTER_KEY`.** The master key is a superuser
credential with no budget: usage through it is attributed to nobody and
bounded by nothing, which quietly defeats the per-person accounting the whole
gateway exists for. Mint yours with:

```bash
./scripts/llm-users.sh --apply       # prints each person's key once
```

**Prefer `local` over the checkpoint name.** `local` is an alias that follows
whatever the engine is serving, so `switch-model` does not strand your config
pointing at a model that is no longer loaded.

## Hermes needs 64K, and that decides the engine

Hermes refuses to start on a model with a window under 64,000 tokens:

```
Model local has a context window of 24,576 tokens, which is below the
minimum 64,000 required by Hermes Agent.
```

Two separate things have to be true, and each failed here first.

**1. The engine must really serve it.** On a 24 GB card, this model under
vLLM cannot reach 64K at all. Measured rather than assumed:

| | weights | KV cache at util 0.94 |
|---|---|---|
| AWQ INT4 safetensors (vLLM) | 19.6 GB | **27,534 tokens** |
| Q4_K_M GGUF (Ollama) | 17.5 GB | **65,536 tokens**, 22.4 GB total |

Same model, same 4-bit precision — GGUF simply packs ~2 GB smaller, and that
2 GB is the entire difference between a 22K window and a 64K one. No vLLM
setting closes a 2.4x gap: `--cpu-offload-gb 4` did free the memory (KV rose
to 152,917 tokens) but the quantized kernels then died with `Pointer argument
cannot be accessed from Triton (cpu tensor?)`.

So **the gateway fronts Ollama for this model**. Ollama runs on the *host* and
claims the same card, so **vLLM must be stopped** — they cannot share it. vLLM
is still the better engine when a 22K window is enough; it batches far better.

The 64K is requested per call by the gateway (`num_ctx` in
`config/litellm/config.yaml`), *not* by `OLLAMA_CONTEXT_LENGTH`. A server-side
default depends on how Ollama happened to be launched and is lost on a reboot,
which would silently drop every client to 4096.

### How much context you can have

Past 64K the limit moves again, and the lever is the **precision of the KV
cache** rather than the weights. This model is a hybrid attention/SSM design
(`qwen35.ssm.*`): most of its 65 layers keep a fixed-size state and only a
minority grow a KV cache with context, which is why its context is far cheaper
than a plain transformer of the same size.

Measured on a 24 GB card, largest window that stays **entirely** on the GPU:

| KV cache | max context | VRAM | headroom |
|---|---|---|---|
| `f16` | 65,536 | 22.4 GB | 131,072 spills 27% to system RAM |
| `q8_0` | 114,688 | 22.8 GB | 122,880 already spills. Near-lossless |
| **`q4_0` (shipped)** | **131,072** | **21.9 GB** | **~2.6 GB** |
| `q4_0` + `num_gpu` | 262,144 — the model's **full** window | 24.2 GB | ~370 MiB |

**The full 262,144 does fit**, which the arithmetic alone would not tell you:
the KV cache costs 44 MiB per 1K tokens at `q8_0`, so a full window is 22.0 GiB
at `f16`, 11.0 at `q8_0` and 5.5 at `q4_0` — and only the last leaves room for
17.5 GB of weights. It needs `num_gpu` to override Ollama's estimator, which
otherwise offloads ~15% of the layers even though the total fits.

**It is not the default, because ~370 MiB of headroom is one browser away from
an OOM**, and forcing `num_gpu` removes the graceful spill that would otherwise
absorb that. 131,072 loads unaided with about 2.6 GB spare. To take the full
window anyway:

```bash
OLLAMA_CONTEXT_LENGTH=262144 ./scripts/start-ollama.sh
# and set num_ctx: 262144 plus num_gpu: 99 in config/litellm/config.yaml
```

Everything here was verified **by retrieval, not by loading** — the engine will
happily load a window it then spills. At 262,144, two facts placed at 25% and
75% depth of a 201,397-token prompt both came back correctly; at 131,072, a
fact in the middle of a 114,123-token prompt came back through the gateway.

**Two things silently degrade instead of failing.** Both cost time here:

- Start Ollama **only** via `scripts/start-ollama`. `OLLAMA_KV_CACHE_TYPE` is
  server-level environment, so an ordinary `ollama serve` gives an `f16` cache
  — and the gateway still asks for 131,072. Nothing errors; a quarter of the
  model moves to system RAM and generation crawls.
- Ollama **truncates** a prompt longer than the window rather than refusing it.
  A 245K-token test document came back with `prompt_eval_count` of 57,346 and
  the model correctly reporting it could not find the fact — which looks
  exactly like a retrieval failure. Assert `prompt_eval_count` in any
  long-context test.

**2. Hermes must be able to LEARN it.** This is where the time actually went.

The gateway used to report `max_input_tokens: null`, so Hermes probed the
window itself and **wrote the answer to disk**:

```
~/AppData/Local/hermes/context_length_cache.yaml
  local@https://gateway.llm.localhost/v1: 24576
```

That entry was correct when vLLM served 24,576. Nothing invalidated it when the
engine changed, so Hermes kept refusing a 131,072 engine while quoting a number
that existed only in its own cache. Learn the shape of this failure: **the
error names a limit no live component has.**

Two fixes, and both belong in place:

* The gateway now **advertises** the window (`model_info.max_input_tokens` in
  `config/litellm/config.yaml`). That is what stops any client — Hermes,
  Open WebUI, an SDK — from having to guess and then cache the guess.
* `model.context_length: 131072` in Hermes' own config, as belt and braces.

Set the override only when it is **true**. Declaring 64K while the engine
served 22.5K made every prompt fail with `Context length exceeded (22 tokens)`
— a message that points at the prompt rather than at the lie in the config.

**A stale model NAME does the same thing.** If `providers.<name>.models` lists
a checkpoint the gateway no longer serves — `Qwen3.8-27B-AWQ-MTP`, say — Hermes
offers it, cannot resolve a window for it, and refuses with the same message
naming that model. Keep the list equal to what `/v1/models` returns.

## Argus

```yaml
mcp_servers:
  argus:
    url: https://argus.llm.localhost/mcp
    headers:
      Authorization: Bearer <your GitLab personal access token>
```

Argus resolves every request's identity against GitLab, so the token is a
GitLab PAT with `read_api` — **your own**, not the indexing service token.
What you can see through Argus is exactly what you can see in GitLab.

## Three things that will waste your time

**Proxy exclusions.** If `HTTP_PROXY`/`HTTPS_PROXY` are set, `NO_PROXY` must
include the stack's domain or requests are sent to a corporate proxy and fail
with a bare "Connection error" that names nothing:

```
NO_PROXY=localhost,127.0.0.1,::1,.local,llm.localhost,.llm.localhost
```

**Name resolution.** `*.localhost` resolves in browsers but **not** in curl,
SDK clients, or Hermes. Run `./scripts/setup-hosts.sh` once on the client
machine.

**Ollama does not restart itself.** It is a host process
(started by `scripts/start-ollama`), not a Windows service, and has no entry under
`HKCU:\...\CurrentVersion\Run` on this machine — so after a reboot the
gateway advertises models that nothing is serving, and every request fails at
the engine rather than at the gateway. Start it before the stack, or add it to
your login items.

**Killing Ollama leaks its runners.** `Stop-Process -Force` on `ollama.exe`
does not reap the `llama-server.exe` children, and each orphan keeps a whole
copy of the model in VRAM. Three of them pinned this card at 24.1 GB of
24.6 GB, and the only visible symptom was benchmark numbers that made no
sense — a 128K window spilling *more* than a 256K one. `start-ollama.ps1`
reaps them; by hand, kill `llama-server` too.

**Hermes needs the stack CA in its own bundle.** `SSL_CERT_FILE` and
`REQUESTS_CA_BUNDLE` point at `~/AppData/Local/hermes/ca-bundle.pem`, which
ships ~120 public roots and knows nothing about a local CA. Without the stack's
CA appended, every call fails as a bare **"Connection error"** that names
neither TLS nor the certificate:

```bash
cat llm-stack/config/traefik/certs/ca.crt >> ~/AppData/Local/hermes/ca-bundle.pem
```

This hides behind the context check, which runs first and needs no network —
so a stale context cache masks a broken bundle until you fix the cache.

**Hermes caches MCP schemas.** After changing anything about a tool server,
clear it or you are testing the previous description:

```
%LOCALAPPDATA%\hermes\cache\mcp_schema_cache.json
```

That cache cost an afternoon here: a tool description change appeared to have
no effect, because the model was still being handed the old text.

## Checking it works

```bash
# the model, through the gateway, with your key
curl https://gateway.llm.localhost/v1/chat/completions \
  -H "Authorization: Bearer sk-..." -H 'Content-Type: application/json' \
  -d '{"model":"local","messages":[{"role":"user","content":"hi"}],"max_tokens":16}'
```

Then confirm the call landed on **you** — Grafana → **Usage by person**, or:

```bash
curl "https://gateway.llm.localhost/user/info?user_id=you@example.com" \
  -H "Authorization: Bearer $LITELLM_MASTER_KEY"
```

If spend did not move, the request was attributed to nobody — almost always
the master key in `api_key`, or a repeated prompt served from cache.

## What the local model can and cannot do

Tool calling needs a model that can hold context across several tool calls and
choose sensibly. Open WebUI's own documentation names current frontier models
as a reasonable minimum and says small models will not manage it. An 8B will
struggle to drive Argus well; the 27B is better but still not a frontier
model. If Argus tool use disappoints, that is a model limit rather than a
wiring fault — check the wiring with the curl above before concluding
otherwise.
