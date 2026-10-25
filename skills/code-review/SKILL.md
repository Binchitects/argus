---
name: code-review
title: Review code
description: Review code or a change for bugs, security holes, performance and readability, worst first, each with where and how to fix it. Use when asked to review, check or look over code, a diff, a branch or a merge request.
parameters:
  - { name: what, description: "The code to review: a file, a diff, a branch, or the change just made", required: false, default: "the code or change in this conversation" }
  - { name: focus, description: "What matters most, like security or performance", required: false, default: "correctness first, then security, performance and readability" }
---
Review {{what}}, with {{focus}} as the focus.

1. Read it whole before judging, and read the code around a change (callers, tests, the types it uses) when you can. Understand what it is meant to do.
2. Look for, in this order:
   - **Correctness**: wrong logic, off-by-one, null or empty cases, error paths, races, resources not released, behaviour that changed by accident.
   - **Security**: untrusted input reaching queries, commands, paths or HTML; secrets in code or logs; missing checks of who may do what.
   - **Performance**: work repeated in loops, queries per item, unbounded growth, blocking calls on hot paths.
   - **Readability and fit**: names, duplication, dead code, the project's own conventions.
3. Report each finding as: severity (critical, major, minor, nit), `file:line`, what is wrong and why it matters, and the fix (code when short). Worst first.
4. Say what is good too, briefly, and what you could not check (tests not run, code not seen).

Do not rewrite the code wholesale; keep to what the change needs. If there is nothing wrong, say so plainly.
