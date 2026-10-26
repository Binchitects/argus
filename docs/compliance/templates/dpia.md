# Data protection impact assessment (GDPR Art. 35)

> A template for assessing a processing that is likely to be a high risk to
> people before it starts: health or special-category data at scale, models
> that help decide about people, monitoring people, recordings of calls. It is
> pre-filled with what Arena brings; replace each [bracket] and decide. If the
> risk left is still high, consult the supervisory authority first (Art. 36).

| | |
|---|---|
| Processing | [e.g. clinicians summarising patient letters with Arena's models] |
| Owner | [name, role] |
| Data protection officer's advice | [their advice, date] |
| People consulted | [the people affected or their representatives, where suitable] |
| Decided | [go / go with changes / no / consult the authority], by [name], [date] |
| Review | [date, and whenever the processing changes] |

## 1. What the processing is

- **Purpose and benefit**: [what for, who gains].
- **Data**: [kinds: names, health, ...], of [whom], about [how many] people.
- **How**: [who writes what into which room or chat, which models answer,
  which tools run]. The [data flows](../data-flows.md) say where data goes
  inside the stack.
- **Models**: [the company's own on its hardware / a cloud provider, by name,
  region, contract].
- **Kept**: [period, Settings → Data retention], [legal hold when].
- **Recipients**: [who reads the results].

## 2. Necessary and proportionate

- Lawful basis: [Art. 6(1)(...)] and for special categories [Art. 9(2)(...)].
- Only what is needed: [why each kind of data is needed; what is left out].
  Arena's safeguards can mask identifiers before the model sees them.
- People are told: [the privacy notice, accepted in Arena's sign-in notice].
- Their rights: asked in Your account → Your data, answered within a month
  (access, rectification, erasure, restriction, objection, portability).
- Processors: [contracts under Art. 28; transfers under Chapter V].

## 3. Risks to people

Each: what could happen to people, how likely (remote, possible, probable), how
severe (minimal, significant, severe), the measures, the risk left.

| Risk | Likely | Severe | Measures (Arena's and yours) | Left |
|---|---|---|---|---|
| Health data reaches a model outside the company | [ ] | [ ] | the template Health and identity data to cloud models (DAT-08); only local models for this room | [ ] |
| Someone not entitled reads the chats | [ ] | [ ] | own accounts, two-factor, roles reviewed (IAM-01 to IAM-07); room membership | [ ] |
| A model's answer is wrong and is acted on | [ ] | [ ] | a person checks every answer before use; [training] | [ ] |
| Data kept longer than needed | [ ] | [ ] | retention swept hourly and audited (DAT-01, DAT-07) | [ ] |
| A breach goes unnoticed or unreported | [ ] | [ ] | security alerts, the incident register with the 72-hour and 60-day clocks (MON-02) | [ ] |
| The host or its disks are stolen | [ ] | [ ] | files sealed (DAT-05), disks and backups encrypted, physical security | [ ] |
| People cannot exercise their rights | [ ] | [ ] | data requests with their deadlines (DAT-06) | [ ] |
| [Profiling or decisions about people] | [ ] | [ ] | [a person decides; Art. 22] | [ ] |

## 4. Outcome

- Measures to add before it starts: [list, owner, date].
- Risk left: [low / medium / high]. High: consult the supervisory authority.
- Signed: [owner], [DPO], [date].
