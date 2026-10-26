# HIPAA, 45 CFR Part 164 (Security Rule, Breach Notification, Privacy Rule)

<!-- Written by tools/compliance-docs.py from controls.yaml and frameworks.yaml: change those and run it, not this. -->

Arena's controls serve 33 of the 68 clauses here. Every clause is met by the company that runs Arena, under the document beside it; where Arena has a control, the control is the technical part and its evidence is in Admin → Compliance. The titles are short names, not the clauses' text. [Shared responsibility](../README.md#shared-responsibility), [the controls](../controls.md).

| Clause | | Arena | The company's document |
|---|---|---|---|
| 164.308(a)(1)(i) | Security management process |  | [Risk management policy](../policies/risk-management.md) |
| 164.308(a)(1)(ii)(A) | Risk analysis (required) | [GOV-03](../controls.md#gov-03-what-a-release-ships-is-listed-and-its-known-vulnerabilities-gate-it) | [Risk management policy](../policies/risk-management.md) |
| 164.308(a)(1)(ii)(B) | Risk management (required) | [GOV-03](../controls.md#gov-03-what-a-release-ships-is-listed-and-its-known-vulnerabilities-gate-it) | [Risk management policy](../policies/risk-management.md) |
| 164.308(a)(1)(ii)(C) | Sanction policy (required) |  | [People security policy](../policies/people-security.md) |
| 164.308(a)(1)(ii)(D) | Information system activity review (required) | [LOG-01](../controls.md#log-01-who-did-what-is-recorded), [LOG-04](../controls.md#log-04-the-audit-log-is-sent-on-to-the-companys-siem), [MON-01](../controls.md#mon-01-the-service-is-watched-and-people-are-alerted) | [Information security policy](../policies/information-security.md) |
| 164.308(a)(2) | Assigned security responsibility |  | [Information security policy](../policies/information-security.md) |
| 164.308(a)(3)(i) | Workforce security |  | [People security policy](../policies/people-security.md) |
| 164.308(a)(3)(ii)(A) | Authorisation and/or supervision (addressable) |  | [Access control policy](../policies/access-control.md) |
| 164.308(a)(3)(ii)(B) | Workforce clearance procedure (addressable) | [IAM-07](../controls.md#iam-07-privileged-access-is-reviewed) | [Access control policy](../policies/access-control.md) |
| 164.308(a)(3)(ii)(C) | Termination procedures (addressable) | [IAM-04](../controls.md#iam-04-joiners-and-leavers-follow-the-companys-directory) | [People security policy](../policies/people-security.md) |
| 164.308(a)(4)(i) | Information access management |  | [Access control policy](../policies/access-control.md) |
| 164.308(a)(4)(ii)(A) | Isolating health care clearinghouse functions (required) |  | [Access control policy](../policies/access-control.md) |
| 164.308(a)(4)(ii)(B) | Access authorisation (addressable) | [IAM-05](../controls.md#iam-05-least-privilege-by-roles-and-rules) | [Access control policy](../policies/access-control.md) |
| 164.308(a)(4)(ii)(C) | Access establishment and modification (addressable) | [IAM-04](../controls.md#iam-04-joiners-and-leavers-follow-the-companys-directory), [IAM-07](../controls.md#iam-07-privileged-access-is-reviewed) | [Access control policy](../policies/access-control.md) |
| 164.308(a)(5)(i) | Security awareness and training | [GOV-02](../controls.md#gov-02-everyone-accepts-the-acceptable-use-and-privacy-notice) | [People security policy](../policies/people-security.md) |
| 164.308(a)(5)(ii)(A) | Security reminders (addressable) |  | [People security policy](../policies/people-security.md) |
| 164.308(a)(5)(ii)(B) | Protection from malicious software (addressable) | [OPS-02](../controls.md#ops-02-code-the-model-writes-runs-apart) | [Information security policy](../policies/information-security.md) |
| 164.308(a)(5)(ii)(C) | Log-in monitoring (addressable) | [IAM-01](../controls.md#iam-01-each-person-signs-in-as-themselves-with-a-strong-password) | [Access control policy](../policies/access-control.md) |
| 164.308(a)(5)(ii)(D) | Password management (addressable) | [IAM-01](../controls.md#iam-01-each-person-signs-in-as-themselves-with-a-strong-password) | [Access control policy](../policies/access-control.md) |
| 164.308(a)(6)(i) | Security incident procedures |  | [Incident response runbook](../incident-response.md) |
| 164.308(a)(6)(ii) | Response and reporting (required) | [MON-01](../controls.md#mon-01-the-service-is-watched-and-people-are-alerted), [MON-02](../controls.md#mon-02-security-events-raise-alerts-and-incidents-are-handled-in-time) | [Incident response runbook](../incident-response.md) |
| 164.308(a)(7)(i) | Contingency plan |  | [Backup and business continuity policy](../policies/business-continuity.md) |
| 164.308(a)(7)(ii)(A) | Data backup plan (required) | [OPS-01](../controls.md#ops-01-backups-restored-and-proven) | [Backup and business continuity policy](../policies/business-continuity.md) |
| 164.308(a)(7)(ii)(B) | Disaster recovery plan (required) | [OPS-01](../controls.md#ops-01-backups-restored-and-proven) | [Backup and business continuity policy](../policies/business-continuity.md) |
| 164.308(a)(7)(ii)(C) | Emergency mode operation plan (required) |  | [Backup and business continuity policy](../policies/business-continuity.md) |
| 164.308(a)(7)(ii)(D) | Testing and revision procedures (addressable) | [OPS-01](../controls.md#ops-01-backups-restored-and-proven) | [Backup and business continuity policy](../policies/business-continuity.md) |
| 164.308(a)(7)(ii)(E) | Applications and data criticality analysis (addressable) |  | [Backup and business continuity policy](../policies/business-continuity.md) |
| 164.308(a)(8) | Evaluation | [GOV-01](../controls.md#gov-01-the-controls-are-kept-from-breaking) | [Information security policy](../policies/information-security.md) |
| 164.308(b)(1) | Business associate contracts and other arrangements |  | [Supplier and transfer policy](../policies/suppliers.md) |
| 164.310(a)(1) | Facility access controls |  | [Physical security policy](../policies/physical-security.md) |
| 164.310(a)(2)(i) | Contingency operations (addressable) |  | [Physical security policy](../policies/physical-security.md) |
| 164.310(a)(2)(ii) | Facility security plan (addressable) |  | [Physical security policy](../policies/physical-security.md) |
| 164.310(a)(2)(iii) | Access control and validation procedures (addressable) |  | [Physical security policy](../policies/physical-security.md) |
| 164.310(a)(2)(iv) | Maintenance records (addressable) |  | [Physical security policy](../policies/physical-security.md) |
| 164.310(b) | Workstation use |  | [Acceptable use policy](../policies/acceptable-use.md) |
| 164.310(c) | Workstation security |  | [Acceptable use policy](../policies/acceptable-use.md) |
| 164.310(d)(1) | Device and media controls |  | [Physical security policy](../policies/physical-security.md) |
| 164.310(d)(2)(i) | Disposal (required) |  | [Physical security policy](../policies/physical-security.md) |
| 164.310(d)(2)(ii) | Media re-use (required) |  | [Physical security policy](../policies/physical-security.md) |
| 164.310(d)(2)(iii) | Accountability (addressable) |  | [Physical security policy](../policies/physical-security.md) |
| 164.310(d)(2)(iv) | Data backup and storage (addressable) |  | [Backup and business continuity policy](../policies/business-continuity.md) |
| 164.312(a)(1) | Access control | [IAM-05](../controls.md#iam-05-least-privilege-by-roles-and-rules) | [Access control policy](../policies/access-control.md) |
| 164.312(a)(2)(i) | Unique user identification (required) | [IAM-01](../controls.md#iam-01-each-person-signs-in-as-themselves-with-a-strong-password) | [Access control policy](../policies/access-control.md) |
| 164.312(a)(2)(ii) | Emergency access procedure (required) |  | [Access control policy](../policies/access-control.md) |
| 164.312(a)(2)(iii) | Automatic logoff (addressable) | [IAM-02](../controls.md#iam-02-sessions-end-on-their-own) | [Access control policy](../policies/access-control.md) |
| 164.312(a)(2)(iv) | Encryption and decryption (addressable) | [DAT-05](../controls.md#dat-05-secrets-and-peoples-files-are-encrypted-at-rest) | [Cryptography policy](../policies/cryptography.md) |
| 164.312(b) | Audit controls | [LOG-01](../controls.md#log-01-who-did-what-is-recorded), [LOG-02](../controls.md#log-02-logs-are-kept-and-kept-for-a-set-time), [LOG-03](../controls.md#log-03-nobody-changes-the-audit-log-unseen), [LOG-04](../controls.md#log-04-the-audit-log-is-sent-on-to-the-companys-siem) | [Information security policy](../policies/information-security.md) |
| 164.312(c)(1) | Integrity | [LOG-03](../controls.md#log-03-nobody-changes-the-audit-log-unseen) | [Data protection and retention policy](../policies/data-protection.md) |
| 164.312(c)(2) | Mechanism to authenticate electronic PHI (addressable) | [LOG-03](../controls.md#log-03-nobody-changes-the-audit-log-unseen) | [Data protection and retention policy](../policies/data-protection.md) |
| 164.312(d) | Person or entity authentication | [IAM-01](../controls.md#iam-01-each-person-signs-in-as-themselves-with-a-strong-password), [IAM-03](../controls.md#iam-03-two-factor-sign-in), [IAM-06](../controls.md#iam-06-api-keys-are-personal-revocable-and-checked) | [Access control policy](../policies/access-control.md) |
| 164.312(e)(1) | Transmission security | [NET-01](../controls.md#net-01-encrypted-in-transit-one-way-in) | [Cryptography policy](../policies/cryptography.md) |
| 164.312(e)(2)(i) | Integrity controls (addressable) |  | [Cryptography policy](../policies/cryptography.md) |
| 164.312(e)(2)(ii) | Encryption (addressable) | [NET-01](../controls.md#net-01-encrypted-in-transit-one-way-in) | [Cryptography policy](../policies/cryptography.md) |
| 164.314(a)(1) | Business associate contracts or other arrangements |  | [Supplier and transfer policy](../policies/suppliers.md) |
| 164.316(a) | Policies and procedures |  | [Information security policy](../policies/information-security.md) |
| 164.316(b)(1) | Documentation |  | [Information security policy](../policies/information-security.md) |
| 164.316(b)(2)(i) | Time limit of six years (required) | [DAT-07](../controls.md#dat-07-records-are-kept-for-set-times-and-no-longer) | [Information security policy](../policies/information-security.md) |
| 164.316(b)(2)(ii) | Availability (required) |  | [Information security policy](../policies/information-security.md) |
| 164.316(b)(2)(iii) | Updates (required) |  | [Information security policy](../policies/information-security.md) |
| 164.404 | Notification to individuals of a breach | [MON-02](../controls.md#mon-02-security-events-raise-alerts-and-incidents-are-handled-in-time) | [Incident response runbook](../incident-response.md) |
| 164.406 | Notification to the media of a breach |  | [Incident response runbook](../incident-response.md) |
| 164.408 | Notification to the Secretary of a breach | [MON-02](../controls.md#mon-02-security-events-raise-alerts-and-incidents-are-handled-in-time) | [Incident response runbook](../incident-response.md) |
| 164.410 | Notification by a business associate |  | [Supplier and transfer policy](../policies/suppliers.md) |
| 164.502(b) | Minimum necessary | [DAT-04](../controls.md#dat-04-personal-data-and-secrets-kept-from-the-model), [DAT-08](../controls.md#dat-08-personal-and-health-data-kept-in-bounds) | [Data protection and retention policy](../policies/data-protection.md) |
| 164.514(b) | De-identification | [DAT-08](../controls.md#dat-08-personal-and-health-data-kept-in-bounds) | [Data protection and retention policy](../policies/data-protection.md) |
| 164.524 | Access of individuals to their health information | [DAT-03](../controls.md#dat-03-a-persons-data-exported), [DAT-06](../controls.md#dat-06-peoples-requests-about-their-data-are-answered) | [Data protection and retention policy](../policies/data-protection.md) |
| 164.526 | Amendment of health information | [DAT-06](../controls.md#dat-06-peoples-requests-about-their-data-are-answered) | [Data protection and retention policy](../policies/data-protection.md) |
| 164.528 | Accounting of disclosures |  | [Data protection and retention policy](../policies/data-protection.md) |
