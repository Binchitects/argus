# CHANGELOG

One `##` per release, newest first. Under it, only the sections that release
has. Each release's full notes are also in its annotated tag — `git tag -n99
v2.9.0`.

Sections used:

- `:rocket:` **Epics and highlights** — the one or two things worth reading
- `:sparkles:` **New features & Enhancements**
- `:bug:` **Bugs fixed**
- `:boom:` **Breaking changes & Deprecations**
- `:arrow_up:` **Deps updates**

## v5.2.0 (2026-10-06)

### :rocket: Epics and highlights

- **Code Arena, an IDE in the browser**: Arena Code is now Code Arena, and
  `code-arena` opens an IDE like VS Code on your own machine around the same
  agent: an explorer, Monaco editor tabs, the agent's edits as diffs to accept
  or revert file by file, search, quick open, real terminals (a
  pseudo-terminal on Linux and macOS, ConPTY on Windows) and the chat beside
  them, with a status bar. `code-arena chat` keeps the agent in the terminal.
  `tools/package-code-arena.sh` builds packages for five systems with their
  checksums
- **Laya, typed decisions on the CPU**: an optional module
  (`COMPOSE_PROFILES=laya`, `laya.enabled` in Helm) runs the Laya decision
  model. The chat gets **Decide (Laya)**, typed questions about a text answered
  with probabilities in about half a second, in English (calibrated) and 100+
  languages including Persian; Arena MCP serves it to agents. Code Arena shows
  every command to Laya first and asks before one it rates likely to destroy
  something, write outside the workspace or reach the network, even in yolo
- **Dual licensing**: the AGPL-3.0-only, with additional terms (attribution,
  origin, trademarks), or a commercial license from Binchitects for uses the
  AGPL does not cover (LICENSING.md). Every interface shows its version and a
  Source link

### :sparkles: New features & Enhancements

- **Tools on servers with a self-signed certificate or a company CA**: per MCP
  server or API, **Trust this CA** (a CA in PEM; the chain must lead to it and
  the name must match) or **Do not check**, beside the default check. Test and
  Read it say why a certificate was refused (self-signed, an untrusted issuer,
  another name, its dates) with its fingerprint; each choice is audited, and
  it applies to that server's own host only
- **Argus signs in with GitLab accounts that LDAP checks**: after GitLab's own
  form refuses the password, each LDAP sign-in the page offers is tried, or
  only the one `GITLAB_LDAP` names (`off` turns it off)
- **Deep research leaves the web to its parts**: the report's model delegates
  the reading and the gap-filling (two rounds at most) and writes; 616 s here
  against 1,606 s in v5.1.0. With Web set to ask first, the model reads the
  web itself, each read asked
- Code Arena: `--version` prints the licence and the source; the IDE's layout
  and theme are kept between runs; the browser opens through a private
  launcher file, so the run's key is never on a command line
- Argus's own web app shows its version and a Source link
  (`ARGUS_SOURCE_URL` for a modified version)

### :bug: Bugs fixed

- A new chat starts on the admin's default model when it can load; a small
  model one person's agent loaded no longer became everyone's default
- Code Arena (found by a review before this release): a link whose target
  went through another link and then `..` could reach files outside the
  working directory; git reads could run a command the repository's own
  settings name (fsmonitor, hooks, filters, merge drivers, text conversion);
  the git tool could read outside the working directory (`diff --no-index`,
  `blame --contents`); unsaved text could be lost when the agent's edit was
  read again or the server stopped answering; macOS keys clashed with Ctrl in
  the terminal
- Laya's look at commands reads every part of a long command, asks when a
  part could not be read, and tells the IDE's page when it stops checking;
  the Laya server answers "busy" instead of queueing without limit
- Tool servers: a redirect to another host is not followed with a custom CA
  or no check; a failed TLS handshake is no longer called an untrusted
  certificate; an issuing CA given alone is held to its dates
- Argus posts each GitLab sign-in form with that form's own CSRF token
- The app image skips Code Arena instead of failing when its offline packs do
  not match the SDK's runtime

### :boom: Breaking changes & Deprecations

- **Arena Code is renamed Code Arena**: the binary `code-arena`, its config
  folder `code-arena`, the download `/api/downloads/code-arena`,
  `tools/publish-code-arena.sh` and docs/code-arena.md. The old config is not
  read: sign in again with `code-arena login`. `code-arena` alone now opens
  the IDE; the terminal agent is `code-arena chat`
- **The licence is the AGPL-3.0-only** (was the GPL-3.0), with a commercial
  license for other uses

### :arrow_up: Deps updates

- Code Arena's page: monaco-editor 0.57.0, @xterm/xterm 6.0.0,
  @xterm/addon-fit 0.11.0
- The laya image: laya 0.3.27 with torch 2.14.1+cpu on python:3.13-slim

## v5.1.0 (2026-10-05)

### :rocket: Epics and highlights

- **Arena Code in the browser**: `arena-code web` serves the same coding agent
  with Argus Arena's chat interface on your own machine: the folder's sessions,
  tool cards, diffs for edits, approvals, tokens with the cached share
- **Connect your tools that work**: every guide checked against its tool's
  current documentation and the live gateway, each ending with a check, with
  Windows (PowerShell) commands, and the site's certificate to download and
  trust on a self-signed deployment
- **Knowledge packs for Qt and seven more**: a `qt` pack with Qt 4.8, 5.15 and
  6.10 in one (12,595 pages, 150,648 symbols), each API with the versions that
  have it; `dotnet`, `cpp`, `cppreference`, `python` (now 3.14), `scripting`,
  `debugger` and `sqlite` built again. Their digests are in
  docs/argus/overview.md; they go to the packs bucket beside the Windows ones

### :sparkles: New features & Enhancements

- **Company sign-in by SAML 2.0** beside OIDC: the identity provider's metadata
  (address or XML) or its address, entity ID and certificate; strict checks
  (its certificate only, RSA-SHA256 or better, no signature wrapping, audience,
  recipient, lifetime, each assertion once on every replica); the same people
  rules as OIDC; steps for Entra ID, Okta and Keycloak
- **Confluence and SharePoint** as company knowledge: Confluence Cloud and Data
  Center (space permissions and page restrictions mirrored), SharePoint and
  OneDrive through Microsoft Graph (each file's permissions, delta sync); where
  they cannot tell, the groups an admin chose
- **The certificate to trust**: Connect your tools offers the site's CA
  (read from what Traefik serves) with its fingerprint and how to install it on
  Linux, macOS and Windows, and each tool's guide says what that tool needs
  (`NODE_EXTRA_CA_CERTS`, `CODEX_CA_CERTIFICATE`, Continue's CA bundle...)
- Connect your tools: Claude Code points every model alias and its sub-agents
  at the chosen model with its window and output limit; MCP servers added for
  every project; Hermes, Cline, Codex, Continue, Qwen Code, OpenClaw, Aider and
  dsh setups brought up to date; Roo Code (shut down) removed
- Arena MCP tells an agent the person's default chat model, and Arena Code
  starts with it
- A clearer Persian voice (piper's gyro: Whisper writes it back with 3.8%
  of characters wrong, against 14.4% for the previous voice)
- The live browser suite runs one model test at a time, and nightly from CI on
  a self-hosted runner on the stack's host (`.github/workflows/e2e-live.yml`)
- Argus cuts a text too long for the embedding model to fit, as Ollama does,
  instead of failing a pack build

### :bug: Bugs fixed

- Video never decoded on the GPU: its 3 GB budget left no room for the decode
  (about 11 GB). Decoding there now gets a 12 GB budget with the weights in RAM
  until needed (63 s for a short clip, against 309 s on the CPU)
- Qwen Code's guide now starts it with `--auth-type openai`: a fresh install
  stopped at "Operation cancelled" with the variables alone

## v5.0.0 (2026-10-05)

### :rocket: Epics and highlights

- **What people expect of a chat**: memory across chats, a prompt library with
  slash commands, assistants a team shares (projects grew into them), read-only
  links to chats, a canvas for documents and code beside the chat, Talk (a
  voice conversation read aloud as it is written), thumbs on every answer and
  Compare (two models blind, with a leaderboard)
- **Company knowledge and the company's own tools**: GitLab wikis and issues,
  folders and websites searched by each person within their rights; long
  files read by their passages; Arena MCP serves each person's chat tools to
  their own agent; **Arena Code**, our own coding agent, one standalone file
  per system, built offline
- **Governance and scale**: company sign-in (OIDC) and SCIM, retention and
  legal hold, exports for eDiscovery, credit per group with chargeback,
  safeguards and secret scanning on the API path too, app replicas on one
  database, a Helm chart, an offline bundle, and restore and rollback tests

### :sparkles: New features & Enhancements

- **Memory**: "remember that I deploy with Podman" is kept and read by every
  later answer, in any chat (a short block after the fixed notes, so the
  prompt cache keeps its start). The model offers to remember what you mention
  and keeps it only when you accept. Your account → Memory lists, edits and
  deletes them; each person or the company can turn it off. Nobody else sees
  them
- **Prompt library**: Workspace → Prompts keeps prompts with `{{blanks}}`,
  yours, shared with your groups, or the company's. `/` in the composer opens
  them; plugins bring their own (`gitlab-issues` brings `/triage`). Arena MCP
  serves them to agents as MCP prompts
- **Assistants**: instructions, files (up to 200), a model, thinking, tools,
  starters, an icon and a colour, shared with chosen groups or everyone, with
  editors. Workspace → Assistants is the gallery, with how much each is used.
  Nobody it is not shared with sees it, admins included. Existing projects are
  private assistants
- **Shared chats**: a read-only link for the company or chosen groups, to the
  whole chat or the branch on screen; signed in only; **Fork into my chats**
  copies it with its files; the owner sees the opens and revokes it
- **Canvas**: documents (Markdown) and code beside the chat, which you and the
  model both edit. The model changes them by exact edits, all or none; every
  change is a version with a diff and restore; a selection can be asked
  about. Export to Markdown, Word (built by the app) and PDF
- **Talk**: the sound-wave button starts a voice conversation. It listens,
  writes down what was said, and reads each sentence of the answer aloud as
  soon as it is written; speaking over it stops it
- **Feedback and arena**: thumbs up or down with a reason on every answer;
  **Compare** sends a question to two models side by side and blind, the vote
  moves an Elo leaderboard; Admin → Quality per model and assistant
- **A model for small steps, and Auto**: sub-agents, titles, compaction and
  the safeguards' check go to a small model; **Auto** routes each question to
  the small or the big model by kind and difficulty, with the reason shown and
  **Ask the big model**
- **Company knowledge**: Admin → Knowledge adds GitLab projects or groups
  (wikis and issues, read by each project's members), folders under
  `/knowledge` and websites (for chosen groups). The **Company knowledge**
  tool returns only what the asker may read, with its link. Long attachments
  and an assistant's files go to the model as the passages that match each
  question
- **Answer traces**: admins open where an answer's time went (the wait in
  line, each round with its tokens, cache share and speeds, each tool call and
  sub-agent), from a timer under the answer or Admin → Traces. Deep research
  says which step it is on
- **Arena MCP** at `https://DOMAIN/mcp`: each person's chat tools for their own
  agent (Claude Code, Qwen Code, Arena Code), signed in with their API key,
  run as them, each call audited
