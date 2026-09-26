# Authentication

Everything reachable from outside authenticates against one identity provider:
**the app** at `https://<LLM_DOMAIN>`. There is one list of people, one sign-in,
one place to revoke access. People are local accounts, or come from the company
directory (LDAP / Active Directory), or both.

---

## The one thing to understand first

**A static API key cannot "be" single sign-on.** Sign-on is a browser flow: a
redirect, a form, a session cookie. A Python `openai` client, a cron job, or
LiteLLM calling the engine has no browser and cannot complete it.

So browsers and programs get different credentials from the same issuer:

| Caller | Mechanism | Credential |
|---|---|---|
| A person, in a browser | the app's session, or OIDC for chat / Langfuse | session cookie: 1 h idle, 12 h at most |
| A person's tools (Qwen Code, IDEs, scripts) | their API key at the gateway | `sk-...` key, spend tracked per person |
| A machine client of the engine API | OAuth2 client credentials | access token, 1 h |

The static keys (`LLAMACPP_API_KEY`, `LITELLM_MASTER_KEY`) still exist, but only
as internal details: the proxy injects them after authentication and they never
leave the Docker network.

---

## Four enforcement paths

### 1. The app's own sign-in

`https://<LLM_DOMAIN>/login`. Username (or email) and password, then a 6-digit
code for people who turned on two-factor sign-in. The session cookie is scoped
to the domain, so it covers every `*.<LLM_DOMAIN>` service at once.

### 2. OIDC: apps with their own sign-in screen

Open WebUI and Langfuse send people to the app and get back an identity:
username, name, email, and groups. The `admins` group becomes Open WebUI's
admin role; everyone is in `users`. (Grafana signed in this way too until its
dashboards moved into the app; its client is deleted at start.)
Roles are rebuilt at every token refresh, so a demotion reaches the apps without
anyone signing out.

Issuer: `https://<LLM_DOMAIN>/`; discovery at
`/.well-known/openid-configuration`.

### 3. forwardAuth: services with no sign-in of their own

Traefik asks the app (`/api/authz/forward-auth`) before every request to:

| Host | Who gets in |
|---|---|
| `metrics.`, `alerts.`, `logs.`, `cadvisor.`, `node.`, `gpu.`, `s3.`, and the engines' `/metrics` | admins only |
| `api.` (the engine) | a machine token with the `api` scope, or anyone signed in (for `/docs`) |
| any other host | nobody |

(`admin.<LLM_DOMAIN>`, the old admin panel, is now only a redirect into the app.)

A browser without a session is sent to sign in and comes back afterwards
(`302`); a program gets `401`; someone signed in without the right role gets
`403`. On success the app adds `Remote-User`, `Remote-Groups`, `Remote-Email`
and `Remote-Name`; Traefik overwrites any that a browser sent, so they cannot
be forged.

### 4. The gateway authenticates itself

`gateway.<LLM_DOMAIN>` (LiteLLM) is deliberately **not** behind forwardAuth and
gets no credential injection. LiteLLM checks each person's own key, which is
what ties spend to the person; injecting the master key would put every
request under one identity. It is still reachable only through Traefik over TLS.

---

## Using it

### As a person

Open `https://<LLM_DOMAIN>`. The first admin is `admin`, with the password in
`ADMIN_PASSWORD` (used once, on the very first start; change it under **Your
account** afterwards).

Your account page has your API key (make a new one there; the old one stops at
once), your spend and credit, two-factor sign-in (scan a QR code; you get ten
one-time recovery codes), and your password. Changing your password or turning
two-factor sign-in on or off signs you out on every other device; this one
stays signed in. Opening two-factor setup and cancelling changes nothing.

### As a machine client of the engine API

```bash
export TOKEN=$(./scripts/get-token.sh)
```

```bash
curl https://api.llm.localhost/v1/chat/completions -H "Authorization: Bearer $TOKEN" -H 'Content-Type: application/json' -d '{"model":"default","messages":[{"role":"user","content":"hi"}]}'
```

Tokens expire after an hour. Fetch a new one rather than caching it; that is the
point of replacing a static key. Tools that belong to a person should use that
person's API key at the gateway instead, so their usage is attributed to them.

