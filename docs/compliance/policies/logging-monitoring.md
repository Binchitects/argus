# Logging and monitoring policy

> A template. Replace each [bracket], remove what does not apply to you,
> approve it and set its review date.

| | |
|---|---|
| Owner | [Information security officer] |
| Approved by | [Name, role, date] |
| Review | every year |

## Purpose

[Company] records who did what in Arena, keeps the records safe from change,
and watches them for what needs action. This is ISO 27001 A.8.15 and A.8.16,
SOC 2 CC7.2, HIPAA 164.312(b), PCI DSS requirement 10, NIST SP 800-53's AU
family and CSF's Detect function.

## What is recorded

- **The audit log** (LOG-01): sign-ins and their failures, changes to people,
  groups, roles, settings, models and tools, exports, reads of another person's
  data, and the compliance actions. Each entry is chained to the one before,
  so a change shows (LOG-03).
- **The services' logs** (Loki) and **metrics** (Prometheus), for operations.
- **Traces** of answers, for what a model and its tools did.

## Kept

The audit log for [six years] (HIPAA's documentation rule); the logs for
[period] (Settings → Retention); traces for [period]. Records are deleted
when their time is up, unless a legal hold keeps them (DAT-02, DAT-07).

## Watched

- The audit log goes on to [the SIEM] as it is written (LOG-04).
- Alerts: repeated failed sign-ins, an admin granted, a mass export, the audit
  chain broken, a backup failed, a certificate near its end (MON-02), each to
  [who] by [channel].
- [Who] reviews the alerts [daily] and the SIEM's reports [weekly]. What
  needs it becomes an incident ([incident response](../incident-response.md)).

## Clocks

The hosts keep time with [NTP source]: records from several services line up.

## Records

The alerts and what was done about them; each review's date and reviewer.
