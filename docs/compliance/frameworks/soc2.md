# SOC 2, Trust Services Criteria

<!-- Written by tools/compliance-docs.py from controls.yaml and frameworks.yaml: change those and run it, not this. -->

Arena's controls serve 26 of the 61 clauses here. Every clause is met by the company that runs Arena, under the document beside it; where Arena has a control, the control is the technical part and its evidence is in Admin → Compliance. The titles are short names, not the clauses' text. [Shared responsibility](../README.md#shared-responsibility), [the controls](../controls.md).

| Clause | | Arena | The company's document |
|---|---|---|---|
| CC1.1 | Commitment to integrity and ethical values |  | [Information security policy](../policies/information-security.md) |
| CC1.2 | Board independence and oversight of internal control |  | [Information security policy](../policies/information-security.md) |
| CC1.3 | Structures, reporting lines, authorities and responsibilities |  | [Information security policy](../policies/information-security.md) |
| CC1.4 | Attracting, developing and keeping competent people |  | [People security policy](../policies/people-security.md) |
| CC1.5 | Accountability for internal control responsibilities |  | [People security policy](../policies/people-security.md) |
| CC2.1 | Relevant and quality information for internal control |  | [Information security policy](../policies/information-security.md) |
| CC2.2 | Internal communication of objectives and responsibilities |  | [Information security policy](../policies/information-security.md) |
| CC2.3 | Communication with external parties | [GOV-02](../controls.md#gov-02-everyone-accepts-the-acceptable-use-and-privacy-notice) | [Information security policy](../policies/information-security.md) |
| CC3.1 | Objectives clear enough to identify and assess risks |  | [Risk management policy](../policies/risk-management.md) |
| CC3.2 | Identifying and analysing risks |  | [Risk management policy](../policies/risk-management.md) |
| CC3.3 | The potential for fraud |  | [Risk management policy](../policies/risk-management.md) |
| CC3.4 | Changes that could significantly affect internal control |  | [Risk management policy](../policies/risk-management.md) |
| CC4.1 | Ongoing and separate evaluations | [GOV-01](../controls.md#gov-01-the-controls-are-kept-from-breaking) | [Information security policy](../policies/information-security.md) |
| CC4.2 | Communicating deficiencies |  | [Information security policy](../policies/information-security.md) |
| CC5.1 | Control activities that reduce risks |  | [Information security policy](../policies/information-security.md) |
| CC5.2 | General control activities over technology |  | [Information security policy](../policies/information-security.md) |
| CC5.3 | Policies and procedures that put controls in place |  | [Information security policy](../policies/information-security.md) |
| CC6.1 | Logical access security software, infrastructure and architectures | [IAM-01](../controls.md#iam-01-each-person-signs-in-as-themselves-with-a-strong-password), [IAM-02](../controls.md#iam-02-sessions-end-on-their-own), [IAM-03](../controls.md#iam-03-two-factor-sign-in), [IAM-05](../controls.md#iam-05-least-privilege-by-roles-and-rules), [IAM-06](../controls.md#iam-06-api-keys-are-personal-revocable-and-checked), [DAT-05](../controls.md#dat-05-secrets-and-peoples-files-are-encrypted-at-rest) | [Access control policy](../policies/access-control.md) |
| CC6.2 | Registering and authorising new users | [IAM-01](../controls.md#iam-01-each-person-signs-in-as-themselves-with-a-strong-password), [IAM-04](../controls.md#iam-04-joiners-and-leavers-follow-the-companys-directory), [IAM-07](../controls.md#iam-07-privileged-access-is-reviewed) | [Access control policy](../policies/access-control.md) |
| CC6.3 | Role-based access, least privilege and segregation of duties | [IAM-04](../controls.md#iam-04-joiners-and-leavers-follow-the-companys-directory), [IAM-05](../controls.md#iam-05-least-privilege-by-roles-and-rules), [IAM-07](../controls.md#iam-07-privileged-access-is-reviewed) | [Access control policy](../policies/access-control.md) |
| CC6.4 | Physical access to facilities and protected assets |  | [Physical security policy](../policies/physical-security.md) |
| CC6.5 | Discontinuing protections when assets are disposed of |  | [Physical security policy](../policies/physical-security.md) |
| CC6.6 | Security against threats from outside the system's boundaries | [NET-01](../controls.md#net-01-encrypted-in-transit-one-way-in) | [Information security policy](../policies/information-security.md) |
| CC6.7 | Restricting the transmission, movement and removal of information | [DAT-04](../controls.md#dat-04-personal-data-and-secrets-kept-from-the-model), [DAT-08](../controls.md#dat-08-personal-and-health-data-kept-in-bounds), [NET-01](../controls.md#net-01-encrypted-in-transit-one-way-in) | [Data protection and retention policy](../policies/data-protection.md) |
| CC6.8 | Preventing or detecting unauthorised or malicious software | [OPS-02](../controls.md#ops-02-code-the-model-writes-runs-apart) | [Information security policy](../policies/information-security.md) |
| CC7.1 | Detecting configuration changes and new vulnerabilities | [GOV-03](../controls.md#gov-03-what-a-release-ships-is-listed-and-its-known-vulnerabilities-gate-it) | [Change and vulnerability management policy](../policies/change-management.md) |
| CC7.2 | Monitoring system components for anomalies | [LOG-01](../controls.md#log-01-who-did-what-is-recorded), [LOG-02](../controls.md#log-02-logs-are-kept-and-kept-for-a-set-time), [LOG-03](../controls.md#log-03-nobody-changes-the-audit-log-unseen), [LOG-04](../controls.md#log-04-the-audit-log-is-sent-on-to-the-companys-siem), [MON-01](../controls.md#mon-01-the-service-is-watched-and-people-are-alerted) | [Information security policy](../policies/information-security.md) |
| CC7.3 | Evaluating security events | [LOG-03](../controls.md#log-03-nobody-changes-the-audit-log-unseen), [MON-01](../controls.md#mon-01-the-service-is-watched-and-people-are-alerted), [MON-02](../controls.md#mon-02-security-events-raise-alerts-and-incidents-are-handled-in-time) | [Incident response runbook](../incident-response.md) |
| CC7.4 | Responding to security incidents | [MON-02](../controls.md#mon-02-security-events-raise-alerts-and-incidents-are-handled-in-time) | [Incident response runbook](../incident-response.md) |
| CC7.5 | Recovering from security incidents | [MON-02](../controls.md#mon-02-security-events-raise-alerts-and-incidents-are-handled-in-time) | [Incident response runbook](../incident-response.md) |
| CC8.1 | Authorising, designing, testing, approving and putting changes in place | [GOV-01](../controls.md#gov-01-the-controls-are-kept-from-breaking), [GOV-03](../controls.md#gov-03-what-a-release-ships-is-listed-and-its-known-vulnerabilities-gate-it) | [Change and vulnerability management policy](../policies/change-management.md) |
| CC9.1 | Reducing the risks of business disruption | [OPS-01](../controls.md#ops-01-backups-restored-and-proven) | [Backup and business continuity policy](../policies/business-continuity.md) |
| CC9.2 | Risks from vendors and business partners | [GOV-03](../controls.md#gov-03-what-a-release-ships-is-listed-and-its-known-vulnerabilities-gate-it) | [Supplier and transfer policy](../policies/suppliers.md) |
| A1.1 | Managing and planning capacity |  | [Backup and business continuity policy](../policies/business-continuity.md) |
| A1.2 | Environmental protections, software, backups and recovery infrastructure | [OPS-01](../controls.md#ops-01-backups-restored-and-proven) | [Backup and business continuity policy](../policies/business-continuity.md) |
| A1.3 | Testing the recovery plan | [OPS-01](../controls.md#ops-01-backups-restored-and-proven) | [Backup and business continuity policy](../policies/business-continuity.md) |
| C1.1 | Identifying and keeping confidential information | [DAT-04](../controls.md#dat-04-personal-data-and-secrets-kept-from-the-model), [DAT-08](../controls.md#dat-08-personal-and-health-data-kept-in-bounds) | [Data protection and retention policy](../policies/data-protection.md) |
| C1.2 | Disposing of confidential information | [DAT-01](../controls.md#dat-01-chats-and-files-are-deleted-when-their-time-is-up), [DAT-07](../controls.md#dat-07-records-are-kept-for-set-times-and-no-longer) | [Data protection and retention policy](../policies/data-protection.md) |
| PI1.1 | Information about processing objectives and specifications |  | [Change and vulnerability management policy](../policies/change-management.md) |
| PI1.2 | Inputs complete and accurate |  | [Change and vulnerability management policy](../policies/change-management.md) |
| PI1.3 | Processing complete, accurate and timely |  | [Change and vulnerability management policy](../policies/change-management.md) |
| PI1.4 | Outputs complete, accurate and timely |  | [Change and vulnerability management policy](../policies/change-management.md) |
| PI1.5 | Storing inputs, items in processing and outputs |  | [Backup and business continuity policy](../policies/business-continuity.md) |
| P1.1 | Notice of privacy practices | [GOV-02](../controls.md#gov-02-everyone-accepts-the-acceptable-use-and-privacy-notice) | [Data protection and retention policy](../policies/data-protection.md) |
| P2.1 | Choice and consent |  | [Data protection and retention policy](../policies/data-protection.md) |
| P3.1 | Collection consistent with the objectives |  | [Data protection and retention policy](../policies/data-protection.md) |
| P3.2 | Explicit consent where it is needed |  | [Data protection and retention policy](../policies/data-protection.md) |
| P4.1 | Use limited to the purposes identified | [DAT-08](../controls.md#dat-08-personal-and-health-data-kept-in-bounds) | [Data protection and retention policy](../policies/data-protection.md) |
| P4.2 | Retention of personal information | [DAT-01](../controls.md#dat-01-chats-and-files-are-deleted-when-their-time-is-up), [DAT-07](../controls.md#dat-07-records-are-kept-for-set-times-and-no-longer) | [Data protection and retention policy](../policies/data-protection.md) |
| P4.3 | Disposal of personal information | [DAT-01](../controls.md#dat-01-chats-and-files-are-deleted-when-their-time-is-up), [DAT-06](../controls.md#dat-06-peoples-requests-about-their-data-are-answered), [DAT-07](../controls.md#dat-07-records-are-kept-for-set-times-and-no-longer) | [Data protection and retention policy](../policies/data-protection.md) |
| P5.1 | People's access to their personal information | [DAT-03](../controls.md#dat-03-a-persons-data-exported), [DAT-06](../controls.md#dat-06-peoples-requests-about-their-data-are-answered) | [Data protection and retention policy](../policies/data-protection.md) |
| P5.2 | Correction of personal information | [DAT-06](../controls.md#dat-06-peoples-requests-about-their-data-are-answered) | [Data protection and retention policy](../policies/data-protection.md) |
| P6.1 | Disclosure to third parties with consent |  | [Supplier and transfer policy](../policies/suppliers.md) |
| P6.2 | A record of authorised disclosures |  | [Data protection and retention policy](../policies/data-protection.md) |
| P6.3 | A record of unauthorised disclosures |  | [Incident response runbook](../incident-response.md) |
| P6.4 | Commitments from vendors and third parties |  | [Supplier and transfer policy](../policies/suppliers.md) |
| P6.5 | Vendors' notice of unauthorised disclosures |  | [Supplier and transfer policy](../policies/suppliers.md) |
| P6.6 | Notice of breaches to the people affected |  | [Incident response runbook](../incident-response.md) |
| P6.7 | Accounting of disclosures to the people asking |  | [Data protection and retention policy](../policies/data-protection.md) |
| P7.1 | Personal information accurate, complete and relevant |  | [Data protection and retention policy](../policies/data-protection.md) |
| P8.1 | Inquiries, complaints and disputes; monitoring compliance |  | [Data protection and retention policy](../policies/data-protection.md) |
