# GHSA-35jh-r3h4-6jhm: Command Injection in lodash

CVE-2021-23337 · severity high (CVSS:3.1/AV:N/AC:L/PR:H/UI:N/S:U/C:H/I:H/A:H) · CWE-77, CWE-94 · published 2021-05-06.

## Affected

| Ecosystem | Package | Versions | Fixed in |
| --- | --- | --- | --- |
| npm | `lodash` | < 4.17.21 | 4.17.21 |
| npm | `lodash-es` | < 4.17.21 | 4.17.21 |
| npm | `lodash.template` | <= 4.5.0 | none yet |
| npm | `lodash-template` | <= 1.0.0 | none yet |
| RubyGems | `lodash-rails` | < 4.17.21 | 4.17.21 |

## Details

`lodash` versions prior to 4.17.21 are vulnerable to Command Injection via the template function.

## References

- https://nvd.nist.gov/vuln/detail/CVE-2021-23337
- https://github.com/lodash/lodash/commit/3469357cff396a26c363f8c1b5a91dde28ba4b1c
- https://www.oracle.com/security-alerts/cpuoct2021.html
- https://www.oracle.com/security-alerts/cpujul2022.html
- https://www.oracle.com/security-alerts/cpujan2022.html
- https://www.oracle.com//security-alerts/cpujul2021.html
- https://snyk.io/vuln/SNYK-JS-LODASH-1040724
- https://snyk.io/vuln/SNYK-JAVA-ORGWEBJARSNPM-1074929
- https://snyk.io/vuln/SNYK-JAVA-ORGWEBJARSBOWERGITHUBLODASH-1074931
- https://snyk.io/vuln/SNYK-JAVA-ORGWEBJARSBOWER-1074928
- https://snyk.io/vuln/SNYK-JAVA-ORGWEBJARS-1074930
- https://snyk.io/vuln/SNYK-JAVA-ORGFUJIONWEBJARS-1074932

---
Source: the GitHub Advisory Database, CC BY 4.0. https://github.com/advisories/GHSA-35jh-r3h4-6jhm
