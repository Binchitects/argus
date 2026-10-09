# Admin, usage and cost

Everything an operator does happens in the app at `https://DOMAIN`. The
**Admin** area needs the admin role; **Usage & cost** and **Your account** are
for everyone, as are **Connect your tools** (each person's API key and the
setups for their tools) and the **Leaderboard** (the models as people voted
for them in the chat's Compare, unless an admin keeps it to the admins).

People are a table with filters, bulk actions (credit, disable, enable, sign
out) and a page each. The audit log has filters, paging and CSV export. The
Settings page edits everything ([settings.md](settings.md)), and the Model page
has **Switch to this model**.

Sign-in, people, the company directory, company sign-in (OIDC or SAML, and SCIM) and 2FA
are in [authentication.md](authentication.md), and the chat in [chat.md](chat.md). This
page covers the rest.

## Admin

| Page | What it is for |
|---|---|
| **Overview** | Services up, people and admins, total spend, who is at or past their credit, the code index's health, and the certificate the site serves. When the index is stale it says how many repositories, which ones, and *why* when the last run's exit code tells (GitLab unreachable, a token that cannot list every repository, ctags missing). The certificate (from Traefik's metrics in Prometheus) shows the days until it expires and who issued it (your own, or Let's Encrypt); within 30 days it says what to do, and it says so when Traefik serves its own default (browsers warn). |
| **People** | Add, search, and per person: credit, a new API key, password and 2FA resets, admin role, disable, sign out everywhere, legal hold, an export of their data, delete. **Export CSV** downloads everyone with spend and credit left. |
| **Groups** | App groups (the people you add), directory groups (whoever the company directory or the identity provider's groups claim puts in them, by name or DN) and SCIM groups (made and filled by the identity provider; their name and members are changed there). Tools and models are given to groups. Per group: how long members' chats are kept, a credit a month (shared or each member's), a cost centre, and which safeguards apply. A group's **priority in the answers' line** (-10 to 10, 0 for everyone) decides who goes first when the model is busy: higher first, and within one priority the line stays fair (fewest answers running, then served longest ago). Someone in several groups takes the highest. A change is audited. Below the list, the monthly **Chargeback** report. See [Retention, legal hold and exports](#retention-legal-hold-and-exports) and [Credit for groups](#credit-for-groups). |
| **Tools** | What the chat's model may call: Argus, Python (the sandbox), the web (off until you turn it on and allow sites), image generation, the calculator, date and time, reading long files in parts, the canvas (documents and code beside the chat, changed by the model part by part), questions for the person (the model asks with choices instead of guessing), sub-agents (the model splits a task into parts done side by side), memory (the model remembers what each person asks it to; off for everyone under **Settings → Chat → Memory**, and nobody but the person sees their memories), **Decide (Laya)** (typed questions about a text answered with probabilities by the Laya decision model on the CPU; offered only while the `laya` module runs and has a checkpoint loaded, and otherwise says why: [chat.md](chat.md#decide-laya)), **Deep research** (a plan, sub-agents that search the web, and a report with its sources: it runs only through this tool: only those who may use it see it in the chat's **Tools**; on in a chat, the model starts one when asked, and **Ask before each run** has the chat ask the person first. As installed, and after an upgrade from v5.2.0 or earlier, it is on for everyone, on in new chats and asks first, so everyone keeps it until you change that; the deep research a day per person is under **Settings → Safeguards**: [chat.md](chat.md#what-it-does)), and the MCP servers and APIs you add. An **API** is added by its OpenAPI 3 document (JSON or YAML, pasted or fetched from its address): each operation becomes a function, its parameters and JSON body the arguments, and a call that changes something (POST, PUT, PATCH, DELETE) always asks the person first. **Read it** lists the operations before you add it. Per tool: on or off, who may use it (everyone, admins, or chosen groups), on in new chats, ask before each call. An MCP server is tested before it is added; its key is stored encrypted and never shown. **Its certificate (https)**, per server or API: **Check the certificate** (the default: the CAs the app's system trusts), **Trust this CA** (a CA's certificate in PEM, pasted or from a file: a company CA, its issuing CA alone, or a self-signed server's own certificate; the chain must lead to it, each certificate up to it within its dates, and the name must still match; a server that does not send its issuing CA needs it given too, with the root in one bundle, or alone), or **Do not check** (any certificate is accepted; the form and the tool's card warn). When **Test** or **Read it** meets a certificate it does not trust, it says why (self-signed, issued by a CA it does not trust, for another name, not meant for a server, expired or not yet valid, it or a CA in its chain), who issued it, for which names, its dates and fingerprint, and offers both choices there; **Read it** checks the API's address too, even when its document was pasted. A secure connection that fails for another reason (no https at that address, a server that speaks only an old TLS version, no cipher in common) says so instead, as the chat does: no certificate choice helps there. The choice applies to everything sent to that server (its tools listed and called, long calls, an API's document fetched from its address, its calls, and a plugin's OAuth token address on the same server), never to anything else: with **Trust this CA** or **Do not check**, a redirect is followed only on the same host, and one to another host is not (the test or the call ends with its `HTTP 3xx`), so nothing is sent there; each change is audited (`tool.server_tls`, the CA by name and fingerprint). A server whose tools run long can have its own **Longest call** (up to 24 hours; otherwise **Settings → Chat → Longest tool call**, an hour). The same choices decide what [Arena MCP](mcp.md) serves each person's own agent (`mcp.call` in the audit log). |

| **People** | Add, search, and per person: credit, rate limits for their API keys, a new API key, password and 2FA resets, admin role, disable, sign out everywhere, legal hold, an export of their data, delete. **Export CSV** downloads everyone with spend and credit left. |
| **Groups** | App groups (the people you add), directory groups (whoever the company directory or the identity provider's groups claim puts in them, by name or DN) and SCIM groups (made and filled by the identity provider; their name and members are changed there). Tools and models are given to groups. Per group: how long members' chats are kept, a credit a month (shared or each member's), a cost centre, which safeguards apply, and the requests and tokens a minute each member's API key may use. A group's **priority in the answers' line** (-10 to 10, 0 for everyone) decides who goes first when the model is busy: higher first, and within one priority the line stays fair (fewest answers running, then served longest ago). Someone in several groups takes the highest. A change is audited. Below the list, the monthly **Chargeback** report. See [Retention, legal hold and exports](#retention-legal-hold-and-exports), [Credit for groups](#credit-for-groups) and [Rate limits for API keys](#rate-limits-for-api-keys). |
| **Tools** | What the chat's model may call: Argus, Python (the sandbox), the web (off until you turn it on and allow sites), image generation, the calculator, date and time, reading long files in parts, the canvas (documents and code beside the chat, changed by the model part by part), questions for the person (the model asks with choices instead of guessing), sub-agents (the model splits a task into parts done side by side), memory (the model remembers what each person asks it to; off for everyone under **Settings → Chat → Memory**, and nobody but the person sees their memories), **Decide (Laya)** (typed questions about a text answered with probabilities by the Laya decision model on the CPU; offered only while the `laya` module runs and has a checkpoint loaded, and otherwise says why: [chat.md](chat.md#decide-laya)), and the MCP servers and APIs you add. An **API** is added by its OpenAPI 3 document (JSON or YAML, pasted or fetched from its address): each operation becomes a function, its parameters and JSON body the arguments, and a call that changes something (POST, PUT, PATCH, DELETE) always asks the person first. **Read it** lists the operations before you add it. Per tool: on or off, who may use it (everyone, admins, or chosen groups), on in new chats, ask before each call. An MCP server is tested before it is added; its key is stored encrypted and never shown. **Its certificate (https)**, per server or API: **Check the certificate** (the default: the CAs the app's system trusts), **Trust this CA** (a CA's certificate in PEM, pasted or from a file: a company CA, its issuing CA alone, or a self-signed server's own certificate; the chain must lead to it, each certificate up to it within its dates, and the name must still match; a server that does not send its issuing CA needs it given too, with the root in one bundle, or alone), or **Do not check** (any certificate is accepted; the form and the tool's card warn). When **Test** or **Read it** meets a certificate it does not trust, it says why (self-signed, issued by a CA it does not trust, for another name, not meant for a server, expired or not yet valid, it or a CA in its chain), who issued it, for which names, its dates and fingerprint, and offers both choices there; **Read it** checks the API's address too, even when its document was pasted. A secure connection that fails for another reason (no https at that address, a server that speaks only an old TLS version, no cipher in common) says so instead, as the chat does: no certificate choice helps there. The choice applies to everything sent to that server (its tools listed and called, long calls, an API's document fetched from its address, its calls, and a plugin's OAuth token address on the same server), never to anything else: with **Trust this CA** or **Do not check**, a redirect is followed only on the same host, and one to another host is not (the test or the call ends with its `HTTP 3xx`), so nothing is sent there; each change is audited (`tool.server_tls`, the CA by name and fingerprint). A server whose tools run long can have its own **Longest call** (up to 24 hours; otherwise **Settings → Chat → Longest tool call**, an hour). The same choices decide what [Arena MCP](mcp.md) serves each person's own agent (`mcp.call` in the audit log). |
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
| **Quality** | How people rate the answers, over a day, a week, a month or three: per model and per assistant, the answers written, the share rated, the share rated up, and why they were rated down. The latest down-rated answers by title, model, reason and the person's words, never their content: a chat opens (read only, down to the rated answer) only when its owner shared it with the down vote, and each opening is audited. Below, the arena's leaderboard from the votes cast in the range. See [Quality](#quality) below. |
| **Sign-in** | Local accounts, the company directory ("Check the directory now") and company sign-in: OIDC or SAML, the identity provider (its issuer or SAML entity ID), its admin and required groups, and whether SCIM is on. |

Indexing, Packs and Explore talk to Argus through the app's server with
`ARGUS_KEY`, which never reaches a browser. With Argus left out they say
that it is not set up, and how to turn it on.

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
  room, or an admin's **Unload**) counts as unloaded at once, on every replica,
  though the engine lists it as loaded until it has stopped: a request for it
  a moment later waits for room like any other, instead of reaching an engine
  that would load it by unloading the model used least recently.
  The app never leaves the choice to the engine, which would unload the model
  used least recently, whichever it is. An API key's request (a coding agent,
  an IDE) gets room the same way: the gateway asks the app before sending it
  (its guardrail), and the app makes room, or the request waits, or it is
  refused with the reason. A place made for a model is its own until the
  engine loads it, so two requests at once for two models take two places.
  Should the engine still unload a model by its own choice (an admin's
  **Load** at the limit, or a request let through while the app is down,
  with the guardrail's `fail_open`), a kept one comes back, and so does the
  one new chats use, once a model that may make room has been idle for a
  minute (so an agent pausing between its requests is not pushed out for
  it). After an admin's **Unload**, it stays unloaded. In the chat such a
  model reads **Loads when asked**.
  When every place is kept, no other model loads on request, and the chat says
  so. A change of the kept list that flips this restarts llama-server (the kept
  models load again, one after another).
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
- **Could not load** is not always a broken file. To make room, the engine
  tells the model to stop; one told so while it still loads goes on loading
  and answering, and the engine kills it 10 seconds later and marks it failed.
  This is what happened to SmolLM2 in a load test with one model at a time:
  the questions waiting for the big model had the engine stop SmolLM2 the
  moment it started for its own questions; it answered them, and was killed
  and marked failed. The app tries a model that failed again after a minute,
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
- **Find on Hugging Face** searches GGUF models (most downloaded first), opens
  one to list its files grouped as models (a split model's parts together, by
  quantisation, vision projectors apart) with their sizes, whether each fits
  this machine (the GPUs, GPUs and RAM, or too big) and whether it is in the
  library already, and downloads the ones ticked into the library
  (`<library>/<owner>/<repository>/`). **Downloads** shows each one's progress,
  speed and time left; one runs at a time, a paused or cut off one goes on from
  where it got to (HTTP range), every file is checked against Hugging Face's
  SHA-256 and only then renamed into place, so the library never lists half a
  model. A finished one has **Add as a model**, which opens the form with its
  file chosen. The app needs the library writable by its user (`LLM_UID`) and,
  for gated repositories, `HF_TOKEN` of an account that accepted their terms.
  A download is refused when the disk would keep less than 2 GB free.
- **Load** loads a model now, beside the kept ones (refused when every place is
  kept). **Unload** unloads it, and stops keeping it. Answers wait while a
  model loads: seconds when its weights are in the page cache, minutes from
  disk.
- **GPUs.** With two or more GPUs, a model's form asks which it runs on: all of
  them (llama.cpp splits it by layer), or some (`device = CUDA0,...` in its
  preset). The engine numbers GPUs as `nvidia-smi` does
  (`CUDA_DEVICE_ORDER=PCI_BUS_ID`). The memory estimate uses only the GPUs
  chosen, and the image server's share only on the first. Two models kept side
  by side run best on different GPUs. The `.env` model runs on all of them
  unless `LLAMACPP_EXTRA_ARGS` has `--device CUDA1`.
- **Other GPU servers.** Another machine's OpenAI-compatible engine (llama.cpp,
  vLLM, SGLang, another gateway) serves models beside this one's. **Add a
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
- **Add a model** picks a GGUF from the **model library** (`LLAMACPP_LIBRARY_DIR`,
  searched three levels deep; a split model is listed once, by its first part).
  **Each file is read for what it is** (its header and tensor table, not its
  name): a dense or mixture-of-experts language model, with full, hybrid
  (a cache in some layers only), sliding-window or recurrent attention; or an
  embedding model, reranker, vision projector, draft head, image model or LoRA
  adapter. Only language models can be added (the engine serves chat models;
  the others belong to Argus, the image server or a model's own settings), so
  the rest are listed with what they are for. A split model with a
  part missing is refused as incomplete.
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
- **It starts from what fits this machine.** The GPU memory and RAM come from
  Prometheus (the `smi` profile's exporter), less what the image server may take
  (`IMAGEGEN_MAX_VRAM`) and the 1 GiB llama.cpp keeps free. The form estimates
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
  (`kv-unified`, as the `.env` model): one conversation can use all of the
  context. Each conversation keeps its own slot of it ([the token
  cache](#the-token-cache)). They run with `LLAMACPP_THREADS`. Sampling left
  empty is the model's own recommendation from its file, which the engine
  applies.
- **More engine options** are `key = value` lines with llama-server's long
  option names. Options the app or the engine owns (files and paths, the port,
  the key), those the form sets, and names this engine does not know are
  refused: a preset with an unknown option stops the whole engine. The known
  names are this llama.cpp build's (`src/Llm.Api/Models/engine-options.txt`,
  with the command that regenerates it after an engine upgrade). Should the
  engine refuse the list anyway, it serves the `.env` model alone until the list
  changes, and the models it left out say so on their cards.
- **Adding, editing or removing a model restarts the engine** so it reads the
  new list: a few seconds, then the loaded model loads again. The gateway
  learns of the change at once (or within a minute, if it was down).
- The `.env` model is always there; change it under Deployment. Image models
  and cloud models are listed too, for who may use them.
- **Who may use it**: everyone, admins, or chosen groups (admins always may).
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
  the cache). When the cache is full, the engine still empties idle slots, one
  at a time.
- **Checkpoints.** A hybrid or recurrent model cannot cut its state back to an
  earlier point of a prompt (a retry, an edited question, a changed tool list):
  it keeps copies of its state in RAM to go back to. Each such model's preset
  keeps 4 a slot (`ctx-checkpoints = 4`; llama.cpp's own is 32), which read as
  fast as 32 in every test, for an eighth of the RAM. `cache-reuse` (moving
  matching chunks into place) does not work for these models; llama.cpp says
  so in its log and goes without.
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

### Quality

Two things feed it, both from the chat ([chat.md](chat.md)):

- **Thumbs** under each answer, with a reason on a down vote (wrong,
  incomplete, too long, unsafe, ignored instructions, other) and a few words.
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
chat still has it (in a chat, an assistant's, in a chat deleted under legal
hold, in no chat), size and age; search by the file's name, the person or the
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
| Files in no chat | files no chat, assistant or waiting message has, older than the age chosen (7 days by default: a younger one may be being written) |
| Old pictures, videos and speech | what those tools made, past the age chosen (**Settings → Storage → Old generated media**, 90 days by default); the chats keep their words |
| Files of deleted chats | chats deleted while their owner was on legal hold, once the hold is over, with their files (the hourly retention job takes them too); it shows what the holds still keep |
| Leftovers on disk | what no database row owns: a download's `.part` file no download will finish, and Python sandbox jobs left behind (older than an hour) |
| Old backups | shown only: backups beyond the newest ones kept (`BACKUP_KEEP` in `.env`, 14 by default, or the number typed), never the latest nor the newest that ended well, and `scripts/backup.sh --prune --keep N` to run in `deploy/` on the host. With anyone on legal hold it warns that backups from before the hold hold their data (what they deleted since, too) and marks those taken before it: ask whoever placed the hold before removing them. A backup's start is the one its `MANIFEST` gives in UTC; a backup's name is the host's local time, so for a backup from before v5.3, which has only its name, one named up to 14 hours after the hold began counts as before it |
| Models nothing uses | model files no model of Admin → Models uses and no server reads, each chosen by hand: one deleted is gone from the library, and using it again means downloading it again |

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

### Credit for groups

A group's **credit a month** (Admin → Groups → a group → Policies) is shared by
its members, or **each member's**. It counts everything: the chat, API keys,
agents, from the first of the month (UTC). A person's own credit (People) counts
the same way: the chat and their keys together, one credit. Before each chat
answer the app checks the person's spend this month against their credit and
each of their groups'; for API keys the gateway asks the app before each
request (its guardrail, below) and refuses with the same sentence.

Each group with a credit is also a team at the gateway (`group-...`): the credit
per month, its members, and their keys in it, so the gateway itself holds keys
to it. A key is in one team: a person in several groups has theirs in the one
with the least credit (the app holds them to all of them). The teams follow
group changes within a minute, and every ten minutes. A team no group needs is
removed only once no key is in it (LiteLLM deletes a team's keys with it).

**Cost centre** is a label per group. **Chargeback** (below the groups list):
spend per group and per cost centre, month by month, and the people in no group;
**CSV** downloads it. A person in several groups counts in each group, and once
in a cost centre.

**The gateway's guardrail.** `config/litellm.yaml` has LiteLLM's generic
guardrail API call the app (`http://app:8080/internal/guardrail`, with the
gateway's master key) before each request. The app answers in milliseconds:
the credit, and the safeguards ([chat.md](chat.md#safeguards)) for API keys,
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
- **How it is reached**: Traefik sends `gateway.DOMAIN`'s chat completions to
  the app while the app's health check (`GET /v1/answer-cache`) says the cache
  is on; otherwise, and whenever the app is down, straight to LiteLLM as
  before. A change applies within Traefik's check, 10 seconds. The app passes
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

**Prices** are below ([Prices](#prices)); a new person's credit is in
`config/litellm.yaml`, each person's own under People, and a group's under
Groups.

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
- **Pictures, video and speech**, by what they are (Settings → Prices): a
  picture ($0.01), a second of video ($0.05), a minute of sound turned into
  text ($0.006), 1,000 characters read aloud ($0.015).

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

### Dashboards

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