- **Arena Code**: a terminal coding agent in .NET 10 with the base library
  only, one self-contained file for Linux, Windows and macOS (x64 and Arm),
  built from `tools/offline-nuget`. It gets the gateway's models and every
  chat tool over Arena MCP, plus files, shell and git on the person's machine;
  permission modes, sessions, `ARENA.md`. Downloaded from Connect your tools
- **Argus by API key**: coding agents connect to Argus with the person's
  gateway API key; Argus asks the app whose it is and answers as their GitLab
  account. Nobody hands out a GitLab token any more
- **The API in CI**: a GitLab CI template and the `arena` CLI review each merge
  request and explain each failed pipeline, from your own pipeline
- **Answer cache for API keys**: an identical request with the same key is
  answered from the app's database at no cost (`x-arena-cache: hit`), off by
  default, opt-in per key or for all keys
- **Chat bots and the installable app**: Slack, Mattermost and Teams bots and
  email in answer as the person in a chat of theirs; the web app installs,
  works offline for its shell and gets push notifications; a browser extension
  asks about the page
- **Queued messages on the server**: a message sent while a chat answers waits
  on the server, survives a reload, and runs next; Send now and Cancel
- Document previews show every page, 20 more at a time
- Credit and alert news also go by email, and the admins' to an alerts webhook
- The certificate's expiry on Admin → Overview, with alerts 30 and 7 days
  before
- A plugin's OAuth sign-in is renewed, and a lapsed one says to connect again
- Video decodes on the GPU when 8 GB is free as the server loads, else on the
  CPU
- Events that come while a task answers wait their turn instead of being
  dropped
- The model servers may write into the model library (`LLAMA_CACHE` in it)
- **Company sign-in (OIDC)**: Entra ID, Okta, Keycloak, Google or GitLab, with
  PKCE; groups from the provider's claim; an admin group and a required group.
  **SCIM 2.0** at `/scim/v2` creates, updates and deactivates people and
  groups
- **Retention and legal hold**: how long chats are kept, for the company and
  per group; legal hold keeps everything of a person; exports of a person's
  data for eDiscovery, and Your account → Your data for their own
- **Credit for groups**: a credit a month per group, shared or each member's,
  over the chat and API keys together; groups mirrored as gateway teams; a
  chargeback report per group and cost centre
