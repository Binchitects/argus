---
name: security-review
title: Security review
description: Look for security weaknesses in code, configuration or a design (injection, broken access control, secrets, unsafe defaults), each traced from where outside data comes in to where it is used, cited by file and line, and grounded in the security pack's CWE and OWASP pages. Use when asked about security, vulnerabilities, OWASP, a CWE, or before shipping something exposed.
parameters:
  - { name: scope, description: "What to review: a repository, files, a feature, a service, a config", required: false, default: "the code and configuration in this conversation" }
---
Do a security review of {{scope}}. Work from the code, not from memory: a finding you cannot cite is not a finding.

1. **Find the entry points**: routes, handlers, controllers, message consumers, CLI commands, webhooks, file uploads. With Argus: `overview` and `repo_map` for the shape, `search_code` or `grep_at` for the framework's route attributes and handlers.
2. **Find the sinks**, where data is used dangerously: SQL and NoSQL queries, OS commands, file paths, URLs the server fetches (SSRF), HTML or Markdown rendered (XSS), deserialisers, XML parsers (XXE), redirects, code run from text, templates. `grep_at` finds them by pattern at any branch.
3. **Trace each sink back** with `find_references` and `get_file` (or `file_at`): who calls its function, and does data from an entry point reach it without what it needs (a parameter, an allow-list, encoding, a safe parser setting)? Data the program made itself, constants and configuration are not outside data.
4. **Ground each suspicion in the security pack** before you report it: `docs_lookup` the weakness (`CWE-89`, `lang: "cwe"`) and read its mitigations; `docs_search` the fix in the OWASP cheat sheets (`lang: "cheatsheets"`), CodeQL's query for the language (`lang: "codeql-csharp"`, `"codeql-python"`...) and, for a web application, the ASVS requirement (`lang: "asvs"`). Drop any suspicion whose sink line you cannot quote from the file, or whose weakness the pack does not name.
5. **Check the rest** as an attacker would: who may do what (every route's authorisation, objects reached by id without an owner check, tenant separation); secrets in code, history, logs or client bundles; unsafe defaults (debug on, permissive CORS, missing TLS or security headers, containers as root); dependencies with known vulnerabilities; personal data logged or kept too long.

For each finding give, worst first:
- the CWE and its name (from the pack), and severity (critical, high, medium, low) with a one-line reason;
- `file:line` of the sink, with the line quoted exactly;
- where the outside data comes in, and each step to the sink;
- how it could be exploited;
- the fix, as the cheat sheet and the CWE's mitigations describe it (a parameterised query, not an escaped string), with code when short;
- the pack's pages it rests on.

End with what you did not cover, and what to check where the code shown did not settle it. Do not invent findings: when unsure, say what to check. For a whole repository, Code Studio → Security runs this review on every sink and keeps the findings to triage.
