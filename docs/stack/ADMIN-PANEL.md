# Admin panel

`https://admin.<LLM_DOMAIN>` — the console for the parts that have not moved into
the app yet. Members of the `admins` group get a sidebar:

| section | what is there |
|---|---|
| **Overview** | services reachable from the container, totals, who is at or past their credit, and whether the code index is current |
| **People** | moved to the app: the link opens **Admin → People** there |
| **Model** | what is serving and what it was configured with, plus the thinking-level presets |
| **Indexing** | the Argus code index: coverage, per-repo freshness, run log, the reindexing cadence, and the button |
| **Explore** | search what the index actually holds — symbols by name fragment, files by path, across the whole estate |
| **Monitoring** | service health and links out to Grafana, Prometheus and the MCP endpoint |
| **Settings** | the effective configuration, read-only |

A person's own page and the per-account actions (credit, keys, passwords,
deleting) are in the app now; see below.

The Overview's **Code index** tile is the one number here whose failure is
invisible everywhere else on the page: the services are all green and the
answers are simply old. It reads *3/3 repositories current*, names how many are
out of date (and which, in the banner beneath), and distinguishes a repository
that is failing to index from one that is merely late. It calls Argus for all of
it rather than computing freshness itself, so the tile, the Grafana line and the
`ArgusIndexStale` alert are one number — a second opinion on "is this current?"
is a bug waiting for somebody to change a threshold. The same page says whether
automatic reindexing is on, because a console that shows a working index next to
a stack that never reindexes is worse than showing nothing.

**Explore** exists for the question an operator asks when a tool returns nothing
and they cannot tell why. From the chat window, four different things look
identical: the symbol is not in the code, it is named differently, it is private
so the caller's allowlist hid it, or the file was never indexed at all. Each has
a different fix. Search a name fragment and the answer is one of the four —
along with the file list, where a file showing **0 symbols** is the difference
between "the agent cannot find it" and "it is not in the index".

It reads Argus's `/admin/explore`, which is deliberately **not** access-filtered:
the admin token is the estate-wide operator credential, and its holder can
already see everything the index contains. Those queries live in their own
module rather than beside the sixteen access-scoped ones, and a test asserts no
tool module imports them — an unfiltered query reachable from a tool path would
void every ACL guarantee in this project, and nothing else in the suite would
notice.

Runs under the `auth` profile, behind the app's forwardAuth: without it the panel
has no way to know who is asking.

## What it deliberately does not do

**No Docker socket.** This container holds the LiteLLM master key; mounting the
socket would also give it the host. So
"is Prometheus up" is answered by an HTTP probe from inside `llm-net`, not by
inspecting containers — and the Overview says so rather than implying more.

**No secrets on the Settings page.** Not even masked. A masked value still shows
its length and first characters in every screenshot, and the page is an
allow-list of variable *names* rather than a dump of `os.environ`.

**No self-deletion, and no deleting the last admin.** Both are refused. The
first version of that guard compared the username against `who.label`, which is
the *email* — so it never matched, and the administrator account was deleted
from a live stack while the guard was being tested. It now matches on username,
email and label, and refuses to remove the last administrator, because that
leaves a deployment nobody can administer and every remaining account is refused
the page it would take to undo.

## Who is who

The app decides, not the panel. Traefik sends every request through
forward-auth first and the app answers with `Remote-User`, `Remote-Email` and
`Remote-Groups`. The panel reads those and nothing else.

**Traefik overwrites those headers** from the app's response, so a browser
cannot forge them — whatever a client sends is replaced before the panel sees
it. That guarantee only covers traffic arriving through the proxy, so
`REQUIRE_FORWARDED=1` closes the rest: a request with no `X-Forwarded-Host` did
not come through Traefik and is refused. Another container on `llm-net` could
otherwise reach the panel directly and simply claim to be an admin.

Verified rather than assumed:

| request | result |
|---|---|
| no headers | 403 |
| `Remote-Groups: admins`, **not** through the proxy | 403 |
| through the proxy, `Remote-Groups: users` | own usage only |
| through the proxy, `Remote-Groups: admins` | full console |
| non-admin POST to any `/admin/*` route | 403, nothing written |

The app lets any signed-in person reach the panel. Admin gating happens
**inside** the panel, so a non-admin gets a useful page instead of a 403.

## People, keys and passwords moved to the app

Creating people, credit, API keys, password resets and deleting accounts now
happen in the app at `https://<LLM_DOMAIN>` (**Admin → People**, and **Your
account** for each person); see [AUTHENTICATION](AUTHENTICATION.md). The panel's
own pages for those (`/`, `/profile`, `/people`, `/people/<name>`, the password
form and the `/admin/create|rotate|reset|budget|delete` actions) answer with a
redirect to the matching page in the app, so old bookmarks keep working.

The panel no longer writes any account file: its `config/authelia` mount is
read-only. What remains here until phase 2 of the plan
([docs/enterprise/PLAN.md](../enterprise/PLAN.md)) moves them too: the Model
card, Indexing, Packs, Explore, Monitoring and Settings.

## Monitoring

The panel links to Grafana rather than drawing its own charts — the dashboards
already exist and are better. Buttons go to **Usage by person**, **LLM
overview**, **Resources**, and the dashboard list. Set `GRAFANA_URL` to change
where they point; leave it empty to hide the card.

## When the gateway is down

The panel reads usage from LiteLLM on every page load, so a stopped gateway
used to be a 500 with a traceback: the calls caught `HTTPError` and a refused
connection raises `URLError`, a different type. Measured, same request both
ways:

| code | result |
|---|---|
| before | HTTP 500, traceback in the log |
| after | HTTP 200, red banner, usage figures marked incomplete |

A transport failure is now turned into a 503 in the same shape the call sites
already handle, and the views say the numbers are incomplete rather than
showing 0.00 for everyone -- a zero nobody can tell apart from the truth is
worse than an error. `/healthz` reports `degraded` and returns 503, so the
container is marked unhealthy while it is in that state.

For the same reason the service declares **no `depends_on`**. LiteLLM is in
the `gateway` profile and this is in `auth`, and compose rejects the entire
project when an enabled service depends on one whose profile is off --
`--profile auth` alone failed with `depends on undefined service litellm` and
took every other service down with it. Startup ordering bought nothing here,
because the panel calls the gateway per request rather than at boot.

## Secrets it holds

The LiteLLM master key. It publishes no port — Traefik is the only route in —
and runs as a non-root user.
