# Admin panel

`https://admin.<LLM_DOMAIN>` — two faces behind one sign-in. Everyone sees their
own account: what they have spent, their keys, and a password form. Members of
the `admins` group get a console with a sidebar:

| section | what is there |
|---|---|
| **Overview** | services reachable from the container, totals, and who is at or past their credit |
| **People** | every account joined across Authelia and LiteLLM — search, paged, credit, keys, CSV export |
| **Model** | what is serving and what it was configured with, plus the thinking-level presets |
| **Indexing** | the Argus code index: coverage, per-repo freshness, run log, and the button |
| **Monitoring** | service health and links out to Grafana, Prometheus and the MCP endpoint |
| **Settings** | the effective configuration, read-only |

A person's own page is `/people/<username>`, which is also where the per-account
actions live: set credit, issue a key, reset a password, delete the account.

Runs under the `auth` profile, because without Authelia it has no way to know
who is asking and no reason to exist.

## What it deliberately does not do

**No Docker socket.** This container holds the LiteLLM master key and writes
Authelia's account file; mounting the socket would also give it the host. So
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

Authelia decides, not the panel. Traefik sends every request through
forward-auth first and Authelia answers with `Remote-User`, `Remote-Email` and
`Remote-Groups`. The panel reads those and nothing else.

**Traefik overwrites those headers** from Authelia's response, so a browser
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

The access-control rule in Authelia is `one_factor` for any signed-in person.
Admin gating happens **inside** the app so a non-admin gets a useful page
instead of a 403.

## What it changes

| action | where it lands |
|---|---|
| create a person | Authelia `users.yml` + LiteLLM internal user + end user + API key |
| set credit | LiteLLM `max_budget` on both the internal user and the end user |
| new API key | old keys deleted, then one minted |
| reset password | `users.yml` only |
| change own password | `users.yml`, after verifying the current password |

**Both LiteLLM records matter.** The internal user attributes spend; the *end
user* is what makes a ceiling actually bind on the chat path, where everyone
shares one gateway key. Setting only the first tracks a budget without
enforcing it — see `stack/deploy/identity-proxy/app.py` for the measurement behind
that.

**Revoke happens before mint**, not after. The other order leaves a window where
the old key still works alongside the new one, which is the opposite of what
"revoke" means.

**Changing your own password asks for the current one** even though Authelia has
already authenticated the session. A live session is not proof that the person
at the keyboard knows the password; without the check, an unlocked laptop is
enough to lock the owner out of their own account.

## Password hashes

argon2id with Authelia's own parameters — `m=65536, t=3, p=4`, 32-byte hash,
16-byte salt. Entries the panel writes are indistinguishable from ones written
by the `auth-init` service.

This is worth verifying if the parameters ever change, because a mismatch is
accepted when written and rejected at login — a failure nobody notices until
someone cannot sign in:

```bash
docker run --rm authelia/authelia:4.39 authelia crypto hash validate \
  '<hash from users.yml>' --password '<the password>'
```

### Authelia has to re-read the file, and on Docker Desktop it will not

Authelia is configured `watch: true`, and on a normal Linux host that is enough.
**On Docker Desktop it is not.** `users.yml` sits on a Windows bind mount,
inotify events do not cross that boundary, and Authelia never learns the file
changed. The symptom is a user who exists in the file and cannot sign in.

Measured, and the two log lines are what distinguish the cases:

```
before restart:  error="user not found"   ... "which usually indicates they do not exist"
after restart:   "Unsuccessful 1FA authentication attempt by user 'logintest'"
```

The second means Authelia *knows* the user and only rejected the password. So
the account was fine all along; Authelia simply had a stale copy of the file.

Neither `os.replace()` nor an in-place rewrite makes the watch fire, so no write
strategy fixes it. After creating a user or resetting a password on such a host:

```bash
docker compose restart authelia
```

The panel appends that instruction to every message that writes `users.yml`.
Set `WARN_RELOAD=0` on a host where the watch does work to drop the note.

Each write leaves a `users.yml.bak`, and the new file is written to a temp path
and moved into place so Authelia never reads a half-written database.

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

The LiteLLM master key, and write access to Authelia's user database. It
publishes no port — Traefik is the only route in — and runs as a non-root user.
Generated passwords and API keys are shown **once**, in the redirect message
after the action. There is no way to read a key back afterwards; issue a new one
instead.
