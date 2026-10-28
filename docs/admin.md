# Admin, usage and cost

Everything an operator does happens in the app at `https://DOMAIN`. The
**Admin** area needs the admin role; **Usage & cost** and **Your account** are
for everyone, as are **Connect your tools** (each person's API key and the
setups for their tools) and the **Leaderboard** (the models as people voted
for them in the chat's Compare, unless an admin keeps it to the admins).

People are a table with filters, bulk actions (a credit of one kind, disable,
enable, sign out) and a page each. The audit log has filters, paging and CSV export. The
Settings page edits everything ([settings.md](settings.md)), and the Model page
has **Switch to this model**.

Sign-in, people, the company directory, company sign-in (OIDC or SAML, and SCIM) and 2FA
are in [authentication.md](authentication.md), and the chat in [chat.md](chat.md). This
page covers the rest.

## Admin

Every table here (and in Usage, the Leaderboard, the dashboards' table panels
and Argus's own pages) sorts by any of its columns, by a click on the column's
heading, and has a box above it that keeps the rows holding what is typed.

| Page | What it is for |
|---|---|
| **Overview** | Services up, people and admins, this month's spend, who is at or past a credit of their own, the code index's health, and the certificate the site serves. When the index is stale it says how many repositories, which ones, and *why* when the last run's exit code tells (GitLab unreachable, a token that cannot list every repository, ctags missing). The certificate (from Traefik's metrics in Prometheus) shows the days until it expires and who issued it (your own, or Let's Encrypt); within 30 days it says what to do, and it says so when Traefik serves its own default (browsers warn). |
| **People** | Add, search, and per person: credits (one per kind), API access, rate limits for their API keys, a new API key, password and 2FA resets, admin role, disable, sign out everywhere, legal hold, an export of their data, delete. With the company directory on, **Move to the directory** moves the local accounts selected to directory sign-in, keeping everything of theirs ([authentication.md](authentication.md#moving-local-accounts-to-the-directory)). **Export CSV** downloads everyone with this month's spend and credit of each kind, and whether they have API access. |
| **Groups** | App groups (the people you add), directory groups (whoever the company directory or the identity provider's groups claim puts in them, by name or DN) and SCIM groups (made and filled by the identity provider; their name and members are changed there). Tools and models are given to groups. Per group: how long members' chats are kept, credits a month, one per kind (shared or each member's), a cost centre, and the requests and tokens a minute each member's API key may use. A group's **priority in the answers' line** (-10 to 10, 0 for everyone) decides who goes first when the model is busy: higher first, and within one priority the line stays fair (fewest answers running, then served longest ago). Someone in several groups takes the highest. A change is audited. Below the list, the monthly **Chargeback** report. See [Retention, legal hold and exports](#retention-legal-hold-and-exports), [Credits](#credits) and [Rate limits for API keys](#rate-limits-for-api-keys). |
| **Tools** | What the chat's model may call: Argus, Python (the sandbox), the web (off until you turn it on and allow sites), image generation, the calculator, date and time, reading long files in parts, the canvas (documents and code beside the chat, changed by the model part by part), questions for the person (the model asks with choices instead of guessing), sub-agents (the model splits a task into parts done side by side), memory (the model remembers what each person asks it to; off for everyone under **Settings → Chat → Memory**, and nobody but the person sees their memories), **Decide (Laya)** (typed questions about a text answered with probabilities by the Laya decision model on the CPU; offered only while the `laya` module runs and has a checkpoint loaded, and otherwise says why: [chat.md](chat.md#decide-laya)), **Deep research** (a plan, sub-agents that search the web, and a report with its sources: it runs only through this tool: only those who may use it see it in the chat's **Tools**; on in a chat, the model starts one when asked, and **Ask before each run** has the chat ask the person first. As installed, and after an upgrade from v5.2.0 or earlier, it is on for everyone, on in new chats and asks first, so everyone keeps it until you change that; the deep research a day per person is under **Settings → Safeguards**: [chat.md](chat.md#what-it-does)), and the MCP servers and APIs you add. An **API** is added by its OpenAPI 3 document (JSON or YAML, pasted or fetched from its address): each operation becomes a function, its parameters and JSON body the arguments, and a call that changes something (POST, PUT, PATCH, DELETE) always asks the person first. **Read it** lists the operations before you add it. Per tool: on or off, who may use it (everyone, admins, or chosen groups), on in new chats, ask before each call. An MCP server is tested before it is added; its key is stored encrypted and never shown. **Its certificate (https)**, per server or API: **Check the certificate** (the default: the CAs the app's system trusts), **Trust this CA** (a CA's certificate in PEM, pasted or from a file: a company CA, its issuing CA alone, or a self-signed server's own certificate; the chain must lead to it, each certificate up to it within its dates, and the name must still match; a server that does not send its issuing CA needs it given too, with the root in one bundle, or alone), or **Do not check** (any certificate is accepted; the form and the tool's card warn). When **Test** or **Read it** meets a certificate it does not trust, it says why (self-signed, issued by a CA it does not trust, for another name, not meant for a server, expired or not yet valid, it or a CA in its chain), who issued it, for which names, its dates and fingerprint, and offers both choices there; **Read it** checks the API's address too, even when its document was pasted. A secure connection that fails for another reason (no https at that address, a server that speaks only an old TLS version, no cipher in common) says so instead, as the chat does: no certificate choice helps there. The choice applies to everything sent to that server (its tools listed and called, long calls, an API's document fetched from its address, its calls, and a plugin's OAuth token address on the same server), never to anything else: with **Trust this CA** or **Do not check**, a redirect is followed only on the same host, and one to another host is not (the test or the call ends with its `HTTP 3xx`), so nothing is sent there; each change is audited (`tool.server_tls`, the CA by name and fingerprint). A server whose tools run long can have its own **Longest call** (up to 24 hours; otherwise **Settings → Chat → Longest tool call**, an hour). The same choices decide what [Arena MCP](mcp.md) serves each person's own agent (`mcp.call` in the audit log). |
| **Knowledge** | Company knowledge the chat searches: GitLab projects or groups (their wikis and issues, read by each project's members), Confluence spaces and SharePoint or OneDrive sites and libraries (read by whom Confluence or SharePoint lets read, and the groups you choose where they cannot tell), folders mounted under `/knowledge`, and websites (read by the groups you choose). Confluence and SharePoint take an account's token or an app's secret, stored encrypted, and have **Test connection**. Per source: the last sync's state and errors, documents and passages, who may read it and what is mirrored, **Sync now**, its documents, **Settings**, remove. Needs the embedder. See [knowledge.md](knowledge.md). |
| **Storage** | What takes room and where: the disks (the host's, and the folders the app mounts), each database and its largest tables, the chat's files by kind and by person, the model library and what uses each model, Argus's index and packs, backups, logs and metrics, how each grew, and how long each is kept. The chat's files to find, download and delete; clean-ups that show what would go first; each person's room for files. See [Storage](#storage) below. |
| **Models** | Every model at the gateway. The engine's models load and unload with one click (one at a time on one GPU); more are added from the model library on the host. Per model: who may use it, in the chat and with API keys. See [Models](#models) below. |
| **Deployment** | The `.env` model the engine starts with (file, context, longest reply, multi-token prediction, thinking presets, power limits, prices) and every shipped sample with the exact `.env` block to paste to switch to it. |
| **Indexing** | The Argus code index: which repositories GitLab lists are indexed and which branches of each (with each branch's latest commit), every indexed branch at its commit (hash, message, when), **Update** per repository, the reindex schedule (in words or cron, in a time zone), the GitLab push and merge webhook (its secret, shown once, the steps for GitLab, and the last deliveries), and a run's progress (a percentage overall and per repository) and log. See [Choosing what is indexed](argus/README.md#choosing-what-is-indexed). |
| **Packs** | Knowledge packs, loaded like the models: the **pack library** (built packs on the host, the repository's `packs/` or `ARGUS_PACK_LIBRARY_DIR`) lists each with **Load** and **Unload**. Loading links the pack into Argus, instantly and without a copy, and only a pack built with Argus's embedding model loads (another's vectors would not compare). A pack not in the library can still be installed from a URL (with its SHA-256), updated from the published index, and removed. |
| **Explore** | What Argus holds, searched across every repository: **symbols and paths**; **references**, every line where a name is used, the definitions marked (as `find_references` answers a model); **code**, words in the files (`"a phrase"`, `prefix*`); and the **documentation** packs by their words, an API's name, or meaning. Each result opens: a file at its line, or a page of a pack. For "a tool found nothing: is it absent, named differently, or never indexed?". |
| **Monitoring** | Live probes of the gateway, Prometheus, Alertmanager, Loki and Argus, and links to Prometheus's and Alertmanager's own pages. |
| **Dashboards** | Ten dashboards, drawn by the app: the model and the gateway (LLM Overview, Stack Performance), the machine (Resources, GPU Hardware, Host & Containers), the services (Stack Health & Alerts, Logs), Argus and its index, and usage by person. See [Dashboards](#dashboards) below. |
| **Logs** | Every service's logs from Loki, newest first: by container, level (all, warnings and errors, errors) and text, over five minutes to a month, and **Live** for new lines as they are written. The filters are in the address, so a view can be linked. |
| **Alerts** | What fires now (from Alertmanager, with silenced ones marked), every time an alert fired over a day, a week or a month, and every rule with its state, severity, how long its condition must hold, and its query (the app's own disk rule says what it checks in words: it is not PromQL). The certificate alerts warn 30 days before it expires and are critical 7 days before. An alert that starts firing reaches every admin's bell, and by email (when set up) and the alerts webhook (**Settings → Notifications**), once per firing. |
| **Traces** | The slowest answers of a time range (an hour to a month), across people: when, who, the model, the whole time, the slowest step, tokens and the tools used (the sub-agents' too). Each opens its trace: the wait in line, getting ready, each round of the model with its tokens, cache share, first token and the engine's read and write speeds (llama.cpp's own timings when the stream carries them, else worked out from the times), each tool call, and each sub-agent with its time, tokens and tool calls; the slowest step is named, with why. The same trace opens from the timer under an answer in the chat. Times, tokens, sizes and tool names only: what was asked and answered is never shown. |
| **Settings** | Every setting, grouped and searchable: applied at once, or by a restart the app does itself. See [settings.md](settings.md). **Company directory (LDAP)** has a step-by-step guide with examples for OpenLDAP and Active Directory, **Test the settings** (each step, and exactly what is wrong) and **Try a person's sign-in** ([authentication.md](authentication.md#the-company-directory-ldap--active-directory)). **Chat bots** sets up the Slack, Mattermost and Teams bots and email in, and shows the address to give each platform ([integrations.md](integrations.md)). |
| **Audit log** | Every sign-in and every change to people or the index, with who, whom and from where. |
| **Compliance** | ISO 27001, SOC 2, HIPAA and the GDPR: each control with what meets it, its checks now (green, amber, red), the company's part attested, and the evidence for an auditor in one download. See [compliance](compliance/README.md#the-compliance-center). |
| **Quality** | How people rate the answers, over a day, a week, a month or three: per model and per assistant, the answers written, the share rated, the share rated up, and why they were rated down. The latest down-rated answers by title, model, reason and the person's words, never their content: a chat opens (read only, down to the rated answer) only when its owner shared it with the down vote, and each opening is audited. Below, the arena's leaderboard from the votes cast in the range. See [Quality](#quality) below. |
| **Sign-in** | Local accounts, the company directory ("Check the directory now") and company sign-in: OIDC or SAML, the identity provider (its issuer or SAML entity ID), its admin and required groups, and whether SCIM is on. |

Indexing, Packs and Explore talk to Argus through the app's server with
`ARGUS_KEY`, which never reaches a browser. With Argus left out they say
that it is not set up, and how to turn it on.

### Access: roles and rules

**Admin → Access** has its tabs. **Roles**: the built-in ones and your own,
who holds each (**Give it** to a person, a group, a directory group linked on
the Groups page, or everyone, maybe until a date), and **New role** from the
permission list; built-in roles cannot be changed. **Rules**: allows and
denies on models, tools, knowledge sources, repositories and features.
**Checker**: may this person do this, and why. **History**: every change.
Someone who holds an administrative permission through a role (and is not an
Owner) sees the admin pages of what they hold, and only those: a Supervisor
role with `monitoring.read` and `logs.read` sees Monitoring, Dashboards,
Alerts, Logs and Traces.

**Many people** changes many people at once: people by name and everyone in
some groups (a directory group's members too), and up to 20 changes for each:
give or take a role (maybe until a date), add to or take out of an app
group, allow or deny a feature (or leave it to their roles and groups), and
disable or enable accounts. **Dry run** says person by person what would
change and what is so already; **Make the changes** does exactly that, as one
change in the history. It is refused whole if it would leave nobody who
holds Owner or take your own `access.manage` away, and it never disables
your own account. Groups' members need `groups.members`, accounts
`people.disable`; at most 1,000 people at once.

A role given **over** one group (a team manager: People admin or Billing
for the members of one group) reaches only that group and its people: the
People and Groups pages list only them, anyone else is not there, and making
people or groups, or changing who is in a group, stays with admins who
manage everyone.

**Requests for access.** Anyone can ask for a role or for a model, tool,
feature or knowledge source they cannot use (Your account → **Ask for
access**, with why). Everyone who holds `access.manage` hears of it in their
bell and answers under Access → **Requests**: a yes makes it so for that
person (a role they hold, or an allow of the resource, their own deny of it
taken away), maybe until a date; a no can say why. Either way the person's
bell says so, and the History tab and the audit log keep who answered. The
audit log can show what one person did (`?actor=` their id): the duties
handed out, and how each was done.

Rules on repositories hold in Argus too, for the chat's Argus tool and for
coding agents with an API key alike: a repository is named
`gitlab:group/project` (or a pattern, `gitlab:team-a/*`), and Argus asks the
app which of what GitLab lets the person read their rules leave them
(kept five minutes). Without `code.read` they read no code. GitLab's own
rights stay the ceiling: no rule here opens a project GitLab keeps from them.

What each person may do is decided in one place, from three things:

- **Permissions**: a fixed list of what can be done, in two kinds.
  Administrative ones (`people.manage`, `models.manage`, `audit.read`,
  `settings.manage` and the rest) come only from roles; feature ones
  (`feature.chat`, `feature.api_keys`, `feature.code_arena`,
  `feature.pictures` and the rest, and `code.read`, `review.run`) come from
  the **Member** role, which everyone holds, and rules may give or take them.
  Taken from someone: `feature.chat` stops new chats and messages;
  `feature.code_arena` refuses Code Arena; `feature.web_search` takes the web
  and research tools; `feature.pictures`, `feature.video` and
  `feature.speech` take those tools, the speech routes and those models from
  their API keys; `feature.memory`, `feature.tasks`, `feature.share_links`
  and `feature.assistants.share` stop new memories, tasks, share links and
  sharing assistants; `code.read` takes the Argus tool. What they already
  have stays theirs to read and delete.
- **Parts of permissions.** Each broad administrative permission has parts a
  role can hold alone: `people.manage` holds `people.create`, `people.edit`,
  `people.delete`, `people.passwords` (passwords, two-factor, signing out),
  `people.keys` (keys and API access) and `people.limits`; `groups.manage`
  holds create, edit, delete, members and policies; `access.manage` holds
  roles, assign, rules and requests; `models.manage` holds add, edit, delete,
  download, servers, hours and access (`models.load` apart); `tools.manage`,
  `plugins.manage`, `knowledge.manage` (read, add, edit, sync, delete) and
  `storage.manage` (read, cleanup, delete, quota) theirs; `settings.manage`
  one per group of settings (`settings.chat`, `settings.models`,
  `settings.safeguards`, `settings.tools`, `settings.integrations`,
  `settings.data`, `settings.gateway`, `settings.argus`, `settings.branding`,
  `settings.restart`); `signin.manage` the directory, company sign-in and
  moves; `argus.manage` repos, index, packs and webhook; `logs.read` holds
  `traces.read`. Holding any administrative permission of an area lets one
  read that area (who resets passwords sees the people). Features have more
  parts too: Python, the canvas, 3D models, plans, deep research, comparing
  models, attaching files, making assistants, saving prompts, exporting one's
  own data, company knowledge, and choosing the chat's model.
- **Roles**: named sets of permissions. The built-in ones are **Owner**
  (everything), **People admin**, **Model admin**, **Billing**, **Auditor**
  (every `.read`, never a change), **Argus admin**, **Room moderator**,
  **Review admin**, **Team manager** (a group's people, their credit and
  limits, its spend; given over a group), **Supervisor** (dashboards, alerts,
  logs, usage and the audit log, read-only), **Operator** (takes alerts, loads
  and unloads models, cleans up storage), **Safety officer** (the safeguards),
  **Compliance officer** (Admin → Compliance: the controls and their checks;
  reads the audit log; see [compliance](compliance/README.md)) and **Member**. A role is held by a person, by a group (an
  app group, a directory group linked to one, or a SCIM group) or by
  everyone, maybe until a date.
- **Rules**: allow or deny one action on a resource (a model, a tool, a
  knowledge source, a repository, or a feature permission), by its name or a
  pattern (`*` for anything but a slash, `**` for anything), for a person, a
  group, a role's holders or everyone, maybe until a date, with a note.

How a question is decided: a disabled person may do nothing. Otherwise the
rules and roles that match are ranked: the person's own over their groups',
their groups' over a role's, a role's over everyone's; an exact resource over a
pattern, and a longer pattern over a shorter one. At the most specific rank that
matches anything, a deny wins over an allow, and the ranks below do not count.
So a person's own allow opens what their group's deny closes, on purpose, for
exceptions. A model, a tool or a knowledge source that no allow rule names is
everyone's, as each was before anyone chose who may use it. **Owner** may do
everything and use everything, unless a rule for that person themselves denies
it.

**Admins are Owners.** The **Make admin** switch on a person's page gives or
takes their own Owner role. Nobody can be left out: the first admin
(`ADMIN_USERNAME` in `.env`) always holds Owner whatever the data says, a
change that would leave no active Owner is refused, and nobody can take their
own `access.manage` away.

**Who may use it**, on a model's, a tool's or a knowledge source's card,
writes rules on that resource: everyone (an allow for everyone), admins (an
allow for the Owner role) or the chosen groups (an allow for each). Rules for
single people, and denies, are left as they are. **API access** on a person's
page is that person's own deny of `feature.api_keys`.

Each change is kept with what it was before and after, and raises a version
every replica reads with each request, so it applies everywhere from the next
request.

**Upgrading to v5.9** moves every choice there was into roles and rules, and
the same people may do the same things afterwards: each admin holds Owner,
everyone holds Member, each tool's, model's and knowledge source's audience
became its rules (admins only: an allow for Owner), and a person whose API
access was off has a deny of `feature.api_keys`. A folder's and a website's
documents are read by whom the rules on their source allow, decided at each
search.

### Customizing tools

On each card in **Admin → Tools**, **Customize**:

- **What people see**: a name and description of your own.
- **Words to the model**: when to use it, what to avoid, house rules; added to
  the tool's own words, or in their place.
- **Each function**: offered or not, asking the person first or not, a
  description of your own, **fixed arguments** (always sent as you set them,
  and not offered to the model: a project, a region, a limit) and **default
  arguments** (filled when the model leaves them out).
- **Calls a day, per person**; past it, the model is told to answer without
  it.
- **For some groups**: on in new chats, asking first, calls a day, over the
  company's (on in new chats if a group says so; asking first only if every
  group of theirs does; the most calls a day of their groups).
- **Try a function**: called as you, with the saved settings.

Each card shows the tool's calls this week, how many failed, and their time.
A server's, a plugin's or your own tool that fails 25 calls in a row is
turned off, and the admins hear of it (Settings → Chat → **Turn off a failing
tool**).

**Toolsets** (the page's header): named sets of tools, for everyone or some
groups. People switch a chat to one in its **Tools** menu; a group's new chats
can start with one.

**New tool** makes a tool without code:

- **An HTTP request**: its method and address (the host is fixed; the
  model's arguments fill only `{name}` places in its path and query, escaped),
  headers kept encrypted and never shown again (a key, a token), a JSON body
  with `{name}` inside its strings, and the part of a JSON answer to keep
  (`data.items`). Mark one that changes something to ask the person first.
- **A prompt on a model**: a prompt with `{name}` places and a system prompt,
  run on the model you choose; its answer is the tool's result.

**Try it** runs the form's tool before it is saved. Once made, it is a card
like the others: on or off, for whom, customized.

### Safeguards

**Admin → Safeguards** holds the rules that guard what goes to the models and
what comes back. Each rule:

- **looks for** one thing: secrets (private keys, cloud and service tokens,
  passwords; some kinds or all), personal data (e-mail addresses, phone and
  card numbers, IBANs, IP addresses, social security numbers, and health and
  identity data: dates of birth, medical record, NHS, national and Medicare
  numbers: [compliance](compliance/README.md#personal-and-health-data)), words and
  patterns, topics the model judges (built-in harms, or topics you describe:
  "legal advice: what someone should do in their own legal matter"), prompt
  injection (its usual signs and hidden text, and the model's reading if you
  tick it), links (domains refused, or only some allowed), or files (types
  and size);
- **looks** at questions, attached files, answers, what goes to tools and
  what tools bring back, and API keys' requests (the gateway asks before
  each one);
- applies to **everyone**, or to some **groups and people**, and to some
  **models** or all (`@external` names every model on another server or
  provider: personal data can be masked for cloud models only);
- **does** one thing: **Block** (the person is told why and may ask for a
  review; a strike counts towards suspension and rings the admins' bell),
  **Mask** (a marker in place of what was found; secrets are never stored,
  the rest is masked on its way to the model and kept as written in the
  chat), **Warn** (the person confirms before it goes), **Ask for a reason**
  (recorded with the event), **Log only**, **Send to another model** (the
  company's own, say), or **Allow** (exempts what it covers from the rules of
  its detector below it: a group that may paste keys, a team that may name a
  project). **Monitor only** records what a rule would have done and does
  nothing: try a rule for a week before it bites.

Rules run top to bottom (the arrows move them); the first Block ends it.
The model reads a text once for every topic and injection rule together
(Settings → Safeguards → **The model that judges**: a small model is best);
when it cannot be loaded, a text a rule would block waits rather than going
unread. Start from a **template**: secrets, secrets in answers, personal data
to cloud models, classification labels, harmful requests, legal and medical
advice, jailbreaks, prompt injection in tool results, file-sharing sites,
programs, competitors.

The **Test bench** shows which rules fire for a text and what they would do,
without sending or recording anything. **What they did** lists every time a
rule acted (who, where, the rule, the kinds it found, never a secret or
personal data, and a little of the text for words, topics, links and
injection), with a chart of the last 30 days. **Reviews** has people's
requests to review a refusal: **Allow it** (the same message passes that rule
for a day) or **Keep it refused**, with a note; they hear either way.

Who may: `safeguards.read` sees it all and uses the bench,
`safeguards.manage` changes the rules, `safeguards.review` answers reviews;
the **Safety officer** role has them, with the audit log. The limits on
messages, files, pictures and deep research per person stay under Settings →
Safeguards.

### Models

llama.cpp runs in **router mode**: one server that knows several models and
holds up to **Models loaded at once** (under Settings, default 2) of them.
Loading and unloading is an API call, not a restart. The page opens with **the
engine**: how many it holds, which are kept loaded, and how full each GPU
would be with them.

- **Keep loaded** (a switch on each engine model) pins a model: it loads now,
  loads again when the engine starts, and comes back whenever it is not loaded.
  At most **Models loaded at once** can be kept. Keeping one is refused when their
  caches and buffers together cannot fit the GPUs and RAM; when they fit only
  by putting layers in RAM (two on one GPU that holds one, say), it is kept
  with that warning. The list is `config/engine/keep`, which the engine reads
  when it starts; without it, the `.env` model is kept.
- **Tools** (on each chat model's card) is how the chat and Code Arena give
  that model its tools. **Direct**: each tool called on its own, no
  `run_code`. **As code**: `run_code`, and the tools a program may call named
  in its instructions as Python functions (`arena.calculator.calculate(expression)`)
  instead of every definition sent with each request: fewer tokens, many calls
  in one step ([chat.md](chat.md#what-it-does)). In Code Arena, the servers'
  tools (Arena's, Argus's, the person's) go as code and its own files, search,
  git and shell stay direct. **Both**: every tool and `run_code`, a program for
  many calls and a direct call for one or two. **Automatic** (until an admin
  chooses) is direct for the model for small steps and for a model whose name
  says 8B parameters or fewer (`Qwen3-4B`, `llama-3.1-8b`, `SmolLM2-135M`), as
  code for the rest: measured, a 27B model passed 10 of 10 lookups as code, 6
  both and 4 direct, and a 4B one 1 either way
  ([measurements](measurements/tool-modes.md)).
  It applies from the next answer (Code Arena's next start) and is audited
  (`model.tools_mode`). Deep research keeps its tools direct, and so do the
  tools that act in the chat's page (a question to the person, plans, memory,
  the canvas, the browser); a picture or file a program's call makes is the
  person's under the program's card, and costs what a direct one does.
- **Loaded on request.** While a place is left beside the kept models, any other
  model loads when someone asks for it: a chat, an API key, a coding agent. Its
  first answer waits while it loads. At the limit, the app first unloads an
  idle model that may make room, whatever its size (the smallest first: it
  loads again quickest). Three never make room, so the models everyone relies
  on stay: one kept loaded, the one new chats use (Settings, or the working
  hours', else the first kept; with none of them, the one new chats have been
  getting), and the model for small steps while a place is left beside it for
  the others (with fewer places, it shares the last one with them: see **Two
  models at once**). While one of them is not loaded, its place is kept for it
  (the app loads it again), unless an admin unloaded it. When none that
  may make room is idle, the request (an answer, a title, the safeguards'
  check) waits up to a minute for one, then says the engine is full; when each
  place is taken by, or kept for, one of the three, it says so at once, and
  the chat's menu does not offer the others.
  A model is idle only when nothing is on its way to it either: the app's own
  requests until they end (side requests too), and an API key's for 10
  seconds after the gateway let it through, or after the model loaded for it
  (by then the engine says its slot is busy). A model the app unloads (to make
  room, or an admin's **Unload**) reads **Unloading…** at once, on every replica,
  though the engine lists it as loaded until it has stopped (within 10 seconds:
  one still answering then is killed, which reads **Not loaded**, not **Could not
  load**). It still takes its place meanwhile, and a request for it, or for a
  model that needs its place, waits until it has stopped (15 seconds at most),
  instead of reaching an engine that would load it by unloading the model used
  least recently. Each **Loading…** and **Unloading…** says why and for how long:
  loaded by an admin (by name), for a chat or an API request, kept loaded, the
  model new chats use, making room for another, working hours.
  The app never leaves the choice to the engine, which would unload the model
  used least recently, whichever it is. An API key's request (a coding agent,
  an IDE) gets room the same way: the gateway asks the app before sending it
  (its guardrail), and the app makes room, or the request waits, or it is
  refused with the reason. A place made for a model is its own until the
  engine loads it, so two requests at once for two models take two places.
  The app's own loads (a kept model back, the one new chats use, the model for
  small steps) make room the same way. Should the engine still unload a model
  by its own choice (a request let through while the app is down, with the
  guardrail's `fail_open`), a kept one comes back, and so does the one new chats
  use, once a model that may make room has been idle for a minute (so an agent
  pausing between its requests is not pushed out for it). One the engine pushes
  out twice within ten minutes waits before it comes back (1, 2, 4… minutes),
  and its card says why: the engine holds fewer models than **Models loaded at
  once** says (its `--models-max`, or too little GPU memory for them together),
  and two models would otherwise push each other out for ever. After an admin's
  **Unload**, it stays unloaded. In the chat such a model reads **Loads when
  asked**, and asked for with no place left, the chat says an admin unloaded it
  and which models hold the places.
  When every place is kept, no other model loads on request, and the chat says
  so, naming the models that hold them. Keeping or unloading a model never
  restarts llama-server (it always loads what a request asks for, the app having
  made room); only a changed model list or **Models loaded at once** does.
- **Two models at once** (the default). Someone who picks a small model gets it
  loaded beside the big one, instead of unloading the big one for everyone:
  each model has its own line in the chat, so both answer at once. llama.cpp
  places a model loaded on request in what the loaded ones left of the GPU
  (automatic placement), else partly or wholly in RAM, where it is slower. Each
  model is a process of its own: one that does not fit fails to load and says
  so on its card, and the loaded ones go on. The trade-off is memory: the
  second model takes GPU memory or RAM the first could have used, and a model
  placed by hand (layers and experts set on its form) needs all its memory free
  when it loads, so a big model that loads after a small one took its room
  fails. Keep the big model loaded (**Keep loaded**): it loads first when the
  engine starts, and models asked for take what it left. With two at once and
  a model for small steps set, the small model shares the second place with
  the models loaded on request: the app loads it there while the place is
  free; it makes room once idle for a model someone asks for, and while that
  one sits there each small step uses the answer's own model (slower, and on
  the big model's slots) rather than wait; it loads again once that one has
  been idle five minutes. With 3 or more, it keeps a place of its own beside
  the big one, and one is left for the others.
  With **Models loaded at once** at 1, only a kept model stays, and any other
  loaded one, the one new chats use included, makes room once idle: a request
  for another model waits for the loaded one to be idle, a minute at most, then
  is refused. While people keep the loaded model busy (new answers start as
  others end), those on another model are refused each time, not served in
  turn. Every switch unloads the other's model (measured:
  Qwen3.8-Flash-Next reloads in 15 to 20 seconds from the page cache, and its
  cached prompts are lost), and a model asked for while the other still loads
  can be stopped mid-load and read **Could not load**. With a model kept loaded
  and one at a time, no other model loads at all. Raise it to 2 or more.
- **Could not load** is a load that failed. A model the app stopped (to make
  room, or an admin's **Unload**) that the engine had to kill, as it still
  loaded or answered 10 seconds later, reads **Not loaded**: the engine marks
  it failed, but nothing is wrong with it. The app tries a model that failed again after a minute,
  then after 2, 4, 8, 16 and at most 30 minutes, once each time: a kept one
  is loaded again by the app, any other by the next question asked of it, in
  the chat or with an API key (the questions of the next half minute go too,
  while it loads, unless it is seen failing again). Meanwhile the chat, and an
  API key's request, say it could not be loaded just now, and its card says
  from when it is tried again; **Load** tries at once (should it
  fail, the next wait is the longer one). A model that loads is known to be
  fine again.
- **The model for small steps** (Settings → Model → **Model for sub-agents
  and small steps**) does the many short steps around an answer: sub-agents,
  chat titles, compaction summaries, the safeguards' check, and Auto in the
  chat's model menu. It is only fast when the engine holds it beside the big
  model: set **Models loaded at once** to 3 or more (it keeps a place of its
  own, and never makes room for another), or keep it loaded (**Keep loaded**).
  At 2 it shares the second place with the models loaded on request (see **Two
  models at once**). It is used only while it is loaded, or the engine has a
  free place for it: else each step uses the answer's own model, rather than
  wait for another model to make room. Its card reads **Small steps**. The
  page, and the setting itself, warn while it is not at the gateway, shares
  the last place with the others, every place is kept for other models (it
  cannot load, so each step uses the answer's own model), or the engine holds
  one model at once (a step waits for the loaded model to be idle). When the
  safeguards' check cannot have its model (the engine stays full for a
  minute), the message is refused with a note to try again, never let through
  unread; it is not a strike. On the GPU beside the big
  model it takes some of the big one's memory (for a mixture of experts, more
  experts in RAM); a model on another GPU server costs this engine nothing.
  Who may use it is set on its card as for any model: Auto is offered only to
  them, and for the others each step uses the answer's own model.
- **Working hours** change the kept models by day and hour: a small fast model
  in busy hours, the big one at night. Each has days, a time from and until
  (until before from runs past midnight; the same is all day), the models kept
  loaded meanwhile (at most **Models loaded at once**, checked against the GPUs and
  RAM as pinning is), and the model new chats start on. While one is in force,
  its models are kept instead of the pinned ones: they load, and the models only
  the previous window kept unload to make room. When it ends, the pinned models
  are kept again. Where two overlap, the one listed first applies. Times follow
  **Settings → Model → Time zone of working hours** (an IANA name; default UTC).
  The engine summary says which are in force and until when; a model they keep
  reads **Kept by working hours**, and cannot be unloaded by hand meanwhile.
  Chats that chose a model keep it; only new ones (and chats that chose none)
  start on the working hours' model.
- **Default models** (shown while one is missing): the first chat model (`MODEL`),
  the picture, video and embedding models and the speech server's, each with where
  it comes from. The app downloads none on its own: **Download the default models**
  does (from Hugging Face, under **Downloads**), as the installer's download
  choice does once at the first start (`DEFAULT_MODELS=download`); or copy their
  files into the library. `MODEL` is added and kept loaded once its files are
  there, however they came.
- **Find on Hugging Face** searches GGUF models (most downloaded first), opens
  one to list its files grouped as models (a split model's parts together, by
  quantisation, vision projectors apart) with their sizes, whether each fits
  this machine (the GPUs, GPUs and RAM, or too big) and whether it is in the
  library already, and downloads the ones ticked into the library
  (`<library>/<owner>/<repository>/`). **Downloads** shows each one's progress,
  speed and time left; one runs at a time, a paused or cut off one goes on from
  where it got to (HTTP range), every file is checked against Hugging Face's
  SHA-256 and only then renamed into place, so the library never lists half a
  model. A finished one has **Add as a model**, which opens **Add a model** with
  its file chosen (a speech model the speech server fetched: that model), taken
  as what it is. The app needs the library writable by its user (`LLM_UID`) and,
  for gated repositories, `HF_TOKEN` of an account that accepted their terms.
  A download is refused when the disk would keep less than 2 GB free.
- **Snapshots for vLLM.** A repository with safetensors weights and a
  `config.json` also offers its **snapshot** (`snapshot` in
  `GET /api/admin/models/hf/repo`), with its size shown before anything is
  downloaded and what it leaves out: the `original/` and `metal/` copies, GGUF
  files, other weight formats and the read-me, which vLLM never reads (for
  `openai/gpt-oss-120b` that is about two thirds of the repository). It
  downloads (`POST /api/admin/models/hf/downloads` with `snapshot: true`) into
  `<library>/hf/<owner>/<repository>@<revision>/`, every file at the same
  commit and checked as above. `GET /api/admin/models/library/snapshots` lists
  the snapshots with their size and whether each is whole (its config, its
  weights, every shard its index names, nothing still downloading); only a
  whole one can be a vLLM model.
- **vLLM models** (`engineKind: vllm` on `POST /api/admin/models`; the form's
  fields come with the next step): a snapshot, its GPUs (empty: the ones the
  advisor proposes for this machine, kept on save), copies (`replicas`, each on
  its run of the GPUs; a conversation stays on one), its share of each GPU
  (`gpuMemoryUtilization`, 0.90), quantisation, speculative decoding (`mtp`,
  or `eagle3` with a draft snapshot), a CPU tier for its cache (`kvCpuGb`),
  whether it may sleep, and a `fallback` model. **Answers at once** is its
  `--max-num-seqs` and the cache type its `--kv-cache-dtype` (fp8). A setting
  its kind does not have is refused, as are GPUs that cannot hold it, a context
  that does not fit, or a share kept for another model kept loaded there.
  `POST /api/admin/models/advice` with `engineKind: vllm` shows its fit,
  cache and recommended context and answers at once. It loads when asked for
  (a large one takes minutes, and the chat says why it waits), making room only
  among idle models on its GPUs; it takes none of llama.cpp's places (Models
  loaded at once). The models list shows each engine (`engine.instances`) and
  why one is not running.
- **Load** loads a model now, beside the kept ones (refused when every place is
  kept). At the limit the app makes room for it, as for a request: an idle model
  that may make room unloads first, and is waited for until it has stopped. When
  every place is held by models that never make room and some are not kept (the
  model new chats use, the one for small steps), the page asks to **Load
  instead of** one of them: that one unloads, and the loaded one keeps its place
  (**Holds its place**) until an admin unloads it or loads another instead; the
  one it replaced comes back then. **Unload** unloads it, and stops keeping it.
  Load and Unload each show only on their own button that they work. Answers
  wait while a model loads: seconds when its weights are in the page cache,
  minutes from disk.
- **GPUs.** With two or more GPUs, a model's form asks which it runs on: all of
  them (llama.cpp splits it by layer), or some (`device = CUDA0,...` in its
  preset). The engine numbers GPUs as `nvidia-smi` does
  (`CUDA_DEVICE_ORDER=PCI_BUS_ID`). The memory estimate uses only the GPUs
  chosen, and the image server's share only on the first. Two models kept side
  by side run best on different GPUs. The `.env` model runs on all of them
  unless `LLAMACPP_EXTRA_ARGS` has `--device CUDA1`.
- **Other servers and providers.** Another machine's OpenAI-compatible engine
  (llama.cpp, vLLM, SGLang, another gateway), or a cloud provider's API with
  the company's key (OpenAI, Anthropic, Google Gemini, OpenRouter, DeepSeek,
  Mistral, Groq, Together, xAI: the buttons fill in the address), serves models
  beside this one's: to the chat, API keys and Code Arena, priced and allowed
  like any model. **Add a
  server** takes its address (usually ending in `/v1`) and its API key,
  **finds its models** (with the window each reports), and the admin chooses
  which the gateway serves, under which names, with their context, longest
  answer, tools, thinking and images. The app registers them in LiteLLM with
  the server's address and key; people use them from the chat, their API keys
  and agents, with the same access rules and spend as any model.
  - A name the gateway has already (a model here, or on another server) makes
    it **a second copy** of that model, and the copies are a pool: LiteLLM sends
    each request to the least busy copy, and the chat can use the model while
    this machine's copy is not loaded. **At once** (a server model's parallel
    slots; this machine's are its model's **Answers at once**) caps what one copy is
    sent at a time and weighs the copies: a server with 8 slots takes twice the
    requests of one with 4.
  - The key is kept encrypted with the app's key ring (`APP_KEY`) and never
    shown again; leave it empty when editing to keep it.
  - **Health.** Each server's card says whether it answers (checked every 30
    seconds when the page asks), why not, and which chosen models it no longer
    lists. A server must answer to be added.
  - **https.** Its certificate is checked against the public roots and the
    stack's bundle, so a server signed by a private CA is trusted once that CA
    is in `config/ca` (then `docker compose up -d`). Only for a self-signed
    server with no CA to trust, **Check its certificate** can be turned off for
    that server.
  - The gateway is brought in step every minute, so a gateway that was down
    or restarted catches up by itself.
- A model that **could not load** (an incomplete download, a file this
  llama.cpp cannot read, too little GPU memory) says so on its card; the reason
  is in the engine's log (`docker compose logs llamacpp`). It is not tried
  again on its own; when every kept model failed, the `.env` model is loaded
  in their place, and the chat works on.
- **Add a model** is one button and one dialog for every kind of model; **Edit**
  on a model's card opens the same dialog. Its **File or model** lists the
  **model library** (`LLAMACPP_LIBRARY_DIR`, searched three levels deep; a split
  model is listed once, by its first part) by what each file is, and the models
  the speech server has downloaded. **Each GGUF file is read for what it is**
  (its header and tensor table, not its name): a dense or mixture-of-experts
  language model, with full, hybrid (a cache in some layers only),
  sliding-window or recurrent attention; a picture or video model's diffusion
  model or text encoder; or an embedding model, reranker, vision projector,
  draft head or LoRA adapter. A safetensors (or other stable-diffusion.cpp)
  file is known by its name: a diffusion model (Wan, LTX, Hunyuan Video and
  Cosmos make video), a text encoder or a decoder. The dialog takes the choice
  as that kind (**Add it as**: a chat, picture, video, speech to text or text to
  speech model), which can be changed; it says when the file reads as another
  kind and offers to change back. That kind's own sections follow: the chat
  model's below, the picture, video and speech models' under
  [Picture, video and speech models](#picture-video-and-speech-models). A text
  encoder or decoder is put in among a picture or video model's files by its
  role. Embedding models, rerankers, projectors, draft heads and adapters are
  not added as models of their own (they belong to Argus or a model's own
  settings): chosen, they say what they are for. A chat model needs the
  llama.cpp engine; a split model with a part missing is refused as
  incomplete. A saved model keeps its kind when edited.
- **The form asks what that kind of model has, within its limits**, and shows
  them: the context from 4,096 to what it was trained for (up to 4× with YaRN,
  when its file does not stretch it already); the longest answer, at most the
  context less 1,024 tokens for a prompt; the cache types its attention heads
  can take (a quantized cache needs flash attention, which covers heads of 64,
  80, 96, 112, 128 and 256); experts in RAM only for a mixture of experts;
  drafting with multi-token prediction only for a model with its own
  prediction layer or a draft head made for it (same architecture, width and
  vocabulary); a vision projector only one made for its width; thinking and
  tools only when its chat template has them. The API runs the same checks on
  save.
- **It starts from what fits this machine**, whatever it has: no GPU, one, or
  several of any size and vendor. The GPU memory and RAM come from Prometheus
  (the NVIDIA exporter), else from the hardware file an engine writes
  (`services/gpu-probe.py`: `nvidia-smi` or `rocm-smi`); with `LLAMACPP_BACKEND=cpu`
  the models have no GPU and run from RAM. Each GPU's room is its memory less
  what the servers beside the chat models keep **on that GPU** (the picture
  server, `IMAGEGEN_MAX_VRAM`, on its model's GPU, else `MEDIA_GPU`, else the
  first; the embedder on `EMBED_GPU` when it has one) and the 1 GiB llama.cpp
  keeps free. The GPUs a model runs on are named for llama.cpp's backend
  (`CUDA0`, `ROCm0`, `Vulkan0`, `SYCL0`, `MTL0`). The form estimates
  weights, cache and buffers as it is filled in (within 1% of llama.cpp's own
  estimate for dense and hybrid models, measured), and recommends the largest
  context that keeps the model on the GPU (a mixture of experts: its experts in
  RAM as needed), 1,024-token prompt steps when experts are in RAM, and MTP for
  a dense model that fits. **Automatic placement** (the default) leaves the
  layers to llama.cpp's fit, which places them for the memory free when the
  model loads; by hand, a setting that would not fit is refused. A GPU power cap
  far below the card's own (`GPU_POWER_LIMIT_W`) is named for a model that runs
  all on the GPU: it is what limits one.
- The added models share one cache between the answers in parallel
  (`kv-unified`, as the `.env` model), **each slot held to its share of it**
  (`kv-unified-per-slot`: the context divided by **Answers at once**). A long
  conversation therefore never pushes the others' out of the cache, nor is
  pushed out by them (before, a 209,000-token session in a 262,144-token cache
  left 4 slots 53,000 tokens between them, and each turn of anyone's evicted
  someone's prefix: 20 to 35 minutes to read it again). The gateway advertises
  that share as the model's window (`max_input_tokens`), so the chat and Code
  Arena compact before they reach it, and the card says it ("262,144 tokens of
  context, 65,536 a conversation"). For longer conversations, give the model
  fewer answers at once (2 slots: 131,072 each) or more context. Each
  conversation keeps its own slot ([the token cache](#the-token-cache)). They
  run with `LLAMACPP_THREADS`. Sampling left empty is the model's own
  recommendation from its file, which the engine applies.
- A model on **automatic placement** (`fit = on`) leaves room for the picture
  model kept loaded: its fit target (`fit-target`, llama.cpp's own margin of
  1,024 MiB) grows by that model's GPU budget and 1 GiB (FLUX.2-klein: 4,096
  MiB in all) on the picture model's GPU, so the picture server loads beside
  it instead of with its weights in RAM. Keeping another picture model loaded,
  or none, changes the presets, so the engine restarts. A model's own
  `fit-target` line wins.
- **More engine options** are `key = value` lines with llama-server's long
  option names. Options the app or the engine owns (files and paths, the port,
  the key), those the form sets, and names this engine does not know are
  refused: a preset with an unknown option stops the whole engine. The known
  names are this llama.cpp build's (`src/Llm.Api/Models/engine-options.txt`,
  with the command that regenerates it after an engine upgrade). Should the
  engine refuse the list anyway, it serves the `.env` model alone until the list
  changes, and the models it left out say so on their cards.
- **Remove** takes a model off the engine, the gateway and the chat. Its file
  (and its vision projector and draft head) stays in the library unless
  **Also delete its files from disk** is ticked in the dialog: then those no
  other model uses are deleted too, and the message says how much was freed
  and what stayed and why.
- **Adding, editing or removing a model restarts the engine** so it reads the
  new list: a few seconds, then the loaded model loads again. The gateway
  learns of the change at once (or within a minute, if it was down).
- The `.env` model is always there; change it under Deployment. Image models
  and cloud models are listed too, for who may use them.
- **Who may use it**: everyone, admins, or chosen groups (Owners always may;
  it writes rules on the model: [Access](#access-roles-and-rules)).
  The chat lists only the models a person may use, and the server refuses the
  others. API keys follow within a minute: a key's model list at the gateway is
  kept to the models its owner may use, so a call to another answers 403.
- People choose among the loaded models. One that is not loaded is listed
  greyed out, "Not loaded now".

The app writes `config/engine/models.ini` (the models added here),
`config/engine/active` (the model to keep loaded) and
`config/engine/targets.json` (which model Prometheus scrapes). The engine and
Prometheus read them; nothing else does.

#### The token cache

The engine keeps each conversation's prompt in a slot, so its next turn reads
only what is new. A model has as many slots as its **Answers at once**.

- **Each conversation keeps its slot.** The app sends each turn back to the
  slot that holds its conversation (llama.cpp's `id_slot`, which the gateway
  passes on), while that slot is idle. A new conversation takes the slot used
  least recently: it holds another chat's start, so the system prompt and the
  tools (the same for everyone, thousands of tokens) come from the cache too. A
  busy slot is never chosen: the app goes by what the engine said of its slots
  within the last second, so API keys' and other replicas' requests count, or
  asks it and waits half a second for its word. While the engine reads a long
  prompt it may not answer for seconds: then what it said within the last 5
  seconds goes, else the turn goes without a slot (the engine gives it an idle
  one). With none idle, the turn goes without a slot and the engine gives it
  the first that frees. A model with one slot, or with copies on other GPU
  servers (the gateway picks the copy), is left to the engine.
- **Every slot serves answers**: a model runs as many answers at once as it
  has slots (4 slots: 4 answers).
- **Side requests go to the last slot first.** From 3 slots, chat titles, the
  safeguards' check, compaction summaries and Auto's choice go to the last
  slot, which conversations take only when all the others are busy, so they
  seldom push a conversation out of its slot. While it is busy (a long
  summary, an API key's request, an answer), a side request takes another idle
  slot, one no conversation holds first, rather than wait behind it. With 1 or
  2 slots, the engine places them.
- **A sub-agent keeps a slot too**, as a conversation does: each of its steps
  reads only what its last tool call added. An answer runs no more sub-agents
  at once than its model runs answers (and **Sub-agents at once**).
- **Idle slots keep what they hold** (`no-cache-idle-slots`, in every model's
  preset). By default, with one cache for all slots, llama.cpp moves idle slots
  to RAM and empties them at every new request, and a hybrid model's never come
  back (measured: with each chat in its own slot, not one token then came from
  the cache). With each slot held to its share of the cache
  (`kv-unified-per-slot`), no request needs another slot's cells, so emptying
  idle slots would only move a long session's prefix to RAM and back at each
  turn: it stays in its slot, on the GPU, whoever else is answered.
- **Checkpoints.** A hybrid or recurrent model cannot cut its state back to an
  earlier point of a prompt (a retry, an edited question, a changed tool list):
  it keeps copies of its state in RAM to go back to. Each such model's preset
  keeps 4 a slot (`ctx-checkpoints = 4`; llama.cpp's own is 32), which read as
  fast as 32 in every test, for an eighth of the RAM. `cache-reuse` (moving
  matching chunks into place) does not work for these models; llama.cpp says
  so in its log and goes without.
- **Conversations' caches on disk** (Settings → Engine: **Disk for
  conversations' caches**, 200 GB by default, and **Keep conversations'
  caches for**, for ever by default). Below the engine's RAM cache, on
  `KV_CACHE_DIR` (a fast disk, an NVMe; `DATA_DIR/kvcache` by default): when a
  conversation's slot goes to another, the app has the engine write what the
  slot holds (its prompt read, its state, checkpoints and all) to a file of
  its own first; when the conversation comes back to a slot that does not hold
  it, that file is read back into the slot before its turn, so the engine reads
  only what is new, however long after, and across the engine's restarts (a
  long conversation comes back in seconds instead of minutes). It holds for
  chats, sub-agents and **agents through API keys** alike: an agent's session
  is known by its key, its model and how its conversation starts (the system
  prompt and the first question), and goes back to its slot each turn as a
  chat does. The engine's script keeps the folder within its size and age,
  the least lately used first out; a file that cannot be read back (the
  model changed) is skipped, and the prompt read as it would be without.
  Turning it on or off restarts the engine.
- The model's **More engine options** can set each of these otherwise
  (`cache-idle-slots = true`, `ctx-checkpoints = 8`, `cache-reuse = 0`).
- **Its card says what the cache keeps and costs**, for example "Token cache:
  4 slots, each keeping a conversation; small steps (titles, checks,
  summaries) go to the last first · 4 checkpoints a slot of 112.2 MiB: up to
  1.8 GiB of RAM".

**What it costs**, measured with Qwen3.8-Flash-Next (hybrid, 4 slots) on an
RTX 3090:

| What | Cost |
|---|---|
| RAM for checkpoints | 4 a slot × 112.6 MiB = 450 MiB a slot, 1.8 GiB for 4 slots (32 a slot: 3.6 GiB a slot) |
| GPU memory for one more slot | about 113 MiB, its recurrent state (worked out from the file; the context's cache is shared by the slots, so it does not grow). Measured: 21.0 GiB in use at 4 slots, the same as at 2 |
| RAM for the prompt cache | up to 8 GiB per loaded model (llama.cpp's `cache-ram`: each model is a llama-server process of its own), so 16 GiB with two loaded, the default now |
| Price | Cached input tokens are charged at the model's cached price. A model added here registers none, so the gateway charges them nothing (measured: 3,912 cached tokens cost $0), and so does the cost under an answer in the chat. At Flash-Next's 0.20 in and 0.80 out per Mtok, 1,000 turns of 10,000 prompt tokens cost $2.00 of input without the cache; with 77% from the cache (each conversation in its slot), $0.46: $1.54 saved. At a cached price of a tenth (0.02), $0.61: $1.39 saved. Output is the same either way. |

**What it gains**, measured on the same machine (4 chats taking turns, 5 turns
each, a shared start of about 9,000 tokens): with each chat in its own slot,
the later turns read their prompt in 7 to 11 seconds whatever the chat's
length; with llama.cpp choosing the slots, the chats pushed each other out and
the turns took 14, 20, 25 and 31 seconds, growing with the chat. 77% of the
prompt tokens came from the cache instead of 72%, and the prompts took 356
seconds to read instead of 428.

An admin's **Load** is followed up: should the engine lose it (it restarts
when the models' settings change, and a load sent meanwhile is never done),
the app asks again until the model loads, for ten minutes, making room as for
any load. It ends when the model loads or fails to load, and a later admin's
**Load** of another model ends it too (the last word is the admin's). A
**Load** also ends an earlier **Unload**, so the model new chats use loads
again by itself afterwards.

**Chats that lose their slot** to another wait in RAM (llama.cpp's prompt
cache, with their checkpoints) and come back from there: measured with six long
chats taking turns over four slots, 57-63% of each prompt came from the cache,
about all there was to read, thinking on or off. **Settings → Model → RAM for
chats that lost their slot** sizes it per loaded model (8 GB as installed:
about a dozen long chats of a hybrid model, 0.6 GB each); the model card says
it. RAM it takes is not there for the models' files, so on a host whose model
does not fit RAM and the GPU, keep it small. It stays 8 GB as installed: with
each slot held to its share of the cache, a long session keeps its prefix in its
slot whoever else is answered, and this RAM serves only the conversations
beyond the slots (a 65,536-token window of a hybrid model is about 2.3 GB at
`q8_0`); raise it on a host with RAM to spare. The Resources dashboard's
**Memory used by programs** leaves out the page cache that holds the models'
files: **Memory** below it shows both.

#### A model bigger than RAM and the GPU

A model larger than the GPU's memory and RAM together still loads: llama.cpp
maps its file, and the parts that do not fit are read from disk again each
time they are needed. Answers then start slowly and unevenly, and RAM looks
idle in `free` while it is full of the model's file (buff/cache). Measured
here: Qwen3.8-Flash-Next at IQ4_XS is 94 GB, against 61 GB of RAM and 21 GB
of GPU memory in use; its prompts read at 96 tokens a second for a short one
and 290 for a long one, and a first round after an idle spell took 13.7 s for
832 tokens. Two things help:

- A quantization that fits the GPU and RAM together, with room left for the
  other services: the model form and Admin → Models say when one does not
  (about 70 GB for this model at IQ3_XXS, still close to this host's 82 GB).
- `ENGINE_RAM_PROTECT` in `.env` ([configuration.md](configuration.md#env)):
  RAM the host keeps for the engine under memory pressure (cgroup
  `memory.low`), so its pages stay in RAM instead of other files' taking
  their place. It is never a limit, and nothing is killed for it.

#### Picture, video and speech models

Picture, video and speech models are models like the chat models: each has a
card under Admin → Models, and is added, edited, loaded, unloaded, kept loaded
and removed there.

- **Add a model** takes a picture or video model's file, or a model the speech
  server has, as that kind of model (the same dialog as a chat model's; see
  [Models](#models)). A picture or video model is its files from the
  library, each by what it is: the diffusion model (or the whole model), its
  text encoders (an LLM, T5, CLIP) and its decoder (VAE). Its settings are
  what it makes unless a request says otherwise: steps, CFG scale, guidance,
  flow shift, sampler, scheduler, size, negative prompt, and for video the
  frames a second and the longest clip. Its GPU: which one, a budget its
  weights stay within (the rest waits in RAM), and where it decodes: on the
  GPU, on the CPU (slower, less GPU memory), or on the GPU when it has room as
  the model loads (Wan's 13 GB, else the CPU). **More options** takes
  stable-diffusion.cpp's own, one a line (`--seed 42`); those that pick its
  files, where it listens or what it reaches are the app's and refused. A
  speech model is one the speech server has, with its language, and for text
  to speech the voices it offers, its default voice and speed.
- **Its price** (per picture, second of video, minute of sound, or 1,000
  characters read aloud; empty: Settings → Prices) and **how long it stays
  loaded unused** (10 minutes when empty) are set on it too. Each person's and
  group's credit for pictures, video and speech is under People and Groups.
- **One model at a time on each server.** The picture server and the video
  server run one model each: the one kept loaded, else the one asked for last.
  Keeping one loaded stops keeping the others of its server; asking for
  another switches the server to it (a few seconds to load).
- **Loaded when asked for**, by the chat's tools, Talk, read aloud, a voice
  message, or an API key (the gateway's picture and speech paths go by the
  app, which loads the model first), and unloaded after its idle minutes. The
  speech server unloads nothing by itself: the app does, its voice detector
  too (`silero_vad_*`, which it loads by itself for dictation and Talk: ten
  minutes after the last speech to text).
- **One GPU for chat, pictures and video.** Pictures and videos take turns (the
  chat's, Arena MCP's and API keys' alike), and a server never switches to
  another model under a running job. Before a job whose model is short of GPU
  memory (by its server's status file: a video model that would decode on the
  CPU, a picture model whose budget is not free), the GPU is lent to it: the
  idle chat models that are not kept loaded unload (as with **Unload**), it
  waits for their memory (a minute at most), and a server already running the
  model on the CPU starts again, deciding anew where it decodes (measured
  before: a 1-second clip decoded on the CPU took 8.5 minutes, 427 s of them
  decoding). While the GPU is lent no chat model loads: an answer that needs
  one waits and says why ("The GPU is making a video with Wan2.2-TI2V-5B just
  now: … loads again as soon as it is done"; an API key's request waits too, as
  long as the gateway waits for the app's check, 10 minutes), the engine loads
  none by itself, and **Load** says to try once it is done. When the job ends the room goes
  back: the picture or video model unloads (unless kept loaded) and the chat
  models unloaded for it that are kept loaded or that new chats use load again.
  Admin → Models says what happened lately ("Made room for Wan2.2-TI2V-5B (a
  video): unloaded Qwen3.8-Flash-Next.", "Gave the room back …") and, while it
  lasts, that the GPU is lent. A model kept loaded is never unloaded for a job.
  With several replicas each says what its own jobs did; a lending lasts an
  hour at most should its replica stop.
- **States and failures** as a chat model's: Loading… and Unloading… with why
  and since when (a speech model an admin loaded keeps "loaded by an admin"); a model whose server exits says why from its log (too little
  GPU memory, settings it refused, a file it could not read, a crash, no
  answer in ten minutes) and is tried again after 1, 2, 4, 8, 16, then at most
  30 minutes, its server off meanwhile; settings it refused wait until it is
  changed or loaded by hand. **Load** tries at once.
- **Remove** takes the model away (from the gateway and the chat's tools too);
  its files stay in the library unless **Also delete its files from disk** is
  ticked ([On disk](#on-disk)).
- **Find on Hugging Face** has tabs: Chat (GGUF), Pictures and Video (the
  text-to-image and text-to-video repositories, any format stable-diffusion.cpp
  reads; pick the files a model needs) and Speech (the speech server's
  registry: Download asks the speech server to fetch a model, listed under
  Downloads like the others).
- **Settings → Model** names the picture, video and speech to text models the
  chat uses (empty: the first). The picture and video tools pick among those a
  person may use (each model's **Who may use it**), and the chat's model may
  name another picture model the person asks for.


#### On disk

**On disk**, under the models, lists what the model library holds: GGUF
files (a split model as one item, with its parts), Hugging Face snapshots for
vLLM, and picture and video models' other files (safetensors and the like),
largest first, with the library's total and its disk's free room. Each says
which models use it. Deleting one from disk (the bin, with `models.delete`)
removes it for good, every part of a split model and a snapshot's whole
folder, and the folders it leaves empty; a finished download whose files are
all gone is forgotten, so Find on Hugging Face offers it again. It is refused
while a model uses the file (remove the model first, with its files if you
like), while a download still writes into it, and for the servers' own files
(the picture and video servers', the embedder's, Laya's and the media
studio's: **Show the servers' own files too** lists them). Each deletion is
audited as `model.files.delete`, with what was deleted and the room freed.
Admin → Storage's **Models nothing uses** clean-up deletes the same files,
several at once.
### Security

Admin → Security (for those who may manage every review, `reviews.admin`,
where Code Studio is on) shows every repository's latest security review at
a glance. Reviews are run in Code Studio → Security
([chat.md](chat.md#security-reviews)).

- **The numbers**: open findings by severity, the known-exploited (on CISA's
  list), those past their time to fix, and what was new and fixed this week.
  The time to fix is 7, 30, 90 or 180 days by severity, counted from when
  each finding was found. A vulnerable package found when the advisories were
  renewed counts from then.
- **Repositories**: each repository and branch's latest review, the
  known-exploited and most severe first, with its bill of materials to
  download.
- **Cannot wait**: open findings known to be exploited, and those past their
  time.
- **This week**: what reviews, and the packages checked again against renewed
  advisories, found in the last seven days, and what was fixed.
- **Risks accepted**: by whom, why and until when; lapsed ones are marked.

A new critical or known-exploited finding, from a review or from a renewed
advisory, is sent at once to everyone with `reviews.admin` (the security
team), as well as to the review's person. The page warns when no advisories
pack is installed, or when the advisories are more than a week old.

### Feedback

**Feedback** (Oversight, with `feedback.manage`, a permission of its own) lists
what people send about Arena with **Send feedback** (their menu, and Code
Arena's Accounts menu): an idea, a problem, praise or other, its words, who
sent it and the page it came from (`Code Arena: …` from Code Arena). New
ones come to the admins' bell. Mark each read or done, or answer it: the
answer goes to the person's bell, and their **Your feedback** shows it.
Sending is audited (`feedback.sent`), and so is handling it
(`feedback.handled`); a person sends at most twenty a day.

### Prompt log

**Prompt log** (Oversight, with `prompts.review`, a permission of its own that
no built-in role but the admins' has; the Auditor's `.read` permissions do not
include it) shows everyone's questions and the answers they got, newest first:

- **Chat**: each question in a chat (Code Arena's sessions kept in Arena and
  rooms among them) with the model's answer, its chat's title, whether the
  chat was deleted, and the answer's tokens. These are read from the chats
  themselves, so they are there as long as the chats are kept.
- **API**: each chat completion asked with an API key, its last question and
  the whole answer as it went out (cut at 20,000 characters each, one from
  the answer cache too), and its tokens. **Settings → Data retention → Keep API requests in the prompt log
  for** (90 days by default, 0 forever) keeps them; **Settings → API keys → Prompt log of API
  requests** off keeps none. Deleting a person deletes theirs.

Search by words (in the question, and in an API answer), by person (user name
or email) and by where (all, chat, API); **Older** goes on 50 at a time. Every
search is in the audit log as `prompts.viewed`, with what was searched for and
how many were shown. **Your account → Your data** tells each person that their
questions can be read this way.

### Quality

Two things feed it, both from the chat ([chat.md](chat.md)):

- **Thumbs** under each answer, with a few words either way, and a reason on
  a down vote (wrong, incomplete, too long, unsafe, ignored instructions,
  other).
  An answer counts once it is written (its last message); rated share and
  thumbs up are of the answers written in the range.
- **Compare** (arena mode): one question to two models, side by side and
  blind, and the person's vote. Each vote moves an Elo rating: every model
  starts at 1000, a vote moves both by at most 32, more when the lower rated
  wins, and a tie or "both bad" counts half a win each. The leaderboard lists
  each model's rating, wins, losses, ties, "both bad" and win rate (wins over
  votes).

Everyone sees the leaderboard of every vote (**Leaderboard** in the sidebar,
"the company's models on its own questions") unless **Settings → Chat → Arena
leaderboard for everyone** is off; this page shows it either way, for the
range chosen.

### Retention, legal hold and exports

**Retention.** Settings → Data retention → **Keep chats for** is the company's
period (empty: forever). A group can set its own (Admin → Groups → a group →
Policies); a person in several groups keeps the shortest of those their groups
set, and the company's when none does. Every hour a job deletes the chats
whose last message is older than that, with their files, and the files nobody
uses that are as old (uploaded, never sent). An assistant's files stay with the
assistant. Each person's deletion is in the audit log (`retention.delete`) with
counts, never content. Your account shows people how long their chats are kept.

**Legal hold.** Admin → People → a person → **Place on legal hold**, with a
reason (the matter or a ticket), audited (`person.legal_hold`). While it lasts
nothing of theirs is deleted: the job passes them by, and a chat they delete is
hidden from them, not erased. They are not told. A person on hold cannot be
deleted. **End the hold** erases the chats they deleted meanwhile
(`person.legal_hold_end`); retention applies again from the next run.

**Exports.** **Export their data** (on the person's page) downloads a zip for
eDiscovery: their profile, groups and preferences, assistants, scheduled tasks
(never a webhook's address), every chat with every branch as JSON and the
branch on screen as Markdown, and every file (the file itself and the text the
model read). The chats they deleted under hold are in it, marked with
`deletedAt`. Each export is audited (`person.export`). People download their own
copy from Your account → **Your data** (`account.export`), without hidden chats.

### Storage

Admin → Storage has four tabs. Nothing of a person on legal hold is ever
deleted from it.

**Where the room goes.** The disks first: the host's, from node-exporter
through Prometheus (one line per device, as the disk alerts count them), and
the folders the app mounts (the model library, the backups, and Docker's own
data behind the app's root), each matched to the host's disk it is on, so a
disk says what of the stack it holds. Each disk shows how full it is, how fast
it filled over the last week, and, when it is filling, in about how many days
it is full. Then what takes room:

| What | Measured from |
|---|---|
| Each database on the stack's Postgres (`llmapp`, `litellm`, `argus`, `langfuse` where they are) and the largest tables of the app's and the gateway's | Postgres itself |
| The chat's files (uploads, and the pictures, videos, speech and other files the tools made), by where they came from, whether a chat still has them, and by person | the app's database: the files are kept there, inside `llmapp` |
| The model library (`MODELS_DIR`): each model file, which model of Admin → Models uses it (loaded or kept loaded), or which server reads it, and downloads not finished | the folder |
| Argus: its index, GitLab mirrors, checked-out trees and installed packs, and its disk | Argus measures its own data folder (`GET /admin/storage`), at most every ten minutes |
| Backups (`BACKUP_DIR`, mounted in the app at `/backups`, read only): each backup, how it ended, and which is the latest | the folder |
| Metrics: what Prometheus keeps and for how long (30 days or 20 GB) | Prometheus's own metrics and flags |
| Logs: how long Loki keeps them (as shipped, forever), and what each container wrote over the last week | Loki's configuration and its volume API |

Every six hours the app writes down what each thing takes (one row a day for
each, kept 400 days): the 30-day growth beside each. **How long things stay**
lists the rules in force (retention, legal hold, rooms, the answer cache,
backups). **What the app cannot see** says what is left: the app has no Docker
socket (that would be root on the host for anyone who reaches it), so Docker's
images and build cache, the containers' own logs (at most 5 files of 20 MB each)
and the volumes it does not mount are not measured as folders;
`docker system df -v` on the host lists them. Folders are measured at most
every two minutes; **Measure again** asks now.

**Keeping logs for less time.** As shipped, Loki keeps logs forever: its
compactor deletes only logs older than `limits_config.retention_period`, which
is not set. To keep 14 days, say, add `retention_period: 336h` under
`limits_config:` in `deploy/config/loki.yml` (with Helm, the chart's
`files/loki.yml`) and recreate Loki (`docker compose up -d --force-recreate
loki`, or `helm upgrade`). Within a few hours its compactor deletes every log
older than that, for good, and keeps doing so. The page shows the period in
force.

**A disk past the share.** Every five minutes the app compares each disk with
**Settings → Storage → Warn when a disk is fuller than** (80% by default). A
disk past it is an alert like the stack's own (`DiskAboveThreshold`, from the
app): given to Alertmanager, so it fires on the Alerts page (its rule is under
**The app's own**; the history there is Prometheus's, so it is not in it) and
reaches the bell, email and the alerts webhook once, and it ends when the disk
is below again. The rule's state is what Alertmanager holds, so every replica
shows the same. While Prometheus does not answer, a host's disk past the share
keeps its alert as it was, and the folders the app mounts on it raise none of
their own.
With Alertmanager away, the admins are told directly, once each time a disk
passes the share, and not again for it when Alertmanager is back. The Overview shows the fullest disk and warns about any past
the share. Prometheus's own rules still warn at 85% and 95%.

**Files.** The chat's files and what the tools made: whose, which chat, from
where, the kind, the size (the file, its text, a video's sound and the pages
drawn of a document) and when. Filter by person, kind, where from, whether a
chat still has it (in a chat, an assistant's, on a deck, in a chat deleted
under legal hold, in no chat), size and age; search by the file's name, the person or the
chat's title. What a tool made over Arena MCP is in no chat, and still the
tool's (a picture, a video, speech). The list holds the first thousand in the order chosen; the totals
count every match. **Download** gives the file itself, never shown in the page,
audited (`storage.download`). Ticked files are deleted after a confirmation
that says how many, what they take, how many are in chats (the chats keep their
words, without the file) and how many legal hold keeps. Deletions are audited
per person, with counts and sizes (`storage.delete`).

**Clean-ups.** Each shows what it would remove now and the room that frees
(**Preview**), and runs after a confirmation; each run is audited
(`storage.cleanup`), and none touches anything of a person on legal hold. Old
backups are the exception: the app sees the backups read only (they hold every
secret of the stack, and the compose files a restore puts back), so their
preview gives the command that removes them on the host instead.

| Clean-up | What it removes |
|---|---|
| Files in no chat | files no chat, assistant, deck or waiting message has, older than the age chosen (7 days by default: a younger one may be being written) |
| Old pictures, videos and speech | what those tools made, past the age chosen (**Settings → Storage → Old generated media**, 90 days by default); the chats keep their words |
| Files of deleted chats | chats deleted while their owner was on legal hold, once the hold is over, with their files (the hourly retention job takes them too); it shows what the holds still keep |
| Leftovers on disk | what no database row owns: a download's `.part` file no download will finish, and Python sandbox jobs left behind (older than an hour) |
| Old backups | shown only: backups beyond the newest ones kept (`BACKUP_KEEP` in `.env`, 14 by default, or the number typed), never the latest nor the newest that ended well, and `scripts/backup.sh --prune --keep N` to run in `deploy/` on the host. With anyone on legal hold it warns that backups from before the hold hold their data (what they deleted since, too) and marks those taken before it: ask whoever placed the hold before removing them. A backup's start is the one its `MANIFEST` gives in UTC; a backup's name is the host's local time, so for a backup from before v5.3, which has only its name, one named up to 14 hours after the hold began counts as before it |
| Models nothing uses | model files (GGUF, vLLM snapshots, picture and video models' files) no model of Admin → Models uses, no server reads and no download writes into, each chosen by hand (as [On disk](#on-disk) does, one at a time): one deleted is gone from the library, and using it again means downloading it again |

A clean-up that cannot run says why (the backups not mounted, or another
user's).

**People.** Each person with files, what their files take, and their room.
**Settings → Storage → Room for each person's files** is everyone's (empty: no
limit); **Room** gives someone their own (0: no limit) or the company's again,
audited (`storage.quota`). **Give someone a room** finds anyone by name, to give
them their own before they have files. Past it, uploads are refused (HTTP 413, saying what
their files take of their room) and so are the picture, video and speech tools
(the model is told why, and tells them); what Python makes and long tool
results still go, as the answer needs them. Your account shows each person what
their files take of their room.

### Credits

Credits are one per kind, so each can be held on its own:

| kind | what counts to it |
|---|---|
| **Chat** | the chat's answers (titles, the safeguards' check and summaries too) |
| **API keys** | text requests with a person's API key: coding agents, IDEs, scripts |
| **Pictures** | pictures made, in the chat or with a key |
| **Video** | videos made |
| **Speech** | read aloud, Talk, voice messages and sound files turned into text, and speech with a key |

Each is a calendar month's spend (UTC) as the gateway's request log puts it to
the person, by the same rule as the usage dashboards. A person has a credit of
each kind (Admin → People → a person → **Credits this month**; empty: no limit
of their own), and so may each of their groups (Admin → Groups → a group →
Policies), **shared** by the members or **each member's**. Before each chat
answer the app checks the chat credit, and before each picture, video or speech
it makes, that kind's; for an API key's request the gateway asks the app first
(its guardrail, below), which checks the kind of the request's model (a picture
model's request is a picture). Past any credit of that kind, the person's or a
group's, it is refused with the reason ("You have used all your API credit for
this month ($20.00)"). The other kinds go on. A person hears once a month at 80%
of a credit of their own and when it is used up, and the admins when it is used
up (Notifications). A credit of $0 allows none of that kind; it is not listed as
used up, and a request of that kind hears who gives it ("You have no picture
credit: an admin sets it under People", or under Groups for a group's). Arena
MCP leaves the picture, video and speech tools out of a person's tool list
while they have no credit of that kind, and its notes say why. An API key's
picture is refused before its model is loaded for it. A person new since v5.5 has no limit of any kind until an admin sets
one. The person's page
shows each kind's spend against their credit and the tightest group's credit
left; their home page shows the same. People → **Set credit** sets one kind for
everyone selected, leaving their other credits as they were.

The gateway holds no budget: the credits are the app's. On the first start of
v5.5, each person's credit at the gateway (one over the chat and API keys)
became their credit of every kind (unless they had credits already), a group's
one credit its credit of every kind, and the gateway's budgets and the teams
older versions made for groups (`group-...`) were cleared. The gateway is kept
holding none (every ten minutes): a `config/litellm.yaml` changed here that
still has `max_internal_user_budget` would otherwise give new people a budget of
its own; take that line out on upgrade.

**API access** is on for everyone; an admin takes it on the person's page (the
**API access** switch, after a confirmation: the person's own deny of
`feature.api_keys`, [Access](#access-roles-and-rules)). Off, their keys are blocked at the
gateway and refused by the guardrail, none is shown to them or made (by them or
an admin), and Arena's MCP endpoint, Argus and speech with a key refuse them;
the chat stays theirs. Given back, their keys work again (one is made if they had
none). A block the gateway could not take at once is taken at the next check,
within ten minutes, and so is a disabled person's.

**Cost centre** is a label per group. **Chargeback** (below the groups list):
spend per group and per cost centre, month by month, and the people in no group;
**CSV** downloads it. A person in several groups counts in each group, and once
in a cost centre.

**The gateway's guardrail.** `config/litellm.yaml` has LiteLLM's generic
guardrail API call the app (`http://app:8080/internal/guardrail`, with the
gateway's master key) before each request. The app answers in milliseconds:
API access and the credit of the request's kind, and the safeguards ([chat.md](chat.md#safeguards)) for API keys,
then room in the engine for a model that is not loaded (see **Loaded on
request** above: at the limit, the request may wait up to a minute for an idle
model to make room, or is refused with the reason); the chat's own requests
pass at once (the chat checked them, and made room). The path is not
routed by Traefik: only the gateway, inside the network, reaches it. With
`unreachable_fallback: fail_closed`, API requests are refused while the app is
down (a restart); `fail_open` lets them through unchecked instead.

### The answer cache for API keys

Pipelines and FAQ bots often ask the same thing again. **Settings → API keys →
Answer cache** answers a repeated identical request from the app's database
instead of the model:

| value | what it does |
|---|---|
| `off` (the default) | every request goes to the model |
| `opt-in` | each person turns it on for their key (Your account → API key → **Answer repeated requests from the cache**) |
| `all` | every key |

- **What counts as the same**: the same key, the same model and the same
  request (messages, tools, temperature and every other field; whether it
  streams and its `user` do not count), to `/v1/chat/completions` at
  `gateway.DOMAIN`. Requests over 1 MB, and the Anthropic protocol, are never
  cached. The chat never uses it.
- **What a hit is**: the kept answer, whole or streamed as asked, with usage
  at zero and the header `x-arena-cache: hit`. The gateway never sees it, so it
  costs nothing and is not in the usage dashboards. A request asked of the
  model while the cache applies says `x-arena-cache: miss`, and its answer is
  kept if it is complete (errors and cut-off streams are not).
- **How long**: **Keep cached answers for** (24 hours by default, up to 30
  days). `Cache-Control: no-cache` on a request asks the model again (and keeps
  the new answer); `no-store` leaves the cache out.
- **A key the gateway would refuse** (unknown, blocked, expired, or not allowed
  the model) gets nothing from the cache: its request goes to the gateway,
  which refuses it. A person who turns the cache off for their key loses what
  was kept for it at once.
- **How it is reached**: Traefik sends `gateway.DOMAIN`'s completions to the
  app whenever the app is up (its health check, `/healthz`, every 10 seconds),
  cache on or off: each key's line, the fair line and the prompt log are the
  app's too. While the app is down they go straight to LiteLLM. The app passes
  everything it does not answer on to LiteLLM with the caller's own key, and
  streams the answer back as it comes.

### Rate limits for API keys

How much each API key may use a minute at the gateway: **requests a minute**,
and **tokens a minute** (what the model reads and writes, less the prompt it
reads from its cache, as the gateway counts them). Each of the two is, for a
person's keys:

1. **their own** (Admin → People → the person → **Rate limits**), else
2. **the highest of their groups** that set one (Admin → Groups → a group →
   Policies), else
3. **the company's** (Settings → API keys → **Requests a minute, per key** and
   **Tokens a minute, per key**), else no limit.

0 is no limit wherever it is set; an empty box is "not set here". A group's 0
is the highest of all, so a group can free its members (an automation team)
from the company's limit. A person in several groups gets the most generous:
a group that needs more is not held back by a broader one with less, and a
person's own holds back anyone who needs it. (Credit and safeguards are the
other way: the strictest group applies.) Out of the box nothing is set, so
nothing is limited, and an upgrade from v5.2.0 sets nothing either, with one
exception: a limit an admin already put on a key in the gateway's own pages
(LiteLLM's `/ui`, the only way to limit a key before) is kept. The first key
check after the upgrade makes it its person's own, the strictest of their
keys' for each of the two, where it is stricter than what they would get and
they have none of their own; it is audited (`person.set_limits`, "kept from
their API key") and logged, and shows on their page, where you can change it.
A **New key** made before that check keeps it too. From then on the app's
limits are the keys'.

Each of a person's keys carries the limits: the app gives each person one key,
and **New key** makes the next with the same limits, so they are the person's.
People also make keys of their own on **Account → API keys** (up to 20): each
named, each with limits of its own (requests and tokens a minute, requests at
once, a budget in 30 days) that hold under the person's, as the gateway holds
the key to the tighter of the two, and the key sync keeps it so when the
person's limits change. Each is revoked on its own (`person.key_revoked`);
making one is audited (`person.key_made`) and counts with **New key** in the
five an hour. **New key** replaces the main key only: keys of their own and a
Code Studio workspace's keep working.
The gateway counts each key on its own, so a new key starts a fresh minute:
people may make at most five new keys an hour themselves (the sixth is refused
with HTTP 429 and audited as a failed `person.rotate_key`), so a new key is no
way round a limit. **New key** on the person's page is not counted. What was
refused is the person's, whichever of their keys met the limit.
**API requests at once, per key** (Settings → Chat) stays as it was, beside
them.

- **Where it is counted.** The app puts the limits on each key at the gateway
  (LiteLLM's `rpm_limit` and `tpm_limit`), and LiteLLM counts every request of
  the key in windows of a minute: one count, whichever app replica a change
  came from. A request past a limit gets **HTTP 429** with `Retry-After: 60`,
  and says which limit: `Rate limit exceeded for api_key: <the key's hash>.
  Limit type: requests. Current limit: 60, Remaining: 0. Limit resets at: …`
  (`tokens` for tokens a minute, `max_parallel_requests` for requests at once).
  Most clients wait that long and send again on their own (the OpenAI and
  Anthropic libraries do); a script of your own should too.
- **Tokens are checked before the model runs.** A request goes ahead only if
  its prompt (a quarter of its characters, as tokens) and its `max_tokens`
  (when it sets none, as much as its prompt and at least 1,024) fit what is
  left of the minute; the real count replaces the guess when it ends. Keep
  the limit well above the largest request a tool sends: a coding agent's
  prompt is often 30,000 tokens or more. Below 4,096 tokens a minute, the
  gateway also cuts answers that set no `max_tokens` to a quarter of the limit.
- **When a change applies**: a person's own at once; a group's, a change of
  members and the company's within seconds (the same check that keeps each
  key's models in step, on the replica that leads), and every ten minutes
  (directory groups change on their own). A limit set in the gateway's own
  pages is replaced by the app's (one set there before the upgrade is kept, as
  above).
- **The chat is not limited by these.** Its requests go with the chat's own
  key, which carries none: the chat has its fair line (Settings → Chat).
  Answers from the [answer cache](#the-answer-cache-for-api-keys) do not count
  either: they never reach the gateway.
- **Arena MCP.** An agent signs in to Arena MCP with the person's key, but the
  tools that reach a model (pictures, speech, video, and deep research) make
  their requests with the chat's own key, so the gateway cannot count them.
  The app counts each call instead, as one request against the person's
  requests a minute: what their keys sent in the last minute, and their Arena
  MCP calls of these tools that started in it (those that ended from the audit
  log, which has each call at its start; those running now on the replica
  that took the call). Each counts in the minute it started, as the gateway
  counts a request, however long it runs: a five-minute video holds back one
  request, for one minute. Past the limit, the tool call is an error saying
  which limit, audited as `mcp.rate_limited`; their key's card counts these
  calls and refusals too, the Refused by rate limits panel does not (it reads
  the gateway's log). Tokens a minute and requests at once do not apply to
  these tools: pictures, speech and video have no tokens, and the chat's own
  limits for pictures and videos hold. A deep research run started there
  (`deep_research`) is a chat of the person's own: the many requests it makes
  go with the chat's key and are held by the deep research a day and the
  messages per minute (Settings → Safeguards) and the fair line, not by the
  key's limits; only its start counts here. Arena MCP's other built-in tools
  (Argus, Python, the web, the calculator, the time) reach no model.
- **What people see**: their key's card (Your account, and Connect your tools)
  shows each limit and where it comes from, what the key used in the last
  minute, and what was refused in the last day. The minute is read from the
  gateway's request log, which LiteLLM writes every few seconds (a streamed
  answer when it ends): close, not exact.
- **What admins see**: the same on the person's page, and **Refused by rate
  limits** on the Usage by person dashboard: who, with which key, which limit,
  how often and when last. A refusal is in the gateway's request log as a
  failed request with its reason and no prompt or answer; refusals for credit
  are not counted there. A refused request never reached a model, so it is not
  counted as a request anywhere else (a person's own usage, the dashboards'
  requests, the LLM overview's failures and times): a script that retries
  every second shows as the requests that were answered, plus its refusals
  apart. Each change of a limit is audited
  (`person.set_limits`, `group.policies` and `settings.change`).
- **Several gateways.** LiteLLM counts in its own memory. Compose runs one,
  and so does the Helm chart unless `litellm.replicas` is raised: with several,
  each counts on its own, and a key can send up to that many times its limit.

### What the admin area deliberately does not do

**It does not recreate containers.** Admin → Models loads and unloads models
through the engines' own APIs (llama.cpp's router, the speech server's) and the
control files the picture and video servers read, which need no Docker socket.
`.env` holds only what the stack needs to start; a change there is
`docker compose up -d` on the host. A Docker socket in a web app is root on
the host for anyone who reaches it.

**It shows no secrets**, not even masked: a masked value still leaks its length
and first characters into every screenshot. A secret can be replaced, never
read back.

**Prices** are below ([Prices](#prices)); each person's credits are under People,
and a group's under Groups ([Credits](#credits)).

### Prices

Every model has a price, so every request has a cost:

- **A chat model's own** (Admin → Models → Edit, or a model of another GPU
  server in its server's form): dollars per million tokens of **input** (the
  prompt the model reads), **cached input** (prompt tokens the engine reads
  from its cache: the start of a conversation it has seen, sent again with
  every answer) and **output** (what it writes, thinking included). Each one
  left empty is the default. Cached input is never priced above the model's
  input: a price above it (its own, else the default) is refused on save, and
  one a lower default input later puts above it costs what input does. The
  models list shows each model's prices, and which are defaults.
- **The defaults**, for every model without its own: Settings → **Prices**
  ([settings.md](settings.md#prices)), $0.20, $0.02 and $0.80 per million
  tokens out of the box.
- **Pictures, video and speech**: each model's own (Admin → Models → Edit),
  else by what they are (Settings → Prices): a picture ($0.01), a second of
  video ($0.05), a minute of sound turned into text ($0.006), 1,000
  characters read aloud ($0.015).

The app gives the gateway every model's prices when it registers it
(`input_cost_per_token`, `cache_read_input_token_cost`,
`output_cost_per_token`; per picture, per second of sound, per character for
the picture and speech models), again within a minute of any change, and at
once when the defaults are saved. LiteLLM prices a request's cached tokens at
nothing when a model has no cache read price, so every model has one. The chat
asks the speech server for a transcription's length (`verbose_json`), which is
what LiteLLM prices it by. The video server is not behind the gateway: the app
books each clip in the gateway's request log itself, as the chat's request,
with its length.

**Recalculate past costs** (Settings → Prices): requests booked before a model
had a price (or before cached input had one) cost nothing, or too little. Pick
the days, count, then **Recalculate**: requests in the gateway's request log
(what usage, credit, chargeback and the dashboards count) and the parts of chat
answers (what each answer shows) are worked out again at today's prices.
**Only costs booked as free** (on by default) leaves the others as they are; off,
every cost in the range is worked out again. A picture request booked as free
counts as one picture; one with a cost keeps it either way, since the log does
not say how many pictures it made (an API key can ask for several at once).
Speech and transcriptions in the log cannot be priced again
(their length is not kept), nor pictures, video and speech whose files were
deleted since. Each recalculation is audited (`usage.recalculate`, with the
counts and totals); the gateway's own running totals (a key's spend in LiteLLM's
own pages) are left as they are. It never runs by itself, not on upgrade either.

## Usage & cost

**Everyone** (admins) is the "Usage by person" dashboard: people active,
requests, tokens, spend, how much is attributed to a person, requests made with
the master key; per-person and per-key tables; tokens per day by person and by
surface (API or chat); budget headroom; unattributed usage to fix; requests
refused by [rate limits](#rate-limits-for-api-keys); recent requests; and input split into **cache miss** and **cache hit** with the cache
hit rate, output, cost, cost per 1M tokens, per person and over time.

**Everyone's prompts** (admins): each prompt of the time range (or between two
days) with who asked, when, the model, tokens in, cached and out, and its cost;
by person, group, model, and chat or API keys. It shows no chat's title, only
who, when, which model, tokens and cost. Totals count every prompt the filters
match; the table holds the newest 1,000 and sorts by any column. An admin's own
comparisons stay blind to them here until they vote, as below.

**Mine** (everyone) is the same numbers for the signed-in person only: requests,
input cache miss, input cache hit (and what share of input it is), output, cost,
over time and by model; and **Your prompts**, the same list of their own, each
answer linked to its chat.

A prompt is a chat answer, with its rounds, tool calls (pictures, video,
speech) and sub-agents added up at the prices when each ran, or one request of
an API key (from the gateway's request log). The chat's own small steps around
the answers (titles, summaries, the safeguards' check, transcribing a file) are
in the totals above, not in the list. An answer from before costs were kept is
marked with `*`: its cost leaves those parts out until an admin recalculates.
An answer of a comparison (Compare) not voted on yet stays blind, as in the
chat: its model is "Model A" or "Model B", its cost waits for the vote (and is
out of the cost total until then), and the model filter does not find it.

### Usage by group

**Usage by group** (Observe, with `usage.read`) has each group's requests,
tokens and spend over a day, a week, 30 or 90 days: chat and API keys
together, by the dashboards' rule of whose a request is, refusals for a key's
rate limit left out. Each row has the group's members, how many of them used
it, the share of input tokens read from the cache, and its spend, the most
spent first, with **(in no group)** last. Open a group for its members who
used it, each with their own requests, tokens and spend (a name opens the
person). A person in two groups counts in both, so the groups can add up to
more than everyone's together (the line above the table). Each person's own
use is on the **LLM and users** dashboard, and each month's spend per group
and cost centre on Groups' chargeback report.

### Line

**Line** (Observe, with `usage.read`) shows the answers' line every two
seconds: each chat answer and API request running (whose, its model, chat
or API, for how long, the longest first) and each waiting in the order it
will be served (a higher priority first, then whoever has fewest running,
then whoever was served longest ago), with its place. Above them are the
line's settings (Settings → Chat: Answers at once for everyone and per
person, Longest line, Longest wait in line) and each model's places. Each
app replica keeps its own line: the page shows the one that answered.

### Dashboards

The dashboards come in three areas, each seen with its own permission, so a
role can give one without the others (Admin → Access → Roles):

| area | its dashboard | in detail | permission |
|---|---|---|---|
| the model and its users | **LLM and users**: requests, tokens, latency and time to first token, the line (running, waiting, people, the average wait, refusals), the engine's speed, and who uses what (per person, tokens and cost, refused by rate limits) | LLM Overview, Stack Performance, Argus (Usage by person is on Usage) | `usage.read` |
| the system | **System**: targets up and down, alerts firing, the line, each service's requests, server errors and response time (p95, through Traefik), the host | Stack Health & Alerts, Host & Containers, Logs, Indexing | `monitoring.read` |
| the hardware | **Hardware**: GPU utilisation, memory, temperature, fan, power against its limit, clocks and throttling; CPU by mode and per core, load, clock and temperature; memory, swap, disks, disk I/O, network, NVMe temperature | GPU Hardware, Resources | `hardware.read` |

The Supervisor and Operator roles hold all three; an Owner, every
permission. Each file names its area and permission (`"area"`,
`"permission"`, `"main"` for the area's own dashboard; a file of your own
without them is the system's, with `monitoring.read`). The line's numbers come
from the app itself (Prometheus job `app`: `arena_line_running`,
`arena_line_waiting` by model and chat or API, `arena_line_people`,
`arena_line_refused_total` by reason, `arena_line_waited_seconds`), read on
the stack's network only: Traefik routes no `/metrics` to the app, and the app
refuses one that came through it. NVIDIA's DCGM detail (SM activity, tensor
cores, XID errors, ECC, NVLink) needs the **dcgm** profile
(`COMPOSE_PROFILES=dcgm`, where nvcr.io is reachable): the Hardware dashboard
says so, and GPU Hardware's DCGM panels are empty without it.

The dashboards are files in Grafana's JSON format, in `src/Llm.Api/Dashboards/json/`
(built into the app's image, so an upgrade brings the new ones), and the app draws
them itself. Each panel's query runs on the app's server:

- **PostgreSQL** (the usage panels read the gateway's spend tables): Grafana's
  macros are reproduced (`$__timeFilter`, `$__timeGroupAlias`, `$__interval`,
  `$__range` and friends), including how Grafana rounds the interval, and rows
  become series by Grafana's rule for the time-series format. Each query runs as
  a role that can only `SELECT`, in a read-only transaction, one statement, with
  a 15-second timeout and a 5,000-row cap.
- **Prometheus and Loki**: the step Grafana would use (the panel's minimum
  interval, the scrape interval, Loki's 11,000-point limit), the range aligned to
  it, `$__rate_interval`, `$__auto` and the rest, the dashboard's variables
  (Container, Search) and series named by the legend format.
- The browser asks for "panel N of dashboard X over this range, with these
  variables" and gets data back. It never sends or receives a query, and a
  variable's value is only ever one of its options.

The machine dashboards show whatever machine they run on, as it reports itself:
a drive is named by its device, then the model it reports (`node_nvme_info`), as
in `nvme1 · Samsung SSD 990 PRO 2TB`, or by its device alone when it reports no
model; the device comes first so two drives of one model stay apart in a narrow
cell. On a machine with more than one GPU, each is named by the index
nvidia-smi gives it and the model it reports (`nvidia_smi_gpu_info`), as in
`GPU 1 · GeForce RTX 3090`; with one GPU the names stay short (`temp`, `draw`).
The CPU temperature is the hottest sensor and the average of all of them
however many cores there are, and **CPU temperature per sensor** (Resources,
under the temperatures) has a line for each, across the whole width so it
never stretches a panel beside it. The load average has the machine's logical
CPU count beside it. No file names one machine's drives, CPU, GPU or core
count: a test checks that, and checks every Prometheus query with the stack's
own `promtool`.

Every dashboard opens on the last hour: each file's `time` is `now-1h` to
`now`, and a file without one (one of your own) gets the same. **Time range**
offers five minutes to 90 days; the range chosen goes in the address as
Grafana's `from` and `to` (`?from=now-6h&to=now`), so a link opens the same
view, and a link with a range of its own (`now-30m`, or two times) opens on
it; two times on one day show the day once, and on a phone the menu shows as
much of them as fits. A range the app cannot read is the last hour. Usage → **Everyone** is a
dashboard too; **Mine** and **Everyone's prompts** keep their own ranges.

Live dashboards (those with a refresh in their file) refresh themselves, and
can pause. Edit a dashboard file and the next request uses it: the app reads
the files on every request.

`scripts/audit-dashboards.py` runs every panel's queries in the app (over the
last hour, as the pages open, or the window given) and reports errors, empty
panels, null values and percentages out of range:

```bash
python3 scripts/audit-dashboards.py 6h
```

### Charts

One value axis per chart. Colours come in a fixed order, checked for
colour-vision deficiency against the light and dark backgrounds; a series keeps
its colour by name. At most eight series are drawn: the seven largest, and the
rest summed as "Other", or averaged where a sum means nothing (temperatures,
percentages, clock speeds and durations, unless the chart is stacked). Every
chart has a legend when it has more than one series, a tooltip, and **Show as
table**, which lists every series, the folded ones too; in a narrow panel that
button is only its icon, so the title beside it is not cut short. The legend is
one line: two names always fit on it, a longer name cut short (whole on hover
and in the tooltip), and more page. The time axis always spans the chosen range.
Temperatures, power, clock speeds and disk or network rates carry their unit
(°C, W, GHz, MB/s).

A stat shows a value per series. Past two it leaves out their sparklines, and on
a wide screen it scrolls rather than grow taller than a chart: a row of panels
stays one height whatever the machine reports. A name takes two lines before it
is cut short, and shows whole on hover. A gauge per series (a GPU each) is drawn
small when there are several, and past two they scroll the same way. Values sit
side by side only where the panel has room for them, else one under the other;
a value too wide for its panel (a small panel on a window of 1024 to 1280
pixels) is drawn smaller rather than past the panel's edge, and a gauge's ring
and number shrink together. A panel's values stay one size.
