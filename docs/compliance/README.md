# Compliance: the frameworks and the certifications

Arena is built so that a company running it can meet these frameworks, and be
certified or audited against them. Each has a page, clause by clause: its
clauses, Arena's controls that serve each, and the company's document that
governs it.

| Framework | What it is | Its page |
|---|---|---|
| **ISO/IEC 27001:2022** | an information security management system, certified by an auditor: clauses 4 to 10 (the system) and Annex A (the controls) | [clause by clause](frameworks/iso27001.md) |
| **SOC 2** | an auditor's report on the Trust Services Criteria (2017, points of focus revised 2022), Type I or over a period (Type II) | [criterion by criterion](frameworks/soc2.md) |
| **NIST CSF 2.0** | the Cybersecurity Framework's six functions, their categories and subcategories | [subcategory by subcategory](frameworks/nistcsf.md) |
| **NIST SP 800-53 Rev. 5** | the federal control catalog, with its low, moderate, high and privacy baselines (FedRAMP's build on them) | [control by control](frameworks/nist80053.md) |
| **NIST SP 800-171 Rev. 3** | protecting controlled unclassified information (CMMC builds on it) | [requirement by requirement](frameworks/nist800171.md) |
| **PCI DSS v4.0.1** | the card industry's standard: Arena keeps card numbers out (the safeguards block them), which keeps it out of the cardholder data environment | [requirement by requirement](frameworks/pcidss.md) |
| **CSA STAR (CCM v4)** | the Cloud Security Alliance's Cloud Controls Matrix, by its 17 domains | [domain by domain](frameworks/csaccm.md) |
| **HIPAA** | the US rules for health information: the Security Rule (45 CFR 164.308–316) and the Privacy Rule | [section by section](frameworks/hipaa.md) |
| **The GDPR** | the EU's rules for personal data (Regulation 2016/679) | [article by article](frameworks/gdpr.md) |
| **ISO/IEC 42001:2023** | an AI management system, certified as ISO 27001 is: clauses 4 to 10 and Annex A's 38 controls | [clause by clause](frameworks/iso42001.md) |
| **The EU AI Act** | Regulation (EU) 2024/1689: AI literacy, prohibited practices, high-risk systems, transparency | [article by article](frameworks/euaiact.md) |
| **NIST AI RMF 1.0** | the AI Risk Management Framework: Govern, Map, Measure, Manage | [subcategory by subcategory](frameworks/airmf.md) |
| **ISO 22301:2019** | a business continuity management system | [clause by clause](frameworks/iso22301.md) |
| **Common Criteria (ISO/IEC 15408)** | a product's evaluation by an accredited laboratory: the functional and assurance families ([product evaluation](product-evaluation.md), with FIPS 140-3) | [family by family](frameworks/commoncriteria.md) |

The clauses of NIST's frameworks, the AI Act's articles and the Common
Criteria's families come from their published sources
([catalogs](catalogs/), written by `tools/grc-catalogs.py`, each with its
source's address, checksum and licence). ISO's, PCI DSS's and the CCM's texts
are not shared: only their ids, with short titles written here.

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

## The documents

What a company needs beside the software, here to adapt:

- **[The controls](controls.md)**: every control as an auditor reads it, with
  the clauses it serves, the tests that prove it and the company's part.
- **Each framework, clause by clause** (the table above): every clause, Arena's
  controls that serve it, and the company's document that governs it.
- **[The management system](isms.md)**: clauses 4 to 10 of ISO 27001, 42001
  and 22301, which an audit reads first: scope, roles, objectives, internal
  audit, management review, corrective action.
- **[Product evaluation](product-evaluation.md)**: what a Common Criteria
  evaluation of Arena would rest on, and FIPS 140-3.
- **[Data flows](data-flows.md)**: where people's data goes inside the stack,
  and the flows out of the company that an admin turns on.
- **[The incident response runbook](incident-response.md)**: from noticing to
  closure, with the register's steps and the law's clocks.
- **Templates**: the [record of processing](templates/record-of-processing.md)
  (GDPR Art. 30), filled in with what Arena processes, and a [data protection
  impact assessment](templates/dpia.md) (Art. 35).
- **Policies**, templates to replace the brackets of and approve:
  [information security](policies/information-security.md),
  [risk management](policies/risk-management.md),
  [access control](policies/access-control.md),
  [acceptable use](policies/acceptable-use.md),
  [people security](policies/people-security.md),
  [physical security](policies/physical-security.md),
  [backup and business continuity](policies/business-continuity.md),
  [data protection and retention](policies/data-protection.md),
  [suppliers and transfers](policies/suppliers.md),
  [change and vulnerability management](policies/change-management.md),
  [cryptography](policies/cryptography.md),
  [logging and monitoring](policies/logging-monitoring.md),
  [AI](policies/ai.md).

The controls and the framework pages are written from `controls.yaml` and
[`frameworks.yaml`](frameworks.yaml) (each framework's clauses and the
document for each) by `python3 tools/compliance-docs.py`. Change those two and
run it; the deployment tests fail while the pages are not current, while a
control cites a clause the catalog does not list, or when a document is gone.

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

## Risks

Admin → Compliance → Risks is the risk register ISO 27001 (6.1.2, 6.1.3,
8.2, 8.3), ISO 42001 (AI risks), SOC 2 (CC3) and HIPAA (164.308(a)(1)) ask for
(GOV-04). Each risk has:

- **what could happen, to what**: its category (security, privacy, AI,
  continuity, supplier, legal), the assets it threatens, chosen from what
  Arena knows (its database, files, backups, audit log, models, knowledge
  sources, data sources, tools) or named, and the threat;
- **how likely and how bad**, each 1 to 5, before treatment and after it. The
  score is their product: low 1 to 4, medium 5 to 9, high 10 to 16, critical
  20 to 25. A heat map shows them all;
- **its owner and treatment**: reduced with Arena's controls (named, so each
  control's page shows the risks it treats), accepted, transferred or avoided.
  An acceptance is made by the person who decides it, with why and until when,
  a year at most. Then the risk is decided again;
- **the treatment's actions**, each with an owner and a date;
- **a review date its level sets**: three months for critical, six for high,
  a year otherwise (the level left after treatment, once assessed). A review
  says what it found and sets the next.

Every change, review, acceptance and action is in the risk's history and the
audit log. The register and its history are in the evidence (`risks.csv`,
`risk-history.csv`). The `risks` check is amber while the register is empty
or a risk is past its review, its acceptance or its actions' dates, and red a
month past a review. The [risk management policy](policies/risk-management.md)
starts the register with the risks most installations share.

## Statement of Applicability

Admin → Compliance → Statement of Applicability keeps ISO 27001's (6.1.3 d)
and ISO 42001's (6.1.3) statements (GOV-05). For each Annex A control:

- **whether it applies**, and why not when it does not (an exclusion needs its
  reason);
- **how far it is in place**: implemented, in part, planned, or not;
- **Arena's controls that serve it**, with their state now, and the open
  risks those controls treat, so the statement shows why each control is there.

A statement is approved as a whole, by the person who does it, once every
control is decided. Each approval is a version kept as it stood, with who and
when, and can be downloaded again. The statement as it stands, its versions
and the approvals are in the evidence (`soa-iso27001.csv`, `soa-iso42001.csv`,
`soa-approvals.csv`). The `soa` check is amber until ISO 27001's is approved,
when it changes after its approval, and a year after it (ISO 42001's too, once
the company starts one).

## Policies

Admin → Compliance → Policies keeps the company's policies (GOV-06). Adopt
one of the templates (the policies in this folder and [the management
system](isms.md)), or write one. Each has:

- **an owner**, and **who must read it**: everyone who can sign in, the
  admins, or the members of chosen groups;
- **versions**: a draft is edited until a person approves it, and is then in
  force. A later version says what changed, and the earlier ones stay as they
  were;
- **a review**, due after the period the policy sets (a year by default). A
  review that keeps it sets the next. A policy past its review is amber, and
  red a month past;
- **acknowledgements**: each reader is told in their bell when a version
  comes into force, reads it under Your account → Policies to read, and says
  so. Admin → Compliance shows who has not.

Every step is in the audit log. Each version, its approval and review, the
acknowledgements and the texts in force are in the evidence (`policies.csv`,
`policy-acknowledgements.csv`, `policies/`). The `policies` check is amber
until one is in force, when one is past its review, or when one is not read by
everyone a month after it came into force.

## Audits, findings and certificates

Admin → Compliance → Audits keeps the audit programme (GOV-07):

- **audits**: internal, or an external body's (a certification's stage 1 and
  2, surveillance, recertification, a SOC 2 period, an assessment), each with
  its framework, scope, auditor, dates, and the conclusion once reported;
- **findings**: a major or minor nonconformity, an observation or an
  opportunity, from an audit or found otherwise, against a framework's clause
  (`iso27001 A.8.5`) and Arena's control, with its correction, root cause,
  corrective action, owner and date. A nonconformity's action is marked taken
  only once its root cause and action are said; a finding is closed only when
  someone checks that the action worked, and opens again when it did not;
- **certificates**: the certificates and reports the company holds, with their
  scope and when they end;
- **waivers**: a control not met for a time, approved by a person, with why,
  what makes up for it, and until when (a year at most).

Every step is in the audit log, and all of it is in the evidence
(`audits.csv`, `findings.csv`, `certificates.csv`, `waivers.csv`). The `audits`
check is amber without an internal audit in the year, with a finding past its
date, a certificate ending within 90 days or a waiver past its end; red for a
major nonconformity past its date or a certificate that has ended.

## Suppliers

Admin → Compliance → Suppliers is the supplier register (GOV-08). Each
supplier has what it provides; the kinds of data it receives (personal,
health, confidential, code, payment) and where; its certifications and
reports, with their dates; the agreements signed (a DPA, a HIPAA BAA, standard
contractual clauses); its risk; and a review its risk sets (six months for
high, a year for medium, two years for low). A supplier that gets personal or
health data with no agreement said, or health data without a BAA, is flagged.

The services Arena connects to are offered for the register until they are in
it: the cloud model servers (Admin → Models), tool servers, knowledge
connectors, the GitLab Argus reads, and Hugging Face. The register is in the
evidence (`suppliers.csv`). The `suppliers` check is amber while such a
service is missing, a review is past due, or an agreement is.

## AI systems

Admin → Compliance → AI systems is the register of the company's AI systems
that ISO/IEC 42001, the EU AI Act and NIST's AI RMF ask for (GOV-09): the
assistants, agents (Agent Studio's workflows), apps and models people use.
Each has its purpose, intended use and whom it affects; its class under the
AI Act (minimal, limited, high-risk with its Annex III area, or a prohibited
practice) and the company's role (deployer, provider, or both); its impact
assessment, in the assessor's name; its human oversight; and its evaluation.
A system is put into use only once its class, purpose and impact assessment
are said, and for a high-risk one its area, oversight and evaluation too; a
prohibited practice is never in use. Each is reviewed every year (every six
months when high-risk). The shared assistants, the workflows, the shared apps
and the models are offered for the register until they are in it. The
register and each impact assessment are in the evidence (`ai-systems.csv`,
`ai-impact/`); the `ai-systems` check is amber while something is missing or
past its review, red for a system in use without what its class needs.

People know when content is made by AI (GOV-10, the AI Act's Article 50):
under the chat's box, a line says answers are made by AI and can be wrong;
and a picture a model made says so in its own metadata (IPTC's digital source
type in XMP: `trainedAlgorithmicMedia`, or `compositeWithTrainedAlgorithmicMedia`
for a person's picture a model changed). PNG and JPEG carry the mark; video and
sound do not yet.

## Readiness and the auditor's package

Each day once, every compliance check's result is kept, for six years: an audit
over a period (SOC 2 Type II) reads it day by day (GOV-11). For each framework,
Admin → Compliance says how ready the company is, from 0 to 100: Arena's
controls that serve it holding now, their operator's parts attested within the
year, and the checks of the management system its audit reads (the risk
register, the Statement of Applicability, policies, audits, suppliers, access
reviews, incidents, AI systems, as each framework asks). Each gap is named with
where it is put right.

**Download for the auditor** gives a framework's package in one zip:

- `clauses.csv`: every clause, the company's document for it, Arena's controls
  that serve it, their state now and their attestation;
- `readiness.json`: the score and the gaps;
- `check-history.csv`: each check of its controls and its management system,
  day by day over the period asked (`?from=` and `?to=`, a year by default);
- the records its audit reads: the risk register, the Statement of
  Applicability, the policies, the audits and findings, the suppliers, the AI
  systems;
- the drafts its scheme asks for: a **Security Target** for the Common
  Criteria, the **AI Act's Annex IV** technical documentation for each
  high-risk system (its gaps marked "To write"), a **CSF profile** with the
  target for the company to fill in.

Each download is in the audit log.

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

## Encryption

| What | How | Checked by |
|---|---|---|
| Traffic from outside | TLS 1.2 and 1.3 only; for 1.2 only ciphers with forward secrecy and authenticated encryption (Traefik's TLS options in `config/traefik/routes.yml`) | `live:tls` |
| Browsers kept to https | HSTS for a year with Let's Encrypt's certificate; with your own CA, `HSTS_SECONDS` in `.env` once every browser trusts it | `live:headers` |
| Secrets at rest | the signing keys and settings' secrets, encrypted with `APP_KEY` | **Secrets encrypted at rest** |
| People's files at rest | each file's bytes, a video's sound, a document's drawn pages and words, and deck templates: AES-256-GCM under a key from `APP_KEY`, before they reach the database; files stored before are sealed by a job within the hour | **People's files encrypted at rest** |
| Backups | each file AES-256 with `BACKUP_KEY_FILE`, kept apart ([deployment.md](../deployment.md#backups-and-restore)) | **A recent backup that ended well** |
| The host's disks | the company's part: LUKS (or the storage's own encryption) under `DATA_DIR`, `MODELS_DIR` and the backups | `live:disk` |

Chat messages and memories are not sealed one by one (search reads them in the
database): the host's disk encryption covers them, as it covers the database's
own files.

## Security alerts and incidents

Every minute the app looks at what its audit log and access history say, and
raises a security alert (kept, and sent to every admin's bell, e-mail and the
alerts webhook) for:

- many failed sign-ins in ten minutes (20, all accounts together, or 10 for
  one account: `Security:FailedSignIns`, `Security:FailedSignInsOneAccount`);
- an administrative role given to a person, a group or everyone;
- many exports by one person in an hour (a person's data, the evidence: 3);
- a backup that failed, or no good backup for over a day.

A broken audit chain and a certificate near its end raise theirs where they
are found. Each alert is raised once.

**The incident register** (Admin → Compliance → Security alerts and incidents)
takes an incident from an alert or by hand: what happened, its severity, and
whether personal or health data may be involved. Its clocks run from when it
was found: with personal data, 72 hours to tell the supervisory authority or
to record why it need not be told (GDPR 33); with health data, 60 days to tell
the people (HIPAA 164.404, and HHS). Each step (a note, contained, resolved,
the authority told, the people told, closed with what was learned) is kept on
its timeline with who and when, and in the audit log (`incident.*`). It cannot
be closed without what was learned, nor, with personal data, without the
authority question answered. The check **Incidents handled within the law's
time** is red while one is past its clock; the evidence carries the register.

## The sign-in notice

**Settings → Sign-in notice** sets what everyone accepts before using the
service: acceptable use, the privacy notice, links to the full policies
(Markdown), with a title and a version. The sign-in page shows it to anyone.
After signing in (with a password, the directory or company sign-in), each
person accepts its current version before anything else: until they have, the
app answers nothing but the notice (`403 notice`). A new version is accepted
again by everyone at their next step. Each acceptance is kept with its version,
when and from where, is in the audit log (`notice.accept`) and in the evidence
(`notice.csv`). API keys are not sessions and are not held up.

## Personal and health data

The safeguards (Admin → Safeguards) find, in what people send, attach, what
tools bring back and what the model answers: e-mail addresses, phone, card
(Luhn-checked), IBAN, IP addresses and US social security numbers, and health
and identity data:

| Kind | Found by |
|---|---|
| `dob` date of birth | a date after "DOB", "date of birth", "born (on)" (and German, French and Persian) |
| `mrn` medical record number | a number after "MRN", "medical record (number)", "patient ID" |
| `nhs` NHS number | ten digits after "NHS (number)", their check digit right (mod 11) |
| `national_id` national code | ten digits after "national code/ID", "کد ملی", their check digit right |
| `medicare` US Medicare number | the MBI's own shape (11 characters, never S, L, O, I, B or Z) |

A rule warns, masks or blocks them before a question reaches a model, for
everyone or some groups, and for all models or those on other providers
(`@external`). Two templates set what HIPAA and the GDPR's special categories
ask: **Health and identity data to cloud models** refuses a question holding
them before a model on another provider, and **Health and identity data
masked** masks them for the company's own models and in answers. The check
**Personal and health data kept from cloud models** is amber until a rule does
so.

The app's log lines carry no personal data: e-mail addresses, phone, card,
IBAN, social security, NHS, national and Medicare numbers, dates of birth and
medical record numbers are masked in each line's message, its values and an
exception's text before it is written (IP addresses stay, as the audit log
keeps them for security). Answers' traces hold times, tokens and tool names
only; the gateway's request log keeps no prompts or answers.

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

## Supply chain

```bash
python3 tools/release-scan.py
```

It lists what the release ships: every .NET package the app, Argus and Code
Arena resolve (transitive ones too, as `dotnet list package` sees them), the
web apps' npm packages from their lock files (the build tools left out), the
Python packages the images pin in their Dockerfiles, and the base images. It
asks [OSV](https://osv.dev) about each package at its version, writes
`dist/argus-arena-VERSION.cdx.json` (CycloneDX 1.5, the vulnerabilities in it)
and its `.sha256`, and prints what it found, worst first.

A critical vulnerability fails it (exit 1) unless
[accepted-vulnerabilities.yaml](accepted-vulnerabilities.yaml) accepts it: the
advisory's id, the package, why it does not apply or cannot be fixed yet, and
a date it is accepted until. Past that date it fails again, so an acceptance is
looked at once more. When OSV cannot be reached it fails too (exit 2): nothing
known is not the same as nothing found. High findings are listed and do not
fail it; they are fixed in the next release. The base images are listed, not
scanned here; scan them with Trivy where it is installed.

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
guard fails first, so nobody weakens one by accident: `tools/compliance-docs.py`
compares `controls.yaml` with the newest release's, and a control gone, a
clause or a check it no longer has, or fewer tests proving it fails the
deployment tests until the changelog's entry for the next release names it
under a `### :warning: Weakened controls` heading.
