# Supplier and transfer policy

> A template. Replace each [bracket], remove what does not apply to you,
> approve it and set its review date.

| | |
|---|---|
| Owner | [Procurement, with the information security officer] |
| Approved by | [Name, role, date] |
| Review | every year, and before a new supplier sees [Company]'s data |

## Which suppliers

Arena runs on [Company]'s hardware and sends nothing to its makers. Suppliers
appear where [Company] adds them:

| Supplier | What they see | Contract | Where the data goes |
|---|---|---|---|
| [A cloud model API, added to the gateway] | prompts and answers sent to its models | DPA; BAA if health data; [no training on it] | [region] |
| [Company sign-in provider] | who signs in | DPA | |
| [E-mail relay (SMTP)] | notifications | DPA | |
| [SIEM] | the audit log | DPA; BAA if health data | |
| [Off-site backup storage] | encrypted backups | DPA | |
| [Outsourced development, support] | | NDA, DPA | |

## Rules

1. Before a supplier sees [Company]'s data: its security is assessed ([a SOC 2
   report, ISO 27001 certificate, a questionnaire]), a contract with security
   terms is signed (a GDPR data processing agreement, Art. 28; a HIPAA business
   associate agreement for health information), and the transfer is in the
   [record of processing](../templates/record-of-processing.md).
2. Personal data leaves the EEA only to a country with an adequacy decision or
   under standard contractual clauses with a transfer assessment.
3. In Arena, models outside the company are marked so; the safeguards keep
   health data and secrets from them (Admin → Safeguards), and [which groups]
   may use them (Admin → Models, access rules).
4. Suppliers tell [Company] of a breach within [24 hours]; that starts an
   [incident](../incident-response.md).
5. Each supplier is reviewed [every year]: its reports, its incidents, what it
   still needs.
6. When a supplier ends, its access is removed and the data returned or deleted,
   with a confirmation kept.

## Records

The supplier register above, contracts, assessments, reviews. Kept [six years
after the contract ends].
