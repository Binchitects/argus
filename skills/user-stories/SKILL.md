---
name: user-stories
title: User stories
description: Turn a need or a feature idea into user stories with acceptance criteria (Given, When, Then), split small enough to deliver, with edge cases and questions. Use for planning, backlog grooming, or writing tickets.
parameters:
  - { name: idea, description: "The need, feature or epic" }
---
Write user stories for: {{idea}} ($ARGUMENTS).

For each story:
- "As a [who], I want [what], so that [why]" — a real role, not "the user" when it can be more precise.
- **Acceptance criteria** as Given / When / Then, covering the main path, edge cases and errors.
- **Notes**: dependencies, design or data needs, and what is out of scope.
- A rough size (S, M, L) and why.

Split stories so each delivers value on its own and fits in a few days (INVEST). Order them so the first ones make a usable slice. End with the open questions for the product owner.
