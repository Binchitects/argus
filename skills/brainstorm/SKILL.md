---
name: brainstorm
title: Brainstorm
description: Generate many varied ideas for a problem, then sort them and pick the most promising with reasons. Use for naming, product ideas, solutions to an open problem, or "what could we do about…".
parameters:
  - { name: topic, description: "What to brainstorm about" }
  - { name: constraints, description: "Limits to respect: budget, time, audience, tone", required: false, default: "none given" }
---
Brainstorm about {{topic}}. Constraints: {{constraints}}.

1. **Diverge**: list at least 12 ideas, as different from each other as you can (the obvious ones, the opposite ones, ones borrowed from other fields, a bold one). One line each.
2. **Converge**: group them, and rate the strongest five on value, effort and risk in a small table.
3. **Recommend** one or two, with why, and the first concrete step for each.

Ask one question first only if the topic is too vague to start.
