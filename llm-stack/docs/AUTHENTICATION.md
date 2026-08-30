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

For the gateway, ask for the matching audience:

```bash
export TOKEN=$(./scripts/get-token.sh --audience gateway)
```

---

## Managing users

### Declarative (recommended)

`config/authelia/team.yml` is the source of truth for who has access. It holds
no passwords or hashes, so it is safe to commit and review in a pull request.

```yaml
users:
  - username: admin
    displayname: Administrator
    email: admin@llm.localhost
    groups:
      - admins

  - username: alice
    displayname: Alice Example
    email: alice@example.com
    groups:
      - users
```

```bash
./scripts/gen-auth.sh --sync-dry-run    # show the plan, change nothing
```

```bash
./scripts/gen-auth.sh --sync            # make users.yml match team.yml
```

Reconciliation rules:

| Situation | Action |
|---|---|
| In `team.yml`, not in `users.yml` | **CREATE** — random password, printed once |
| In both | password **kept**, groups / displayname / email **updated** |
| In `users.yml`, not in `team.yml` | **REMOVE** — access revoked |

Changing someone's group is just editing their `groups:` and re-syncing; their
password hash is preserved untouched. Offboarding is deleting their block.

The sync **refuses to run if no user is left in `admins`**, since that would
lock you out of Prometheus, Alertmanager and the Traefik dashboard.

New passwords are shown exactly once and are not recoverable — hand them over
promptly. To reset one, delete the user, sync, re-add, sync again.

Groups: `admins` reaches everything including the infrastructure endpoints;
`users` gets chat and Grafana only.

### One-off

```bash
./scripts/gen-auth.sh --add-user alice --password 'their-password' --groups admins
```

Passwords are stored as argon2id hashes; the plaintext is never written to
disk.

**On Linux** Authelia watches `users.yml` and the change is live within a
minute, with no restart.

**On Windows this does not work.** inotify events do not cross Docker Desktop's
bind mounts, so a host-side edit never reaches the watcher and the new user is
reported as *"user not found"* indefinitely. Restart the container after any
`--add-user` or `--sync`:

```bash
docker restart authelia
```

This is a Docker Desktop file-sharing limitation, not an Authelia setting —
`watch: true` is already enabled in `configuration.yml` and works correctly on
the Linux target host.

Remove a user by deleting their block from `config/authelia/users.yml`.

### Requiring 2FA

In `config/authelia/configuration.yml`, change a rule's `policy` from
`one_factor` to `two_factor`. Users enrol a TOTP device from the portal. Because
no SMTP server is configured, the enrolment link is written to a file:

```bash
docker exec authelia cat /data/notification.txt
```

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
| `config/authelia/configuration.yml` | Policies, session, access rules | yes |
| `config/authelia/clients.yml` | OIDC clients + signing key | **no** — generated |
| `config/authelia/users.yml` | Users and password hashes | **no** — generated |
| `config/authelia/secrets/` | OIDC RSA private key | **no** |
| `scripts/gen-auth.sh` | Generates all of the above | yes |
| `scripts/get-token.sh` | Machine-client token helper | yes |

`clients.yml` and `users.yml` are gitignored because they contain the OIDC
signing key and password hashes.

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

Run it after any change to `configuration.yml`, `clients.yml`, or an app's
OAuth settings.

---

## Troubleshooting

**`OAuthCallback`, `invalid_client`, or every app suddenly failing at once.**
Almost always **secret drift**: `clients.yml` was regenerated with fresh client
secrets, but the app containers still hold the values baked in when they were
created. Nothing in the config looks wrong, and an audit that reads `.env` will
happily pass while every real login fails.

`scripts/gen-auth.sh` no longer rotates a secret that already exists in `.env`
(only `--force` does), and `scripts/audit-auth.sh` step 0 compares each
container's secret against `.env`. If it reports STALE:

```bash
docker compose up -d --force-recreate grafana open-webui langfuse
```

Any time you run `gen-auth.sh --force`, recreate those three afterwards.

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

and `claims_policy: 'with_profile'` on each client. `scripts/gen-auth.sh` does
this for the three app clients. Verify with `./scripts/audit-auth.sh`, which
prints the claims that actually arrive.

**Login redirects to a consent screen every time.** Expected for third-party
clients, pointless for first-party apps you own. The generated clients use
`consent_mode: 'implicit'` so the scopes are pre-approved.



**Changing config appears to do nothing.** Authelia does **not** hot-reload
`configuration.yml` or `clients.yml` — only `users.yml` is watched. Restart it:

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
Regenerate both together with `./scripts/gen-auth.sh --force`.

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

**Browser certificate warnings.** Trust the private CA once:

```powershell
.\scripts\gen-certs.ps1 -Trust
```

**`curl` fails with a TLS error on Windows.** Windows `curl` uses the schannel
backend, which refuses a private CA because it cannot check revocation. Add
`--ssl-no-revoke --cacert config/traefik/certs/ca.crt`. Also note `*.localhost`
does not resolve in CLI tools, hence `--resolve host:443:127.0.0.1`.

---

## Turning SSO off

Set `PROTECTED_CHAIN=protected-chain@file` in `.env` and drop `auth` from
`COMPOSE_PROFILES`, then `docker compose up -d`. The stack falls back to the
basic-auth credentials in `PROXY_AUTH_USER` / `PROXY_AUTH_PASSWORD`, and the API
returns to accepting its static key directly.
