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
- `:warning:` **Weakened controls** — each compliance control the release
  makes weaker (gone, a clause or a check dropped, fewer tests), by its id,
  and why ([compliance](docs/compliance/README.md#when-a-change-weakens-a-control))

## v7.6.1 (2026-10-30)

A hotfix: the vLLM engine and NVIDIA's DCGM exporter from images that can be
pulled where NVIDIA's registry refuses, and so carried in the offline bundle.

### :bug: Bugs fixed

- The vllm and dcgm features could not be installed where nvcr.io refuses
  (it answers 403 from many networks), nor carried in an offline bundle:
  their images are now the projects' own on Docker Hub, vLLM's
  `vllm/vllm-openai:v0.31.0` (the version the stack pins, run by the same
  supervisor) and NVIDIA's `nvidia/dcgm-exporter:4.6.1-4.8.4`.
  `VLLM_IMAGE` and `DCGM_EXPORTER_IMAGE` still choose another (NGC's, or
  arena-vllm built by `vllm-build.yml`); the Helm chart's `images.vllm`
  follows

## v7.6.0 (2026-10-30)

The requests of 2026-10-29: company knowledge by upload and from many more
places, Ask your data on more databases and files, a wiki searched with no
model, Agent Studio that reacts to GitLab and acts on it, stronger
safeguards, one command to build and install that works on a fresh machine,
and Code Arena with a code index of its own (find code by what it does or by
name, with fewer tokens than grep) and more of VS Code.

### :rocket: Epics and highlights

- **Code Arena's code index**: an index of the folder's code, made by Code
  Arena's own scanners (C#, TypeScript, JavaScript, Python, Markdown), kept
  in its data folder (never in the repository) and synced before every
  question. `code_search` finds code by what it does or by name and cites
  each place as `path:start-end` with its lines, within a token budget;
  `code_symbol` gives a symbol's source, callers, callees or impact, each use
  tied to what it names and how sure that is; `code_outline` maps a file or a
  folder. Off by default (`"codeIndex": true` turns it on) until it is shown
  to help a model: with no model, the index puts a changed file first for 53%
  of 60 real commits' subjects against 13% for grep's ranking.
  `code-arena index` and `code-arena search` give the same answers in the
  terminal; the IDE's Go to Symbol in Workspace (Ctrl+T, `#` in quick open)
  and its Outline read it too
- **More of VS Code in Code Arena's IDE**: preview, pinned and reordered tabs;
  tabs and unsaved text back after a reload (hot exit); breadcrumbs; the
  Outline; quick open's `#`, `@`, `?` and `>`; Markdown preview; pictures,
  sound and video in tabs; a script's language from its first line; tsc's
  pretty output in Problems
- **Company knowledge from anywhere**: uploads of many document types and
  pictures, read with OCR; GitHub and Gitea, Jira, Notion, WebDAV, S3 and
  feeds as sources
- **Ask your data on more**: MySQL and MariaDB, SQL Server, and uploaded
  CSV, Excel and JSON files
- **A wiki**: the company's documents, code and data, searched and read with
  no model
- **Agent Studio acts**: fifteen GitLab events with filters, and steps that
  run code, query data and knowledge, call HTTP, wait, notify and comment,
  label or open issues in GitLab
- **One command for everything**: `arenactl` builds, packages and installs,
  with every dependency in an offline bundle, and works on a fresh machine

### :sparkles: New features & Enhancements

- Hardware dashboards for one GPU, many or none
- The prompt log shows every request in any state
- Arena tells people who run an old Code Arena
- Safeguards that are harder to get round, and more of them
- The app's own gateway, step by step: the API goes through the app alone,
  Code Studio's workspaces ask models through the app, routes and the chat's
  models come from the app's own tables, and the app's own ledger of model
  requests (LiteLLM stays the default until measured against it)

### :bug: Bugs fixed

- A real GitLab webhook to an Agent Studio workflow was refused (it sent no
  X-Requested-With)
- API completions: a model not ready says so (503 with Retry-After), a copy
  keeps its name, preflights pass
- A tab shown before the IDE's editor area was drawn never got its editor
- The TypeScript scanner said nothing of what holds a symbol, and the C#
  scanner named a field typed with generic arguments after the first of them

### :arrow_up: Deps updates

- MySqlConnector 2.6.2 and Microsoft.Data.SqlClient 7.1.1 (Ask your data on
  MySQL, MariaDB and SQL Server)

## v7.5.0 (2026-10-29)

Arena for many people at once: a fair line when it is busy, a dashboard for
each role, API keys of one's own, a prompt log, feedback on every answer and
checklists the model works through, in the chat and in Code Arena. Code
Arena signs in with a password and is signed by the server's own CA; SGLang
joins llama.cpp and vLLM as an engine chosen at install.

### :rocket: Epics and highlights

- **Fair when it is busy**: requests at once for each person and each group
  (Admin → People and Groups), and for everyone together (Settings → Chat). An
  API key's requests wait in the same fair line as the chat's, and past the
  longest line (`Chat:QueueLength`) a request is told at once to try again
  later. Admin → Line shows who has a request running and who waits, in
  order. API completions now go through the app whenever it is up: before,
  with the answer cache off, the gateway's health check sent them straight to
  LiteLLM, past the line, the keys' own limits and the prompt log
- **A dashboard for each role**: LLM and users, System, and Hardware, each
  with its own permission (`usage.read`, `monitoring.read`,
  `hardware.read`). Admin → Usage by group: each group's requests, tokens,
  cache share and spend, with the members who used it
- **API keys of your own**: Account → API keys makes a key for each tool or
  script (up to 20), named, each with its own requests and tokens a minute
  under the person's, its own requests at once and a monthly budget, changed
  or revoked on its own. Five new keys an hour at most
- **The prompt log**: every prompt and answer, the chat's and the API's
  (answers from the cache too), searchable in Admin → Prompt log by those with
  `prompts.review`, each search audited. Kept as long as Settings → Retention
  says (90 days by default); people's own data export and erasure include it
- **Feedback**: a box on every answer (what was wrong, what was good), and
  Send feedback in the account menu for Arena itself; Admin → Feedback reads
  them and answers, and the answer reaches the person's bell
- **Checklists**: a person's own lists (a definition of done, a release's
  steps), turned on in a chat or a Code Arena session. The model works
  through it with `checklist_check`, ticking each item with what it did; the
  same run shows in both, and `/checklist` turns it on in Code Arena
- **Code Arena, signed**: `scripts/sign-code-arena.sh` makes a code-signing
  certificate from the stack's own CA and signs the builds the server serves;
  `install.sh`, `install.ps1` and `code-arena update` check the signature
  against the server's CA before they install. The installer signs on
  install, upgrade and repair. The signing key never enters a container or a
  bundle
- **Code Arena**: sign in with your user name and password, an Accounts menu
  and the Settings and People buttons; Open Folder, Open Recent and Add Folder
  to Workspace; `@` references to files and folders outside the working
  directory; and its tools on Windows (files held open by others, UTF-16 and
  CRLF files, Git Bash for the shell, `/c/` paths)
- **SGLang beside vLLM**: the `sglang` feature (`installer.sh --with sglang`
  on install, upgrade or repair; `sglang.enabled` on Kubernetes) runs SGLang
  under the same supervisor as vLLM, one server a model from the library's
  Hugging Face snapshots on its own GPUs. Admin → Models chooses the engine;
  the app writes SGLang's own flags, reads its `sglang:*` metrics, and puts
  the chat ahead of API keys inside it as on vLLM. The image is
  `lmsysorg/sglang:v0.5.21-cu130-runtime` (a ROCm one with `rocm.yml`), or
  arena-sglang built from PyPI (`sglang-build.yml`). The RAM llama.cpp seemed
  to leak is its prompt cache, 8 GB a loaded model by default (Settings →
  *RAM for chats that lost their slot*)

### :sparkles: New features & Enhancements

- Admin → Models → On disk: every model file, Hugging Face snapshot and
  picture or video model's file in the library, with the models that use it
  and the servers that keep it, removed with the folders it leaves; removing
  a model can remove its files too
- Arena Apps: publishing again from a chat makes a new version of the app it
  published before, with what changed, instead of a new app
- Code reviews can be shared with groups or people, who read them only while
  Argus can read the repository as them; the people named are told
- Fix them: many security findings fixed at once, each its own fix and
  branch; a security fix's change is checked by the scanners and the review
  before its merge request, and a file the checks cannot read is said
- Code Arena's Problems panel lists the security review's findings, and Fix
  gives the agent the finding
- The scanners' rules cover LDAP, XPath, sessions, cookies, hashes and code
  run from text, and data from beyond requests
- Measurements: the security review on the Juliet test suite (Java and C#)
  and the OWASP Benchmark, and the quantum lab on Qiskit HumanEval
  ([docs/measurements](docs/measurements))
- Generated sound and video say a model made them (GOV-10, the AI Act's
  Article 50), as pictures already did
- The science lab runs an answer's code as written before it ends
- The LLM overview dashboard and two new alerts (`SglangKvCacheFull`,
  `SglangRequestsWaiting`) read SGLang's series

### :bug: Bugs fixed

- A background key sync that read a person's keys before one of their own
  keys was made or changed wrote stale limits over it until the next sync;
  each person's key limits now have one writer at a time
- Code Arena's editor menu had no border or corners on its background
- After a tool call, the chat and Code Arena did not show that they were
  waiting for the model
- Memory's reflection skipped a chat whose tool call was cut short
- A scan's answer was lost when the scanners' tar had padding at its end
- The safeguards took SQL built around a column named password for a
  password
- The dashboards' average wait showed nothing, not 0, before anything had
  waited
- Two CI flakes: the bell's count and list now come from one snapshot, and
  the vLLM test writes its desired state renamed into place

## v7.4.0 (2026-10-28)

Security reviews of your repositories, grounded in what is known of weaknesses
and fixed with Fix it; known vulnerabilities in their packages, renewed every
day; governance, risk and compliance for fourteen frameworks and the
certifications; quantum computing in the science lab; and every part of the
stack chosen at install.

### :rocket: Epics and highlights

- **Security reviews**: Code Studio → Security reads a repository at a branch,
  tag or commit, as you (Argus, read-only). Git grep finds where code calls
  something dangerous: queries, commands, deserialisers, code run from text,
  XML parsers, paths, HTML, fetches and redirects, in C#, Java and Kotlin,
  Python, JavaScript and TypeScript, Go, PHP and Ruby. The model traces the 30
  most dangerous through their callers. A weakness is kept only when it quotes
  the line as the file has it and names a CWE the new `security` knowledge
  pack knows. Each carries that pack's pages: the CWE, OWASP's cheat sheet and
  CodeQL's query help. A second pass, with the CWE's page in hand, tries to
  refute each one. Mark a finding fixed, accept the risk (why, and until
  when), or mark it not a weakness; the repository's next review remembers
- **Known vulnerabilities in packages**: Argus's `deps_check` reads a
  project's lock files and manifests at any commit (npm, yarn, pnpm, NuGet,
  pip, Poetry, uv, Pipenv, Go, Cargo, Maven, Gradle, Bundler, Composer). It
  matches each pinned version against GitHub's reviewed advisories with no
  model, comparing versions as each ecosystem orders them. Each vulnerable
  package comes with how it comes in, its CVEs, whether CISA lists it as
  exploited, and the smallest upgrade that fixes it. `sbom` gives the packages
  as CycloneDX. The `advisories` pack is renewed every day by Argus itself;
  only what changed is embedded again. Reviews of the last 90 days are then
  checked again, so a vulnerability published after a review is found in the
  code it read, without a push
- **Scanners beside the model**: the optional `scanners` feature runs
  opengrep with 94 of Arena's own rules, each tested on its weak and fixed
  examples, plus Bandit, gitleaks (with the history) and Trivy's checks of
  configuration. They run on a network that only Argus reaches, and fetch and
  run nothing they are sent. Their findings join the model's: a match on a
  sink is traced first, and each finding names what found it (the model, the
  scanners, or both). Secrets and misconfigurations have sections of their
  own, and everything downloads as SARIF
- **Fix it on a security finding**: the agent writes a test that shows the
  weakness, then makes the change the references describe (a parameterised
  query, not an escaped string), runs the tests, and opens the merge request
  as you. A vulnerable package is upgraded the smallest step that fixes it.
  A secret is taken out of the code, and you are told to revoke it. Every
  merge request's review, automatic ones too, now says when the change
  brings a vulnerable package or a scanner's finding on its own lines
- **Admin → Security**: every repository at a glance, with what cannot wait
  (the known-exploited, and what is past its time to fix: 7, 30, 90 or 180
  days by severity), what changed this week, the risks accepted, and the
  bills of materials. A new critical or known-exploited finding reaches the
  security team at once
- **GRC and the certifications**: fourteen frameworks in the catalog, each
  clause mapped to Arena's controls: ISO 27001 (clauses 4 to 10 and Annex A),
  ISO 42001, ISO 22301, SOC 2, HIPAA, the GDPR, PCI DSS v4.0.1, NIST CSF 2.0,
  SP 800-53 Rev. 5 with its baselines, SP 800-171 Rev. 3, the AI RMF, the EU
  AI Act, CSA's CCM and the Common Criteria. Admin → Compliance keeps the
  management system, in tabs:
  - a risk register with treatments;
  - ISO 27001's and ISO 42001's Statements of Applicability;
  - policies with owners, versions, reviews and acknowledgements;
  - audits with their findings and corrective actions, certificates and
    waivers;
  - the supplier register and the AI systems register (each system's EU AI
    Act class and impact assessment);
  - management reviews, objectives, and auditors' requests.

  For each framework it says how ready you are, 0 to 100, and Download for
  the auditor gives its package for a period, with the checks' daily history.
  NIST's texts and the AI Act's, which may be shared, are in a `grc`
  knowledge pack the chat cites
- **Quantum computing in the science lab**: a Quantum tool pack and field, on
  simulators in the sandbox:
  - circuits in OpenQASM on Qiskit Aer, with noise;
  - two circuits or a state checked equal;
  - surface and repetition codes sampled with Stim and PyMatching;
  - a qubit's decay with QuTiP;
  - VQE and QAOA with PennyLane;
  - molecules with PySCF and OpenFermion.

  Circuits are drawn, and counts, logical error rates, Bloch vectors and
  energies charted, in the chat and the notebook
- **Only what you need**: every part of the stack is a feature
  (`deploy/features`), turned on or off with `--with` and `--without` on
  install, upgrade and repair, or chosen from a list with each one's disk
  size. A feature that is off runs nothing, loads no image, and leaves the
  menus, the palette and the tour. Its API answers 404, saying how to turn
  it on. `make-installer.sh --without` makes a smaller bundle, and
  `--kubernetes` takes the same choices into the chart

### :sparkles: New features & Enhancements

- The `security` knowledge pack (2,676 pages, 3,023 symbols): MITRE's CWE
  4.20 and CAPEC 3.9, OWASP's Cheat Sheet Series, ASVS 5.0, WSTG and Top 10s
  (web, API, LLM), CodeQL's security query help (not its engine), and .NET's,
  Bandit's, gosec's and eslint-plugin-security's rules. Each id is a symbol
  (`CWE-89`, `V6.2.1`, `cs/sql-injection`, `B608`); each source and CodeQL
  language is a facet. Semgrep's registry rules, the SEI CERT wiki and the
  CIS benchmarks are not taken: their terms do not allow it
- The `advisories` pack (36,053 advisories, 67,682 affected ranges), with a
  table of what each affects that `deps_check` reads. `argus pack renew`
  builds it on a connected machine for offline hosts, and Admin → Packs has
  **Renew**
- Argus's `scan_at` tool: the scanners on any commit as the person, or on a
  change's files alone (`paths`)
- The `/security-review` skill follows the same grounded procedure in a chat
  or Code Arena
- A new compliance control, VUL-01 (ISO 27001 A.8.8, A.8.28, A.8.29; SOC 2
  CC7.1; PCI DSS 6.2, 6.3, 11.3; NIST RA-5, SI-2; the AI Act's Article 15;
  Common Criteria AVA_VAN), with its live check: a review in 30 days,
  advisories within a week, and findings fixed in time. The controls GOV-04
  to GOV-12 cover the GRC records
- Generated and edited pictures carry IPTC's mark of AI in their metadata,
  and the chat says answers are made by AI (the AI Act's Article 50, GOV-10)
- The release SBOM lists the scanners' programs too (Trivy and gitleaks as
  Go modules, opengrep)

### :bug: Bugs fixed

- Postgres ran with Docker's 64 MB of shared memory, and a busy moment's
  parallel query failed with "could not resize shared memory segment". It has
  1 GB now, in compose and in the chart
- Argus kept a key check that the app answered at once beyond its cache, so
  a refused key could stay refused
- Code Arena's `stop_command` answered before the command had ended, so with
  piped input a session could end before the model heard how it ended

## v7.3.0 (2026-10-27)

A science lab, where models work problems of mathematics, physics, chemistry,
statistics and biology with tools that check every step — SymPy, Pint, RDKit,
Biopython and Lean with Mathlib — and keep each problem as a notebook; and
Argus Arena installed on Kubernetes with Helm, for large teams and for
availability.

### :rocket: Epics and highlights

- **The science lab**: Science lab in the sidebar. Choose a science, write the
  problem, press Work it: the model works it with the lab's tools, and the
  problem is kept as a notebook — the model's words, and a cell for each
  computation, check, solution, conversion, molecule, sequence and proof,
  with what the tool gave and its verdict (proved, refuted, holds only
  numerically; a reaction balanced and checked; a proof checked by Lean).
  On top, how many steps were proved and refuted; the lab lists the
  notebooks with the same. A notebook is a chat underneath: ask more, or open
  it as a chat to branch, edit or share it
- **Every answer checked**: an answer in a notebook that no tool checked is
  not left at that — the lab asks the model to check it with its tools and
  correct it, and the notebook says so. Measured on forty MATH-500 problems
  ([lab-math.md](docs/measurements/lab-math.md)): Qwen3-4B called a tool in
  39 of them (none before the check), scored as it does alone (36 / 40) and
  took 41% longer; the lab checks, it does not make a small model cleverer
- **Proofs checked by Lean**: the Proofs (Lean) tool checks a theorem and its
  proof with Lean 4.35 and Mathlib in a sandbox of its own (no network, its
  own user and limits): checked only when Lean accepts it with no sorry,
  rejected with Lean's messages, which the model reads to correct its proof
- **On Kubernetes, with Helm**: `installer.sh install --kubernetes` checks the
  cluster first (tools, rights, storage, ingress, TLS, GPUs, room, metrics)
  and says what each lack needs, makes the secrets in the cluster (never on
  a command line, in a file or the log), installs the chart atomically and
  checks the app answers; external Postgres, air-gapped registries, upgrades
  that keep the release's values. The chart's app, web and gateway roll
  without a gap, spread over nodes, keep serving through drains
  (PodDisruptionBudgets) and grow with their load (autoscaling)

### :sparkles: New features & Enhancements

- Science lab tools, each a pack the lab and any chat can turn on: Science lab
  (math_compute, math_check, math_solve with solutions put back, units_convert
  with a dimension check), Chemistry (chem_molecule from SMILES with its
  properties and a drawing, chem_formula molar masses, chem_balance checked
  atom by atom and charge, chem_similarity), Biology (bio_sequence, bio_align)
  and Proofs (lean_check). Six fields: Mathematics, Physics, Chemistry,
  Statistics, Biology, Any science, each with its own instructions and tools
- The sandbox image gains RDKit 2026.9.1 and Biopython 1.88; a lean profile
  runs Lean 4.35 with Mathlib (an image of about 13 GB, from GitHub's releases
  and Mathlib's cache; LEAN_CACHE_AZURE=1 where cache.mathlib.org is out of
  reach); `scripts/lean-check.py` checks a running one
- Code Arena's IDE: panes moved and rearranged as VS Code's (views between
  the activity bar and the panel, the side bar left or right, the panel
  below or beside the editor); a gear for its settings and the model's
  tools; files changed by hand show in the editor and in Changes; Changes'
  right-click adds to the chat, explains and fixes; a readable right-click
  menu
- The Helm chart: `availability.strictSpread`, `availability.autoscale`,
  `lean.enabled`; an install, its status and an upgrade on a kind cluster in
  CI
- `evals/run_lab_math.py`: MATH-500 problems through the chat, each model
  alone and in a lab notebook, graded with MATH's own answer normalization.
  The maths model tried, Qwen2.5-Math-7B, was not kept: behind Qwen3-4B alone
  (32 / 40) and lost in the lab (a 4,096-token context, no tool calling)

### :bug: Bugs fixed

- The test sandbox wrote its heartbeat in place: a read could see half of it
  and call the sandbox down (a CI flake in meetings and run_code)
- CI's browser suite: a stack with no gateway answers at once; a chat deleted
  mid-answer stops the answer first; the bell's spec follows what the browser
  allows
- Scheduled tasks' form: each way a task runs is its own tab panel
- The Helm chart's app did not start: HSTS's seconds were rendered as
  3.1536e+07, which the app cannot read as a number
- The Helm chart's /.well-known path was a Prefix, which ingress-nginx's
  strict path checking refuses, turning the whole Ingress away
- The Helm chart made the workspaces' volume claim with Code Studio's
  workspaces off: nothing mounted it, it never bound where binding waits for a
  pod, and `helm --wait` waited until the install failed
- A page closed while the app asked whether a module runs logged an
  unhandled error

### :arrow_up: Deps updates

- The sandbox image: RDKit 2026.9.1 and Biopython 1.88 (new)
- The lean image (new): Lean 4.35.0, Mathlib v4.35.0, elan 4.2.4
- DOMPurify 3.4.17, Monaco's copy too (GHSA-cg2g-xh64-v4q7, critical, no
  longer accepted; GHSA-mrv2-3pqr-j5jr, high; three low ones), and Zod 4.6.7
  (GHSA-mrhx-c7xc-m3w5, high): the release scan finds two low advisories left

