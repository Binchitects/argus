# Admin, usage and cost

Everything an operator does happens in the app at `https://<LLM_DOMAIN>`. The
**Admin** area needs the admin role; **Usage & cost** and **Your account** are
for everyone. (The old admin panel at `https://admin.<LLM_DOMAIN>` is gone; that
address redirects each old page to its place here, so bookmarks keep working.)

The new web at `https://next.<LLM_DOMAIN>` has the same pages, rebuilt ([plan](../enterprise/PLAN.md), 3B):
people in a table with filters, bulk actions and a detail page; the audit log
with filters and export; the Settings page, which edits everything; and the
Model page's **Switch to this model**. It replaces the pages below when the chat
is rebuilt too (3C).

Sign-in, people, the company directory and 2FA are in
[AUTHENTICATION.md](AUTHENTICATION.md), and the chat in [CHAT.md](CHAT.md). This
page covers the rest.

## Admin

| Page | What it is for |
|---|---|
| **Overview** | Services up, people and admins, total spend, who is at or past their credit, and the code index's health. When the index is stale it says how many repositories, which ones, and *why* when the last run's exit code tells (GitLab unreachable, a token that cannot list every repository, ctags missing). |
| **People** | Add, search, and per person: credit, a new API key, password and 2FA resets, admin role, disable, sign out everywhere, delete. **Export CSV** downloads everyone with spend and credit left. |
| **Model** | What is running (model, file, context, longest reply, multi-token prediction, thinking presets, power limits, prices) and every shipped sample with the exact `.env` block to paste to switch to it. |
| **Indexing** | The Argus code index: repositories, failures, files and symbols; start a run for extra branches (globs work; each default branch is always included); watch it and read its log. |
| **Packs** | Installed knowledge packs, with incompatible ones shown and why; install from a URL (with its SHA-256), update from the published index, remove. |
| **Explore** | What the index actually holds: search symbols and paths across the estate. For "a tool found nothing: is it absent, named differently, or never indexed?". |
| **Monitoring** | Live probes of the gateway, Prometheus, Grafana and Argus, and links to the tools that are not in the app yet. |
| **Settings** | The effective configuration, read-only, from an allow-list of names. In the new web (`next.<domain>`) it is editable: every setting, applied at once, by a restart, or with one host command. See [SETTINGS.md](SETTINGS.md). |
| **Audit log** | Every sign-in and every change to people or the index, with who, whom and from where. |
| **Sign-in** | Local accounts and the company directory; "Check the directory now". |

Indexing, Packs and Explore talk to Argus through the app's server with
`ARGUS_ADMIN_TOKEN`, which never reaches a browser. Without the `argus` profile
they say that Argus is not set up, and how to turn it on.

### What the admin area deliberately does not do

**It does not switch the model.** It shows the steps: switching recreates the
engine container, which needs the Docker socket, and a socket in a web app is
root on the host for anyone who reaches it.

**It shows no secrets**, not even masked: a masked value still leaks its length
and first characters into every screenshot. Settings is an allow-list of names,
never the environment.

**Prices and defaults are edited in `stack/.env`**, then `docker compose up -d`:
`PRICE_INPUT_PER_MTOK`, `PRICE_CACHED_INPUT_PER_MTOK`, `PRICE_OUTPUT_PER_MTOK`,
`LITELLM_DEFAULT_USER_BUDGET`. The Model and Settings pages show what is in effect.

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

### How the app draws the dashboards

The app reads the same dashboard files Grafana provisions
(`stack/config/grafana/dashboards/`) and runs their PostgreSQL panels itself:

- Grafana's macros are reproduced (`$__timeFilter`, `$__timeGroupAlias`,
  `$__interval`, `$__range` and friends), including how Grafana rounds the
  interval, and rows become series by Grafana's rule for the time-series format.
- The browser asks for "panel N of dashboard X over this range" and gets rows
  back. It never sends or receives SQL.
- Each query runs as a role that can only `SELECT`, in a read-only transaction,
  one statement, with a 15-second timeout and a 5,000-row cap.
- Panels that read Prometheus or Loki say so and point to Grafana until phase 5
  of the [plan](../enterprise/PLAN.md).

Edit a dashboard file and both Grafana and the app pick it up; the app reads the
files on every request.

`scripts/compare-dashboards.py` holds the app to Grafana: every SQL panel is
queried in both with the same range and interval, and the rows must be equal.

```bash
python3 scripts/compare-dashboards.py 30d
```

### Charts

One value axis per chart. Colours come in a fixed order, checked for
colour-vision deficiency against the light and dark backgrounds; a series keeps
its colour by name. At most eight series are drawn: the seven largest, and the
rest summed as "Other". Every chart has a legend when it has more than one
series, a tooltip, and **Show as table**. The time axis always spans the chosen
range.
