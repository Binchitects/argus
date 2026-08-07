# Indexing more than one branch

A repository often has long-lived release branches alongside the trunk —
`main` plus `v1`, `v2`, `v3`. Before this, Argus indexed exactly one ref per
project, whichever GitLab reported as the default, and a developer working on
v2 received trunk answers **with nothing saying so**. That is worse than no
answer: it is a confident answer about code they are not changing.

## Configuring it

```yaml
index:
  data_dir: /var/lib/argus
  db_path:  /var/lib/argus/index.db
  branches: ["main", "v*"]      # glob patterns, fnmatch syntax
```

Omit `branches` entirely and Argus indexes each project's default branch and
nothing else — exactly what it did before, so an existing config is unchanged
by upgrading.

**The default branch is always indexed**, whether or not it matches a pattern.
A config of `["v*"]` against a default of `main` would otherwise index every
release branch and leave the default empty, and no error would be raised —
every unqualified question would come back empty on a healthy-looking index.

**Cost is roughly linear in branches matched.** `["main", "v*"]` against a
repo with v1/v2/v3 is four times the files, symbols and disk. Measured at
10,212 files the index was 224 MB; a four-branch layout of the same estate
would be near 900 MB. Match deliberately — `v*` catches `v1` and also
`vendor-experiment`.

## Asking about a branch

`find_symbol`, `find_references`, `search_code` and `which_repo` take an
optional `branch`:

```jsonc
{"name": "DecodeFrame"}                  // the default branch
{"name": "DecodeFrame", "branch": "v2"}  // v2 across every permitted project
```

Naming a branch that is not indexed is an **error naming the branches that
are**, never an empty result. An agent that receives `[]` concludes the symbol
does not exist; the one failure this feature exists to prevent is answering
confidently about the wrong code, and an unexplained empty result is the same
mistake wearing different clothes.

Tools that address a repo row directly — `get_file`, `repo_map`, `impact_of` —
take a `repo_id`, which already identifies one project *at one branch*. They
are not branch-scoped, because the caller has already chosen.

## What it does to the rest of the system

**Access control is unchanged.** Permission is a property of the GitLab
project, and GitLab has no per-branch read ACL to mirror, so a user permitted
on a project is permitted on all of its indexed branches.
`acl._map_to_repo_ids` already resolved a project to every row it owns, so this
needed no change — but it is the seam where multi-branch could silently widen
access, so it is covered by a test rather than left to inspection.

Branch selection **narrows** an allowlist access control has already produced.
It can never widen one: naming a branch of a repo you cannot read stays empty.

**Mirrors are shared, worktrees are not.** `ensure_mirror` already fetched
`+refs/heads/*`, so every branch is present after one fetch and indexing four
branches costs no extra network. Worktrees are per branch, and the branch
component of the path is percent-encoded — `release/v1` and `release-v1` are
different branches and must not collide.

**Branches that disappear are pruned.** A release branch deleted upstream, or
a default branch renamed, would otherwise leave a fully populated row that
keeps answering questions and never looks stale: it has a real SHA and a real
timestamp from the last run that did find it. Each pass deletes rows for
branches no longer selected, which cascades to their files and symbols.

## Storage, honestly

There is no deduplication between branches. Two branches sharing 99% of their
files store both copies, twice the symbols and twice the FTS entries. Content
addressing by `blob_sha` would fix that and is not built — it would change the
`files` primary key and every writer, and the measurement that would justify
it (how much real release branches actually diverge) has not been taken.

Index the branches people ask questions about, not every branch that exists.
