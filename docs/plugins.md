# Plugins and APIs

Ready-made tools for the chat. A plugin is a folder with a `plugin.yaml` and
the files it names: an API (its OpenAPI document) or an MCP server, how its
calls sign in, which calls change something, the settings an admin fills in,
and prompts it adds to the library. **No plugin code runs in the app**: its
tools are a remote MCP server, or an API the app calls with each operation's
arguments.

Installed, a plugin is a tool like any other in **Admin → Tools**: on or off,
for everyone or some groups, on in new chats, asking before each call. Its
own writes always ask first.

## Installing

**Admin → Plugins** lists:

- the plugins that come with the app (the repository's `plugins/` folder, in
  the image at `/plugins`);
- a catalog's, when **Settings → Plugins → Plugin catalog** names one (below).

**Install** first says what the plugin does (its operations or its MCP
server, how people sign in, what asks first, the prompts it adds), then asks
for its settings. A
secret setting is stored encrypted (under `APP_KEY`) and never shown again.
**Upload a zip** and **From an address** (with its SHA-256, so only that exact
file installs) take a plugin from outside the catalog.

**Update** appears when the catalog has another version; the settings stay,
and its prompts become the new version's. **Remove** takes its tool out of
every chat, its prompts out of the library, and forgets everyone's connected
accounts for it.

Every install, update, settings change, connection and call is in the audit
log (`plugin.*`).

## Each person's own account

A plugin that signs in **per person** (`per_person: oauth2` or `api_key`)
calls its service as the person asking, with their rights there, never with a
shared key. Until they connect, the chat says so and where: **Your account →
Connections**.

- **OAuth** (`oauth2`): **Connect** sends them to sign in at the service,
  which sends them back with a code; the app trades it for tokens, keeps them
  encrypted, and refreshes them when they expire. The admin registers the app
  at the service once (for GitLab: Admin → Applications, redirect URI
  `https://DOMAIN/api/account/connections/callback`) and puts its ID and
  secret in the plugin's settings.
- **API key** (`api_key`): the person pastes their own key.

## The manifest

```yaml
name: gitlab-issues            # lowercase, digits, dashes
version: 1.1.0
title: GitLab issues
description: Finds projects, reads issues, creates them and comments on them in GitLab.
tools:
  openapi: openapi.yaml        # a file in the plugin; or  mcp: https://tools.example.com/mcp
  url: "{gitlab_url}/api/v4"   # where the API is; {setting} is filled in
auth:
  per_person: oauth2           # none (one key for everyone), api_key or oauth2
  header: Authorization
  value: "Bearer {token}"      # {token}: the person's token or key (or the "token" setting)
  oauth2:
    authorize: "{gitlab_url}/oauth/authorize"
    token: "{gitlab_url}/oauth/token"
    scopes: [api]
  help: Sign in to GitLab and allow the chat to work with issues as you.
writes: [create_issue, add_note]   # functions (their own names) that ask first
prompts: [prompts/triage.md]       # files in the plugin, each a prompt for the library
settings:
  - { key: gitlab_url, title: GitLab address, type: url, required: true }
  - { key: client_id, title: Application ID, type: text, required: true }
  - { key: client_secret, title: Application secret, type: secret, required: true }
```

With `per_person: none`, a `token` setting (a secret) is the one key for
everyone, sent as `header: value`.

## Prompts

`prompts:` lists Markdown files in the plugin. Each is a prompt of the library
(**Workspace → Prompts**, and `/` in the chat): a front matter with its slash
name and title, then its text, with `{{variables}}` the chat asks for before
sending.

```markdown
---
name: triage
title: Triage a GitLab issue
---
Triage issue #{{issue}} in the GitLab project {{project}}. Read it and its comments, then say …
```

A plugin's prompts are for whoever may use its tool (Admin → Tools), and
nobody changes them: they come with an install or an update, and go with the
plugin. `gitlab-issues` brings `/triage`.

## An API by its OpenAPI document

Without a plugin, **Admin → Tools → Add a server or API → API (OpenAPI)**
takes a document (pasted, or fetched from its address), the API's address (or
the document's first server) and an optional key header. **Read it** lists
the operations before you add it.

Each operation becomes a function named `{api}__{operationId}` (snake case):
its path, query and header parameters and its JSON body are the arguments,
references (`$ref`) resolved. A call returns the status and the body (cut at
100,000 characters). Anything but GET, HEAD and OPTIONS asks the person first,
whatever the tool's own setting. OpenAPI 3 only; convert Swagger 2 first.

## A catalog

**Settings → Plugins → Plugin catalog** is the address of an `index.json`:

```json
{ "plugins": [
  { "name": "jira", "version": "1.2.0", "title": "Jira", "description": "…",
    "url": "jira-1.2.0.zip", "sha256": "…", "per_person": "oauth2" } ] }
```

A zip's address may be relative to the index. Each zip is checked against its
SHA-256. With **Catalog's public key** (PEM, ECDSA P-256) set, the index must
also come with `index.json.sig`, its signature in base64 (DER), or nothing is
installed from it:

```bash
openssl dgst -sha256 -sign catalog-key.pem index.json | base64 -w0 > index.json.sig
```
