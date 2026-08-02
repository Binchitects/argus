# Reviewer Checklist

Work through this in order. Every item is here because **it caught a real defect on this project** — none of it is generic advice.

---

## Before you read the diff

- [ ] Read the task brief. You are checking against what was asked, not what you would have asked.
- [ ] Note what a previous review already approved. Re-litigating settled decisions is noise, and it crowds out real findings.
- [ ] Note what the implementer *claims*. Treat it as unverified. A stated rationale never downgrades a finding's severity.

## Spec compliance

- [ ] **Missing** — requirements skipped, or claimed but not implemented.
- [ ] **Extra** — anything not requested. Over-engineering is a finding.
- [ ] **Misunderstood** — right feature, wrong shape.
- [ ] Anything you cannot verify from the diff alone → mark ⚠️, do not broaden your search.

## Test integrity — the highest-yield section

This project has shipped **eight tests that passed with their bug fully reintroduced.** For each new or changed test:

- [ ] **What else could make this pass?** Ask it explicitly for every assertion.
- [ ] Does it assert on the **specific** behaviour, or on an aggregate other fixtures can satisfy?
      *Real case:* `assert second.skipped >= 1` was satisfied by two unrelated fixture files on every run.
- [ ] Does the fixture make the property **provable**? Two repos with colliding names and paths, so the allowlist is the only discriminator — not one repo, where any implementation passes.
- [ ] Is the assertion reading the thing it claims to read?
      *Real case:* `COUNT(*)` on an external-content FTS table proxies to the content table and is structurally blind to index desync. Only `MATCH` sees it.
- [ ] Can two different mechanisms produce the **same observable**?
      *Real case:* `mode=ro` and `PRAGMA query_only` raise an identical error, but only one is unbypassable.
- [ ] Was the test **demonstrated failing**, or only claimed to? Revert the production change and check.
- [ ] Does a "revert" that deletes the whole function prove anything? It proves the test *calls* the code. Only a **targeted** revert proves the assertion discriminates.

## Seams — where every defect on this project has lived

Per-task reviews check a module against its own brief. Defects hide in the composition.

- [ ] Do the return shapes actually agree across the call chain? Field names, types, `None` handling.
      *Real case:* one tool returned a namespace *string* where the consuming tool required an integer id — and the tool description asserted otherwise.
- [ ] Trace one item end to end through every state combination the code admits.
- [ ] Does this change remove a mechanism that something else was quietly relying on?
      *Real case:* replacing a wrong proxy also removed the accidental self-heal riding on it.
- [ ] Does a comment or docstring assert something that is no longer true?
      *Real case:* "over-matching can only move a path to `uncovered` — the safe direction" was true when written and became false when `uncovered` started deleting data.

## Agent-facing surfaces

If an LLM reads it, it is code.

- [ ] **Tool descriptions** — does each name the question it answers? Does it state its limits so the model qualifies rather than asserts?
- [ ] **Error text is prompt text** — is it actionable, and is every suggestion in it *true*?
      *Real case:* an error told the model to retry with `regex=True`, a parameter that existed nowhere.
- [ ] Do documented tool compositions actually work? Follow the chain the description promises.

## Security boundary

- [ ] Is the check **before** the work, or after? A filter applied post-fetch has a window.
- [ ] Is the enforcement structural (signature, type) or conventional (remembering)?
- [ ] Does an **empty** permission set return nothing, or everything?
- [ ] Can a credential reach a log, an exception message, or a traceback with locals?
- [ ] Are failures **fail-closed**, and is an exception ever mistaken for a denial?
- [ ] Is the *denied* path audited, not just the allowed one?
      *Real case:* only resolved-then-rejected requests were logged; anonymous probes left no trace at all.

## Execution model

- [ ] Sync I/O inside `async def`? It blocks the event loop for every other caller.
- [ ] Connection/resource lifetime — opened, used and closed in one place, on one thread?
- [ ] Does a per-request path do something that belongs at startup?
      *Real case:* schema migration ran per request, triggered by untrusted traffic.

## Deployment reality

- [ ] Does anything verify the **deployed** path, or only the units?
      *Real case:* 223 passing tests, two clean image builds and a valid compose config all passed while the deployment rejected every real call.
- [ ] Does the health check exercise the same middleware as real traffic, or bypass it and give false confidence?

## Before you submit

- [ ] Every finding has a `file:line` and a concrete failure scenario — inputs/state → wrong outcome.
- [ ] **Severity matches the verdict.** An Important finding means *Needs fixes*. Filing one and approving is a defect in the review.
- [ ] Nothing manufactured to look thorough. If it is clean, say so plainly.
- [ ] Say what you deliberately did **not** check, so the next reader knows the edges.
