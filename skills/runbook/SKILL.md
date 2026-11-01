---
name: runbook
title: Write a runbook
description: Write an operational runbook for a service or a recurring task — what it is, how to check it is healthy, and step-by-step procedures for common alerts and failures, with exact commands. Use for on-call handover, a new service, or an alert without instructions.
parameters:
  - { name: service, description: "The service or task the runbook is for" }
---
Write a runbook for {{service}} ($ARGUMENTS), for an engineer on call at 3 a.m. who does not know it well.

- **Overview**: what it does, who depends on it, where it runs, its owners, and its dashboards and logs.
- **Health**: how to tell it is well — the checks and commands, and what normal looks like.
- **Procedures**, one per alert or common failure: the symptom, the likely causes, then numbered steps with the exact commands and what each should show; when to escalate and to whom.
- **Routine tasks**: restarts, deploys, rollbacks, backups and restores, scaling, certificate renewal — each step by step.
- **Dangerous operations**: what can lose data or cause an outage, marked clearly, with how to undo.

Use the commands and names of this project when you can see them; mark what must be filled in.