### What an upgrade does, and what an admin does after

The migration adds the lab notebooks' science (`conversations.LabField`, empty
for every chat there is). Rebuild the sandbox image (RDKit and Biopython). For
proofs checked by Lean, add `lean` to COMPOSE_PROFILES (an image of about 13 GB,
built once; `LEAN_CACHE_AZURE=1` where cache.mathlib.org is out of reach) and
check it with `deploy/scripts/lean-check.py`. On Kubernetes, `installer.sh
requirements --kubernetes` says what a cluster lacks before anything changes.

### :warning: Weakened controls

- None.

## v7.2.0 (2026-10-26)

Planning and code mode, for long work: the chat and Code Arena propose a plan,
the person approves it, each step is grounded in the code, checked and
recorded, and a plan left half done carries on in the next session; and every
tool becomes a Python function a short program calls many times at once, as
the person, so a long task keeps its window for what matters.

### :rocket: Epics and highlights

- **Plans you approve, carried out and proved**: Code Arena's `plan` tool is
  its working state (each step with what done means, its files, what it waits
  for), kept through compactions, in the IDE's Plan view and on Arena's Plans
  board. In plan mode the plan is proposed, read again by a sub-agent with
  fresh eyes, its steps grounded in the code (Argus's change_impact: who uses
  each symbol, in every repository), then reviewed: approve, approve asking,
  change a step, another way, or reject. A step whose check is a command is
  done only once that command passed; a checkpoint before each step lets
  `/plan undo N` put its files back. `/plan resume` (and `code-arena --plan`)
  carries a plan an earlier session left on; `run_steps` runs steps that wait
  for nothing at once, research by sub-agents and changes in git worktrees
  merged back with their checks
