# Product evaluation: Common Criteria and FIPS 140-3

A product is evaluated, not only the company that runs it. A Common Criteria
evaluation (ISO/IEC 15408, under the CCRA) is made by an accredited laboratory
against a Security Target (ST), at an assurance level (EAL1 to EAL7) or
against a Protection Profile, and a national scheme certifies it. A FIPS 140-3
validation (NIST's CMVP) is of a cryptographic module. Arena has neither.
This page says what an evaluation of it would rest on, and what a company
that needs one does.

## Common Criteria

**The Target of Evaluation (TOE)**: Argus Arena's app, web and gateway, as a
release ships them, with the configuration its guidance gives. The hosts, the
container engine, Postgres and the models are its environment.

**The security problem**: people and their data, a model's answers and the
code it writes, in the company's network. Threats: an outsider signing in as
someone, an insider reaching data they should not, a model leaking data or
running code out of bounds, records changed after the fact.

**The security functions**, and the Part 2 families they answer (each with the
control that provides it, [the controls](controls.md)):

| Function | Families | Arena's controls |
|---|---|---|
| Identification and authentication | FIA_UID, FIA_UAU, FIA_AFL, FIA_SOS | IAM-01, IAM-03, IAM-06 |
| Sessions | FTA_SSL, FTA_TAB | IAM-02, GOV-02 |
| Access control | FDP_ACC, FDP_ACF, FMT_SMR | IAM-05 |
| Audit | FAU_GEN, FAU_STG, FAU_SAR | LOG-01, LOG-02, LOG-03, LOG-04 |
| Data protection | FDP_IFC, FDP_IFF, FDP_SDC, FCS_CKM, FCS_COP | DAT-04, DAT-05, DAT-08 |
| Trusted channels | FTP_ITC, FTP_TRP | NET-01 |
| Recovery | FPT_RCV | OPS-01 |

**Assurance** (Part 3), what a laboratory reads:

- ADV, the design: the code is open (AGPL), and `docs/` describes the
  architecture.
- AGD, the guidance: `docs/deployment.md`, `docs/configuration.md` and the
  installer's own checks.
- ALC, the life cycle: each release is tagged, with a changelog, an SBOM and
  its checksums, and is scanned for known vulnerabilities (GOV-03). Flaws are
  fixed in releases.
- ATE, the tests: the test suites that prove each control (`controls.yaml`).
- AVA, vulnerability analysis: the laboratory's own, with the release scan's
  findings as input.

**What a company that needs a certificate does**: it chooses the scheme and the
assurance level, or a Protection Profile its buyers ask for. It takes this
outline to an accredited laboratory, which writes the ST with it, evaluates the
release, and the scheme certifies that release. A later release is a new
evaluation, or a maintenance under the scheme's rules.

## FIPS 140-3

Arena uses the cryptography of its platform: .NET's, which on Linux is
OpenSSL's, and Postgres's and Traefik's. It has no module of its own to
validate. Where a validated module is required, the host and the images must
use a FIPS 140-3 validated OpenSSL provider, with only approved algorithms
enabled. Arena's own uses (TLS 1.2 and 1.3, AES-GCM for files at rest, SHA-256
for the audit chain, PBKDF2 for passwords) are approved algorithms. That
images built on a validated provider are in a later plan, and needs its
source approved.

## Records

The ST, the evaluation report, the certificate and its scope, kept with the
release they are for.
