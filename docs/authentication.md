# Authentication

Everything reachable from outside authenticates against one identity provider:
**the app** at `https://DOMAIN`. There is one list of people, one sign-in,
one place to revoke access. People are local accounts, or come from the company
directory (LDAP / Active Directory) or the company's identity provider (OIDC or
SAML 2.0, with SCIM provisioning), side by side.

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
service at once. Behind the form the Argus logo gives way to its hundred eyes,
which open across the screen, look about and close back into it, over and over
(a still logo when the device asks for reduced motion).

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

Set up in **Admin → Settings → Company directory (LDAP)**
(`/admin/settings#company-directory-ldap`), and off while **Directory server**
is empty. Every setting applies as soon as it is saved, with no restart. The
section opens with a step-by-step guide (open until a directory is set), each
field's help has an example for OpenLDAP and one for Active Directory, and it
ends with two checks that use the values in the form, saved or not.

| Setting | OpenLDAP | Active Directory |
|---|---|---|
| **Directory server** | `ldap://ldap.example.com:389` with **Use StartTLS**, or `ldaps://ldap.example.com:636` | `ldaps://dc1.corp.example.com:636` |
| **Directory's CA** | the CA of its TLS certificate, in PEM, when it is your own | your enterprise CA's root (`certutil -ca.cert ca.cer`, then `certutil -encode ca.cer ca.pem`) |
| **Service account** / its password | a **read-only** DN: `cn=readonly,dc=example,dc=com` | `reader@corp.example.com`, `CORP\reader`, or its DN |
| **Where people are** | `ou=people,dc=example,dc=com` | `OU=Staff,DC=corp,DC=example,DC=com`, or the domain, `DC=corp,DC=example,DC=com` |
| **Which entries are people** | the default, which finds `uid`, `sAMAccountName`, `userPrincipalName` and `mail` (`{0}` is the name typed) | without disabled accounts: `(&(objectCategory=person)(objectClass=user)(!(userAccountControl:1.2.840.113556.1.4.803:=2))(\|(sAMAccountName={0})(userPrincipalName={0})(mail={0})))` |
| **Where groups are** | `ou=groups,dc=example,dc=com`, unless its memberOf overlay covers your kind of group | empty: `memberOf` is always there |
| **Admin group** | `llm-admins`, or its DN | `LLM Admins`, or its DN |
| **Required group** | `llm-users` (empty: everyone the search finds) | `LLM Users`; never `Domain Users`, which is in nobody's `memberOf` |

**Test the settings** connects, checks the certificate, signs in as the service
account and looks for people and the groups named, step by step. Each step says
what works and, when something does not, exactly what to fix: the address or
the port ("nothing answers at", "ldaps:// needs the TLS port"), the certificate
(self-signed, issued by a CA this server does not trust, for another name,
expired; or the directory demanding a client certificate, which the app has none
of: OpenLDAP's `TLSVerifyClient demand`, the default of the osixia/openldap
image, whose `LDAP_TLS_VERIFY_CLIENT` must then be `never` or `try`), the
service account (a wrong DN or password, a DN in another domain than the server
holds, `user@domain` or `DOMAIN\user` on a server that takes
only DNs, Active Directory's own reasons below), where people are (empty, not a
DN, or missing, with the part that exists), the user filter, and the groups (not
found; a `posixGroup` or no group at all, such as the OU above it; not in their
members' `memberOf`; or several groups of one name, which all count: a failure
for the admin group, a warning for the required group). It says which password it used: the
one typed in the field, or the saved one when the field is blank. The saved one
is used only with the saved server (its host and port) and service account, over
a connection at least as safe as the saved one: with another server or account
in the form, or with **Use StartTLS** turned off, **Accept any certificate**
turned on or another CA in **Directory's CA**, type the password, so a saved
password never goes to a server it was not saved for, nor in the clear. The test
says which change kept it back. The password is tried and saved exactly as typed;
one that starts or ends with a space (often a copy and paste) is pointed out.
Each test is in the audit log (`settings.ldap_test`) with the server it went to.

