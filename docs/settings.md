# Settings in the app

Admin → Settings (`https://DOMAIN/admin/settings`) lists every setting an
admin can change, in groups. Each is typed and checked. In plain view (nothing
only on hover) each says what it does, when a change applies and, where it
matters, what else a change affects. A number or a duration shows its unit and
its limits beside its box (a size in bytes shows it in KB or MB instead), and a
changed setting shows its default ("The default: 60 minutes"). A text that is
too long is refused when saved, with its limit. Search looks across every group. A group can be linked directly, for example
`/admin/settings#company-directory-ldap`. A setting that belongs to one choice
shows only while it is chosen: **Company sign-in** shows OIDC's settings or
SAML's, by its **Protocol**. Long values (SAML metadata, a certificate) get a
box of several lines.

They all live in the app's database; `.env` holds only what the stack needs to
start ([configuration.md](configuration.md)). The models have their own page:
Admin → Models.

## When a change applies

Each setting has a badge that says when it applies:

| Badge | When it applies |
|---|---|
| **Applies at once** | Immediately. Examples: the company directory, company sign-in, chat limits, sign-in lockouts, branding, the default model and thinking levels. |
| **After a restart** | When the app restarts. The page offers **Restart the app now**: the app stops itself and Docker's restart policy starts it again, in a few seconds. Examples: session lifetimes, the longest chat answer, how many models the engine holds at once. |

A value saved here wins over one the environment gives. The page shows the
value it overrides, and **Back to the environment's value** removes the saved one.

## Prices

**Settings → Prices** holds what a chat model costs when it has no price of
its own (Admin → Models), and what pictures, video and speech cost. Each is in
dollars; a change applies at once, and the gateway is given the new prices when
it is saved.

| Setting | Default | What it prices |
|---|---|---|
| Input, per million tokens | 0.20 | the prompt the model reads |
| Cached input, per million tokens | 0.02 | prompt tokens the engine reads from its cache; never above a model's input |
| Output, per million tokens | 0.80 | what the model writes, thinking included |
| A picture | 0.01 | each picture the picture model draws |
| A second of video | 0.05 | each second of a clip |
| A minute of sound turned into text | 0.006 | speech to text, by the sound's length |
| 1,000 characters read aloud | 0.015 | text to speech |

The defaults are about what hosted services charge for a small open model; set
your own. A new price counts from the moment it is saved: what was booked
before keeps the price it had. Below the prices, **Recalculate past costs**
works out the costs of chosen days again at today's prices, on request
([admin.md](admin.md#prices)).

## Storage

**Settings → Storage** decides when a disk is too full and how much each
person's files may take; Admin → Storage uses the rest ([admin.md](admin.md#storage)).
Each applies at once.

| Setting | Default | What it does |
|---|---|---|
| Warn when a disk is fuller than | 80% | a disk of the host, or a folder the app mounts, past this share is an alert (the Alerts page, the bell, email and the alerts webhook), once each time it passes it |
| Room for each person's files | empty: no limit | what one person's files may take, in MB: uploads, and the pictures, videos and speech the tools make; past it those are refused. An admin can give someone their own under Admin → Storage → People |
| Old generated media | 90 days | the age at which the clean-up of old pictures, videos and speech takes them (nothing is deleted by itself) |
| Backups the clean-up keeps | 14 | how many backups the clean-up of old backups keeps, the newest |

## The source link

**Settings → Branding → Where the source is** is linked as **Source** beside
the version at the foot of the sidebar and of the navigation on a phone (the
collapsed sidebar shows it as an icon, with the version in its tooltip), so
every signed-in page offers it. Argus Arena is under the AGPL, which has every
person who uses it over a network offered the complete source of the version
they use: a deployment of a modified version points this at its own source.
[LICENSING.md](../LICENSING.md) says when a commercial license is needed
instead.

## Secrets

- A secret is write-only in the page. It shows whether it is set, never its
  value, not even masked: a masked value still leaks its length into every
  screenshot.
- A saved secret (the directory's service password, the company sign-in client
  secret, the mail password) is stored AES-256-GCM encrypted under a key derived
  from `APP_KEY`. A database dump alone does not reveal it.
- The SCIM token is not a setting: **Company sign-in** makes it, shows it once
  and keeps only its SHA-256. The same group shows what to register at the
  identity provider (the redirect URI for OIDC; this app's entity ID, Reply URL
  and metadata address for SAML), and tests the provider before you save.

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
