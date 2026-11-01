---
name: design-doc
title: Design document
description: Write a technical design document for a feature or system — goals and non-goals, the design with its interfaces and data, alternatives, rollout, risks and open questions — grounded in the existing code. Use before building something that spans several components or teams.
parameters:
  - { name: feature, description: "What is being designed" }
---
Write a design document for {{feature}} ($ARGUMENTS).

1. **Summary** in a paragraph, then **goals** and **non-goals** (what it deliberately does not do).
2. **Background**: what exists today (read the code it touches) and why it is not enough.
3. **Design**: the components and how they interact (a Mermaid diagram), the APIs (requests, responses, errors), the data model and migrations, and the key algorithms. Concrete enough to build from.
4. **Alternatives considered** and why each was not chosen.
5. **Cross-cutting**: security and privacy, performance and scale (with numbers), observability (logs, metrics, alerts), failure modes and recovery, accessibility where there is a UI.
6. **Rollout**: steps, feature flags, data migration, backward compatibility, how to roll back; and the **test plan**.
7. **Risks and open questions**, each with an owner when known.

Keep it as short as the change allows. Mark assumptions.
