# Common Criteria CC:2022 (ISO/IEC 15408): the functional (Part 2) and assurance (Part 3) families

<!-- Written by tools/compliance-docs.py from controls.yaml and frameworks.yaml: change those and run it, not this. -->

Arena's controls serve 25 of the 121 clauses here. Every clause is met by the company that runs Arena, under the document beside it; where Arena has a control, the control is the technical part and its evidence is in Admin → Compliance. The titles are short names, not the clauses' text. [Shared responsibility](../README.md#shared-responsibility), [the controls](../controls.md).

| Clause | | Arena | The company's document |
|---|---|---|---|
| FAU_ARP | Security audit automatic response |  | [Logging and monitoring policy](../policies/logging-monitoring.md) |
| FAU_GEN | Security audit data generation | [LOG-01](../controls.md#log-01-who-did-what-is-recorded) | [Logging and monitoring policy](../policies/logging-monitoring.md) |
| FAU_SAA | Security audit analysis |  | [Logging and monitoring policy](../policies/logging-monitoring.md) |
| FAU_SAR | Security audit review | [LOG-04](../controls.md#log-04-the-audit-log-is-sent-on-to-the-companys-siem) | [Logging and monitoring policy](../policies/logging-monitoring.md) |
| FAU_SEL | Security audit event selection |  | [Logging and monitoring policy](../policies/logging-monitoring.md) |
| FAU_STG | Security audit data storage | [LOG-02](../controls.md#log-02-logs-are-kept-and-kept-for-a-set-time), [LOG-03](../controls.md#log-03-nobody-changes-the-audit-log-unseen) | [Logging and monitoring policy](../policies/logging-monitoring.md) |
| FCO_NRO | Non-repudiation of origin |  | [Logging and monitoring policy](../policies/logging-monitoring.md) |
| FCO_NRR | Non-repudiation of receipt |  | [Logging and monitoring policy](../policies/logging-monitoring.md) |
| FCS_CKM | Cryptographic key management | [DAT-05](../controls.md#dat-05-secrets-and-peoples-files-are-encrypted-at-rest) | [Cryptography policy](../policies/cryptography.md) |
| FCS_COP | Cryptographic operation | [DAT-05](../controls.md#dat-05-secrets-and-peoples-files-are-encrypted-at-rest), [NET-01](../controls.md#net-01-encrypted-in-transit-one-way-in) | [Cryptography policy](../policies/cryptography.md) |
| FCS_RBG | Random bit generation |  | [Cryptography policy](../policies/cryptography.md) |
| FCS_RNG | Generation of random numbers |  | [Cryptography policy](../policies/cryptography.md) |
| FDP_ACC | Access control policy | [IAM-05](../controls.md#iam-05-least-privilege-by-roles-and-rules) | [Data protection and retention policy](../policies/data-protection.md) |
| FDP_ACF | Access control functions | [IAM-05](../controls.md#iam-05-least-privilege-by-roles-and-rules) | [Data protection and retention policy](../policies/data-protection.md) |
| FDP_DAU | Data authentication |  | [Data protection and retention policy](../policies/data-protection.md) |
| FDP_ETC | Export from the TOE |  | [Data protection and retention policy](../policies/data-protection.md) |
| FDP_IFC | Information flow control policy | [DAT-04](../controls.md#dat-04-personal-data-and-secrets-kept-from-the-model) | [Data protection and retention policy](../policies/data-protection.md) |
| FDP_IFF | Information flow control functions | [DAT-04](../controls.md#dat-04-personal-data-and-secrets-kept-from-the-model) | [Data protection and retention policy](../policies/data-protection.md) |
| FDP_IRC | Information Retention Control |  | [Data protection and retention policy](../policies/data-protection.md) |
| FDP_ITC | Import from outside of the TOE |  | [Data protection and retention policy](../policies/data-protection.md) |
| FDP_ITT | Internal TOE transfer |  | [Data protection and retention policy](../policies/data-protection.md) |
| FDP_RIP | Residual information protection |  | [Data protection and retention policy](../policies/data-protection.md) |
| FDP_ROL | Rollback |  | [Data protection and retention policy](../policies/data-protection.md) |
| FDP_SDC | Stored data confidentiality | [DAT-05](../controls.md#dat-05-secrets-and-peoples-files-are-encrypted-at-rest) | [Data protection and retention policy](../policies/data-protection.md) |
| FDP_SDI | Stored data integrity |  | [Data protection and retention policy](../policies/data-protection.md) |
| FDP_UCT | Inter-TSF user data confidentiality transfer protection |  | [Data protection and retention policy](../policies/data-protection.md) |
| FDP_UIT | Inter-TSF user data integrity transfer protection |  | [Data protection and retention policy](../policies/data-protection.md) |
| FIA_AFL | Authentication failures | [IAM-01](../controls.md#iam-01-each-person-signs-in-as-themselves-with-a-strong-password) | [Access control policy](../policies/access-control.md) |
| FIA_API | Authentication proof of identity |  | [Access control policy](../policies/access-control.md) |
| FIA_ATD | User attribute definition |  | [Access control policy](../policies/access-control.md) |
| FIA_SOS | Specification of secrets | [IAM-01](../controls.md#iam-01-each-person-signs-in-as-themselves-with-a-strong-password) | [Access control policy](../policies/access-control.md) |
| FIA_UAU | User authentication | [IAM-01](../controls.md#iam-01-each-person-signs-in-as-themselves-with-a-strong-password), [IAM-03](../controls.md#iam-03-two-factor-sign-in), [IAM-06](../controls.md#iam-06-api-keys-are-personal-revocable-and-checked) | [Access control policy](../policies/access-control.md) |
| FIA_UID | User identification | [IAM-01](../controls.md#iam-01-each-person-signs-in-as-themselves-with-a-strong-password) | [Access control policy](../policies/access-control.md) |
| FIA_USB | User-subject binding |  | [Access control policy](../policies/access-control.md) |
| FMT_LIM | Limited capabilities and availability |  | [Access control policy](../policies/access-control.md) |
| FMT_MOF | Management of functions in TSF |  | [Access control policy](../policies/access-control.md) |
| FMT_MSA | Management of security attributes |  | [Access control policy](../policies/access-control.md) |
| FMT_MTD | Management of TSF data |  | [Access control policy](../policies/access-control.md) |
| FMT_REV | Revocation |  | [Access control policy](../policies/access-control.md) |
| FMT_SAE | Security attribute expiration |  | [Access control policy](../policies/access-control.md) |
| FMT_SMF | Specification of Management Functions |  | [Access control policy](../policies/access-control.md) |
| FMT_SMR | Security management roles | [IAM-05](../controls.md#iam-05-least-privilege-by-roles-and-rules) | [Access control policy](../policies/access-control.md) |
| FPR_ANO | Anonymity |  | [Data protection and retention policy](../policies/data-protection.md) |
| FPR_PSE | Pseudonymity |  | [Data protection and retention policy](../policies/data-protection.md) |
| FPR_UNL | Unlinkability |  | [Data protection and retention policy](../policies/data-protection.md) |
| FPR_UNO | Unobservability |  | [Data protection and retention policy](../policies/data-protection.md) |
| FPT_EMS | TOE emanation |  | [Information security policy](../policies/information-security.md) |
| FPT_FLS | Fail secure |  | [Information security policy](../policies/information-security.md) |
| FPT_INI | TSF initialization |  | [Information security policy](../policies/information-security.md) |
| FPT_ITA | Availability of exported TSF data |  | [Information security policy](../policies/information-security.md) |
| FPT_ITC | Confidentiality of exported TSF data |  | [Information security policy](../policies/information-security.md) |
| FPT_ITI | Integrity of exported TSF data |  | [Information security policy](../policies/information-security.md) |
| FPT_ITT | Internal TOE TSF data transfer |  | [Information security policy](../policies/information-security.md) |
| FPT_PHP | TSF physical protection |  | [Information security policy](../policies/information-security.md) |
| FPT_RCV | Trusted recovery | [OPS-01](../controls.md#ops-01-backups-restored-and-proven) | [Information security policy](../policies/information-security.md) |
| FPT_RPL | Replay detection |  | [Information security policy](../policies/information-security.md) |
| FPT_SSP | State synchrony protocol |  | [Information security policy](../policies/information-security.md) |
| FPT_STM | Time stamps |  | [Information security policy](../policies/information-security.md) |
| FPT_TDC | Inter-TSF TSF data consistency |  | [Information security policy](../policies/information-security.md) |
| FPT_TEE | Testing of external entities |  | [Information security policy](../policies/information-security.md) |
| FPT_TRC | Internal TOE TSF data replication consistency |  | [Information security policy](../policies/information-security.md) |
| FPT_TST | TSF self-test |  | [Information security policy](../policies/information-security.md) |
| FRU_FLT | Fault tolerance |  | [Backup and business continuity policy](../policies/business-continuity.md) |
| FRU_PRS | Priority of service |  | [Backup and business continuity policy](../policies/business-continuity.md) |
| FRU_RSA | Resource allocation |  | [Backup and business continuity policy](../policies/business-continuity.md) |
| FTA_LSA | Limitation on scope of selectable attributes |  | [Access control policy](../policies/access-control.md) |
| FTA_MCS | Limitation on multiple concurrent sessions |  | [Access control policy](../policies/access-control.md) |
| FTA_SSL | Session locking and termination | [IAM-02](../controls.md#iam-02-sessions-end-on-their-own) | [Access control policy](../policies/access-control.md) |
| FTA_TAB | TOE access banners | [GOV-02](../controls.md#gov-02-everyone-accepts-the-acceptable-use-and-privacy-notice) | [Access control policy](../policies/access-control.md) |
| FTA_TAH | TOE access history |  | [Access control policy](../policies/access-control.md) |
| FTA_TSE | TOE session establishment |  | [Access control policy](../policies/access-control.md) |
| FTP_ITC | Inter-TSF trusted channel | [NET-01](../controls.md#net-01-encrypted-in-transit-one-way-in) | [Cryptography policy](../policies/cryptography.md) |
| FTP_PRO | Trusted channel protocol |  | [Cryptography policy](../policies/cryptography.md) |
| FTP_TRP | Trusted path | [NET-01](../controls.md#net-01-encrypted-in-transit-one-way-in) | [Cryptography policy](../policies/cryptography.md) |
| APE_INT | PP introduction |  | [Product evaluation (Common Criteria and FIPS 140-3)](../product-evaluation.md) |
| APE_CCL | Conformance claims |  | [Product evaluation (Common Criteria and FIPS 140-3)](../product-evaluation.md) |
| APE_SPD | Security problem definition |  | [Product evaluation (Common Criteria and FIPS 140-3)](../product-evaluation.md) |
| APE_OBJ | Security objectives |  | [Product evaluation (Common Criteria and FIPS 140-3)](../product-evaluation.md) |
| APE_ECD | Extended components definition |  | [Product evaluation (Common Criteria and FIPS 140-3)](../product-evaluation.md) |
| APE_REQ | Security requirements |  | [Product evaluation (Common Criteria and FIPS 140-3)](../product-evaluation.md) |
| ACE_INT | PP-Module introduction |  | [Product evaluation (Common Criteria and FIPS 140-3)](../product-evaluation.md) |
| ACE_CCL | PP-Module conformance claims |  | [Product evaluation (Common Criteria and FIPS 140-3)](../product-evaluation.md) |
| ACE_SPD | PP-Module security problem definition |  | [Product evaluation (Common Criteria and FIPS 140-3)](../product-evaluation.md) |
| ACE_OBJ | PP-Module security objectives |  | [Product evaluation (Common Criteria and FIPS 140-3)](../product-evaluation.md) |
| ACE_ECD | PP-Module extended components definition |  | [Product evaluation (Common Criteria and FIPS 140-3)](../product-evaluation.md) |
| ACE_REQ | PP-Module security requirements |  | [Product evaluation (Common Criteria and FIPS 140-3)](../product-evaluation.md) |
| ACE_MCO | PP-Module consistency |  | [Product evaluation (Common Criteria and FIPS 140-3)](../product-evaluation.md) |
| ACE_CCO | PP-Configuration consistency |  | [Product evaluation (Common Criteria and FIPS 140-3)](../product-evaluation.md) |
| ASE_INT | ST introduction |  | [Product evaluation (Common Criteria and FIPS 140-3)](../product-evaluation.md) |
| ASE_CCL | Conformance claims |  | [Product evaluation (Common Criteria and FIPS 140-3)](../product-evaluation.md) |
| ASE_SPD | Security problem definition |  | [Product evaluation (Common Criteria and FIPS 140-3)](../product-evaluation.md) |
| ASE_OBJ | Security objectives |  | [Product evaluation (Common Criteria and FIPS 140-3)](../product-evaluation.md) |
| ASE_ECD | Extended components definition |  | [Product evaluation (Common Criteria and FIPS 140-3)](../product-evaluation.md) |
| ASE_REQ | Security requirements |  | [Product evaluation (Common Criteria and FIPS 140-3)](../product-evaluation.md) |
| ASE_TSS | TOE summary specification | [GOV-11](../controls.md#gov-11-readiness-is-measured-and-the-evidence-is-kept-over-time) | [Product evaluation (Common Criteria and FIPS 140-3)](../product-evaluation.md) |
| ADV_ARC | Security Architecture |  | [Product evaluation (Common Criteria and FIPS 140-3)](../product-evaluation.md) |
| ADV_FSP | Functional specification |  | [Product evaluation (Common Criteria and FIPS 140-3)](../product-evaluation.md) |
| ADV_IMP | Implementation representation |  | [Product evaluation (Common Criteria and FIPS 140-3)](../product-evaluation.md) |
| ADV_INT | TSF internals |  | [Product evaluation (Common Criteria and FIPS 140-3)](../product-evaluation.md) |
| ADV_SPM | Security policy modelling |  | [Product evaluation (Common Criteria and FIPS 140-3)](../product-evaluation.md) |
| ADV_TDS | TOE design |  | [Product evaluation (Common Criteria and FIPS 140-3)](../product-evaluation.md) |
| AGD_OPE | Operational user guidance |  | [Product evaluation (Common Criteria and FIPS 140-3)](../product-evaluation.md) |
| AGD_PRE | Preparative procedures |  | [Product evaluation (Common Criteria and FIPS 140-3)](../product-evaluation.md) |
| ALC_CMC | CM capabilities | [GOV-01](../controls.md#gov-01-the-controls-are-kept-from-breaking) | [Product evaluation (Common Criteria and FIPS 140-3)](../product-evaluation.md) |
| ALC_CMS | CM scope |  | [Product evaluation (Common Criteria and FIPS 140-3)](../product-evaluation.md) |
| ALC_DEL | Delivery |  | [Product evaluation (Common Criteria and FIPS 140-3)](../product-evaluation.md) |
| ALC_DVS | Developer environment security |  | [Product evaluation (Common Criteria and FIPS 140-3)](../product-evaluation.md) |
| ALC_FLR | Flaw remediation | [GOV-03](../controls.md#gov-03-what-a-release-ships-is-listed-and-its-known-vulnerabilities-gate-it), [VUL-01](../controls.md#vul-01-repositories-are-reviewed-for-vulnerabilities-fixed-or-accepted-in-time) | [Product evaluation (Common Criteria and FIPS 140-3)](../product-evaluation.md) |
| ALC_LCD | Development Life-cycle definition |  | [Product evaluation (Common Criteria and FIPS 140-3)](../product-evaluation.md) |
| ALC_TDA | TOE Development Artefacts |  | [Product evaluation (Common Criteria and FIPS 140-3)](../product-evaluation.md) |
| ALC_TAT | Tools and techniques |  | [Product evaluation (Common Criteria and FIPS 140-3)](../product-evaluation.md) |
| ATE_COV | Coverage |  | [Product evaluation (Common Criteria and FIPS 140-3)](../product-evaluation.md) |
| ATE_DPT | Depth |  | [Product evaluation (Common Criteria and FIPS 140-3)](../product-evaluation.md) |
| ATE_FUN | Functional tests | [GOV-01](../controls.md#gov-01-the-controls-are-kept-from-breaking), [GOV-11](../controls.md#gov-11-readiness-is-measured-and-the-evidence-is-kept-over-time) | [Product evaluation (Common Criteria and FIPS 140-3)](../product-evaluation.md) |
| ATE_IND | Independent testing |  | [Product evaluation (Common Criteria and FIPS 140-3)](../product-evaluation.md) |
| AVA_VAN | Vulnerability analysis | [GOV-03](../controls.md#gov-03-what-a-release-ships-is-listed-and-its-known-vulnerabilities-gate-it), [VUL-01](../controls.md#vul-01-repositories-are-reviewed-for-vulnerabilities-fixed-or-accepted-in-time) | [Product evaluation (Common Criteria and FIPS 140-3)](../product-evaluation.md) |
| ACO_COR | Composition rationale |  | [Product evaluation (Common Criteria and FIPS 140-3)](../product-evaluation.md) |
| ACO_DEV | Development evidence |  | [Product evaluation (Common Criteria and FIPS 140-3)](../product-evaluation.md) |
| ACO_REL | Reliance of dependent component |  | [Product evaluation (Common Criteria and FIPS 140-3)](../product-evaluation.md) |
| ACO_CTT | Composed TOE testing |  | [Product evaluation (Common Criteria and FIPS 140-3)](../product-evaluation.md) |
| ACO_VUL | Composition vulnerability analysis |  | [Product evaluation (Common Criteria and FIPS 140-3)](../product-evaluation.md) |
