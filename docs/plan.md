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

## In progress: v7.7.1 (hotfix)

Asked 2026-11-02. Built and merged into `main`; left is the release.

1. **Upgrade, rollback and resume.** Built: an upgrade from 7.6.3 works, a
   failed one rolls back, and one stopped part way carries on.
2. **A model's window and its longest answer, everywhere.** Built: every
   engine, picker and page holds one request to both, and the context ring
   warns when they do not fit.
3. **Code Arena's compaction, sync, context popup and skills.** Built: the
   whole conversation kept after a compaction, the web's compaction seen at
   once, the context popup as detailed as the web's, skills shown, used with
   `/name` and made by the person.
4. **A model's engine changed at runtime.** Built: the Engine field in Edit
   and Add a model, the move without lost answers, and vLLM and SGLang running
   a GGUF file from the library.
5. **Source Control and changes fold as VS Code's do**, and **calm themes**
   with 4.5:1 contrast everywhere. Built.
6. **Functional-test races.** The races found while building fixed at their
   cause. Done when the functional test passes ten runs in a row on live.
7. **Release.** The offline scenarios pass (upgrade, rollback, resume), then
   the tag, the GitHub release and the bundle.

## Next: v7.7.2 (hotfix) — the 3 H200 server, and what people hit

The user's production host has three H200 NVL GPUs and serves Qwen3.8 27B
with a 262K window to 10 to 15 people. Planned before (2026-11-02): even GPU
load and rewind. Added 2026-11-06: the bugs below.

### Headlines

1. **Even load on every GPU.** Requests spread over all three GPUs on
   llama.cpp, vLLM and SGLang alike. Done when a load test on the H200 host
   shows each GPU within 10% of the others.
2. **The full window for each person.** Each of 10 to 15 people gets the whole
   262K, not 262K split between them (llama.cpp divides `ctx` by its slots).
   Settings take "context per person" and "people at once", and say what the
   GPUs' memory allows. Done when 15 chats each hold 262K at once.
3. **Rewind to a message**, in the web chat and in Code Arena, as Claude
   desktop does: the chat and the files it changed go back to that point.

### Bugs

4. **Tokens per second, right.** Find why the dashboard shows 100 tok/s on the
   27B where at least 200 is expected, fix the number or the serving. Add
   per-GPU and per-person performance panels.
5. **Code Arena compacts in time.** Compaction starts before the window is
   full, never sticks, and no history goes missing. Done when a long session
   test passes three compactions.
6. **No empty space in Code Arena's chat** after a few prompts.
7. **Code Arena loads fast.** Opening a chat, its history and the sync with
   Argus Arena are fast. Done with a measured time for a 500-message chat.
8. **Settings reach Code Arena live.** A change in Argus Arena applies in a
   running Code Arena without a restart.
9. **No forced Compare.** The chat never opens Compare for a person without
   the compare permission.
10. **Esc cancels a skill.** An entered skill can be skipped or cancelled with
    Esc, in both chats.
11. **Code Arena's tools work and are quick.** grep, run task and the others
    fixed and measured. The Python tool does something useful, or it goes.
12. **Large files.** The model gets a short summary of each file in context
    and reads only the part it needs.
13. **Safeguard blocks are explained.** The person sees what caused a block
    and the finding. The false password findings are fixed.
14. **A self-signed GitLab certificate works.** People connect their apps and
    accounts to a GitLab with the company's own CA.
15. **Workspaces work.** Each broken case reproduced in a test and fixed.
16. **Right to left.** Both chats lay out RTL languages fully.
17. **Code Arena starts quietly.** No shell window at start. Logs go to a file
    beside the exe.
18. **Code Arena is self-contained.** The exe runs with an old Edge or
    Chromium WebView.
19. **Argus counts repositories, not branches.** 127 repositories show 127,
    not 479.
20. **The wiki search finds things.** No random code results, symbols such as
    `MyLib::TestFunc` found, every result with pages, and filters.
