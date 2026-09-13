# Authentication

Every externally reachable component authenticates against a single identity
provider: **Authelia**. There is one user database, one login, one place to
revoke access.

---

## The one thing to understand first

**A static API key cannot "be" SSO.** Single sign-on is a browser flow — a
redirect to a login page, a form, a session cookie. A Python `openai` client, a
cron job, or LiteLLM calling vLLM has no browser and cannot complete it.

So the stack does not try to force browsers and machines down the same path.
Instead, **both get their credentials from the same issuer**:

| Caller | Mechanism | Credential |
|---|---|---|
| Human, in a browser | OIDC / forwardAuth | Session cookie, 12 h |
| Machine, in code | OAuth2 client-credentials | Access token, short-lived |

The static keys (`VLLM_API_KEY`, `LITELLM_MASTER_KEY`) still exist, but they are
now **internal implementation details**. They are injected by the proxy after
authentication and never leave the Docker network. Presenting one from outside
is rejected.

---

## Three enforcement paths

### 1. forwardAuth — services with no login of their own

Traefik asks Authelia about every request before it reaches the backend.
Authelia answers `200` (allow), or `302` (send the browser to the portal).

Covers: Prometheus, Alertmanager, Loki, cAdvisor, node-exporter, GPU exporter
and the MinIO console. (The Traefik dashboard is disabled entirely — this
Traefik is a plain reverse proxy.)

Restricted to the `admins` group. Note that a subject-restricted rule does not
*deny* on mismatch, it simply does not match — so there must be **no permissive
catch-all rule beneath it**, or any authenticated user falls through into it.
`default_policy: deny` covers everything not listed.

### 2. OIDC — apps with their own login screen

Grafana, Open WebUI and Langfuse delegate their own sign-in to Authelia. These
get a real *identity*, not just a gate: Authelia's `admins` group maps to
Grafana's `Admin` role automatically.

### 3. Bearer authz — the vLLM API

`api.llm.localhost` accepts an OAuth2 access token issued by Authelia, carrying
the special `authelia.bearer.authz` scope. A browser hitting `/docs` instead gets the normal
login redirect, so the Swagger UI still works interactively.

After Authelia authorises the caller, a Traefik middleware **replaces** the
`Authorization` header with the service's internal static key:

```
client ──Bearer <authelia token>──▶ Traefik ──▶ Authelia: is this valid?
                                      │              │ 200
                                      ▼              ▼
                              swap header for the internal key
                                      │
                                      ▼
                                    vLLM (sees its own --api-key)
```

Middleware order is significant — Authelia must inspect the original header
*before* it is overwritten.

---

### 4. The gateway authenticates itself

`gateway.llm.localhost` (LiteLLM) is deliberately **not** behind Authelia and
gets **no credential injection**. LiteLLM validates its own virtual keys, so the
caller's key must reach it untouched — injecting a master key would attribute
every request to one identity and destroy per-user usage tracking.

That is the route developers and SDK clients use. It is still only reachable
through Traefik over TLS; LiteLLM is the authenticator rather than Authelia.

---

## Using it

### As a human

Go to any service over HTTPS. You are redirected to
**https://auth.llm.localhost**, log in once, and the session cookie is scoped to
`llm.localhost`, so it covers every `*.llm.localhost` hostname.

Default account: `admin`. The password is `AUTHELIA_ADMIN_PASSWORD` in `.env`.

### As a machine

```bash
export TOKEN=$(./scripts/get-token.sh)
```

```bash
curl https://api.llm.localhost/v1/chat/completions -H "Authorization: Bearer $TOKEN" -H 'Content-Type: application/json' -d '{"model":"default","messages":[{"role":"user","content":"hi"}]}'
```

With the OpenAI SDK:

```python
import subprocess
from openai import OpenAI

token = subprocess.check_output(["./scripts/get-token.sh"], text=True).strip()
client = OpenAI(base_url="https://api.llm.localhost/v1", api_key=token)
print(client.chat.completions.create(
    model="default",
    messages=[{"role": "user", "content": "hi"}],
).choices[0].message.content)
```

Tokens expire. Fetch a new one rather than caching it indefinitely — that is the
entire point of replacing a static key.

The token is requested with the OAuth2 `resource` parameter, not
`audience`. Authelia matches an `audience` value by EXACT string, so a
token minted for `https://api.<domain>` is refused at
`https://api.<domain>/v1/models` -- every real endpoint 401s while the
token itself introspects as active and correctly scoped. `resource`
(RFC 8707) has prefix semantics, so one token covers the whole origin.
`get-token.sh` handles this; if you build the request yourself, do not
swap the parameter back.

For the gateway, ask for the matching audience:

```bash
export TOKEN=$(./scripts/get-token.sh --audience gateway)
```

---

## Managing users

**Use the admin panel** at `https://admin.<LLM_DOMAIN>`. Signed in as an admin you
can add a person (a password and an API key are generated and shown once), set
their credit, issue a new key (the old one stops working), and reset a password.
Everyone else who signs in sees only their own usage and a password form.

The account list is `config/authelia/users.yml`. The `auth-init` service creates
it on the very first start with a single `admin` account whose password is
`AUTHELIA_ADMIN_PASSWORD`, and never touches it again; after that the panel owns
it. Passwords are argon2id hashes; the plaintext is never written to disk.

Groups: `admins` reaches everything including the infrastructure endpoints
(metrics, alerts, logs); `users` gets chat, Grafana and their own panel page.

**There is no delete button.** To remove someone, delete their block from
`config/authelia/users.yml` (Authelia reloads it within a minute) and delete their
gateway user and keys:

```bash
curl -X POST https://gateway.<LLM_DOMAIN>/user/delete -H "Authorization: Bearer $LITELLM_MASTER_KEY" -H 'Content-Type: application/json' -d '{"user_ids":["alice@example.com"]}'
```

**On Linux** Authelia watches `users.yml` and a change is live within a minute.
**On Docker Desktop (Windows) it is not:** inotify events do not cross its bind
mounts, so run `docker compose restart authelia` after adding someone.

### Password only (`one_factor`), by decision

Every surface uses `one_factor`: username and password. Two-factor was shipped
once and removed at the operator's request. To require TOTP again for a surface,
change its `policy` to `two_factor` in `config/authelia/configuration.template.yml`
and each person enrols a device from the portal (with no SMTP configured, the
enrolment link is written to `/data/notification.txt` inside the authelia
container).

**Keep `api.` and `gateway.` at `one_factor` even then.** Authelia treats a
`client_credentials` token as 1FA by definition, so requiring a second factor
there denies every machine caller while looking like a hardening win.

`configuration.template.yml` is loaded by Authelia directly -- there is no
generated `configuration.yml` any more -- so an edit takes effect on the next
`docker compose up -d authelia`.

---

## What is deliberately NOT behind SSO

**Internal service-to-service traffic.** Prometheus scraping `vllm:8000/metrics`,
LiteLLM calling `vllm:8000`, Promtail shipping to Loki — these travel over the
Docker network and never touch Traefik. Forcing them through SSO would mean
every exporter implementing OAuth2 for no security gain, since the network is
already isolated.

**Nothing else is published.** Every service port mapping has been removed;
only Traefik's :80 and :443 exist. There is no way to reach a backend without
passing through the proxy, and therefore no way to bypass authentication from
outside the Docker network.

The trade-off is that CLI tools must resolve `*.llm.localhost` — run
`scripts/setup-hosts` once. Health checks that need no credentials probe from
*inside* the network instead (`scripts/health.sh` uses a container on
`llm-net`).

---

## Files

