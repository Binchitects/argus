# Settings in the app

Admin → Settings (`https://<LLM_DOMAIN>/admin/settings` in the new web) lists
every setting an admin can change: 97 of them in 13 groups. Each is typed and
checked, with its unit, default and limits, and a note on what changing it
does. Search looks across every group. A group can be linked directly, for
example `/admin/settings#company-directory-ldap`.

## When a change applies

Each setting has a badge that says when it applies:

| Badge | Where the value lives | When it applies |
|---|---|---|
| **At once** | the app's database | Immediately. Examples: the company directory, chat limits, sign-in lockouts, branding. |
| **Restart** | the app's database | When the app restarts. The page offers **Restart the app now**: the app stops itself and Docker's restart policy starts it again, in a few seconds. Examples: session lifetimes and the longest chat answer. |
| **.env** | `stack/.env` | When you apply it on the host. Examples: the model, the engine and hardware, image generation, prices, credit defaults, Argus's GitLab connection, profiles, retention and backup. |

For both database kinds, a value saved here wins over `.env`. The page shows
the value it overrides, and **Back to the .env value** removes the saved one.

## Applying .env changes

Other services read these values from `.env` when they start. The app never
touches `.env` or Docker: no web container has the Docker socket. Instead it
writes the changes to `config/app/pending.env`, and the page shows them as
pending with the command:

```bash
./scripts/apply-settings.sh              # in the stack folder: shows, asks, applies
./scripts/apply-settings.sh --dry-run    # shows only
```

The script:

1. Accepts only settings the app is allowed to change. These are the names
   compose passes to the app as `StackEnv__…`, kept in step with the app's
   catalog by a test.
2. Checks every value again, refusing quotes, `$`, `#`, backslashes and line
   breaks. Values come from a web service, and `.env` would read those
   characters differently.
3. Shows each change. A secret's value is never shown.
4. Asks before doing anything. `--yes` skips the question, for automation.
5. Keeps a copy of `.env` (`.env.bak-<time>`), writes the changes, removes the
   pending file, and runs `docker compose up -d`. That recreates only what
   changed.

A pending change that is already in effect drops out by itself, for example
when you edited `.env` by hand. **Discard pending change** removes one without
applying it.

**Switch to this model** on the Model page saves a shipped deployment's model
block (keeping your own model directory) as pending changes, so a switch is one
click and one command.

Making these changes apply without the command is phase 6 of the
[plan](../enterprise/PLAN.md).

## Secrets

- A secret is write-only in the page. It shows whether it is set, never its
  value, not even masked: a masked value still leaks its length into every
  screenshot.
- The directory's service password is stored AES-256-GCM encrypted under a key
  derived from `APP_DATA_KEY`. A database dump alone does not reveal it.
- A secret bound for `.env` (a GitLab token, a Hugging Face token) waits in
  `config/app/pending.env`. That file is mode 0600, git ignores it, and it is
  deleted once applied, like `.env` itself.
- Compose passes the app only whether each `.env` secret is set
  (`${NAME:+yes}`), never the secret.

## What cannot be changed here, and why

| Setting | Why not |
|---|---|
| Generated secrets (`LITELLM_MASTER_KEY`, `APP_DATA_KEY`, database passwords, OIDC client secrets, …) | Changing one after the first start breaks what it protects; some can never change. They stay in `.env`, with how to make each one. |
| `LLM_DOMAIN`, ports, `BIND_ADDRESS` | A wrong value locks you out of this page; the certificate and every sign-in address follow the domain. |
| Host paths other than the model directory | They move data; a wrong one loses it. |

## Every change is audited

Each save writes one `settings.change` event per setting, with who, what and
from where. Secrets are logged as "a new secret value". Restarts are logged as
`settings.restart`. See Admin → Audit log, under **Settings**.
