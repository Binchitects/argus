---
name: write-tests
title: Write tests
description: Write automated tests for code — the behaviour that matters, edge cases and failures — in the project's own test framework and style. Use when asked to add tests, raise coverage, or prove a fix.
parameters:
  - { name: target, description: "What to test: a function, a class, a file, a feature", required: false, default: "the code in this conversation" }
  - { name: framework, description: "The test framework, when not the project's own", required: false, default: "the project's own (find it in its existing tests)" }
---
Write tests for {{target}} with {{framework}}.

1. Find how the project already tests (folder, naming, helpers, fakes) and follow it.
2. Test behaviour, not implementation: what a caller sees. One test per behaviour, named as a sentence of what it proves.
3. Cover the usual case, the edges (empty, one, many, the limits, unicode, time zones where dates are involved), and the failures (bad input, a dependency down, a timeout).
4. Keep tests independent and deterministic: no real network, clock or randomness without control; no sleeps — wait for the condition.
5. Run them when you can, and show they fail without the code they test where that proves something (a bug fix).

Say what you covered, what you left out and why.
