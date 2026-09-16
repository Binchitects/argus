# Argus end-to-end verification

Run against a real GitLab CE at `http://host.docker.internal:8929`.
**15/15 checks passed.**

## Index measurements

| Metric | Value |
|---|---|
| Full index wall-clock | 1.7s |
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
| index run completed | PASS | 1.7s |
| the vector index was built, so semantic_search can be verified | PASS | 0.7s |
| symbols were extracted | PASS |  |
| cross-repo includes were recorded | PASS |  |
| dev_alpha's allowlist is exactly their one project | PASS | got ['eal-core'] |
| dev_beta's allowlist is exactly their one project | PASS | got ['etl-decoder'] |
| the two developers' allowlists are disjoint | PASS |  |
| driver-shim is in NOBODY's allowlist | PASS |  |
| get_file refuses driver-shim for dev_alpha | PASS |  |
| index is non-empty, so the isolation checks below are meaningful | PASS | 70 symbols indexed |
| find_symbol returns only what the caller may read, and finds it | PASS | alpha=['eal-core'] (want eal-core) beta=[] (want a subset of etl-decoder) |
| find_references returns only what the caller may read, and finds it | PASS | alpha=['eal-core'] beta=['etl-decoder'] |
| an EMPTY allowlist returns nothing, not everything | PASS |  |