OpenLDAP refuses a DN with no entry just as it refuses a wrong password, so the
test looks the DN up without signing in: an entry found means the password is
wrong; none, with the part of the DN that exists, means a typo in it. A server's
own administrator has no entry and often lies outside what the server holds:
`cn=Directory Manager` on 389 Directory Server and FreeIPA, `cn=admin,cn=config`
on OpenLDAP (on the osixia/openldap image its password is `LDAP_CONFIG_PASSWORD`,
not `LDAP_ADMIN_PASSWORD`, and it may read only the server's settings, not
people). For those the test says the password is wrong; for any other DN with no
entry it adds that only such an administrator (OpenLDAP's `rootdn`) signs in
without one. A password typed for another service account than the saved one (or
for any, before one is saved) tells whether it is right, as a sign-in does, so
the same brakes hold the test: a refused one counts against that DN from your
address and against the account here of the person whose DN it is, and the test
is refused while either is held. Typing the saved account's own password again
(after it changed in the directory, say) is never held.

Active Directory refuses a sign-in with one code for many reasons; the test and
the audit log say which: `52e` the name or password is wrong, `525` no such
account, `530` not at this time, `531` not from this computer, `532` the
password has expired, `533` the account is disabled, `701` the account has
expired, `773` the password must be changed first ("User must change password at
next logon", ticked by default for a new account), `775` the account is locked
out. A service account is best set to **Password never expires**.

**Try a person's sign-in** takes a username and a password and checks them as
signing in does: the same search, the same check of the password by the
directory, the same groups, and this app's own rules (an email is needed, a local
account of that name is never taken over). It shows who they would be here
(username, email, name, groups, admin or not), or exactly why they could not
sign in. Nothing is saved or changed; the password is never stored or logged,
and the try is in the audit log (`settings.ldap_try`) with the server and its
outcome. A try is a guess at a password like any sign-in, so the same brakes
hold it: a wrong password counts as a wrong sign-in does, against that name from
your address and against their account here, and a try is refused while either
is held (**Settings → Sign-in and sessions** says for how long). A service
account's password typed for the try is held as for the test.

People sign in with their directory name (`uid` or `sAMAccountName`), their email
or `userPrincipalName`, or `DOMAIN\name` (the domain is left out). The app
searches for them with the service account, then checks the password by binding
as them. On the first sign-in it creates them, gives them an API key, and sets
their role from the admin group. The directory stays in charge of their name,
email, role and password; the app does not let those be changed here. With
**Where groups are** empty, groups come from each person's `memberOf`, asked for
by name (OpenLDAP's overlay sends it only then), from anywhere in the directory.
With it set, they come from the groups there that name them as a `member` or
`uniqueMember`, and from `memberOf` only where the server sends it unasked
(Active Directory does, OpenLDAP does not), as in v5.2.0: on OpenLDAP a group of
the same name elsewhere, such as another app's `admins`, never makes anyone an
admin here. Direct members only: a group inside a group does not count. A group
is a `groupOfNames`, a `groupOfUniqueNames` or an Active Directory group; a
`posixGroup` lists its members by uid (`memberUid`), which is not read, so nobody
is ever in one here (with the rfc2307bis schema a `posixGroup` can also be a
`groupOfNames`, its members listed in `member`). A group goes by its full DN or
its common name, a comma in it included (`CN=Sales\, EMEA,...` is `Sales, EMEA`);
a DN matches however it is written (capitals, spaces after its commas, `\,` or
`\2C`). A group, admin group or required group saved in v5.2.0 as `Sales\` (its
name cut at the comma) keeps matching.

By its name, every group of that name counts, wherever people's groups come
from: with **Where groups are** empty, any group of that name in the directory.
When another group has the same name (another app's `admins`, say), name the
group by its full DN; **Test the settings** lists the groups a name matches.
This is new for OpenLDAP with the memberOf overlay and **Where groups are**
empty, as v5.2.0's help advised: v5.2.0 never asked for `memberOf` there, so its
admin and required groups matched nobody, while from this version on they apply
as soon as it starts, from `memberOf`, by name across the whole directory. Run
**Test the settings** after upgrading such an installation.

Every **Check the directory every** the app re-reads every directory person.
Anyone who left the directory, or the required group, is disabled: signed out,
API keys blocked. They are enabled again if they come back. **Admin → Sign-in →
Check the directory now** runs the same check at once. When the directory cannot
be used (not reached, its certificate refused, the service account refused,
**Where groups are** not there), nobody is changed and the check says why; the
app keeps running. Only a person's own entry gone counts as leaving. The same
goes for the required group: when nobody at all is in it, it must be found (a
group of a kind signing in reads, not a `posixGroup` or an OU), and one of its
members must be in it as signing in reads people, before anyone is disabled for
not being in it. So a typo in its name changes nobody, and nor does a group that
signing in cannot see anyone in: a `groupOfNames` with **Where groups are** empty
on a server whose memberOf overlay keeps only `groupOfUniqueNames` (the
osixia/openldap image), or a group named by its DN outside **Where groups are**
on OpenLDAP. The check says what to fix.

A directory that is busy or unavailable when it checks someone's password
(rather than refusing it) is the directory's state, not a wrong password: they
are told it cannot be reached, and nothing counts against them.

Safeguards: an empty password is refused before the directory sees it (many
servers treat it as an anonymous bind and say yes); the service account is never
used with a name and no password, which servers would take for anonymous;
sign-in names are escaped, so `*` or `)(` cannot change the search; a directory
entry never takes over a local account of the same name or email; an entry
without an email cannot sign in. **Accept any certificate** turns off the
certificate check and is for testing only; the app logs a warning while it is on.
Give a company CA in **Directory's CA** instead.

Local accounts keep working next to the directory: keep at least one local admin
as a way in if the directory is down.

---

## Company sign-in (OIDC)

Company sign-in speaks OIDC (this section) or SAML 2.0 ([below](#company-sign-in-by-saml-20)):
**Protocol** in the same settings group picks one, and the page then shows
only that one's settings. The admin group, the required group, the button
label and everything about people below apply to both.

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

## Company sign-in by SAML 2.0

For an identity provider that the company runs by SAML (Entra ID, Okta,
Keycloak, ADFS, PingFederate). Set **Protocol** to `saml` in **Admin → Settings
→ Company sign-in**; a change applies at once:

| Setting | Example |
|---|---|
| Identity provider's metadata | its metadata address: the app reads the provider's entity ID, sign-in address and signing certificates there, and again every hour |
| Or its metadata XML | the metadata file pasted, when the app cannot reach the provider |
| Sign-in address, Provider's entity ID, Signing certificate (by hand) | without metadata; set, each wins over the metadata's. The certificate is PEM or its base64, several PEM blocks while the provider rolls over to a new one |
| This app's entity ID | empty: `https://DOMAIN` |
| Username attribute | empty: the NameID. An email-like value gives its part before the @ |
| Email attribute | `email`; without it, an email-like username or NameID is the email |
| Display name attribute | `displayName` |
| Groups attribute | `groups`: one value per group |
| Sign-in started at the provider | off: only sign-ins started from the sign-in page are taken |
| Admin group, Required group, Button label | as for OIDC above, compared with the groups attribute's values |

An attribute is found by its Name (often a URI), or else its FriendlyName.

Give the provider this app's details, which the Settings page shows below the
settings: the **entity ID** (Identifier, Audience URI), the **Reply URL**
`https://DOMAIN/api/auth/company/saml/acs` (Assertion Consumer Service,
HTTP-POST), or simply this app's metadata at
`https://DOMAIN/api/auth/company/saml/metadata`. **Test the identity provider**
reads the metadata (or the values by hand) before you save.

The **Sign in with ...** button sends the person to the provider with an
AuthnRequest (HTTP-Redirect binding, unsigned). The provider posts its answer
back through the browser, and the app checks it:

- an XML signature on the Response or the Assertion, by the provider's
  certificate only: a key or certificate inside the answer proves nothing.
  RSA with SHA-256 or better; SHA-1 is refused;
- no signature wrapping: exactly one assertion, no other element with the
  signed element's ID, and the signature covers the very element the app reads;
- the issuer, the audience (this app's entity ID), the Destination and
  Recipient (the Reply URL), and InResponseTo: the request this browser made,
  whose ID waited in a protected cookie for 10 minutes;
- the lifetime (NotBefore, NotOnOrAfter, with two minutes for the clocks) and a
  bearer subject confirmation;
- each assertion signs someone in once: its ID is kept until it expires, so one
  captured and posted again is refused, on every replica.

Refused, with the reason in the audit log: an encrypted assertion (the app has
no key to decrypt it: turn assertion encryption off), an answer nobody here
asked for (unless **Sign-in started at the provider** is on), and the people
refusals of OIDC above. People are made, matched by email, given their groups
and their role exactly as with OIDC; they are company accounts (`oidc` in the
API), with the provider's NameID as their subject.

**Entra ID.** Enterprise applications → New application → Create your own
application (non-gallery). Single sign-on → SAML:

1. Basic SAML Configuration: Identifier (Entity ID) `https://DOMAIN`, Reply URL
   `https://DOMAIN/api/auth/company/saml/acs` (or **Upload metadata file** with
   this app's metadata).
2. Attributes & Claims: the NameID (user.userprincipalname) gives the username.
   **Add a group claim** (security groups, or the groups assigned to the
   application) for the groups; Entra ID sends their object IDs. Add a claim
   `displayname` (namespace `http://schemas.microsoft.com/identity/claims`,
   source user.displayname) for the name.
3. SAML Certificates: copy the **App Federation Metadata Url** into **Identity
   provider's metadata**.
4. Users and groups: assign who may sign in.

Then set the attributes: email
`http://schemas.xmlsoap.org/ws/2005/05/identity/claims/emailaddress`, display
name `http://schemas.microsoft.com/identity/claims/displayname`, groups
`http://schemas.microsoft.com/ws/2008/06/identity/claims/groups`.

**Okta.** Applications → Create App Integration → SAML 2.0:

1. Single sign-on URL `https://DOMAIN/api/auth/company/saml/acs` (used for the
   Recipient and Destination too), Audience URI `https://DOMAIN`, Name ID format
   EmailAddress or Unspecified, Application username the Okta username.
2. Attribute Statements: `email` (user.email), `displayName` (user.displayName).
   Group Attribute Statements: `groups`, with a filter such as "Starts with
   llm-".
3. Sign On: copy the **Metadata URL** into **Identity provider's metadata**,
   and assign people under Assignments.

The default attribute names fit.

**Keycloak.** Clients → Import client, with this app's metadata (or Create
client, type SAML, Client ID `https://DOMAIN`):

1. Assertion Consumer Service POST Binding URL
   `https://DOMAIN/api/auth/company/saml/acs`; Name ID format `username` (or
   `email`) with **Force name ID format** on.
2. Keys: **Client signature required** off (the app's requests are unsigned).
   Signature and Encryption: **Sign assertions** on, **Encrypt assertions** off.
3. Client scopes → the client's dedicated scope → mappers: a **User Property**
   mapper `email` (SAML Attribute Name `email`), and a **Group list** mapper
   with Group attribute name `groups` (Full group path off for plain names).
4. **Identity provider's metadata**:
   `https://<host>/realms/<realm>/protocol/saml/descriptor`.

Not done: signed requests, encrypted assertions and single logout (signing out
here ends the session here, not at the provider). When the provider rolls over
to a new certificate, metadata read from its address is read again on the
first answer the old one cannot check (at most once a minute); a certificate
set by hand is changed by hand.

---

## What protects the sign-in

| Protection | Setting |
|---|---|
| Password strength | zxcvbn score 3 or more (rejects `Password1!`, accepts a few unrelated words); your own names count against it |
| Guessing one account | 5 failures for one name from one address in 10 minutes ban that pair for 12 hours; colleagues behind the same NAT are unaffected |
| Password spraying | 50 failures from one address in 10 minutes ban the address for 1 hour |
| Guessing from many addresses | 10 failures on an account lock it for 15 minutes, counted atomically so parallel guesses cannot slip past |
| Floods | 120 sign-in requests per minute per address, the directory's test and try included |
| Guessing through the directory's checks | a password typed in **Test the settings** for another service account, or in **Try a person's sign-in**, counts as a sign-in does |
| Sessions | 1 hour idle, 12 hours absolute (no action extends that), 30 days with "keep me signed in"; re-checked against the account every minute |
| Cross-site requests | every state change needs an `X-Requested-With` header, which another site cannot send; the SAML Reply URL, which the provider's page posts to, is guarded by the signature and the request this browser made |
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
| SAML assertions already used, until they expire | the `saml_assertions` table of `llmapp` |
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
database, and start it. Everyone signs in again, and the secrets saved in the
Settings page, such as the directory's service password, no longer read: each
says so under it, to be typed again.)

**Someone cannot sign in.** Look them up in **Admin → Audit log**: it says
whether it was a wrong password, a lock, a ban, a disabled account, or (for the
directory) the directory's own reason (`directory refused: the password is
wrong`, `nobody by that name`, `its password has expired`, `the account is
disabled`...) or not being in the sign-in group. A locked person can wait 15
minutes or be given a new password. For a directory person, **Settings →
Company directory → Try a person's sign-in** with their name and password shows
every step.

**Company sign-in answers "did not work".** **Admin → Audit log** has the
reason: a signature that does not match the provider's keys, another issuer or
client, a clock out of step, or the provider's own error. For "another issuer",
the Identity provider setting must be the provider's issuer exactly (Entra ID:
your tenant's, not `common`). A provider that answers "redirect URI mismatch"
needs exactly the redirect URI the Settings page shows.

**Company sign-in answers "cannot be used here".** The audit log says which: no
email, an email the provider has not verified, a username or email someone else
here has, or a local admin's email (local admins are never taken over).

**Directory sign-ins answer "cannot be reached".** The app could not use the
directory: a wrong **Directory server**, a firewall, a certificate the app does
not trust (for `ldaps://` or StartTLS; give its CA in **Directory's CA**), the
service account refused, **Where groups are** not there, or the directory busy
when it checked the password. **Test the settings** says which, and the app's log has
the same sentence. Local accounts still work.

**The test says the server refused the service account.** The step says why.
OpenLDAP gives one answer for a DN with no entry and for a wrong password, so
the test then looks the DN up anonymously: "the password is wrong" when an entry
has the DN, "no entry has this DN" with the part of it that exists when it has
none. Where the server shows anonymous lookups nothing (as many do), it cannot
tell and says "the DN or the password is wrong": check the DN letter by letter
(a comma inside a name is written `\,`; copy it from the directory as its admin
sees it) and retype the password (the test says whether it used the typed one
or the saved one). If it says the server holds another domain, the DN cannot be
there. On Active Directory the reason is exact (no such account, wrong password,
expired, must change, disabled, locked); for a DN that is hard to get right,
write the account as `reader@corp.example.com`.

**The directory is the osixia/openldap image.** Its base DN comes from
`LDAP_DOMAIN` (`example.org`, so `dc=example,dc=org`, unless it or
`LDAP_BASE_DN` is set). Its accounts are `cn=admin,<base DN>` with
`LDAP_ADMIN_PASSWORD` and, with `LDAP_READONLY_USER=true`, the better choice
`cn=readonly,<base DN>` (`LDAP_READONLY_USER_USERNAME`) with
`LDAP_READONLY_USER_PASSWORD`. It holds no people until you add them: **Where
people are** is where you put them. Anonymous connections see nothing in it, so
the test cannot tell a misspelt service account DN from a wrong password there.
Its memberOf overlay covers only `groupOfUniqueNames` groups: with
`groupOfNames` groups, set **Where groups are**. For StartTLS or `ldaps://` it
needs two changes, then the container recreated: its TLS (on by default)
demands a client certificate, which the app has none of, so set
`LDAP_TLS_VERIFY_CLIENT=try`; and the certificate it makes for itself comes from
the image's own CA, which expired on 2026-01-15, so mount a certificate of your
own, for the name the app reaches it by, at
`/container/service/slapd/assets/certs` (`ldap.crt`, `ldap.key` and `ca.crt`,
or the names in `LDAP_TLS_CRT_FILENAME`, `LDAP_TLS_KEY_FILENAME` and
`LDAP_TLS_CA_CRT_FILENAME`) and paste its CA in **Directory's CA**. From the
app's container, `localhost` is the app itself: reach the directory by its
container name on a Docker network the two share, or by the host's address.

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
