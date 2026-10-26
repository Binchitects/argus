# Incident response runbook

What to do from the moment something looks wrong to the incident's closure,
with Arena's incident register (Admin → Compliance → Security alerts and
incidents) keeping the timeline and the law's clocks. Replace each [bracket]
with your own; the register and the alerts are described in
[Security alerts and incidents](README.md#security-alerts-and-incidents).

| | |
|---|---|
| Owner | [Information security officer] |
| Incident lead on call | [rota, phone] |
| Supervisory authority (GDPR) | [name, how to notify, e.g. its online form] |
| HHS (HIPAA) | the breach portal; under 500 people: within 60 days of the year's end |
| Legal counsel | [name, phone] |
| Review | every year, and after every incident of high severity |

## 1. Notice and report

Anyone who sees something wrong tells [where: a channel, a phone, an e-mail]
at once: a strange answer carrying someone else's data, a sign-in they did not
make, a lost device with an export, a supplier's breach notice. Arena itself
raises security alerts for many failed sign-ins, an admin role given, many
exports in an hour, a failed or missing backup, a broken audit chain and a
certificate near its end: they reach every admin's bell and e-mail and the
alerts webhook.

## 2. Assess (within [1 hour])

The incident lead decides whether it is an incident. If it is:

1. **Open it in the register**, from its alert or by hand: what happened, the
   severity (low, medium, high, critical) and whether **personal data** or
   **health data** may be involved, with when it was found. The clocks start
   then: 72 hours to the authority with personal data, 60 days to the people
   with health data.
2. Name who works on it. Keep every step in the register as a note.

Severity: **critical** — health or special-category data of many, or an admin
account taken over; **high** — personal data of some, or the service down for
long; **medium** — contained to one account or no personal data; **low** —
an attempt that failed.

## 3. Contain

Stop the harm, then record **Contained** with how it was stopped:

| What | In Arena |
|---|---|
| An account taken over | Admin → People → the person: **Sign out everywhere**, reset the password and two-factor, revoke their API keys, disable if needed |
| An admin's access misused | remove the role (Admin → Access); check the audit log for what they did |
| A key or secret leaked | rotate it at its source; in Arena, revoke the API key; a leaked `APP_KEY` means a planned rebuild ([cryptography](policies/cryptography.md)) |
| A model leaking data | take it out of use (Admin → Models), turn on the safeguard templates that apply |
| A cloud model or supplier breached | stop sending to it (Admin → Models; the gateway) |
| The host compromised | take it off the network; keep its disks for evidence; restore elsewhere from a backup taken before ([continuity](policies/business-continuity.md)) |

## 4. Collect evidence

Before changing more than containment needs:

- Admin → Compliance → **Download evidence**: the zip with the audit log's chain (its
  hashes show nothing was changed), the incidents, access and settings.
- Admin → Audit log, filtered to the people and time; the SIEM's copy of it.
- Logs from Admin → Logs (Loki keeps 90 days) and the host's.
- Put a **legal hold** on the people involved (Admin → People) so retention
  removes nothing of theirs.

Keep evidence read only, note who handled it and when (chain of custody).

## 5. Notify

| Who | When | How it is recorded |
|---|---|---|
| The supervisory authority (GDPR Art. 33) | personal data and a risk to people: within **72 hours** of finding it; in phases if not all is known | **Authority told**, with its reference; or **Authority need not be told**, with why (no risk to people) |
| The people affected (GDPR Art. 34) | a high risk to them: without undue delay | **People told**, with how |
| The people affected (HIPAA 164.404) | unsecured health information: within **60 days** | **People told** |
| HHS (164.408) and the media (164.406) | 500 or more people: within 60 days (the media for 500 in a state); fewer: the year's log | a note with the filing |
| A controller you process for, or a covered entity (GDPR Art. 33(2), HIPAA 164.410) | without undue delay; [the contract's hours] | a note |
| [Customers, insurers, the police] | [as contracts and law say] | a note |

The check **Incidents handled within the law's time** in Admin → Compliance is
red while an incident is past a clock without its step.

## 6. Recover

Restore what was lost or changed (a backup restore, accounts re-enabled after
reset), watch for the attacker's return, and record **Resolved**.

## 7. Close and learn

Within [two weeks] of resolving, the lead and those involved review: what
happened, why, what worked, what did not, and what changes (a control, a
setting, training, a policy). **Close it** with those lessons: Arena
does not close an incident without them, nor, with personal data, without the
authority question answered. The changes go into the risk register and the
change log.

## Drills

[Twice a year], a tabletop exercise on one of: an admin account taken over,
health data sent to a cloud model, the host's disk lost, a supplier's breach.
Record it as a low-severity incident with "Drill" in its title.
