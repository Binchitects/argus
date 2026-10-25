---
name: plan-change
title: Plan a change
description: Plan a code change before making it — what it touches, the steps in order, how each is checked, and the risks — grounded in the code as it is. Use before a feature, a migration or any change of several steps.
parameters:
  - { name: goal, description: "What the change should achieve", required: false, default: "the change asked for in this conversation" }
---
Plan how to make this change: {{goal}}. Do not change anything yet.

1. **Understand**: find the code involved (search, read callers and tests). State what exists today and what must change.
2. **Impact**: the files, modules, data, APIs and other systems the change touches; who calls what changes.
3. **Steps**: in order, each small enough to review, each with what "done" means and how it is checked (a test, a build, a command, a page to look at). Mark the steps that can run in parallel.
4. **Risks**: what could break, migrations and data, compatibility, performance, security; and how to roll back.
5. **Open questions** for the person, if any.

Give the plan as a numbered list; keep it as short as the change allows.