| Path | What it is | In git? |
|---|---|---|
| `config/authelia/configuration.template.yml` | Policies, session, access rules; Authelia fills in the domain | yes |
| `config/authelia/clients.yml` | OIDC clients + signing key | **no** — rebuilt by `auth-init` on every start |
| `config/authelia/users.yml` | Users and password hashes | **no** — created once by `auth-init`, then the panel's |
| `config/authelia/secrets/` | OIDC RSA private key | **no** — generated once by `auth-init` |
| `scripts/get-token.sh` | Machine-client token helper | yes |

The client secrets themselves live in `.env` (`*_OIDC_CLIENT_SECRET`); `auth-init`
hashes them into `clients.yml`, so the apps and Authelia can never disagree.

---

## Auditing it

`scripts/audit-auth.sh` performs a **real** login and a full authorization-code
exchange for every client, then decodes the resulting ID token and reports the
claims actually delivered. It needs no browser, so it catches in seconds what
otherwise takes three round-trips of clicking:

```bash
./scripts/audit-auth.sh
```

It checks, in order: first-factor login; the authorization-code flow and claims
per OIDC client; the machine client-credentials grant plus authorised and
unauthorised API access; that forwardAuth denies anonymous requests; and that
the same requests succeed with a session cookie.

Run it after any change to `configuration.template.yml`, `.env` secrets, or an app's
OAuth settings.

---

## Troubleshooting

**`OAuthCallback`, `invalid_client`, or every app suddenly failing at once.**
Almost always **secret drift**: `clients.yml` was regenerated with fresh client
secrets, but the app containers still hold the values baked in when they were
created. Nothing in the config looks wrong, and an audit that reads `.env` will
happily pass while every real login fails.

`clients.yml` is rebuilt from `.env` on every start, so the usual cause now is a
secret changed in `.env` while an app container kept the old value.
`scripts/audit-auth.sh` step 0 compares each container's secret against `.env`.
If it reports STALE, `docker compose up -d` recreates the apps with the new value.

**`No email found in user object` (or the app complains a claim is missing).**
Authelia keeps `email`, `name` and `groups` in the **userinfo endpoint** by
default and issues a deliberately minimal ID token. Clients that read claims
straight off the ID token — NextAuth, which Langfuse uses — then fail. The fix
is a claims policy that puts them in the ID token as well:

```yaml
identity_providers:
  oidc:
    claims_policies:
      with_profile:
        id_token: ['email', 'email_verified', 'name', 'preferred_username', 'groups']
```

and `claims_policy: 'with_profile'` on each client. `auth-init` does this for
the three app clients. Verify with `./scripts/audit-auth.sh`, which
prints the claims that actually arrive.

**Login redirects to a consent screen every time.** Expected for third-party
clients, pointless for first-party apps you own. The generated clients use
`consent_mode: 'implicit'` so the scopes are pre-approved.



**Changing config appears to do nothing.** Authelia does **not** hot-reload
`configuration.template.yml` or `clients.yml` — only `users.yml` is watched. Restart it:

```bash
docker compose up -d --force-recreate authelia
```

**A service is unreachable through the proxy after a config change.** Traefik
labels are baked in at container creation. Changing `PROTECTED_CHAIN` or any
label requires recreating the affected containers, not just Traefik:

```bash
docker compose up -d
```

**Traefik returns 404 for a service that is running.** Traefik refuses to route
containers whose Docker healthcheck is not passing. Check with
`docker compose ps` — a service stuck in `starting` or `unhealthy` will not be
routed. This is intended behaviour, not a bug.

**`invalid_client` from the token endpoint.** Either the `api` client is not
registered in the *running* Authelia instance (restart it), or
`API_OIDC_CLIENT_SECRET` in `.env` does not match the hash in `clients.yml`.
`docker compose up -d authelia` reruns `auth-init`, which rebuilds the hash from `.env`.