- **The chat plans too**: for multi-step work of its own it proposes a plan,
  shown under its answer with Approve, Change a step and Reject; approved, each
  step shows as it is carried out. The Plans page lists plans in progress
  first, from chats and from Code Arena
- **Tools as code** (`run_code`): the model writes a short Python program in
  the sandbox that calls the chat's tools as functions
  (`arena.argus.find_symbol(...)`, found with `arena.tools.search`), loops,
  filters and joins, and only what it prints comes back. Each call runs as the
  person: their rights, the safeguards, the audit; tools that ask first are
  called directly. Code Arena has the same module for its own tools, Arena's,
  Argus's and the person's MCP servers, through a bridge that checks each call
  as a direct one; agents on Arena MCP (Claude Code, Qwen Code...) take the
  tools as code at `https://DOMAIN/mcp/code`: `search_tools` and `run_code` in
  place of every definition
- **Each model's tools, your way**: Admin → Models → **Tools** on each chat
  model: direct, as code, or both. Automatic until chosen: direct for the model
  for small steps and names that say 8B parameters or fewer, as code for the
  rest; the chat and Code Arena follow it. Questions to the person, plans,
  memory, the canvas and the browser are always called directly
- **Measured, and the default follows it**: ten documentation tasks (eight
  task families and two six-lookup tables) through Arena's own chat.
  Qwen3.8-27B passed **10 of 10 as code**, 6 with both and 4 direct: as code it
  looked up every answer (43 lookups from 36 programs), direct it answered
  from memory; it wrote half as many tokens (10k against 19k) and finished in
  two thirds of the time, reading more prompt (235k, 70% from the cache).
  Qwen3-4B passed 1 either way, so small models stay direct
  ([measurements](docs/measurements/tool-modes.md))

### :sparkles: New features & Enhancements

- Connect your tools: a **Tools as code** switch puts `/mcp/code` in the
  setups for Claude Code, Qwen Code and other MCP clients
- `evals/run_tool_modes.py`: the same tasks through Arena's chat with tools
  direct and as code, the answer key taken from the packs first
- What's new in 7.2, the tour after the release

### :bug: Bugs fixed

- A background job's failure no longer stops the app: a model timing out in a
  chat digest restarted it (HttpClient timeouts escaped eleven loops)
- A step added to a plan already kept (the board's "A new step", the chat's
  plan_change) no longer fails, and a plan change the chat's Plans tool
  refused is no longer saved
- Sub-agents no longer get `run_code`, which had no functions to call for them
- A program's call of a tool behind an MCP server (Argus, an admin's server)
  works ("The node already has a parent"), and what a program's calls make
  (pictures, files) and cost comes with its result
- Code Arena's image builds have the `arena` module it embeds

### What an upgrade does, and what an admin does after

