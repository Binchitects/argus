# Admin, usage and cost

Everything an operator does happens in the app at `https://DOMAIN`. The
**Admin** area needs the admin role; **Usage & cost** and **Your account** are
for everyone, as is **Connect your tools** (each person's API key and the
setups for their tools).

People are a table with filters, bulk actions (credit, disable, enable, sign
out) and a page each. The audit log has filters, paging and CSV export. The
Settings page edits everything ([settings.md](settings.md)), and the Model page
has **Switch to this model**.

Sign-in, people, the company directory and 2FA are in
[authentication.md](authentication.md), and the chat in [chat.md](chat.md). This
page covers the rest.

## Admin

| Page | What it is for |
|---|---|
| **Overview** | Services up, people and admins, total spend, who is at or past their credit, and the code index's health. When the index is stale it says how many repositories, which ones, and *why* when the last run's exit code tells (GitLab unreachable, a token that cannot list every repository, ctags missing). |
| **People** | Add, search, and per person: credit, a new API key, password and 2FA resets, admin role, disable, sign out everywhere, delete. **Export CSV** downloads everyone with spend and credit left. |
| **Groups** | App groups (the people you add) and directory groups (whoever the company directory puts in them, by name or DN). Tools and models are given to groups. |
| **Tools** | What the chat's model may call: Argus, Python (the sandbox), the web (off until you turn it on and allow sites), image generation, the calculator, date and time, reading long files in parts, the canvas (documents and code beside the chat, changed by the model part by part), questions for the person (the model asks with choices instead of guessing), sub-agents (the model splits a task into parts done side by side), and the MCP servers and APIs you add. An **API** is added by its OpenAPI 3 document (JSON or YAML, pasted or fetched from its address): each operation becomes a function, its parameters and JSON body the arguments, and a call that changes something (POST, PUT, PATCH, DELETE) always asks the person first. **Read it** lists the operations before you add it. Per tool: on or off, who may use it (everyone, admins, or chosen groups), on in new chats, ask before each call. An MCP server is tested before it is added; its key is stored encrypted and never shown. A server whose tools run long can have its own **Longest call** (up to 24 hours; otherwise **Settings → Chat → Longest tool call**, an hour). |
| **Models** | Every model at the gateway. The engine's models load and unload with one click (one at a time on one GPU); more are added from the model library on the host. Per model: who may use it, in the chat and with API keys. See [Models](#models) below. |
| **Deployment** | The `.env` model the engine starts with (file, context, longest reply, multi-token prediction, thinking presets, power limits, prices) and every shipped sample with the exact `.env` block to paste to switch to it. |
| **Indexing** | The Argus code index: which repositories GitLab lists are indexed and which branches of each (with each branch's latest commit), every indexed branch at its commit (hash, message, when), **Update** per repository, the reindex schedule (in words or cron, in a time zone), the GitLab push and merge webhook (its secret, shown once, the steps for GitLab, and the last deliveries), and a run's progress (a percentage overall and per repository) and log. See [Choosing what is indexed](argus/README.md#choosing-what-is-indexed). |
| **Packs** | Knowledge packs, loaded like the models: the **pack library** (built packs on the host, the repository's `packs/` or `ARGUS_PACK_LIBRARY_DIR`) lists each with **Load** and **Unload**. Loading links the pack into Argus, instantly and without a copy, and only a pack built with Argus's embedding model loads (another's vectors would not compare). A pack not in the library can still be installed from a URL (with its SHA-256), updated from the published index, and removed. |
| **Explore** | What Argus holds, searched across every repository: **symbols and paths**; **references**, every line where a name is used, the definitions marked (as `find_references` answers a model); **code**, words in the files (`"a phrase"`, `prefix*`); and the **documentation** packs by their words, an API's name, or meaning. Each result opens: a file at its line, or a page of a pack. For "a tool found nothing: is it absent, named differently, or never indexed?". |
| **Monitoring** | Live probes of the gateway, Prometheus, Alertmanager, Loki and Argus, and links to Prometheus's and Alertmanager's own pages. |
| **Dashboards** | Ten dashboards, drawn by the app: the model and the gateway (LLM Overview, Stack Performance), the machine (Resources, GPU Hardware, Host & Containers), the services (Stack Health & Alerts, Logs), Argus and its index, and usage by person. See [Dashboards](#dashboards) below. |
| **Logs** | Every service's logs from Loki, newest first: by container, level (all, warnings and errors, errors) and text, over five minutes to a month, and **Live** for new lines as they are written. The filters are in the address, so a view can be linked. |
| **Alerts** | What fires now (from Alertmanager, with silenced ones marked), every time an alert fired over a day, a week or a month, and every rule with its state, severity, how long its condition must hold, and its query. |
| **Settings** | Every setting, grouped and searchable: applied at once, or by a restart the app does itself. See [settings.md](settings.md). |
| **Audit log** | Every sign-in and every change to people or the index, with who, whom and from where. |
| **Sign-in** | Local accounts and the company directory; "Check the directory now". |

Indexing, Packs and Explore talk to Argus through the app's server with
`ARGUS_KEY`, which never reaches a browser. With Argus left out they say
that it is not set up, and how to turn it on.

### Models

llama.cpp runs in **router mode**: one server that knows several models and
holds up to **Models loaded at once** (under Settings, default 1) of them. Loading and unloading is an API call, not a
restart. The page opens with **the engine**: how many it holds, which are kept
loaded, and how full each GPU would be with them.

- **Keep loaded** (a switch on each engine model) pins a model: it loads now,
  loads again when the engine starts, and comes back whenever it is not loaded.
  At most **Models loaded at once** can be kept. Keeping one is refused when their
  caches and buffers together cannot fit the GPUs and RAM; when they fit only
  by putting layers in RAM (two on one GPU that holds one, say), it is kept
  with that warning. The list is `config/engine/keep`, which the engine reads
  when it starts; without it, the `.env` model is kept.
- **Loaded on request.** While a place is left beside the kept models, any other
  model loads when someone asks for it: a chat, an API key, a coding agent. Its
  first answer waits while it loads. At the limit, the model used least
  recently unloads first; when that is a kept one, it comes back, and the one
  not kept makes room. In the chat such a model reads **Loads when asked**.
  When every place is kept, no other model loads on request, and the chat says
  so. A change of the kept list that flips this restarts llama-server (the kept
  models load again, one after another).
- **Working hours** change the kept models by day and hour: a small fast model
  in busy hours, the big one at night. Each has days, a time from and until
  (until before from runs past midnight; the same is all day), the models kept
  loaded meanwhile (at most **Models loaded at once**, checked against the GPUs and
  RAM as pinning is), and the model new chats start on. While one is in force,
  its models are kept instead of the pinned ones: they load, and the models only
  the previous window kept unload to make room. When it ends, the pinned models
  are kept again. Where two overlap, the one listed first applies. Times follow
  **Settings → Model → Time zone of working hours** (an IANA name; default UTC).
  The engine summary says which are in force and until when; a model they keep
  reads **Kept by working hours**, and cannot be unloaded by hand meanwhile.
  Chats that chose a model keep it; only new ones (and chats that chose none)
  start on the working hours' model.
- **Find on Hugging Face** searches GGUF models (most downloaded first), opens
  one to list its files grouped as models (a split model's parts together, by
  quantisation, vision projectors apart) with their sizes, whether each fits
  this machine (the GPUs, GPUs and RAM, or too big) and whether it is in the
  library already, and downloads the ones ticked into the library
  (`<library>/<owner>/<repository>/`). **Downloads** shows each one's progress,
  speed and time left; one runs at a time, a paused or cut off one goes on from
  where it got to (HTTP range), every file is checked against Hugging Face's
  SHA-256 and only then renamed into place, so the library never lists half a
  model. A finished one has **Add as a model**, which opens the form with its
  file chosen. The app needs the library writable by its user (`LLM_UID`) and,
  for gated repositories, `HF_TOKEN` of an account that accepted their terms.
  A download is refused when the disk would keep less than 2 GB free.
- **Load** loads a model now, beside the kept ones (refused when every place is
  kept). **Unload** unloads it, and stops keeping it. Answers wait while a
  model loads: seconds when its weights are in the page cache, minutes from
  disk.
- **GPUs.** With two or more GPUs, a model's form asks which it runs on: all of
  them (llama.cpp splits it by layer), or some (`device = CUDA0,...` in its
  preset). The engine numbers GPUs as `nvidia-smi` does
  (`CUDA_DEVICE_ORDER=PCI_BUS_ID`). The memory estimate uses only the GPUs
  chosen, and the image server's share only on the first. Two models kept side
  by side run best on different GPUs. The `.env` model runs on all of them
  unless `LLAMACPP_EXTRA_ARGS` has `--device CUDA1`.
- **Other GPU servers.** Another machine's OpenAI-compatible engine (llama.cpp,
  vLLM, SGLang, another gateway) serves models beside this one's. **Add a
  server** takes its address (usually ending in `/v1`) and its API key,
  **finds its models** (with the window each reports), and the admin chooses
  which the gateway serves, under which names, with their context, longest
  answer, tools, thinking and images. The app registers them in LiteLLM with
  the server's address and key; people use them from the chat, their API keys
  and agents, with the same access rules and spend as any model.
  - A name the gateway has already (a model here, or on another server) makes
    it **a second copy** of that model: LiteLLM spreads requests between them,
    and the chat can use the model while this machine's copy is not loaded.
  - The key is kept encrypted with the app's key ring (`APP_KEY`) and never
    shown again; leave it empty when editing to keep it.
  - **Health.** Each server's card says whether it answers (checked every 30
    seconds when the page asks), why not, and which chosen models it no longer
    lists. A server must answer to be added.
  - **https.** Its certificate is checked against the public roots and the
    stack's bundle, so a server signed by a private CA is trusted once that CA
    is in `config/ca` (then `docker compose up -d`). Only for a self-signed
    server with no CA to trust, **Check its certificate** can be turned off for
    that server.
  - The gateway is brought in step every minute, so a gateway that was down
    or restarted catches up by itself.
- A model that **could not load** (an incomplete download, a file this
  llama.cpp cannot read, too little GPU memory) says so on its card; the reason
  is in the engine's log (`docker compose logs llamacpp`). It is not tried
  again on its own; when every kept model failed, the `.env` model is loaded
  in their place, and the chat works on.
- **Add a model** picks a GGUF from the **model library** (`LLAMACPP_LIBRARY_DIR`,
  searched three levels deep; a split model is listed once, by its first part).
  **Each file is read for what it is** (its header and tensor table, not its
  name): a dense or mixture-of-experts language model, with full, hybrid
  (a cache in some layers only), sliding-window or recurrent attention; or an
  embedding model, reranker, vision projector, draft head, image model or LoRA
  adapter. Only language models can be added (the engine serves chat models;
  the others belong to Argus, the image server or a model's own settings), so
  the rest are listed with what they are for. A split model with a
  part missing is refused as incomplete.
- **The form asks what that kind of model has, within its limits**, and shows
  them: the context from 4,096 to what it was trained for (up to 4× with YaRN,
  when its file does not stretch it already); the longest answer, at most the
  context less 1,024 tokens for a prompt; the cache types its attention heads
  can take (a quantized cache needs flash attention, which covers heads of 64,
  80, 96, 112, 128 and 256); experts in RAM only for a mixture of experts;
  drafting with multi-token prediction only for a model with its own
  prediction layer or a draft head made for it (same architecture, width and
  vocabulary); a vision projector only one made for its width; thinking and
  tools only when its chat template has them. The API runs the same checks on
  save.
- **It starts from what fits this machine.** The GPU memory and RAM come from
  Prometheus (the `smi` profile's exporter), less what the image server may take
  (`IMAGEGEN_MAX_VRAM`) and the 1 GiB llama.cpp keeps free. The form estimates
  weights, cache and buffers as it is filled in (within 1% of llama.cpp's own
  estimate for dense and hybrid models, measured), and recommends the largest
  context that keeps the model on the GPU (a mixture of experts: its experts in
  RAM as needed), 1,024-token prompt steps when experts are in RAM, and MTP for
  a dense model that fits. **Automatic placement** (the default) leaves the
  layers to llama.cpp's fit, which places them for the memory free when the
  model loads; by hand, a setting that would not fit is refused. A GPU power cap
  far below the card's own (`GPU_POWER_LIMIT_W`) is named for a model that runs
  all on the GPU: it is what limits one.
- The added models share one cache between the answers in parallel
  (`kv-unified`, as the `.env` model): one conversation can use all of the
  context. They run with `LLAMACPP_THREADS`. Sampling left empty is the model's
  own recommendation from its file, which the engine applies.
- **More engine options** are `key = value` lines with llama-server's long
  option names. Options the app or the engine owns (files and paths, the port,
  the key), those the form sets, and names this engine does not know are
  refused: a preset with an unknown option stops the whole engine. The known
  names are this llama.cpp build's (`src/Llm.Api/Models/engine-options.txt`,
  with the command that regenerates it after an engine upgrade). Should the
  engine refuse the list anyway, it serves the `.env` model alone until the list
  changes, and the models it left out say so on their cards.
- **Adding, editing or removing a model restarts the engine** so it reads the
  new list: a few seconds, then the loaded model loads again. The gateway
  learns of the change at once (or within a minute, if it was down).
- The `.env` model is always there; change it under Deployment. Image models
  and cloud models are listed too, for who may use them.
- **Who may use it**: everyone, admins, or chosen groups (admins always may).
  The chat lists only the models a person may use, and the server refuses the
  others. API keys follow within a minute: a key's model list at the gateway is
  kept to the models its owner may use, so a call to another answers 403.
- People choose among the loaded models. One that is not loaded is listed
  greyed out, "Not loaded now".

The app writes `config/engine/models.ini` (the models added here),
`config/engine/active` (the model to keep loaded) and
`config/engine/targets.json` (which model Prometheus scrapes). The engine and
Prometheus read them; nothing else does.

### What the admin area deliberately does not do

**It does not recreate containers.** Admin → Models loads and unloads models
through the engines' own APIs (llama.cpp's router, the speech server's) and the
control files the picture and video servers read, which need no Docker socket.
`.env` holds only what the stack needs to start; a change there is
`docker compose up -d` on the host. A Docker socket in a web app is root on
the host for anyone who reaches it.

**It shows no secrets**, not even masked: a masked value still leaks its length
and first characters into every screenshot. A secret can be replaced, never
read back.

**Prices are each model's** (Admin → Models); a new person's credit is in
`config/litellm.yaml`, and each person's own under People.

## Usage & cost

**Everyone** (admins) is the "Usage by person" dashboard: people active,
requests, tokens, spend, how much is attributed to a person, requests made with
the master key; per-person and per-key tables; tokens per day by person and by
surface (API or chat); budget headroom; unattributed usage to fix; recent
requests; and input split into **cache miss** and **cache hit** with the cache
hit rate, output, cost, cost per 1M tokens, per person and over time.

**Mine** (everyone) is the same numbers for the signed-in person only: requests,
input cache miss, input cache hit (and what share of input it is), output, cost,
over time and by model.

### Dashboards

The dashboards are files in Grafana's JSON format, in `deploy/config/dashboards/`,
and the app draws them itself. Each panel's query runs on the app's server:

- **PostgreSQL** (the usage panels read the gateway's spend tables): Grafana's
  macros are reproduced (`$__timeFilter`, `$__timeGroupAlias`, `$__interval`,
  `$__range` and friends), including how Grafana rounds the interval, and rows
  become series by Grafana's rule for the time-series format. Each query runs as
  a role that can only `SELECT`, in a read-only transaction, one statement, with
  a 15-second timeout and a 5,000-row cap.
- **Prometheus and Loki**: the step Grafana would use (the panel's minimum
  interval, the scrape interval, Loki's 11,000-point limit), the range aligned to
  it, `$__rate_interval`, `$__auto` and the rest, the dashboard's variables
  (Container, Search) and series named by the legend format.
- The browser asks for "panel N of dashboard X over this range, with these
  variables" and gets data back. It never sends or receives a query, and a
  variable's value is only ever one of its options.

Live dashboards (those with a refresh in their file) refresh themselves, and
can pause. Edit a dashboard file and the next request uses it: the app reads
the files on every request.

`scripts/audit-dashboards.py` runs every panel's queries in the app and reports
errors, empty panels, null values and percentages out of range:

```bash
python3 scripts/audit-dashboards.py 6h
```

### Charts

One value axis per chart. Colours come in a fixed order, checked for
colour-vision deficiency against the light and dark backgrounds; a series keeps
its colour by name. At most eight series are drawn: the seven largest, and the
rest summed as "Other". Every chart has a legend when it has more than one
series, a tooltip, and **Show as table**. The time axis always spans the chosen
range.
