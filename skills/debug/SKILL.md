---
name: debug
title: Debug systematically
description: Find the cause of a bug or failure step by step — reproduce, narrow down, test each hypothesis — before changing anything, then fix it at its cause and prove it. Use for errors, crashes, wrong results, flaky tests or "it does not work".
parameters:
  - { name: problem, description: "What goes wrong: the error, the symptom, when it happens", required: false, default: "the problem described in this conversation" }
---
Debug {{problem}} systematically. Do not guess-and-patch.

1. **Reproduce**: find the shortest reliable way to see it fail. Note the exact error and where it is thrown.
2. **Gather evidence**: logs, stack traces, recent changes (git log, the diff), inputs, versions, environment. Read the code on the failing path.
3. **Hypothesise**: list the possible causes, most likely first, each with what would confirm or rule it out.
4. **Test** each hypothesis with the cheapest check (a log line, a smaller input, a debugger, a test) until one is confirmed. Say what each check showed.
5. **Fix the cause**, not the symptom, in the narrowest place. No retries, sleeps or catch-all handlers to hide it.
6. **Prove it**: the reproduction now passes, a test guards it, and nothing around it broke.

Report the cause, the evidence, the fix and how you proved it.
