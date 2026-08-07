# Argus roadmap

Phases 1, 2, 3 and 5 have shipped: a private code index with GitLab-derived
access control, nine MCP tools, a cross-repo dependency graph, and portable
public documentation packs. 529 tests pass locally and in the container.

Everything below is ordered by one principle: **measure before building.**
Several decisions on this list have been deferred twice because guessing at
them would have been cheaper than measuring, and wrong.

---

## Step 0 — The real indexing run

**Nothing else on this roadmap should start first.** Every remaining decision
of consequence is gated on numbers this project does not have.

### What it unblocks

| Question | Currently | Decided by |
|---|---|---|
| Postgres or stay on SQLite? | Deferred twice | Index size, write contention, query latency at real scale |
| `_LEXICAL_SMOOTHING = 10`, `_FLOOR_RATIO = 0.35` | Defensible guesses | `which_repo` accuracy on real questions |
| Is selective embedding (Phase 4) affordable? | Estimated 70–90k vectors | Actual public-symbol count |
| Does `which_repo` work? | Passes tests on fixtures | Hand-checked answers on real tasks |
| Incremental indexing fast enough for a webhook? | Untested at scale | Per-repo pass duration |

### Before you trust a single number

**Verify the service token sees your private projects.** `argus/gitlab.py`
enumerates with `membership=false`, which for a **non-admin** token returns
only *public* projects. Measured on the test instance: an admin token saw 3 of
3 private projects; a non-admin token saw 1 of 3.

If the token is not an admin, the index will silently cover a fraction of your
repositories and every measurement below will be confidently wrong. This has
been an open question since Phase 1 and has never been checked against a real
instance.

```bash
# Expect this count to match what you actually have.
curl -s -H "PRIVATE-TOKEN: $ARGUS_GITLAB_TOKEN" \
  "$GITLAB_URL/api/v4/projects?membership=false&simple=true&per_page=100" \
  | python -c "import json,sys; print(len(json.load(sys.stdin)))"
```

### The run

```bash
export ARGUS_GITLAB_TOKEN=<service token>
export ARGUS_GIT_ASKPASS_TOKEN=$ARGUS_GITLAB_TOKEN
time argus index --config /etc/argus/config.yaml
argus status --config /etc/argus/config.yaml
```

### Record

- Repo count, total files, total symbols, index file size on disk
- Wall-clock for a cold full pass, and for a warm no-change pass
- The resolution summary the run prints: resolved / external / **ambiguous** /
  not found. A high ambiguous share means many repos ship headers with the
  same basename, and `which_repo` will be correspondingly weaker — that is a
  property of your `-I` layout, not something tool tuning fixes.
- Cross-repo edge count

### Then hand-check `which_repo`

Ten real questions your developers have actually asked, one per input shape at
minimum: a prose task, a symbol name, a stack trace, a diff under review.
Record the top answer and whether it was right. **Write down the misses** —
the point of this exercise is the failures, not a score.

Deliverable: `docs/index-measurements.md`, in the shape of
`docs/pack-measurements.md`, which records its misses too.

---

## Then, in order

### A. Tune retrieval from the measurements *(small, immediately valuable)*

`_LEXICAL_SMOOTHING` and `_FLOOR_RATIO` are currently reasoned defaults with
their reasoning documented at the constant. Set them from the hand-checked
results. If `which_repo`'s misses share a cause — as Phase 5's did, where a
page discussing an API outranked the page defining it — fix the cause rather
than the constant.

### B. Scale and operations *(depends on Step 0)*

The Postgres question, answered with numbers rather than intuition. Also:
parallel or incremental indexing if a cold pass is too slow, the GitLab
webhook trigger the design specifies but never built, backup/restore, and
metrics an operator can alert on.

**Do not start this before Step 0.** Postgres is a large, mostly irreversible
change, and the only honest input is a real index.

### C. Identity and governance *(independent — can run in parallel)*

What a security review will ask for: SSO/OIDC instead of per-developer GitLab
PATs, audit export and retention, and an air-gapped install path. Nothing here
is blocked by measurements.

### D. Phase 4 — semantic search over private code *(depends on Step 0)*

Deliberately last among the retrieval work, and the decision is already
recorded: it extends the knowledge-pack pipeline — `packs/quantize.py`,
`argus/embed.py`, the measured two-stage search — rather than forking a second
embedding stack. Phase 5's binary + int8 quantization turns the original
spec's ~270 MB of float32 vectors into ~8.6 MB coarse plus ~69 MB rescore.

The measurement that matters: query embedding costs **2,254 ms on CPU Ollama**
against 89 ms for a whole pack search. The embedder, not the index, sets the
latency users feel. On CPU-only hardware, semantic search is a batch luxury,
not an interactive feature.

### E. Pack ecosystem *(independent)*

MDN, .NET, Win32 and Linux packs on the format already proven, plus a signed
public registry and `argus pack update` against it. This is the direct
continuation of the original "huge knowledge base, shareable" ask, and the
format has survived contact with two real corpora.

### F. Developer surface *(independent)*

IDE integration, a CLI for humans rather than agents, a search UI. Worth doing
only once Step 0 confirms the answers are good enough to want direct access to.

---

## Done since this roadmap was written

- **Enumeration guard.** `argus index` now refuses when the service token
  cannot see every repository, naming the numbers. The silent-partial-index
  risk this document opens with is now impossible to miss.
- **Scheduled refresh.** `argus index --interval` plus a `refresher` compose
  service, under the existing `indexer` profile.
- **Backup and restore.** `argus backup`, documented in
  `docs/backup-and-restore.md`, verified by restore drill.

## Known follow-ups, carried

- **`mcp` 2.0 is released**; this project pins `>=1.9,<2.0`. The upgrade
  renames `FastMCP` → `MCPServer`, which breaks `create_app`, and requires
  re-verifying the DNS-rebinding allowlist behaviour — the defect that once
  returned 421 on every proxied call while `/healthz` stayed green. A
  deliberate task, not a version bump.
- **`which_repo`'s prose path is the weakest**, by design: three of the four
  input shapes never needed embeddings, so it ships useful without them.
  Phase 4 improves prose and nothing else.
- **Phase 5's retrieval quality was 6 good, 2 partial, 2 wrong** on ten
  hand-checked questions. Both misses shared a cause: a page discussing an API
  outranked the page defining it. `docs_lookup` was fixed for exactly this;
  `docs_search` was left alone deliberately, because tuning ranking after
  seeing ten questions is how you fit to ten questions.
- **A Phase 2-era `git stash` is still in the stash list.** It was applied by
  accident during Phase 3 and left conflict markers in `argus/mcpsrv/server.py`;
  the working tree was restored from HEAD and nothing committed was lost. The
  stash itself is untouched and worth a look before it is dropped.
