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

Everything on it shipped in v7.8.0 to v7.8.3, except what needs the hardware
below, and two parts kept out on purpose:

1. **The LMDeploy image in the bundle.** pypi.org stalls from the build
   host's network, so it was built only with a third-party mirror; it ships
   once it builds from pypi.org (or the company's own mirror).
2. **VS Code extensions that need their own code** (an extension host), in
   Next below.

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

## Next

1. **Argus's code graphs.** Who calls a method, what implements an interface,
   what a merge request breaks across every repository a person can read, with
   the lines, tests and owners. A Code map page, call and type hierarchy in
   Code Arena, an Impact card on reviews; `symbol_graph`, `mr_impact` and
   `service_contracts` for agents. Design: [argus-graph.md](design/argus-graph.md),
   31 commits; the headline is reachable at commit 27.
2. **The app's own gateway**, commits 10 to 21 of
   [own-gateway.md](design/own-gateway.md). LiteLLM goes only after the
   measured gates and a day's soak.
3. **Evals.** Test sets for assistants, prompts and models, run on every
   change, scored by rules or a judge model, what got worse shown first.
4. **Code Arena's extensions.** An extension host for VS Code extensions that
   need their own code (ESLint, GitLens, Pylance).
5. **Flaky tests to fix at their cause:** `ConcurrencyTests.A_model_asked_for_at_the_engines_limit_unloads_an_idle_one_not_kept_never_the_big_one`,
   `StdioChildTests.A_child_that_listens_is_reached_on_its_port_and_one_that_ends_first_says_why`,
   `ToolMeasureTests.Run_shell_keeps_a_long_output_whole_ends_with_a_child_left_behind_and_says_how_it_ended`
   (each failed once under a full suite's load, and passes alone).

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
