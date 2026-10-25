---
name: refactor
title: Refactor safely
description: Restructure code without changing what it does — smaller steps, each checked by the tests — to make it clearer or easier to change. Use for cleanup, extracting or renaming, removing duplication, or preparing code for a change.
parameters:
  - { name: target, description: "The code to refactor", required: false, default: "the code in this conversation" }
  - { name: goal, description: "What the refactoring should achieve", required: false, default: "clearer code that is easier to change" }
---
Refactor {{target}} toward {{goal}}, without changing its behaviour.

1. Make sure tests cover the behaviour first; if they do not, add characterisation tests before touching anything.
2. Work in small steps (rename, extract, move, inline, remove duplication), running the tests after each.
3. Keep the public interface unless the goal is to change it; then update every caller in the same change.
4. Keep to the project's style and conventions; do not mix refactoring with new features or bug fixes.
5. Stop when the goal is met: no gold-plating.

Show the steps taken, and say how behaviour was kept (the tests run, their results).
