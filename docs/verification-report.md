# Argus end-to-end verification

Run against a real GitLab CE at `http://localhost:8929`.
**15/15 checks passed.**

## Index measurements

| Metric | Value |
|---|---|
| Full index wall-clock | 2.8s |
| repos | 3 |
| files | 8 |
| symbols | 70 |
| public_symbols | 68 |
| includes | 4 |

## Checks

| Check | Result | Detail |
|---|---|---|
| service token enumerates every seeded private project | PASS | expected ['driver-shim', 'eal-core', 'etl-decoder'], saw ['driver-shim', 'eal-core', 'etl-decoder'] |
| a NON-admin token sees fewer projects (membership=false caveat is real) | PASS | admin saw 3, dev_alpha saw 1 |
| index run completed | PASS | 2.8s |
| symbols were extracted | PASS |  |
| cross-repo includes were recorded | PASS |  |
| dev_alpha's allowlist is exactly their one project | PASS | got ['eal-core'] |
| dev_beta's allowlist is exactly their one project | PASS | got ['etl-decoder'] |
| the two developers' allowlists are disjoint | PASS |  |
| driver-shim is in NOBODY's allowlist | PASS |  |
| get_file refuses driver-shim for dev_alpha | PASS |  |
| index is non-empty, so the isolation checks below are meaningful | PASS | no symbols indexed -- isolation assertions would pass vacuously |
| DecodeFrame is actually findable by the repo that defines it | PASS | alpha found 2 |
| find_symbol never crosses the allowlist | PASS | alpha=['root/eal-core'] beta=[] |
| find_references never crosses the allowlist | PASS | alpha=['root/eal-core'] beta=['root/etl-decoder'] |
| an EMPTY allowlist returns nothing, not everything | PASS |  |
