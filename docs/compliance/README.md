# Compliance: ISO 27001, SOC 2, HIPAA and the GDPR

Arena is built so that a company running it can meet four frameworks:

| Framework | What it is | The part Arena covers |
|---|---|---|
| **ISO/IEC 27001:2022** | an information security management system, certified by an auditor | the technical controls of Annex A (5.15–5.18, 5.28, 5.33–5.34, 8.2–8.5, 8.10–8.16, 8.20–8.26, 8.29, 8.32) |
| **SOC 2** | an auditor's report on the Trust Services Criteria (2017, points of focus revised 2022) | the common criteria CC6–CC9, availability A1, confidentiality C1 and privacy P4–P5 |
| **HIPAA** | the US rules for health information: the Security Rule (45 CFR 164.308–316) and the Privacy Rule | the technical safeguards of 164.312, the parts of the administrative safeguards of 164.308 a system can do, and documentation kept as 164.316 asks |
| **The GDPR** | the EU's rules for personal data (Regulation 2016/679) | security of processing (Article 32), privacy by design (25), the people's rights (15–22), storage limitation (5(1)(e)) and accountability (5(2)) |

A framework is met by an organisation, not by a program. Arena gives the
controls, the evidence and the documents; the company that runs it does the
rest, and an auditor checks both.

## Shared responsibility

| | Arena does | The company running it does |
|---|---|---|
| **People and access** | accounts, roles and rules, two-factor, company sign-in (OIDC, SAML, LDAP), SCIM, sessions that end | decides who gets what, joins and leavers, reviews access, trains people |
| **Records** | the audit log, the logs, retention, legal hold, a person's data exported | keeps the policies, decides the periods, answers requests in time |
| **Data** | personal data masked before a model, secrets encrypted, sandboxes, safeguards on answers | classifies its data, decides which models may see what, signs contracts (a HIPAA BAA with anyone else who touches health data, a GDPR data processing agreement) |
| **Infrastructure** | TLS at the edge, only ports 80 and 443 published, backups with restore drills, monitoring and alerts | the hosts, their disks' encryption, physical security, the network around them, the certificate |
| **Change** | tested releases, a changelog, rollback by backup | approves upgrades, keeps its own change records |

Arena runs on your hardware, inside your network: no data goes to its makers.
Models outside the company (a cloud API added to the gateway) are a transfer
of data the company decides on.

## Controls and how they are kept

Each control Arena provides is in [`controls.yaml`](controls.yaml): its id
(`IAM-01`), what meets it, the frameworks' clauses it serves, where it is
documented, the tests that prove it, the live checks that watch it, and the
operator's part. The same file drives Admin → Compliance.

Nothing in it may go stale:

- **The guard test** (`ComplianceControlsTests`, in every test run) fails when
  a control names a test that does not exist, a document section that is gone,
  or a live check nobody runs, or when a framework's clause is written wrong.
- **Each test that proves a control carries its id** as an xUnit trait,
  `[Trait("Control", "IAM-01")]`, and the guard checks both ways: every test a
  control names carries it, and every test that carries it is named. Run one
  control's tests with `tools/dn test --filter Control=IAM-01`.
- **Live checks** run inside the app (`app:` checks, shown in Admin →
  Compliance and by `GET /api/admin/compliance`) and from outside
  (`live:` checks, by [`deploy/scripts/compliance-check.py`](#checking-a-live-stack)).

## Checking a live stack

```bash
python3 deploy/scripts/compliance-check.py
```

It reads the admin's password from `deploy/.env` (never printing it), then
checks from outside what a browser sees: TLS 1.2 or later and nothing older,
the security headers, the session cookie's flags and the guard against
cross-site requests. It prints the app's own checks beside them and exits
non-zero when any of them fails. `--strict` fails on warnings too. A release
runs it against the stack it was deployed to (see the release checklist in
[development.md](../development.md#releasing)).

Each check is green (met), amber (met in part, or a choice the company should
make knowingly, such as chats kept forever) or red (not met). An amber check
says what to change.

## When a change weakens a control

A release that makes a control weaker (a shorter list of what is audited, a
default made looser, a check removed) says so under **Weakened controls** in
its [changelog](../../CHANGELOG.md) entry, naming the control's id and why. The
guard test fails first, so nobody weakens one by accident.