- **Safeguards everywhere**: secret scanning (refuse, mask or let through) in
  messages, files and on the API path, through the gateway's guardrail; groups
  choose which checks apply
- **Scale out**: app replicas share one database (`scale.yml`,
  `APP_REPLICAS`): one leads the once-only work and another takes over within
  seconds; approvals, Stop and settings reach every replica; scheduled tasks
  run once. Groups get a priority in the answers' line. A model on several GPU
  servers is a pool. An external Postgres through `DB_HOST` and friends
- **Helm chart** (`deploy/helm/argus-arena`) for Kubernetes
- **Offline and recovery**: `scripts/airgap.sh` packs and loads a bundle for a
  host with no network; `restore-test.sh` and `rollback-test.sh` prove a
  backup restores and a release rolls back, in a throwaway project

### :bug: Bugs fixed

- A comparison not voted on yet stays blind in search, and a question that
  could not be compared goes back into the box with Compare still on
- The web's own headers blocked the microphone and sound from `blob:`, which
  voice messages and Read aloud need

### :boom: Breaking changes & Deprecations

- Projects are now assistants: `/api/projects` is `/api/assistants`, a chat's
  `projectId` is `assistantId`, and a person's export has `assistants.json`
  in place of `projects.json`
- In the platform, Argus refuses a GitLab token from coding agents: they
  connect with the person's API key (`LLM_SERVICE_API_KEY` in the client
  files). A standalone Argus is unchanged
- `ILiteLlm.KeyInfoAsync` takes the key itself and returns the models it may
  call

## v4.1.0 (2026-10-04)

### :rocket: Epics and highlights

- **Fewer tokens, faster answers**: the prompt cache by design (over 99% of a
  chat's next turn read from the cache), tools on demand (a first question
  reads about 900 tokens instead of 6,000 or more), long tool results
  condensed, and answers as long as each person wants
- **A platform**: plugins (installed from a catalog, each person connecting
  their own account, writes asking first, every call audited), any REST API as
  tools by its OpenAPI document, and tasks run by events (GitLab merge
  requests, failed pipelines, issues, or any system's JSON), answering back in
  GitLab
- **Index on push and merge**, the GitLab webhook in one step

### :sparkles: New features & Enhancements

- **Tasks run by events**: a task runs on a schedule, on GitLab's events (a
  merge request opened or updated, a pipeline failed, an issue opened) or on
  any system's JSON posted to its address with its secret (shown once). A
  merge request brings its changes and a failed pipeline the end of its jobs'
  logs, read by a GitLab bot of the admin's (Settings → Scheduled tasks), which
  also comments the answer back when asked; each comment is audited. Argus's
  read-only token is never used for it
- Sub-agents make at most 5 tool calls each, and write at most 1,200 tokens a
  step (without thinking)

- **Index on push and merge**: Admin → Indexing → Push and merge webhook makes
  the GitLab webhook's secret (shown once; Argus keeps its hash, the app
  nothing), shows the address and the steps for GitLab, and lists the last
  deliveries. A push or a merged merge request updates that repository at once.
  Argus's GitLab token stays read-only: a Maintainer adds the webhook in GitLab
- **Prompt cache by design**: the tools stay in every round of an answer (the
  last round switches calling off instead of dropping them, which made the
  engine read the whole chat again), old messages are left out in steps, and
  every model's preset sets `cache-reuse`. The next turn of a long chat reads
  over 99% of its prompt from the cache. LLM Overview shows the cache hit per
  model
- **Tools on demand**: past 6,000 characters of tool definitions (Settings →
  Chat), a chat sends whole only the tools it loaded, and a line for each
  other; the model loads one with `load_tools` and it stays loaded in that
  chat. With every tool on, a first question reads about 900 tokens instead
  of over 6,000
- **Tool results condensed**: `fetch_page` takes a focus and returns a long
  page's passages about it; pages read lately come from a day's cache; a tool
  result past 24,000 characters goes to the model as its start and is kept
  whole as a file the model reads on with `read_file`. Sub-agents start with
  the tools the chat loaded, and never run more at once than the engine has
  places
- **Plugins**: Admin → Plugins installs ready-made tools (from the app's own
  plugins, a catalog checked by SHA-256 and optionally signed, a zip or an
  address), with their settings; a plugin that signs in per person calls its
  service as the person (OAuth or their own key, in Your account →
  Connections); its writes ask first; every call is audited. Comes with
  GitLab issues
- **APIs as tools (OpenAPI)**: Admin → Tools → Add a server or API takes an
  OpenAPI 3 document (JSON or YAML, pasted or fetched); its operations become
  functions, and every call that changes something asks the person first
- A model's tool arguments with a key written twice no longer fail the whole
  answer, and a tool's own fault ends only that call
- GitLab sign-in with a username and password (`GITLAB_USERNAME`,
  `GITLAB_PASSWORD`) can be set again; v4.0.0 had stopped passing it to Argus.
  docs/deployment.md maps every v3 `.env` option to where it went
- The answer line and sub-agents now keep to what the engine serves at once:
  the engine watcher reports the loaded models' places, as documented
- **Answer length**: Your account → Answers picks Short, Normal or Thorough for
  every chat; **Shorter** and **Longer** under an answer answer again at about
  half or twice its words

## v4.0.0 (2026-10-03)

### :rocket: Epics and highlights

- **Simple to deploy**: one compose file of 300 lines (from 2,100) and a short
  `.env`: the domain, the models folder, the first model and six secrets.
  Every module runs; one is left out with a line in
  `docker-compose.override.yml`. No setup containers: each running image
  prepares itself, and the app fetches the models (the first chat model, the
  picture, video and embedding files) and tells the speech server to fetch its
  own
- **Sound and video in and out**: voice messages from the composer, sound and
  video files; a model that hears and sees (Qwen3-Omni) gets the sound and the
  frames, others a transcript (Whisper, made once) and the frames; answers read
  aloud (Kokoro, a Persian voice); tools that make videos (Wan2.2 TI2V 5B) and
  speech
- **Picture, video and speech models managed like the chat models** in Admin →
  Models: on or off, keep loaded, load, unload, who may use them
- **Podman**: the same files with `podman.yml`, rootless

### :sparkles: New features & Enhancements

- TLS is Traefik's alone: its own certificate, Let's Encrypt (`ACME_EMAIL`), or
  a certificate of your own; Traefik's routes are one file and it no longer
  mounts the Docker socket
- The speech models are at the gateway for API keys (`/v1/audio/transcriptions`,
  `/v1/audio/speech`); the chat's `/api/chat/speech` reads an answer aloud
- Sound and video play in place in the chat and the Files panel
- Admin → Services probes every module (engine, pictures, video, speech, web search)
- The default chat model, the thinking levels and how many models the engine
  holds at once are settings in the app
