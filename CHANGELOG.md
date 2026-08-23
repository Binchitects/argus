# Changelog

## 1.1.0

Forty-nine commits since v1.0.0. The theme is retrieval quality: v1.0 could
index and serve, but nobody had measured whether `docs_find` actually answered
description-shaped questions. It did not, and most of the reason was data
rather than ranking.

Note that `pyproject.toml` still read `0.1.0rc1` throughout v1.0 -- the version
was never bumped at that release. It now tracks the tag.

### docs_find answers roughly twice as often

Measured over a 36-question set, top-10: **25% -> 44%** unscoped, and **58%**
when the caller names the source.

* **`docs_find` now serves the hybrid arm.** `search_symbols_hybrid` was
  implemented, documented, and had no callers -- the tool ran the purely
  lexical arm. Term overlap between a question and its answer's description is
  35%, so a term scorer had a low ceiling however it weighted; twelve of
  twenty-five answers shared one word with the question or none.
* **Every pack now has zero blank descriptions.** `docs_find` searches that
  field and skips rows where it is empty, so a blank description made a symbol
  invisible while it still occupied disk. cpp went from 100% blank, python
  from 50%.
* **Descriptions come from the page, not the page's title.** cpp two-word
  descriptions 56% -> 2.2%, python 62% -> 16.3%. `_countof`'s entire
  searchable text had been "_countof Macro".
* **A chunk now says which of a page's symbols it documents.** A 369-symbol
  page returned an arbitrary 8 of them, ordered by rowid.
* **The tool description names the installed sources**, and a `lang` naming no
  installed pack widens instead of returning nothing. Measured through Hermes:
  the model passes `lang` on 5 of 8 calls, including `scripting` for a
  PowerShell question -- knowable only from that list.

### GitLab authentication

* **`gitlab.auth: password`** for username/password sign-in, alongside the
  existing access token. The two are not interchangeable: an access token goes
  in `PRIVATE-TOKEN`, an OAuth token in `Authorization: Bearer`.
  `argus/credentials.py` owns that distinction and every API caller asks it
  for headers.
* The password is read from `ARGUS_GITLAB_PASSWORD` only; a `password` key in
  the config file is **refused**, not ignored.
* **Verified against GitLab 19.2.1: recent GitLab has removed the password
  grant**, and no headless username/password path replaces it. The error says
  so and names the fix rather than reading like a bad password. Use
  `auth: token` unless your GitLab predates the removal.

### Indexing at estate scale

First run against 47 repositories, 55,603 files, **1,491,167 symbols**, 37.8
minutes, zero failures or timeouts.

* **`which_repo` ranks on the raw score**, not the display-clamped
  `confidence`. Every score above 1.0 compared equal and ties broke
  alphabetically -- lz4 beat zstd for "compress a byte stream with a
  dictionary" because `l` sorts before `z`.
* **Known and unfixed:** `which_repo`'s lexical evidence matches query words
  against identifiers, and at 1.5M symbols "store", "key" and "memory" are
  identifiers nearly everywhere. Asked to "store key-value pairs in memory
  with expiry", redis did not place. Routing is 5/10 on the estate set.

### Packs

Rebuilt against current upstream: cpp, python (3.14), wdk, win32, scripting.
The python pack records the branch it was actually built from -- it claimed
`main` while built from `3.14`.

**Trap worth knowing:** incremental rebuild keys on document content, so an
adapter change does *not* propagate to unchanged documents. Delete the
destination pack to force a full build after changing an adapter; the build
reports a healthy symbol count either way.

## 1.0.0

Initial release. 11 packs, ACL enforced structurally and audited, container
healthy, 741 tests.
