# Risk management policy

> A template. Replace each [bracket], remove what does not apply to you,
> approve it and set its review date.

| | |
|---|---|
| Owner | [Information security officer] |
| Approved by | [Name, role, date] |
| Review | every year, and when something significant changes |

## Purpose

[Company] knows the risks to the information it handles in Arena, decides on
each, and checks that what it decided is done. This is the risk analysis and
risk management HIPAA asks for (164.308(a)(1)) and the risk assessment of
ISO 27001 (clause 6.1) and SOC 2 (CC3).

## The asset inventory

Kept by [owner] and reviewed every [year]. At least:

| Asset | Owner | What it holds | Class | Where |
|---|---|---|---|---|
| The Arena host(s) | [IT] | the stack, its data and backups | confidential | [site, rack] |
| Arena's database (Postgres) | [admin] | people, chats, files, settings, the audit log | confidential, personal, [health] | the `postgres` volume |
| Models | [admin] | model weights | internal | `MODELS_DIR` |
| Backups | [admin] | everything above, encrypted | confidential | [backup location] |
| [Company sign-in, GitLab, ...] | | | | |

[Data flows](../data-flows.md) say where people's data goes inside the stack.

## The risk register

Kept in Arena, under Admin → Compliance → Risks ([how](../README.md#risks)).
Each risk: what could happen, to which asset, how likely (1–5), how bad (1–5),
the owner, the treatment (reduce, avoid, transfer, accept), the controls that
reduce it ([Arena's](../controls.md) and [Company]'s), the risk left after
them, and the date it is next looked at. Management accepts the risks left.

Start from these, which apply to most installations:

| Risk | Arena's controls | Yours |
|---|---|---|
| An account is taken over | IAM-01, IAM-02, IAM-03, MON-02 | two-factor for everyone, training |
| An admin misuses their access | IAM-05, IAM-07, LOG-01, LOG-03, LOG-04 | segregation of duties, the SIEM watched |
| Personal or health data reaches a model outside the company | DAT-04, DAT-08 | which models may see what; contracts |
| A model's answer leaks another person's data | IAM-05 (knowledge readers), DAT-04 | |
| The host's disk is stolen | DAT-05 | disk encryption, physical security |
| Data is lost | OPS-01 | an off-site copy of the backups, restore drills |
| A vulnerable package ships | GOV-03 | upgrades applied within [days] |
| Code a model writes escapes | OPS-02 | the host patched |

## Threat intelligence

[Who] follows [sources: the vendor's advisories, CERT, OSV] and adds what
applies to the register; Arena's release scan checks its packages against OSV.

## Records

The register, each version kept, and management's acceptance of the risks left.
Kept six years.