- A failed model download is provisioned again; downloads land under a name of
  their own (the server's files)

### :bug: Bugs fixed

- A model that does not think got thinking switches its template does not know
  (Qwen3-Omni answered nothing)
- The video server could be unloaded as idle in the middle of a long job

### :boom: Breaking changes & Deprecations

- A new layout: the compose project is `arena` (volumes `arena_*`), `.env` has
  new names (`DOMAIN`, `MODELS_DIR`, `MODEL`, `DB_PASSWORD`, `APP_KEY`,
  `ENGINE_KEY`, `ARGUS_KEY`, `GATEWAY_KEY`, `GITLAB_URL`, `GITLAB_TOKEN`), and
  the app's image runs as uid 1000. `docs/deployment.md` has the move from v3
- No `.env` model: it is added under Admin → Models like any other
- The Settings page no longer edits `.env`: `apply-settings.sh`, the pending
  changes and the env samples are gone
- The stack CA, its bundle and the certificate download are gone
- Removed: Langfuse, vLLM, cAdvisor, DCGM, Redis, the preflight, the air-gap
  bundle, the Windows scripts, the Makefile, and the checks written for the
  old layout (acceptance, e2e-check, health, smoke-test, audit-auth, domain-check)

### :arrow_up: Deps updates

- ghcr.io/speaches-ai/speaches 0.9.0-rc.3 (CPU) for speech; models
  faster-whisper-large-v3-turbo, Kokoro-82M, Piper fa_IR amir; Wan2.2 TI2V 5B
  for video; Qwen3-Omni-30B-A3B as a model to add

## v3.2.0 (2026-10-03)

### :rocket: Epics and highlights

- **Argus Arena.** The product's name and logo
- **Projects**: chats kept together, with instructions and files every answer
  in them reads
- **Deep research**: a plan, sub-agents that search and read the web, and a
  report with numbered citations and its sources
- **Safeguards** against abuse and harm, each part configurable: limits per
  person, blocked words and patterns, the model checking each message,
  personal data masked, the web's content marked as data, suspension after
  repeated refusals

### :sparkles: New features & Enhancements

- **Chat**
  - **Queue** a message while an answer runs, or **Send now** (stops the
    answer and sends it)
  - **Export** a chat as Markdown, a web page, PDF or JSON, or a summary the
    model writes for a reader
  - **Search** your chats: titles, your messages, answers, tool results, file
    names; by time and model; a result opens the chat at that message
  - Long tool calls are seen to work (a running bar, a timer, a placeholder
    while a picture is drawn); waiting for the model counts the seconds
  - After compacting, the context gauge shows what the next request carries
- **Notifications** clear, one or all; the bell no longer scrolls sideways
- **Argus**: a repository's index is removed or rebuilt from the Indexing page;
  the four Windows packs are installed from the Binchitects bucket
- An open page says when a new version is deployed, with a Reload
- **Tests as scripts**: `clients-check.py` (the API, Argus over MCP, Qwen Code
  and DeepSeek Harness, 22 checks), `scale-test.py` (30 people chatting at
  once through the fair-use queue, every key at once, 60 sandbox jobs: none
  failed, no answer carried another's secret) and `upgrade-test.py` (an old
  release from zero, data in, this one over it; or this one from zero)

### :bug: Bugs fixed

- The chat header's title was a rename button; it is gone (rename from the
  chat list)
- An e2e test of a chat without a model left real chats in the admin's list
- On a new deployment the first chat failed with "the model gateway is not
  reachable": the gateway sets up its database (161 migrations) for a minute or
  more after the model already serves. The chat now waits for it, up to about
  a minute and a half

## v3.1.0 (2026-10-02)

### :rocket: Epics and highlights

- **Sub-agents.** The model splits a task into parts done side by side by
  sub-agents with the chat's tools. Their work shows live, one under the
  other, as the chat shows its own (thinking, tool cards, pictures, words),
  folds when done, and is kept with the call; what they make (ten pictures at
  once included) is the chat's, in the answer and the Files panel as soon as
  it exists
- **Scheduled tasks.** Questions asked on a schedule, as you, delivered as a
  chat, a notification, an email and a webhook post
- **Notifications that happen**: an answer ready while you were away, a task
  run, credit at 80% and used up, system alerts for admins, model downloads;
  on the desktop too while the tab is hidden

### :sparkles: New features & Enhancements

- **Chat**
  - **Answer now** cuts a long thought short and answers at once
  - A **context gauge** shows how full the model's context is and what fills
    it (system prompt, tools, files, your messages, answers, tool results),
    with **Compact now**; compaction by hand or on its own before it fills
  - The model **asks you** with choices instead of guessing
  - **Documents** (PDF, Word, PowerPoint, Excel, OpenDocument) show as their
    pages; pictures and pages **zoom**
  - **Download all** of a chat's files as one zip
  - **Mermaid** diagrams drawn in the answer, tall and wide ones readable;
    right-to-left text read in its own direction; tool calls show code and
    output as code blocks; tool calls may run for hours and show progress
  - Answers finish when the page is closed, and can be watched again
- **Models**
  - **Hugging Face**: search GGUF models, see which fit the machine, download
    into the library (resumed, checked against their SHA-256)
  - **Working hours**: which models are kept loaded, and which new chats start
    on, by day and hour
- **Argus indexing**: choose repositories and branches, update one, see each
  at its commit with progress, on a schedule
- **Sandbox**: OCR in Arabic, Persian and Chinese (simplified and
  traditional); Chinese, Japanese and Korean fonts; LibreOffice runs in a job
- **Accessibility**: focus comes back to what opened a dialog; the chat
  announces only what matters; contrast and target sizes checked on every page
- **One version everywhere**: `VERSION` at the repository's root sets the
  app's, Argus's and the UI's
- **TLS**: the stack makes its own CA once and signs the domain's certificate
  with it (renewed, and reissued for a new domain, with no restart), so people
  trust it once; or the operator's own certificate. No HSTS, so a browser can
  still pass the warning before it trusts the CA

### :bug: Bugs fixed

- Spend: the overview, People, the export and each person's credit counted
  only API keys (LiteLLM books the chat to the person as an end user); they now
  read the gateway's request log as the dashboards do. An answer's cost counts
  its sub-agents
- Pictures made by sub-agents running side by side were lost (a shared
  database context); each sub-agent now works in its own scope
- The UI said v1.0.0 (an empty build argument hid `VERSION`)
- A sent file stayed in the input box until the whole answer had finished
- The time zone picker on the indexing page did not open
- The Files sheet on a phone had two close buttons on top of each other; the
  picture viewer opened with focus on a tooltip, so Escape closed only that

## v3.0.0 (2026-09-28)

### :rocket: Epics and highlights

- **A platform around the model.** The enterprise app (`src/Llm.Api`,
  `src/web`) signs everyone in (accounts, LDAP or Active Directory, two-factor,
  OIDC for the services with their own login), runs the chat with its tools
  (Argus, a Python sandbox, the web, image generation, files and Office
  documents), manages people, groups, models and every setting, and shows ten
  dashboards, every service's logs and the alerts. Only the app, the web and
  the model server remain: the services it replaced are gone from the
  deployment with no compatibility path; the plan and its phases are in
  `docs/plan.md`