The migrations add the plans' checks and review and the models' tool modes
(`model_tool_modes`, empty: every model automatic). Rebuild the sandbox image
(its runner relays a program's calls). Then, under Admin → Models, look at each
chat model's **Tools**: a strong model you trust with programs may go as code.

### :warning: Weakened controls

- None.

## v7.1.0 (2026-10-26)

Compliance, built in and kept: what a company running Arena needs for ISO/IEC
27001, SOC 2, HIPAA and the GDPR — the controls, live checks of each, the
evidence in one download, the documents and templates, and a guard that keeps
every later release from weakening them unseen.

### :rocket: Epics and highlights

- **The Compliance center** (Admin → Compliance, with a new Compliance officer
  role): each of the 27 controls with the clauses it serves in the four
  frameworks, its checks now (green, amber or red, each saying what to change),
  the company's part attested (kept a year, audited), and **Download
  evidence**: the controls, the checks, the attestations, the access reviews,
  the notice's acceptances, the incidents, privileged access, the settings and
  the audit log's chain in one zip. `deploy/scripts/compliance-check.py` checks
  from outside what a browser sees (TLS, headers, cookies, cross-site requests,
  the data's disk encryption) beside the app's own checks
- **An audit log nobody changes unseen**: each entry sealed onto the one
  before with SHA-256, the chain walked and anchored daily (its own volume and
  the logs), a break told to every admin; kept six years; sent on to the
  company's SIEM by syslog (UDP, TCP or TLS) or a signed webhook, each entry
  once and in order
- **People's rights and the law's clocks**: data requests (GDPR 15–22) asked
  from Your account or taken in by an admin, each due in a month, erasure
  that removes everything of theirs and certifies what is kept and why;
  security alerts (failed sign-ins, an admin role given, many exports, a failed
  or missing backup) to every admin; an incident register with the GDPR's 72
  hours and HIPAA's 60 days from finding to closure with lessons
- **Kept in bounds**: two-factor required for admins, everyone or chosen
  groups (company sign-in must then prove its own second factor); access
  reviews every quarter; people's files, sound, pages and words sealed at rest
  with AES-256-GCM; encrypted backups; TLS 1.2+ with forward secrecy and HSTS;
  dates of birth, medical record, NHS, national and Medicare numbers found and
  kept from cloud models; the app's logs written with personal data masked;
  a sign-in notice everyone accepts in its current version
- **The documents**: [docs/compliance](docs/compliance/README.md) with the
  shared responsibility, every control as an auditor reads it, each framework
  clause by clause (ISO 27001's 93, SOC 2's 61 criteria, HIPAA's 68 sections,
  37 GDPR articles) with Arena's controls and the company's document for each,
  the data flows, an incident runbook, a record of processing, a DPIA and
  eleven policy templates; in the manual too

### :sparkles: New features & Enhancements

- Every release has its SBOM (`tools/release-scan.py`: CycloneDX, every .NET,
  npm and Python package and base image, checked against OSV); a critical
  vulnerability fails the release unless accepted in writing until a date
- Settings → Data retention: how long the audit log (six years), the gateway's
  request log (400 days), tool calls and the bell (180 days each) are kept,
  swept hourly and audited; Admin → Compliance lists every kind of data's period
- Settings → Audit log, Compliance and Sign-in notice; a group's policies can
  require two-factor of its members
- `backup.sh --open` and encrypted backups opened by `--verify`, `--restore`
  and `restore-test.sh`
- What's new in 7.1, the tour after the release

### :boom: Breaking changes & Deprecations

- Loki now deletes logs older than 90 days (its compactor had no period, so
  they were kept forever)
- The gateway's request log, tool calls and the bell are swept to their new
  periods on the first hour after the upgrade; set them to 0 first to keep
  everything
- With two-factor required, or a sign-in notice set, people do that first at
  their next step: the app answers nothing else until they have
- Company sign-in, while a second factor is required, refuses a provider that
  does not say one was used (OIDC `amr`/`acr`, SAML's authentication context)

### What an upgrade does, and what an admin does after

The migrations add the compliance tables and seal the audit log's chain from
its oldest entry; a job seals the files stored before; the new `audit` volume
holds the chain's anchors. Then, in Admin → Compliance, work through what is
amber: require two-factor, set the sign-in notice, choose how long chats are
kept, add the two health-data safeguard templates, set `BACKUP_KEY_FILE` for
encrypted backups and `HSTS_SECONDS` once the certificate is one browsers
trust, encrypt the data's disk, and attest the company's part.

### :warning: Weakened controls

- None: this is the first release with the controls.

## v7.0.1 (2026-10-26)

What's new, told once, a tidier place to type, and the sketch and the panel
working again.

### :sparkles: New features & Enhancements

- **A panel at work, in view**: its dialog shows how far it is (the round,
  the turns spoken of all it will have, who speaks), stays open as the chat
  fills, and when closed a bar above the box says how far it is, with
  **Open**; after a reload too (`GET /api/chat/conversations/{id}/panels`)
- **What's new**: after a release, each person is shown its highlights once, a
  page each with where to try it (Memory, the chat, Skills, Code Arena), and
  again from the account menu or the command palette; kept with the person,
  so it opens once on every device

### :bug: Bugs fixed

- The sketch's **Attach it**, and every "make it a page, a diagram…" button,
  did nothing: the picture was taken as missing before the browser had made
  it
- The chat's message box: Send fell onto a line of its own under the tools;
  it stays at the bottom right beside the context gauge, and the tools wrap
  among themselves when the box is narrow (the keyboard hint shows only when
  the box is wide enough for it)
- **Ask a panel**: in a new chat there was no button for it (the chat is made
  for it now, named after the question); and once the verdict came, it pushed
  the dialog's Done button off the screen (what was said and the verdict
  scroll together)
- An answer's row of actions lost the pin and leave-out buttons added in 7.0
  (leaving out a question leaves out its answer, and the context gauge pins
  or leaves out any message)
- Code Arena's chat: the model's name had shrunk to nothing beside the
  thinking level in a narrow chat panel (the level shows as its icon there);
  the editor's empty page no longer ran under the chat when the window is
  narrow

## v7.0.0 (2026-10-25)

Memory, and work that never loses the thread: what Arena and Code Arena
remember, what fills the model's window, chats and sessions that go on for
thousands of messages, skills that learn from your work, and Code Arena
delivered and kept current by your Arena.

### :rocket: Epics and highlights

- **One memory, everywhere**: the chat and Code Arena keep the same memories,
  each with whose it is (yours, a project's, the company's), its kind
  (preference, fact, decision, how to, lesson) and where it came from. Each
  answer reads the pinned ones and those that matter to its question, by
  meaning; memories form themselves from chats that went quiet (Remember
  these?, or on their own); the garden merges what says the same, lets go of
  what was replaced or unused for months and tightens long ones, each change
  with Undo; Memory has History, Markdown in and out, and the IDE a Memory
  view. Code Arena keeps a project's long memory in `.remember/` in the
  repository, read at every session's start and kept with `remember_project`
- **Very long chats and sessions**: before compacting, older tools' results
  are cleared (often that is enough); a big result is kept aside and read
  again in parts; the summary comes in sections, cites message numbers and
  ends with the chat's stretches; pinned messages and the plan come back after
  it. A chat that compacted, or a long Code Arena session, finds and reads its
  own earlier messages by their numbers (`chat_search`, `chat_read`,
  `session_search`, `session_read`); a long chat opens at its end, and an
  answer reads whole only what it sends. Every chat and session gets a digest
  as it goes quiet: `search_history` and `open_chat` find past work by what it
  was about, and a question comes with the past chats it bears on
- **Skills that learn**: 21 skills come with Arena (code review, tests,
  debugging, planning, release notes, meeting notes and more). A chat or a
  session that worked out a way of doing something worth repeating proposes
  a skill; a skill that went wrong or that you corrected proposes its better
  version, applied with a version and Undo; an answer marked bad is read as a
  lesson. Code Arena keeps what it worked out with `skill_save`, reminded after
  a long turn
- **Code Arena, delivered by your Arena**: Connect your tools installs it in
  one line (the build checked against its SHA-256), every deploy builds it
  for five systems, and it checks the Arena for a newer version and updates
  itself (`code-arena update`, or Update in the IDE's status bar)

### :sparkles: New features & Enhancements

- The context gauge shows the memory apart and the biggest messages, each
  pinned or left out from there; any question can be pinned (said word for
  word after every summary) or left out of what the model reads, with its
  answer; Code Arena's `/context` and the IDE's Context show the same
- Settings → Chat: **Digests of past chats**, **Tidy memories**,
  **Arena's own skills**
- `tools/fetch-code-arena-packs.sh` keeps .NET's packs at the SDK's version;
  `make-installer.sh` packs Code Arena's packages of the release, making them
  when there are none
- A new README

### :bug: Bugs fixed

- After a summary, Code Arena's to-do list was not said to the model
- Recall gave a dozen unrelated memories: it now keeps those well above the
  typical candidate's score
- vLLM's preset files could lose a write made at the same moment
- A test server whose port was taken as it started failed instead of trying
  another

## v6.9.0 (2026-10-25)

Everyone, in every language: meetings made into minutes, calls in rooms with
live captions in each reader's language, and Arena on the phone.

### :rocket: Epics and highlights

- **Meetings** (Workspace → Meetings): record one in the browser or upload a
  sound or a video (up to 60 MB, three hours as the browser records). It is
  made in steps shown as they go: speech to text writes down every word with
  its time, the studio's voice model tells the speakers apart, and the chat
  model writes the title, the summary, the decisions and the action items
  (whose each is and by when) and names the speakers from what they called
  each other. The meeting's page plays the recording from any line of the
  transcript, marks the line playing, and finds words in it; a speaker told
  to be a person in Arena has their action items, reminded once on the day
  due. **Make a plan** makes a plan with a step per action item; **Ask about
  it** opens a chat with the minutes and the whole transcript
- **Calls in rooms**: **Call** in a room's header, and its people **Join**.
  Each page talks to every other directly (WebRTC, up to 8; the server passes
  only their signals); what each says is written down as they speak and shown
  to all as live captions, in each reader's language when they read the room
  in one, the original a click away. **Mute** stops the sound and the
  captions
- **Arena on the phone**: installed, it is in the phone's Share sheet (a file,
  a page or words from any app open a new chat with them), a long press on
  its icon offers **New chat**, **Talk** and **Record a meeting**, and a
  message sent with no connection waits above the box and goes by itself
  when the connection is back

### :sparkles: New features & Enhancements

- `Rooms:Calls:IceServers` for browsers on different networks;
  `Rooms:Calls:MostInCall` (8); `Meetings:MaxUploadBytes`,
  `Meetings:MostPerPerson`
- Money is not math in answers and the manual: "$0.05 … $0.006" stays text,
  "$x$" stays a formula
- The tools dialog's kind is a choice (MCP server, API) rather than tabs
  without panels

### :bug: Bugs fixed

- On a busy machine an agent connecting to Arena MCP could be told a
  plugin "failed to start", or miss Argus's tools: Argus read the database on
  the request's context beside a plugin's sign-in. It reads with its own now
- The background engines (answers, reviews, the business map, meetings) stop
  with the host's own stopping token: the test host could dispose them first
- On a phone, a room's header lay over the Thinking picker and the
  composer's Send button left the box; at 320 px, tables in a card, tabs and
  the webhook card's buttons widened the page

## v6.8.0 (2026-10-23)

Code Studio, and Argus the business's technical mind: an IDE like VS Code in
the browser, on the server or on your own machine, with review inside it, and
an Argus that explains the business, not only the code.

### :rocket: Epics and highlights

- **Code Studio** (Workspace → Code Studio, `/code`): Code Arena's IDE in
  the browser, the whole window — editor, explorer, search, source control,
  terminals, tasks, run and debug, language servers, extensions, the agent's
  chat — on a **workspace on the server** (a GitLab project and branch,
  cloned and pushed with your own token, its engine run as its own user with
  limits and no Docker socket, asleep when unused) or on **your own machine**
  (`code-arena web --connect` in a folder: it shows in Code Studio while it
  runs, reached through a tunnel it dials with your API key; only you reach
  it)
- **Review in the IDE**: a Review view reviews the changes not yet committed,
  or the branch over a base, with a reviewer that reads the code and changes
  nothing; findings worst first, marked in the editor and in Problems, each
  with why and the fix — **Apply fix** makes a fix that fits the file in the
  editor, **Ask the agent** hands one to the chat
- **The business map** (Code Studio → Business map): a project mapped into
  what its software does for the business — its domain, what it lets the
  business do, its processes step by step, the rules it enforces and why,
  what it deals in, whom it talks to, a glossary — read by a model with
  Argus's read-only tools from the code, the documents, GitLab's issues and
  wiki; every item says where it was read and opens there
- **Argus explains the business**: in any chat with Argus on, it answers as
  the company's senior technical expert, from the map first
  (`business_map`, `business_explain`), confirmed in the code, the issues and
  the wiki, in business words first, with its sources
- **Skills**, as Claude Code's: the prompt library becomes skills — a slash
  name, when to use it, instructions with parameters. `/name` puts the skill
  above the box with a field for each parameter and sends it as a skill (the
  chat shows the chip, not pasted text); the model also uses one on its own
  when a question fits (`use_skill`). A plugin's skill folders (`SKILL.md`)
  are slash commands and Code Arena skills alike

### :sparkles: New features & Enhancements

- Argus reads GitLab's issues and wikis: `issue_list`, `issue_get`,
  `wiki_list`, `wiki_get` (34 tools), read-only, checked per person
- Workspaces reach Argus with the person's own key; Code Arena served for a
  workspace needs no page of its own
- Queued messages and edited questions keep the skill they used; Arena MCP
  serves skills as prompts
- A machine's tunnel held by one app replica is reached by the others at its
  own address, with the tunnel's key
- The help panel and the manual cover the workspace, the business map and
  skills

### :bug: Bugs fixed

- The workspaces service took Arena's internal address (`http://app:8080`)
  and a company's GitLab on http; it keeps what an engine says and tells it
  when the engine does not start
- The workspaces image got its supervisor past `.dockerignore`
- A model was registered twice at the gateway when two syncs ran at once
  (the watcher's pass and an admin's save), doubling what it was sent at
  once: one sync at a time now, and a copy registered twice is taken away

### :boom: Breaking changes & Deprecations

- Prompts are skills: `/api/prompts` is `/api/skills`, Workspace → Prompts
  is Workspace → Skills, the `feature.prompts` permission is
  `feature.skills` (roles and rules move with the upgrade; every saved
  prompt is kept as a skill)
- A plugin's manifest has no `prompts:` key: its skill folders (`skills:`,
  each a `SKILL.md` with `title` and `parameters` in its front matter) are
  its slash commands (`gitlab-issues` 1.2.0)

## v6.7.0 (2026-10-22)

Vector and motion: SVG you would ship, made of named parts, that moves as
you say.

### :rocket: Epics and highlights

- **Vector drawings** (Media studio → Vector): an **illustration** from a few
  words (the picture model draws it flat; it is traced into clean shapes,
  cut out side by side with no seams), SVG **drawn by the model** in a
  style (icons, diagrams, simple shapes; what it got wrong goes back to it
  to fix), an **SVG of your own** (made safe, and told what went), or any
  picture of yours traced
- **Named parts**: a traced drawing comes as its things (a fox, the moon,
  stars), not a heap of shapes: the chat model names the things in the
  words, CLIPSeg finds each in the picture, and the shapes are grouped into
  them; many of a kind become pieces that move one after another
- **Made and animated in one go**: describe it and how it moves ("a laptop
  sends a file to a server, which lights up"); the words are split into the
  picture to draw and the motion, and the drawing comes out moving (gently
  alive when the words say nothing; Moving or Still)
- **Directed animation**: the planner tells the request as a little film in
  beats on one cycle (anticipation, the move, follow-through, secondary
  motion under it), from 36 motions compiled into the SVG as CSS — fade,
  slide, pop, spin, float, drift, sway, breathe, twinkle, glow, draw the
  lines on, travel and arc between parts, jump and land with squash and
  stretch, flip, a camera that goes close, a trail of ghost copies, dashes
  that flow along an arrow, keyframes of your own; or pick a part on the
  stage and give it a motion by hand, with a timeline (play, pause, scrub,
  loop). The file plays anywhere
- **Diagrams**: how something works, crisp and clean — the model lays out
  its things (Lucide's icons on tiles, labels, arrows), the app draws them
  alike, each a part that moves on its own
- **Out of the studio**: PNG up to 4,096 pixels, a looping GIF, an MP4, a
  WebM that keeps its see-through background, or a sprite sheet, recorded
  by the stack's own Chromium; and in a chat, the **Vector drawings** tool
  draws and animates SVG (`draw_svg`, `animate_svg`), shown as it plays

### :sparkles: New features & Enhancements

- A picture server already running its model gets GPU room when a chat
  model loaded since left it too little (the picture failed before)
- The model that draws SVG (Settings → Chat) plans the drawings and their
  motions; a larger one plans far better (Qwen3.8-27B here)
- A drawing's code changed by hand shows on the stage as it is typed; every
  change is a version (the last 50), and any can be put back
- Drawings are served so that, even opened on their own, they run nothing
  and reach nothing (a sandboxing content policy), in the studio and in
  chats
- Rooms read in one's own language (twenty languages; the original a click
  away), translated faithfully by the model an admin names
- The studio server tells who spoke each part of a recording
  (`/v1/speakers`), the groundwork of meetings

### :bug: Bugs fixed

- Toasts' close buttons are 24 pixels (WCAG 2.2 target size)

### :arrow_up: Deps updates

- Sandbox: vtracer 0.6.15 and resvg-py 0.5.0
- Studio: CLIPSeg (CIDAS/clipseg-rd64-refined, 603 MB, Apache-2.0), fetched
  with the studio's default models
- Lucide's icons (lucide-static 1.61.0, ISC) for diagrams, in the app

## v6.6.0 (2026-10-22)

The media studio: pictures you change by painting, narrated videos from a few
words, voices and music, and 3D you can print.

### :rocket: Epics and highlights

- **Media studio** (Workspace → Media studio): pictures from a few words (a
  shape, one to four), then any of them, or one you upload, **changed** by
  saying how: all of it, or only the part you paint over (the rest stays
  exactly as it was, its edge blended softly); **variations** a little to a
  lot different; **enlarged** four times (Real-ESRGAN); **cut out** of its
  background; or **set moving** (a short clip from the video model that
  starts from it). Each is a job in the background, in its turn on the GPU,
  shown where it will land in the gallery and said in the bell when long;
  every result keeps where it came from
- **Videos from a few words**: say what a video is about and the model
  writes a storyboard (shots, each what is seen and what the narrator
  says). Change any of it, then **Make the video**: each shot drawn and set
  moving, the narration spoken (your voice settings, or a voice from a
  sample you may use), music made for its length, and all cut into one MP4
  with captions and the music ducked under the voice. A changed shot is
  made again alone
- **Speech and music**: words spoken in the voice model's own voice or in a
  voice from a 10 to 30 second sample (only with your word that you may use
  it; watermarked as made by a model), and music from a description and
  lyrics, 15 seconds to 2 minutes
- **3D you can print**: ask the 3D tool to print a model and it comes back as
  STL and 3MF too (millimetres, on the plate, its real size when it fits,
  each part with its colour), with its printed size and whether every part
  is closed; ask for a turntable and a video of it turning once round plays
  in the chat

### :sparkles: New features & Enhancements

- The **studio** module (`COMPOSE_PROFILES=studio`, `studio.enabled` in the
  chart): BiRefNet lite, Chatterbox and ACE-Step on one GPU service on an
  internal network, one model at a time, unloaded when unused; its work
  borrows GPU room as pictures do (idle chat models unloaded, given back
  after)
- Show your screen with a model that cannot see: the bar offers a model
  that can and switches to it in one click
- The bell, readable: news in sections, repeats folded, plain words; a
  workflow run that went well says nothing
- A llama.cpp model that does not fit beside the loaded ones gets room from
  idle ones first, and a model that fails to load says why in words
- A price for a minute of music (Settings → Prices); pictures, clips, speech
  and music the studio makes are booked in the gateway's log as the chat's
- Storage shows the studio's files as **In the media studio**, and music as
  its own kind

### :arrow_up: Deps updates

- Fetched into the model library with the default models: Real-ESRGAN x4plus
  (67 MB), BiRefNet lite (178 MB), Chatterbox's English model (3.2 GB),
  ACE-Step v1 3.5B (8.3 GB), all from Hugging Face
- The studio image: PyTorch 2.11 (CUDA 13), chatterbox-tts 0.1.7, ACE-Step
  (GitHub, commit 1bee4c9)

## v6.5.0 (2026-10-22)

Agents that do the work: workflows that run as you, an agent that fixes the
code and opens the merge request, and a panel that argues it out.

### :rocket: Epics and highlights

- **Agent Studio** (Workspace → Agent Studio): workflows on a canvas that run
  as you. A trigger starts them: by hand, a schedule, a webhook, GitLab's
  events, a word in a room, or a form you share. Steps ask a model (JSON
  too), run an agent with tools, call a tool, branch (**If**), loop (**For
  each**) and wait for your yes (**Ask me first**). What comes out goes to a
  room, an email, a GitLab comment or a file. Every run is kept and replayed
  step by step on the canvas; after a yes it goes on without running again
  what ran. Templates (a morning digest, a review of each merge request, a
  room's triage, a request form, one by one), and a scheduled task made a
  workflow
- **Fix it** (Code → Fix it, and on every review finding): an agent takes the
  branch, makes the change for a finding, an issue, a failed pipeline or your
  words, runs the tests it can, and shows the diff and each step it took;
  **Another round** for more; then it opens the merge request as you, with
  your own GitLab token (scope api, kept encrypted: never the app's or
  Argus's)
- **Ask a panel** (in the chat): the expert, the critic, the one who says no
  and your assistants discuss a question in turns, round by round, watched
  live; a moderator writes the verdict with who said what, into the chat or
  room

### :sparkles: New features & Enhancements

- An app's `arena.ask` and Agent Studio's steps may name a model the person
  may use
- The `feature.studio` and `feature.fixit` permissions
- Browser tests for Agent Studio against the real model

## v6.4.0 (2026-10-22)

Apps: what the chat builds, people use; the company's data asked in words;
decks from a few words.

### :rocket: Epics and highlights

- **Decks** (Workspace → Decks, and the **Decks** chat tool): "make a deck
  about…" and the model writes the slides (title, sections, points, two
  columns, charts, pictures, quotes) with speaker notes. Charts get their
  numbers from your words or from the company's data (never made up: a
  chart whose numbers no one gave loses them and says why), pictures come
  from the picture model one by one. Change it by asking ("a slide on costs
  after the third", "make this slide shorter") or by hand; every version is
  kept. **Present** it full screen, print it to **PDF**, or download it as
  **PowerPoint**, built in the company's template (its layouts, theme and
  logo; PowerPoint's own charts; speaker notes). Admins upload templates
- **Ask your data** (Workspace → Data): admins connect PostgreSQL databases
  (the tables people may ask, for everyone or some groups); a question in
  words becomes SQL, shown and explained, run read only, and answered with
  a chart and the rows. Change the SQL and run it, ask a follow-up, and pin
  charts to **boards** that refresh hourly or daily as their owner, are
  shared like apps, and post a chart to a room. Every query is kept for
  admins. In a chat, the **Company data** tool does the same
- **Arena Apps** (Workspace → Apps): a page or a component the chat built,
  published from its preview at its own link, for you, some groups or
  everyone; a gallery, versions, and **Remix** to make one your own. An app
  asks the model and keeps its data (`window.arena`), as the person using
  it: their model, credit and safeguards

### :sparkles: New features & Enhancements

- Read only, for real: each data query is one statement (the server refuses
  more), in a read-only transaction with a time limit, after its plan shows
  it reads only the tables opened; a superuser connection is refused, and a
  role that may write is warned of
- Administration → Data sources: connect and try a database, choose its
  tables, tell the model what to know about the data, limits on rows and
  seconds, and **What was asked** (who, the question, the SQL)
- Decks: a slide's own picture can be uploaded; a chart can be asked of a
  database by hand; **Ask the numbers again** refreshes a deck's charts
- Storage: a picture on a deck is **On a deck**, kept by the clean-ups
- The `feature.apps`, `feature.data` and `feature.decks` permissions, and
  `data.manage` for data sources
- Browser tests for apps, data (with `E2E_DATA_CONNECTION`) and decks; the
  service worker refused over the stack's own certificate is no console
  error

## v6.3.0 (2026-10-21)

"Show me": the model sees what you see, builds what you draw, and works a
browser while you watch.

### :rocket: Epics and highlights

- **The browser agent**: with **Browser** on in a chat's Tools, the model
  drives a real browser (Chromium, in its own container) to do a task on
  websites: it opens pages, clicks, types, scrolls and reads, each step
  shown as a picture of the screen (and given to a model that sees). The
  chat's browser is live above the box; **Take over** hands it to you (the
  agent waits) and **Hand it back** lets it go on. Anything that sends or
  changes something on a site asks you first; it never types a password or
  a card number. It reaches only public sites on the admin's lists, through
  the app, never your network; each chat has its own cookies
- **Napkin to app**: **Sketch** opens a whiteboard in the chat (on a photo
  or the shared screen too); one click makes the drawing a running page, a
  React component, a diagram, a 3D scene or a table, and a page opens
  running when the answer is done
- **Show your screen**: share a window, a tab or the screen, and each
  question (typed or said in Talk) takes a picture of it for a model that
  sees; nothing else is recorded

### :sparkles: New features & Enhancements

- A tool's pictures can go to a model that sees, after the step's results
  (the browser's screen)
- Settings → Tools: **Sites the browser agent may open** and **Sites the
  browser agent never opens**; the `feature.browser` permission
- The `browser` compose profile (`COMPOSE_PROFILES=browser`)

## v6.2.0 (2026-10-21)

### :rocket: Epics and highlights

- **Automatic reviews** (Code → Automatic reviews): a project's merge
  requests reviewed as they open and get new commits, from GitLab's webhook
  to the rule's own address and a look at the open ones every few minutes
  (only into the branches named, drafts when ready, only what changed since
  the last review); a branch reviewed against another on a schedule, skipped
  when it has not moved; each review posted in a room if the rule has one.
  Read with the rule's person's access, at their cost, at most so many a day
- **GitHub and Gitea pull requests**, reviewed as merge requests are:
  `github:owner/repo` and `gitea:owner/repo`, read through Argus with each
  person's own read-only token, linked once in Account → Code hosts (checked,
  kept encrypted, never shown again): their rights there are all that is read
- **Plugins that bring more, and run their own server**: a group's own
  values for a plugin's settings (another key, another address); scheduled
  tasks people add to their own; Code Arena skills kept with theirs; and a
  plugin's own MCP server, in Python, run in the sandbox with no network,
  with a helper for authors

### :sparkles: New features & Enhancements

- Rooms: invite links (members or viewers, how long, for how many; stopped
  at any time) and the room's export as Markdown or JSON with who wrote each
  message
- Access → Many people: roles, groups, features and accounts changed for
  many people at once, with a dry run that says person by person what would
  change, then made as one change in the history
- Argus: `argus graph-eval` measures the repository links against what their
  owners know (precision by tier, layer and kind, recall, calibration, what
  is wrong and missed) and writes calibrated priors the next rebuild uses;
  each repository has a line saying what it is for (README, manifest or
  code; or an admin's words), in the overview and the system map
- `deploy/scripts/people-load.py`: 50, 100 or 300 people signing in,
  chatting and reading answers, with p50/p95/p99 per step and every
  failure; results in testing.md

### :bug: Bugs fixed

- An office of 300 behind one address signing in within a minute was
  refused 180 times: the sign-in flood guard is now 600 a minute per address
  and a setting (`Auth__SignInsPerMinute`)

### :boom: Breaking changes & Deprecations

- The Argus client's sessions are opened for a person by id and email
  (their linked code host tokens go with them)

## v6.1.0 (2026-10-21)

### :rocket: Epics and highlights

- **Safeguard rules** (Admin → Safeguards): an ordered list of rules, each
  a detector (secrets, personal data, words and patterns, topics the model
  judges, prompt injection, links, files), where it looks (questions, files,
  answers as they stream, what goes to tools and what comes back, API keys'
  requests), for everyone or some groups and people, with some models or all
  (`@external`: every model on another provider), and an action: block,
  mask, warn (the person confirms), ask for a reason (recorded), log, send to
  another model, or allow. Monitor mode for every rule, a test bench, what
  the rules did day by day, and reviews: a refused person asks, an admin
  allows (the same message passes for a day) or keeps it refused. Templates
  to start from; the Safety officer role. The old switches and the groups'
  safeguard settings become rules
- **Tools as the admin makes them**: each tool's name and words, each
  function on or off and asking first, fixed and default arguments, calls a
  day per person, settings per group, and its use this week; a server's or
  plugin's tool that keeps failing is turned off and the admins hear.
  **Toolsets** for chats and for a group's new chats. **Tools without
  code**: an HTTP request to an address the admin fixes, or a prompt on a
  model, tried before they are offered
- **Plugins that bring more**: assistants, safeguard rules (in monitor
  mode) and toolsets besides tools; what a plugin asks for is granted at
  install (and at an update that asks for more); every version is kept to go
  back to; a version can be pinned; each plugin's health; a starter plugin
  and a check for authors; an update from a zip

### :sparkles: New features & Enhancements

- More secrets found: Google API keys, Stripe keys, Hugging Face and npm
  tokens, JSON web tokens, Azure storage keys; personal data now covers IP
  addresses and social security numbers
- The chat asks the person when a rule warns or wants a reason, and a
  refused message has **Ask for a review**; an answer a rule masks or
  withholds changes on screen as it streams

### :boom: Breaking changes & Deprecations

- Settings → Safeguards no longer has the secret, personal data, blocked
  words and model check switches, and groups no longer have safeguard
  settings: they are rules under Admin → Safeguards (converted on upgrade)
- Installing a plugin that brings more than tools needs the admin's yes
  (`accept` in the API)

## v6.0.1 (2026-10-21)

### :rocket: Epics and highlights

- **3D models that work for any model**: the 3D tool builds a model from
  parts the model describes (`make_3d_parts`), so even a small model gets a
  house, a room or a person right instead of writing Blender code that
  fails. People are one part (`figure`): a skinned, smoothed body with a
  face, hands, hair and clothes, in poses (standing, walking, waving,
  sitting...), any height, build and body shape; trees too (round, pine,
  palm, bush); and organic shapes (capsule, lathe, extrude, tube, 3D text,
  smoothing)
- **Textures, shaders and lights**: patterns Blender draws (brick, tiles,
  shingles, wood, stone, concrete, grass, marble, brushed metal...) at their
  real size; textures made as images first (`make_texture`) from a pattern,
  from a description the picture model draws, or from a photo, with
  roughness and normal maps; glass, metal, ceramic and glowing shaders;
  lights as parts (point, spot, area, sun) and lighting for the pictures
  (day, studio, night)
- **A 3D viewer in the chat**: a `.glb` opens in the answer, turned with
  the mouse, a finger or the arrow keys and zoomed; a model with lights can
  be seen by its own lights. The `.glb` carries its textures (patterns baked
  into images), glass, glow and lights

### :bug: Bugs fixed

- The 3D tool's pictures were skipped when the chat had a picture of its
  own; a model's pictures are framed to fit it
- The model no longer pastes Blender code into its answer when a script
  fails

## v6.0.0 (2026-10-21)

### :rocket: Epics and highlights

- **Any hardware, any engine**: vLLM beside llama.cpp behind one engine
  interface, and servers the app does not run (SGLang, Ollama, a cloud);
  placement for any machine, from CPU only to eight GPUs of any vendor;
  several copies of a model with fallbacks; vLLM's KV cache in RAM, on disk
  or in a shared store (LMCache); media jobs borrow only their own GPU while
  vLLM models sleep. Admin → Models adds vLLM models. The `arena-vllm` image
  builds where vLLM's own cannot be pulled
- **Durable at scale**: API keys wait their turn behind the chat, at the
  gateway and inside vLLM; the gateway runs as several replicas sharing their
  counts in Valkey (`ha.yml`), a deleted key stopping on every replica within
  ten seconds; Postgres can be put back to any second with pgBackRest
  (`pitr.yml`), with a restore drill
- **Operational roles**: Team manager (a group's people, credit, limits,
  models and tools, its reports), Supervisor (monitoring, read-only) and
  Operator (alerts, models, backups); people ask for a role on their account
  page and access managers answer
- **Access in detail**: every broad permission has its parts (people's
  passwords, keys and limits; each settings group; models' downloads,
  servers and hours; plugins' install and configure; storage cleanup...),
  each route asks for its own, and a broad one still holds its parts. More
  features can be taken: Python, canvas, 3D, plans, research, compare,
  uploads, assistants, prompts, export, knowledge, choosing a model
- **Rooms**: reply to a message and @mention people and the model; the
  model answers only `@Arena` or a reply to one of its answers (the reply is
  the prompt, with the answer it replies to); people mentioned or replied to
  hear it in their bell

### :sparkles: New features & Enhancements

- **3D models**: the chat's 3D tool has the model write a Blender script and
  runs it with Blender 4.5 LTS headless in a sandbox of its own (no network,
  8 GB, 4 CPUs, 4 minutes): `model.glb` and two pictures come back
  (`COMPOSE_PROFILES=3d`)
- **Plans**: the chat's Plans tool makes plans with steps, owners, dates and
  dependencies and keeps them up to date; the Plans page shows a board and a
  timeline, and a room's plans are its people's
- **Code review as a tool**: a chat, a coding agent through Arena MCP or
  Code Arena starts a review of a merge request or branch and reads its
  verdict and findings; Code Arena's `/review [BASE]` reviews local changes
- **Code Arena**: keeps room for answers as long as its longest and carries
  on an answer cut at the output limit, compacting first when needed; takes
  screenshots for the model (a page through a headless browser, or the
  screen); runs the person's own coding agents (Claude Code, Codex, Gemini
  CLI, on their own subscriptions) with `/agent` and `ask_agent`; cloud
  providers by the company's key (Admin → Models → servers has presets for
  OpenAI, Anthropic, Gemini, OpenRouter, DeepSeek, Mistral, Groq, Together,
  xAI)
- **Argus links repositories by what they talk to while they run**:
  services in compose and Kubernetes files, Kafka topics, databases, service
  URLs, OpenAPI titles, and what owners declare in Backstage and Pact
- **Alerts for vLLM engines and their GPUs**; load-test scripts for 50, 100
  and 300 people; a bring-up check and runbook for a multi-GPU server

### :arrow_up: Deps updates

- System.Security.Cryptography.Pkcs and Xml 10.0.13 (past the versions
  NuGet's audit flags)
- Blender 4.5.14 LTS (the 3D sandbox), Valkey 8.1, pgBackRest (in the
  Postgres image), vLLM on python:3.13-slim

## v5.9.0 (2026-10-20)

### :rocket: Epics and highlights

- **Access control per person and group**: a fixed list of permissions,
  built-in roles (Owner, People admin, Model admin, Billing, Auditor, Argus
  admin, Room moderator, Review admin, Member) and roles of your own, held by
  people, groups (directory and SCIM groups too) or everyone, maybe until a
  date; allows and denies on models, tools, knowledge sources, repositories
  and features, the most specific winning; one check behind every route,
  Arena MCP, the gateway's key lists and Argus. **Admin → Access** has the
  roles, rules, a "may this person…?" checker and the history. People see
  the admin pages their roles give (a Supervisor with `monitoring.read` sees
  the dashboards and alerts), and a role given over one group reaches only
  its people (team managers)
- **Rooms**: chats several people share. Every message says who wrote it;
  whoever asks pays, under their own rules; messages can be for the people
  only, or the model answers only when @Arena is named; questions wait in
  the room's line; who is here and who is typing show live; no one's memory,
  code or personal tools in a room's answers
- **Code review**: merge requests (or a branch over another) reviewed by the
  model with your own read-only GitLab access through Argus; findings on
  lines, each put to a second pass that tries to refute it; a walkthrough,
  risk and verdict; resolve or dismiss findings, review again after new
  commits, discuss it in a room. The **Code** page also asks the code
  assistant about a project. Nothing is posted to GitLab

### :sparkles: New features & Enhancements

- **Conversations' caches on NVMe**: when a conversation's (or an API
  agent's) engine slot goes to another, its cache is written to
  `KV_CACHE_DIR` and read back when it returns, across restarts (200 GB, kept
  for ever by default: Settings → Engine)
- **API keys' parallel requests wait their turn** (four at once per key by
  default, then a line of up to ten minutes) instead of being refused, so
  agents no longer look stuck
- **Feature permissions hold**: taking chat, Code Arena, the web, pictures,
  video, speech, memory, tasks or sharing from someone takes those tools,
  routes and models (from their keys too)
- **Argus follows the app's rules** on repositories (`gitlab:group/*`) and
  `code.read`, under GitLab's own rights
- **Code Arena's chat** has the web chat's model picker, Tools menu,
  attachments (pictures for a model that sees), answer again (shorter,
  longer, another model) and editing a question
- **The installer** checks every prerequisite and puts right what it can
  (`installer.sh requirements --fix`), and asks where data and models live:
  the stack's data is in folders of `DATA_DIR`, not Docker volumes
- **The GPU dashboard** shows clocks, throttling, PCIe, fans and ECC, and
  DCGM's detail where it runs (the `dcgm` profile)

### :boom: Breaking changes & Deprecations

- Admin roles replace the one admin flag; today's admins became Owners and
  the "Who may use it" choices became rules, as they were
- The stack's volumes move into `DATA_DIR` on upgrade (the installer does it,
  with a backup first)

## v5.8.0 (2026-10-16)

### :rocket: Epics and highlights

- **Code Arena stays responsive in long sessions**: a compaction that fails
  falls back to leaving older messages out instead of ending the turn; a
  request the model's window refuses is compacted and asked again once; the
  prompt's start stays the same between turns so the engine's cache is
  reused; a long wait says about how long; tool calls have a deadline; the
  IDE's chat draws once per frame and only the last turns
- **Code Arena and the web chat stay in step by message ids**: web edits,
  answers written again, other branches, /rewind and compactions follow both
  ways; thinking, tokens, times, models and attachments come through; while
  Code Arena answers the web chat queues the person's message; the IDE shows
  where the sync stands
- **Picture and video models get room on the GPU**: a video (and a picture
  when short of memory) unloads idle chat models, runs on the GPU and loads
  them back (a 1 s video: 92 s, was 510 s on the CPU)

### :sparkles: New features & Enhancements

- **Reply to part of an answer**: select it (or Ctrl+Shift+') and Reply, Ask
  about this or Copy; the quote rides above the question, the model reads it
  first, and it syncs between Code Arena and the web chat
- **When to compact is each person's choice** (Your account → Answers: the
  company's default, a share of the context, or only when a message would
  not fit), in the web chat and in Code Arena (which follows the account
  unless its config or a flag says otherwise); both compact before a request
  would not fit, and on the model's refusal compact and ask once more
- **One dialog adds and edits every kind of model** (Admin → Models): the
  file chosen says whether it is a chat, picture, video or speech model
- Docker Compose, Podman and Kubernetes deploy the same stack, and a test
  keeps them so (Argus on Kubernetes checks people's keys again)
- Pictures, video and speech Arena's tools make reach Code Arena (saved under
  .code-arena/media and shown in its chat)
- A person with no picture, video or speech credit is told an admin sets it,
  and Arena MCP does not offer those tools to them; an API key's picture
  request is refused before its model loads
- The design for a multi-GPU server (docs/multi-gpu-server.md)
- **Code Arena's tools menu and tools on demand**, as in the web chat: Arena's
  tools on or off per session (the IDE's Tools menu, /tools on|off), and
  past 6,000 characters of definitions only those the model loads go whole
  (about 8,700 tokens fewer in every request with Arena's tools)
- **Answer again and Edit in Code Arena's chat**, as in the web chat: the
  same, shorter, longer, another model or thinking, or with the files as
  they were before the turn; the earlier turns stay in Arena on a branch
- **Files in Code Arena's chat**, as in the web chat: clip, paste or drop;
  Arena reads documents' text for the model, a model that sees gets the
  pictures, and the chat on the web shows them as the question's attachments
- Code Arena's IDE reaches the person's files in Arena through code-arena,
  which adds the key (the page never holds it); Arena takes the key on those
  routes alone
- Code Arena's model picker says what the web chat's does: which models are
  loaded or load when asked, which see pictures, the deployment's thinking
  levels and its default (which "default" thinking now sends)

### :bug: Bugs fixed

- The speech server's voice detector is unloaded when idle; a speech model
  an admin loaded keeps saying so
- Builds go on when NuGet's audit learns of a vulnerability later
- Arena MCP no longer offers the canvas, which could not work there (its document is
  kept with a chat, and an agent's call has none)

### :boom: Breaking changes & Deprecations

- Each conversation on a llama.cpp model gets the window one of its slots
  holds (context ÷ answers at once, 65,536 for the default model here):
  long sessions are no longer evicted by others
- The engine's key goes by the environment (LLAMA_API_KEY), no longer on its
  command line
- `Chat:AutoCompactPercent` is now `Chat:CompactAt`, a share of the whole
  context (20-95), 0 for only when needed; a saved value is moved
- Code Arena and Arena sync by message ids: older Code Arena builds cannot
  sync with this Arena

## v5.7.0 (2026-10-16)

### :rocket: Epics and highlights

- **Code Arena's IDE works as VS Code does**: its commands under VS Code's
  ids in the palette (by category, the ones run lately first), VS Code's keys
  (chords too) with the person's keybindings.json over them, the person's
  settings.json and the project's .vscode/settings.json (a language's own
  too) setting the editor, compares, terminals, the theme and saving, and
  VS Code's Settings (Ctrl+,) and Keyboard Shortcuts (Ctrl+K Ctrl+S) editors;
  VS Code's own settings.json can be brought over
- **Run and debug**: Run and Debug lists launch.json's configurations and the
  project's own (.NET, npm, Python, Go, Cargo, CMake, Make), starts one with
  its debugger (F5) or without (Ctrl+F5), Debug or Release; the debugger
  speaks the Debug Adapter Protocol to gdb, lldb-dap, netcoredbg, debugpy,
  Delve or js-debug (whichever is installed): breakpoints (conditions,
  logpoints, F9), the call stack, variables (changed by a double-click),
  watches, values at the lines' ends, a hover's value, the Debug Console, Run
  to Cursor, stepping, restart and stop. Tasks (tasks.json's and the
  project's) run from the palette and Ctrl+Shift+B, their problem matchers
  feeding Problems
- **Language servers and VS Code extensions**: the editor speaks the
  Language Server Protocol (completion, hover, definition, references,
  rename, code actions, formatting, diagnostics) to servers found on PATH or
  named in config.json, in a trusted folder and sandboxed; VS Code extensions
  bring colour and file icon themes, snippets, languages, keybindings and
  setting defaults, from a .vsix or VS Code's own on this computer
- **Code Arena opens in a desktop window of its own** (Chrome, Edge,
  Chromium or Brave in app mode, no tabs or address bar; closing it stops
  code-arena); `--browser` or `"window": false` in config.json for a tab

### :sparkles: New features & Enhancements

- launch.json compounds (their preLaunchTask, then every configuration;
  stopAll), attaching to a process (`${command:pickProcess}`), a program's
  own terminal (runInTerminal), the configuration's internalConsoleOptions
- Code Arena says whose chats in Arena its sessions are kept with (login,
  /sync, the IDE)

- **Compare as VS Code does**: a side may be the file itself (typing there
  edits it, Ctrl+S saves it), the file as saved (Ctrl+K D), the clipboard
  (Ctrl+K C), another file, two files of the Explorer, git's index, HEAD or
  any commit; the bar has the counts, the previous and next change, inline or
  side by side, whitespace, folding and swapping; Source Control compares the
  index, HEAD and a commit's files, shows a file's history and compares with
  any revision; the change at the cursor is staged, unstaged, reverted or (the
  agent's) accepted alone; the editor's gutter shows the lines changed since
  git's index (Alt+F3)
- **Formatting with the project's own tools**: Prettier from the project's
  node_modules (else installed), ruff or black, gofmt, rustfmt,
  clang-format, dotnet format and the person's own from config.json, run by
  code-arena in a sandbox; Format Document, Format Selection, Format Document
  With…, format on save (within a time limit) and Save without Formatting;
  VS Code's prettier.* settings
- **Folder trust, as VS Code's**: a project's own programs (its formatters,
  tasks.json, launch.json, debug sessions) run only in a folder the person
  trusts; the choice is kept on this computer
- Saving as VS Code does: trailing whitespace, the final newline, auto save,
  Save All; the status bar sets the indentation, the line endings and the
  language; tabs have VS Code's menu; Alt+Z, navigate back and forward,
  reopen a closed tab

### :bug: Bugs fixed

- Everything a review of the new IDE found: Run to Cursor's breakpoint is
  never kept, a step answered after its stop leaves the program stopped,
  debug sessions that end are let go, inline values of shadowed names, the
  debug hover's Markdown, quick diff on CRLF files and large rewrites, the
  editor's own formatter where no project formatter runs, settings and
  keybindings files keep their comments, compares keep unsaved edits, keys
  the browser keeps get keys the page receives, terminal settings applied

### :boom: Breaking changes & Deprecations

- The theme and code completion's on/off are now settings.json's
  (`workbench.colorTheme`, `window.autoDetectColorScheme`,
  `codeArena.inlineCompletion.enabled`): the theme picked before is asked
  for once again
- `/api/git/diff` is gone: `/api/git/show` reads a file at any revision

## v5.6.1 (2026-10-11)

### :bug: Bugs fixed

- A backup ended no more on v5.6.0: it opened the picture and video servers'
  log pipe in their status volume and waited for ever. That volume (rewritten
  every 2 seconds) is no longer backed up, and a volume's snapshot opens only
  regular files

## v5.6.0 (2026-10-11)

### :rocket: Epics and highlights

- **Picture, video and speech models are managed like chat models**: Admin →
  Models adds one (its files from the library by role, or a model the speech
  server has), edits its settings (steps, CFG, guidance, flow shift, sampler,
  scheduler, size, negative prompt, frames a second, longest clip; the GPU, a
  budget, where it decodes and more of stable-diffusion.cpp's own options; a
  speech model's language, voices, default voice and speed), its own price
  and how long it stays loaded unused, loads, unloads, keeps loaded and
  removes it. Each server runs the model kept loaded, else the one asked for
  last, and several picture models can share one server
- **Loaded when asked for, everywhere**: the chat's tools, Talk, read aloud,
  voice messages and API keys (a key's picture loads its model first, as the
  chat's do); the app unloads models unused for their idle minutes, speech
  models too (the speech server's own unloading is off)
- **Found like chat models**: Find on Hugging Face searches text-to-image and
  text-to-video repositories and downloads the files picked, and lists the
  speech server's registry, whose models it asks the speech server to fetch
- **Fail and recover like chat models**: a model whose server exits says why
  (too little GPU memory, settings it refused, a missing file, a crash, no
  answer in time) and is tried again after 1, 2, 4… 30 minutes; Load tries at
  once

### :sparkles: New features & Enhancements

- Settings → Model names the picture, video and speech to text models the
  chat uses; the tools pick only among those a person may use, and the chat's
  model may name another picture model the person asks for
- The picture and video servers report what they do every 2 seconds (the
  model and generation they run, their phase, the GPU's free memory, how they
  exited, the files they lack, where they decode)

### :boom: Breaking changes & Deprecations

- A picture, video or speech model has no On switch: removing it is how (the
  upgrade leaves out any that was off, and keeps whether each was kept loaded)
- The picture and video servers read `<server>.conf` and write to a new
  `media-status` volume; their model flags left `docker-compose.yml` and the
  chart for the app's model settings
- Usage counts a request to its credit by its kind of call only

## v5.5.1 (2026-10-11)

### :bug: Bugs fixed

- An upgrade to v5.5 keeps the groups' old credit column, so v5.4.0 still
  runs on the upgraded database until the backup is restored (it failed to
  list groups); a database already upgraded to v5.5.0 is not changed
- A chat waits about half a minute for a model gateway that is starting,
  after an upgrade or a restart, instead of failing after 3 seconds

## v5.5.0 (2026-10-11)

### :rocket: Epics and highlights

- **Argus links repositories more strongly and more precisely**: every link
  says how sure it is (strong, likely or weak), its layer (build, CI, deploy)
  and why, with the files and lines that make it. A name several repositories
  provide is settled by what else the user has (a package of one of them, the
  same file's other uses, its other links), else kept as a candidate, shown but
  never walked. Uses in tests, samples, generated code and vendored copies no
  longer make false links or false hubs; names are matched exactly; each
  ecosystem's own conventions are read (ProjectReference, parent POMs, version
  catalogs, relative submodules, CI components, tsconfig aliases and more).
  The graph tools take `min_confidence`, `layers` and `include_tests`;
  `dependency_path` returns the surest chains, not the shortest;
  `change_impact` searches a symbol only in the files that use the changed
  repository, each line with how sure it is; `impact_of` follows imports, not
  only #includes (docs/argus/graph.md)
- **Credits per kind**: chat, API, pictures, video and speech each have a
  credit a month for each person and group, any of them unlimited; **API
  access** is on for everyone and an admin can take it from one person
- **Code Arena's IDE comes closer to VS Code**: files cited in answers open at
  their lines, edits name the lines they changed, ask about the code you chose
  (add to chat, explain, fix, complete), grey-text completion from the model, a
  command palette, @ files, go to line, terminal links, Problems from the
  project's own check (fixed by the agent), Source control, and replace in
  Search
- **Every chat in step with Code Arena both ways**, within seconds, and the
  web chat follows a chat changed in another tab or device

### :sparkles: New features & Enhancements

- **The default models are an installer choice**: downloaded at the first
  start, copied from a folder, or skipped (`--default-models`); the app never
  downloads them on its own, and Admin → Models → Default models lists what is
  missing
- Admin → Models says **Unloading…** while a model stops, and why a model
  loads (an admin by name, a request, kept loaded, making room)
- `/web all` and `/web WORDS` list and search every chat in Arena from Code
  Arena; `/sync all` sends Arena what it lacks

### :bug: Bugs fixed

- Code Arena no longer looks stuck in the middle of work: a command with no
  time limit runs on as a job and the model carries on when it ends; the next
  message always runs, and what is typed while the model works is kept
- Two models no longer push each other out: the app alone decides which model
  makes room, a chat on a kept model that is loading again is answered, and a
  model killed while answering reads "Not loaded", not "Could not load"
- A picture asked for just as its server is turned off turns it on again
- Code Arena's IDE reads .tsx and .jsx with JSX instead of marking errors
- Argus no longer gives a header two projects have to the one with it nearer
  its top, nor marks a repository's own code as vendored because of names
  every project has

### :boom: Breaking changes & Deprecations

- Argus reads every repository again once after the upgrade (its reader's
  contract moved to 5) and rebuilds its links; `repo_map`, `system_map`,
  `dependency_path` and `change_impact` return confidence, tiers and possible
  links, and only walk likely and strong links of the product's own code by
  default
- On the first start, each person's gateway budget becomes their credit of
  every kind (a group's, its credit of every kind); the gateway's budgets and
  the groups' teams are cleared
- The app downloads the default models only with `DEFAULT_MODELS=download`
  (`defaultModels` in the chart); the installer sets it when asked to

## v5.4.0 (2026-10-10)

### :rocket: Epics and highlights

- **Code Arena and the web chat stay in step, both ways**: every Code Arena
  session is a chat in Arena too (marked with its folder in the chat list),
  continued on the web with the chat's own tools; what was added there comes
  into the session at its next turn. `/web` lists your chats and continues one
  in Code Arena. Messages go in the background and again until Arena has them,
  each known by its place so one sent twice is kept once
- **Code Arena does what other coding agents do**, from a feature-by-feature
  comparison with Claude Code, Qwen Code, Hermes Agent and DeepSeek Harness
  (docs/plan.md): memory it keeps and you edit (`remember`, `/memory`), search
  of earlier sessions, AGENTS.md, CLAUDE.md and QWEN.md read as instructions,
  `@file` and `!command`, checkpoints and `/rewind`, your own and the
  project's commands, sub-agents and skills (from `.arena/`, `.claude/`,
  `.qwen/`), kept allow and deny rules, and **a sandbox for its commands**
  (bubblewrap on Linux, sandbox-exec on macOS): secrets and Docker's socket out
  of reach, writes only in the workspace, the build caches and a /tmp of its
  own; checked on a real host
- **Argus across a whole estate**: repositories are linked by everything they
  use of each other (packages, imports, namespaces, modules, #includes,
  submodules, CI includes, images) in .NET, JavaScript and TypeScript, Python,
  Go, Java and Kotlin, Rust, Protocol Buffers and C/C++; `system_map` (hubs,
  layers, cycles, groups, every link), `dependency_path` (how one repository
  reaches another, with the evidence) and `change_impact` (what a change
  reaches, and a symbol's uses only where they depend on it). On a generated
  estate of 200 repositories, its 469 links were found exactly
- **An offline installer**: one file per release with every image and the
  deploy files; install, upgrade from v5.2.0 with a backup and rollback,
  repair, remove, status and verify, on Docker or rootless Podman

### :sparkles: New features & Enhancements

- **Code Arena commits its own work**: each turn's changes are committed with
  Code Arena as the author and you as the committer (your work in progress is
  left alone); a commit the model runs is Code Arena's too
- **Code Arena carries on** through a restarting gateway (five tries over
  about a minute and a half, each said), a stalled stream (given up and asked
  again) and a connection dropped mid-answer (what came is kept, the model
  carries on from it)
- **Local accounts move to the company directory** from Admin → People
  (**Move to the directory**): only the password check moves; the account,
  its chats, keys, spend, email, sessions and two-step sign-in stay
- **Every table sorts and filters**: any column by a click, a search box
  above it, across the app (Usage, dashboards' tables, Storage, Leaderboard,
  Quality, tool results), the tables in chat answers, and Argus's pages
- **RAM for chats that lost their slot**: Settings → Model sizes the engine's
  RAM cache per model (8 GB); the model card says it. Six long chats taking
  turns over four slots read 57-63% of each prompt from the cache
- **Tool rounds wait less**: the thinking between tool calls is lighter
  (Settings → Model, `low` as installed): 8-9.5 s to 4.7 s a round, measured
- **The engine's RAM kept for the model** (`ENGINE_RAM_PROTECT`), for a model
  larger than RAM and the GPU, which reads its weights from disk
- Deep research runs only as a tool (the chat's Tools menu): no button in the
  message box
- The Resources dashboard says memory used by programs apart from the page
  cache that holds the models' files

### :bug: Bugs fixed

- An admin's Load lost in an engine restart is sent again (it ends an earlier
  Unload), so a model no longer stays unloaded while the page waits
- Code Arena's prompt keeps its keys after a wait on long commands
- A headline number shrinks with its card instead of being cut
- Code Arena: an answer cut by a connection closed cleanly mid-stream was
  taken as finished

### :boom: Breaking changes & Deprecations

- Argus reads every repository again once after the upgrade (its reader's
  contract moved to 4: Go, Java, Kotlin, Rust and .proto files and manifests
  are indexed now)
- Argus serves 20 tools (`system_map`, `dependency_path`, `change_impact` are
  new); `repo_map` describes links by kind, names and an example, not by
  #include weight only
- Code Arena commits each turn's changes by default (`"autoCommit": false`
  turns it off), keeps sessions in step with Arena (`"syncChats": false`), and
  runs commands in a sandbox where the system has one (`"sandbox": "off"`)

## v5.3.0 (2026-10-10)

### :rocket: Epics and highlights

- **People at once, and the token cache**: each model has its own line, so
  people on a small model no longer wait behind the big one; two models are
  loaded at once by default, and the big model everyone uses is never unloaded
  for another (API keys' requests get room the same way); each chat keeps its
  own engine slot (`id_slot`), so a follow-up turn reads only what is new:
  7–11 s per turn with four people taking turns, where it grew to 31 s.
  Measured live after the upgrade, four people at once: the one on a small
  model got a first word in 6 s (was 84–88 s, and 2 of 6 answers failed to
  load the model), the three on the big one 2–3 s on a follow-up (was 84–121 s)
- **What each prompt cost**: every model has a price (its own or Settings →
  Prices, cached input priced apart), pictures, video and speech too; every
  answer shows its tokens and cost to everyone; Usage lists each prompt, and
  admins can recalculate past costs recorded at nothing
- **Argus repositories**: one repository whatever token lists it (the copies
  an earlier token made are merged on upgrade), search, filters, sorting and
  batch changes, reindexing on a schedule, and each repository's own progress
  and a log people can read
- **Help everywhere**: a manual in the app (Manual, Ctrl K), a **?** on every
  page with that page's help, in Argus's own app and Code Arena's IDE too

### :sparkles: New features & Enhancements

- **Company directory (LDAP)**: the test goes step by step and says exactly
  what to fix (the server's message, Active Directory's reasons, a DN with no
  entry, which password it used); **Try a person's sign-in**; a setup guide and
  OpenLDAP and Active Directory examples for every field
- **Voice for each person**: the language they speak, a voice per language
  from what the speech models offer, the speed and reading aloud in Talk, with
  **Try it** (Your account → Voice; everyone's defaults in Settings → Speech);
  API keys' speech without a voice or language uses the key's person's
- **Rate limits for API keys**: requests and tokens a minute for the company,
  a group or a person; 429 with Retry-After saying which limit; people see
  their limits and use on their key card
- **Deep research is a tool** in Admin → Tools (on/off, who, ask first); the
  model can start one when allowed; Arena MCP serves it to agents
- **Admin → Storage**: disks and what takes room (databases, files by person,
  models, Argus, backups, logs, metrics) with trends and a disk alert; browse,
  download and delete files (legal hold respected); clean-ups with a preview;
  per-person storage room
- **Arrow keys** bring back earlier messages in the chat and in Code Arena (its
  terminal prompt is now edited in place, with a history per folder)
- **Code Arena**: Arena MCP and Argus connect in the background and never hang
  it (Argus answers at once however large GitLab is); the context use and the
  compaction threshold are shown and set (`--compact-at`, `/context`, the IDE);
  long commands can run with no time limit, watched until done, stopped only by
  you
- **Sign-in**: the Argus logo, then its hundred eyes open across the screen
  and close again (a still logo when reduced motion is asked for)
- **The chat list folds**, as a whole and group by group
- **Dashboards** name drives, CPUs and GPUs from the machine they run on, open
  on the last hour, and per-core temperature charts keep their rows in line

### :bug: Bugs fixed

- Postgres's health check logged "FATAL: database "arena" does not exist"
  every ten seconds
- Costs were 0 for every model but one, cached tokens were never priced, and
  transcriptions and videos had no cost
- People on different models waited for each other; a small model could be
  stopped while loading and then never offered again
- Changing Argus's GitLab token made a second copy of every repository
- Coding agents waited up to 42 s (or forever) for Argus with many projects
- Two drive names from the development machine were written into the
  dashboards; per-core temperature panels stretched their rows

### :boom: Breaking changes & Deprecations

- Models loaded at once now defaults to 2 (a value an admin saved is kept);
  the engine restarts once after the upgrade to apply the new presets
  (`no-cache-idle-slots`, `ctx-checkpoints = 4` for hybrid models)
- `Chat:EngineSlots` is gone (each model's own slots set its places)

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
