# The controls

<!-- Written by tools/compliance-docs.py from controls.yaml and frameworks.yaml: change those and run it, not this. -->

The 27 controls Arena provides, from [controls.yaml](controls.yaml). How they are kept: [README](README.md#controls-and-how-they-are-kept). Each framework's clauses and the controls that serve them: [ISO/IEC 27001:2022, Annex A](frameworks/iso27001.md), [SOC 2, Trust Services Criteria](frameworks/soc2.md), [HIPAA, 45 CFR Part 164 (Security Rule, Breach Notification, Privacy Rule)](frameworks/hipaa.md), [The GDPR, Regulation (EU) 2016/679](frameworks/gdpr.md).

## People and access (IAM)

### IAM-01: Each person signs in as themselves, with a strong password

Every person has their own account. Passwords must score 3 or more on zxcvbn, with the person's own names counted against them; a wrong password and an unknown name get the same answer; guessing one account, spraying many and floods are stopped and lock out.

**Serves**

- [ISO/IEC 27001:2022, Annex A](frameworks/iso27001.md): A.5.16 Identity management; A.5.17 Authentication information; A.8.5 Secure authentication
- [SOC 2, Trust Services Criteria](frameworks/soc2.md): CC6.1 Logical access security software, infrastructure and architectures; CC6.2 Registering and authorising new users
- [HIPAA, 45 CFR Part 164 (Security Rule, Breach Notification, Privacy Rule)](frameworks/hipaa.md): 164.308(a)(5)(ii)(C) Log-in monitoring (addressable); 164.308(a)(5)(ii)(D) Password management (addressable); 164.312(a)(2)(i) Unique user identification (required); 164.312(d) Person or entity authentication
- [The GDPR, Regulation (EU) 2016/679](frameworks/gdpr.md): Art.32 Security of processing

**Documented in** [authentication.md#what-protects-the-sign-in](../authentication.md#what-protects-the-sign-in)

**Proved by**

- `Llm.Tests/IdentityTests.Wrong_password_and_unknown_name_get_the_same_answer`
- `Llm.Tests/IdentityTests.A_person_changes_their_own_password_only_with_the_current_one_and_a_strong_new_one`
- `Llm.Tests/IdentityTests.Five_failures_ban_that_account_from_that_address_but_not_colleagues_behind_it`
- `Llm.Tests/IdentityTests.Spraying_many_accounts_from_one_address_bans_the_address`
- `Llm.Tests/IdentityTests.Ten_failures_lock_the_account_from_any_address`
- `Llm.Tests/ConcurrentSignInTests.Parallel_wrong_passwords_still_reach_the_lockout`

**Watched by** `app:passwords` (Admin → Compliance, and `deploy/scripts/compliance-check.py`)

**The company's part** A password policy people are told about; no shared accounts; the first admin's password (ADMIN_PASSWORD) kept as a break-glass secret.

### IAM-02: Sessions end on their own

A session ends after an hour without activity and after 12 hours in any case (30 days only when the person asks to stay signed in); each one is re-checked against the account every minute, so a disabled person or a reset password ends it at once. The cookie is Secure, HttpOnly and SameSite.

**Serves**

- [ISO/IEC 27001:2022, Annex A](frameworks/iso27001.md): A.8.5 Secure authentication
- [SOC 2, Trust Services Criteria](frameworks/soc2.md): CC6.1 Logical access security software, infrastructure and architectures
- [HIPAA, 45 CFR Part 164 (Security Rule, Breach Notification, Privacy Rule)](frameworks/hipaa.md): 164.312(a)(2)(iii) Automatic logoff (addressable)
- [The GDPR, Regulation (EU) 2016/679](frameworks/gdpr.md): Art.32 Security of processing

**Documented in** [authentication.md#what-protects-the-sign-in](../authentication.md#what-protects-the-sign-in)

**Proved by**

- `Llm.Tests/SessionCapTests.Refreshing_a_session_does_not_restart_the_twelve_hour_cap`
- `Llm.Tests/IdentityTests.A_password_reset_ends_the_persons_sessions_and_only_the_new_password_works`
- `Llm.Tests/IdentityTests.Disabling_signs_out_and_blocks_keys_and_enabling_undoes_it`

**Watched by** `app:sessions`, `live:cookies` (Admin → Compliance, and `deploy/scripts/compliance-check.py`)

**The company's part** Decide whether "keep me signed in" fits (for health data, set Auth:RememberMe no longer than Auth:SessionMax); screens locked when people step away.

### IAM-03: Two-factor sign-in

People enrol an authenticator app and get recovery codes; an admin can reset a lost one. The company requires it of admins or of everyone, and a group of its members: who has none sets it up at their next sign-in and reaches nothing else until then, and cannot turn it off while it is required. Company sign-in brings the identity provider's own second factor: when one is required, the provider must say it was used (OIDC's amr or acr, SAML's authentication context), or the sign-in is refused.

**Serves**

- [ISO/IEC 27001:2022, Annex A](frameworks/iso27001.md): A.8.5 Secure authentication
- [SOC 2, Trust Services Criteria](frameworks/soc2.md): CC6.1 Logical access security software, infrastructure and architectures
- [HIPAA, 45 CFR Part 164 (Security Rule, Breach Notification, Privacy Rule)](frameworks/hipaa.md): 164.312(d) Person or entity authentication
- [The GDPR, Regulation (EU) 2016/679](frameworks/gdpr.md): Art.32 Security of processing

**Documented in** [authentication.md#what-protects-the-sign-in](../authentication.md#what-protects-the-sign-in)

**Proved by**

- `Llm.Tests/IdentityTests.Two_factor_sign_in_with_an_authenticator_and_with_a_recovery_code`
- `Llm.Tests/TwoFactorPolicyTests.Required_of_everyone_a_person_without_one_sets_it_up_first_reaches_nothing_else_and_cannot_turn_it_off`
- `Llm.Tests/TwoFactorPolicyTests.Required_of_admins_or_of_a_groups_members_it_reaches_only_them_and_the_compliance_check_says_who_has_none`
- `Llm.Tests/CompanySignInTests.A_second_factor_required_of_someone_must_come_from_the_provider`
- `Llm.Tests/SamlSignInTests.A_second_factor_required_of_someone_must_be_the_assertions_authentication_context`

**Watched by** `app:two-factor` (Admin → Compliance, and `deploy/scripts/compliance-check.py`)

**The company's part** Choose who must use it (Settings → Sign-in: admins at least; everyone for health data); require a second factor at the identity provider for company sign-in.

### IAM-04: Joiners and leavers follow the company's directory

People come from the company's directory (LDAP or Active Directory), OIDC or SAML with their groups and role; SCIM creates, changes and deactivates them, and a person who leaves is disabled with their keys stopped at once.

**Serves**

- [ISO/IEC 27001:2022, Annex A](frameworks/iso27001.md): A.5.16 Identity management; A.5.18 Access rights
- [SOC 2, Trust Services Criteria](frameworks/soc2.md): CC6.2 Registering and authorising new users; CC6.3 Role-based access, least privilege and segregation of duties
- [HIPAA, 45 CFR Part 164 (Security Rule, Breach Notification, Privacy Rule)](frameworks/hipaa.md): 164.308(a)(3)(ii)(C) Termination procedures (addressable); 164.308(a)(4)(ii)(C) Access establishment and modification (addressable)
- [The GDPR, Regulation (EU) 2016/679](frameworks/gdpr.md): Art.32 Security of processing

**Documented in** [authentication.md#the-company-directory-ldap--active-directory](../authentication.md#the-company-directory-ldap--active-directory), [authentication.md#scim-provisioning](../authentication.md#scim-provisioning), [authentication.md#company-sign-in-by-saml-20](../authentication.md#company-sign-in-by-saml-20)

**Proved by**

- `Llm.Tests/ScimTests.Scim_needs_its_token_which_is_shown_once_stored_as_a_hash_and_replaced_at_once`
- `Llm.Tests/ScimTests.A_person_deactivated_through_scim_is_disabled_here_and_their_keys_stop_at_once`
- `Llm.Tests/SamlSignInTests.Saml_sign_in_creates_the_person_with_their_groups_their_role_and_a_key`
- `Llm.Tests/CompanySignInTests.Company_sign_in_creates_the_person_with_their_groups_their_role_and_a_key`
- `Llm.Tests/LdapTests.The_sync_disables_people_who_leave_and_restores_them_when_they_return`

**The company's part** The joiner, mover and leaver process in the directory; local accounts kept to the few who need them.

### IAM-05: Least privilege, by roles and rules

What a person may do comes from roles (built in, or made by the company) held directly, through a group or by everyone, and from rules that give or take a feature; admin roles can be scoped to some groups. The checker says why a person may or may not do something.

**Serves**

- [ISO/IEC 27001:2022, Annex A](frameworks/iso27001.md): A.5.15 Access control; A.5.18 Access rights; A.8.2 Privileged access rights; A.8.3 Information access restriction
- [SOC 2, Trust Services Criteria](frameworks/soc2.md): CC6.1 Logical access security software, infrastructure and architectures; CC6.3 Role-based access, least privilege and segregation of duties
- [HIPAA, 45 CFR Part 164 (Security Rule, Breach Notification, Privacy Rule)](frameworks/hipaa.md): 164.308(a)(4)(ii)(B) Access authorisation (addressable); 164.312(a)(1) Access control
- [The GDPR, Regulation (EU) 2016/679](frameworks/gdpr.md): Art.25 Data protection by design and by default; Art.32 Security of processing

**Documented in** [admin.md#access-roles-and-rules](../admin.md#access-roles-and-rules)

**Proved by**

- `Llm.Tests/EndpointAccessTests.A_role_reaches_the_reads_of_its_permissions_and_no_others`
- `Llm.Tests/EndpointAccessTests.Each_built_in_role_holds_exactly_its_permissions`
- `Llm.Tests/AccessEndpointsTests.A_rule_takes_a_feature_from_a_group_and_the_checker_says_why`

**The company's part** Decide who holds which role, and review it.

### IAM-06: API keys are personal, revocable and checked

Each key belongs to one person and works only as them; an unknown, blocked or expired key is refused; rotating a key revokes the old one first, and disabling a person stops their keys.

**Serves**

- [ISO/IEC 27001:2022, Annex A](frameworks/iso27001.md): A.5.17 Authentication information; A.8.5 Secure authentication
- [SOC 2, Trust Services Criteria](frameworks/soc2.md): CC6.1 Logical access security software, infrastructure and architectures
- [HIPAA, 45 CFR Part 164 (Security Rule, Breach Notification, Privacy Rule)](frameworks/hipaa.md): 164.312(d) Person or entity authentication
- [The GDPR, Regulation (EU) 2016/679](frameworks/gdpr.md): Art.32 Security of processing

**Documented in** [authentication.md#as-a-machine-client-of-the-engine-api](../authentication.md#as-a-machine-client-of-the-engine-api)

**Proved by**

- `Llm.Tests/KeyCheckTests.An_unknown_blocked_or_expired_key_is_refused`
- `Llm.Tests/IdentityTests.Rotating_a_key_revokes_the_old_one_first`

**The company's part** People keep their keys out of shared files and repositories.

### IAM-07: Privileged access is reviewed

An access review lists every privileged role given to a person, a group or everyone, each member of a group that holds one, and the accounts nobody signed in to for long, for a reviewer to keep or revoke; revoking takes effect at once and is audited, and a closed review is kept for good. One starts by itself every quarter and the admins are told; accounts unused for long are disabled when the company says so.

**Serves**

- [ISO/IEC 27001:2022, Annex A](frameworks/iso27001.md): A.5.18 Access rights; A.8.2 Privileged access rights
- [SOC 2, Trust Services Criteria](frameworks/soc2.md): CC6.2 Registering and authorising new users; CC6.3 Role-based access, least privilege and segregation of duties
- [HIPAA, 45 CFR Part 164 (Security Rule, Breach Notification, Privacy Rule)](frameworks/hipaa.md): 164.308(a)(3)(ii)(B) Workforce clearance procedure (addressable); 164.308(a)(4)(ii)(C) Access establishment and modification (addressable)
- [The GDPR, Regulation (EU) 2016/679](frameworks/gdpr.md): Art.32(1)(d) Security of processing

**Documented in** [compliance/README.md#access-reviews](README.md#access-reviews)

**Proved by**

- `Llm.Tests/AccessReviewTests.A_review_lists_privileged_roles_their_groups_members_and_unused_accounts_and_revoking_takes_effect_at_once`
- `Llm.Tests/AccessReviewTests.The_schedule_starts_a_review_when_one_is_due_tells_the_admins_and_disables_unused_accounts_when_asked`

**Watched by** `app:access-review` (Admin → Compliance, and `deploy/scripts/compliance-check.py`)

**The company's part** Name the reviewers (the Compliance officer role), finish each review by its due date, and review the directory's and SCIM's groups at their source as the review says.

## Records (LOG)

### LOG-01: Who did what is recorded

The audit log keeps every sign-in, every change to people, groups, roles, settings and the index, every export and every tool an agent calls as a person, with who, whom, when and from where; never content. Reads of another person's data are audited as changes are: an admin's export of them, and their shared chat opened by someone else.

**Serves**

- [ISO/IEC 27001:2022, Annex A](frameworks/iso27001.md): A.8.15 Logging
- [SOC 2, Trust Services Criteria](frameworks/soc2.md): CC7.2 Monitoring system components for anomalies
- [HIPAA, 45 CFR Part 164 (Security Rule, Breach Notification, Privacy Rule)](frameworks/hipaa.md): 164.308(a)(1)(ii)(D) Information system activity review (required); 164.312(b) Audit controls
- [The GDPR, Regulation (EU) 2016/679](frameworks/gdpr.md): Art.5(2) Principles of processing personal data; Art.30 Records of processing activities

**Documented in** [admin.md#admin](../admin.md#admin), [settings.md#every-change-is-audited](../settings.md#every-change-is-audited)

**Proved by**

- `Llm.Tests/IdentityTests.Everything_is_in_the_audit_log`
- `Llm.Tests/ArenaMcpTests.An_agent_signs_in_with_the_persons_key_gets_their_chat_tools_and_each_call_is_audited`
- `Llm.Tests/ArenaMcpTests.At_its_code_address_an_agent_gets_search_tools_and_run_code_whose_program_calls_the_tools_as_the_person`
- `Llm.Tests/ShareTests.A_shared_chat_opens_for_a_colleague_and_not_for_someone_outside_the_group_and_revoking_stops_it`

**Watched by** `app:audit-log` (Admin → Compliance, and `deploy/scripts/compliance-check.py`)

**The company's part** Someone reviews the audit log on a schedule.

### LOG-02: Logs are kept, and kept for a set time

Every service's logs go to Loki, searchable by container, level and text; how long Loki keeps them is read from its configuration and shown.

**Serves**

- [ISO/IEC 27001:2022, Annex A](frameworks/iso27001.md): A.8.15 Logging
- [SOC 2, Trust Services Criteria](frameworks/soc2.md): CC7.2 Monitoring system components for anomalies
- [HIPAA, 45 CFR Part 164 (Security Rule, Breach Notification, Privacy Rule)](frameworks/hipaa.md): 164.312(b) Audit controls
- [The GDPR, Regulation (EU) 2016/679](frameworks/gdpr.md): Art.5(1)(e) Principles of processing personal data

**Documented in** [admin.md#storage](../admin.md#storage)

**Proved by**

- `Llm.Tests/StorageTests.Lokis_retention_is_read_from_its_configuration`
- `Llm.Tests/RecordRetentionTests.Loki_is_set_to_keep_logs_for_ninety_days_in_compose_and_in_the_chart`

**Watched by** `app:log-retention` (Admin → Compliance, and `deploy/scripts/compliance-check.py`)

**The company's part** Decide how long logs are kept.

### LOG-03: Nobody changes the audit log unseen

Each entry is sealed onto the one before it with SHA-256, under one lock for every writer, so an entry changed, removed or slipped in breaks the chain from there; every day the whole chain is walked and its newest entry anchored in a volume of its own and the log, which shows an end rewritten. A break tells every admin, is in the audit log, and turns the check red; the evidence carries the chain's walk and its anchors.

**Serves**

- [ISO/IEC 27001:2022, Annex A](frameworks/iso27001.md): A.5.28 Collection of evidence; A.5.33 Protection of records; A.8.15 Logging
- [SOC 2, Trust Services Criteria](frameworks/soc2.md): CC7.2 Monitoring system components for anomalies; CC7.3 Evaluating security events
- [HIPAA, 45 CFR Part 164 (Security Rule, Breach Notification, Privacy Rule)](frameworks/hipaa.md): 164.312(b) Audit controls; 164.312(c)(1) Integrity; 164.312(c)(2) Mechanism to authenticate electronic PHI (addressable)
- [The GDPR, Regulation (EU) 2016/679](frameworks/gdpr.md): Art.5(1)(f) Principles of processing personal data; Art.5(2) Principles of processing personal data; Art.32(1)(b) Security of processing

**Documented in** [compliance/README.md#the-audit-log-as-a-chain](README.md#the-audit-log-as-a-chain)

**Proved by**

- `Llm.Tests/AuditChainTests.An_entry_changed_removed_or_slipped_in_breaks_the_chain_there_and_every_admin_is_told`
- `Llm.Tests/AuditChainTests.An_anchor_kept_apart_shows_the_end_of_the_chain_rewritten_and_entries_from_before_the_chain_are_sealed`
- `Llm.Tests/AuditChainTests.Many_writers_at_once_still_make_one_chain`

**Watched by** `app:audit-chain` (Admin → Compliance, and `deploy/scripts/compliance-check.py`)

**The company's part** Keep the audit volume's folder (DATA_DIR/audit) where the database's administrators cannot write, and back it up apart; restrict direct database access and keep its own logs.

### LOG-04: The audit log is sent on to the company's SIEM

Every entry, as it is written, in order and each once, by syslog (RFC 5424 over UDP, TCP or TLS) and to a webhook signed with HMAC-SHA256, with its hash so the SIEM can walk the chain too. How far it got is kept in the database, so a restart or another replica goes on from there; a collector that does not answer is tried again and nothing is skipped.

**Serves**

- [ISO/IEC 27001:2022, Annex A](frameworks/iso27001.md): A.8.15 Logging; A.8.16 Monitoring activities
- [SOC 2, Trust Services Criteria](frameworks/soc2.md): CC7.2 Monitoring system components for anomalies
- [HIPAA, 45 CFR Part 164 (Security Rule, Breach Notification, Privacy Rule)](frameworks/hipaa.md): 164.308(a)(1)(ii)(D) Information system activity review (required); 164.312(b) Audit controls
- [The GDPR, Regulation (EU) 2016/679](frameworks/gdpr.md): Art.32(1)(b) Security of processing

**Documented in** [compliance/README.md#sending-it-to-the-siem](README.md#sending-it-to-the-siem)

**Proved by**

- `Llm.Tests/AuditForwardTests.Each_entry_is_posted_once_in_order_signed_and_a_webhook_that_fails_gets_them_all_later`
- `Llm.Tests/AuditForwardTests.Syslog_gets_each_entry_octet_counted_as_RFC_5424_with_failures_as_warnings`

**Watched by** `app:audit-forwarding` (Admin → Compliance, and `deploy/scripts/compliance-check.py`)

**The company's part** Run the SIEM, keep what it receives for as long as the frameworks ask (six years under HIPAA), and watch its alerts.

## Monitoring (MON)

### MON-01: The service is watched and people are alerted

Prometheus and Alertmanager watch the stack; an alert that starts firing reaches every admin's bell, email and the alerts webhook once; the certificate warns 30 days before it expires and is critical 7 days before.

**Serves**

- [ISO/IEC 27001:2022, Annex A](frameworks/iso27001.md): A.8.16 Monitoring activities
- [SOC 2, Trust Services Criteria](frameworks/soc2.md): CC7.2 Monitoring system components for anomalies; CC7.3 Evaluating security events
- [HIPAA, 45 CFR Part 164 (Security Rule, Breach Notification, Privacy Rule)](frameworks/hipaa.md): 164.308(a)(1)(ii)(D) Information system activity review (required); 164.308(a)(6)(ii) Response and reporting (required)
- [The GDPR, Regulation (EU) 2016/679](frameworks/gdpr.md): Art.32(1)(b) Security of processing

**Documented in** [admin.md#admin](../admin.md#admin)

**Proved by**

- `Llm.Tests/ObserveTests.Alerts_show_what_fires_and_every_rule_and_survive_either_source_down`
- `Llm.Tests/NotificationsTests.A_system_alert_that_starts_firing_reaches_every_admin_once`
- `Llm.Tests/OperationsTests.The_certificate_alerts_warn_thirty_days_before_and_are_critical_seven_days_before`

**Watched by** `app:certificate` (Admin → Compliance, and `deploy/scripts/compliance-check.py`)

**The company's part** Someone takes the alerts, around the clock where the company needs it.

### MON-02: Security events raise alerts, and incidents are handled in time

Many failed sign-ins, an administrative role given, many exports by one person, a failed or late backup raise a security alert once, to every admin; the incident register takes an incident from an alert or by hand, runs the GDPR's 72 hours and HIPAA's 60 days from when it was found, keeps every step with who and when, and is closed only with what was learned (and, with personal data, the authority question answered).

**Serves**

- [ISO/IEC 27001:2022, Annex A](frameworks/iso27001.md): A.5.24 Information security incident management planning and preparation; A.5.25 Assessment and decision on information security events; A.5.26 Response to information security incidents; A.5.27 Learning from information security incidents; A.8.16 Monitoring activities
- [SOC 2, Trust Services Criteria](frameworks/soc2.md): CC7.3 Evaluating security events; CC7.4 Responding to security incidents; CC7.5 Recovering from security incidents
- [HIPAA, 45 CFR Part 164 (Security Rule, Breach Notification, Privacy Rule)](frameworks/hipaa.md): 164.308(a)(6)(ii) Response and reporting (required); 164.404 Notification to individuals of a breach; 164.408 Notification to the Secretary of a breach
- [The GDPR, Regulation (EU) 2016/679](frameworks/gdpr.md): Art.33 Notifying the supervisory authority of a breach; Art.34 Telling the people affected of a breach

**Documented in** [compliance/README.md#security-alerts-and-incidents](README.md#security-alerts-and-incidents)

**Proved by**

- `Llm.Tests/IncidentTests.Failed_sign_ins_an_admin_made_many_exports_and_a_failed_backup_each_raise_one_alert_to_every_admin`
- `Llm.Tests/IncidentTests.An_incident_runs_from_its_alert_to_closure_with_the_GDPRs_72_hours_and_HIPAAs_60_days`

**Watched by** `app:incidents` (Admin → Compliance, and `deploy/scripts/compliance-check.py`)

**The company's part** Name who answers the alerts and runs incidents, know which supervisory authority to tell and how, and keep the incident-response runbook.

## Data (DAT)

### DAT-01: Chats and files are deleted when their time is up

The company sets how long chats are kept, a group can set a shorter time, and every hour a job deletes chats past it with their files, and files nobody uses that are as old; each deletion is audited with counts.

**Serves**

- [ISO/IEC 27001:2022, Annex A](frameworks/iso27001.md): A.5.33 Protection of records; A.8.10 Information deletion
- [SOC 2, Trust Services Criteria](frameworks/soc2.md): C1.2 Disposing of confidential information; P4.2 Retention of personal information; P4.3 Disposal of personal information
- [The GDPR, Regulation (EU) 2016/679](frameworks/gdpr.md): Art.5(1)(e) Principles of processing personal data; Art.17 Right to erasure

**Documented in** [admin.md#retention-legal-hold-and-exports](../admin.md#retention-legal-hold-and-exports)

**Proved by**

- `Llm.Tests/RetentionTests.A_chat_past_its_groups_retention_is_deleted_with_its_files_and_one_under_legal_hold_is_not`

**Watched by** `app:chat-retention` (Admin → Compliance, and `deploy/scripts/compliance-check.py`)

**The company's part** Decide the periods, and write them in the retention policy.

### DAT-02: Legal hold

A person on legal hold keeps everything: retention passes them by, and a chat they delete is hidden from them, not erased, until the hold ends.

**Serves**

- [ISO/IEC 27001:2022, Annex A](frameworks/iso27001.md): A.5.28 Collection of evidence; A.5.33 Protection of records
- [The GDPR, Regulation (EU) 2016/679](frameworks/gdpr.md): Art.17(3)(e) Right to erasure

**Documented in** [admin.md#retention-legal-hold-and-exports](../admin.md#retention-legal-hold-and-exports)

**Proved by**

- `Llm.Tests/RetentionTests.A_person_on_legal_hold_who_deletes_a_chat_only_hides_it_until_the_hold_ends`

**The company's part** Legal decides holds and when they end.

### DAT-03: A person's data, exported

People download everything of theirs from Your account; an admin exports a person's data for eDiscovery, audited: their profile, chats as JSON and Markdown, and files.

**Serves**

- [ISO/IEC 27001:2022, Annex A](frameworks/iso27001.md): A.5.34 Privacy and protection of PII
- [SOC 2, Trust Services Criteria](frameworks/soc2.md): P5.1 People's access to their personal information
- [HIPAA, 45 CFR Part 164 (Security Rule, Breach Notification, Privacy Rule)](frameworks/hipaa.md): 164.524 Access of individuals to their health information
- [The GDPR, Regulation (EU) 2016/679](frameworks/gdpr.md): Art.15 Right of access; Art.20 Right to data portability

**Documented in** [admin.md#retention-legal-hold-and-exports](../admin.md#retention-legal-hold-and-exports)

**Proved by**

- `Llm.Tests/RetentionTests.A_persons_export_holds_their_profile_chats_as_json_and_markdown_and_their_files`

**The company's part** Answer a person's request within the time the law gives (a month under the GDPR).

### DAT-04: Personal data and secrets kept from the model

Safeguards mask personal data before a question reaches the model while the chat keeps it as written; answers are masked or withheld by rule; what tools send and bring back is held by their rules; a pasted secret or private key is refused and audited by its kind only.

**Serves**

- [ISO/IEC 27001:2022, Annex A](frameworks/iso27001.md): A.8.11 Data masking; A.8.12 Data leakage prevention
- [SOC 2, Trust Services Criteria](frameworks/soc2.md): C1.1 Identifying and keeping confidential information; CC6.7 Restricting the transmission, movement and removal of information
- [HIPAA, 45 CFR Part 164 (Security Rule, Breach Notification, Privacy Rule)](frameworks/hipaa.md): 164.502(b) Minimum necessary
- [The GDPR, Regulation (EU) 2016/679](frameworks/gdpr.md): Art.25 Data protection by design and by default; Art.32(1)(a) Security of processing

**Documented in** [admin.md#safeguards](../admin.md#safeguards), [chat.md#safeguards](../chat.md#safeguards)

**Proved by**

- `Llm.Tests/SafeguardsTests.Personal_data_reaches_the_model_masked_and_the_chat_keeps_it_as_written`
- `Llm.Tests/SafeguardRulesTests.Answers_are_masked_or_withheld_and_what_tools_send_and_bring_back_is_held_by_their_rules`
- `Llm.Tests/SecretScanningTests.A_pasted_private_key_is_refused_in_the_chat_and_through_the_API_and_audited_by_its_kind_only`

**The company's part** Decide which data may reach which model, and which models are outside the company.

### DAT-05: Secrets and people's files are encrypted at rest

The signing keys, settings' secrets (directory, SSO, connectors) and tokens are stored encrypted with APP_KEY; a changed key fails loudly instead of resetting keys; secrets are never sent back to a browser. People's files (their bytes, a video's sound, a document's drawn pages) and deck templates are sealed with AES-256-GCM under a key from APP_KEY before they reach the database, and a job seals those stored before. The disk under the stack's data is checked for encryption from the host.

**Serves**

- [ISO/IEC 27001:2022, Annex A](frameworks/iso27001.md): A.8.24 Use of cryptography
- [SOC 2, Trust Services Criteria](frameworks/soc2.md): CC6.1 Logical access security software, infrastructure and architectures
- [HIPAA, 45 CFR Part 164 (Security Rule, Breach Notification, Privacy Rule)](frameworks/hipaa.md): 164.312(a)(2)(iv) Encryption and decryption (addressable)
- [The GDPR, Regulation (EU) 2016/679](frameworks/gdpr.md): Art.32(1)(a) Security of processing

**Documented in** [authentication.md#what-protects-the-sign-in](../authentication.md#what-protects-the-sign-in)

**Proved by**

- `Llm.Tests/KeyRingTests.With_a_data_key_the_key_ring_is_encrypted_and_survives_a_restart`
- `Llm.Tests/KeyRingTests.A_changed_data_key_fails_loudly_instead_of_silently_resetting_keys`
- `Llm.Tests/SettingsTests.A_secret_is_stored_encrypted_used_in_plain_and_never_sent_back`
- `Llm.Tests/KnowledgeConnectorTests.Test_connection_says_what_it_reached_and_secrets_are_stored_encrypted_and_never_sent_back`
- `Llm.Tests/FileSealingTests.A_file_is_stored_encrypted_read_as_it_was_and_those_stored_before_are_sealed_by_the_job`
- `Llm.Tests/FileSealingTests.Without_APP_KEY_files_are_stored_as_they_are_and_the_check_says_so`

**Watched by** `app:data-key`, `app:files-sealed`, `live:disk` (Admin → Compliance, and `deploy/scripts/compliance-check.py`)

**The company's part** Keep APP_KEY with the company's secrets, apart from the backups; encrypt the hosts' disks.

### DAT-06: People's requests about their data are answered

A person asks from Your account (or an admin takes the request in for them) for a copy, their data to take elsewhere, a correction, restriction, objection or erasure; each has its month, the admins hear of it, and every step is audited. Restriction and objection stop everything that learns from their data; erasure removes everything of theirs, takes them out of what others keep, refuses while a legal hold lasts, and gives a certificate of what is kept and why and when backups let go of the rest.

**Serves**

- [ISO/IEC 27001:2022, Annex A](frameworks/iso27001.md): A.5.34 Privacy and protection of PII
- [SOC 2, Trust Services Criteria](frameworks/soc2.md): P5.1 People's access to their personal information; P5.2 Correction of personal information; P4.3 Disposal of personal information
- [HIPAA, 45 CFR Part 164 (Security Rule, Breach Notification, Privacy Rule)](frameworks/hipaa.md): 164.524 Access of individuals to their health information; 164.526 Amendment of health information
- [The GDPR, Regulation (EU) 2016/679](frameworks/gdpr.md): Art.12(3) Transparent information and how the rights are exercised; Art.15 Right of access; Art.16 Right to rectification; Art.17 Right to erasure; Art.18 Right to restriction of processing; Art.20 Right to data portability; Art.21 Right to object

**Documented in** [compliance/README.md#peoples-data-requests](README.md#peoples-data-requests)

**Proved by**

- `Llm.Tests/DataRequestTests.A_person_asks_the_admins_hear_it_is_due_in_a_month_and_answering_tells_them`
- `Llm.Tests/DataRequestTests.Restriction_stops_everything_that_learns_from_their_data_until_it_is_lifted`
- `Llm.Tests/DataRequestTests.Erasure_removes_everything_of_theirs_takes_them_out_of_what_others_keep_and_certifies_what_is_kept`

**Watched by** `app:data-requests` (Admin → Compliance, and `deploy/scripts/compliance-check.py`)

**The company's part** Name who answers them (a data protection officer, with the privacy.manage permission), verify who is asking, and tell people where to complain.

### DAT-07: Records are kept for set times, and no longer

The audit log six years unless set (only ever from the start of its chain), the gateway's request log, the record of tool calls and the bell each for a period set under Settings → Data retention, removed by the hourly sweep and audited with counts; the services' logs 90 days (Loki's compactor) and metrics 30 days (Prometheus). Admin → Compliance lists how long each kind of data and record is kept, and what removes it.

**Serves**

- [ISO/IEC 27001:2022, Annex A](frameworks/iso27001.md): A.5.33 Protection of records; A.8.10 Information deletion; A.8.15 Logging
- [SOC 2, Trust Services Criteria](frameworks/soc2.md): C1.2 Disposing of confidential information; P4.2 Retention of personal information; P4.3 Disposal of personal information
- [HIPAA, 45 CFR Part 164 (Security Rule, Breach Notification, Privacy Rule)](frameworks/hipaa.md): 164.316(b)(2)(i) Time limit of six years (required)
- [The GDPR, Regulation (EU) 2016/679](frameworks/gdpr.md): Art.5(1)(e) Principles of processing personal data

**Documented in** [compliance/README.md#how-long-things-are-kept](README.md#how-long-things-are-kept)

**Proved by**

- `Llm.Tests/RecordRetentionTests.Records_past_their_time_go_the_audit_log_only_from_the_start_of_its_chain_and_the_check_says_how_long_each_is_kept`

**Watched by** `app:records-retention`, `app:log-retention` (Admin → Compliance, and `deploy/scripts/compliance-check.py`)

**The company's part** Decide the periods, write them in the retention policy, and keep backups as long as it says.

### DAT-08: Personal and health data kept in bounds

The safeguards find e-mail addresses, phone, card, IBAN and social security numbers, and health and identity data: dates of birth and medical record numbers by the words that name them, NHS numbers and Iranian national codes with their check digits right, and US Medicare numbers by their shape; a rule warns, masks or blocks them before a question reaches a model, stricter for models on other providers (the template "Health and identity data to cloud models" blocks them there). The app's log lines carry no personal data: it is masked before a line is written (IP addresses stay, as security needs them); answers' traces hold times, tokens and tool names only, and the gateway's request log no prompts.

**Serves**

- [ISO/IEC 27001:2022, Annex A](frameworks/iso27001.md): A.5.34 Privacy and protection of PII; A.8.11 Data masking; A.8.12 Data leakage prevention
- [SOC 2, Trust Services Criteria](frameworks/soc2.md): C1.1 Identifying and keeping confidential information; CC6.7 Restricting the transmission, movement and removal of information; P4.1 Use limited to the purposes identified
- [HIPAA, 45 CFR Part 164 (Security Rule, Breach Notification, Privacy Rule)](frameworks/hipaa.md): 164.502(b) Minimum necessary; 164.514(b) De-identification
- [The GDPR, Regulation (EU) 2016/679](frameworks/gdpr.md): Art.5(1)(c) Principles of processing personal data; Art.9 Special categories of personal data; Art.25 Data protection by design and by default; Art.32(1)(a) Security of processing

**Documented in** [compliance/README.md#personal-and-health-data](README.md#personal-and-health-data)

**Proved by**

- `Llm.Tests/PersonalDataTests.Health_and_identity_data_are_found_by_what_names_them_and_their_check_digits`
- `Llm.Tests/PersonalDataTests.A_wrong_check_digit_or_a_number_nothing_names_is_not_taken_for_one`
- `Llm.Tests/PersonalDataTests.The_apps_log_lines_carry_no_personal_data_but_keep_the_addresses_security_needs`
- `Llm.Tests/PersonalDataTests.The_compliance_check_wants_a_rule_keeping_health_data_from_cloud_models_and_the_templates_give_one`

**Watched by** `app:personal-data` (Admin → Compliance, and `deploy/scripts/compliance-check.py`)

**The company's part** Decide which models may see which data (a HIPAA BAA, a GDPR processing agreement with any provider outside the company), add the templates, and add the company's own identifiers as words or patterns.

## Network (NET)

### NET-01: Encrypted in transit, one way in

Only Traefik's ports 80 and 443 are published, 80 only to redirect; TLS 1.2 and 1.3 only, and for 1.2 only ciphers with forward secrecy and authenticated encryption; HSTS for a year with a trusted certificate (HSTS_SECONDS); the services talk on Docker's network; every page carries a strict content security policy and the security headers; a state change needs a header another site cannot send; hosts without a rule are refused.

**Serves**

- [ISO/IEC 27001:2022, Annex A](frameworks/iso27001.md): A.8.20 Networks security; A.8.24 Use of cryptography; A.8.26 Application security requirements
- [SOC 2, Trust Services Criteria](frameworks/soc2.md): CC6.6 Security against threats from outside the system's boundaries; CC6.7 Restricting the transmission, movement and removal of information
- [HIPAA, 45 CFR Part 164 (Security Rule, Breach Notification, Privacy Rule)](frameworks/hipaa.md): 164.312(e)(1) Transmission security; 164.312(e)(2)(ii) Encryption (addressable)
- [The GDPR, Regulation (EU) 2016/679](frameworks/gdpr.md): Art.32(1)(a) Security of processing

**Documented in** [authentication.md#what-is-deliberately-not-behind-sign-in](../authentication.md#what-is-deliberately-not-behind-sign-in), [deployment.md#certificates](../deployment.md#certificates)

**Proved by**

- `Llm.Tests/FoundationTests.Security_headers_are_set`
- `Llm.Tests/IdentityTests.State_changes_without_the_csrf_header_are_refused`
- `Llm.Tests/ForwardAuthTests.Hosts_without_a_rule_are_denied_even_to_admins`
- `Llm.Tests/FileSealingTests.With_HSTS_set_the_app_sends_it_as_Traefik_does_and_without_it_clears_any_kept_before`

**Watched by** `live:tls`, `live:headers`, `live:csrf` (Admin → Compliance, and `deploy/scripts/compliance-check.py`)

**The company's part** A certificate from the company's CA or Let's Encrypt; the network around the hosts.

## Operations (OPS)

### OPS-01: Backups, restored and proven

scripts/backup.sh backs up the databases, the settings and the files, with a second copy elsewhere, each file encrypted (AES-256, PBKDF2) with a key kept apart from the backups (BACKUP_KEY_FILE), refused inside them; point-in-time recovery archives the WAL; the drill restores into a throwaway stack, never the live one, and checks that every person, chat and setting came back.

**Serves**

- [ISO/IEC 27001:2022, Annex A](frameworks/iso27001.md): A.5.30 ICT readiness for business continuity; A.8.13 Information backup
- [SOC 2, Trust Services Criteria](frameworks/soc2.md): A1.2 Environmental protections, software, backups and recovery infrastructure; A1.3 Testing the recovery plan; CC9.1 Reducing the risks of business disruption
- [HIPAA, 45 CFR Part 164 (Security Rule, Breach Notification, Privacy Rule)](frameworks/hipaa.md): 164.308(a)(7)(ii)(A) Data backup plan (required); 164.308(a)(7)(ii)(B) Disaster recovery plan (required); 164.308(a)(7)(ii)(D) Testing and revision procedures (addressable)
- [The GDPR, Regulation (EU) 2016/679](frameworks/gdpr.md): Art.32(1)(c) Security of processing; Art.32(1)(d) Security of processing

**Documented in** [deployment.md#backups-and-restore](../deployment.md#backups-and-restore), [deployment.md#point-in-time-recovery-pitr](../deployment.md#point-in-time-recovery-pitr)

**Proved by**

- `deploy/test_backup.py::test_By_default_it_keeps_BACKUP_KEEP_here_and_in_the_second_copy`
- `deploy/test_backup.py::test_Every_file_is_encrypted_the_MANIFEST_names_the_key_without_it_and_it_opens_only_with_that_key`
- `deploy/test_backup.py::test_A_key_inside_deploy_or_the_backups_is_refused_before_anything_is_taken`
- `deploy/test_pitr.py::test_pitr_yml_archives_wal_to_the_backup_disk`
- `deploy/test_pitr.py::test_the_drill_restores_into_a_scratch_container_never_the_stacks`
- `deploy/test_recovery.py::test_The_live_project_is_refused_before_anything_runs`
- `deploy/test_recovery_check.py::test_A_person_lost_in_the_restore_fails_it`

**Watched by** `app:backups` (Admin → Compliance, and `deploy/scripts/compliance-check.py`)

**The company's part** Schedule the backups, keep the second copy off the host, keep the backups' key (BACKUP_KEY_FILE) apart from them with a copy in the company's vault, run the drill on a schedule, and encrypt pgBackRest's repository (its repo1-cipher-type and repo1-cipher-pass) when using it.

### OPS-02: Code the model writes runs apart

Python, documents and a plugin's own server run in the sandbox: no network, a read-only root, its own limits, nothing that outlives a run.

**Serves**

- [ISO/IEC 27001:2022, Annex A](frameworks/iso27001.md): A.8.22 Segregation of networks; A.8.7 Protection against malware
- [SOC 2, Trust Services Criteria](frameworks/soc2.md): CC6.8 Preventing or detecting unauthorised or malicious software
- [HIPAA, 45 CFR Part 164 (Security Rule, Breach Notification, Privacy Rule)](frameworks/hipaa.md): 164.308(a)(5)(ii)(B) Protection from malicious software (addressable)
- [The GDPR, Regulation (EU) 2016/679](frameworks/gdpr.md): Art.32(1)(b) Security of processing

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

- [ISO/IEC 27001:2022, Annex A](frameworks/iso27001.md): A.5.36 Compliance with policies, rules and standards for information security; A.8.29 Security testing in development and acceptance; A.8.32 Change management
- [SOC 2, Trust Services Criteria](frameworks/soc2.md): CC4.1 Ongoing and separate evaluations; CC8.1 Authorising, designing, testing, approving and putting changes in place
- [HIPAA, 45 CFR Part 164 (Security Rule, Breach Notification, Privacy Rule)](frameworks/hipaa.md): 164.308(a)(8) Evaluation
- [The GDPR, Regulation (EU) 2016/679](frameworks/gdpr.md): Art.5(2) Principles of processing personal data; Art.25 Data protection by design and by default

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

- [ISO/IEC 27001:2022, Annex A](frameworks/iso27001.md): A.5.10 Acceptable use of information and other associated assets; A.6.3 Information security awareness, education and training
- [SOC 2, Trust Services Criteria](frameworks/soc2.md): CC2.3 Communication with external parties; P1.1 Notice of privacy practices
- [HIPAA, 45 CFR Part 164 (Security Rule, Breach Notification, Privacy Rule)](frameworks/hipaa.md): 164.308(a)(5)(i) Security awareness and training
- [The GDPR, Regulation (EU) 2016/679](frameworks/gdpr.md): Art.12 Transparent information and how the rights are exercised; Art.13 Information when data is collected from the person

**Documented in** [compliance/README.md#the-sign-in-notice](README.md#the-sign-in-notice)

**Proved by**

- `Llm.Tests/NoticeTests.Everyone_accepts_the_current_version_before_anything_else_and_a_new_version_again`
- `Llm.Tests/NoticeTests.Without_a_notice_nothing_waits_and_the_check_asks_for_one`

**Watched by** `app:notice` (Admin → Compliance, and `deploy/scripts/compliance-check.py`)

**The company's part** Write the notice and the policies it points to, keep them current, and set a new version when they change in substance.

### GOV-03: What a release ships is listed and its known vulnerabilities gate it

Each release has a software bill of materials (CycloneDX) of every .NET, npm and Python package and base image it ships, checked against OSV's advisories; a critical vulnerability fails the release unless it is accepted in writing, with why and until when, and an acceptance past its date counts no more.

**Serves**

- [ISO/IEC 27001:2022, Annex A](frameworks/iso27001.md): A.5.21 Managing information security in the ICT supply chain; A.8.8 Management of technical vulnerabilities; A.8.28 Secure coding
- [SOC 2, Trust Services Criteria](frameworks/soc2.md): CC7.1 Detecting configuration changes and new vulnerabilities; CC8.1 Authorising, designing, testing, approving and putting changes in place; CC9.2 Risks from vendors and business partners
- [HIPAA, 45 CFR Part 164 (Security Rule, Breach Notification, Privacy Rule)](frameworks/hipaa.md): 164.308(a)(1)(ii)(A) Risk analysis (required); 164.308(a)(1)(ii)(B) Risk management (required)
- [The GDPR, Regulation (EU) 2016/679](frameworks/gdpr.md): Art.32(1)(d) Security of processing

**Documented in** [compliance/README.md#supply-chain](README.md#supply-chain), [development.md#releasing](../development.md#releasing)

**Proved by**

- `deploy/test_release_scan.py::test_What_ships_is_listed_and_the_build_tools_are_not`
- `deploy/test_release_scan.py::test_A_critical_finding_fails_the_release_and_is_in_the_SBOM`
- `deploy/test_release_scan.py::test_Accepted_with_a_date_not_past_it_ships_and_high_ones_never_stop_it`
- `deploy/test_release_scan.py::test_OSV_out_of_reach_fails_it_too`

**The company's part** The release manager runs the scan and attaches the SBOM to the release; the security officer reviews each acceptance before its date and the high findings each release.
