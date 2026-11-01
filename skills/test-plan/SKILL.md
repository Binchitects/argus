---
name: test-plan
title: Test plan
description: Write a test plan for a feature or release — what to test and why, the cases (happy paths, edge cases, errors, security, performance, accessibility), the data and environments, and what must pass to ship. Use before a release, for QA handover, or when a change is risky.
parameters:
  - { name: feature, description: "The feature, change or release to test", required: false, default: "the change in this conversation" }
---
Write a test plan for {{feature}} ($ARGUMENTS).

1. **Scope**: what is tested and what is not, and the risks that matter most.
2. **Cases**, grouped by area, each with steps, data and the expected result:
   - main flows; boundaries and empty, large and malformed input; errors and recovery;
   - permissions (who may and may not), security (injection, access to others' data);
   - concurrency and repeated actions; upgrades and existing data;
   - performance (what load, what is acceptable); accessibility (keyboard, screen reader, contrast) for UI.
3. **Automated or by hand**: which cases belong in unit, integration or end-to-end tests, and which are checked by a person.
4. **Environments and data** needed.
5. **Exit criteria**: what must pass to ship, and what is a known, accepted gap.

Mark the cases most likely to find a bug.
