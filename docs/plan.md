# Plan: what is ongoing and next

This file tracks only work that is in progress or planned. What has shipped is
in [CHANGELOG.md](../CHANGELOG.md), one section per release, and in each
release's tag (`git tag -n99 vX.Y.Z`). An item leaves this file when its
release ships.

## Standing rules

- **Releases.** Every release has a theme and two or three headline features
  that can be shown in a minute. Groundwork goes in between, never as a
  release's whole content. The CHANGELOG's Epics section leads with the
  headlines.
- **How a release is made.** As [development.md](development.md#releasing)
  says: every suite green, `./arenactl check compliance` and
  `tools/release-scan.py` pass, the upgrade from the oldest release works,
  the offline bundle is built and checked, then the tag and
  `gh release create` with the changelog, the `.run`, the SBOM and Code
  Arena's packages.
- **Parity tests stay alive.** `functional-test.py`, `acceptance.py` and
  `audit-auth.sh` test the live stack from the outside, and every release
  passes them. A stack must also deploy from zero on empty volumes.
- **Tests per layer.** xUnit, xUnit with Testcontainers, Vitest and
  Playwright with axe. A port is checked by a comparison test: the same input
  to old and new, the same output.
- **Flaky tests.** Find the race between the test and the app and fix its
  cause. No retries, no sleeps. Ten runs in a row and the whole class pass.
- **Compliance.** A new feature brings its controls, tests and live checks.
  A control made weaker is named under Weakened controls in the changelog.
- **One item at a time.** Each item is committed, pushed and deployed live
  before the next. Downloads (images, models, packages, data sets) need a yes,
  with their size said.
- **Settings.** Everything is set in the app, applied at once where its
  service can take it, else saved with the one command to apply it. No Docker
  socket.

## Open from the 2026-11-06 list

Everything on it shipped in v7.8.0 to v7.8.4, except what needs the hardware
below, and VS Code extensions that need their own code (an extension host, in
Next below). The LMDeploy image is in the bundle since v7.8.4, built from
pypi.org through an HTTP proxy (`arenactl --proxy`).

## Needs the production host (3x H200 NVL)

1. Even load on every GPU, on each engine: done when a load test shows each
   GPU within 10% of the others.
2. 15 people each with the full 262K window on Qwen3.8 27B at once, with
   the advisor's numbers checked against the real memory.
3. The 27B's decode speed: at least 200 tok/s for a person on the fixed
   dashboard.
4. FP4 against FP8 on Hopper: `arenactl check fp4-h200` (the RTX 3090
   numbers are in multi-gpu-server.md).
5. Strata and LMDeploy measured beside vLLM and SGLang.

## Needs Windows

Code Arena on a real Windows machine: no console window, the log beside the
program, and Edge or Chrome 109.

## v8.1 (asked 2026-11-13)

1. **Context stretch on every engine**: RoPE scaling (YaRN and the rest) on vLLM, SGLang, LMDeploy and Strata, as
   llama.cpp has it.
2. **Where the time goes**: how long the model took, the tools, and the person, on each message, each session, in
   total, in Accounting and on the dashboards.
3. **Accounting and project metrics live**: both update as requests are booked, not on a refresh.
4. **Goals and tasks from the sessions**: a session sets its project's goals and tasks and keeps them up to date.
5. **Runs until the goal**: in Code Arena and the chat, an answer cut at its longest length is continued by itself,
   until the goal is met, without the person having to ask.
6. **Every kind of model**: a model that does more than chat (pictures from a text encoder's model, and the rest:
   pottokao/Qwen-Image-2.1-Text-Encoder-Heretic-GGUF) is recognised for all it does, after research into the kinds.
7. **The system's token capacity live**: the Stack Performance dashboard shows how many tokens a second the system
   can make now and makes, in numbers that read right, a line per model and per GPU, not one line for all.
8. **A test bench under Models**: a model's capability (what it answers well) and capacity (how much at once) tested.
9. **Chat sync between Code Arena and the Arena that never misses**.
10. **The sign-in's eyes, the logo and the pattern**: better eyes on the sign-in page, the logo more an owl (the eyes in
    the A kept), and the brand's symbol as a pattern everywhere.
11. **Agents and scheduled tasks in Code Arena** (asked 2026-11-13): Arena's agents (Agent Studio) and scheduled tasks
    made, run and followed from Code Arena too, the IDE and the terminal.

