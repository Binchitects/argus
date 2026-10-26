# Information security policy

> A template. Replace each [bracket], remove what does not apply to you,
> approve it and set its review date. Where Arena does part of a rule, the
> page or setting is named; [the controls](../controls.md) say what proves it.

| | |
|---|---|
| Owner | [Information security officer] |
| Approved by | [Name, role, date] |
| Applies to | everyone who uses or runs Arena at [Company], and the systems it runs on |
| Review | every year, after a significant change, and after an incident |

## Purpose

[Company] protects the confidentiality, integrity and availability of the
information it handles in Arena: people's conversations and files, company
knowledge, personal data and, where it applies, health information.

## Roles

- **Management** approves this policy and the risks it accepts, gives the
  resources, and reviews the information security programme every year.
- **The information security officer** ([name]) owns the policies, the risk
  register, the incident register and the access reviews. In Arena they hold
  the **Compliance officer** role (Admin → Access).
- **Admins** run Arena: settings, people, models, backups. Admin roles are
  given to as few people as the work needs, and reviewed.
- **Everyone** follows the [acceptable use policy](acceptable-use.md) and
  reports what looks wrong (an [incident](../incident-response.md)).

## Rules

1. Each system holding [Company]'s information has an owner, is in the asset
   inventory ([risk management](risk-management.md)), and is protected as the
   risk to it requires.
2. Access is given by role and need, and reviewed
   ([access control](access-control.md)).
3. What happens is recorded. Arena keeps an audit log of who did what (Admin →
   Audit), chained so a change to it shows, and kept six years; it sends it on
   to [the SIEM: name] (Settings → Audit log). Logs are kept [90 days].
4. The service is watched. Arena's alerts reach [the on-call: who] (Settings →
   Notifications); security alerts reach every admin.
5. Malware: the hosts run [endpoint protection: name]; code a model writes,
   and the conversion of sound and video people upload, run only in Arena's
   sandbox, which has no network.
6. Networks: only ports 80 and 443 of the Arena host are reachable, and only
   from [networks]. Its clocks follow [NTP server]. Web filtering for people's
   own devices is [how].
7. Changes follow the [change management policy](change-management.md).
8. Information is protected by its class ([data protection](data-protection.md))
   and with cryptography as the [cryptography policy](cryptography.md) says.
9. [Company] knows the laws and contracts that apply to it: [the GDPR, HIPAA
   as a covered entity / business associate, contracts with customers, ...],
   and keeps the licences of the software and models it uses.
10. Compliance is checked: Admin → Compliance every [month] by the information
    security officer; an independent review every [year] by [internal audit /
    an outside auditor].

## Records

The approved policies, the risk register, Admin → Compliance's evidence (one
zip with the controls, the checks, the attestations, the access reviews, the
notice's acceptances, the incidents and the audit log's chain), management's
reviews. Kept six years.

## Exceptions

Only in writing, approved by [the information security officer], with a reason
and an end date, in the risk register.
