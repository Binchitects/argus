# The management system (ISMS and AIMS)

> A template. Replace each [bracket], remove what does not apply to you,
> approve it and set its review date.

| | |
|---|---|
| Owner | [Information security officer] |
| Approved by | [Name, role, date] |
| Review | every year, after each management review |

ISO/IEC 27001 (information security), ISO/IEC 42001 (AI) and ISO 22301
(business continuity) share one structure, clauses 4 to 10: the system around
the controls. A certification audit reads this document first. One management
system can serve all three, with the scope of each said below. Arena keeps the
records listed here. Running the system is the company's part.

## Context and scope (4.1 to 4.4)

- **The organisation and its context**: [what the company does, its sites, the
  laws it falls under (the GDPR, HIPAA, the EU AI Act, PCI DSS if it takes cards),
  its contracts].
- **Interested parties and what they need**: [customers, staff, regulators,
  suppliers, owners], each with its requirements.
- **The scope**: [the Arena installation at [site], the people who run it, the
  services it gives to [teams]]. Out of scope: [the hosts' hardware, if a
  supplier runs it; models the company does not run].
- **For AI (42001)**: the AI systems in scope are the assistants, agents and
  apps people make in Arena and the models behind them. The company's role for
  each is provider, deployer or both.

## Leadership (5.1 to 5.3)

- **Commitment**: [the leadership] approves this system, gives it resources, and
  reviews it (9.3).
- **Policies**: the [information security policy](policies/information-security.md)
  and the [AI policy](policies/ai.md), with the topic policies under them.
- **Roles**: who owns the system, each policy, each risk and each control.
  In Arena, Admin → Access gives the roles' permissions, and Admin → Compliance
  records who attested each control's operator part.

| Role | Who | Arena's permission |
|---|---|---|
| Information security officer | [name] | Owner, or compliance.manage |
| AI system owner (42001 A.3.2) | [name] | assistants and agents they own |
| Data protection officer (GDPR 37) | [name] | compliance.read, data requests |
| Auditor (internal) | [name] | compliance.read, audit.read |

## Planning (6.1 to 6.3)

- **Risks and opportunities**: the [risk management policy](policies/risk-management.md)
  says how risks are assessed and treated (6.1.2, 6.1.3, and 42001's AI risk).
- **The Statement of Applicability** (27001 6.1.3 d): each Annex A control,
  whether it applies, why, and how far it is in place. Arena's part of each is in
  [the controls](controls.md) and the [ISO 27001 page](frameworks/iso27001.md).
- **AI system impact assessment** (42001 6.1.4): for each AI system, the
  effects on the people it touches. Use the [DPIA template](templates/dpia.md)
  where personal data is involved.
- **Objectives** (6.2): measurable, and checked. Arena's live checks
  (Admin → Compliance) measure several, for example: every admin with two-factor,
  the audit chain unbroken, a backup less than a day old, data requests answered
  within 30 days, access reviews done each quarter.
- **Changes to the system** (6.3): planned and recorded, as the
  [change management policy](policies/change-management.md) says.

## Support (7.1 to 7.5)

- **Resources and competence**: [who runs Arena, their training].
- **Awareness**: everyone accepts the acceptable use notice at sign-in (GOV-02),
  and its version and their acceptance are kept.
- **Communication**: [what is told to whom, when, by whom].
- **Documented information**: these documents are under version control
  (`docs/compliance/`, or the company's copy). Arena's controls, tests and checks
  are in `controls.yaml`, and their evidence comes from Admin → Compliance →
  Download evidence.

## Operation (8.1 to 8.4)

The controls run as [the controls](controls.md) say, and the risks are
assessed and treated on the dates the register sets. For continuity
(22301 clause 8): the business impact analysis and the plans are in the
[business continuity policy](policies/business-continuity.md), and Arena's
backups are restored in drills (OPS-01).

## Performance evaluation (9.1 to 9.3)

- **Monitoring and measurement**: the objectives above, from Admin → Compliance's
  checks, each month.
- **Internal audit** (9.2): a programme covering every clause and control over
  [three years], each audit by someone who does not audit their own work. Each
  finding is recorded with its nonconformity and its corrective action.
- **Management review** (9.3), [twice a year]. Its inputs are the status of
  actions from the last review, changes inside and outside, nonconformities and
  corrective actions, the results of monitoring and audits, the interested
  parties' feedback, the risks and their treatment, and opportunities to
  improve. Its results are decisions and changes, kept as minutes.

## Improvement (10.1, 10.2)

A nonconformity is recorded with what happened, its root cause, the
correction, the corrective action, who owns it, by when, and the check that it
worked. Security incidents follow the [incident response runbook](incident-response.md).

## Records

The scope, the SoA, the risk register, the impact assessments, the objectives
and their measurements, the audit programme and reports, the management
reviews' minutes, and the nonconformities with their actions. Kept [six years].
