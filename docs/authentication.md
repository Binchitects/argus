# Authentication

Everything reachable from outside authenticates against one identity provider:
**the app** at `https://DOMAIN`. There is one list of people, one sign-in,
one place to revoke access. People are local accounts, or come from the company
directory (LDAP / Active Directory) or the company's identity provider (OIDC,
with SCIM provisioning), side by side.

---

## The one thing to understand first

**A static API key cannot "be" single sign-on.** Sign-on is a browser flow: a
redirect, a form, a session cookie. A Python `openai` client, a cron job, or
LiteLLM calling the engine has no browser and cannot complete it.

So browsers and programs get different credentials from the same issuer:

| Caller | Mechanism | Credential |
|---|---|---|
| A person, in a browser | the app's session | session cookie: 1 h idle, 12 h at most |
| A person's tools (Claude Code, Qwen Code, editors, scripts) | their API key at the gateway, from **Connect your tools** | `sk-...` key, spend tracked per person |

The static keys (`ENGINE_KEY`, `GATEWAY_KEY`) are internal details: the app
and the gateway use them inside the stack's network, and they never leave it.

---

## Five enforcement paths

### 1. The app's own sign-in

`https://DOMAIN/login`. Username (or email) and password, then a 6-digit
code for people who turned on two-factor sign-in; or **Sign in with ...**, the
company's identity provider, when it is set up ([below](#company-sign-in-oidc)).
The session cookie is scoped to the domain, so it covers every `*.DOMAIN`
service at once.

### 2. OIDC: apps with their own sign-in screen

An app with its own sign-in screen sends people to the app and gets back an
identity: username, name, email, and groups (`admins` for admins; everyone is
in `users`). Roles are rebuilt at every token refresh, so a demotion reaches
the apps without anyone signing out. The registered clients are exactly the
configured ones: the app deletes any other client at start.

Issuer: `https://DOMAIN/`; discovery at
`/.well-known/openid-configuration`.

### 3. forwardAuth: services with no sign-in of their own

Nothing without a sign-in of its own is published. Prometheus, Alertmanager,
Loki, the exporters and the engines are reached only inside the stack's
network; the app shows their data, to admins. Traefik routes three names:
`DOMAIN` (the app), `gateway.DOMAIN` and `argus.DOMAIN`.

### 4. The gateway authenticates itself

`gateway.DOMAIN` (LiteLLM) is deliberately **not** behind forwardAuth and
gets no credential injection. LiteLLM checks each person's own key, which is
what ties spend to the person; injecting the master key would put every
request under one identity. It is still reachable only through Traefik over TLS.

### 5. Argus takes the same API key

`argus.DOMAIN/mcp` is not behind forwardAuth either. A coding agent sends the
person's API key; Argus asks the app whose it is (`POST /api/authz/key`, inside
the network with `ARGUS_KEY`; refused through the proxy) and answers as that
person's GitLab account. The app refuses a key that is unknown, blocked or
expired, or whose person is disabled. It never logs the key. GitLab tokens are
not accepted: nobody hands one out for Argus.

---

## Using it

### As a person

Open `https://DOMAIN`. The first admin is `admin`, with the password in
`ADMIN_PASSWORD` (used once, on the very first start; change it under **Your
account** afterwards).

Your account page has your API key, for your tools and for coding agents at
Argus (make a new one there; the old one stops at once, at Argus within six
minutes), your spend and credit, two-factor sign-in (scan a QR code; you get
ten one-time recovery codes), and your password. Changing your password or
turning two-factor sign-in on or off signs you out on every other device; this
one stays signed in. Opening two-factor setup and cancelling changes nothing.

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
  blocked), sign them out everywhere, or delete them,
