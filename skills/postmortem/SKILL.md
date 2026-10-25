---
name: postmortem
title: Incident postmortem
description: Write a blameless postmortem of an incident — summary, impact, timeline, root cause, what went well and badly, and actions with owners. Use after an outage, a security incident or a serious bug in production.
parameters:
  - { name: incident, description: "The incident, in a few words" }
---
Write a blameless postmortem of: {{incident}}, from what is given ($ARGUMENTS).

Sections:
- **Summary**: what happened, in three sentences.
- **Impact**: who and what was affected, for how long, how badly (numbers when known).
- **Timeline**: detection, response, mitigation and resolution, with times (and time zone).
- **Root cause**: asked "why" until the systemic cause, not a person. Contributing factors too.
- **What went well, what went badly, where we were lucky.**
- **Actions**: a table of what, owner, due date and priority — each one preventing recurrence, detecting it sooner, or reducing its impact.

Mark what is not known yet. Never blame a person; describe what the system allowed.
