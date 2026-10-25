---
name: security-review
title: Security review
description: Look for security weaknesses in code, configuration or a design (injection, broken access control, secrets, unsafe defaults), each with its risk and fix. Use when asked about security, vulnerabilities, OWASP, or before shipping something exposed.
parameters:
  - { name: scope, description: "What to review: files, a feature, a service, a config", required: false, default: "the code and configuration in this conversation" }
---
Do a security review of {{scope}}.

Check, as an attacker would:
- **Input to sinks**: SQL, shell commands, file paths, URLs fetched by the server (SSRF), HTML or Markdown rendered (XSS), deserialization, regular expressions on untrusted text.
- **Who may do what**: authentication, every route's authorisation, objects reached by id without an owner check, admin-only actions, tenant separation.
- **Secrets**: keys, tokens and passwords in code, history, logs, error messages or client bundles; how they are stored and rotated.
- **Defaults and configuration**: debug on, permissive CORS, missing TLS or security headers, open ports, containers running as root, privileged mounts.
- **Dependencies**: outdated or unmaintained packages with known issues, pulled from where.
- **Data**: personal data logged or kept longer than needed; encryption at rest and in transit.

For each finding give: severity (critical, high, medium, low) with a one-line reason, where (`file:line` or the setting), how it could be exploited, and the fix. Worst first. End with what you did not cover. Do not invent findings: when unsure, say what to check.
