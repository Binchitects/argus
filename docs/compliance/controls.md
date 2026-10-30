# The controls

<!-- Written by tools/compliance-docs.py from controls.yaml and frameworks.yaml: change those and run it, not this. -->

The 37 controls Arena provides, from [controls.yaml](controls.yaml). How they are kept: [README](README.md#controls-and-how-they-are-kept). Each framework's clauses and the controls that serve them: [ISO/IEC 27001:2022, clauses 4 to 10 and Annex A](frameworks/iso27001.md), [SOC 2, Trust Services Criteria](frameworks/soc2.md), [HIPAA, 45 CFR Part 164 (Security Rule, Breach Notification, Privacy Rule)](frameworks/hipaa.md), [The GDPR, Regulation (EU) 2016/679](frameworks/gdpr.md), [ISO/IEC 42001:2023, the AI management system: clauses 4 to 10 and Annex A](frameworks/iso42001.md), [ISO 22301:2019, business continuity management systems](frameworks/iso22301.md), [PCI DSS v4.0.1: its twelve requirements and their sections](frameworks/pcidss.md), [CSA Cloud Controls Matrix v4 (CSA STAR), by its domains](frameworks/csaccm.md), [NIST Cybersecurity Framework 2.0: its categories and subcategories](frameworks/nistcsf.md), [NIST SP 800-53 Rev. 5 (5.2.0), with the low, moderate, high and privacy baselines](frameworks/nist80053.md), [NIST SP 800-171 Rev. 3, protecting controlled unclassified information (CMMC builds on it)](frameworks/nist800171.md), [NIST AI Risk Management Framework 1.0 (AI 100-1): Govern, Map, Measure and Manage, as its Playbook lists them](frameworks/airmf.md), [EU Artificial Intelligence Act, Regulation (EU) 2024/1689: its articles and annexes](frameworks/euaiact.md), [Common Criteria CC:2022 (ISO/IEC 15408): the functional (Part 2) and assurance (Part 3) families](frameworks/commoncriteria.md).

## People and access (IAM)

### IAM-01: Each person signs in as themselves, with a strong password

Every person has their own account. Passwords must score 3 or more on zxcvbn, with the person's own names counted against them; a wrong password and an unknown name get the same answer; guessing one account, spraying many and floods are stopped and lock out.

**Serves**

- [ISO/IEC 27001:2022, clauses 4 to 10 and Annex A](frameworks/iso27001.md): A.5.16 Identity management; A.5.17 Authentication information; A.8.5 Secure authentication
- [SOC 2, Trust Services Criteria](frameworks/soc2.md): CC6.1 Logical access security software, infrastructure and architectures; CC6.2 Registering and authorising new users
- [HIPAA, 45 CFR Part 164 (Security Rule, Breach Notification, Privacy Rule)](frameworks/hipaa.md): 164.308(a)(5)(ii)(C) Log-in monitoring (addressable); 164.308(a)(5)(ii)(D) Password management (addressable); 164.312(a)(2)(i) Unique user identification (required); 164.312(d) Person or entity authentication
- [The GDPR, Regulation (EU) 2016/679](frameworks/gdpr.md): Art.32 Security of processing
- [PCI DSS v4.0.1: its twelve requirements and their sections](frameworks/pcidss.md): 8.2 User identification and accounts are strictly managed; 8.3 Strong authentication is established and managed
- [CSA Cloud Controls Matrix v4 (CSA STAR), by its domains](frameworks/csaccm.md): IAM Identity and access management
- [NIST Cybersecurity Framework 2.0: its categories and subcategories](frameworks/nistcsf.md): PR.AA-01 Identities and credentials for authorized users, services, and hardware are managed by the organization; PR.AA-03 Users, services, and hardware are authenticated
- [NIST SP 800-53 Rev. 5 (5.2.0), with the low, moderate, high and privacy baselines](frameworks/nist80053.md): AC-2 Account Management; AC-7 Unsuccessful Logon Attempts; IA-2 Identification and Authentication (Organizational Users); IA-4 Identifier Management; IA-5 Authenticator Management; IA-5(1) Password-based Authentication
- [NIST SP 800-171 Rev. 3, protecting controlled unclassified information (CMMC builds on it)](frameworks/nist800171.md): 03.01.01 Account Management; 03.01.08 Unsuccessful Logon Attempts; 03.05.01 User Identification and Authentication; 03.05.05 Identifier Management; 03.05.07 Password Management
- [Common Criteria CC:2022 (ISO/IEC 15408): the functional (Part 2) and assurance (Part 3) families](frameworks/commoncriteria.md): FIA_UID User identification; FIA_UAU User authentication; FIA_AFL Authentication failures; FIA_SOS Specification of secrets

**Documented in** [authentication.md#what-protects-the-sign-in](../authentication.md#what-protects-the-sign-in)

**Proved by**

- `Llm.Tests/IdentityTests.Wrong_password_and_unknown_name_get_the_same_answer`
- `Llm.Tests/IdentityTests.A_person_changes_their_own_password_only_with_the_current_one_and_a_strong_new_one`
- `Llm.Tests/IdentityTests.Five_failures_ban_that_account_from_that_address_but_not_colleagues_behind_it`
- `Llm.Tests/IdentityTests.Spraying_many_accounts_from_one_address_bans_the_address`
- `Llm.Tests/IdentityTests.Ten_failures_lock_the_account_from_any_address`
- `Llm.Tests/ConcurrentSignInTests.Parallel_wrong_passwords_still_reach_the_lockout`

**Watched by** `app:passwords` (Admin → Compliance, and `./arenactl check compliance`)

**The company's part** A password policy people are told about; no shared accounts; the first admin's password (ADMIN_PASSWORD) kept as a break-glass secret.

### IAM-02: Sessions end on their own

A session ends after an hour without activity and after 12 hours in any case (30 days only when the person asks to stay signed in); each one is re-checked against the account every minute, so a disabled person or a reset password ends it at once. The cookie is Secure, HttpOnly and SameSite.

**Serves**

- [ISO/IEC 27001:2022, clauses 4 to 10 and Annex A](frameworks/iso27001.md): A.8.5 Secure authentication
- [SOC 2, Trust Services Criteria](frameworks/soc2.md): CC6.1 Logical access security software, infrastructure and architectures
- [HIPAA, 45 CFR Part 164 (Security Rule, Breach Notification, Privacy Rule)](frameworks/hipaa.md): 164.312(a)(2)(iii) Automatic logoff (addressable)
- [The GDPR, Regulation (EU) 2016/679](frameworks/gdpr.md): Art.32 Security of processing
- [PCI DSS v4.0.1: its twelve requirements and their sections](frameworks/pcidss.md): 8.2 User identification and accounts are strictly managed
- [CSA Cloud Controls Matrix v4 (CSA STAR), by its domains](frameworks/csaccm.md): IAM Identity and access management
- [NIST Cybersecurity Framework 2.0: its categories and subcategories](frameworks/nistcsf.md): PR.AA-03 Users, services, and hardware are authenticated
- [NIST SP 800-53 Rev. 5 (5.2.0), with the low, moderate, high and privacy baselines](frameworks/nist80053.md): AC-11 Device Lock; AC-12 Session Termination; SC-23 Session Authenticity
- [NIST SP 800-171 Rev. 3, protecting controlled unclassified information (CMMC builds on it)](frameworks/nist800171.md): 03.01.10 Device Lock; 03.01.11 Session Termination
- [Common Criteria CC:2022 (ISO/IEC 15408): the functional (Part 2) and assurance (Part 3) families](frameworks/commoncriteria.md): FTA_SSL Session locking and termination

**Documented in** [authentication.md#what-protects-the-sign-in](../authentication.md#what-protects-the-sign-in)

**Proved by**

- `Llm.Tests/SessionCapTests.Refreshing_a_session_does_not_restart_the_twelve_hour_cap`
- `Llm.Tests/IdentityTests.A_password_reset_ends_the_persons_sessions_and_only_the_new_password_works`
- `Llm.Tests/IdentityTests.Disabling_signs_out_and_blocks_keys_and_enabling_undoes_it`

**Watched by** `app:sessions`, `live:cookies` (Admin → Compliance, and `./arenactl check compliance`)

**The company's part** Decide whether "keep me signed in" fits (for health data, set Auth:RememberMe no longer than Auth:SessionMax); screens locked when people step away.

### IAM-03: Two-factor sign-in

People enrol an authenticator app and get recovery codes; an admin can reset a lost one. The company requires it of admins or of everyone, and a group of its members: who has none sets it up at their next sign-in and reaches nothing else until then, and cannot turn it off while it is required. Company sign-in brings the identity provider's own second factor: when one is required, the provider must say it was used (OIDC's amr or acr, SAML's authentication context), or the sign-in is refused.

**Serves**

- [ISO/IEC 27001:2022, clauses 4 to 10 and Annex A](frameworks/iso27001.md): A.8.5 Secure authentication
- [SOC 2, Trust Services Criteria](frameworks/soc2.md): CC6.1 Logical access security software, infrastructure and architectures
- [HIPAA, 45 CFR Part 164 (Security Rule, Breach Notification, Privacy Rule)](frameworks/hipaa.md): 164.312(d) Person or entity authentication
- [The GDPR, Regulation (EU) 2016/679](frameworks/gdpr.md): Art.32 Security of processing
- [PCI DSS v4.0.1: its twelve requirements and their sections](frameworks/pcidss.md): 8.4 Multi-factor authentication secures access into the CDE; 8.5 Multi-factor authentication systems prevent misuse
- [CSA Cloud Controls Matrix v4 (CSA STAR), by its domains](frameworks/csaccm.md): IAM Identity and access management
- [NIST Cybersecurity Framework 2.0: its categories and subcategories](frameworks/nistcsf.md): PR.AA-03 Users, services, and hardware are authenticated
- [NIST SP 800-53 Rev. 5 (5.2.0), with the low, moderate, high and privacy baselines](frameworks/nist80053.md): IA-2(1) Multi-factor Authentication to Privileged Accounts; IA-2(2) Multi-factor Authentication to Non-privileged Accounts
- [NIST SP 800-171 Rev. 3, protecting controlled unclassified information (CMMC builds on it)](frameworks/nist800171.md): 03.05.03 Multi-Factor Authentication
- [Common Criteria CC:2022 (ISO/IEC 15408): the functional (Part 2) and assurance (Part 3) families](frameworks/commoncriteria.md): FIA_UAU User authentication

**Documented in** [authentication.md#what-protects-the-sign-in](../authentication.md#what-protects-the-sign-in)

**Proved by**

- `Llm.Tests/IdentityTests.Two_factor_sign_in_with_an_authenticator_and_with_a_recovery_code`
- `Llm.Tests/TwoFactorPolicyTests.Required_of_everyone_a_person_without_one_sets_it_up_first_reaches_nothing_else_and_cannot_turn_it_off`
- `Llm.Tests/TwoFactorPolicyTests.Required_of_admins_or_of_a_groups_members_it_reaches_only_them_and_the_compliance_check_says_who_has_none`
- `Llm.Tests/CompanySignInTests.A_second_factor_required_of_someone_must_come_from_the_provider`
- `Llm.Tests/SamlSignInTests.A_second_factor_required_of_someone_must_be_the_assertions_authentication_context`

**Watched by** `app:two-factor` (Admin → Compliance, and `./arenactl check compliance`)

**The company's part** Choose who must use it (Settings → Sign-in: admins at least; everyone for health data); require a second factor at the identity provider for company sign-in.

### IAM-04: Joiners and leavers follow the company's directory

People come from the company's directory (LDAP or Active Directory), OIDC or SAML with their groups and role; SCIM creates, changes and deactivates them, and a person who leaves is disabled with their keys stopped at once.

**Serves**

- [ISO/IEC 27001:2022, clauses 4 to 10 and Annex A](frameworks/iso27001.md): A.5.16 Identity management; A.5.18 Access rights
- [SOC 2, Trust Services Criteria](frameworks/soc2.md): CC6.2 Registering and authorising new users; CC6.3 Role-based access, least privilege and segregation of duties
- [HIPAA, 45 CFR Part 164 (Security Rule, Breach Notification, Privacy Rule)](frameworks/hipaa.md): 164.308(a)(3)(ii)(C) Termination procedures (addressable); 164.308(a)(4)(ii)(C) Access establishment and modification (addressable)
- [The GDPR, Regulation (EU) 2016/679](frameworks/gdpr.md): Art.32 Security of processing
- [PCI DSS v4.0.1: its twelve requirements and their sections](frameworks/pcidss.md): 8.2 User identification and accounts are strictly managed
- [CSA Cloud Controls Matrix v4 (CSA STAR), by its domains](frameworks/csaccm.md): IAM Identity and access management; HRS Human resources
- [NIST Cybersecurity Framework 2.0: its categories and subcategories](frameworks/nistcsf.md): PR.AA-01 Identities and credentials for authorized users, services, and hardware are managed by the organization; PR.AA-05 Access permissions, entitlements, and authorizations are defined in a policy, managed, enforced, and reviewed, and incorporate the principles of least privilege and separation of duties
- [NIST SP 800-53 Rev. 5 (5.2.0), with the low, moderate, high and privacy baselines](frameworks/nist80053.md): AC-2 Account Management; AC-2(1) Automated System Account Management; PS-4 Personnel Termination; PS-5 Personnel Transfer
- [NIST SP 800-171 Rev. 3, protecting controlled unclassified information (CMMC builds on it)](frameworks/nist800171.md): 03.01.01 Account Management; 03.09.02 Personnel Termination and Transfer

**Documented in** [authentication.md#the-company-directory-ldap--active-directory](../authentication.md#the-company-directory-ldap--active-directory), [authentication.md#scim-provisioning](../authentication.md#scim-provisioning), [authentication.md#company-sign-in-by-saml-20](../authentication.md#company-sign-in-by-saml-20)

**Proved by**

- `Llm.Tests/ScimTests.Scim_needs_its_token_which_is_shown_once_stored_as_a_hash_and_replaced_at_once`
- `Llm.Tests/ScimTests.A_person_deactivated_through_scim_is_disabled_here_and_their_keys_stop_at_once`
- `Llm.Tests/SamlSignInTests.Saml_sign_in_creates_the_person_with_their_groups_their_role_and_a_key`
- `Llm.Tests/CompanySignInTests.Company_sign_in_creates_the_person_with_their_groups_their_role_and_a_key`
- `Llm.Tests/LdapTests.The_sync_disables_people_who_leave_and_restores_them_when_they_return`

**The company's part** The joiner, mover and leaver process in the directory; local accounts kept to the few who need them.

### IAM-05: Least privilege, by roles and rules

What a person may do comes from roles (built in, or made by the company) held directly, through a group or by everyone, and from rules that give or take a feature; admin roles can be scoped to some groups. The checker says why a person may or may not do something. The wiki finds and shows each person only the documents they may read.

**Serves**

- [ISO/IEC 27001:2022, clauses 4 to 10 and Annex A](frameworks/iso27001.md): A.5.15 Access control; A.5.18 Access rights; A.8.2 Privileged access rights; A.8.3 Information access restriction
- [SOC 2, Trust Services Criteria](frameworks/soc2.md): CC6.1 Logical access security software, infrastructure and architectures; CC6.3 Role-based access, least privilege and segregation of duties
- [HIPAA, 45 CFR Part 164 (Security Rule, Breach Notification, Privacy Rule)](frameworks/hipaa.md): 164.308(a)(4)(ii)(B) Access authorisation (addressable); 164.312(a)(1) Access control
- [The GDPR, Regulation (EU) 2016/679](frameworks/gdpr.md): Art.25 Data protection by design and by default; Art.32 Security of processing
- [PCI DSS v4.0.1: its twelve requirements and their sections](frameworks/pcidss.md): 7.2 Access to system components and data is defined and assigned; 7.3 Access is managed through an access control system
- [CSA Cloud Controls Matrix v4 (CSA STAR), by its domains](frameworks/csaccm.md): IAM Identity and access management
- [NIST Cybersecurity Framework 2.0: its categories and subcategories](frameworks/nistcsf.md): PR.AA-05 Access permissions, entitlements, and authorizations are defined in a policy, managed, enforced, and reviewed, and incorporate the principles of least privilege and separation of duties
- [NIST SP 800-53 Rev. 5 (5.2.0), with the low, moderate, high and privacy baselines](frameworks/nist80053.md): AC-3 Access Enforcement; AC-5 Separation of Duties; AC-6 Least Privilege
- [NIST SP 800-171 Rev. 3, protecting controlled unclassified information (CMMC builds on it)](frameworks/nist800171.md): 03.01.02 Access Enforcement; 03.01.04 Separation of Duties; 03.01.05 Least Privilege
- [Common Criteria CC:2022 (ISO/IEC 15408): the functional (Part 2) and assurance (Part 3) families](frameworks/commoncriteria.md): FDP_ACC Access control policy; FDP_ACF Access control functions; FMT_SMR Security management roles

**Documented in** [admin.md#access-roles-and-rules](../admin.md#access-roles-and-rules)

**Proved by**

- `Llm.Tests/EndpointAccessTests.A_role_reaches_the_reads_of_its_permissions_and_no_others`
- `Llm.Tests/EndpointAccessTests.Each_built_in_role_holds_exactly_its_permissions`
- `Llm.Tests/AccessEndpointsTests.A_rule_takes_a_feature_from_a_group_and_the_checker_says_why`
- `Llm.Tests/WikiTests.People_find_and_read_the_documents_they_may_read_and_nothing_else`

**The company's part** Decide who holds which role, and review it.

### IAM-06: API keys are personal, revocable and checked

Each key belongs to one person and works only as them; an unknown, blocked or expired key is refused; rotating a key revokes the old one first, and disabling a person stops their keys.

**Serves**

- [ISO/IEC 27001:2022, clauses 4 to 10 and Annex A](frameworks/iso27001.md): A.5.17 Authentication information; A.8.5 Secure authentication
- [SOC 2, Trust Services Criteria](frameworks/soc2.md): CC6.1 Logical access security software, infrastructure and architectures
- [HIPAA, 45 CFR Part 164 (Security Rule, Breach Notification, Privacy Rule)](frameworks/hipaa.md): 164.312(d) Person or entity authentication
- [The GDPR, Regulation (EU) 2016/679](frameworks/gdpr.md): Art.32 Security of processing
- [PCI DSS v4.0.1: its twelve requirements and their sections](frameworks/pcidss.md): 8.6 Application and system accounts are strictly managed
- [CSA Cloud Controls Matrix v4 (CSA STAR), by its domains](frameworks/csaccm.md): IAM Identity and access management
- [NIST Cybersecurity Framework 2.0: its categories and subcategories](frameworks/nistcsf.md): PR.AA-01 Identities and credentials for authorized users, services, and hardware are managed by the organization
- [NIST SP 800-53 Rev. 5 (5.2.0), with the low, moderate, high and privacy baselines](frameworks/nist80053.md): AC-2 Account Management; IA-5 Authenticator Management
- [NIST SP 800-171 Rev. 3, protecting controlled unclassified information (CMMC builds on it)](frameworks/nist800171.md): 03.05.12 Authenticator Management
- [Common Criteria CC:2022 (ISO/IEC 15408): the functional (Part 2) and assurance (Part 3) families](frameworks/commoncriteria.md): FIA_UAU User authentication

**Documented in** [authentication.md#as-a-machine-client-of-the-engine-api](../authentication.md#as-a-machine-client-of-the-engine-api)

**Proved by**

- `Llm.Tests/KeyCheckTests.An_unknown_blocked_or_expired_key_is_refused`
- `Llm.Tests/IdentityTests.Rotating_a_key_revokes_the_old_one_first`

**The company's part** People keep their keys out of shared files and repositories.

### IAM-07: Privileged access is reviewed

An access review lists every privileged role given to a person, a group or everyone, each member of a group that holds one, and the accounts nobody signed in to for long, for a reviewer to keep or revoke; revoking takes effect at once and is audited, and a closed review is kept for good. One starts by itself every quarter and the admins are told; accounts unused for long are disabled when the company says so.

**Serves**

- [ISO/IEC 27001:2022, clauses 4 to 10 and Annex A](frameworks/iso27001.md): A.5.18 Access rights; A.8.2 Privileged access rights
- [SOC 2, Trust Services Criteria](frameworks/soc2.md): CC6.2 Registering and authorising new users; CC6.3 Role-based access, least privilege and segregation of duties
- [HIPAA, 45 CFR Part 164 (Security Rule, Breach Notification, Privacy Rule)](frameworks/hipaa.md): 164.308(a)(3)(ii)(B) Workforce clearance procedure (addressable); 164.308(a)(4)(ii)(C) Access establishment and modification (addressable)
- [The GDPR, Regulation (EU) 2016/679](frameworks/gdpr.md): Art.32(1)(d) Security of processing
- [PCI DSS v4.0.1: its twelve requirements and their sections](frameworks/pcidss.md): 7.2 Access to system components and data is defined and assigned
- [CSA Cloud Controls Matrix v4 (CSA STAR), by its domains](frameworks/csaccm.md): IAM Identity and access management
- [NIST Cybersecurity Framework 2.0: its categories and subcategories](frameworks/nistcsf.md): PR.AA-05 Access permissions, entitlements, and authorizations are defined in a policy, managed, enforced, and reviewed, and incorporate the principles of least privilege and separation of duties
- [NIST SP 800-53 Rev. 5 (5.2.0), with the low, moderate, high and privacy baselines](frameworks/nist80053.md): AC-2 Account Management; AC-6(7) Review of User Privileges
- [NIST SP 800-171 Rev. 3, protecting controlled unclassified information (CMMC builds on it)](frameworks/nist800171.md): 03.01.01 Account Management

**Documented in** [compliance/README.md#access-reviews](README.md#access-reviews)

**Proved by**

- `Llm.Tests/AccessReviewTests.A_review_lists_privileged_roles_their_groups_members_and_unused_accounts_and_revoking_takes_effect_at_once`
- `Llm.Tests/AccessReviewTests.The_schedule_starts_a_review_when_one_is_due_tells_the_admins_and_disables_unused_accounts_when_asked`

**Watched by** `app:access-review` (Admin → Compliance, and `./arenactl check compliance`)

**The company's part** Name the reviewers (the Compliance officer role), finish each review by its due date, and review the directory's and SCIM's groups at their source as the review says.

## Records (LOG)

### LOG-01: Who did what is recorded

The audit log keeps every sign-in, every change to people, groups, roles, settings and the index, every export and every tool an agent calls as a person, with who, whom, when and from where; never content. Reads of another person's data are audited as changes are: an admin's export of them, and their shared chat opened by someone else.

**Serves**

- [ISO/IEC 27001:2022, clauses 4 to 10 and Annex A](frameworks/iso27001.md): A.8.15 Logging
- [SOC 2, Trust Services Criteria](frameworks/soc2.md): CC7.2 Monitoring system components for anomalies
- [HIPAA, 45 CFR Part 164 (Security Rule, Breach Notification, Privacy Rule)](frameworks/hipaa.md): 164.308(a)(1)(ii)(D) Information system activity review (required); 164.312(b) Audit controls
- [The GDPR, Regulation (EU) 2016/679](frameworks/gdpr.md): Art.5(2) Principles of processing personal data; Art.30 Records of processing activities
- [ISO/IEC 42001:2023, the AI management system: clauses 4 to 10 and Annex A](frameworks/iso42001.md): A.6.2.8 AI system recording of event logs
- [PCI DSS v4.0.1: its twelve requirements and their sections](frameworks/pcidss.md): 10.2 Audit logs support detection and forensic analysis
- [CSA Cloud Controls Matrix v4 (CSA STAR), by its domains](frameworks/csaccm.md): LOG Logging and monitoring
- [NIST Cybersecurity Framework 2.0: its categories and subcategories](frameworks/nistcsf.md): PR.PS-04 Log records are generated and made available for continuous monitoring; DE.CM-03 Personnel activity and technology usage are monitored to find potentially adverse events
- [NIST SP 800-53 Rev. 5 (5.2.0), with the low, moderate, high and privacy baselines](frameworks/nist80053.md): AU-2 Event Logging; AU-3 Content of Audit Records; AU-12 Audit Record Generation
- [NIST SP 800-171 Rev. 3, protecting controlled unclassified information (CMMC builds on it)](frameworks/nist800171.md): 03.03.01 Event Logging; 03.03.02 Audit Record Content
- [EU Artificial Intelligence Act, Regulation (EU) 2024/1689: its articles and annexes](frameworks/euaiact.md): Art.12 Record-keeping
- [Common Criteria CC:2022 (ISO/IEC 15408): the functional (Part 2) and assurance (Part 3) families](frameworks/commoncriteria.md): FAU_GEN Security audit data generation

**Documented in** [admin.md#admin](../admin.md#admin), [settings.md#every-change-is-audited](../settings.md#every-change-is-audited)

**Proved by**

- `Llm.Tests/IdentityTests.Everything_is_in_the_audit_log`
- `Llm.Tests/ArenaMcpTests.An_agent_signs_in_with_the_persons_key_gets_their_chat_tools_and_each_call_is_audited`
- `Llm.Tests/ArenaMcpTests.At_its_code_address_an_agent_gets_search_tools_and_run_code_whose_program_calls_the_tools_as_the_person`
- `Llm.Tests/ShareTests.A_shared_chat_opens_for_a_colleague_and_not_for_someone_outside_the_group_and_revoking_stops_it`

**Watched by** `app:audit-log` (Admin → Compliance, and `./arenactl check compliance`)

**The company's part** Someone reviews the audit log on a schedule.

### LOG-02: Logs are kept, and kept for a set time

Every service's logs go to Loki, searchable by container, level and text; how long Loki keeps them is read from its configuration and shown.

**Serves**

- [ISO/IEC 27001:2022, clauses 4 to 10 and Annex A](frameworks/iso27001.md): A.8.15 Logging
- [SOC 2, Trust Services Criteria](frameworks/soc2.md): CC7.2 Monitoring system components for anomalies
- [HIPAA, 45 CFR Part 164 (Security Rule, Breach Notification, Privacy Rule)](frameworks/hipaa.md): 164.312(b) Audit controls
- [The GDPR, Regulation (EU) 2016/679](frameworks/gdpr.md): Art.5(1)(e) Principles of processing personal data
- [ISO/IEC 42001:2023, the AI management system: clauses 4 to 10 and Annex A](frameworks/iso42001.md): A.6.2.8 AI system recording of event logs
- [PCI DSS v4.0.1: its twelve requirements and their sections](frameworks/pcidss.md): 10.5 Audit log history is retained and available
- [CSA Cloud Controls Matrix v4 (CSA STAR), by its domains](frameworks/csaccm.md): LOG Logging and monitoring
- [NIST Cybersecurity Framework 2.0: its categories and subcategories](frameworks/nistcsf.md): PR.PS-04 Log records are generated and made available for continuous monitoring
- [NIST SP 800-53 Rev. 5 (5.2.0), with the low, moderate, high and privacy baselines](frameworks/nist80053.md): AU-11 Audit Record Retention
- [Common Criteria CC:2022 (ISO/IEC 15408): the functional (Part 2) and assurance (Part 3) families](frameworks/commoncriteria.md): FAU_STG Security audit data storage

**Documented in** [admin.md#storage](../admin.md#storage)

**Proved by**

- `Llm.Tests/StorageTests.Lokis_retention_is_read_from_its_configuration`
- `Llm.Tests/RecordRetentionTests.Loki_is_set_to_keep_logs_for_ninety_days_in_compose_and_in_the_chart`

**Watched by** `app:log-retention` (Admin → Compliance, and `./arenactl check compliance`)

**The company's part** Decide how long logs are kept.

### LOG-03: Nobody changes the audit log unseen

Each entry is sealed onto the one before it with SHA-256, under one lock for every writer, so an entry changed, removed or slipped in breaks the chain from there; every day the whole chain is walked and its newest entry anchored in a volume of its own and the log, which shows an end rewritten. A break tells every admin, is in the audit log, and turns the check red; the evidence carries the chain's walk and its anchors.

**Serves**

- [ISO/IEC 27001:2022, clauses 4 to 10 and Annex A](frameworks/iso27001.md): A.5.28 Collection of evidence; A.5.33 Protection of records; A.8.15 Logging
- [SOC 2, Trust Services Criteria](frameworks/soc2.md): CC7.2 Monitoring system components for anomalies; CC7.3 Evaluating security events
- [HIPAA, 45 CFR Part 164 (Security Rule, Breach Notification, Privacy Rule)](frameworks/hipaa.md): 164.312(b) Audit controls; 164.312(c)(1) Integrity; 164.312(c)(2) Mechanism to authenticate electronic PHI (addressable)
- [The GDPR, Regulation (EU) 2016/679](frameworks/gdpr.md): Art.5(1)(f) Principles of processing personal data; Art.5(2) Principles of processing personal data; Art.32(1)(b) Security of processing
- [PCI DSS v4.0.1: its twelve requirements and their sections](frameworks/pcidss.md): 10.3 Audit logs are protected from destruction and modification
- [CSA Cloud Controls Matrix v4 (CSA STAR), by its domains](frameworks/csaccm.md): LOG Logging and monitoring
- [NIST Cybersecurity Framework 2.0: its categories and subcategories](frameworks/nistcsf.md): PR.PS-04 Log records are generated and made available for continuous monitoring
- [NIST SP 800-53 Rev. 5 (5.2.0), with the low, moderate, high and privacy baselines](frameworks/nist80053.md): AU-9 Protection of Audit Information; AU-9(3) Cryptographic Protection
- [NIST SP 800-171 Rev. 3, protecting controlled unclassified information (CMMC builds on it)](frameworks/nist800171.md): 03.03.08 Protection of Audit Information
- [Common Criteria CC:2022 (ISO/IEC 15408): the functional (Part 2) and assurance (Part 3) families](frameworks/commoncriteria.md): FAU_STG Security audit data storage

**Documented in** [compliance/README.md#the-audit-log-as-a-chain](README.md#the-audit-log-as-a-chain)

**Proved by**

- `Llm.Tests/AuditChainTests.An_entry_changed_removed_or_slipped_in_breaks_the_chain_there_and_every_admin_is_told`
- `Llm.Tests/AuditChainTests.An_anchor_kept_apart_shows_the_end_of_the_chain_rewritten_and_entries_from_before_the_chain_are_sealed`
- `Llm.Tests/AuditChainTests.Many_writers_at_once_still_make_one_chain`

**Watched by** `app:audit-chain` (Admin → Compliance, and `./arenactl check compliance`)

**The company's part** Keep the audit volume's folder (DATA_DIR/audit) where the database's administrators cannot write, and back it up apart; restrict direct database access and keep its own logs.

### LOG-04: The audit log is sent on to the company's SIEM

Every entry, as it is written, in order and each once, by syslog (RFC 5424 over UDP, TCP or TLS) and to a webhook signed with HMAC-SHA256, with its hash so the SIEM can walk the chain too. How far it got is kept in the database, so a restart or another replica goes on from there; a collector that does not answer is tried again and nothing is skipped.

**Serves**

- [ISO/IEC 27001:2022, clauses 4 to 10 and Annex A](frameworks/iso27001.md): A.8.15 Logging; A.8.16 Monitoring activities
- [SOC 2, Trust Services Criteria](frameworks/soc2.md): CC7.2 Monitoring system components for anomalies
- [HIPAA, 45 CFR Part 164 (Security Rule, Breach Notification, Privacy Rule)](frameworks/hipaa.md): 164.308(a)(1)(ii)(D) Information system activity review (required); 164.312(b) Audit controls
- [The GDPR, Regulation (EU) 2016/679](frameworks/gdpr.md): Art.32(1)(b) Security of processing
- [PCI DSS v4.0.1: its twelve requirements and their sections](frameworks/pcidss.md): 10.4 Audit logs are reviewed for anomalies
- [CSA Cloud Controls Matrix v4 (CSA STAR), by its domains](frameworks/csaccm.md): LOG Logging and monitoring
- [NIST Cybersecurity Framework 2.0: its categories and subcategories](frameworks/nistcsf.md): DE.AE-03 Information is correlated from multiple sources
- [NIST SP 800-53 Rev. 5 (5.2.0), with the low, moderate, high and privacy baselines](frameworks/nist80053.md): AU-6 Audit Record Review, Analysis, and Reporting; AU-9(2) Store on Separate Physical Systems or Components
- [NIST SP 800-171 Rev. 3, protecting controlled unclassified information (CMMC builds on it)](frameworks/nist800171.md): 03.03.05 Audit Record Review, Analysis, and Reporting
- [Common Criteria CC:2022 (ISO/IEC 15408): the functional (Part 2) and assurance (Part 3) families](frameworks/commoncriteria.md): FAU_SAR Security audit review

**Documented in** [compliance/README.md#sending-it-to-the-siem](README.md#sending-it-to-the-siem)

**Proved by**

- `Llm.Tests/AuditForwardTests.Each_entry_is_posted_once_in_order_signed_and_a_webhook_that_fails_gets_them_all_later`
- `Llm.Tests/AuditForwardTests.Syslog_gets_each_entry_octet_counted_as_RFC_5424_with_failures_as_warnings`

**Watched by** `app:audit-forwarding` (Admin → Compliance, and `./arenactl check compliance`)

**The company's part** Run the SIEM, keep what it receives for as long as the frameworks ask (six years under HIPAA), and watch its alerts.

## Monitoring (MON)

### MON-01: The service is watched and people are alerted

Prometheus and Alertmanager watch the stack; an alert that starts firing reaches every admin's bell, email and the alerts webhook once; the certificate warns 30 days before it expires and is critical 7 days before.

**Serves**

- [ISO/IEC 27001:2022, clauses 4 to 10 and Annex A](frameworks/iso27001.md): A.8.16 Monitoring activities
- [SOC 2, Trust Services Criteria](frameworks/soc2.md): CC7.2 Monitoring system components for anomalies; CC7.3 Evaluating security events
- [HIPAA, 45 CFR Part 164 (Security Rule, Breach Notification, Privacy Rule)](frameworks/hipaa.md): 164.308(a)(1)(ii)(D) Information system activity review (required); 164.308(a)(6)(ii) Response and reporting (required)
- [The GDPR, Regulation (EU) 2016/679](frameworks/gdpr.md): Art.32(1)(b) Security of processing
- [PCI DSS v4.0.1: its twelve requirements and their sections](frameworks/pcidss.md): 10.7 Failures of critical security controls are detected and answered
- [CSA Cloud Controls Matrix v4 (CSA STAR), by its domains](frameworks/csaccm.md): LOG Logging and monitoring
- [NIST Cybersecurity Framework 2.0: its categories and subcategories](frameworks/nistcsf.md): DE.CM-01 Networks and network services are monitored to find potentially adverse events; DE.CM-09 Computing hardware and software, runtime environments, and their data are monitored to find potentially adverse events
- [NIST SP 800-53 Rev. 5 (5.2.0), with the low, moderate, high and privacy baselines](frameworks/nist80053.md): CA-7 Continuous Monitoring; SI-4 System Monitoring
- [NIST SP 800-171 Rev. 3, protecting controlled unclassified information (CMMC builds on it)](frameworks/nist800171.md): 03.14.06 System Monitoring

**Documented in** [admin.md#admin](../admin.md#admin)

**Proved by**

- `Llm.Tests/ObserveTests.Alerts_show_what_fires_and_every_rule_and_survive_either_source_down`
- `Llm.Tests/NotificationsTests.A_system_alert_that_starts_firing_reaches_every_admin_once`
- `Llm.Tests/OperationsTests.The_certificate_alerts_warn_thirty_days_before_and_are_critical_seven_days_before`

**Watched by** `app:certificate` (Admin → Compliance, and `./arenactl check compliance`)

**The company's part** Someone takes the alerts, around the clock where the company needs it.

### MON-02: Security events raise alerts, and incidents are handled in time

Many failed sign-ins, an administrative role given, many exports by one person, a failed or late backup raise a security alert once, to every admin; the incident register takes an incident from an alert or by hand, runs the GDPR's 72 hours and HIPAA's 60 days from when it was found, keeps every step with who and when, and is closed only with what was learned (and, with personal data, the authority question answered).

**Serves**

- [ISO/IEC 27001:2022, clauses 4 to 10 and Annex A](frameworks/iso27001.md): A.5.24 Information security incident management planning and preparation; A.5.25 Assessment and decision on information security events; A.5.26 Response to information security incidents; A.5.27 Learning from information security incidents; A.8.16 Monitoring activities
- [SOC 2, Trust Services Criteria](frameworks/soc2.md): CC7.3 Evaluating security events; CC7.4 Responding to security incidents; CC7.5 Recovering from security incidents
- [HIPAA, 45 CFR Part 164 (Security Rule, Breach Notification, Privacy Rule)](frameworks/hipaa.md): 164.308(a)(6)(ii) Response and reporting (required); 164.404 Notification to individuals of a breach; 164.408 Notification to the Secretary of a breach
- [The GDPR, Regulation (EU) 2016/679](frameworks/gdpr.md): Art.33 Notifying the supervisory authority of a breach; Art.34 Telling the people affected of a breach
- [ISO/IEC 42001:2023, the AI management system: clauses 4 to 10 and Annex A](frameworks/iso42001.md): A.8.4 Communication of incidents
- [ISO 22301:2019, business continuity management systems](frameworks/iso22301.md): 8.4.3 Warning and communication
- [PCI DSS v4.0.1: its twelve requirements and their sections](frameworks/pcidss.md): 10.4 Audit logs are reviewed for anomalies; 12.10 Security incidents are responded to immediately
- [CSA Cloud Controls Matrix v4 (CSA STAR), by its domains](frameworks/csaccm.md): SEF Security incident management, e-discovery and cloud forensics
- [NIST Cybersecurity Framework 2.0: its categories and subcategories](frameworks/nistcsf.md): DE.AE-02 Potentially adverse events are analyzed to better understand associated activities; RS.MA-01 The incident response plan is executed in coordination with relevant third parties once an incident is declared; RS.CO-02 Internal and external stakeholders are notified of incidents
- [NIST SP 800-53 Rev. 5 (5.2.0), with the low, moderate, high and privacy baselines](frameworks/nist80053.md): IR-4 Incident Handling; IR-5 Incident Monitoring; IR-6 Incident Reporting; IR-8 Incident Response Plan; SI-4 System Monitoring
- [NIST SP 800-171 Rev. 3, protecting controlled unclassified information (CMMC builds on it)](frameworks/nist800171.md): 03.06.01 Incident Handling; 03.06.02 Incident Monitoring, Reporting, and Response Assistance
- [NIST AI Risk Management Framework 1.0 (AI 100-1): Govern, Map, Measure and Manage, as its Playbook lists them](frameworks/airmf.md): MANAGE 4.3 Incidents and errors are communicated to relevant AI actors including affected communities. Processes for tracking, responding to, and recovering from incidents and errors are followed and documented.
- [EU Artificial Intelligence Act, Regulation (EU) 2024/1689: its articles and annexes](frameworks/euaiact.md): Art.73 Reporting of serious incidents

**Documented in** [compliance/README.md#security-alerts-and-incidents](README.md#security-alerts-and-incidents)

**Proved by**

- `Llm.Tests/IncidentTests.Failed_sign_ins_an_admin_made_many_exports_and_a_failed_backup_each_raise_one_alert_to_every_admin`
- `Llm.Tests/IncidentTests.An_incident_runs_from_its_alert_to_closure_with_the_GDPRs_72_hours_and_HIPAAs_60_days`

**Watched by** `app:incidents` (Admin → Compliance, and `./arenactl check compliance`)

**The company's part** Name who answers the alerts and runs incidents, know which supervisory authority to tell and how, and keep the incident-response runbook.

## Data (DAT)

### DAT-01: Chats and files are deleted when their time is up

The company sets how long chats are kept, a group can set a shorter time, and every hour a job deletes chats past it with their files, and files nobody uses that are as old; each deletion is audited with counts.

**Serves**

- [ISO/IEC 27001:2022, clauses 4 to 10 and Annex A](frameworks/iso27001.md): A.5.33 Protection of records; A.8.10 Information deletion
- [SOC 2, Trust Services Criteria](frameworks/soc2.md): C1.2 Disposing of confidential information; P4.2 Retention of personal information; P4.3 Disposal of personal information
- [The GDPR, Regulation (EU) 2016/679](frameworks/gdpr.md): Art.5(1)(e) Principles of processing personal data; Art.17 Right to erasure
- [PCI DSS v4.0.1: its twelve requirements and their sections](frameworks/pcidss.md): 3.2 Storage of account data is kept to a minimum
- [CSA Cloud Controls Matrix v4 (CSA STAR), by its domains](frameworks/csaccm.md): DSP Data security and privacy lifecycle management
- [NIST SP 800-53 Rev. 5 (5.2.0), with the low, moderate, high and privacy baselines](frameworks/nist80053.md): SI-12 Information Management and Retention

**Documented in** [admin.md#retention-legal-hold-and-exports](../admin.md#retention-legal-hold-and-exports)

**Proved by**

- `Llm.Tests/RetentionTests.A_chat_past_its_groups_retention_is_deleted_with_its_files_and_one_under_legal_hold_is_not`

**Watched by** `app:chat-retention` (Admin → Compliance, and `./arenactl check compliance`)

**The company's part** Decide the periods, and write them in the retention policy.

### DAT-02: Legal hold

A person on legal hold keeps everything: retention passes them by, and a chat they delete is hidden from them, not erased, until the hold ends.

**Serves**

- [ISO/IEC 27001:2022, clauses 4 to 10 and Annex A](frameworks/iso27001.md): A.5.28 Collection of evidence; A.5.33 Protection of records
- [The GDPR, Regulation (EU) 2016/679](frameworks/gdpr.md): Art.17(3)(e) Right to erasure
- [CSA Cloud Controls Matrix v4 (CSA STAR), by its domains](frameworks/csaccm.md): DSP Data security and privacy lifecycle management
- [NIST SP 800-53 Rev. 5 (5.2.0), with the low, moderate, high and privacy baselines](frameworks/nist80053.md): SI-12 Information Management and Retention

**Documented in** [admin.md#retention-legal-hold-and-exports](../admin.md#retention-legal-hold-and-exports)

**Proved by**

- `Llm.Tests/RetentionTests.A_person_on_legal_hold_who_deletes_a_chat_only_hides_it_until_the_hold_ends`

**The company's part** Legal decides holds and when they end.

### DAT-03: A person's data, exported

People download everything of theirs from Your account; an admin exports a person's data for eDiscovery, audited: their profile, chats as JSON and Markdown, and files.

**Serves**

- [ISO/IEC 27001:2022, clauses 4 to 10 and Annex A](frameworks/iso27001.md): A.5.34 Privacy and protection of PII
- [SOC 2, Trust Services Criteria](frameworks/soc2.md): P5.1 People's access to their personal information
- [HIPAA, 45 CFR Part 164 (Security Rule, Breach Notification, Privacy Rule)](frameworks/hipaa.md): 164.524 Access of individuals to their health information
- [The GDPR, Regulation (EU) 2016/679](frameworks/gdpr.md): Art.15 Right of access; Art.20 Right to data portability
- [CSA Cloud Controls Matrix v4 (CSA STAR), by its domains](frameworks/csaccm.md): DSP Data security and privacy lifecycle management; IPY Interoperability and portability

**Documented in** [admin.md#retention-legal-hold-and-exports](../admin.md#retention-legal-hold-and-exports)

**Proved by**

- `Llm.Tests/RetentionTests.A_persons_export_holds_their_profile_chats_as_json_and_markdown_and_their_files`

**The company's part** Answer a person's request within the time the law gives (a month under the GDPR).

### DAT-04: Personal data and secrets kept from the model

Safeguards mask personal data before a question reaches the model while the chat keeps it as written; answers are masked or withheld by rule; what tools send and bring back is held by their rules; a pasted secret or private key is refused and audited by its kind only; words and prompt injection are read through their disguises and in eleven languages, commands that do damage are kept from agents' tools, and data sent out through images and links is stopped.

**Serves**

- [ISO/IEC 27001:2022, clauses 4 to 10 and Annex A](frameworks/iso27001.md): A.8.11 Data masking; A.8.12 Data leakage prevention
- [SOC 2, Trust Services Criteria](frameworks/soc2.md): C1.1 Identifying and keeping confidential information; CC6.7 Restricting the transmission, movement and removal of information
- [HIPAA, 45 CFR Part 164 (Security Rule, Breach Notification, Privacy Rule)](frameworks/hipaa.md): 164.502(b) Minimum necessary
- [The GDPR, Regulation (EU) 2016/679](frameworks/gdpr.md): Art.25 Data protection by design and by default; Art.32(1)(a) Security of processing
- [PCI DSS v4.0.1: its twelve requirements and their sections](frameworks/pcidss.md): 3.2 Storage of account data is kept to a minimum
- [CSA Cloud Controls Matrix v4 (CSA STAR), by its domains](frameworks/csaccm.md): DSP Data security and privacy lifecycle management
- [NIST Cybersecurity Framework 2.0: its categories and subcategories](frameworks/nistcsf.md): PR.DS-01 The confidentiality, integrity, and availability of data-at-rest are protected; PR.DS-02 The confidentiality, integrity, and availability of data-in-transit are protected
- [NIST SP 800-53 Rev. 5 (5.2.0), with the low, moderate, high and privacy baselines](frameworks/nist80053.md): AC-4 Information Flow Enforcement; SI-15 Information Output Filtering
- [NIST SP 800-171 Rev. 3, protecting controlled unclassified information (CMMC builds on it)](frameworks/nist800171.md): 03.01.03 Information Flow Enforcement
- [Common Criteria CC:2022 (ISO/IEC 15408): the functional (Part 2) and assurance (Part 3) families](frameworks/commoncriteria.md): FDP_IFC Information flow control policy; FDP_IFF Information flow control functions

**Documented in** [admin.md#safeguards](../admin.md#safeguards), [chat.md#safeguards](../chat.md#safeguards)

**Proved by**

- `Llm.Tests/SafeguardsTests.Personal_data_reaches_the_model_masked_and_the_chat_keeps_it_as_written`
- `Llm.Tests/SafeguardRulesTests.Answers_are_masked_or_withheld_and_what_tools_send_and_bring_back_is_held_by_their_rules`
- `Llm.Tests/SafeguardDetectorTests.A_word_is_found_through_its_disguises_and_masks_what_was_written`
- `Llm.Tests/SafeguardDetectorTests.Injection_and_jailbreaks_are_found_in_any_language_and_disguise`
- `Llm.Tests/SafeguardDetectorTests.Commands_that_do_damage_are_found`
- `Llm.Tests/SafeguardDetectorTests.Data_sent_out_through_an_image_or_a_link_is_found_but_not_to_the_companys_own_site`
- `Llm.Tests/SecretScanningTests.A_pasted_private_key_is_refused_in_the_chat_and_through_the_API_and_audited_by_its_kind_only`

**The company's part** Decide which data may reach which model, and which models are outside the company.

### DAT-05: Secrets and people's files are encrypted at rest

The signing keys, settings' secrets (directory, SSO, connectors) and tokens are stored encrypted with APP_KEY; a changed key fails loudly instead of resetting keys; secrets are never sent back to a browser. People's files (their bytes, a video's sound, a document's drawn pages) and deck templates are sealed with AES-256-GCM under a key from APP_KEY before they reach the database, and a job seals those stored before. The disk under the stack's data is checked for encryption from the host.

**Serves**

- [ISO/IEC 27001:2022, clauses 4 to 10 and Annex A](frameworks/iso27001.md): A.8.24 Use of cryptography
- [SOC 2, Trust Services Criteria](frameworks/soc2.md): CC6.1 Logical access security software, infrastructure and architectures
- [HIPAA, 45 CFR Part 164 (Security Rule, Breach Notification, Privacy Rule)](frameworks/hipaa.md): 164.312(a)(2)(iv) Encryption and decryption (addressable)
- [The GDPR, Regulation (EU) 2016/679](frameworks/gdpr.md): Art.32(1)(a) Security of processing
- [PCI DSS v4.0.1: its twelve requirements and their sections](frameworks/pcidss.md): 3.5 Card numbers are secured wherever they are stored; 3.6 Keys protecting stored account data are secured
- [CSA Cloud Controls Matrix v4 (CSA STAR), by its domains](frameworks/csaccm.md): CEK Cryptography, encryption and key management
- [NIST Cybersecurity Framework 2.0: its categories and subcategories](frameworks/nistcsf.md): PR.DS-01 The confidentiality, integrity, and availability of data-at-rest are protected
- [NIST SP 800-53 Rev. 5 (5.2.0), with the low, moderate, high and privacy baselines](frameworks/nist80053.md): SC-12 Cryptographic Key Establishment and Management; SC-28 Protection of Information at Rest; SC-28(1) Cryptographic Protection
- [NIST SP 800-171 Rev. 3, protecting controlled unclassified information (CMMC builds on it)](frameworks/nist800171.md): 03.13.08 Transmission and Storage Confidentiality; 03.13.10 Cryptographic Key Establishment and Management
- [Common Criteria CC:2022 (ISO/IEC 15408): the functional (Part 2) and assurance (Part 3) families](frameworks/commoncriteria.md): FCS_CKM Cryptographic key management; FCS_COP Cryptographic operation; FDP_SDC Stored data confidentiality

**Documented in** [authentication.md#what-protects-the-sign-in](../authentication.md#what-protects-the-sign-in)

**Proved by**

- `Llm.Tests/KeyRingTests.With_a_data_key_the_key_ring_is_encrypted_and_survives_a_restart`
- `Llm.Tests/KeyRingTests.A_changed_data_key_fails_loudly_instead_of_silently_resetting_keys`
- `Llm.Tests/SettingsTests.A_secret_is_stored_encrypted_used_in_plain_and_never_sent_back`
- `Llm.Tests/KnowledgeConnectorTests.Test_connection_says_what_it_reached_and_secrets_are_stored_encrypted_and_never_sent_back`
- `Llm.Tests/FileSealingTests.A_file_is_stored_encrypted_read_as_it_was_and_those_stored_before_are_sealed_by_the_job`
- `Llm.Tests/FileSealingTests.Without_APP_KEY_files_are_stored_as_they_are_and_the_check_says_so`

**Watched by** `app:data-key`, `app:files-sealed`, `live:disk` (Admin → Compliance, and `./arenactl check compliance`)

**The company's part** Keep APP_KEY with the company's secrets, apart from the backups; encrypt the hosts' disks.

### DAT-06: People's requests about their data are answered

A person asks from Your account (or an admin takes the request in for them) for a copy, their data to take elsewhere, a correction, restriction, objection or erasure; each has its month, the admins hear of it, and every step is audited. Restriction and objection stop everything that learns from their data; erasure removes everything of theirs, takes them out of what others keep, refuses while a legal hold lasts, and gives a certificate of what is kept and why and when backups let go of the rest.

**Serves**

- [ISO/IEC 27001:2022, clauses 4 to 10 and Annex A](frameworks/iso27001.md): A.5.34 Privacy and protection of PII
- [SOC 2, Trust Services Criteria](frameworks/soc2.md): P5.1 People's access to their personal information; P5.2 Correction of personal information; P4.3 Disposal of personal information
- [HIPAA, 45 CFR Part 164 (Security Rule, Breach Notification, Privacy Rule)](frameworks/hipaa.md): 164.524 Access of individuals to their health information; 164.526 Amendment of health information
- [The GDPR, Regulation (EU) 2016/679](frameworks/gdpr.md): Art.12(3) Transparent information and how the rights are exercised; Art.15 Right of access; Art.16 Right to rectification; Art.17 Right to erasure; Art.18 Right to restriction of processing; Art.20 Right to data portability; Art.21 Right to object
- [CSA Cloud Controls Matrix v4 (CSA STAR), by its domains](frameworks/csaccm.md): DSP Data security and privacy lifecycle management

**Documented in** [compliance/README.md#peoples-data-requests](README.md#peoples-data-requests)

**Proved by**

- `Llm.Tests/DataRequestTests.A_person_asks_the_admins_hear_it_is_due_in_a_month_and_answering_tells_them`
- `Llm.Tests/DataRequestTests.Restriction_stops_everything_that_learns_from_their_data_until_it_is_lifted`
- `Llm.Tests/DataRequestTests.Erasure_removes_everything_of_theirs_takes_them_out_of_what_others_keep_and_certifies_what_is_kept`

**Watched by** `app:data-requests` (Admin → Compliance, and `./arenactl check compliance`)

**The company's part** Name who answers them (a data protection officer, with the privacy.manage permission), verify who is asking, and tell people where to complain.

### DAT-07: Records are kept for set times, and no longer

The audit log six years unless set (only ever from the start of its chain), the gateway's request log, the record of tool calls, the bell and the prompt log of API requests each for a period set under Settings → Data retention, removed by the hourly sweep and audited with counts; the services' logs 90 days (Loki's compactor) and metrics 30 days (Prometheus). Admin → Compliance lists how long each kind of data and record is kept, and what removes it.

**Serves**

- [ISO/IEC 27001:2022, clauses 4 to 10 and Annex A](frameworks/iso27001.md): A.5.33 Protection of records; A.8.10 Information deletion; A.8.15 Logging
- [SOC 2, Trust Services Criteria](frameworks/soc2.md): C1.2 Disposing of confidential information; P4.2 Retention of personal information; P4.3 Disposal of personal information
- [HIPAA, 45 CFR Part 164 (Security Rule, Breach Notification, Privacy Rule)](frameworks/hipaa.md): 164.316(b)(2)(i) Time limit of six years (required)
- [The GDPR, Regulation (EU) 2016/679](frameworks/gdpr.md): Art.5(1)(e) Principles of processing personal data
- [PCI DSS v4.0.1: its twelve requirements and their sections](frameworks/pcidss.md): 3.2 Storage of account data is kept to a minimum; 10.5 Audit log history is retained and available
- [CSA Cloud Controls Matrix v4 (CSA STAR), by its domains](frameworks/csaccm.md): DSP Data security and privacy lifecycle management
- [NIST SP 800-53 Rev. 5 (5.2.0), with the low, moderate, high and privacy baselines](frameworks/nist80053.md): AU-11 Audit Record Retention; SI-12 Information Management and Retention

**Documented in** [compliance/README.md#how-long-things-are-kept](README.md#how-long-things-are-kept)

**Proved by**

- `Llm.Tests/RecordRetentionTests.Records_past_their_time_go_the_audit_log_only_from_the_start_of_its_chain_and_the_check_says_how_long_each_is_kept`

**Watched by** `app:records-retention`, `app:log-retention` (Admin → Compliance, and `./arenactl check compliance`)

**The company's part** Decide the periods, write them in the retention policy, and keep backups as long as it says.

### DAT-08: Personal and health data kept in bounds

The safeguards find e-mail addresses, phone, card, IBAN and social security numbers, and health and identity data: dates of birth and medical record numbers by the words that name them, NHS numbers and Iranian national codes with their check digits right, and US Medicare numbers by their shape; a rule warns, masks or blocks them before a question reaches a model, stricter for models on other providers (the template "Health and identity data to cloud models" blocks them there). The app's log lines carry no personal data: it is masked before a line is written (IP addresses stay, as security needs them); answers' traces hold times, tokens and tool names only, and the gateway's request log no prompts. The one record of what people ask is the prompt log: only those with prompts.review (no built-in role but the admins') read it, each search is audited, people are told on their account page, it is kept for a set time, and it can be turned off for API requests.

**Serves**

- [ISO/IEC 27001:2022, clauses 4 to 10 and Annex A](frameworks/iso27001.md): A.5.34 Privacy and protection of PII; A.8.11 Data masking; A.8.12 Data leakage prevention
- [SOC 2, Trust Services Criteria](frameworks/soc2.md): C1.1 Identifying and keeping confidential information; CC6.7 Restricting the transmission, movement and removal of information; P4.1 Use limited to the purposes identified
- [HIPAA, 45 CFR Part 164 (Security Rule, Breach Notification, Privacy Rule)](frameworks/hipaa.md): 164.502(b) Minimum necessary; 164.514(b) De-identification
- [The GDPR, Regulation (EU) 2016/679](frameworks/gdpr.md): Art.5(1)(c) Principles of processing personal data; Art.9 Special categories of personal data; Art.25 Data protection by design and by default; Art.32(1)(a) Security of processing
- [PCI DSS v4.0.1: its twelve requirements and their sections](frameworks/pcidss.md): 3.2 Storage of account data is kept to a minimum; 3.3 Sensitive authentication data is not stored after authorization
- [CSA Cloud Controls Matrix v4 (CSA STAR), by its domains](frameworks/csaccm.md): DSP Data security and privacy lifecycle management
- [NIST Cybersecurity Framework 2.0: its categories and subcategories](frameworks/nistcsf.md): PR.DS-01 The confidentiality, integrity, and availability of data-at-rest are protected
- [NIST SP 800-53 Rev. 5 (5.2.0), with the low, moderate, high and privacy baselines](frameworks/nist80053.md): PT-2 Authority to Process Personally Identifiable Information; PT-3 Personally Identifiable Information Processing Purposes
- [NIST AI Risk Management Framework 1.0 (AI 100-1): Govern, Map, Measure and Manage, as its Playbook lists them](frameworks/airmf.md): MEASURE 2.10 Privacy risk of the AI system – as identified in the MAP function – is examined and documented.

**Documented in** [compliance/README.md#personal-and-health-data](README.md#personal-and-health-data)

**Proved by**

- `Llm.Tests/PersonalDataTests.Health_and_identity_data_are_found_by_what_names_them_and_their_check_digits`
- `Llm.Tests/PersonalDataTests.A_wrong_check_digit_or_a_number_nothing_names_is_not_taken_for_one`
- `Llm.Tests/PersonalDataTests.The_apps_log_lines_carry_no_personal_data_but_keep_the_addresses_security_needs`
- `Llm.Tests/PersonalDataTests.The_compliance_check_wants_a_rule_keeping_health_data_from_cloud_models_and_the_templates_give_one`
- `Llm.Tests/PromptLogTests.The_chats_and_the_API_requests_questions_and_answers_are_read_with_prompts_review_and_each_search_is_audited`
- `Llm.Tests/PromptLogTests.An_answer_from_the_answer_cache_is_in_the_prompt_log_as_well`
- `Llm.Tests/PromptLogTests.With_the_prompt_log_off_API_requests_are_not_kept`
- `Llm.Tests/PromptLogTests.Every_API_request_is_in_the_log_from_its_arrival_while_it_runs_and_when_it_fails_is_refused_or_is_stopped`
- `Llm.Tests/PromptLogTests.A_chat_question_is_in_the_log_with_its_answers_state_and_why_it_failed`

**Watched by** `app:personal-data` (Admin → Compliance, and `./arenactl check compliance`)

**The company's part** Decide which models may see which data (a HIPAA BAA, a GDPR processing agreement with any provider outside the company), add the templates, and add the company's own identifiers as words or patterns.

## Network (NET)

### NET-01: Encrypted in transit, one way in

Only Traefik's ports 80 and 443 are published, 80 only to redirect; TLS 1.2 and 1.3 only, and for 1.2 only ciphers with forward secrecy and authenticated encryption; HSTS for a year with a trusted certificate (HSTS_SECONDS); the services talk on Docker's network; every page carries a strict content security policy and the security headers; a state change needs a header another site cannot send; hosts without a rule are refused; the API host goes by the app alone, which passes on only the model API's own paths, so the model gateway's management and pages are not reachable from outside.

**Serves**

- [ISO/IEC 27001:2022, clauses 4 to 10 and Annex A](frameworks/iso27001.md): A.8.20 Networks security; A.8.24 Use of cryptography; A.8.26 Application security requirements
- [SOC 2, Trust Services Criteria](frameworks/soc2.md): CC6.6 Security against threats from outside the system's boundaries; CC6.7 Restricting the transmission, movement and removal of information
- [HIPAA, 45 CFR Part 164 (Security Rule, Breach Notification, Privacy Rule)](frameworks/hipaa.md): 164.312(e)(1) Transmission security; 164.312(e)(2)(ii) Encryption (addressable)
- [The GDPR, Regulation (EU) 2016/679](frameworks/gdpr.md): Art.32(1)(a) Security of processing
- [PCI DSS v4.0.1: its twelve requirements and their sections](frameworks/pcidss.md): 1.3 Network access to and from the cardholder data environment is restricted; 1.4 Connections between trusted and untrusted networks are controlled; 4.2 Card numbers are protected with strong cryptography in transmission
- [CSA Cloud Controls Matrix v4 (CSA STAR), by its domains](frameworks/csaccm.md): IVS Infrastructure and virtualization security; CEK Cryptography, encryption and key management
- [NIST Cybersecurity Framework 2.0: its categories and subcategories](frameworks/nistcsf.md): PR.DS-02 The confidentiality, integrity, and availability of data-in-transit are protected; PR.IR-01 Networks and environments are protected from unauthorized logical access and usage
- [NIST SP 800-53 Rev. 5 (5.2.0), with the low, moderate, high and privacy baselines](frameworks/nist80053.md): SC-7 Boundary Protection; SC-8 Transmission Confidentiality and Integrity; SC-8(1) Cryptographic Protection; SC-13 Cryptographic Protection
- [NIST SP 800-171 Rev. 3, protecting controlled unclassified information (CMMC builds on it)](frameworks/nist800171.md): 03.13.01 Boundary Protection; 03.13.08 Transmission and Storage Confidentiality
- [Common Criteria CC:2022 (ISO/IEC 15408): the functional (Part 2) and assurance (Part 3) families](frameworks/commoncriteria.md): FTP_ITC Inter-TSF trusted channel; FTP_TRP Trusted path; FCS_COP Cryptographic operation

**Documented in** [authentication.md#what-is-deliberately-not-behind-sign-in](../authentication.md#what-is-deliberately-not-behind-sign-in), [deployment.md#certificates](../deployment.md#certificates)

**Proved by**

- `Llm.Tests/FoundationTests.Security_headers_are_set`
- `Llm.Tests/IdentityTests.State_changes_without_the_csrf_header_are_refused`
- `Llm.Tests/ForwardAuthTests.Hosts_without_a_rule_are_denied_even_to_admins`
- `Llm.Tests/FileSealingTests.With_HSTS_set_the_app_sends_it_as_Traefik_does_and_without_it_clears_any_kept_before`
- `Llm.Tests/ApiPassageTests.The_model_list_and_embeddings_are_passed_on_with_the_callers_own_key`
- `Llm.Tests/ApiPassageTests.The_gateways_own_management_is_not_found_from_outside`

**Watched by** `live:tls`, `live:headers`, `live:csrf` (Admin → Compliance, and `./arenactl check compliance`)

**The company's part** A certificate from the company's CA or Let's Encrypt; the network around the hosts.

## Operations (OPS)

### OPS-01: Backups, restored and proven

scripts/backup.sh backs up the databases, the settings and the files, with a second copy elsewhere, each file encrypted (AES-256, PBKDF2) with a key kept apart from the backups (BACKUP_KEY_FILE), refused inside them; point-in-time recovery archives the WAL; the drill restores into a throwaway stack, never the live one, and checks that every person, chat and setting came back.

**Serves**

- [ISO/IEC 27001:2022, clauses 4 to 10 and Annex A](frameworks/iso27001.md): A.5.30 ICT readiness for business continuity; A.8.13 Information backup
- [SOC 2, Trust Services Criteria](frameworks/soc2.md): A1.2 Environmental protections, software, backups and recovery infrastructure; A1.3 Testing the recovery plan; CC9.1 Reducing the risks of business disruption
- [HIPAA, 45 CFR Part 164 (Security Rule, Breach Notification, Privacy Rule)](frameworks/hipaa.md): 164.308(a)(7)(ii)(A) Data backup plan (required); 164.308(a)(7)(ii)(B) Disaster recovery plan (required); 164.308(a)(7)(ii)(D) Testing and revision procedures (addressable)
- [The GDPR, Regulation (EU) 2016/679](frameworks/gdpr.md): Art.32(1)(c) Security of processing; Art.32(1)(d) Security of processing
- [ISO 22301:2019, business continuity management systems](frameworks/iso22301.md): 8.4.5 Recovery; 8.5 Exercise programme; 8.6 Evaluation of business continuity documentation and capabilities
- [CSA Cloud Controls Matrix v4 (CSA STAR), by its domains](frameworks/csaccm.md): BCR Business continuity management and operational resilience
- [NIST Cybersecurity Framework 2.0: its categories and subcategories](frameworks/nistcsf.md): PR.DS-11 Backups of data are created, protected, maintained, and tested; RC.RP-03 The integrity of backups and other restoration assets is verified before using them for restoration
- [NIST SP 800-53 Rev. 5 (5.2.0), with the low, moderate, high and privacy baselines](frameworks/nist80053.md): CP-4 Contingency Plan Testing; CP-9 System Backup; CP-10 System Recovery and Reconstitution
- [NIST SP 800-171 Rev. 3, protecting controlled unclassified information (CMMC builds on it)](frameworks/nist800171.md): 03.08.09 System Backup – Cryptographic Protection
- [Common Criteria CC:2022 (ISO/IEC 15408): the functional (Part 2) and assurance (Part 3) families](frameworks/commoncriteria.md): FPT_RCV Trusted recovery

**Documented in** [deployment.md#backups-and-restore](../deployment.md#backups-and-restore), [deployment.md#point-in-time-recovery-pitr](../deployment.md#point-in-time-recovery-pitr)

**Proved by**

- `deploy/test_backup.py::test_By_default_it_keeps_BACKUP_KEEP_here_and_in_the_second_copy`
- `deploy/test_backup.py::test_Every_file_is_encrypted_the_MANIFEST_names_the_key_without_it_and_it_opens_only_with_that_key`
- `deploy/test_backup.py::test_A_key_inside_deploy_or_the_backups_is_refused_before_anything_is_taken`
- `deploy/test_pitr.py::test_pitr_yml_archives_wal_to_the_backup_disk`
- `deploy/test_pitr.py::test_the_drill_restores_into_a_scratch_container_never_the_stacks`
- `deploy/test_recovery.py::test_The_live_project_is_refused_before_anything_runs`
- `deploy/test_recovery_check.py::test_A_person_lost_in_the_restore_fails_it`

**Watched by** `app:backups` (Admin → Compliance, and `./arenactl check compliance`)

**The company's part** Schedule the backups, keep the second copy off the host, keep the backups' key (BACKUP_KEY_FILE) apart from them with a copy in the company's vault, run the drill on a schedule, and encrypt pgBackRest's repository (its repo1-cipher-type and repo1-cipher-pass) when using it.

### OPS-02: Code the model writes runs apart

Python, documents and a plugin's own server run in the sandbox: no network, a read-only root, its own limits, nothing that outlives a run.

**Serves**

- [ISO/IEC 27001:2022, clauses 4 to 10 and Annex A](frameworks/iso27001.md): A.8.22 Segregation of networks; A.8.7 Protection against malware
- [SOC 2, Trust Services Criteria](frameworks/soc2.md): CC6.8 Preventing or detecting unauthorised or malicious software
- [HIPAA, 45 CFR Part 164 (Security Rule, Breach Notification, Privacy Rule)](frameworks/hipaa.md): 164.308(a)(5)(ii)(B) Protection from malicious software (addressable)
- [The GDPR, Regulation (EU) 2016/679](frameworks/gdpr.md): Art.32(1)(b) Security of processing
- [CSA Cloud Controls Matrix v4 (CSA STAR), by its domains](frameworks/csaccm.md): IVS Infrastructure and virtualization security; AIS Application and interface security
- [NIST Cybersecurity Framework 2.0: its categories and subcategories](frameworks/nistcsf.md): PR.PS-05 Installation and execution of unauthorized software are prevented
- [NIST SP 800-53 Rev. 5 (5.2.0), with the low, moderate, high and privacy baselines](frameworks/nist80053.md): SC-39 Process Isolation; SC-44 Detonation Chambers
- [NIST AI Risk Management Framework 1.0 (AI 100-1): Govern, Map, Measure and Manage, as its Playbook lists them](frameworks/airmf.md): MEASURE 2.7 AI system security and resilience – as identified in the MAP function – are evaluated and documented.
- [EU Artificial Intelligence Act, Regulation (EU) 2024/1689: its articles and annexes](frameworks/euaiact.md): Art.15 Accuracy, robustness and cybersecurity

**Documented in** [plugins.md#a-server-of-its-own-in-the-sandbox](../plugins.md#a-server-of-its-own-in-the-sandbox)

**Proved by**

- `Llm.Tests/ChatToolsTests.Python_runs_in_the_sandbox_with_the_chats_files_and_what_it_writes_comes_back`
- `Llm.Tests/ChatToolsTests.A_program_calls_the_chats_tools_as_the_person_through_the_sandbox_and_only_what_it_prints_comes_back`
- `Llm.Tests/PluginTests.A_plugins_own_server_runs_in_the_sandbox_with_its_settings_one_job_a_call`

**The company's part** Keep the sandbox's host patched.

## Governance (GOV)

### GOV-01: The controls are kept from breaking

This file names, for each control, the tests and live checks that prove it; the guard test fails when one of them is gone, and the release checklist requires the live checks to pass.

**Serves**

- [ISO/IEC 27001:2022, clauses 4 to 10 and Annex A](frameworks/iso27001.md): A.5.36 Compliance with policies, rules and standards for information security; A.8.29 Security testing in development and acceptance; A.8.32 Change management; 9.1 Monitoring, measurement, analysis and evaluation; 7.5.3 Control of documented information
- [SOC 2, Trust Services Criteria](frameworks/soc2.md): CC4.1 Ongoing and separate evaluations; CC8.1 Authorising, designing, testing, approving and putting changes in place
- [HIPAA, 45 CFR Part 164 (Security Rule, Breach Notification, Privacy Rule)](frameworks/hipaa.md): 164.308(a)(8) Evaluation
- [The GDPR, Regulation (EU) 2016/679](frameworks/gdpr.md): Art.5(2) Principles of processing personal data; Art.25 Data protection by design and by default
- [ISO/IEC 42001:2023, the AI management system: clauses 4 to 10 and Annex A](frameworks/iso42001.md): 9.1 Monitoring, measurement, analysis and evaluation; A.6.2.4 AI system verification and validation
- [PCI DSS v4.0.1: its twelve requirements and their sections](frameworks/pcidss.md): 12.4 PCI DSS compliance is managed
- [CSA Cloud Controls Matrix v4 (CSA STAR), by its domains](frameworks/csaccm.md): A&A Audit and assurance
- [NIST Cybersecurity Framework 2.0: its categories and subcategories](frameworks/nistcsf.md): GV.OV-03 Organizational cybersecurity risk management performance is evaluated and reviewed for adjustments needed; ID.IM-01 Improvements are identified from evaluations
- [NIST SP 800-53 Rev. 5 (5.2.0), with the low, moderate, high and privacy baselines](frameworks/nist80053.md): CA-2 Control Assessments; CA-7 Continuous Monitoring; SA-11 Developer Testing and Evaluation
- [NIST SP 800-171 Rev. 3, protecting controlled unclassified information (CMMC builds on it)](frameworks/nist800171.md): 03.12.01 Security Assessment; 03.12.03 Continuous Monitoring
- [Common Criteria CC:2022 (ISO/IEC 15408): the functional (Part 2) and assurance (Part 3) families](frameworks/commoncriteria.md): ATE_FUN Functional tests; ALC_CMC CM capabilities

**Documented in** [compliance/README.md#controls-and-how-they-are-kept](README.md#controls-and-how-they-are-kept), [compliance/README.md#the-documents](README.md#the-documents), [compliance/README.md#when-a-change-weakens-a-control](README.md#when-a-change-weakens-a-control), [compliance/README.md#the-compliance-center](README.md#the-compliance-center), [development.md#releasing](../development.md#releasing)

**Proved by**

- `Llm.Tests/ComplianceControlsTests.Every_control_names_tests_documents_and_checks_that_exist`
- `Llm.Tests/ComplianceControlsTests.Every_test_that_proves_a_control_is_named_by_it`
- `Llm.Tests/ComplianceControlsTests.The_app_runs_its_checks_and_admins_see_them_with_the_controls`
- `Llm.Tests/ComplianceControlsTests.The_companys_part_is_attested_and_audited_and_the_evidence_holds_everything_an_auditor_asks_for`
- `deploy/test_compliance_check.py::test_A_failed_check_fails_the_run_and_warnings_only_with_strict`
- `deploy/test_compliance_docs.py::test_The_written_documents_are_current`
- `deploy/test_compliance_docs.py::test_Every_clause_a_control_cites_is_listed_and_every_document_is_there`
- `deploy/test_compliance_docs.py::test_The_next_releases_entry_names_each_under_Weakened_controls_or_it_fails`
- `deploy/test_compliance_docs.py::test_Nothing_is_weaker_than_in_the_newest_release_unless_the_changelog_says_so`

**The company's part** The release manager runs the checklist; an auditor samples it.

### GOV-02: Everyone accepts the acceptable use and privacy notice

The notice the company sets (acceptable use, the privacy notice) is on the sign-in page for anyone to read; each person accepts its current version before anything else, whichever way they signed in, and again when a new version is set; each acceptance is kept with its version, time and address, audited, and in the evidence.

**Serves**

- [ISO/IEC 27001:2022, clauses 4 to 10 and Annex A](frameworks/iso27001.md): A.5.10 Acceptable use of information and other associated assets; A.6.3 Information security awareness, education and training; 7.3 Awareness
- [SOC 2, Trust Services Criteria](frameworks/soc2.md): CC2.3 Communication with external parties; P1.1 Notice of privacy practices
- [HIPAA, 45 CFR Part 164 (Security Rule, Breach Notification, Privacy Rule)](frameworks/hipaa.md): 164.308(a)(5)(i) Security awareness and training
- [The GDPR, Regulation (EU) 2016/679](frameworks/gdpr.md): Art.12 Transparent information and how the rights are exercised; Art.13 Information when data is collected from the person
- [ISO/IEC 42001:2023, the AI management system: clauses 4 to 10 and Annex A](frameworks/iso42001.md): 7.3 Awareness
- [PCI DSS v4.0.1: its twelve requirements and their sections](frameworks/pcidss.md): 12.2 Acceptable use policies are defined and implemented
- [CSA Cloud Controls Matrix v4 (CSA STAR), by its domains](frameworks/csaccm.md): HRS Human resources
- [NIST Cybersecurity Framework 2.0: its categories and subcategories](frameworks/nistcsf.md): GV.PO-01 Policy for managing cybersecurity risks is established based on organizational context, cybersecurity strategy, and priorities and is communicated and enforced; PR.AT-01 Personnel are provided with awareness and training so that they possess the knowledge and skills to perform general tasks with cybersecurity risks in mind
- [NIST SP 800-53 Rev. 5 (5.2.0), with the low, moderate, high and privacy baselines](frameworks/nist80053.md): AC-8 System Use Notification; PL-4 Rules of Behavior; PT-5 Privacy Notice
- [NIST SP 800-171 Rev. 3, protecting controlled unclassified information (CMMC builds on it)](frameworks/nist800171.md): 03.15.03 Rules of Behavior
- [Common Criteria CC:2022 (ISO/IEC 15408): the functional (Part 2) and assurance (Part 3) families](frameworks/commoncriteria.md): FTA_TAB TOE access banners

**Documented in** [compliance/README.md#the-sign-in-notice](README.md#the-sign-in-notice)

**Proved by**

- `Llm.Tests/NoticeTests.Everyone_accepts_the_current_version_before_anything_else_and_a_new_version_again`
- `Llm.Tests/NoticeTests.Without_a_notice_nothing_waits_and_the_check_asks_for_one`

**Watched by** `app:notice` (Admin → Compliance, and `./arenactl check compliance`)

**The company's part** Write the notice and the policies it points to, keep them current, and set a new version when they change in substance.

### GOV-03: What a release ships is listed and its known vulnerabilities gate it

Each release has a software bill of materials (CycloneDX) of every .NET, npm and Python package and base image it ships, checked against OSV's advisories; a critical vulnerability fails the release unless it is accepted in writing, with why and until when, and an acceptance past its date counts no more.

**Serves**

- [ISO/IEC 27001:2022, clauses 4 to 10 and Annex A](frameworks/iso27001.md): A.5.21 Managing information security in the ICT supply chain; A.8.8 Management of technical vulnerabilities; A.8.28 Secure coding
- [SOC 2, Trust Services Criteria](frameworks/soc2.md): CC7.1 Detecting configuration changes and new vulnerabilities; CC8.1 Authorising, designing, testing, approving and putting changes in place; CC9.2 Risks from vendors and business partners
- [HIPAA, 45 CFR Part 164 (Security Rule, Breach Notification, Privacy Rule)](frameworks/hipaa.md): 164.308(a)(1)(ii)(A) Risk analysis (required); 164.308(a)(1)(ii)(B) Risk management (required)
- [The GDPR, Regulation (EU) 2016/679](frameworks/gdpr.md): Art.32(1)(d) Security of processing
- [PCI DSS v4.0.1: its twelve requirements and their sections](frameworks/pcidss.md): 6.3 Security vulnerabilities are identified and addressed
- [CSA Cloud Controls Matrix v4 (CSA STAR), by its domains](frameworks/csaccm.md): TVM Threat and vulnerability management; STA Supply chain management, transparency and accountability
- [NIST Cybersecurity Framework 2.0: its categories and subcategories](frameworks/nistcsf.md): ID.AM-02 Inventories of software, services, and systems managed by the organization are maintained; ID.RA-01 Vulnerabilities in assets are identified, validated, and recorded; GV.SC-07 The risks posed by a supplier, their products and services, and other third parties are understood, recorded, prioritized, assessed, responded to, and monitored over the course of the relationship
- [NIST SP 800-53 Rev. 5 (5.2.0), with the low, moderate, high and privacy baselines](frameworks/nist80053.md): CM-8 System Component Inventory; RA-5 Vulnerability Monitoring and Scanning; SI-2 Flaw Remediation; SR-4 Provenance
- [NIST SP 800-171 Rev. 3, protecting controlled unclassified information (CMMC builds on it)](frameworks/nist800171.md): 03.11.02 Vulnerability Monitoring and Scanning; 03.14.01 Flaw Remediation
- [Common Criteria CC:2022 (ISO/IEC 15408): the functional (Part 2) and assurance (Part 3) families](frameworks/commoncriteria.md): ALC_FLR Flaw remediation; AVA_VAN Vulnerability analysis

**Documented in** [compliance/README.md#supply-chain](README.md#supply-chain), [development.md#releasing](../development.md#releasing)

**Proved by**

- `deploy/test_release_scan.py::test_What_ships_is_listed_and_the_build_tools_are_not`
- `deploy/test_release_scan.py::test_A_critical_finding_fails_the_release_and_is_in_the_SBOM`
- `deploy/test_release_scan.py::test_Accepted_with_a_date_not_past_it_ships_and_high_ones_never_stop_it`
- `deploy/test_release_scan.py::test_OSV_out_of_reach_fails_it_too`

**The company's part** The release manager runs the scan and attaches the SBOM to the release; the security officer reviews each acceptance before its date and the high findings each release.

### GOV-04: Risks are assessed, treated and reviewed

A register of the risks to the company's information and its AI (Admin → Compliance → Risks): each with the assets it threatens (from what Arena knows: its stores, models, knowledge, data sources and tools), its likelihood and impact before and after treatment on a 5 by 5 scale, an owner, a treatment (reduced with named controls, accepted by a person with why and until when, a year at most, transferred, avoided), the treatment's actions with their dates, and a review date its level sets (three months for critical, six for high, a year otherwise). Every change is in the risk's history and the audit log, and the register is in the evidence.

**Serves**

- [ISO/IEC 27001:2022, clauses 4 to 10 and Annex A](frameworks/iso27001.md): 6.1.1 Actions to address risks and opportunities: general; 6.1.2 Information security risk assessment; 6.1.3 Information security risk treatment; 8.2 Information security risk assessment, performed; 8.3 Information security risk treatment, implemented; A.5.7 Threat intelligence
- [SOC 2, Trust Services Criteria](frameworks/soc2.md): CC3.1 Objectives clear enough to identify and assess risks; CC3.2 Identifying and analysing risks; CC3.3 The potential for fraud; CC3.4 Changes that could significantly affect internal control; CC9.1 Reducing the risks of business disruption
- [HIPAA, 45 CFR Part 164 (Security Rule, Breach Notification, Privacy Rule)](frameworks/hipaa.md): 164.308(a)(1)(ii)(A) Risk analysis (required); 164.308(a)(1)(ii)(B) Risk management (required)
- [The GDPR, Regulation (EU) 2016/679](frameworks/gdpr.md): Art.32(1) Security of processing; Art.32(2) Security of processing
- [ISO/IEC 42001:2023, the AI management system: clauses 4 to 10 and Annex A](frameworks/iso42001.md): 6.1.1 Actions to address risks and opportunities: general; 6.1.2 AI risk assessment; 6.1.3 AI risk treatment; 8.2 AI risk assessment, performed; 8.3 AI risk treatment, implemented
- [ISO 22301:2019, business continuity management systems](frameworks/iso22301.md): 6.1 Actions to address risks and opportunities; 8.2.3 Risk assessment
- [PCI DSS v4.0.1: its twelve requirements and their sections](frameworks/pcidss.md): 12.3 Risks to the CDE are identified and managed
- [CSA Cloud Controls Matrix v4 (CSA STAR), by its domains](frameworks/csaccm.md): GRC Governance, risk and compliance
- [NIST Cybersecurity Framework 2.0: its categories and subcategories](frameworks/nistcsf.md): GV.RM Risk Management Strategy; ID.RA Risk Assessment; ID.RA-05 Threats, vulnerabilities, likelihoods, and impacts are used to understand inherent risk and inform risk response prioritization; ID.RA-06 Risk responses are chosen, prioritized, planned, tracked, and communicated
- [NIST SP 800-53 Rev. 5 (5.2.0), with the low, moderate, high and privacy baselines](frameworks/nist80053.md): RA-3 Risk Assessment; RA-7 Risk Response; PM-9 Risk Management Strategy; CA-5 Plan of Action and Milestones
- [NIST SP 800-171 Rev. 3, protecting controlled unclassified information (CMMC builds on it)](frameworks/nist800171.md): 03.11.01 Risk Assessment
- [NIST AI Risk Management Framework 1.0 (AI 100-1): Govern, Map, Measure and Manage, as its Playbook lists them](frameworks/airmf.md): MAP 4.1 Approaches for mapping AI technology and legal risks of its components – including the use of third-party data or software – are in place, followed, and documented, as are risks of infringement of a third-party’s intellectual property or other rights.; MANAGE 1.2 Treatment of documented AI risks is prioritized based on impact, likelihood, or available resources or methods.; MANAGE 1.3 Responses to the AI risks deemed high priority as identified by the Map function, are developed, planned, and documented. Risk response options can include mitigating, transferring, avoiding, or accepting.; MANAGE 1.4 Negative residual risks (defined as the sum of all unmitigated risks) to both downstream acquirers of AI systems and end users are documented.
- [EU Artificial Intelligence Act, Regulation (EU) 2024/1689: its articles and annexes](frameworks/euaiact.md): Art.9 Risk management system

**Documented in** [compliance/README.md#risks](README.md#risks), [compliance/policies/risk-management.md#the-risk-register](policies/risk-management.md#the-risk-register)

**Proved by**

- `Llm.Tests/RiskRegisterTests.A_risk_has_its_levels_before_and_after_treatment_its_controls_and_a_review_its_level_sets`
- `Llm.Tests/RiskRegisterTests.An_acceptance_names_who_why_and_until_when_and_a_treatment_has_its_actions_and_history`
- `Llm.Tests/RiskRegisterTests.Only_those_who_manage_compliance_change_the_register_and_an_auditor_reads_it`

**Watched by** `app:risks` (Admin → Compliance, and `./arenactl check compliance`)

**The company's part** Assess the risks at least once a year and after significant changes, with the owners who know them; management accepts the risks left, and the treatment's actions are done by their dates.

### GOV-05: The Statement of Applicability is kept and approved

For each of ISO 27001's and ISO 42001's Annex A controls (Admin → Compliance → Statement of Applicability): whether it applies, and why when it does not; how far it is in place; Arena's controls that serve it and the open risks they treat. Approved as a whole by a person, once every control is decided; each approval is a version kept as it stood, and each line's change is audited. The statement as it stands, every version and the approvals are downloads and in the evidence.

**Serves**

- [ISO/IEC 27001:2022, clauses 4 to 10 and Annex A](frameworks/iso27001.md): 6.1.3 Information security risk treatment; 7.5.3 Control of documented information
- [ISO/IEC 42001:2023, the AI management system: clauses 4 to 10 and Annex A](frameworks/iso42001.md): 6.1.3 AI risk treatment; 7.5.3 Control of documented information
- [CSA Cloud Controls Matrix v4 (CSA STAR), by its domains](frameworks/csaccm.md): GRC Governance, risk and compliance
- [NIST SP 800-53 Rev. 5 (5.2.0), with the low, moderate, high and privacy baselines](frameworks/nist80053.md): PL-2 System Security and Privacy Plans
- [NIST SP 800-171 Rev. 3, protecting controlled unclassified information (CMMC builds on it)](frameworks/nist800171.md): 03.15.02 System Security Plan

**Documented in** [compliance/README.md#statement-of-applicability](README.md#statement-of-applicability)

**Proved by**

- `Llm.Tests/SoaTests.Each_Annex_A_control_says_whether_it_applies_and_why_how_far_and_what_serves_it`
- `Llm.Tests/SoaTests.It_is_approved_once_every_control_is_decided_each_approval_a_version_kept_as_it_stood`

**Watched by** `app:soa` (Admin → Compliance, and `./arenactl check compliance`)

**The company's part** Decide each control with the risk owners, approve the statement (the information security officer, or management), and approve it again after the risk assessment each year and when the scope changes.

### GOV-06: Policies are owned, approved, reviewed and read

The company's policies (Admin → Compliance → Policies), adopted from the templates in docs/compliance or written there: each with an owner, the people who must read it (everyone, the admins, or groups), and versions. A draft is in force once a person approves it (a later version says what changed); its review is due after the period the policy sets; and each reader acknowledges the version in force under Your account, told of it in their bell. Every step is audited; every version, its approval and review, the acknowledgements and the texts in force are in the evidence.

**Serves**

- [ISO/IEC 27001:2022, clauses 4 to 10 and Annex A](frameworks/iso27001.md): 5.2 Policy; 7.3 Awareness; 7.5.2 Creating and updating documented information; 7.5.3 Control of documented information; A.5.1 Policies for information security; A.6.3 Information security awareness, education and training
- [ISO/IEC 42001:2023, the AI management system: clauses 4 to 10 and Annex A](frameworks/iso42001.md): 5.2 AI policy; 7.3 Awareness; 7.5.2 Creating and updating documented information; 7.5.3 Control of documented information; A.2.2 AI policy; A.2.4 Review of the AI policy
- [ISO 22301:2019, business continuity management systems](frameworks/iso22301.md): 5.2 Business continuity policy; 7.5 Documented information
- [SOC 2, Trust Services Criteria](frameworks/soc2.md): CC2.2 Internal communication of objectives and responsibilities; CC5.3 Policies and procedures that put controls in place
- [HIPAA, 45 CFR Part 164 (Security Rule, Breach Notification, Privacy Rule)](frameworks/hipaa.md): 164.316(a) Policies and procedures; 164.316(b)(1) Documentation; 164.316(b)(2)(iii) Updates (required)
- [PCI DSS v4.0.1: its twelve requirements and their sections](frameworks/pcidss.md): 12.1 An information security policy is known and current
- [CSA Cloud Controls Matrix v4 (CSA STAR), by its domains](frameworks/csaccm.md): GRC Governance, risk and compliance
- [NIST Cybersecurity Framework 2.0: its categories and subcategories](frameworks/nistcsf.md): GV.PO-01 Policy for managing cybersecurity risks is established based on organizational context, cybersecurity strategy, and priorities and is communicated and enforced; GV.PO-02 Policy for managing cybersecurity risks is reviewed, updated, communicated, and enforced to reflect changes in requirements, threats, technology, and organizational mission
- [NIST SP 800-53 Rev. 5 (5.2.0), with the low, moderate, high and privacy baselines](frameworks/nist80053.md): PL-1 Policy and Procedures; PM-1 Information Security Program Plan
- [NIST SP 800-171 Rev. 3, protecting controlled unclassified information (CMMC builds on it)](frameworks/nist800171.md): 03.15.01 Policy and Procedures
- [NIST AI Risk Management Framework 1.0 (AI 100-1): Govern, Map, Measure and Manage, as its Playbook lists them](frameworks/airmf.md): GOVERN 1.2 The characteristics of trustworthy AI are integrated into organizational policies, processes, and procedures.; GOVERN 1.4 The risk management process and its outcomes are established through transparent policies, procedures, and other controls based on organizational risk priorities.

**Documented in** [compliance/README.md#policies](README.md#policies)

**Proved by**

- `Llm.Tests/PolicyTests.A_policy_from_a_template_is_in_force_once_approved_and_each_reader_acknowledges_its_version`
- `Llm.Tests/PolicyTests.A_new_version_says_what_changed_is_read_again_and_a_review_keeps_it_in_force`

**Watched by** `app:policies` (Admin → Compliance, and `./arenactl check compliance`)

**The company's part** Adopt the policies that apply, fill in their brackets, have them approved by management, review each on time, and follow up with those who have not read them.

### GOV-07: Audits, their findings and corrective actions are kept

The audit programme (Admin → Compliance → Audits): each audit, internal or an external body's (a certification's stages, surveillance, recertification, a SOC 2 period), with its scope, auditor, dates and conclusion. Each finding (a major or minor nonconformity, an observation, an opportunity) against a framework's clause and Arena's control, with its correction, root cause, corrective action, owner and date; a nonconformity's action is taken only once its root cause and action are said, and closed only when someone checks that it worked (else it opens again). The certificates and reports the company holds, with when they end; and waivers of a control not met for a time, by a person, with why, what makes up for it and until when (a year at most). Every step is audited and all of it is in the evidence.

**Serves**

- [ISO/IEC 27001:2022, clauses 4 to 10 and Annex A](frameworks/iso27001.md): 9.2.1 Internal audit: general; 9.2.2 Internal audit programme; 10.1 Continual improvement; 10.2 Nonconformity and corrective action; A.5.35 Independent review of information security; A.5.36 Compliance with policies, rules and standards for information security
- [ISO/IEC 42001:2023, the AI management system: clauses 4 to 10 and Annex A](frameworks/iso42001.md): 9.2.1 Internal audit: general; 9.2.2 Internal audit programme; 10.1 Continual improvement; 10.2 Nonconformity and corrective action
- [ISO 22301:2019, business continuity management systems](frameworks/iso22301.md): 9.2 Internal audit; 10.1 Nonconformity and corrective action; 10.2 Continual improvement
- [SOC 2, Trust Services Criteria](frameworks/soc2.md): CC4.1 Ongoing and separate evaluations; CC4.2 Communicating deficiencies; CC5.1 Control activities that reduce risks
- [PCI DSS v4.0.1: its twelve requirements and their sections](frameworks/pcidss.md): 12.4 PCI DSS compliance is managed
- [CSA Cloud Controls Matrix v4 (CSA STAR), by its domains](frameworks/csaccm.md): A&A Audit and assurance
- [NIST Cybersecurity Framework 2.0: its categories and subcategories](frameworks/nistcsf.md): ID.IM-01 Improvements are identified from evaluations; ID.IM-02 Improvements are identified from security tests and exercises, including those done in coordination with suppliers and relevant third parties; GV.OV-01 Cybersecurity risk management strategy outcomes are reviewed to inform and adjust strategy and direction
- [NIST SP 800-53 Rev. 5 (5.2.0), with the low, moderate, high and privacy baselines](frameworks/nist80053.md): CA-2 Control Assessments; CA-5 Plan of Action and Milestones; CA-7 Continuous Monitoring
- [NIST SP 800-171 Rev. 3, protecting controlled unclassified information (CMMC builds on it)](frameworks/nist800171.md): 03.12.01 Security Assessment; 03.12.02 Plan of Action and Milestones
- [NIST AI Risk Management Framework 1.0 (AI 100-1): Govern, Map, Measure and Manage, as its Playbook lists them](frameworks/airmf.md): GOVERN 1.5 Ongoing monitoring and periodic review of the risk management process and its outcomes are planned, organizational roles and responsibilities are clearly defined, including determining the frequency of periodic review.

**Documented in** [compliance/README.md#audits-findings-and-certificates](README.md#audits-findings-and-certificates)

**Proved by**

- `Llm.Tests/AuditTests.A_finding_is_acted_on_with_its_root_cause_and_closed_only_when_its_action_is_checked`
- `Llm.Tests/AuditTests.The_check_wants_an_internal_audit_each_year_findings_in_time_certificates_current_and_waivers_decided`

**Watched by** `app:audits` (Admin → Compliance, and `./arenactl check compliance`)

**The company's part** Plan internal audits that cover every clause and control over the certification cycle, by people who do not audit their own work; act on the findings by their dates; renew the certificates before they end.

### GOV-08: Suppliers are registered, agreed with and reviewed

The supplier register (Admin → Compliance → Suppliers): each model provider, cloud, connector or other service, with what it provides, the kinds of data it receives (personal, health, confidential, code, payment), where, its certifications and reports, the agreements signed, its risk and a review its risk sets (six months high, a year medium, two years low). The services Arena connects to (cloud model servers, tool servers, knowledge connectors, the GitLab Argus reads, Hugging Face) are offered for the register until they are in it. Every change and review is audited, and the register is in the evidence.

**Serves**

- [ISO/IEC 27001:2022, clauses 4 to 10 and Annex A](frameworks/iso27001.md): A.5.19 Information security in supplier relationships; A.5.20 Addressing information security within supplier agreements; A.5.21 Managing information security in the ICT supply chain; A.5.22 Monitoring, review and change management of supplier services; A.5.23 Information security for use of cloud services
- [ISO/IEC 42001:2023, the AI management system: clauses 4 to 10 and Annex A](frameworks/iso42001.md): A.10.2 Allocating responsibilities; A.10.3 Suppliers
- [SOC 2, Trust Services Criteria](frameworks/soc2.md): CC9.2 Risks from vendors and business partners
- [HIPAA, 45 CFR Part 164 (Security Rule, Breach Notification, Privacy Rule)](frameworks/hipaa.md): 164.308(b)(1) Business associate contracts and other arrangements; 164.314(a)(1) Business associate contracts or other arrangements
- [The GDPR, Regulation (EU) 2016/679](frameworks/gdpr.md): Art.28 Processors
- [PCI DSS v4.0.1: its twelve requirements and their sections](frameworks/pcidss.md): 12.8 Risks from third-party service providers are managed
- [CSA Cloud Controls Matrix v4 (CSA STAR), by its domains](frameworks/csaccm.md): STA Supply chain management, transparency and accountability
- [NIST Cybersecurity Framework 2.0: its categories and subcategories](frameworks/nistcsf.md): GV.SC-04 Suppliers are known and prioritized by criticality; GV.SC-07 The risks posed by a supplier, their products and services, and other third parties are understood, recorded, prioritized, assessed, responded to, and monitored over the course of the relationship
- [NIST SP 800-53 Rev. 5 (5.2.0), with the low, moderate, high and privacy baselines](frameworks/nist80053.md): SA-9 External System Services; SR-6 Supplier Assessments and Reviews
- [NIST SP 800-171 Rev. 3, protecting controlled unclassified information (CMMC builds on it)](frameworks/nist800171.md): 03.17.01 Supply Chain Risk Management Plan; 03.17.03 Supply Chain Requirements and Processes
- [NIST AI Risk Management Framework 1.0 (AI 100-1): Govern, Map, Measure and Manage, as its Playbook lists them](frameworks/airmf.md): GOVERN 6.1 Policies and procedures are in place that address AI risks associated with third-party entities, including risks of infringement of a third party’s intellectual property or other rights.

**Documented in** [compliance/README.md#suppliers](README.md#suppliers)

**Proved by**

- `Llm.Tests/SupplierTests.What_Arena_connects_to_is_offered_until_registered_with_its_data_agreements_and_review`

**Watched by** `app:suppliers` (Admin → Compliance, and `./arenactl check compliance`)

**The company's part** Register each supplier with what it gets, sign a DPA (and a BAA for health data) where it processes personal data, keep its certificates' dates current, and review it on time.

### GOV-09: AI systems are registered, classified, assessed and overseen

The register of the company's AI systems (Admin → Compliance → AI systems): assistants, agents, apps and models, each with its purpose, intended use and whom it affects, its class under the EU AI Act (minimal, limited, high-risk with its Annex III area, or a prohibited practice) and the company's role (deployer, provider), its impact assessment in the assessor's name, its human oversight and its evaluation. A system is put into use only once its class, purpose and impact assessment are said, and for a high-risk one its area, oversight and evaluation; a prohibited practice is never in use. Reviewed each year (every six months when high-risk). The shared assistants, Agent Studio's workflows, the shared apps and the models are offered until registered. Audited, and in the evidence with each impact assessment.

**Serves**

- [ISO/IEC 42001:2023, the AI management system: clauses 4 to 10 and Annex A](frameworks/iso42001.md): 6.1.4 AI system impact assessment; 8.4 AI system impact assessment, performed; A.4.2 Resource documentation; A.5.2 AI system impact assessment process; A.5.3 Documentation of AI system impact assessments; A.5.4 Assessing AI system impact on individuals or groups of individuals; A.5.5 Assessing societal impacts of AI systems; A.6.2.4 AI system verification and validation; A.6.2.6 AI system operation and monitoring; A.9.2 Processes for responsible use of AI systems; A.9.3 Objectives for responsible use of AI systems; A.9.4 Intended use of the AI system
- [EU Artificial Intelligence Act, Regulation (EU) 2024/1689: its articles and annexes](frameworks/euaiact.md): Art.5 Prohibited AI practices; Art.6 Classification rules for high-risk AI systems; Art.14 Human oversight; Art.26 Obligations of deployers of high-risk AI systems; Art.27 Fundamental rights impact assessment for high-risk AI systems
- [NIST AI Risk Management Framework 1.0 (AI 100-1): Govern, Map, Measure and Manage, as its Playbook lists them](frameworks/airmf.md): GOVERN 1.6 Mechanisms are in place to inventory AI systems and are resourced according to organizational risk priorities.; MAP 1.1 Intended purpose, potentially beneficial uses, context-specific laws, norms and expectations, and prospective settings in which the AI system will be deployed are understood and documented. Considerations include: specific set or types of users along with their expectations; potential positive and negative impacts of system uses to individuals, communities, organizations, society, and the planet; assumptions and related limitations about AI system purposes; uses and risks across the development or product AI lifecycle; TEVV and system metrics.; MAP 3.5 Processes for human oversight are defined, assessed, and documented in accordance with organizational policies from GOVERN function.; MAP 5.1 Likelihood and magnitude of each identified impact (both potentially beneficial and harmful) based on expected use, past uses of AI systems in similar contexts, public incident reports, feedback from those external to the team that developed or deployed the AI system, or other data are identified and documented.; MEASURE 2.1 Test sets, metrics, and details about the tools used during test, evaluation, validation, and verification (TEVV) are documented.
- [NIST SP 800-53 Rev. 5 (5.2.0), with the low, moderate, high and privacy baselines](frameworks/nist80053.md): CM-8 System Component Inventory; PM-5 System Inventory

**Documented in** [compliance/README.md#ai-systems](README.md#ai-systems)

**Proved by**

- `Llm.Tests/AiGovernanceTests.An_AI_system_is_put_into_use_only_once_its_class_impact_oversight_and_evaluation_are_said`

**Watched by** `app:ai-systems` (Admin → Compliance, and `./arenactl check compliance`)

**The company's part** Register each AI system before it goes into use, with its owner; classify it under the AI Act with your counsel; assess its impact with the people it affects in mind; give each high-risk system its human oversight and evaluation, and its fundamental rights assessment where Article 27 asks for one.

### GOV-10: People know when content is made by AI

Under the chat's box, people are told that answers are made by AI and can be wrong. A picture a model made (in the chat or the media studio) says so in its own metadata, as IPTC's digital source type trainedAlgorithmicMedia in XMP; a person's picture a model changed (an edit, a cutout) says compositeWithTrainedAlgorithmicMedia. Sound and video a model made (speech, voices, music, clips, stories) say so too, where XMP's spec puts it: an MP3's ID3v2 tag, an MP4's uuid box, a WAV's or an AVI's _PMX chunk; WebM, which has no place for XMP, in a Matroska tag. The chat's read-aloud, streamed as it plays, is not kept and not marked.

**Serves**

- [EU Artificial Intelligence Act, Regulation (EU) 2024/1689: its articles and annexes](frameworks/euaiact.md): Art.50 Transparency obligations for providers and deployers of certain AI systems
- [ISO/IEC 42001:2023, the AI management system: clauses 4 to 10 and Annex A](frameworks/iso42001.md): A.8.2 System documentation and information for users; A.8.5 Information for interested parties
- [NIST AI Risk Management Framework 1.0 (AI 100-1): Govern, Map, Measure and Manage, as its Playbook lists them](frameworks/airmf.md): GOVERN 4.2 Organizational teams document the risks and potential impacts of the AI technology they design, develop, deploy, evaluate and use, and communicate about the impacts more broadly.; MANAGE 2.4 Mechanisms are in place and applied, responsibilities are assigned and understood to supersede, disengage, or deactivate AI systems that demonstrate performance or outcomes inconsistent with intended use.

**Documented in** [compliance/README.md#ai-systems](README.md#ai-systems)

**Proved by**

- `Llm.Tests/AiGovernanceTests.A_generated_picture_says_in_its_metadata_that_a_model_made_or_changed_it`
- `Llm.Tests/AiGovernanceTests.Generated_sound_and_video_say_in_their_metadata_that_a_model_made_them`

**The company's part** Tell people elsewhere too where your use of AI is not obvious (a published text, a voice on the phone), and keep the marks on what you publish.

### GOV-11: Readiness is measured and the evidence is kept over time

Each day once, every compliance check's result is kept (six years), so an audit over a period (SOC 2 Type II) reads it day by day. For each framework, Admin → Compliance says how ready the company is, from 0 to 100: Arena's controls that serve it holding now, their operator's parts attested within the year, and the checks of the management system its audit reads; with each gap and where it is put right. Each framework's auditor package is one download: its clauses with Arena's controls, their state and attestations, the checks' history over the period asked, the records its audit reads, and the drafts its scheme asks for (a Common Criteria Security Target, the AI Act's Annex IV for each high-risk system, a CSF profile). Each download is audited.

**Serves**

- [ISO/IEC 27001:2022, clauses 4 to 10 and Annex A](frameworks/iso27001.md): 9.1 Monitoring, measurement, analysis and evaluation; 9.3.2 Management review inputs
- [ISO/IEC 42001:2023, the AI management system: clauses 4 to 10 and Annex A](frameworks/iso42001.md): 9.1 Monitoring, measurement, analysis and evaluation; 9.3.2 Management review inputs
- [ISO 22301:2019, business continuity management systems](frameworks/iso22301.md): 9.1 Monitoring, measurement, analysis and evaluation
- [SOC 2, Trust Services Criteria](frameworks/soc2.md): CC4.1 Ongoing and separate evaluations; CC4.2 Communicating deficiencies
- [CSA Cloud Controls Matrix v4 (CSA STAR), by its domains](frameworks/csaccm.md): A&A Audit and assurance
- [NIST Cybersecurity Framework 2.0: its categories and subcategories](frameworks/nistcsf.md): GV.OV-03 Organizational cybersecurity risk management performance is evaluated and reviewed for adjustments needed
- [NIST SP 800-53 Rev. 5 (5.2.0), with the low, moderate, high and privacy baselines](frameworks/nist80053.md): CA-7 Continuous Monitoring; CA-2 Control Assessments
- [NIST SP 800-171 Rev. 3, protecting controlled unclassified information (CMMC builds on it)](frameworks/nist800171.md): 03.12.03 Continuous Monitoring
- [EU Artificial Intelligence Act, Regulation (EU) 2024/1689: its articles and annexes](frameworks/euaiact.md): Art.11 Technical documentation; Annex IV Technical documentation referred to in Article 11(1)
- [NIST AI Risk Management Framework 1.0 (AI 100-1): Govern, Map, Measure and Manage, as its Playbook lists them](frameworks/airmf.md): MEASURE 1.1 Approaches and metrics for measurement of AI risks enumerated during the Map function are selected for implementation starting with the most significant AI risks. The risks or trustworthiness characteristics that will not – or cannot – be measured are properly documented.
- [Common Criteria CC:2022 (ISO/IEC 15408): the functional (Part 2) and assurance (Part 3) families](frameworks/commoncriteria.md): ASE_TSS TOE summary specification; ATE_FUN Functional tests

**Documented in** [compliance/README.md#readiness-and-the-auditors-package](README.md#readiness-and-the-auditors-package)

**Proved by**

- `Llm.Tests/ReadinessTests.Each_framework_says_how_ready_and_what_is_missing_and_its_package_holds_what_its_auditor_reads`
- `Llm.Tests/ReadinessTests.The_checks_are_kept_once_a_day_and_the_package_reads_them_over_the_period`

**The company's part** Read each framework's gaps before its audit, and hand the auditor its package for the period under audit.

### GOV-12: The management system is run, reviewed and measured

Admin → Compliance → Management: each of Arena's controls has its owner in the company; management reviews are recorded with who took part, the minutes and the decisions, beside their inputs as the clause names them, drawn up from the system as it stood (the actions from the review before, the findings, the audits, the objectives and readiness, the risks); the security objectives are each measured by a compliance check's daily history, as the share of days it held against its target; and an auditor, who may only read, asks for evidence there and is answered. Audited, and in the evidence.

**Serves**

- [ISO/IEC 27001:2022, clauses 4 to 10 and Annex A](frameworks/iso27001.md): 5.3 Organizational roles, responsibilities and authorities; 6.2 Information security objectives and planning to achieve them; 9.3.1 Management review: general; 9.3.2 Management review inputs; 9.3.3 Management review results
- [ISO/IEC 42001:2023, the AI management system: clauses 4 to 10 and Annex A](frameworks/iso42001.md): 5.3 Roles, responsibilities and authorities; 6.2 AI objectives and planning to achieve them; 9.3.1 Management review: general; 9.3.2 Management review inputs; 9.3.3 Management review results
- [ISO 22301:2019, business continuity management systems](frameworks/iso22301.md): 5.3 Roles, responsibilities and authorities; 6.2 Business continuity objectives and planning to achieve them; 9.3 Management review
- [SOC 2, Trust Services Criteria](frameworks/soc2.md): CC1.3 Structures, reporting lines, authorities and responsibilities; CC2.2 Internal communication of objectives and responsibilities
- [CSA Cloud Controls Matrix v4 (CSA STAR), by its domains](frameworks/csaccm.md): GRC Governance, risk and compliance
- [NIST Cybersecurity Framework 2.0: its categories and subcategories](frameworks/nistcsf.md): GV.RR-01 Organizational leadership is responsible and accountable for cybersecurity risk and fosters a culture that is risk-aware, ethical, and continually improving; GV.RR-02 Roles, responsibilities, and authorities related to cybersecurity risk management are established, communicated, understood, and enforced
- [NIST SP 800-53 Rev. 5 (5.2.0), with the low, moderate, high and privacy baselines](frameworks/nist80053.md): PM-2 Information Security Program Leadership Role; PM-6 Measures of Performance
- [NIST AI Risk Management Framework 1.0 (AI 100-1): Govern, Map, Measure and Manage, as its Playbook lists them](frameworks/airmf.md): GOVERN 2.1 Roles and responsibilities and lines of communication related to mapping, measuring, and managing AI risks are documented and are clear to individuals and teams throughout the organization.

**Documented in** [compliance/README.md#running-the-management-system](README.md#running-the-management-system)

**Proved by**

- `Llm.Tests/ManagementTests.A_review_keeps_its_inputs_as_the_system_stood_and_objectives_are_measured_by_their_checks`
- `Llm.Tests/ManagementTests.An_auditor_reads_and_asks_for_evidence_and_changes_nothing_else`

**Watched by** `app:management` (Admin → Compliance, and `./arenactl check compliance`)

**The company's part** Name each control's owner, hold the management review at least once a year with the leadership, set objectives that matter, and answer the auditors' requests in time.

## Vulnerabilities (VUL)

### VUL-01: Repositories are reviewed for vulnerabilities, fixed or accepted in time

Code Studio → Security reviews a repository at a commit, read with the person's own access: its packages matched against GitHub's reviewed advisories and CISA's known-exploited list (the advisories pack, renewed each day), and the places where outside data reaches a query, a command, a path, a fetch, a deserialiser or HTML, each kept only when its line is cited and its CWE is in the security pack, then put to a pass that tries to refute it. Each finding is fixed, accepted with why and until when, or marked wrong, audited; its bill of materials is kept. The vulnerabilities check wants a review within 30 days against advisories no older than a week, and each open finding fixed within its severity's time (critical 7 days, high 30, medium 90, low 180); an overdue critical or high one fails it.

**Serves**

- [ISO/IEC 27001:2022, clauses 4 to 10 and Annex A](frameworks/iso27001.md): A.8.8 Management of technical vulnerabilities; A.8.28 Secure coding; A.8.29 Security testing in development and acceptance
- [SOC 2, Trust Services Criteria](frameworks/soc2.md): CC7.1 Detecting configuration changes and new vulnerabilities; CC8.1 Authorising, designing, testing, approving and putting changes in place
- [HIPAA, 45 CFR Part 164 (Security Rule, Breach Notification, Privacy Rule)](frameworks/hipaa.md): 164.308(a)(1)(ii)(A) Risk analysis (required); 164.308(a)(1)(ii)(B) Risk management (required)
- [The GDPR, Regulation (EU) 2016/679](frameworks/gdpr.md): Art.32(1)(d) Security of processing
- [PCI DSS v4.0.1: its twelve requirements and their sections](frameworks/pcidss.md): 6.2 Bespoke and custom software is developed securely; 6.3 Security vulnerabilities are identified and addressed; 11.3 Vulnerabilities are regularly identified and addressed
- [CSA Cloud Controls Matrix v4 (CSA STAR), by its domains](frameworks/csaccm.md): TVM Threat and vulnerability management; AIS Application and interface security
- [NIST Cybersecurity Framework 2.0: its categories and subcategories](frameworks/nistcsf.md): ID.RA-01 Vulnerabilities in assets are identified, validated, and recorded; PR.PS-06 Secure software development practices are integrated, and their performance is monitored throughout the software development life cycle
- [NIST SP 800-53 Rev. 5 (5.2.0), with the low, moderate, high and privacy baselines](frameworks/nist80053.md): RA-5 Vulnerability Monitoring and Scanning; SI-2 Flaw Remediation; SA-11 Developer Testing and Evaluation; SA-15 Development Process, Standards, and Tools
- [NIST SP 800-171 Rev. 3, protecting controlled unclassified information (CMMC builds on it)](frameworks/nist800171.md): 03.11.02 Vulnerability Monitoring and Scanning; 03.14.01 Flaw Remediation
- [EU Artificial Intelligence Act, Regulation (EU) 2024/1689: its articles and annexes](frameworks/euaiact.md): Art.15 Accuracy, robustness and cybersecurity
- [Common Criteria CC:2022 (ISO/IEC 15408): the functional (Part 2) and assurance (Part 3) families](frameworks/commoncriteria.md): AVA_VAN Vulnerability analysis; ALC_FLR Flaw remediation

**Documented in** [compliance/README.md#vulnerabilities](README.md#vulnerabilities)

**Proved by**

- `Llm.Tests/SecurityReviewTests.A_repository_is_reviewed_as_its_person_and_only_cited_weaknesses_the_pack_knows_are_kept`
- `Llm.Tests/SecurityReviewTests.The_vulnerabilities_check_counts_what_is_past_its_time_to_fix`
- `Llm.Tests/SecurityReviewTests.When_the_advisories_are_renewed_a_reviews_packages_are_checked_again_at_the_commit_it_read`
- `Llm.Tests/FixTests.A_security_findings_fix_starts_from_the_branch_reviewed_with_the_weakness_its_references_and_a_test_first`
- `Llm.Tests/SecurityReviewTests.Admin_security_shows_each_repositorys_latest_review_what_cannot_wait_this_week_and_the_risks_accepted`
- `Llm.Tests/SecurityReviewTests.What_people_decided_is_remembered_for_the_repositorys_next_review_and_a_fixed_one_found_again_is_open`
- `Llm.Tests/ReviewTests.A_merge_request_that_adds_a_vulnerable_package_or_a_scanners_finding_says_so`
- `Llm.Tests/FixTests.A_security_fixs_change_is_checked_by_the_scanners_and_the_review_before_its_merge_request`
- `Llm.Tests/CodeArenaSyncTests.Code_Arena_reads_the_latest_security_review_of_its_branch_with_what_Fix_it_would_ask`

**Watched by** `app:vulnerabilities` (Admin → Compliance, and `./arenactl check compliance`)

**The company's part** Load the advisories pack and renew it each day, review each repository at least monthly and before each release, and fix or accept what it finds within its time.
