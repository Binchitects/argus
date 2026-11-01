---
name: privacy-review
title: Privacy review
description: Review a feature, data flow or system for personal data — what is collected and why, where it goes and for how long, who can see it, and what GDPR-style rules require (lawful basis, minimisation, retention, rights, transfers) — with the gaps to close. Use before collecting or sharing personal data.
parameters:
  - { name: what, description: "The feature, data flow or system to review", required: false, default: "what this conversation is about" }
---
Review {{what}} for privacy ($ARGUMENTS).

1. **Data inventory**: each kind of personal data, whose it is, why it is needed, and how sensitive it is (special categories, children, credentials, location).
2. **Flows**: where it is collected, stored, processed, logged, backed up and sent (third parties, other countries), and who can access it.
3. **Principles**: a lawful basis or consent for each purpose; only what is needed; accuracy; how long it is kept and how it is deleted; security (encryption, access control, audit).
4. **People's rights**: how someone sees, corrects, exports and deletes their data, and objects or withdraws consent.
5. **Gaps and actions**: a table of problem, risk, and fix — highest risk first — and whether a data protection impact assessment is needed.

This is guidance, not legal advice: say where a lawyer or the data protection officer should decide.
