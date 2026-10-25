---
name: explain-code
title: Explain code
description: Explain what code does and why, for a given reader — its purpose, how it flows, the important details and the traps. Use when asked how something works, what a piece of code does, or to onboard someone to code.
parameters:
  - { name: code, description: "The code to explain: a file, a function, a module", required: false, default: "the code in this conversation" }
  - { name: reader, description: "Who it is for", required: false, default: "a developer new to this codebase" }
---
Explain {{code}} for {{reader}}.

- Start with its purpose in one or two sentences: what problem it solves and where it sits.
- Then how it works, in the order it runs: the main path first, then the branches that matter. Cite `file:line` for each step.
- Point out what is not obvious: assumptions, side effects, concurrency, error handling, performance traps, and why it is done this way when the code says.
- Define the domain words it uses.
- End with what to read next to understand more.

Use plain language and short sections. Include a small diagram (Mermaid) when the flow has more than a few steps.