- **Argus in .NET.** Every part of the Python Argus has a C# counterpart in
  `src/Argus`: the indexer, ACL, knowledge packs, the query engine, the MCP
  server with its seventeen tools, the admin surface, the scheduler and the CLI.
  A conformance run drove both against the same fake GitLab and embeddings and
  compared every index table, tool call, HTTP answer and pack build: identical.
  The Python package is gone
- **Argus alone** is still a deployment of its own (`deploy/argus-standalone/`):
  its own small app with accounts, a chat that searches the code, and keys for
  MCP clients and the gateway
- **One repository layout**: `src/`, `tests/`, `deploy/`, `tools/` and `docs/`,
  one .NET solution (`LlmService.slnx`) and one CI workflow for every part
- **Several models at once, on any GPU, on any server.** Models are kept loaded
  or loaded on request, up to `LLAMACPP_MODELS_MAX`, each on the GPUs chosen for
  it, with a memory check per GPU; and models on other machines' GPU servers
  sit behind the same gateway, as models of their own or as second copies of
  one here
- **Live previews in the chat**: pages, pictures, Mermaid diagrams and React
  components the model writes run in a sandbox with no network and no access
  to the app

### :sparkles: New features & Enhancements

- Argus: `argus user` for the standalone app's accounts; embeddings from
  llama.cpp over the OpenAI protocol (`ARGUS_EMBED_URL`); `argus backup`
  includes the app database; `argus verify --claude-hook` is a complete Claude
  Code Stop hook; optional HTTPS from PEM files; `argus healthcheck`
- Dashboards: an index-health row on Indexing; KV cache and prompt throughput
  for llama.cpp on LLM Overview
- The image server's GPU budget is a fixed 2 GiB (`IMAGEGEN_MAX_VRAM`)
- **Connect your tools** (`/setup`): each person makes their own API key and
  follows numbered steps for their tool, with this deployment's address and the
  model's limits filled in, Argus over MCP and the certificate: Claude Code
  (the gateway serves `/v1/messages`), Codex CLI (`/v1/responses`), Qwen Code,
  OpenCode, Aider, Hermes, OpenClaw, DeepSeek Harness, Continue, Cline and Roo
  Code, Python and curl
- Models: each file is read for what it is (dense or MoE, attention type,
  embedding, reranker, image, projector, draft) and offered only the settings
  its kind has, with its real context and output limits and a memory estimate
  checked against the engine's own fit
- Argus packs are added like models: **Add a pack** lists the built packs in
  the pack library (`ARGUS_PACK_LIBRARY_DIR`), shows what each holds, and
  loads it by linking, nothing copied
- Argus Explore searches references, code and docs across every repository and
  pack, and shows a document as rendered Markdown or its source
- The Python sandbox adds OpenCV, scikit-image, pillow-heif, polars, plotly,
  XGBoost, LightGBM, Numba and geopandas; each run gets one thread per library
  pool, and a run's address-space limit is 3072 MB by default. A plotly chart
  Python writes opens running in the Files panel
- The test GitLab seeds a read-only account for Argus and, with
  `SEED_PERSON_EMAIL`, a person of the platform, for demos

### :bug: Bugs fixed

- `argus verify` never exited 2: it read a `status` that `verify_text` nests
  under `corrections`, so the Stop hook passed every contradicted draft
- `docs_verify` reported the description as part of the last contract field;
  contradicted fields carry `stated`
- Python docstrings never reached the index: ctags was not asked for each
  symbol's language. Symbol contract version 3 re-extracts on the next pass
- A deploy on empty volumes: the sandbox image did not build (odfpy is published
  only as source), and every picture failed because the image server filled the
  empty GPU before the chat model

### :boom: Breaking changes & Deprecations

- The repository moved: `stack/` is `deploy/` (its `deploy/` is `services/`),
  `app/` is `src/` and `tests/`, `scripts/` is `tools/`, `dotnet/` and
  `frontend/` are `src/Argus` and `src/argus-web`. In `.env`,
  `LLM_DEPLOY_DIR=./deploy` becomes `LLM_SERVICES_DIR=./services`
- The Python Argus and its tests are gone; the .NET image is built from
  `src/Argus/Dockerfile`
- Grafana is gone: its dashboards are drawn by the app. Its data volume is no
  longer used and can be deleted
- The services the app replaced are gone, with their settings: Open WebUI
  (`chat.` and the `openwebui` profile, `WEBUI_*`, `OPENWEBUI_OIDC_CLIENT_SECRET`),
  `identity-proxy`, the `admin.` and `next.` redirects, the `auth` profile with
  `auth-init` and its basic-auth fallback (`PROXY_AUTH_*`, `PROTECTED_CHAIN`,
  `protected-chain`), the one-time import of an Authelia user file and its
  password hashes, and `services/argus-local.yml`. Start from an env sample:
  none of these keys is read any more
- `AUTHELIA_ADMIN_PASSWORD` is `ADMIN_PASSWORD`; `config/authelia/directory`
  is `config/directory` (Argus reads it through `ARGUS_USERS_FILE`); the
  gateway's identity header is `X-LLM-User-Email`
- An OIDC client the configuration does not name is deleted at start
- `config/engine/active` is `config/engine/keep` (a list); the engine numbers
  GPUs as `nvidia-smi` does (`CUDA_DEVICE_ORDER=PCI_BUS_ID`)
- `SANDBOX_JOB_MEMORY_MB` defaults to 3072 and `SANDBOX_MEMORY` to 4g

## v2.9.0 (2026-09-18)

### :rocket: Epics and highlights

- **The Windows packs know which Windows.** Microsoft publishes the OS each API
  arrived in, in the front matter of every sdk-api page, and the adapter parsed
  those keys and dropped them — so every pack built from that reference knew an
  API's header and its `.lib` and not whether the target machine exports the
  function at all. The OS fields now lead the contract both `docs_lookup` and
  `docs_search` return
- **The admin console manages packs.** A Knowledge packs page: installed packs
  with their version, embedding model, size and licence, plus install from a
  URL, update from a published index, and remove
- **A grounded evaluation, and the defect it found.** 52 questions whose
  answers are read from Microsoft's own front matter take the packs from
  **7/52 to 51/52** — and turned up 29,557 symbols that no lookup could reach

### :sparkles: New features & Enhancements

- Packs carry `req.target-min-winverclnt` (loaded on 52,506 of sdk-api's 65,908
  pages) and `req.target-min-winversvr` (50,530), across 285 values from
  Windows 2000 Professional to Windows 11 24H2 and Server 2025
- Packs carry `req.redist` (2,893 pages) — a different kind of requirement from
  an OS version: the API is there, and something else has to be installed first
