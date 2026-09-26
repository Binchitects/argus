# Admin, usage and cost

Everything an operator does happens in the app at `https://<LLM_DOMAIN>`. The
**Admin** area needs the admin role; **Usage & cost** and **Your account** are
for everyone. (The old admin panel at `https://admin.<LLM_DOMAIN>` is gone; that
address redirects each old page to its place here, so bookmarks keep working.)

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
| **Tools** | What the chat's model may call: Argus, Python (the sandbox), the web (off until you turn it on and allow sites), image generation, the calculator, date and time, reading long files in parts, and MCP servers you add. Per tool: on or off, who may use it (everyone, admins, or chosen groups), on in new chats, ask before each call. An MCP server is tested before it is added; its key is stored encrypted and never shown. |
| **Models** | Every model at the gateway. The engine's models load and unload with one click (one at a time on one GPU); more are added from the model library on the host. Per model: who may use it, in the chat and with API keys. See [Models](#models) below. |
| **Deployment** | The `.env` model the engine starts with (file, context, longest reply, multi-token prediction, thinking presets, power limits, prices) and every shipped sample with the exact `.env` block to paste to switch to it. |
| **Indexing** | The Argus code index: repositories, failures, files and symbols; start a run for extra branches (globs work; each default branch is always included); watch it and read its log. |
| **Packs** | Installed knowledge packs, with incompatible ones shown and why; install from a URL (with its SHA-256), update from the published index, remove. |
| **Explore** | What the index actually holds: search symbols and paths across the estate. For "a tool found nothing: is it absent, named differently, or never indexed?". |
| **Monitoring** | Live probes of the gateway, Prometheus, Alertmanager, Loki and Argus, and links to Prometheus's and Alertmanager's own pages. |
| **Dashboards** | Ten dashboards, drawn by the app: the model and the gateway (LLM Overview, Stack Performance), the machine (Resources, GPU Hardware, Host & Containers), the services (Stack Health & Alerts, Logs), Argus and its index, and usage by person. See [Dashboards](#dashboards) below. |
| **Logs** | Every service's logs from Loki, newest first: by container, level (all, warnings and errors, errors) and text, over five minutes to a month, and **Live** for new lines as they are written. The filters are in the address, so a view can be linked. |
| **Alerts** | What fires now (from Alertmanager, with silenced ones marked), every time an alert fired over a day, a week or a month, and every rule with its state, severity, how long its condition must hold, and its query. |
| **Settings** | Every setting, grouped and searchable: applied at once, by a restart the app does itself, or with one host command for `.env` values. See [settings.md](settings.md). |
| **Audit log** | Every sign-in and every change to people or the index, with who, whom and from where. |
| **Sign-in** | Local accounts and the company directory; "Check the directory now". |

Indexing, Packs and Explore talk to Argus through the app's server with
`ARGUS_ADMIN_TOKEN`, which never reaches a browser. Without the `argus` profile
they say that Argus is not set up, and how to turn it on.

### Models

llama.cpp runs in **router mode**: one server that knows several models and
loads one at a time (`LLAMACPP_MODELS_MAX`, default 1, since one GPU holds one
large model). Switching is an API call, not a restart.

- **Load** a model and the one before it unloads, for everyone. Answers wait
  while it loads: seconds when its weights are in the page cache, minutes from
  disk. **Unload** leaves the engine with none until one is loaded again. The
  app remembers the last one loaded and loads it again after a restart.
- A model that **could not load** (an incomplete download, a file this
  llama.cpp cannot read, too little GPU memory) says so on its card; the reason
  is in the engine's log (`docker compose logs llamacpp`). It is not tried
  again on its own: the `.env` model is loaded in its place, and the chat
  works on.
- **Add a model** picks a GGUF from the **model library** (`LLAMACPP_LIBRARY_DIR`,
  searched three levels deep; a split model is listed once, by its first part).
  **Each file is read for what it is** (its header and tensor table, not its
  name): a dense or mixture-of-experts language model, with full, hybrid
  (a cache in some layers only), sliding-window or recurrent attention; or an
  embedding model, reranker, vision projector, draft head, image model or LoRA
  adapter. Only language models can be added: the engine serves one model at a
  time, so the rest are listed with what they are for. A split model with a
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

**It does not recreate containers.** Admin → Models switches between models
through the engine's own API (llama.cpp's router), which needs no Docker
socket. Changing the `.env` model, or anything else in `.env`, recreates
containers, and that stays a host command (`./scripts/apply-settings.sh`): a
Docker socket in a web app is root on the host for anyone who reaches it.

**It shows no secrets**, not even masked: a masked value still leaks its length
and first characters into every screenshot. A secret can be replaced, never
read back.

**Prices and credit defaults are `.env` values**: change them under Settings →
Credit and prices, then run `./scripts/apply-settings.sh` on the host.

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
and the app draws them itself (Grafana was removed in phase 5 of the
[plan](plan.md)). Each panel's query runs on the app's server:

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

Before Grafana was removed, every panel was queried in both over the same range
and gave the same data: 153 of 153, over 30 days for usage and 6 hours for
metrics, over 7 days and 24 hours, and over 2 days and 1 hour. The script that
did it, `compare-dashboards.py`, is in the history (commit 8c07f7b).
`scripts/audit-dashboards.py` now runs every panel's queries in the app and
reports errors, empty panels, null values and percentages out of range:

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