---

## Managing people

**Admin → People** in the app. Signed in as an admin you can:

- add a person (a password and an API key are generated and shown once),
- set their credit (empty = unlimited), make a new API key, reset their password
  or their two-factor sign-in,
- make them an admin, disable them (signed out within a minute, API keys
  blocked), sign them out everywhere, or delete them.

You cannot remove the last admin, or disable, demote or delete yourself.
Every sign-in and every change is in **Admin → Audit log**, with who, whom and
from which address.

The username must equal the person's GitLab username: Argus uses it to answer
with their own repository access.

### From Authelia

An install that ran Authelia keeps its people: on its first start the app
imports `config/authelia/users.yml` once, with roles, and everyone signs in with
their old password (it is re-hashed at that first sign-in). Langfuse links the
existing accounts by email.

---

## The company directory (LDAP / Active Directory)

Off while `LDAP_URL` is empty. Set these in `.env`:

| Setting | Example |
|---|---|
| `LDAP_URL` | `ldaps://dc1.example.com:636`, or `ldap://ldap.example.com` with `LDAP_STARTTLS=true` |
| `LDAP_BIND_DN` / `LDAP_BIND_PASSWORD` | a **read-only** service account that can search people and groups |
| `LDAP_USER_BASE_DN` | `OU=Staff,DC=example,DC=com` |
| `LDAP_GROUP_BASE_DN` | only for directories without `memberOf` (OpenLDAP without the overlay) |
| `LDAP_ADMIN_GROUP` | `llm-admins`: members are admins here |
| `LDAP_REQUIRED_GROUP` | `llm-users`: only members may sign in (empty = anyone the search finds) |
| `LDAP_SYNC_INTERVAL` | `00:15:00` |

People sign in with their directory name (`uid` or `sAMAccountName`) or email.
The app searches for them with the service account, then checks the password by
binding as them. On the first sign-in it creates them, gives them an API key,
and sets their role from the admin group. The directory stays in charge of
their name, email, role and password; the app does not let those be changed
here.

Every `LDAP_SYNC_INTERVAL` the app re-reads every directory person. Anyone who
left the directory, or the required group, is disabled: signed out, API keys
blocked. They are enabled again if they come back. **Admin → Sign-in → Check the
directory now** runs the same check at once. When the directory cannot be
reached, nobody is changed.

Safeguards: an empty password is refused before the directory sees it (many
servers treat it as an anonymous bind and say yes); sign-in names are escaped,
so `*` or `)(` cannot change the search; a directory entry never takes over a
local account of the same name or email; an entry without an email cannot sign
in. `LDAP_INSECURE_SKIP_VERIFY=true` turns off the certificate check and is for
testing only; the app logs a warning while it is on.

Local accounts keep working next to the directory: keep at least one local admin
as a way in if the directory is down.

---

## What protects the sign-in

| Protection | Setting |
|---|---|
| Password strength | zxcvbn score 3 or more (rejects `Password1!`, accepts a few unrelated words); your own names count against it |
| Guessing one account | 5 failures for one name from one address in 10 minutes ban that pair for 12 hours; colleagues behind the same NAT are unaffected |
| Password spraying | 50 failures from one address in 10 minutes ban the address for 1 hour |
| Guessing from many addresses | 10 failures on an account lock it for 15 minutes, counted atomically so parallel guesses cannot slip past |
| Floods | 120 sign-in requests per minute per address |
| Sessions | 1 hour idle, 12 hours absolute (no action extends that), 30 days with "keep me signed in"; re-checked against the account every minute |
| Cross-site requests | every state change needs an `X-Requested-With` header, which another site cannot send |
| Answers | a wrong password and an unknown name get the same answer |
| Keys at rest | the session and OIDC signing keys are stored in the database, encrypted with `APP_DATA_KEY` |

**`APP_DATA_KEY` never changes after the first start.** If it is lost or
changed, the app refuses to start and says so, rather than quietly making new
keys (which would sign everyone out and break single sign-on). Keep it with your
other secrets.

---

## What is deliberately NOT behind sign-in

**Internal service-to-service traffic.** Prometheus scraping, LiteLLM calling
the engine, Promtail shipping to Loki: these travel over the Docker network and
never touch Traefik. The network is already isolated.