12. **Code Arena's UI tidied** (asked 2026-11-13): the chat's header holds only what is used often, the rest in a
    menu or where it belongs; no tool twice in its tools, and those of no use in Code Arena gone; the tools'
    customization simple; every pane resizable.
13. **Checklists in Code Arena** (asked 2026-11-13): made, edited and deleted there too (the terminal and the IDE), and a
    repository's own (.arena/checklists) for everyone who opens it.

Done (v8.1.0): every item above.

## Next

1. **Farzan Graph's code graphs.** Who calls a method, what implements an interface,
   what a merge request breaks across every repository a person can read, with
   the lines, tests and owners. A Code map page, call and type hierarchy in
   Code Arena, an Impact card on reviews; `symbol_graph`, `mr_impact` and
   `service_contracts` for agents. Design: [code-intel-graph.md](design/code-intel-graph.md),
   31 commits; the headline is reachable at commit 27. Done: 1 (access kept as
   GitLab project ids, repository row ids never reused), 2 (the allowlist in
   the SQL of the graph tools, impact_of and semantic search), 3 (a name several
   repositories provide settled only by the user's own manifests; candidates
   shared among the readable), 4 (secrets taken out of configuration files
   before they are stored, and out of those stored before), and the symbol graph inside
   each repository with `symbol_graph` (callers, callees, references, implementations,
   bases; Code Arena's scanners linked). Still to do: stable symbol keys, cross-repository
   symbol edges, tests and owners, `mr_impact`, contracts, the Code map page, and the
   measured gates.
2. **The app's own gateway**, commits 10 to 21 of
   [own-gateway.md](design/own-gateway.md). Measured on the offline host
   (2026-11-11, [gateway.md](measurements/gateway.md)): every gate passes,
   6 ms overhead at p95 against LiteLLM's 17 ms; two app replicas, a pool of two
   copies, a copy killed, speech and transcription all pass. LiteLLM is gone
   (commits 19 to 21: the code, the deployment, the docs), with its keys and
   log imported once on an upgrade and its database dropped after the backup.
   Still to do: the day's soak (`gateway-soak.py`, started 2026-11-11) before
   it is merged into v8.0; clients-check.py on the H200 host; the importer goes
   in the release after 8.0.
3. **Evals.** Test sets for assistants, prompts and models, run on every
   change, scored by rules or a judge model, what got worse shown first.
4. **Code Arena's extensions.** An extension host for VS Code extensions that
   need their own code (ESLint, GitLens, Pylance).
5. **Flaky tests to fix at their cause:** `ConcurrencyTests.A_model_asked_for_at_the_engines_limit_unloads_an_idle_one_not_kept_never_the_big_one`,
   `StdioChildTests.A_child_that_listens_is_reached_on_its_port_and_one_that_ends_first_says_why`,
   `DebugSessionTests.Each_response_goes_to_the_page_that_asked_with_its_own_number_and_events_go_to_every_page`
   (the stop took 8.7 s on CI, past the 5 s its waits add up to: something else waits)
   (each failed once under a full suite's load, and passes alone).

## Groundwork, in between

1. vLLM and SGLang measured on the H200 host: a smoke test per model, numbers,
   and the people load tests; DCGM's image.
2. `code-intel graph-eval` with the owners' links of the company's repositories,
   and the priors calibrated from it.
3. Reviews: plain git repositories, two refs compared on Gitea, and GitHub's
   and Gitea's webhooks for automatic reviews.
4. The chat's default model with more answers at once, or a second copy, where
   people wait for it.
5. Kubernetes: CloudNativePG in the chart, and backups to object storage.
6. Measurements: Fix it on CVEfixes (needs a yes), qiskit-ibm-runtime for the
   quantum set's 51 device problems (needs a yes), chemistry's and physics'
   lab sets.
7. The science lab: a team of models on one problem, plots in cells, and
   telling a person when the model's context is too small for its tools.
8. Tool modes: the graph questions (need a measured person with GitLab
   rights), and tools on demand a direct-mode model passes over.
9. Carried: Code Arena's sandbox on Windows; Laya's routing quality; deep
   research under six minutes.

## Later, each needing a yes

FedRAMP's baselines; NIST AI 600-1; NIS2 and DORA; ISO/IEC 27701, 27017 and
27018; a FIPS mode built on OpenSSL's validated provider.
