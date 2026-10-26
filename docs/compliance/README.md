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

## The Compliance center

**Admin → Compliance** (the Owner and the **Compliance officer** role; the
**Auditor** role reads it) shows, for each framework, the controls that serve
it with how many are met now, and for each control:

- what meets it in Arena, and the clauses it serves;
- its checks, green, amber or red, each saying what it found and what to
  change: passwords and lockout, sessions, admins' second factor, the audit
  log, how long chats and logs are kept, the newest backup, the certificate,
  secrets encrypted at rest;
- where it is documented (links into this manual) and the tests that prove it;
- **the company's part**, and **Attest**: who says it is done, what was done and
  where its proof is. An attestation is kept for good with the person's name and
  the date, is in the audit log (`compliance.attest`), and counts for a year;
  then the control's part is due again.

**Download evidence** gives one zip for the auditor: the controls, every
check's result, every attestation, the settings (a secret only as set or not),
who holds an administrative permission (how they sign in, whether they have a
second factor, when they last signed in), and the audit log's last 90 days
counted by action. Each download is audited (`compliance.evidence`).

## Access reviews

An access review lists, for a reviewer to **Keep** or **Revoke**:

- every administrative role given to a person, a group or everyone (the first
  admin, `ADMIN_USERNAME`, is listed and always stays an Owner: the way back in);
- each member of a group that holds one (a directory's or SCIM's group says to
  change it at its source);
- each account nobody signed in to for longer than **Settings → Compliance →
  An account is unused after** (90 days unless set).

Revoking takes the role away, removes the member, or disables the account, at
once and audited. A review is closed when every item is decided, and is kept
for good with each decision: who, when, and what came of it. The evidence
download carries every review as `access-reviews.csv`.

A review starts by itself every **Review access every** days (90 unless set;
0: only by hand), due two weeks later, and the admins are told. **Disable
unused accounts** disables accounts unused that long every day, never the
first admin. The check **Privileged access was reviewed** is green while the
last review was closed within the period.

## People's data requests

A person asks from **Your account → Your data** for one of: a copy of their
data, their data to take elsewhere, a correction, their data's processing
restricted, an objection to its processing, or their data erased; an admin with
`privacy.manage` (the Compliance officer role has it) can take one in for them
from Admin → Compliance. Each request is due a month after it comes in (GDPR
Article 12(3)); the admins are told when one comes in, the person when it is
answered, and every step is in the audit log (`privacy.*`). The check
**People's data requests answered in time** is red while one is past its month.

- **A copy, or to take elsewhere**: their export (Your account → Your data, or
  the admin's export on their page) is everything of theirs, as JSON and
  Markdown, with their files. The admin says what was given, and answers it.
- **A correction**: made on their profile (Admin → People), then answered.
- **Restriction or objection**: **Restrict** keeps their data but stops
  everything that learns from it: their memories, chat digests, reflection and
  skills learned from their work (in the chat and from Code Arena), until it is
  lifted.
- **Erasure**: **Erase everything** removes the person and everything of theirs
  (chats with every message and digest, files, memories, skills, meetings,
  media, apps, decks, workflows, tasks, plans, tool calls, connected accounts,
  ...), erases their words from other people's rooms (keeping the place), takes
  their name off others' meeting actions, and clears them out of the gateway's
  request log (its totals stay, no longer theirs). It is refused while they are
  on legal hold. The **certificate** lists what was erased and how much, what is
  kept and why (the audit log's entries naming them: the law requires them and
  the chain forbids changing them), and when the backups taken before let go of
  the rest.

## How long things are kept

Admin → Compliance lists how long each kind of data and record is kept, and
what removes it:

| What | Kept | Set in |
|---|---|---|
| Chats and their files | until deleted, or the days set (a group can keep its own) | Settings → Data retention → Keep chats for |
| The audit log | 6 years, then only its oldest entries go, so the chain stays whole | Keep the audit log for |
| The gateway's request log | 400 days | Keep the gateway's request log for |
| The record of tool calls | 180 days | Keep the record of tool calls for |
| News in the bell | 180 days | Keep news in the bell for |
| The services' logs (Loki) | 90 days | `retention_period` in `config/loki.yml` |
| Metrics (Prometheus) | 30 days, or 20 GB | its `--storage.tsdb.retention` flags in `docker-compose.yml` |
| Backups | the newest `BACKUP_KEEP` | `.env` |

The hourly sweep removes what is past its time, and audits each removal with
counts (`retention.delete`, `retention.records`). Nothing of a person on legal
hold is removed. The checks **Chats are kept for a set time**, **Records are
kept for set times** and **Logs are kept for a set time** say what is kept
forever, and the audit log kept less than the six years HIPAA asks.

## The audit log as a chain

Each entry of the audit log carries a SHA-256 hash of its fields and of the
hash of the entry before it. Every writer, on every replica, seals its entry
under one database lock, so the entries make one chain in the order of their
ids. Changing an entry, removing one or slipping one in breaks the chain from
there, and walking it finds the first entry that no longer follows.

Every day the app walks the whole chain. When it holds, the newest entry's id
and hash are kept as an anchor, apart from the database: a line of
`anchors.jsonl` in the `audit` volume (`DATA_DIR/audit`) and a line in the
app's log, which Loki keeps. An anchor that no longer matches shows a chain
rewritten from that point on, which the walk alone cannot. When the chain is
broken, every admin is told (the bell, and email and the alerts webhook when
set up), the break is in the audit log (`audit.chain_broken`), and the check
**Nobody changed the audit log unseen** is red. **GET
/api/admin/compliance/audit-chain** walks it on demand, and **POST
/api/admin/compliance/audit-chain/anchor** anchors it now, before an audit
for example. The evidence download carries both.

Entries written before the chain existed are sealed, oldest first, the first
time the app starts after the upgrade. The oldest entry kept is the chain's
start: when old entries age out, the walk starts from the oldest one left.

## Sending it to the SIEM

**Settings → Audit log** sends every entry on to the company's SIEM as it is
written, in order and each once:

- **Send it to syslog**: `udp://host:514`, `tcp://host:514` or
  `tls://host:6514` (the collector's certificate is checked). Each entry is an
  RFC 5424 message, facility *log audit*, informational or (a failure) warning,
  whose text is the entry as JSON with its hash. Over TCP and TLS each message
  is octet-counted (RFC 6587).
- **Post it to a webhook**: an `https://` address, given the entries as a JSON
  array in batches of up to 200. With **The webhook's signing key** (make one
  with `openssl rand -hex 32`), each post carries `X-Arena-Signature: sha256=`
  and the HMAC-SHA256 of its body, in hex.

How far it got is kept in the database, so a restart or another replica goes
on from there; one replica sends at a time. A collector that does not answer
is tried again, later each time (up to every five minutes), and nothing is
skipped. The check **The audit log is sent on to the SIEM** says how far it got
and, when the last try failed, why.

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
