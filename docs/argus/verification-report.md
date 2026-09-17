# Argus end-to-end verification

Run against a real GitLab CE at `http://host.docker.internal:8929`.
**16/16 checks passed.**

## Index measurements

| Metric | Value |
|---|---|
| Full index wall-clock | 1.7s |
| repos | 4 |
| files | 11 |
| symbols | 101 |
| public_symbols | 97 |
| includes | 5 |

## Checks

| Check | Result | Detail |
|---|---|---|
| service token enumerates every seeded private project | PASS | expected ['driver-shim', 'eal-core', 'etl-decoder'], saw ['driver-shim', 'eal-core', 'etl-decoder'] |
| a NON-admin token sees fewer projects (membership=false caveat is real) | PASS | admin saw 3, dev_alpha saw 1 |
| index run completed | PASS | 1.7s |
| the release branch was indexed alongside trunk | PASS | 1.2s |
| the vector index was built, so semantic_search can be verified | PASS | 0.6s |
| symbols were extracted | PASS |  |
| cross-repo includes were recorded | PASS |  |
| dev_alpha's allowlist is exactly their one project | PASS | got ['eal-core'] |
| dev_beta's allowlist is exactly their one project | PASS | got ['etl-decoder'] |
| the two developers' allowlists are disjoint | PASS |  |
| driver-shim is in NOBODY's allowlist | PASS |  |
| get_file refuses driver-shim for dev_alpha | PASS |  |
| index is non-empty, so the isolation checks below are meaningful | PASS | 101 symbols indexed |
| find_symbol returns only what the caller may read, and finds it | PASS | alpha=['eal-core'] (want eal-core) beta=[] (want a subset of etl-decoder) |
| find_references returns only what the caller may read, and finds it | PASS | alpha=['eal-core'] beta=['etl-decoder'] |
| an EMPTY allowlist returns nothing, not everything | PASS |  |