**`invalid_client` at the token exchange, after the user already logged in.**
The login itself succeeded and Authelia issued a code; the *client* failed to
authenticate when redeeming it. Almost always a `token_endpoint_auth_method`
mismatch, not a wrong secret. Isolate it with a deliberately bogus code:

```bash
curl -u 'CLIENT_ID:SECRET' -d 'grant_type=authorization_code&code=bogus&redirect_uri=https://x/y' https://auth.llm.localhost/api/oidc/token
```

`invalid_grant` means client authentication **worked** (only the code was bad) —
the method is right. `invalid_client` means it did not. All three apps here
(Grafana, Open WebUI via authlib, Langfuse) send HTTP Basic, so every client is
registered as `client_secret_basic`.

**Grafana: `user already exists` after a successful OAuth login.** The OAuth
half worked — Authelia authenticated you and Grafana received the userinfo. It
then failed trying to *provision* the user, because a **local** account with the
same login already existed. Grafana deliberately refuses to attach an OIDC
identity to a pre-existing local account, since that would let anyone who
registers a matching username at the IdP inherit a local admin.

This is why `GRAFANA_ADMIN_USER` is `localadmin` rather than `admin`: Authelia's
default user is `admin`, and identical names collide. Keep the two namespaces
distinct. If you hit it after the fact, rename the local account and reset
Grafana's database (dashboards and datasources are provisioned from files, so
nothing is lost):

```bash
docker compose stop grafana && docker compose rm -f grafana
```

```bash
docker volume rm llmservice_grafana-data && docker compose up -d grafana
```

The same collision applies to any app where you created a local account *before*
enabling SSO. Open WebUI and Langfuse are unaffected here only because their
data was wiped, so the first OIDC login provisions the account cleanly.

**`redirect_uri does not match` / login bounces immediately.** This fails at the
*authorization* step, before you even see the login form — the opposite end of
the flow from the Grafana case above. The app is building a callback URL from
its own base-URL setting, and that must be the **proxy hostname**, not the
direct port:

| App | Setting | Must be |
|---|---|---|
| Grafana | `GF_SERVER_ROOT_URL` | `https://grafana.llm.localhost/` |
| Open WebUI | `OPENID_REDIRECT_URI` | `https://chat.llm.localhost/oauth/oidc/callback` |
| Langfuse | `NEXTAUTH_URL` | `https://traces.llm.localhost` |

Langfuse is the easy one to miss: NextAuth derives the redirect from
`NEXTAUTH_URL`, which defaults to the direct `http://localhost:3002`.

Preflight all three without clicking through a browser — an anonymous GET to the
authorization endpoint returns **302 to the login portal** when `client_id` and
`redirect_uri` are accepted, and a 400 when they are not:

```bash
curl -o /dev/null -w '%{http_code}
' -G --data-urlencode 'client_id=langfuse' --data-urlencode 'redirect_uri=https://traces.llm.localhost/api/auth/callback/custom' --data-urlencode 'response_type=code' --data-urlencode 'scope=openid email profile' https://auth.llm.localhost/api/oidc/authorization
```

Note the consequence: setting these to the proxy hostname means the **direct
ports no longer work for interactive login**. Use the hostnames.

**Browser certificate warnings.** Expected once per browser: the certificate is
self-signed (generated by `tls-init`). Accept it; it does not change on restart.

**`curl` fails with a TLS error on Windows.** Windows `curl` uses the schannel
backend, which cannot check revocation for a self-signed certificate. Add
`--ssl-no-revoke --cacert config/traefik/certs/tls.crt`. Also note `*.localhost`
does not resolve in CLI tools, hence `--resolve host:443:127.0.0.1`.

---

## Turning SSO off

Set `PROTECTED_CHAIN=protected-chain@file` in `.env` and drop `auth` from
`COMPOSE_PROFILES`, then `docker compose up -d`. The stack falls back to the
basic-auth credentials in `PROXY_AUTH_USER` / `PROXY_AUTH_PASSWORD`, and the API
returns to accepting its static key directly.