- The driver reference gains `req.kmdf-ver` and `req.umdf-ver`. A WDF driver
  targets a framework release rather than an OS build, and pages like
  `WdfDriverCreate` state no OS at all. Inert for sdk-api, whose values for both
  keys are empty on every page
- Two new packs, `win32-samples` (136.2 MB) and `wdk-samples` (76.8 MB) — the
  sample corpora the composites always advertised and neither shipped pack
  contained. What calling an API looks like in a working program, against the
  reference's description of what it does. The set is now 11 packs, 1.87 GB,
  444,058 symbols, 0 unresolved each
- New admin endpoints `GET /admin/packs`, `POST /admin/packs/install|update|remove`,
  all under the admin token because all four write a directory the MCP path
  never touches. Install and update run as **jobs** — a pack is up to a
  gigabyte and the console's client gives up after ten seconds. Remove is one
  unlink and is deliberately not a job
- Update reads `ARGUS_PACK_INDEX_URL`. Unset, the button is absent and the card
  names the variable rather than failing, the same discipline as the webhook
  token
- New evaluation harness, `evals/run_windows_versions.py`, with an `--ablate`
  mode that builds the same pages twice so one variable moves

### :bug: Bugs fixed

- **Interface methods were unreachable under their documented name.** 45% of
  the reference is COM methods, titled `IFoo::Bar (header.h)` — a qualified name
  with no kind word, which the title regex required. The UID spells the same
  entity with a dot, so only that was indexed: 29,557 symbols carried a `.` and
  exactly 1 carried a `::`. `docs_lookup("IMFCaptureSource::GetMirrorState")`
  returned nothing. Worst shape a miss can take here — the server's
  instructions read an empty result as "undocumented" and forbid answering from
  memory, so the caller gets a refusal rather than a wrong answer
- README's pack table carried pre-rebuild sizes and claimed samples the `win32`
  pack does not contain; it is the API reference alone, 65,906 of sdk-api's
  65,908 files
- Corrected the pack doc's source list, which still named two sources of sixteen

### :boom: Breaking changes & Deprecations

- None. Packs built by an earlier adapter still install and still answer; they
  simply do not carry a version floor, and `docs_lookup` for a COM method under
  its `::` spelling still misses until they are rebuilt

## v2.8.0 (2026-09-17)

### :rocket: Epics and highlights

- **NVMe temperature on both dashboards, with no new exporter.** node-exporter
  mounts the host `/sys`, so its hwmon collector had been scraping the kernel's
  `nvme` sensors all along and Prometheus already held
  `node_hwmon_temp_celsius` for both drives. The gap was that no panel read it

### :sparkles: New features & Enhancements

- `resources.json` and `stack-performance.json` gain NVMe temperature panels.
  The chips arrive as `nvme_nvme0` / `nvme_nvme1` with nothing linking a chip to
  a drive model, so `label_replace` names them cheapest-layer-first: a generic
  fallback, so a drive added later still appears at all, and two exact matches
  on top for the real models
- Thresholds are a drive's — yellow 70, red 85 — because an NVMe throttles well
  before a core does

## v2.7.1 (2026-09-17)

### :bug: Bugs fixed

- **Indexing a second branch emptied the cross-repo graph for the whole
  estate.** A shared header appeared once per ref, the include resolver could
  not choose between two identical paths and recorded it `ambiguous`, so every
  edge into that project vanished — measured on the fixture as 2 edges to 0,
  with nothing anywhere reporting a problem

### :sparkles: New features & Enhancements

- Resolution now prefers the branch the include came from, as a preference
  rather than a filter, so a header that exists only on another branch still
  resolves
- The cold fixture lifecycle indexes trunk and a release branch and asserts the
  graph survived with zero ambiguous includes

## v2.7.0 (2026-09-17)

### :rocket: Epics and highlights

- **Argus reads what the code does, not just what it is called.** The doc
  comment above every definition was already in the index (`files.content` plus
  `symbols.line`) and had never been read. Measured on a deliberately
  falsifiable fixture: for *"what reclaims keys whose time to live has
  elapsed"*, ranking on names alone returns the wrong function and ranking with
  the doc returns the right one — and the mirror question flips the same way

### :sparkles: New features & Enhancements

- Doc comments are extracted, stored, led into the embedded text, and returned
  on every symbol result
- New `overview` tool describing what each repository *is*: its README, its
  layout, its key symbols
- Multi-branch indexing: name a branch and get that branch, name none and get
  trunk. An unindexed branch is refused by name, listing the branches that are
- Branch-agnostic behaviour is verified rather than assumed — eight checks over
  a real two-branch index, part of the cold fixture lifecycle

### :bug: Bugs fixed

- `symbols_sha` could not notice an extractor change
- `argus index` never embedded at all, so the poller and the webhook were
  indexing code `semantic_search` could never see
- Vectors could not notice their text had changed
- vec0 tables accumulated orphans without bound while polluting the KNN stage

## v2.6.1 (2026-09-17)

### :bug: Bugs fixed

- `ArgusIndexErrored` fired for ever over a repository whose worktree directory
  existed but was not a usable worktree: the check tested only that the path was
  there, so it took the checkout branch, failed, and could never take the
  worktree-add branch that would have rebuilt it. An alert that cannot clear is
  what teaches people to ignore the ones that can
- The Dockerfile guard found its own gap and now discovers the repo's top-level
  trees instead of listing two

## v2.6.0 (2026-09-17)

### :rocket: Epics and highlights

- **verify-after has an enforcement point.** `docs_verify` was an MCP tool, and
  that was the problem: every client can run a shell command when the model
  finishes and block on its exit code; almost none can be made to call a *tool*
  at that moment

### :sparkles: New features & Enhancements

- `argus verify` is the same check with an exit code — 0 clean, **2 contradicted
  (blocking)**, 6 could not check and deliberately **not** blocking, because a
  deployment without packs would otherwise become an agent that cannot finish a
  sentence
- `clients/claude-code/verify-after.sh` wires it into a Stop hook

## v2.5.6 (2026-09-17)

### :sparkles: New features & Enhancements

- An Explore page in the admin console that answers what four identical-looking
  chat failures actually are: not in the code, named differently, private, or
  never indexed. Symbols by fragment, files by path, repository counts —
  including the file showing **0 symbols**, which is the difference between "the
  agent cannot find it" and "it is not in the index"
- Its queries are unfiltered by design, so they live in `store/explore.py` apart
  from the access-scoped ones, with a test asserting no MCP tool module imports
  them

### :bug: Bugs fixed

- The route cleared `row_factory`, so it answered 200 with an error beside empty
  lists and the console drew "Nothing is indexed yet" while the index held
  seventy symbols

## v2.5.5 (2026-09-17)

### :rocket: Epics and highlights

