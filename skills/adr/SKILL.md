---
name: adr
title: Architecture decision record
description: Write an architecture decision record — the context, the options weighed with their trade-offs, the decision and its consequences — in the team's ADR format. Use when a technical choice is made or needs making (a library, a database, a pattern, an API style).
parameters:
  - { name: decision, description: "The decision to record, or the question to decide" }
  - { name: number, description: "The ADR's number", required: false, default: "the next free number" }
---
Write ADR {{number}}: {{decision}} ($ARGUMENTS).

Use this form (Markdown, about one page):
- **Title**: "ADR-{{number}}: " and the decision as a short sentence.
- **Status**: proposed, accepted, superseded (by which) or deprecated; and the date.
- **Context**: the forces at play — requirements, constraints, what exists, what hurts today. Facts, not the answer.
- **Options**: two to four real options. For each: how it works here, pros, cons, cost and risk.
- **Decision**: the option chosen and the reasons that decided it.
- **Consequences**: what gets easier and harder, what must change now, what to watch, and when to revisit.

If the project has an ADR folder (docs/adr, docs/decisions), follow its numbering and template, and say where the file goes.
