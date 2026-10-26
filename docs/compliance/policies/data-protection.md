# Data protection and retention policy

> A template. Replace each [bracket], remove what does not apply to you,
> approve it and set its review date. Where Arena does part of a rule, the
> page or setting is named ([compliance](../README.md)).

| | |
|---|---|
| Owner | [The data protection officer, or the information security officer] |
| Approved by | [Name, role, date] |
| Review | every year |

## Classes of information

| Class | What | Rules |
|---|---|---|
| Public | published | none |
| Internal | most work | [Company]'s people and models only |
| Confidential | customers', contracts, source | those who need it; [no outside models] |
| Personal | about a person | as below, and the GDPR |
| Special / health | health, beliefs, biometrics | as personal, and only [the named groups and local models]; HIPAA where it applies |

Documents and chats are labelled [how: a knowledge source's readers, a room's
members, a heading]. Arena's safeguards find personal and health data (names
of kinds: e-mail, phone, card, IBAN, date of birth, medical record, NHS,
national and Medicare numbers) and, with the templates **Health and identity
data to cloud models** and **Health and identity data masked**, keep it from
models outside the company and mask it elsewhere (Admin → Safeguards).

## Principles

[Company] processes personal data lawfully, for the purposes in the [record of
processing](../templates/record-of-processing.md), no more than they need,
accurate, kept no longer than below, and secure. New processing that is likely
to be high risk (health data at scale, monitoring people, new kinds of models
deciding about people) has a [DPIA](../templates/dpia.md) first. The data
protection officer is [name, contact] (or: [Company] needs none, because ...).

## People's rights

People ask in Your account → Your data, or by [e-mail]; an admin takes in
other requests (Admin → People → a person → Data requests). Each request is
answered within a month (Arena counts the days and tells the admins), after
checking who asks. Access gives an export; rectification is done in Arena or
at the source; erasure removes everything of theirs unless a legal hold or a
law keeps it (the reply says what is kept and why); restriction and objection
stop what learns from their data. HIPAA: access and amendment within [30
days]; disclosures are accounted for.

## How long things are kept

| What | How long | Set in |
|---|---|---|
| Chats and their files | [N days; forever is a choice to record] | Settings → Data retention |
| The audit log | six years | fixed |
| The gateway's request log, tool calls, the bell | [N days] | Settings → Data retention |
| Logs (Loki) | 90 days | `config/loki.yml` |
| Backups | [14 backups] | `BACKUP_KEEP` |
| Accounts of leavers | [deleted with the directory / after N days] | |

A legal hold (Admin → People → a person) stops deletion while a claim or an
investigation needs it. Deleting is real: Arena sweeps every hour and audits
what it removed; backups let go after their period.

## Transfers and sharing

To suppliers only under the [supplier policy](suppliers.md). Data leaves
[Company] only as people are allowed to send it ([exports, e-mail]); exports
are audited and many in an hour raise an alert.

## Records

The record of processing, the DPIAs, the data requests (in the evidence),
legal holds, consents where processing rests on them. Kept [six years].