- **Index on push, not only on a timer.** `POST /hook/gitlab` takes a GitLab
  push event and indexes the repository that changed, gated by its own
  `ARGUS_WEBHOOK_TOKEN` (unset = the route does not exist)

### :sparkles: New features & Enhancements

- A push during a pass is queued rather than dropped, drained one repository per
  pass; an overfull queue collapses into one full pass
- Events it has no use for are acknowledged rather than refused, because GitLab
  disables a webhook that keeps failing. The poll stays on as the floor

## v2.5.4 (2026-09-17)

### :bug: Bugs fixed

- **`pack update` had no producer.** Nothing could make the index JSON it reads,
  a format with a mandatory checksum and a consumer-facing URL that existed only
  in the parsing code. `argus pack index` now writes it
- The update command itself had no test, only its parts did
- An archive refetch kept pages the new release deleted, because extraction
  wrote into the existing tree without clearing it — so the pack reported the
  new version while serving documentation that no longer exists

## v2.5.3 (2026-09-17)

### :rocket: Epics and highlights

- **Every MCP tool is contract-tested against live Argus.** `verify_mcp.py`
  asserted `len(tools) == 5` against a server with sixteen, so it could only
  ever fail and nobody ran it

### :sparkles: New features & Enhancements

- `verify_tools.py` takes the tool list **from the server** so there is no count
  to keep in sync, calls every tool over StreamableHTTP with real GitLab tokens,
  checks each result's declared shape, and asserts no structured field names a
  repository the caller cannot read
- Refusals the server makes on purpose are classified rather than reported as
  breakage; tools the fixture cannot exercise print as **NOT COVERED** rather
  than counting as passing — 41/41 with 18 reported skips

### :bug: Bugs fixed

- `ensure_mirror` did not retarget a mirror whose GitLab URL changed, so a host
  migration would have failed every repository at once with an error naming an
  address no longer in the configuration
- A vacuity guard in `verify.py` printed its failure text on success and let the
  isolation checks pass against nothing
- The Dockerfile COPY whitelist, which had then broken `docker build` four times

## v2.5.2 (2026-09-17)

### :bug: Bugs fixed

- **The bundled test GitLab is a fixture again.** It was
  `restart: unless-stopped`, so it survived reboots and sat at 2.63 GiB and
  2.17% CPU indefinitely while serving nothing. It is `restart: "no"` now, with
  `scripts/test-gitlab/run.sh` as its lifecycle — up, wait, seed, verify,
  teardown, **including on failure**
- `run.sh` waited on a readiness endpoint GitLab 404s through Docker NAT
- `verify.py` wrote its report to a path the docs restructure left behind
- Its work dir had to move off the checkout for SQLite WAL to work at all

## v2.5.1 (2026-09-17)

### :rocket: Epics and highlights

- **Embeddings on the GPU.** The embedder was CPU-only and capped at two cores,
  which made it the entire cost of an indexed lookup. Measured warm:
  **94 ms → 5 ms median per embed, 18×**, for 849 MB of VRAM

### :sparkles: New features & Enhancements

- Bulk passes move by the same factor: 70 symbols, 6.58 s → 0.43 s
- `acceptance.py` now asserts `ollama ps` reports GPU, because Ollama falls back
  to the CPU silently and nothing else in the stack would notice
- Engine throughput unchanged: 19.3 vs 19.8 tok/s, inside the run-to-run spread

## v2.5.0 (2026-09-17)

### :rocket: Epics and highlights

- **The index measures itself and reindexes itself.** Argus exports Prometheus
  metrics for index freshness, an `ArgusIndexStale` alert watches them, and the
  serve process now runs the periodic reindex that was documented but never
  wired up

### :sparkles: New features & Enhancements

- The admin console's Overview shows the same numbers, from the same snapshot
  the alert is built on — so the tile, the line in Grafana and the page cannot
  disagree

### :bug: Bugs fixed

- A repository with no branches silently disappeared from a run
- An admin-panel test gate sat mid-file, so half the checks could not fail
- `package.sh` shipped config files the containers cannot read when the builder
  has a tight umask

## v2.4.0 (2026-09-16)

### :rocket: Epics and highlights

- **The admin panel becomes a console.** A sidebar with Overview, People, Model,
  Indexing, Monitoring and Settings; search and pagination over people;
  per-person pages; **delete an account** (which did not exist); CSV export with
  formula-injection escaping; service health; a theme toggle

### :bug: Bugs fixed

- It would delete the account you are signed in as — which it did to the live
  administrator during testing
- It would delete the last admin

## v2.3.0 (2026-09-16)

### :rocket: Epics and highlights

- **Thinking level is now a per-chat choice**, from the Open WebUI model picker:
  the stack creates a `Deep think` / `Balanced` / `Quick` / `No thinking` model
  per level, verified end to end at 2654 / 1251 / 0 characters of reasoning

### :bug: Bugs fixed

- The `Reasoning Effort` field Open WebUI already has does nothing on this
  stack — LiteLLM drops it for a custom `openai/` api_base — which the docs had
  been quietly wrong about

## v2.2.0 (2026-09-16)

### :sparkles: New features & Enhancements

- `clients/`: a copy-pasteable sample config per agent harness, each marked with
  whether it was actually executed
- **Qwen Code was tested for the first time** (qwen 0.23.3, against the stack's
  own gateway), DeepSeek Harness re-verified after the restructure, and the four
  gotchas that only appear when a real client drives it are now written down

## v2.1.2 (2026-09-16)

### :bug: Bugs fixed

- "failed to connect to Argus" in Open WebUI now says why. Open WebUI renders
  any 401 as a connection failure, and the sentence explaining the refusal went
  into the response body and nowhere else, so the logs said only
  `reason=token_rejected`
- `denied` events now carry `detail`, which is what makes the usual cause — the
  person has no GitLab account — visible from `docker compose logs argus`

## v2.1.1 (2026-09-16)

### :bug: Bugs fixed

- **The airgap bundle, verified by booting it on a clean project** — which found
  four ways it could fail on a host that cannot fix it:
  - the locally-built images were tied to `COMPOSE_PROJECT_NAME`, so renaming
    the project made `up` die on `No such image`
  - nothing checked the bundle covered the profiles about to run
  - the image archives shipped mode 0600, so extracting as root then running as
    yourself gave permission denied
  - the documented split-join silently wrote a 0-byte file on a read-only mount

### :sparkles: New features & Enhancements

- A profile-coverage check in `load.sh --check`

## v2.1.0 (2026-09-16)

### :sparkles: New features & Enhancements

- **A repository you can navigate**, and packaging scripts that check their own
  output: `argus/` → `src/argus/`, `llm-stack/` → `stack/`, one `docs/` tree,
  repository tooling in `scripts/`, and the older standalone Caddy deployment
  retired
- `release.sh` gained an offline mode

### :bug: Bugs fixed

- The drop-in archive had been shipping neither the admin panel nor the CPU
  exporter, and refused to build at all on a machine where the stack had run
