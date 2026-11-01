---
name: threat-model
title: Threat model
description: Model the threats to a system or feature with STRIDE — what it protects, who could attack it and how, the controls in place, and what to add, ranked by risk. Use before building or changing something that handles data, money, identities or access.
parameters:
  - { name: system, description: "The system, feature or change to model", required: false, default: "the system or change in this conversation" }
---
Make a threat model of {{system}} ($ARGUMENTS).

1. **Scope**: what it does, its components, and the data it holds or moves, each with how sensitive it is.
2. **Trust boundaries and data flows**: who and what talks to what, across which boundary (the internet, the internal network, a browser, a third party). A short Mermaid diagram when it helps.
3. **Threats by STRIDE**, per component or flow: Spoofing, Tampering, Repudiation, Information disclosure, Denial of service, Elevation of privilege. For each: the attacker, how, and what it would cost.
4. **Controls**: what already stands against each (name the code or setting when you can see it), and what is missing.
5. **Risks**: a table of threat, likelihood, impact, risk (high, medium, low), existing control, and the action to take, highest risk first.

Say what you assumed and what you could not see. Prefer concrete, checkable actions (a test, a setting, a review) over general advice.
