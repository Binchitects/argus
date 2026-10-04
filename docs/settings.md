# Settings in the app

Admin → Settings (`https://DOMAIN/admin/settings`) lists every setting an
admin can change, in groups. Each is typed and checked, with its unit, default
and limits, and a note on what changing it does. Search looks across every
group. A group can be linked directly, for example
`/admin/settings#company-directory-ldap`.

They all live in the app's database; `.env` holds only what the stack needs to
start ([configuration.md](configuration.md)). The models have their own page:
Admin → Models.

## When a change applies

Each setting has a badge that says when it applies:

| Badge | When it applies |
|---|---|
| **At once** | Immediately. Examples: the company directory, chat limits, sign-in lockouts, branding, the default model and thinking levels. |
| **Restart** | When the app restarts. The page offers **Restart the app now**: the app stops itself and Docker's restart policy starts it again, in a few seconds. Examples: session lifetimes, the longest chat answer, how many models the engine holds at once. |

A value saved here wins over one the environment gives. The page shows the
value it overrides, and **Back to the environment's value** removes the saved one.

## Secrets

- A secret is write-only in the page. It shows whether it is set, never its
  value, not even masked: a masked value still leaks its length into every
  screenshot.
- A saved secret (the directory's service password, the mail password) is
  stored AES-256-GCM encrypted under a key derived from `APP_KEY`. A database
  dump alone does not reveal it.

## What a group can set for its members

Some settings are the company's default, and a group can set its own (Admin →
Groups → a group → Policies):

| Setting | A group's own | When a person is in several groups |
|---|---|---|
| Data retention → **Keep chats for** | days | the shortest |
| Safeguards → **Secrets in messages and files** | refuse, mask or off | the strictest (refuse, then mask) |
| Safeguards → **Mask personal data** | mask or off | mask |
| Safeguards → **The model checks each message** | check or off | check |
| Safeguards → **Blocked words and patterns** | apply, or not for this group | apply |

A group also has a credit a month and a cost centre, which have no company
setting ([admin.md](admin.md#credit-for-groups)).

## What cannot be changed here, and why

| Setting | Why not |
|---|---|
| The secrets in `.env` (`APP_KEY`, `GATEWAY_KEY`, `DB_PASSWORD`, …) | Changing one after the first start breaks what it protects; `APP_KEY` can never change. |
| `DOMAIN`, the ports | A wrong value locks you out of this page; every sign-in address follows the domain. |
| `MODELS_DIR` | It moves the models; every server reads them there. |

## Every change is audited

Each save writes one `settings.change` event per setting, with who, what and
from where. Secrets are logged as "a new secret value". Restarts are logged as
`settings.restart`. See Admin → Audit log, under **Settings**.