- place them on legal hold, or export their data
  ([admin.md](admin.md#retention-legal-hold-and-exports)).

You cannot remove the last admin, or disable, demote or delete yourself.
Every sign-in and every change is in **Admin → Audit log**, with who, whom and
from which address.

The username must equal the person's GitLab username: Argus uses it to answer
with their own repository access, in the chat and for coding agents with their
API key.

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

## Company sign-in (OIDC)

Off while no identity provider is set. Set it in **Admin → Settings → Company
sign-in**; a change applies at once:

| Setting | Example |
|---|---|
| Identity provider | its issuer: `https://login.microsoftonline.com/<tenant ID>/v2.0` (Entra ID), `https://example.okta.com`, `https://sso.example.com/realms/staff` (Keycloak), `https://accounts.google.com`, `https://gitlab.example.com` |
| Client ID, Client secret | the app's registration at the provider; the secret is stored encrypted (empty for a public client) |
| Scopes | `openid profile email`; add `groups` where the provider sends groups only for it |
| Username claim | `preferred_username`; `nickname` for GitLab, `email` for Google |
| Groups claim | `groups`; a dotted path such as `realm_access.roles` reaches into an object |
| Admin group | `llm-admins`: members are admins here |
| Required group | `llm-users`: only members may sign in (empty = anyone the provider lets through) |
| Button label | `Okta`: the sign-in page says "Sign in with Okta" |

Register the app at the provider as a web application, with the redirect URI the
Settings page shows: `https://DOMAIN/api/auth/company/callback`. **Test the
identity provider** reads its discovery document and keys before you save.

The sign-in page then has a **Sign in with ...** button above the password
form. The app sends the person to the provider (the authorization code flow
with PKCE) and checks the identity token that comes back: its signature against
the provider's published keys, the issuer, the audience (the client ID), its
lifetime and the nonce of this sign-in. The state ties the answer to the
browser that asked, so nobody can slip their own sign-in into someone else's.
The provider does the password and the two-factor sign-in; the app asks for no
second code.

On the first sign-in the app creates the person, with an API key, or matches
someone already here by email: a local or directory account of that email
becomes the provider's, and its password no longer signs in. A local admin is
never taken over: keep one as a way in when the provider is down. The provider
is in charge of the name, email and role; the role is set at each sign-in from
the admin group. A changed email gets a new API key (the gateway knows people
by email).

The username comes from the username claim and must equal the person's GitLab
username (Argus). An email-like value (`alice@example.com`, as Entra ID sends
it) gives the part before the @.

The groups claim's values are kept like a directory's: a directory group in
**Admin → Groups** named as one of them has those people as members. The admin
and required groups are compared with them exactly, ignoring case: a Keycloak
path (`/llm-admins`), a GitLab group's full path, an Entra ID group's object
ID. A look-alike group elsewhere in the provider never counts. A SCIM group of
that name counts too, so Entra ID can go by group names. When the identity
token carries no groups (GitLab's), the userinfo endpoint's are used.

Someone who left the required group is refused at their next sign-in and
disabled: signed out everywhere, API keys blocked. They are enabled again at
their first sign-in after they are back. Between sign-ins it is SCIM that tells
the app at once.

Refused, with the reason in the audit log: an account with no email, one whose
email the provider says is not verified, a username or email someone else here
has, a local admin's email, a disabled person.

| Provider | Notes |
|---|---|
| Entra ID | An app registration (Web) with a client secret. The issuer names your tenant (not `common`). Groups come as object IDs, unless the token configuration emits names; SCIM groups carry the names. |
| Okta | An OIDC web app. Add a groups claim to the ID token, or the `groups` scope. |
| Keycloak | A confidential client with a "Group Membership" mapper (claim `groups`). With "Full group path" on, names start with `/`. |
| Google | No groups. Username claim `email`. |
| GitLab | **Admin → Applications**, scopes `openid profile email`. Username claim `nickname`; groups are full paths, from the userinfo endpoint. |

---

## SCIM provisioning

With SCIM the identity provider makes, changes and deactivates people and groups
here at once, without waiting for anyone to sign in. In **Admin → Settings →
Company sign-in**, **Make a token** shows a bearer token once; the app keeps
only its SHA-256. Give the provider the token and the address
`https://DOMAIN/scim/v2`. A new token replaces the old one at once, and **Turn
off** revokes it.

| Endpoint | What it does |
|---|---|
| `/scim/v2/Users` | list, with filters on `userName`, `externalId`, `emails.value`, `id` and `active` (`eq`, joined by `and`), make, read, replace, change (PATCH), delete |
| `/scim/v2/Groups` | the same for groups and their members (filters on `displayName`, `externalId`, `id`, `members.value`) |
| `/scim/v2/ServiceProviderConfig`, `/Schemas`, `/ResourceTypes` | what the server supports: PATCH and filters; no bulk, sorting, ETags or password changes |

- Deactivating someone (`active` false, or DELETE) disables them here at once:
  signed out everywhere, API keys blocked at the gateway. `active` true enables
  them again, when it was SCIM that disabled them; an admin's or a safeguard's
  decision stands. Nobody is deleted through SCIM: an admin deletes them under
  People, with their chats.
- People SCIM makes have no password here: they sign in with the company
  account, matched to it by email at their first sign-in.
- SCIM sees everyone except local admins, who stay a way in that the provider
  cannot change. A local or directory person SCIM changes becomes the provider's.
- SCIM groups are app groups the provider decides: their name and members
  cannot be changed in the app. Tools and models are given to them like to any
  group. Groups made in the app are not seen by SCIM.
- Every change is in **Admin → Audit log**, by `scim`.

---

## SAML

Next. Checking a SAML assertion means checking an XML signature, which needs
`System.Security.Cryptography.Xml`, a package outside .NET's shared framework
that the app does not ship; checking XML signatures by hand invites signature
wrapping attacks. Entra ID, Okta, Keycloak, Google and GitLab all speak OIDC:
use company sign-in.

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
| Keys at rest | the session and OIDC signing keys are stored in the database, encrypted with `APP_KEY` |

**`APP_KEY` never changes after the first start.** If it is lost or
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
| Who is who for Argus (username → email, no passwords) | `config/directory/users.yml`, written by the app |
| Company sign-in settings (the client secret encrypted), the SCIM token's SHA-256 | the `settings` table of `llmapp` |
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
`APP_KEY` differs from the value of the first start. Put the old value
back. (Only if it is truly lost: stop the app, delete the rows of the
`DataProtectionKeys` table and the `oidc.%` rows of `settings` in the `llmapp`
database, and start it. Everyone signs in again.)

**Someone cannot sign in.** Look them up in **Admin → Audit log**: it says
whether it was a wrong password, a lock, a ban, a disabled account, or (for the
directory) not being in the sign-in group. A locked person can wait 15 minutes
or be given a new password.

**Company sign-in answers "did not work".** **Admin → Audit log** has the
reason: a signature that does not match the provider's keys, another issuer or
client, a clock out of step, or the provider's own error. For "another issuer",
the Identity provider setting must be the provider's issuer exactly (Entra ID:
your tenant's, not `common`). A provider that answers "redirect URI mismatch"
needs exactly the redirect URI the Settings page shows.

**Company sign-in answers "cannot be used here".** The audit log says which: no
email, an email the provider has not verified, a username or email someone else
here has, or a local admin's email (local admins are never taken over).

**Directory sign-ins answer "cannot be reached".** The app could not bind with
the service account: wrong `LDAP_URL`, a firewall, a certificate the app does not
trust (for `ldaps://` or StartTLS), or a wrong `LDAP_BIND_DN` / password.
Local accounts still work.

**A service is unreachable through the proxy after a config change.** Traefik
labels are baked in at container creation. Changing a label requires
recreating the affected containers: `docker compose up -d`.

**Traefik returns 404 for a service that is running.** Traefik does not route to
a container whose health check is failing. `docker compose ps` shows it as
`starting` or `unhealthy`.

**`redirect_uri` rejected, or the sign-in bounces at once.** The app builds its
callback from its own base-URL setting, which must be the proxy hostname:

| App | Setting | Must be |
|---|---|---|
| an OIDC client | its redirect URI | `https://<its host>/...` as registered in the app |

**Browser certificate warnings.** Traefik's own certificate: set `ACME_EMAIL`
for Let's Encrypt, or bring your own (docs/deployment.md, Certificates).

**`curl` fails with a TLS error on Windows.** Windows `curl` cannot check
revocation for a private CA. Add
`--ssl-no-revoke --cacert config/traefik/certs/ca.crt`. `*.localhost` does not
resolve in CLI tools either, hence `--resolve host:443:127.0.0.1`.
