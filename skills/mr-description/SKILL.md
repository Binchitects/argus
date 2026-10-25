---
name: mr-description
title: Write a merge request description
description: Write a merge or pull request description — what changed, why, how it was tested, risks and what to review first. Use when opening a merge request or pull request.
parameters:
  - { name: changes, description: "The changes: a branch, a diff, or what was done", required: false, default: "the current branch's changes against its base" }
---
Write a merge request description for {{changes}}.

Sections:
- **What**: the change in a few sentences, for someone who has not seen it.
- **Why**: the problem or goal, with issue links when known.
- **How**: the approach and the main decisions; alternatives rejected, briefly.
- **Testing**: what was run (tests, manual checks) and the results; what was not tested.
- **Risks and rollout**: migrations, configuration, breaking changes, how to roll back.
- **Review guide**: where to start reading, and what deserves the closest look.

Keep it short and concrete; bullet points over prose. Screenshots or examples for UI and API changes.
