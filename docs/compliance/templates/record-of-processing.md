# Record of processing activities (GDPR Art. 30)

> A template, filled in with what Arena itself processes. Replace each
> [bracket], remove the activities you do not use, add your own, and keep it
> current: a supervisory authority may ask for it. Where [Company] processes
> for a customer, keep the processor's record (Art. 30(2)) as well.

| | |
|---|---|
| Controller | [Company, address] |
| Representative in the EU | [if the company is outside it] |
| Data protection officer | [name, contact] |
| Last reviewed | [date] |

## Activities

### 1. Accounts and sign-in

| | |
|---|---|
| Purpose | giving people at [Company] access to Arena, securing it |
| Lawful basis | Art. 6(1)(b) the employment contract / (f) legitimate interest in securing systems |
| People | employees, contractors |
| Data | name, user name, e-mail, groups, role, password hash, second factor, sessions (address, browser), sign-ins and failures |
| Recipients | admins; [the company sign-in provider] |
| Transfers outside the EEA | [none / which, under which safeguard] |
| Kept | while the account exists; sign-ins in the audit log six years |
| Security | [IAM-01 to IAM-07](../controls.md#people-and-access-iam), [LOG-03](../controls.md#records-log) |

### 2. Conversations with models

| | |
|---|---|
| Purpose | helping people with their work: answers, documents, code, pictures, speech |
| Lawful basis | Art. 6(1)(b) / (f); Art. 9(2)[(h) health care / (b) employment law] where health data is involved |
| People | employees; [the people they write about: customers, patients] |
| Data | what people write and upload, the answers, tool calls, sound and pictures; personal data the safeguards find is masked or kept from outside models |
| Recipients | the person and those they share with; the company's own models; [cloud model providers, by name] |
| Transfers outside the EEA | [cloud models' regions, safeguards] |
| Kept | [Settings → Data retention: N days, or kept until deleted]; tool calls [N days] |
| Security | [DAT-01, DAT-04, DAT-05, DAT-08](../controls.md#data-dat), [OPS-02](../controls.md#operations-ops) |

### 3. Company knowledge

| | |
|---|---|
| Purpose | answering from the company's documents and code |
| Lawful basis | Art. 6(1)(f) |
| People | authors and people named in the documents |
| Data | the passages of the sources an admin adds, with their readers |
| Recipients | only those the source lets read each document |
| Kept | while the source is connected |
| Security | IAM-05 |

### 4. Use, cost and the gateway's log

| | |
|---|---|
| Purpose | credits, chargeback, capacity, finding abuse |
| Lawful basis | Art. 6(1)(f) |
| People | employees, their API keys' clients |
| Data | who used which model, when, tokens and cost; the request log |
| Recipients | admins; [finance: the chargeback report] |
| Kept | the request log [N days]; monthly totals [N years] |
| Security | IAM-06, DAT-07 |

### 5. Security monitoring and the audit log

| | |
|---|---|
| Purpose | security, accountability, legal claims |
| Lawful basis | Art. 6(1)(c) legal obligation [HIPAA 164.312(b)] / (f) |
| People | everyone who uses or runs Arena |
| Data | who did what and when, from where; logs with personal data masked; security alerts |
| Recipients | admins, the compliance officer; [the SIEM provider] |
| Kept | the audit log six years; logs 90 days |
| Security | [LOG-01 to LOG-04](../controls.md#records-log), MON-02 |

### 6. People's data requests and incidents

| | |
|---|---|
| Purpose | answering people's rights; handling breaches |
| Lawful basis | Art. 6(1)(c) |
| People | those asking; those affected by an incident |
| Data | the request, its steps; the incident, its notifications |
| Recipients | admins, the compliance officer; the supervisory authority |
| Kept | [six years] |
| Security | DAT-06, MON-02 |

### 7. [Meetings, calls and phone: recordings, captions]

| | |
|---|---|
| Purpose | [meeting notes, captions] |
| Lawful basis | [Art. 6(1)(a) consent of those recorded / (f)] |
| Data | voices, what was said, its transcript |
| Kept | [N days] |

## General description of the security measures (Art. 30(1)(g))

See [the controls](../controls.md) and the [information security
policy](../policies/information-security.md): accounts of their own with
strong passwords and two-factor, least privilege by roles reviewed every [90
days], an audit log chained against changes, encryption in transit (TLS 1.2+)
and at rest (AES-256-GCM, encrypted backups, encrypted disks), personal and
health data kept from outside models, backups restored and proven, alerts and
an incident register with the law's clocks.
