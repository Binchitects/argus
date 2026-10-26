# The GDPR, Regulation (EU) 2016/679

<!-- Written by tools/compliance-docs.py from controls.yaml and frameworks.yaml: change those and run it, not this. -->

Arena's controls serve 15 of the 37 clauses here. Every clause is met by the company that runs Arena, under the document beside it; where Arena has a control, the control is the technical part and its evidence is in Admin → Compliance. The titles are short names, not the clauses' text. [Shared responsibility](../README.md#shared-responsibility), [the controls](../controls.md).

| Clause | | Arena | The company's document |
|---|---|---|---|
| Art.5 | Principles of processing personal data | [LOG-01](../controls.md#log-01-who-did-what-is-recorded), [LOG-02](../controls.md#log-02-logs-are-kept-and-kept-for-a-set-time), [LOG-03](../controls.md#log-03-nobody-changes-the-audit-log-unseen), [DAT-01](../controls.md#dat-01-chats-and-files-are-deleted-when-their-time-is-up), [DAT-07](../controls.md#dat-07-records-are-kept-for-set-times-and-no-longer), [DAT-08](../controls.md#dat-08-personal-and-health-data-kept-in-bounds), [GOV-01](../controls.md#gov-01-the-controls-are-kept-from-breaking) | [Data protection and retention policy](../policies/data-protection.md) |
| Art.6 | Lawfulness of processing |  | [Record of processing activities](../templates/record-of-processing.md) |
| Art.7 | Conditions for consent |  | [Data protection and retention policy](../policies/data-protection.md) |
| Art.8 | A child's consent to online services |  | [Data protection and retention policy](../policies/data-protection.md) |
| Art.9 | Special categories of personal data | [DAT-08](../controls.md#dat-08-personal-and-health-data-kept-in-bounds) | [Data protection and retention policy](../policies/data-protection.md) |
| Art.10 | Data on criminal convictions and offences |  | [Data protection and retention policy](../policies/data-protection.md) |
| Art.11 | Processing that does not need identification |  | [Data protection and retention policy](../policies/data-protection.md) |
| Art.12 | Transparent information and how the rights are exercised | [DAT-06](../controls.md#dat-06-peoples-requests-about-their-data-are-answered), [GOV-02](../controls.md#gov-02-everyone-accepts-the-acceptable-use-and-privacy-notice) | [Data protection and retention policy](../policies/data-protection.md) |
| Art.13 | Information when data is collected from the person | [GOV-02](../controls.md#gov-02-everyone-accepts-the-acceptable-use-and-privacy-notice) | [Data protection and retention policy](../policies/data-protection.md) |
| Art.14 | Information when data is not collected from the person |  | [Data protection and retention policy](../policies/data-protection.md) |
| Art.15 | Right of access | [DAT-03](../controls.md#dat-03-a-persons-data-exported), [DAT-06](../controls.md#dat-06-peoples-requests-about-their-data-are-answered) | [Data protection and retention policy](../policies/data-protection.md) |
| Art.16 | Right to rectification | [DAT-06](../controls.md#dat-06-peoples-requests-about-their-data-are-answered) | [Data protection and retention policy](../policies/data-protection.md) |
| Art.17 | Right to erasure | [DAT-01](../controls.md#dat-01-chats-and-files-are-deleted-when-their-time-is-up), [DAT-02](../controls.md#dat-02-legal-hold), [DAT-06](../controls.md#dat-06-peoples-requests-about-their-data-are-answered) | [Data protection and retention policy](../policies/data-protection.md) |
| Art.18 | Right to restriction of processing | [DAT-06](../controls.md#dat-06-peoples-requests-about-their-data-are-answered) | [Data protection and retention policy](../policies/data-protection.md) |
| Art.19 | Telling recipients of a rectification, erasure or restriction |  | [Data protection and retention policy](../policies/data-protection.md) |
| Art.20 | Right to data portability | [DAT-03](../controls.md#dat-03-a-persons-data-exported), [DAT-06](../controls.md#dat-06-peoples-requests-about-their-data-are-answered) | [Data protection and retention policy](../policies/data-protection.md) |
| Art.21 | Right to object | [DAT-06](../controls.md#dat-06-peoples-requests-about-their-data-are-answered) | [Data protection and retention policy](../policies/data-protection.md) |
| Art.22 | Automated decisions, profiling included |  | [Data protection and retention policy](../policies/data-protection.md) |
| Art.24 | Responsibility of the controller |  | [Information security policy](../policies/information-security.md) |
| Art.25 | Data protection by design and by default | [IAM-05](../controls.md#iam-05-least-privilege-by-roles-and-rules), [DAT-04](../controls.md#dat-04-personal-data-and-secrets-kept-from-the-model), [DAT-08](../controls.md#dat-08-personal-and-health-data-kept-in-bounds), [GOV-01](../controls.md#gov-01-the-controls-are-kept-from-breaking) | [Change and vulnerability management policy](../policies/change-management.md) |
| Art.26 | Joint controllers |  | [Supplier and transfer policy](../policies/suppliers.md) |
| Art.28 | Processors |  | [Supplier and transfer policy](../policies/suppliers.md) |
| Art.29 | Processing under the controller's or processor's authority |  | [Access control policy](../policies/access-control.md) |
| Art.30 | Records of processing activities | [LOG-01](../controls.md#log-01-who-did-what-is-recorded) | [Record of processing activities](../templates/record-of-processing.md) |
| Art.31 | Cooperation with the supervisory authority |  | [Incident response runbook](../incident-response.md) |
| Art.32 | Security of processing | [IAM-01](../controls.md#iam-01-each-person-signs-in-as-themselves-with-a-strong-password), [IAM-02](../controls.md#iam-02-sessions-end-on-their-own), [IAM-03](../controls.md#iam-03-two-factor-sign-in), [IAM-04](../controls.md#iam-04-joiners-and-leavers-follow-the-companys-directory), [IAM-05](../controls.md#iam-05-least-privilege-by-roles-and-rules), [IAM-06](../controls.md#iam-06-api-keys-are-personal-revocable-and-checked), [IAM-07](../controls.md#iam-07-privileged-access-is-reviewed), [LOG-03](../controls.md#log-03-nobody-changes-the-audit-log-unseen), [LOG-04](../controls.md#log-04-the-audit-log-is-sent-on-to-the-companys-siem), [MON-01](../controls.md#mon-01-the-service-is-watched-and-people-are-alerted), [DAT-04](../controls.md#dat-04-personal-data-and-secrets-kept-from-the-model), [DAT-05](../controls.md#dat-05-secrets-and-peoples-files-are-encrypted-at-rest), [DAT-08](../controls.md#dat-08-personal-and-health-data-kept-in-bounds), [NET-01](../controls.md#net-01-encrypted-in-transit-one-way-in), [OPS-01](../controls.md#ops-01-backups-restored-and-proven), [OPS-02](../controls.md#ops-02-code-the-model-writes-runs-apart), [GOV-03](../controls.md#gov-03-what-a-release-ships-is-listed-and-its-known-vulnerabilities-gate-it) | [Information security policy](../policies/information-security.md) |
| Art.33 | Notifying the supervisory authority of a breach | [MON-02](../controls.md#mon-02-security-events-raise-alerts-and-incidents-are-handled-in-time) | [Incident response runbook](../incident-response.md) |
| Art.34 | Telling the people affected of a breach | [MON-02](../controls.md#mon-02-security-events-raise-alerts-and-incidents-are-handled-in-time) | [Incident response runbook](../incident-response.md) |
| Art.35 | Data protection impact assessment |  | [Data protection impact assessment](../templates/dpia.md) |
| Art.36 | Prior consultation |  | [Data protection impact assessment](../templates/dpia.md) |
| Art.37 | Designation of the data protection officer |  | [Data protection and retention policy](../policies/data-protection.md) |
| Art.38 | Position of the data protection officer |  | [Data protection and retention policy](../policies/data-protection.md) |
| Art.39 | Tasks of the data protection officer |  | [Data protection and retention policy](../policies/data-protection.md) |
| Art.44 | General principle for transfers |  | [Supplier and transfer policy](../policies/suppliers.md) |
| Art.45 | Transfers on the basis of an adequacy decision |  | [Supplier and transfer policy](../policies/suppliers.md) |
| Art.46 | Transfers subject to appropriate safeguards |  | [Supplier and transfer policy](../policies/suppliers.md) |
| Art.49 | Derogations for specific situations |  | [Supplier and transfer policy](../policies/suppliers.md) |