**Nothing else is published.** Only Traefik's :80 and :443 exist. There is no
way to reach a backend without passing through the proxy, and therefore no way
to bypass authentication from outside the Docker network.

---

## Files and settings

| What | Where |
|---|---|
| People, roles, 2FA, audit log, OIDC clients and keys | the app's `llmapp` database on the shared Postgres |
| OIDC client secrets | `.env` (`*_OIDC_CLIENT_SECRET`); the app registers the clients from them on every start |
| Who is who for Argus (username → email, no passwords) | `config/authelia/directory/users.yml`, written by the app |
| Basic auth for `PROTECTED_CHAIN=protected-chain@file` | `config/traefik/auth/users.htpasswd`, from `PROXY_AUTH_*` |
| Machine-client token helper | `scripts/get-token.sh` |

The app runs as `LLM_UID:LLM_GID` (the owner of `deploy/config`), so the files it
writes stay yours.

---

## Auditing it

```bash
./scripts/audit-auth.sh
```

It signs in for real and runs the full authorization-code exchange for every
client, printing the claims that arrive; checks that a used code and a wrong
client secret are refused and an unknown redirect is never followed; gets a
machine token and uses it (and a forged one); checks forwardAuth anonymous and
signed in; and checks the CSRF guard, the security headers and the session
cookie flags. It exits non-zero when anything fails. Run it after changing
`.env` secrets or an app's OAuth settings.

---

## Troubleshooting

**Every app fails at once with `invalid_client`.** Almost always **secret
drift**: a secret changed in `.env` but an app container still holds the old
value. `audit-auth.sh` step 0 compares each container with `.env`; if it reports
STALE, `docker compose up -d` recreates it.

**The app will not start: "The stored sign-in keys cannot be decrypted".**
`APP_DATA_KEY` differs from the value of the first start. Put the old value
back. (Only if it is truly lost: stop the app, delete the rows of the
`DataProtectionKeys` table and the `oidc.%` rows of `settings` in the `llmapp`
database, and start it. Everyone signs in again.)

**Someone cannot sign in.** Look them up in **Admin → Audit log**: it says
whether it was a wrong password, a lock, a ban, a disabled account, or (for the
directory) not being in the sign-in group. A locked person can wait 15 minutes
or be given a new password.

**Directory sign-ins answer "cannot be reached".** The app could not bind with
the service account: wrong `LDAP_URL`, a firewall, a certificate the app does not
trust (for `ldaps://` or StartTLS), or a wrong `LDAP_BIND_DN` / password.
Local accounts still work.

**A service is unreachable through the proxy after a config change.** Traefik
labels are baked in at container creation. Changing `PROTECTED_CHAIN` or any
label requires recreating the affected containers: `docker compose up -d`.

**Traefik returns 404 for a service that is running.** Traefik does not route to
a container whose health check is failing. `docker compose ps` shows it as
`starting` or `unhealthy`.

**`redirect_uri` rejected, or the sign-in bounces at once.** The app builds its
callback from its own base-URL setting, which must be the proxy hostname:

| App | Setting | Must be |
|---|---|---|
| Open WebUI | `OPENID_REDIRECT_URI` | `https://chat.<LLM_DOMAIN>/oauth/oidc/callback` |
| Langfuse | `NEXTAUTH_URL` | `https://traces.<LLM_DOMAIN>` |

**Browser certificate warnings.** Expected once per browser: the certificate is
self-signed (generated by `tls-init`). Accept it; it does not change on restart.

**`curl` fails with a TLS error on Windows.** Windows `curl` cannot check
revocation for a self-signed certificate. Add
`--ssl-no-revoke --cacert config/traefik/certs/tls.crt`. `*.localhost` does not
resolve in CLI tools either, hence `--resolve host:443:127.0.0.1`.

---

## Turning forwardAuth off

Set `PROTECTED_CHAIN=protected-chain@file` in `.env` and `docker compose up -d`.
The services with no sign-in of their own then use the basic-auth credentials in
`PROXY_AUTH_USER` / `PROXY_AUTH_PASSWORD` instead. The app, chat and the
gateway keep their own sign-in.
