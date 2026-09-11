# Admin panel

`https://admin.<LLM_DOMAIN>` — one page, two faces. Everyone who signs in sees
their own usage and can change their password. Members of the `admins` group
also get a console: create people, set credit, revoke and reissue API keys,
reset passwords.

Runs under the `auth` profile, because without Authelia it has no way to know
who is asking and no reason to exist.

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
enforcing it — see `deploy/identity-proxy/app.py` for the measurement behind
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
by `scripts/gen-auth.sh`.

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
