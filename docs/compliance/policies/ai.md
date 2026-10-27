# AI policy

> A template. Replace each [bracket], remove what does not apply to you,
> approve it and set its review date.

| | |
|---|---|
| Owner | [AI system owner, or the information security officer] |
| Approved by | [Name, role, date] |
| Review | every year, and when a law, a model or a use changes |

## Purpose

[Company] uses AI to [purposes], responsibly and lawfully. This is the AI
policy of ISO/IEC 42001 (5.2, A.2), the governance of NIST's AI RMF (Govern),
and the basis for the duties of the EU AI Act that apply to [Company] as a
[provider or deployer].

## What it covers

Arena's models (Admin → Models), the assistants, agents and apps people make
in it, the knowledge they read, and models outside the company that the
gateway reaches.

## Principles

- **Purpose**: each AI system has an owner, a purpose, the people it is for,
  and an intended use that is written down (42001 A.9.4).
- **Classification under the AI Act**: no prohibited practice (Article 5). A
  system in an area of Annex III (hiring, credit, education, essential
  services, law enforcement, and the rest) is high-risk and is not put into use
  until its requirements are met (Articles 9 to 15, 26 and 27).
- **People told**: people know when they are dealing with an AI, and generated
  pictures, video and sound are marked as such (Article 50).
- **Human oversight**: [who] can stop or override each system. Agents that act
  (send, merge, pay) ask a person first, as Arena's approvals let them.
- **Data**: the data a system reads is chosen for its purpose. Personal and
  health data stay in the bounds the safeguards set (DAT-04, DAT-08), and models
  outside the company see only what the company allows.
- **Evaluation**: a model or assistant is measured on its task before people
  rely on it (42001 A.6.2.4, AI RMF Measure), and again when it changes.
  Arena's measurements are in `docs/measurements/`.
- **Records**: what AI systems did is in the audit log and the traces
  (42001 A.6.2.8; the AI Act's Article 12 for a high-risk system).
- **Concerns**: anyone can report a concern about an AI system to [address],
  without blame (42001 A.3.3).
- **Literacy**: the people who use or run AI here know enough of what it does
  and fails at (Article 4): [training].

## Impact assessment

Before a system goes into use, and when it changes, its owner assesses its
effects on the people it touches (42001 6.1.4, A.5). This includes the
assessment of fundamental rights where Article 27 asks for it. The
[DPIA template](../templates/dpia.md) serves where personal data is involved.

## Suppliers

A model from outside the company is a supplier ([supplier policy](suppliers.md)):
what data it gets, where, under which terms, and what its maker says of its
evaluation (42001 A.10.3).

## Records

The register of AI systems with their owners, purposes and classes; the
impact assessments; the evaluations; the concerns raised and what was done.
Kept [six years], or ten for a high-risk system's technical documentation
(Article 18).