- The airgap bundler never read its own archive back, and now does

## v2.0.0 (2026-09-16)

### :rocket: Epics and highlights

- **Indexing you can watch and logs you can search**: Loki on by default, an
  Indexing dashboard, and five dead or silent tools fixed

### :bug: Bugs fixed

- `impact_of` failed for every permitted caller
- `semantic_search` failed for everyone
- The per-repo progress table had never worked
- An unreachable GitLab exited on a raw traceback
- The panel discarded a failed run's log, leaving only an exit code

### :sparkles: New features & Enhancements

- GitLab username/password auth
- A redesigned admin panel
- Every path as an `.env` variable

## v1.14.0 (2026-09-15)

### :sparkles: New features & Enhancements

- Offline bundles
- A settable thinking level
- RTX 5090 / NVFP4 samples
- A verified backup
- The Argus audit trail

### :bug: Bugs fixed

- Four silent deploy failures

## v1.13.0 (2026-09-14)

### :sparkles: New features & Enhancements

- Argus per person in Open WebUI, read-only
- "ask a maintainer" notices
- All instructions in the README

## v1.12.1 (2026-09-14)

### :bug: Bugs fixed

- Fresh-clone deploy verified

## v1.12.0 (2026-09-14)

### :sparkles: New features & Enhancements

- `docker compose up` is the whole deployment

## v1.11.0 (2026-09-13)

### :sparkles: New features & Enhancements

- The documentation tool accepts the questions it is actually asked

## v1.10.0 (2026-09-13)

### :sparkles: New features & Enhancements

- Deployable from a clean clone

## v1.9.0 (2026-09-07)

### :sparkles: New features & Enhancements

- Argus by default, and the 177B MoE question answered

## v1.8.0 (2026-09-07)

### :sparkles: New features & Enhancements

- Acceptance-tested

## v1.7.0 (2026-09-07)

### :sparkles: New features & Enhancements

- The domain is settable, and setup is re-runnable

## v1.6.0 (2026-09-06)

### :sparkles: New features & Enhancements

- A clean checkout deploys unattended

## v1.5.0 (2026-09-04)

### :bug: Bugs fixed

- Tool calls that survive streaming

## v1.4.0 (2026-09-04)

### :sparkles: New features & Enhancements

- One number per person, and the key that produced it

## v1.3.0 (2026-09-04)

### :sparkles: New features & Enhancements

- Monitoring that outlives the engine

## v1.2.0 (2026-09-04)

### :sparkles: New features & Enhancements

- Long context on a 24 GB card

## v1.1.0 (2026-08-23)

Forty-nine commits since v1.0.0. The theme is retrieval quality: v1.0 could
index and serve, but nobody had measured whether `docs_find` actually answered
description-shaped questions. It did not, and most of the reason was data rather
than ranking.

Note that `pyproject.toml` still read `0.1.0rc1` throughout v1.0 — the version
was never bumped at that release. It now tracks the tag.

### :rocket: Epics and highlights

- **`docs_find` answers roughly twice as often.** Measured over a 36-question
  set, top-10: **25% → 44%** unscoped, and **58%** when the caller names the
  source

### :sparkles: New features & Enhancements

- **`docs_find` now serves the hybrid arm.** `search_symbols_hybrid` was
  implemented, documented, and had no callers — the tool ran the purely lexical
  arm. Term overlap between a question and its answer's description is 35%, so a
  term scorer had a low ceiling however it weighted; twelve of twenty-five
  answers shared one word with the question or none
- **Every pack now has zero blank descriptions.** `docs_find` searches that field
  and skips rows where it is empty, so a blank description made a symbol
  invisible while it still occupied disk. cpp went from 100% blank, python from
  50%
- **Descriptions come from the page, not the page's title.** cpp two-word
  descriptions 56% → 2.2%, python 62% → 16.3%. `_countof`'s entire searchable
  text had been "_countof Macro"
- **A chunk now says which of a page's symbols it documents.** A 369-symbol page
  returned an arbitrary 8 of them, ordered by rowid
- **The tool description names the installed sources**, and a `lang` naming no
  installed pack widens instead of returning nothing. Measured through Hermes:
  the model passes `lang` on 5 of 8 calls, including `scripting` for a
  PowerShell question — knowable only from that list
- **`gitlab.auth: password`** for username/password sign-in, alongside the
  existing access token. The two are not interchangeable: an access token goes
  in `PRIVATE-TOKEN`, an OAuth token in `Authorization: Bearer`.
  `argus/credentials.py` owns that distinction and every API caller asks it for
  headers
- The password is read from `ARGUS_GITLAB_PASSWORD` only; a `password` key in
  the config file is **refused**, not ignored
- **Indexing at estate scale**, first run: 47 repositories, 55,603 files,
  **1,491,167 symbols**, 37.8 minutes, zero failures or timeouts
- **`which_repo` ranks on the raw score**, not the display-clamped `confidence`.
  Every score above 1.0 compared equal and ties broke alphabetically — lz4 beat
  zstd for "compress a byte stream with a dictionary" because `l` sorts before
  `z`
- Packs rebuilt against current upstream: cpp, python (3.14), wdk, win32,
  scripting. The python pack records the branch it was actually built from — it
  claimed `main` while built from `3.14`

### :boom: Breaking changes & Deprecations

- **Verified against GitLab 19.2.1: recent GitLab has removed the password
  grant**, and no headless username/password path replaces it. The error says so
  and names the fix rather than reading like a bad password. Use `auth: token`
  unless your GitLab predates the removal

### :bug: Bugs fixed

- **Known and unfixed:** `which_repo`'s lexical evidence matches query words
  against identifiers, and at 1.5M symbols "store", "key" and "memory" are
  identifiers nearly everywhere. Asked to "store key-value pairs in memory with
  expiry", redis did not place. Routing is 5/10 on the estate set

## v1.0.0 (2026-08-12)

### :sparkles: New features & Enhancements

- Initial release. 11 packs, ACL enforced structurally and audited, container
  healthy, 741 tests

## v0.1.0-rc1 (2026-08-07)

First tagged release: a private GitLab code index with per-developer access
control, nine MCP tools, a cross-repo dependency graph, and portable public
documentation packs. Shipped Phases 1, 2, 3 and 5; 539 tests, green in the
container.

### :rocket: Epics and highlights

- Measured on real code — four public C projects, because production GitLab was
  not reachable — 1,199 files, 33,102 symbols, 0 errors, a 14.8 s cold pass,
  1.2% ambiguous includes, and `which_repo` 8/10 top-1 at a 0.5 ms median. Full
  numbers and misses in `docs/index-measurements.md`

### :boom: Breaking changes & Deprecations

- **Not yet validated at production scale.** Pilot with a limited repo set and a
  handful of developers; `docs/roadmap.md` Step 0 says what the pilot is meant
  to produce
