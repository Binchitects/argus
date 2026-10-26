# Access control policy

> A template. Replace each [bracket], remove what does not apply to you,
> approve it and set its review date. Where Arena does part of a rule, the
> page or setting is named.

| | |
|---|---|
| Owner | [Information security officer] |
| Approved by | [Name, role, date] |
| Review | every year |

## Rules

1. **Everyone has their own account.** No shared accounts. People come from
   [the company's directory: LDAP, Active Directory, OIDC, SAML, SCIM]
   (Settings → Company sign-in, Company directory), so joiners get an account
   and leavers lose it with the directory. Accounts made by hand are for [cases], approved by
   [whom].
2. **Passwords** are strong (Arena refuses weak ones: a zxcvbn score of 3 or
   more) and never shared. Arena locks out guessing.
3. **Two-factor** is required for [everyone / admins and the groups that see
   health data] (Settings → Sign-in and sessions, or a group's policies).
   Company sign-in must itself ask for a second factor (Settings → Company
   sign-in).
4. **Sessions** end after [1 hour] idle and [12 hours] at most (Arena's
   defaults); people lock their screens.
5. **Least privilege.** Access is by role (Admin → Access): the roles are given
   by [whom], to groups where possible, and only as the work needs. Admin roles
   are held by [at most N people]; nobody approves their own access. Company
   knowledge is read only by those its source allows.
6. **API keys** are personal, have an end date and limits, and are revoked when
   no longer needed (Arena revokes a leaver's with their account).
7. **Access reviews** every [90 days] (Settings → Compliance, "Review access
   every"): [the information security officer] confirms or removes each
   privileged access and each account unused for long, in Admin → Compliance →
   Access reviews, and the result is kept.
8. **Leavers and movers**: their access ends on their last day ([HR] tells
   [IT]); a mover's old access is removed, not kept.
9. **Emergency access**: [the break-glass account, sealed, its use audited and
   reviewed].
10. **Source code** of [Company]'s own is reached through [GitLab] by its
    groups; Argus reads it with a read-only token.
11. **Privileged programs** on the hosts (shells, `docker`) are used only by
    [IT], and their use is logged by [how].

## Records

The access reviews (in the evidence), the requests and approvals for roles,
the audit log. Kept six years.