21. **A default install passes compliance.** The requirements a fresh install
    leaves unmet are met by default or explained.

## v7.8 — Argus's code graphs, and the agent you can watch

### Headlines

1. **Argus's code graphs.** Who calls a method, what implements an interface,
   what a merge request breaks across every repository a person can read, with
   the lines, tests and owners. A Code map page, call and type hierarchy in
   Code Arena, an Impact card on reviews; `symbol_graph`, `mr_impact` and
   `service_contracts` for agents. Design: [argus-graph.md](design/argus-graph.md),
   31 commits; the headline is reachable at commit 27.
2. **Tasks and subagents beside the chat.** Code Arena gets assistants and
   subagents, shown in a tasks panel as Claude desktop does, not mixed into
   the chat.
3. **What is in the window.** Both apps show what fills the context: the
   messages, each tool's and skill's share, the system prompt and the room
   kept for the answer.

### Also

4. **Send while it works.** In both chats a message sent during a turn is taken
   in as Claude Code does, without stopping the turn.
5. **Goal mode.** Code Arena keeps working until a stated goal is met, and
   says how it checked.
6. **Skip thinking** in Code Arena, per message or per session.
7. **Detail in a modal.** Admin's repository rows and model cards are compact,
   with their detail in a modal.
8. **The wiki search on the graph.** Symbols and their callers searched by
   name, with filters of one's own.
9. **The app's own gateway**, commits 10 to 21 of
   [own-gateway.md](design/own-gateway.md). LiteLLM goes only after the
   measured gates and a day's soak.
10. **The IDE's editor groups.**

## v7.9 — A model of your own, and Code Arena your way

### Headlines

1. **Model lab.** Teach a small model the company's words on the local GPU (a
   LoRA from chosen knowledge, prompts and good answers), compare it with its
   base in blind battles, and give it to people in one click. Needs a yes:
   the training libraries.
2. **Test apps in a VM.** A tool that drives VirtualBox and VMware VMs: start,
   snapshot, copy in, run and test an app, read the screen.
3. **Skills and tools of your own.** Import and export skills in both apps.
   Local skills and tools in Code Arena. Tools customised in Code Arena as in
   the web.

### Also

4. **Evals.** Test sets for assistants, prompts and models, run on every
   change, scored by rules or a judge model, what got worse shown first.
5. **Code Arena closer to VS Code.** A customisable sidebar; chat and file
   tabs that detach; a middle click closes a tab; several repositories open in
   Source Control at once.
6. **Themes.** The common VS Code themes, sharp like Arena's own, and options
   for poor vision (high contrast, larger text).
7. **Context, much leaner.** Tool results and file reads summarised, measured
   on the tool-modes tasks, and improved each release.

## v8.0 — Projects, and access that is clear

### Headlines

1. **Projects.** Projects in Argus Arena and Code Arena with their own
   accounting and metrics (tokens, cost, time, people). Global projects every
   person may contribute to; each person may make one.
2. **Reports people can read.** Exports of a project's progress, tokens, time
   and people, and a time window on every report and dashboard.
3. **Permissions, redone.** Finer permissions with a clearer admin page. What
   a person may not use is hidden. New people join a default group.

### Also

4. **Every feature in the UI.** Nothing is left to a setting only the manual
   names.
5. **Compliance for new features.** Each feature from 7.7 on has its
   controls, with default rules and policies on at install.
6. **FP4 on H200.** Hopper has no native FP4; measure vLLM's MXFP4 and NVFP4
   paths against FP8. Needs a yes: the models.
7. **More engines.** Strata as an engine, and others worth adding. Needs a
   yes: their images.

## Groundwork, in between

1. vLLM and SGLang measured on the H200 host: a smoke test per model, numbers,
   and the people load tests; DCGM's image.
2. `argus graph-eval` with the owners' links of the company's repositories,
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
