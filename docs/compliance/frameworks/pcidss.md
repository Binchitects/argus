# PCI DSS v4.0.1: its twelve requirements and their sections

<!-- Written by tools/compliance-docs.py from controls.yaml and frameworks.yaml: change those and run it, not this. -->

Arena's controls serve 28 of the 63 clauses here. Every clause is met by the company that runs Arena, under the document beside it; where Arena has a control, the control is the technical part and its evidence is in Admin → Compliance. The titles are short names, not the clauses' text. [Shared responsibility](../README.md#shared-responsibility), [the controls](../controls.md).

| Clause | | Arena | The company's document |
|---|---|---|---|
| 1.1 | Processes for network security controls are defined |  | [Information security policy](../policies/information-security.md) |
| 1.2 | Network security controls are configured and maintained |  | [Information security policy](../policies/information-security.md) |
| 1.3 | Network access to and from the cardholder data environment is restricted | [NET-01](../controls.md#net-01-encrypted-in-transit-one-way-in) | [Information security policy](../policies/information-security.md) |
| 1.4 | Connections between trusted and untrusted networks are controlled | [NET-01](../controls.md#net-01-encrypted-in-transit-one-way-in) | [Information security policy](../policies/information-security.md) |
| 1.5 | Devices that join untrusted networks and the CDE are risks mitigated |  | [Information security policy](../policies/information-security.md) |
| 2.1 | Processes for secure configurations are defined |  | [Change and vulnerability management policy](../policies/change-management.md) |
| 2.2 | System components are configured and managed securely |  | [Change and vulnerability management policy](../policies/change-management.md) |
| 2.3 | Wireless environments are configured and managed securely |  | [Information security policy](../policies/information-security.md) |
| 3.1 | Processes for protecting stored account data are defined |  | [Data protection and retention policy](../policies/data-protection.md) |
| 3.2 | Storage of account data is kept to a minimum | [DAT-01](../controls.md#dat-01-chats-and-files-are-deleted-when-their-time-is-up), [DAT-04](../controls.md#dat-04-personal-data-and-secrets-kept-from-the-model), [DAT-07](../controls.md#dat-07-records-are-kept-for-set-times-and-no-longer), [DAT-08](../controls.md#dat-08-personal-and-health-data-kept-in-bounds) | [Data protection and retention policy](../policies/data-protection.md) |
| 3.3 | Sensitive authentication data is not stored after authorization | [DAT-08](../controls.md#dat-08-personal-and-health-data-kept-in-bounds) | [Data protection and retention policy](../policies/data-protection.md) |
| 3.4 | Display and copying of full card numbers are restricted |  | [Data protection and retention policy](../policies/data-protection.md) |
| 3.5 | Card numbers are secured wherever they are stored | [DAT-05](../controls.md#dat-05-secrets-and-peoples-files-are-encrypted-at-rest) | [Cryptography policy](../policies/cryptography.md) |
| 3.6 | Keys protecting stored account data are secured | [DAT-05](../controls.md#dat-05-secrets-and-peoples-files-are-encrypted-at-rest) | [Cryptography policy](../policies/cryptography.md) |
| 3.7 | Key management covers the keys' whole life |  | [Cryptography policy](../policies/cryptography.md) |
| 4.1 | Processes for protecting data in transmission are defined |  | [Cryptography policy](../policies/cryptography.md) |
| 4.2 | Card numbers are protected with strong cryptography in transmission | [NET-01](../controls.md#net-01-encrypted-in-transit-one-way-in) | [Cryptography policy](../policies/cryptography.md) |
| 5.1 | Processes for protection from malicious software are defined |  | [Change and vulnerability management policy](../policies/change-management.md) |
| 5.2 | Malicious software is prevented or detected and addressed |  | [Change and vulnerability management policy](../policies/change-management.md) |
| 5.3 | Anti-malware mechanisms are active and monitored |  | [Change and vulnerability management policy](../policies/change-management.md) |
| 5.4 | Anti-phishing mechanisms protect users |  | [People security policy](../policies/people-security.md) |
| 6.1 | Processes for secure systems and software are defined |  | [Change and vulnerability management policy](../policies/change-management.md) |
| 6.2 | Bespoke and custom software is developed securely | [VUL-01](../controls.md#vul-01-repositories-are-reviewed-for-vulnerabilities-fixed-or-accepted-in-time) | [Change and vulnerability management policy](../policies/change-management.md) |
| 6.3 | Security vulnerabilities are identified and addressed | [GOV-03](../controls.md#gov-03-what-a-release-ships-is-listed-and-its-known-vulnerabilities-gate-it), [VUL-01](../controls.md#vul-01-repositories-are-reviewed-for-vulnerabilities-fixed-or-accepted-in-time) | [Change and vulnerability management policy](../policies/change-management.md) |
| 6.4 | Public-facing web applications are protected against attacks |  | [Change and vulnerability management policy](../policies/change-management.md) |
| 6.5 | Changes to system components are managed securely |  | [Change and vulnerability management policy](../policies/change-management.md) |
| 7.1 | Processes for restricting access by need to know are defined |  | [Access control policy](../policies/access-control.md) |
| 7.2 | Access to system components and data is defined and assigned | [IAM-05](../controls.md#iam-05-least-privilege-by-roles-and-rules), [IAM-07](../controls.md#iam-07-privileged-access-is-reviewed) | [Access control policy](../policies/access-control.md) |
| 7.3 | Access is managed through an access control system | [IAM-05](../controls.md#iam-05-least-privilege-by-roles-and-rules) | [Access control policy](../policies/access-control.md) |
| 8.1 | Processes for identifying users and authenticating them are defined |  | [Access control policy](../policies/access-control.md) |
| 8.2 | User identification and accounts are strictly managed | [IAM-01](../controls.md#iam-01-each-person-signs-in-as-themselves-with-a-strong-password), [IAM-02](../controls.md#iam-02-sessions-end-on-their-own), [IAM-04](../controls.md#iam-04-joiners-and-leavers-follow-the-companys-directory) | [Access control policy](../policies/access-control.md) |
| 8.3 | Strong authentication is established and managed | [IAM-01](../controls.md#iam-01-each-person-signs-in-as-themselves-with-a-strong-password) | [Access control policy](../policies/access-control.md) |
| 8.4 | Multi-factor authentication secures access into the CDE | [IAM-03](../controls.md#iam-03-two-factor-sign-in) | [Access control policy](../policies/access-control.md) |
| 8.5 | Multi-factor authentication systems prevent misuse | [IAM-03](../controls.md#iam-03-two-factor-sign-in) | [Access control policy](../policies/access-control.md) |
| 8.6 | Application and system accounts are strictly managed | [IAM-06](../controls.md#iam-06-api-keys-are-personal-revocable-and-checked) | [Access control policy](../policies/access-control.md) |
| 9.1 | Processes for restricting physical access are defined |  | [Physical security policy](../policies/physical-security.md) |
| 9.2 | Physical access controls manage entry to facilities |  | [Physical security policy](../policies/physical-security.md) |
| 9.3 | Physical access for personnel and visitors is authorized |  | [Physical security policy](../policies/physical-security.md) |
| 9.4 | Media with cardholder data is securely handled |  | [Physical security policy](../policies/physical-security.md) |
| 9.5 | Point-of-interaction devices are protected from tampering |  | [Physical security policy](../policies/physical-security.md) |
| 10.1 | Processes for logging and monitoring are defined |  | [Logging and monitoring policy](../policies/logging-monitoring.md) |
| 10.2 | Audit logs support detection and forensic analysis | [LOG-01](../controls.md#log-01-who-did-what-is-recorded) | [Logging and monitoring policy](../policies/logging-monitoring.md) |
| 10.3 | Audit logs are protected from destruction and modification | [LOG-03](../controls.md#log-03-nobody-changes-the-audit-log-unseen) | [Logging and monitoring policy](../policies/logging-monitoring.md) |
| 10.4 | Audit logs are reviewed for anomalies | [LOG-04](../controls.md#log-04-the-audit-log-is-sent-on-to-the-companys-siem), [MON-02](../controls.md#mon-02-security-events-raise-alerts-and-incidents-are-handled-in-time) | [Logging and monitoring policy](../policies/logging-monitoring.md) |
| 10.5 | Audit log history is retained and available | [LOG-02](../controls.md#log-02-logs-are-kept-and-kept-for-a-set-time), [DAT-07](../controls.md#dat-07-records-are-kept-for-set-times-and-no-longer) | [Logging and monitoring policy](../policies/logging-monitoring.md) |
| 10.6 | Time synchronization keeps time consistent |  | [Logging and monitoring policy](../policies/logging-monitoring.md) |
| 10.7 | Failures of critical security controls are detected and answered | [MON-01](../controls.md#mon-01-the-service-is-watched-and-people-are-alerted) | [Logging and monitoring policy](../policies/logging-monitoring.md) |
| 11.1 | Processes for regular security testing are defined |  | [Change and vulnerability management policy](../policies/change-management.md) |
| 11.2 | Wireless access points are identified and monitored |  | [Information security policy](../policies/information-security.md) |
| 11.3 | Vulnerabilities are regularly identified and addressed | [VUL-01](../controls.md#vul-01-repositories-are-reviewed-for-vulnerabilities-fixed-or-accepted-in-time) | [Change and vulnerability management policy](../policies/change-management.md) |
| 11.4 | Penetration testing is regularly performed |  | [Change and vulnerability management policy](../policies/change-management.md) |
| 11.5 | Intrusions and unexpected file changes are detected |  | [Logging and monitoring policy](../policies/logging-monitoring.md) |
| 11.6 | Unauthorized changes on payment pages are detected |  | [Change and vulnerability management policy](../policies/change-management.md) |
| 12.1 | An information security policy is known and current | [GOV-06](../controls.md#gov-06-policies-are-owned-approved-reviewed-and-read) | [Information security policy](../policies/information-security.md) |
| 12.2 | Acceptable use policies are defined and implemented | [GOV-02](../controls.md#gov-02-everyone-accepts-the-acceptable-use-and-privacy-notice) | [Acceptable use policy](../policies/acceptable-use.md) |
| 12.3 | Risks to the CDE are identified and managed | [GOV-04](../controls.md#gov-04-risks-are-assessed-treated-and-reviewed) | [Risk management policy](../policies/risk-management.md) |
| 12.4 | PCI DSS compliance is managed | [GOV-01](../controls.md#gov-01-the-controls-are-kept-from-breaking), [GOV-07](../controls.md#gov-07-audits-their-findings-and-corrective-actions-are-kept) | [The management system (ISMS and AIMS)](../isms.md) |
| 12.5 | PCI DSS scope is documented and validated |  | [The management system (ISMS and AIMS)](../isms.md) |
| 12.6 | Security awareness education is ongoing |  | [People security policy](../policies/people-security.md) |
| 12.7 | Personnel are screened |  | [People security policy](../policies/people-security.md) |
| 12.8 | Risks from third-party service providers are managed | [GOV-08](../controls.md#gov-08-suppliers-are-registered-agreed-with-and-reviewed) | [Supplier and transfer policy](../policies/suppliers.md) |
| 12.9 | Third-party service providers support their customers' compliance |  | [Supplier and transfer policy](../policies/suppliers.md) |
| 12.10 | Security incidents are responded to immediately | [MON-02](../controls.md#mon-02-security-events-raise-alerts-and-incidents-are-handled-in-time) | [Incident response runbook](../incident-response.md) |
