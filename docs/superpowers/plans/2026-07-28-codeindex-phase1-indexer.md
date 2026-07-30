# CodeIndex Phase 1 — Indexer Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Build the CodeIndex indexer — mirror every GitLab repo, extract symbols and includes from changed files, and store them in SQLite behind an access-control-safe query API, driven by a CLI.

**Architecture:** A `mirror` module maintains bare git mirrors plus one worktree per repo and computes changed-file sets from git diffs. A `parse` package extracts symbols (universal-ctags) and `#include` directives from those files. A `store` package owns all SQLite access, split into service-side writes and allowlist-gated reads. A serialized `worker` orchestrates one repo at a time. No HTTP server, no embeddings, no MCP in this phase.

**Tech Stack:** Python 3.11+, SQLite (FTS5, external-content mode), universal-ctags, git, httpx, PyYAML, pytest.

Spec: [`docs/superpowers/specs/2026-07-28-local-code-assistant-design.md`](../specs/2026-07-28-local-code-assistant-design.md)

## Global Constraints

Every task's requirements implicitly include this section.

- **Python `>=3.11`.** The index host is Linux; workstations run 3.13. Do not use 3.12+ syntax.
- **`allowed_repo_ids` is the first positional parameter of every public function in `codeindex/store/queries.py`**, with no default. This is enforced by an introspection test, not by convention.
- **`store/writes.py` is service-side and takes no allowlist.** Reads and writes are separate modules precisely so the allowlist rule can be stated absolutely for one of them.
- **CodeIndex never writes to GitLab.** Read-only API calls; no pushes, no merge requests.
- **Default branch only.** No multi-branch indexing.
- **No network in tests.** GitLab is mocked; git operations run against temp fixture repos created by the tests.
- **Deferred to later phases, do not build:** tree-sitter parsing, embeddings, `sqlite-vec`, the MCP server, the ACL module, cross-repo include *resolution*. Phase 1 stores raw include strings only.
- **File exclusion defaults** (from spec, configurable): dirs `third_party`, `vendor`, `node_modules`, `build`, `out`, `x64`, `Debug`, `Release`; max file size `1048576` bytes; binary detection = null byte in first 8192 bytes.
- **Commit after every task.** Conventional commit prefixes (`feat:`, `test:`, `chore:`).

## File Structure

| File | Responsibility |
|---|---|
| `pyproject.toml` | Package metadata, deps, `codeindex` console script |
| `codeindex/config.py` | Typed config loaded from YAML + env overrides |
| `codeindex/store/db.py` | Connection setup, numbered SQL migrations |
| `codeindex/store/migrations/001_initial.sql` | Phase 1–3 schema |
| `codeindex/store/writes.py` | Service-side inserts/updates/deletes (no allowlist) |
| `codeindex/store/queries.py` | Allowlist-gated reads (allowlist first positional) |
| `codeindex/parse/filters.py` | Which files to index; language detection |
| `codeindex/parse/ctags.py` | ctags invocation → `Symbol` records |
| `codeindex/parse/includes.py` | `#include` extraction |
| `codeindex/gitlab.py` | GitLab REST client (project enumeration) |
| `codeindex/mirror.py` | git mirror/worktree lifecycle, change detection |
| `codeindex/worker.py` | Per-repo indexing orchestration |
| `codeindex/cli.py` | `codeindex index` / `codeindex status` |

---

### Task 1: Project scaffolding and config

**Files:**
- Create: `pyproject.toml`
- Create: `codeindex/__init__.py`
- Create: `codeindex/config.py`
- Create: `tests/__init__.py`
- Test: `tests/test_config.py`

**Interfaces:**
- Consumes: nothing.
- Produces: `Config.load(path: Path) -> Config`; `Config` has `.gitlab: GitLabConfig` (`.url: str`, `.token: str`) and `.index: IndexConfig` (`.data_dir: Path`, `.db_path: Path`, `.max_file_bytes: int`, `.exclude_dirs: tuple[str, ...]`, `.repo_time_budget_seconds: int`). Raises `ConfigError` on missing required values.

- [ ] **Step 1: Write the failing test**

`tests/test_config.py`:

```python
import pytest
from pathlib import Path
from codeindex.config import Config, ConfigError

YAML = """
gitlab:
  url: https://gitlab.internal
  token: from-file
index:
  data_dir: /var/lib/codeindex
  db_path: /var/lib/codeindex/index.db
"""


def test_loads_yaml(tmp_path):
    p = tmp_path / "c.yaml"
    p.write_text(YAML)
    cfg = Config.load(p)
    assert cfg.gitlab.url == "https://gitlab.internal"
    assert cfg.gitlab.token == "from-file"
    assert cfg.index.db_path == Path("/var/lib/codeindex/index.db")


def test_env_overrides_token(tmp_path, monkeypatch):
    p = tmp_path / "c.yaml"
    p.write_text(YAML)
    monkeypatch.setenv("CODEINDEX_GITLAB_TOKEN", "from-env")
    assert Config.load(p).gitlab.token == "from-env"


def test_defaults_applied(tmp_path):
    p = tmp_path / "c.yaml"
    p.write_text(YAML)
    cfg = Config.load(p)
    assert cfg.index.max_file_bytes == 1048576
    assert "node_modules" in cfg.index.exclude_dirs
    assert cfg.index.repo_time_budget_seconds == 600


def test_missing_token_raises(tmp_path):
    p = tmp_path / "c.yaml"
    p.write_text("gitlab:\n  url: https://x\nindex:\n  data_dir: /d\n  db_path: /d/i.db\n")
    with pytest.raises(ConfigError, match="token"):
        Config.load(p)
```

- [ ] **Step 2: Run test to verify it fails**

Run: `pytest tests/test_config.py -v`
Expected: FAIL with `ModuleNotFoundError: No module named 'codeindex'`

- [ ] **Step 3: Write the implementation**

`pyproject.toml`:

```toml
[project]
name = "codeindex"
version = "0.1.0"
requires-python = ">=3.11"
dependencies = ["pyyaml>=6.0", "httpx>=0.27"]

[project.optional-dependencies]
dev = ["pytest>=8.0"]

[project.scripts]
codeindex = "codeindex.cli:main"

[build-system]
requires = ["setuptools>=68"]
build-backend = "setuptools.build_meta"

[tool.setuptools.packages.find]
include = ["codeindex*"]

[tool.setuptools.package-data]
codeindex = ["store/migrations/*.sql"]
```

`codeindex/__init__.py`: empty file.

`tests/__init__.py`: empty file.

`codeindex/config.py`:

```python
from __future__ import annotations

import os
from dataclasses import dataclass
from pathlib import Path

import yaml

DEFAULT_EXCLUDE_DIRS = (
    "third_party", "vendor", "node_modules",
    "build", "out", "x64", "Debug", "Release",
)


class ConfigError(Exception):
    """Raised when configuration is missing or malformed."""


@dataclass(frozen=True)
class GitLabConfig:
    url: str
    token: str


@dataclass(frozen=True)
class IndexConfig:
    data_dir: Path
    db_path: Path
    max_file_bytes: int = 1048576
    exclude_dirs: tuple[str, ...] = DEFAULT_EXCLUDE_DIRS
    repo_time_budget_seconds: int = 600

    @property
    def mirrors_dir(self) -> Path:
        return self.data_dir / "mirrors"

    @property
    def trees_dir(self) -> Path:
        return self.data_dir / "trees"


@dataclass(frozen=True)
class Config:
    gitlab: GitLabConfig
    index: IndexConfig

    @staticmethod
    def load(path: Path) -> "Config":
        raw = yaml.safe_load(Path(path).read_text()) or {}
        gl = raw.get("gitlab") or {}
        ix = raw.get("index") or {}

        url = gl.get("url")
        if not url:
            raise ConfigError("gitlab.url is required")

        token = os.environ.get("CODEINDEX_GITLAB_TOKEN") or gl.get("token")
        if not token:
            raise ConfigError(
                "gitlab.token is required (set it in config or CODEINDEX_GITLAB_TOKEN)"
            )

        for key in ("data_dir", "db_path"):
            if not ix.get(key):
                raise ConfigError(f"index.{key} is required")

        return Config(
            gitlab=GitLabConfig(url=url.rstrip("/"), token=token),
            index=IndexConfig(
                data_dir=Path(ix["data_dir"]),
                db_path=Path(ix["db_path"]),
                max_file_bytes=int(ix.get("max_file_bytes", 1048576)),
                exclude_dirs=tuple(ix.get("exclude_dirs", DEFAULT_EXCLUDE_DIRS)),
                repo_time_budget_seconds=int(ix.get("repo_time_budget_seconds", 600)),
            ),
        )
```

- [ ] **Step 4: Install and run tests**

Run: `pip install -e ".[dev]" && pytest tests/test_config.py -v`
Expected: 4 passed

- [ ] **Step 5: Commit**

```bash
git add pyproject.toml codeindex/__init__.py codeindex/config.py tests/__init__.py tests/test_config.py
git commit -m "feat: add package scaffolding and typed config loader"
```

---

### Task 2: Database connection and migrations

**Files:**
- Create: `codeindex/store/__init__.py`
- Create: `codeindex/store/db.py`
- Create: `codeindex/store/migrations/001_initial.sql`
- Test: `tests/store/__init__.py`, `tests/store/test_db.py`

**Interfaces:**
- Consumes: nothing.
- Produces: `connect(db_path: Path | str) -> sqlite3.Connection` (WAL, foreign keys on, `Row` factory); `migrate(conn) -> int` returning the applied schema version; `open_db(db_path) -> sqlite3.Connection` doing both. Tables: `repos`, `files`, `symbols`, `includes`, `repo_deps`, `index_errors`, `index_queue`, `files_fts`.

- [ ] **Step 1: Write the failing test**

`tests/store/test_db.py`:

```python
from codeindex.store.db import open_db, migrate

EXPECTED_TABLES = {
    "repos", "files", "symbols", "includes",
    "repo_deps", "index_errors", "index_queue", "files_fts",
}


def test_migrate_creates_tables(tmp_path):
    conn = open_db(tmp_path / "i.db")
    rows = conn.execute(
        "SELECT name FROM sqlite_master WHERE type IN ('table','view')"
    ).fetchall()
    names = {r["name"] for r in rows}
    assert EXPECTED_TABLES <= names


def test_migrate_is_idempotent(tmp_path):
    path = tmp_path / "i.db"
    v1 = migrate(open_db(path))
    v2 = migrate(open_db(path))
    assert v1 == v2 == 1


def test_foreign_keys_enforced(tmp_path):
    import sqlite3
    import pytest
    conn = open_db(tmp_path / "i.db")
    with pytest.raises(sqlite3.IntegrityError):
        conn.execute(
            "INSERT INTO files (repo_id, path, size, blob_sha, content)"
            " VALUES (999, 'a.c', 1, 'deadbeef', 'x')"
        )
        conn.commit()


def test_cascade_delete_removes_files(tmp_path):
    conn = open_db(tmp_path / "i.db")
    conn.execute(
        "INSERT INTO repos (gitlab_id, path_with_namespace, default_branch, http_url)"
        " VALUES (1, 'g/a', 'main', 'https://x/g/a')"
    )
    repo_id = conn.execute("SELECT id FROM repos").fetchone()["id"]
    conn.execute(
        "INSERT INTO files (repo_id, path, size, blob_sha, content)"
        " VALUES (?, 'a.c', 1, 'deadbeef', 'x')", (repo_id,)
    )
    conn.commit()
    conn.execute("DELETE FROM repos WHERE id = ?", (repo_id,))
    conn.commit()
    assert conn.execute("SELECT COUNT(*) c FROM files").fetchone()["c"] == 0
```

- [ ] **Step 2: Run test to verify it fails**

Run: `pytest tests/store/test_db.py -v`
Expected: FAIL with `ModuleNotFoundError: No module named 'codeindex.store'`

- [ ] **Step 3: Write the implementation**

`codeindex/store/__init__.py`: empty file.
`tests/store/__init__.py`: empty file.

`codeindex/store/migrations/001_initial.sql`:

```sql
CREATE TABLE IF NOT EXISTS repos (
  id                  INTEGER PRIMARY KEY,
  gitlab_id           INTEGER NOT NULL UNIQUE,
  path_with_namespace TEXT    NOT NULL,
  default_branch      TEXT    NOT NULL,
  http_url            TEXT    NOT NULL,
  last_indexed_sha    TEXT,
  last_indexed_at     INTEGER
);

CREATE TABLE IF NOT EXISTS files (
  id       INTEGER PRIMARY KEY,
  repo_id  INTEGER NOT NULL REFERENCES repos(id) ON DELETE CASCADE,
  path     TEXT    NOT NULL,
  lang     TEXT,
  size     INTEGER NOT NULL,
  blob_sha TEXT    NOT NULL,
  content  TEXT    NOT NULL,
  UNIQUE (repo_id, path)
);
CREATE INDEX IF NOT EXISTS idx_files_repo ON files(repo_id);

CREATE TABLE IF NOT EXISTS symbols (
  id        INTEGER PRIMARY KEY,
  repo_id   INTEGER NOT NULL REFERENCES repos(id) ON DELETE CASCADE,
  file_id   INTEGER NOT NULL REFERENCES files(id) ON DELETE CASCADE,
  name      TEXT    NOT NULL,
  kind      TEXT    NOT NULL,
  line      INTEGER NOT NULL,
  end_line  INTEGER,
  signature TEXT,
  scope     TEXT,
  is_public INTEGER NOT NULL DEFAULT 0
);
CREATE INDEX IF NOT EXISTS idx_symbols_name ON symbols(name);
CREATE INDEX IF NOT EXISTS idx_symbols_repo ON symbols(repo_id);
CREATE INDEX IF NOT EXISTS idx_symbols_file ON symbols(file_id);

CREATE TABLE IF NOT EXISTS includes (
  id               INTEGER PRIMARY KEY,
  repo_id          INTEGER NOT NULL REFERENCES repos(id) ON DELETE CASCADE,
  file_id          INTEGER NOT NULL REFERENCES files(id) ON DELETE CASCADE,
  raw              TEXT    NOT NULL,
  is_angle         INTEGER NOT NULL DEFAULT 0,
  resolved_file_id INTEGER,
  resolved_repo_id INTEGER,
  is_external      INTEGER NOT NULL DEFAULT 0
);
CREATE INDEX IF NOT EXISTS idx_includes_repo ON includes(repo_id);
CREATE INDEX IF NOT EXISTS idx_includes_file ON includes(file_id);

CREATE TABLE IF NOT EXISTS repo_deps (
  from_repo_id INTEGER NOT NULL REFERENCES repos(id) ON DELETE CASCADE,
  to_repo_id   INTEGER NOT NULL REFERENCES repos(id) ON DELETE CASCADE,
  weight       INTEGER NOT NULL,
  PRIMARY KEY (from_repo_id, to_repo_id)
);

CREATE TABLE IF NOT EXISTS index_errors (
  id      INTEGER PRIMARY KEY,
  repo_id INTEGER NOT NULL REFERENCES repos(id) ON DELETE CASCADE,
  path    TEXT,
  stage   TEXT    NOT NULL,
  message TEXT    NOT NULL,
  ts      INTEGER NOT NULL
);

CREATE TABLE IF NOT EXISTS index_queue (
  repo_id     INTEGER PRIMARY KEY REFERENCES repos(id) ON DELETE CASCADE,
  enqueued_at INTEGER NOT NULL,
  reason      TEXT    NOT NULL
);

CREATE VIRTUAL TABLE IF NOT EXISTS files_fts USING fts5(
  path,
  content,
  content='files',
  content_rowid='id',
  tokenize='unicode61'
);
```

`codeindex/store/db.py`:

```python
from __future__ import annotations

import sqlite3
from pathlib import Path

MIGRATIONS_DIR = Path(__file__).parent / "migrations"


def connect(db_path: Path | str) -> sqlite3.Connection:
    path = Path(db_path)
    path.parent.mkdir(parents=True, exist_ok=True)
    conn = sqlite3.connect(path)
    conn.row_factory = sqlite3.Row
    conn.execute("PRAGMA journal_mode = WAL")
    conn.execute("PRAGMA foreign_keys = ON")
    conn.execute("PRAGMA synchronous = NORMAL")
    return conn


def migrate(conn: sqlite3.Connection) -> int:
    current = conn.execute("PRAGMA user_version").fetchone()[0]
    for sql_file in sorted(MIGRATIONS_DIR.glob("*.sql")):
        version = int(sql_file.name.split("_", 1)[0])
        if version <= current:
            continue
        conn.executescript(sql_file.read_text())
        # PRAGMA does not accept bound parameters; version is a validated int.
        conn.execute(f"PRAGMA user_version = {version}")
        conn.commit()
        current = version
    return current


def open_db(db_path: Path | str) -> sqlite3.Connection:
    conn = connect(db_path)
    migrate(conn)
    return conn
```

- [ ] **Step 4: Run tests**

Run: `pytest tests/store/test_db.py -v`
Expected: 4 passed

- [ ] **Step 5: Commit**

```bash
git add codeindex/store tests/store
git commit -m "feat: add sqlite connection, migrations and phase-1 schema"
```

---

### Task 3: Service-side writes

**Files:**
- Create: `codeindex/store/writes.py`
- Test: `tests/store/test_writes.py`

**Interfaces:**
- Consumes: `codeindex.store.db.open_db`.
- Produces:
  - `upsert_repo(conn, *, gitlab_id, path_with_namespace, default_branch, http_url) -> int`
  - `set_last_indexed(conn, repo_id: int, sha: str, ts: int) -> None`
  - `upsert_file(conn, *, repo_id, path, lang, size, blob_sha, content) -> int`
  - `delete_file(conn, repo_id: int, path: str) -> None`
  - `replace_symbols(conn, repo_id: int, file_id: int, symbols: list[dict]) -> None`
  - `replace_includes(conn, repo_id: int, file_id: int, includes: list[dict]) -> None`
  - `record_error(conn, repo_id, path, stage, message, ts) -> None`
  - Symbol dicts use keys `name, kind, line, end_line, signature, scope, is_public`.
  - Include dicts use keys `raw, is_angle`.

FTS5 external-content mode requires explicit index maintenance: a plain `DELETE` on `files` leaves the index stale. `upsert_file` and `delete_file` issue the documented `'delete'` command with the **old** row values before writing new ones.

- [ ] **Step 1: Write the failing test**

`tests/store/test_writes.py`:

```python
import pytest
from codeindex.store.db import open_db
from codeindex.store import writes


@pytest.fixture
def conn(tmp_path):
    return open_db(tmp_path / "i.db")


@pytest.fixture
def repo_id(conn):
    return writes.upsert_repo(
        conn, gitlab_id=1, path_with_namespace="g/a",
        default_branch="main", http_url="https://x/g/a",
    )


def test_upsert_repo_is_idempotent(conn):
    a = writes.upsert_repo(conn, gitlab_id=7, path_with_namespace="g/a",
                           default_branch="main", http_url="https://x/g/a")
    b = writes.upsert_repo(conn, gitlab_id=7, path_with_namespace="g/a-renamed",
                           default_branch="trunk", http_url="https://x/g/a")
    assert a == b
    row = conn.execute("SELECT * FROM repos WHERE id = ?", (a,)).fetchone()
    assert row["path_with_namespace"] == "g/a-renamed"
    assert row["default_branch"] == "trunk"


def test_upsert_file_replaces_content(conn, repo_id):
    f1 = writes.upsert_file(conn, repo_id=repo_id, path="a.c", lang="c",
                            size=3, blob_sha="aaa", content="old")
    f2 = writes.upsert_file(conn, repo_id=repo_id, path="a.c", lang="c",
                            size=3, blob_sha="bbb", content="new")
    assert f1 == f2
    assert conn.execute("SELECT content FROM files").fetchone()["content"] == "new"


def test_fts_reflects_updates_and_deletes(conn, repo_id):
    writes.upsert_file(conn, repo_id=repo_id, path="a.c", lang="c",
                       size=9, blob_sha="aaa", content="alphaword")
    assert _fts_count(conn, "alphaword") == 1

    writes.upsert_file(conn, repo_id=repo_id, path="a.c", lang="c",
                       size=8, blob_sha="bbb", content="betaword")
    assert _fts_count(conn, "alphaword") == 0
    assert _fts_count(conn, "betaword") == 1

    writes.delete_file(conn, repo_id, "a.c")
    assert _fts_count(conn, "betaword") == 0


def _fts_count(conn, term):
    return conn.execute(
        "SELECT COUNT(*) c FROM files_fts WHERE files_fts MATCH ?", (term,)
    ).fetchone()["c"]


def test_replace_symbols_clears_previous(conn, repo_id):
    fid = writes.upsert_file(conn, repo_id=repo_id, path="a.c", lang="c",
                             size=1, blob_sha="aaa", content="x")
    writes.replace_symbols(conn, repo_id, fid, [
        {"name": "Old", "kind": "function", "line": 1, "end_line": 3,
         "signature": "(void)", "scope": None, "is_public": 1},
    ])
    writes.replace_symbols(conn, repo_id, fid, [
        {"name": "New", "kind": "function", "line": 5, "end_line": 9,
         "signature": "(int)", "scope": None, "is_public": 0},
    ])
    names = [r["name"] for r in conn.execute("SELECT name FROM symbols")]
    assert names == ["New"]


def test_delete_file_cascades_symbols(conn, repo_id):
    fid = writes.upsert_file(conn, repo_id=repo_id, path="a.c", lang="c",
                             size=1, blob_sha="aaa", content="x")
    writes.replace_symbols(conn, repo_id, fid, [
        {"name": "F", "kind": "function", "line": 1, "end_line": 2,
         "signature": None, "scope": None, "is_public": 1},
    ])
    writes.delete_file(conn, repo_id, "a.c")
    assert conn.execute("SELECT COUNT(*) c FROM symbols").fetchone()["c"] == 0


def test_replace_includes(conn, repo_id):
    fid = writes.upsert_file(conn, repo_id=repo_id, path="a.c", lang="c",
                             size=1, blob_sha="aaa", content="x")
    writes.replace_includes(conn, repo_id, fid, [
        {"raw": "eal/decoder.h", "is_angle": 0},
        {"raw": "stdio.h", "is_angle": 1},
    ])
    rows = conn.execute("SELECT raw, is_angle FROM includes ORDER BY raw").fetchall()
    assert [(r["raw"], r["is_angle"]) for r in rows] == [
        ("eal/decoder.h", 0), ("stdio.h", 1),
    ]
```

- [ ] **Step 2: Run test to verify it fails**

Run: `pytest tests/store/test_writes.py -v`
Expected: FAIL with `ImportError: cannot import name 'writes'`

- [ ] **Step 3: Write the implementation**

`codeindex/store/writes.py`:

```python
from __future__ import annotations

import sqlite3


def upsert_repo(conn: sqlite3.Connection, *, gitlab_id: int,
                path_with_namespace: str, default_branch: str,
                http_url: str) -> int:
    conn.execute(
        """
        INSERT INTO repos (gitlab_id, path_with_namespace, default_branch, http_url)
        VALUES (?, ?, ?, ?)
        ON CONFLICT(gitlab_id) DO UPDATE SET
            path_with_namespace = excluded.path_with_namespace,
            default_branch      = excluded.default_branch,
            http_url            = excluded.http_url
        """,
        (gitlab_id, path_with_namespace, default_branch, http_url),
    )
    conn.commit()
    return conn.execute(
        "SELECT id FROM repos WHERE gitlab_id = ?", (gitlab_id,)
    ).fetchone()["id"]


def set_last_indexed(conn: sqlite3.Connection, repo_id: int, sha: str, ts: int) -> None:
    conn.execute(
        "UPDATE repos SET last_indexed_sha = ?, last_indexed_at = ? WHERE id = ?",
        (sha, ts, repo_id),
    )
    conn.commit()


def _fts_delete(conn: sqlite3.Connection, row: sqlite3.Row) -> None:
    """Remove a row from the external-content FTS index using its OLD values."""
    conn.execute(
        "INSERT INTO files_fts(files_fts, rowid, path, content) "
        "VALUES ('delete', ?, ?, ?)",
        (row["id"], row["path"], row["content"]),
    )


def upsert_file(conn: sqlite3.Connection, *, repo_id: int, path: str,
                lang: str | None, size: int, blob_sha: str, content: str) -> int:
    existing = conn.execute(
        "SELECT id, path, content FROM files WHERE repo_id = ? AND path = ?",
        (repo_id, path),
    ).fetchone()

    if existing is not None:
        _fts_delete(conn, existing)
        conn.execute(
            "UPDATE files SET lang = ?, size = ?, blob_sha = ?, content = ? WHERE id = ?",
            (lang, size, blob_sha, content, existing["id"]),
        )
        file_id = existing["id"]
    else:
        cur = conn.execute(
            "INSERT INTO files (repo_id, path, lang, size, blob_sha, content)"
            " VALUES (?, ?, ?, ?, ?, ?)",
            (repo_id, path, lang, size, blob_sha, content),
        )
        file_id = cur.lastrowid

    conn.execute(
        "INSERT INTO files_fts(rowid, path, content) VALUES (?, ?, ?)",
        (file_id, path, content),
    )
    conn.commit()
    return file_id


def delete_file(conn: sqlite3.Connection, repo_id: int, path: str) -> None:
    row = conn.execute(
        "SELECT id, path, content FROM files WHERE repo_id = ? AND path = ?",
        (repo_id, path),
    ).fetchone()
    if row is None:
        return
    _fts_delete(conn, row)
    conn.execute("DELETE FROM files WHERE id = ?", (row["id"],))
    conn.commit()


def replace_symbols(conn: sqlite3.Connection, repo_id: int, file_id: int,
                    symbols: list[dict]) -> None:
    conn.execute("DELETE FROM symbols WHERE file_id = ?", (file_id,))
    conn.executemany(
        "INSERT INTO symbols"
        " (repo_id, file_id, name, kind, line, end_line, signature, scope, is_public)"
        " VALUES (?, ?, ?, ?, ?, ?, ?, ?, ?)",
        [
            (repo_id, file_id, s["name"], s["kind"], s["line"], s.get("end_line"),
             s.get("signature"), s.get("scope"), int(s.get("is_public", 0)))
            for s in symbols
        ],
    )
    conn.commit()


def replace_includes(conn: sqlite3.Connection, repo_id: int, file_id: int,
                     includes: list[dict]) -> None:
    conn.execute("DELETE FROM includes WHERE file_id = ?", (file_id,))
    conn.executemany(
        "INSERT INTO includes (repo_id, file_id, raw, is_angle) VALUES (?, ?, ?, ?)",
        [(repo_id, file_id, i["raw"], int(i.get("is_angle", 0))) for i in includes],
    )
    conn.commit()


def record_error(conn: sqlite3.Connection, repo_id: int, path: str | None,
                 stage: str, message: str, ts: int) -> None:
    conn.execute(
        "INSERT INTO index_errors (repo_id, path, stage, message, ts)"
        " VALUES (?, ?, ?, ?, ?)",
        (repo_id, path, stage, message[:2000], ts),
    )
    conn.commit()
```

- [ ] **Step 4: Run tests**

Run: `pytest tests/store/test_writes.py -v`
Expected: 6 passed (`_fts_count` is a helper, not a test)

- [ ] **Step 5: Commit**

```bash
git add codeindex/store/writes.py tests/store/test_writes.py
git commit -m "feat: add service-side store writes with FTS index maintenance"
```

---

### Task 4: Allowlist-gated reads — the enforcement guarantee

**Files:**
- Create: `codeindex/store/queries.py`
- Test: `tests/store/test_queries.py`

**Interfaces:**
- Consumes: `codeindex.store.db.open_db`, `codeindex.store.writes`.
- Produces, all with `allowed_repo_ids: Sequence[int]` as first positional parameter:
  - `find_symbol(allowed_repo_ids, conn, name, kind=None, limit=50) -> list[sqlite3.Row]`
  - `search_code(allowed_repo_ids, conn, query, limit=50) -> list[sqlite3.Row]`
  - `get_file(allowed_repo_ids, conn, repo_id, path) -> sqlite3.Row | None`
  - `index_status(allowed_repo_ids, conn) -> list[sqlite3.Row]`

**Write the enforcement test before anything else in this task.** It is what forces the parameter to be positional and required. Implement the store first and the allowlist inevitably becomes an optional keyword — it is more convenient at every individual call site, and each of those conveniences is a future leak.

- [ ] **Step 1: Write the failing test**

`tests/store/test_queries.py`:

```python
import inspect
import pytest

from codeindex.store.db import open_db
from codeindex.store import writes, queries


def test_every_public_query_takes_allowlist_first():
    """The enforcement guarantee: a new query cannot forget the allowlist."""
    offenders = []
    for name, fn in inspect.getmembers(queries, inspect.isfunction):
        if name.startswith("_") or fn.__module__ != queries.__name__:
            continue
        params = list(inspect.signature(fn).parameters.values())
        if not params or params[0].name != "allowed_repo_ids":
            offenders.append(f"{name}: first param is not allowed_repo_ids")
        elif params[0].default is not inspect.Parameter.empty:
            offenders.append(f"{name}: allowed_repo_ids has a default")
    assert offenders == []


@pytest.fixture
def two_repos(tmp_path):
    conn = open_db(tmp_path / "i.db")
    ids = {}
    for gid, ns, word in ((1, "g/alpha", "alphaword"), (2, "g/beta", "betaword")):
        rid = writes.upsert_repo(conn, gitlab_id=gid, path_with_namespace=ns,
                                 default_branch="main", http_url=f"https://x/{ns}")
        fid = writes.upsert_file(conn, repo_id=rid, path="src/a.c", lang="c",
                                 size=len(word), blob_sha=f"sha{gid}", content=word)
        writes.replace_symbols(conn, rid, fid, [
            {"name": "SharedName", "kind": "function", "line": 1, "end_line": 2,
             "signature": "(void)", "scope": None, "is_public": 1},
        ])
        writes.set_last_indexed(conn, rid, f"sha{gid}", 1000 + gid)
        ids[ns] = rid
    return conn, ids


def test_find_symbol_excludes_disallowed_repo(two_repos):
    conn, ids = two_repos
    rows = queries.find_symbol([ids["g/alpha"]], conn, "SharedName")
    assert len(rows) == 1
    assert rows[0]["repo_id"] == ids["g/alpha"]


def test_search_code_excludes_disallowed_repo(two_repos):
    conn, ids = two_repos
    assert queries.search_code([ids["g/alpha"]], conn, "betaword") == []
    assert len(queries.search_code([ids["g/beta"]], conn, "betaword")) == 1


def test_get_file_refuses_disallowed_repo(two_repos):
    conn, ids = two_repos
    assert queries.get_file([ids["g/alpha"]], conn, ids["g/beta"], "src/a.c") is None
    assert queries.get_file([ids["g/beta"]], conn, ids["g/beta"], "src/a.c") is not None


def test_index_status_excludes_disallowed_repo(two_repos):
    conn, ids = two_repos
    rows = queries.index_status([ids["g/beta"]], conn)
    assert [r["path_with_namespace"] for r in rows] == ["g/beta"]


def test_empty_allowlist_returns_nothing_not_everything(two_repos):
    conn, _ = two_repos
    assert queries.find_symbol([], conn, "SharedName") == []
    assert queries.search_code([], conn, "alphaword") == []
    assert queries.index_status([], conn) == []


def test_allowlist_must_be_a_sequence_of_ints(two_repos):
    conn, ids = two_repos
    with pytest.raises(TypeError):
        queries.find_symbol("all", conn, "SharedName")
    with pytest.raises(TypeError):
        queries.find_symbol([None], conn, "SharedName")
```

- [ ] **Step 2: Run test to verify it fails**

Run: `pytest tests/store/test_queries.py -v`
Expected: FAIL with `ImportError: cannot import name 'queries'`

- [ ] **Step 3: Write the implementation**

`codeindex/store/queries.py`:

```python
from __future__ import annotations

import sqlite3
from collections.abc import Sequence


def _placeholders(allowed_repo_ids: Sequence[int]) -> tuple[str, list[int]]:
    """Validate the allowlist and render it as SQL placeholders.

    A str is explicitly rejected: it is a Sequence, so accepting it would let
    ``find_symbol("all", ...)`` silently degrade into a per-character filter.
    """
    if isinstance(allowed_repo_ids, (str, bytes)) or not isinstance(
        allowed_repo_ids, (list, tuple, set, frozenset)
    ):
        raise TypeError(
            "allowed_repo_ids must be a list/tuple/set of int repo ids, "
            f"got {type(allowed_repo_ids).__name__}"
        )
    ids = list(allowed_repo_ids)
    if any(not isinstance(i, int) or isinstance(i, bool) for i in ids):
        raise TypeError("allowed_repo_ids must contain only int repo ids")
    return ",".join("?" for _ in ids), ids


def find_symbol(allowed_repo_ids: Sequence[int], conn: sqlite3.Connection,
                name: str, kind: str | None = None,
                limit: int = 50) -> list[sqlite3.Row]:
    marks, ids = _placeholders(allowed_repo_ids)
    if not ids:
        return []
    sql = (
        "SELECT s.repo_id, r.path_with_namespace, f.path, s.name, s.kind,"
        "       s.line, s.end_line, s.signature, s.scope, s.is_public"
        "  FROM symbols s"
        "  JOIN files f ON f.id = s.file_id"
        "  JOIN repos r ON r.id = s.repo_id"
        f" WHERE s.repo_id IN ({marks}) AND s.name = ?"
    )
    params: list = [*ids, name]
    if kind is not None:
        sql += " AND s.kind = ?"
        params.append(kind)
    sql += " ORDER BY s.is_public DESC, r.path_with_namespace, f.path LIMIT ?"
    params.append(limit)
    return conn.execute(sql, params).fetchall()


def search_code(allowed_repo_ids: Sequence[int], conn: sqlite3.Connection,
                query: str, limit: int = 50) -> list[sqlite3.Row]:
    marks, ids = _placeholders(allowed_repo_ids)
    if not ids:
        return []
    return conn.execute(
        "SELECT f.repo_id, r.path_with_namespace, f.path,"
        "       snippet(files_fts, 1, '[', ']', '…', 16) AS snippet"
        "  FROM files_fts"
        "  JOIN files f ON f.id = files_fts.rowid"
        "  JOIN repos r ON r.id = f.repo_id"
        f" WHERE files_fts MATCH ? AND f.repo_id IN ({marks})"
        "  ORDER BY rank LIMIT ?",
        [query, *ids, limit],
    ).fetchall()


def get_file(allowed_repo_ids: Sequence[int], conn: sqlite3.Connection,
             repo_id: int, path: str) -> sqlite3.Row | None:
    marks, ids = _placeholders(allowed_repo_ids)
    if not ids:
        return None
    return conn.execute(
        "SELECT f.repo_id, r.path_with_namespace, f.path, f.lang, f.size, f.content"
        "  FROM files f JOIN repos r ON r.id = f.repo_id"
        f" WHERE f.repo_id = ? AND f.path = ? AND f.repo_id IN ({marks})",
        [repo_id, path, *ids],
    ).fetchone()


def index_status(allowed_repo_ids: Sequence[int],
                 conn: sqlite3.Connection) -> list[sqlite3.Row]:
    marks, ids = _placeholders(allowed_repo_ids)
    if not ids:
        return []
    return conn.execute(
        "SELECT r.id AS repo_id, r.path_with_namespace, r.last_indexed_sha,"
        "       r.last_indexed_at,"
        "       (SELECT COUNT(*) FROM files   WHERE repo_id = r.id) AS files,"
        "       (SELECT COUNT(*) FROM symbols WHERE repo_id = r.id) AS symbols,"
        "       (SELECT COUNT(*) FROM index_errors WHERE repo_id = r.id) AS errors"
        "  FROM repos r"
        f" WHERE r.id IN ({marks})"
        "  ORDER BY r.path_with_namespace",
        ids,
    ).fetchall()
```

- [ ] **Step 4: Run tests**

Run: `pytest tests/store/test_queries.py -v`
Expected: 7 passed

- [ ] **Step 5: Commit**

```bash
git add codeindex/store/queries.py tests/store/test_queries.py
git commit -m "feat: add allowlist-gated read queries with enforcement test"
```

---

### Task 5: File filters and language detection

**Files:**
- Create: `codeindex/parse/__init__.py`
- Create: `codeindex/parse/filters.py`
- Test: `tests/parse/__init__.py`, `tests/parse/test_filters.py`

**Interfaces:**
- Consumes: `codeindex.config.DEFAULT_EXCLUDE_DIRS`.
- Produces: `is_binary(data: bytes) -> bool`; `detect_lang(path: str) -> str | None`; `should_index(path, size, data, *, max_bytes, exclude_dirs) -> bool`.

- [ ] **Step 1: Write the failing test**

`tests/parse/test_filters.py`:

```python
from codeindex.config import DEFAULT_EXCLUDE_DIRS
from codeindex.parse import filters

KW = {"max_bytes": 1000, "exclude_dirs": DEFAULT_EXCLUDE_DIRS}


def test_is_binary_detects_null_byte():
    assert filters.is_binary(b"abc\x00def")
    assert not filters.is_binary(b"plain text")


def test_is_binary_only_scans_first_8k():
    assert not filters.is_binary(b"a" * 8192 + b"\x00")


def test_detect_lang():
    assert filters.detect_lang("src/a.cpp") == "cpp"
    assert filters.detect_lang("src/a.h") == "cpp"
    assert filters.detect_lang("src/a.c") == "c"
    assert filters.detect_lang("s.py") == "python"
    assert filters.detect_lang("README.md") == "markdown"
    assert filters.detect_lang("a.bin") is None


def test_should_index_accepts_source():
    assert filters.should_index("src/main.cpp", 100, b"int main(){}", **KW)


def test_should_index_rejects_oversize():
    assert not filters.should_index("src/main.cpp", 2000, b"x", **KW)


def test_should_index_rejects_binary():
    assert not filters.should_index("src/main.cpp", 10, b"\x00\x01", **KW)


def test_should_index_rejects_unknown_extension():
    assert not filters.should_index("assets/logo.bin", 10, b"x", **KW)


def test_should_index_rejects_excluded_dirs():
    for path in (
        "third_party/zlib/zlib.c",
        "src/vendor/x.c",
        "build/gen.cpp",
        "x64/Release/thing.c",
        "node_modules/pkg/i.js",
    ):
        assert not filters.should_index(path, 10, b"x", **KW), path


def test_excluded_dir_matches_whole_component_only():
    """'outbound' must not be excluded just because 'out' is on the list."""
    assert filters.should_index("src/outbound/net.c", 10, b"x", **KW)
```

- [ ] **Step 2: Run test to verify it fails**

Run: `pytest tests/parse/test_filters.py -v`
Expected: FAIL with `ModuleNotFoundError: No module named 'codeindex.parse'`

- [ ] **Step 3: Write the implementation**

`codeindex/parse/__init__.py`: empty file.
`tests/parse/__init__.py`: empty file.

`codeindex/parse/filters.py`:

```python
from __future__ import annotations

from collections.abc import Sequence
from pathlib import PurePosixPath

EXTENSION_LANG = {
    ".c": "c", ".h": "cpp", ".hpp": "cpp", ".hxx": "cpp", ".inl": "cpp",
    ".cc": "cpp", ".cpp": "cpp", ".cxx": "cpp",
    ".py": "python", ".cs": "csharp",
    ".ts": "typescript", ".tsx": "typescript",
    ".js": "javascript", ".jsx": "javascript",
    ".md": "markdown", ".txt": "text",
}

HEADER_EXTENSIONS = frozenset({".h", ".hpp", ".hxx", ".inl"})


def is_binary(data: bytes) -> bool:
    return b"\x00" in data[:8192]


def detect_lang(path: str) -> str | None:
    return EXTENSION_LANG.get(PurePosixPath(path).suffix.lower())


def should_index(path: str, size: int, data: bytes, *,
                 max_bytes: int, exclude_dirs: Sequence[str]) -> bool:
    if size > max_bytes:
        return False
    if detect_lang(path) is None:
        return False
    # Match whole path components so 'outbound' is not caught by 'out'.
    excluded = {d.lower() for d in exclude_dirs}
    parts = [p.lower() for p in PurePosixPath(path).parts[:-1]]
    if excluded.intersection(parts):
        return False
    return not is_binary(data)
```

- [ ] **Step 4: Run tests**

Run: `pytest tests/parse/test_filters.py -v`
Expected: 9 passed

- [ ] **Step 5: Commit**

```bash
git add codeindex/parse tests/parse
git commit -m "feat: add file filters and language detection"
```

---

### Task 6: ctags symbol extraction

**Files:**
- Create: `codeindex/parse/ctags.py`
- Test: `tests/parse/test_ctags.py`
- Test fixtures: `tests/fixtures/ctags/decoder.h`, `tests/fixtures/ctags/decoder.cpp`

**Interfaces:**
- Consumes: `codeindex.parse.filters.HEADER_EXTENSIONS`.
- Produces: `CtagsUnavailable` exception; `extract_symbols(root: Path, rel_paths: list[str]) -> dict[str, list[dict]]` mapping relative path → symbol dicts with keys `name, kind, line, end_line, signature, scope, is_public`; `is_public_symbol(path, scope, file_restricted) -> bool`.

`is_public_symbol` implements the spec's definition: declared in a header **and** not inside a `detail`/`internal`/`impl`/`anonymous` scope, **or** a non-`static` symbol in a `.c`/`.cpp`. ctags reports file-restricted (`static`) symbols via the `file` JSON field when `--fields=+f` is passed.

- [ ] **Step 1: Write the failing test**

`tests/fixtures/ctags/decoder.h`:

```cpp
#pragma once
namespace eal {
struct DecoderConfig {
    int max_frames;
};
int DecodeFrame(const char* buf, int len);
namespace detail {
int ScratchBuffer(int n);
}
}
```

`tests/fixtures/ctags/decoder.cpp`:

```cpp
#include "decoder.h"
static int HelperOnly(int x) { return x + 1; }
int PublicImpl(int x) { return HelperOnly(x); }
```

`tests/parse/test_ctags.py`:

```python
import shutil
from pathlib import Path

import pytest

from codeindex.parse import ctags

FIXTURES = Path(__file__).parent.parent / "fixtures" / "ctags"
pytestmark = pytest.mark.skipif(
    shutil.which("ctags") is None, reason="universal-ctags not installed"
)


@pytest.fixture(scope="module")
def parsed():
    return ctags.extract_symbols(FIXTURES, ["decoder.h", "decoder.cpp"])


def _by_name(symbols):
    return {s["name"]: s for s in symbols}


def test_extracts_header_symbols(parsed):
    names = _by_name(parsed["decoder.h"])
    assert "DecodeFrame" in names
    assert "DecoderConfig" in names
    assert names["DecodeFrame"]["kind"] in ("function", "prototype")
    assert names["DecodeFrame"]["line"] > 0


def test_header_symbol_is_public(parsed):
    assert _by_name(parsed["decoder.h"])["DecodeFrame"]["is_public"] == 1


def test_detail_namespace_is_not_public(parsed):
    scratch = _by_name(parsed["decoder.h"])["ScratchBuffer"]
    assert "detail" in (scratch["scope"] or "")
    assert scratch["is_public"] == 0


def test_static_function_in_cpp_is_not_public(parsed):
    assert _by_name(parsed["decoder.cpp"])["HelperOnly"]["is_public"] == 0


def test_non_static_function_in_cpp_is_public(parsed):
    assert _by_name(parsed["decoder.cpp"])["PublicImpl"]["is_public"] == 1


def test_signature_captured(parsed):
    sig = _by_name(parsed["decoder.h"])["DecodeFrame"]["signature"]
    assert sig is not None and "const char" in sig


def test_missing_files_do_not_raise(tmp_path):
    assert ctags.extract_symbols(tmp_path, ["nope.c"]) == {}


def test_is_public_symbol_rules():
    assert ctags.is_public_symbol("a/b.h", None, False) is True
    assert ctags.is_public_symbol("a/b.h", "eal::detail", False) is False
    assert ctags.is_public_symbol("a/b.cpp", None, False) is True
    assert ctags.is_public_symbol("a/b.cpp", None, True) is False
```

- [ ] **Step 2: Run test to verify it fails**

Run: `pytest tests/parse/test_ctags.py -v`
Expected: FAIL with `ImportError: cannot import name 'ctags'`

- [ ] **Step 3: Write the implementation**

`codeindex/parse/ctags.py`:

```python
from __future__ import annotations

import json
import shutil
import subprocess
from pathlib import Path, PurePosixPath

from .filters import HEADER_EXTENSIONS

PRIVATE_SCOPES = frozenset({"detail", "internal", "impl", "anonymous"})

CTAGS_ARGS = [
    "--output-format=json",
    # n=line, K=long kind, S=signature, s=scope, e=end line,
    # f=file-limited visibility (i.e. `static`), surfaced as JSON key "file".
    "--fields=+nKSsef",
    # Universal Ctags ships the C/C++ `prototype` kind DISABLED by default.
    # Without these, header-only declarations (`int Foo(int);` with no body)
    # produce no tag at all — which would drop most of a C/C++ public API.
    "--kinds-c=+p", "--kinds-c++=+p",
    "-L", "-",   # read the file list from stdin
    "-f", "-",   # write tags to stdout
]


class CtagsUnavailable(RuntimeError):
    """universal-ctags is not installed or is the wrong implementation."""


def is_public_symbol(path: str, scope: str | None, file_restricted: bool) -> bool:
    if scope:
        for part in (p.strip() for p in scope.replace("::", ".").split(".")):
            # A C++ anonymous namespace has internal linkage, but ctags reports
            # it as a generated identifier like "__anond398a7c10111" — never the
            # literal "anonymous" — and emits no "file": true for its members.
            if part in PRIVATE_SCOPES or part.startswith("__anon"):
                return False
    if PurePosixPath(path).suffix.lower() in HEADER_EXTENSIONS:
        return True
    return not file_restricted


def extract_symbols(root: Path, rel_paths: list[str]) -> dict[str, list[dict]]:
    """Run ctags over rel_paths (relative to root); return path -> symbols."""
    if not rel_paths:
        return {}
    exe = shutil.which("ctags")
    if exe is None:
        raise CtagsUnavailable(
            "ctags not found on PATH — install universal-ctags on the index host"
        )

    existing = [p for p in rel_paths if (root / p).is_file()]
    if not existing:
        return {}

    proc = subprocess.run(
        [exe, *CTAGS_ARGS],
        input="\n".join(existing),
        cwd=root,
        capture_output=True,
        text=True,
        encoding="utf-8",
        errors="replace",
    )
    if proc.returncode != 0 and not proc.stdout:
        raise CtagsUnavailable(f"ctags failed: {proc.stderr.strip()[:500]}")

    results: dict[str, list[dict]] = {}
    for line in proc.stdout.splitlines():
        if not line.strip():
            continue
        try:
            entry = json.loads(line)
        except json.JSONDecodeError:
            continue
        if entry.get("_type") != "tag":
            continue

        path = PurePosixPath(entry["path"].replace("\\", "/")).as_posix()
        scope = entry.get("scope")
        results.setdefault(path, []).append({
            "name": entry["name"],
            "kind": entry.get("kind", "unknown"),
            "line": int(entry.get("line", 0)),
            "end_line": int(entry["end"]) if entry.get("end") else None,
            "signature": entry.get("signature"),
            "scope": scope,
            "is_public": int(is_public_symbol(path, scope, bool(entry.get("file", False)))),
        })
    return results
```

- [ ] **Step 4: Run tests**

Run: `pytest tests/parse/test_ctags.py -v`
Expected: 8 passed. If they skip, install ctags first: `sudo apt install universal-ctags` (Linux) or `winget install UniversalCtags.Ctags` (Windows).

- [ ] **Step 5: Commit**

```bash
git add codeindex/parse/ctags.py tests/parse/test_ctags.py tests/fixtures/ctags
git commit -m "feat: add ctags symbol extraction with public-symbol rules"
```

---

### Task 7: Include extraction

**Files:**
- Create: `codeindex/parse/includes.py`
- Test: `tests/parse/test_includes.py`

**Interfaces:**
- Consumes: nothing.
- Produces: `extract_includes(content: str) -> list[dict]` with keys `raw` (the path inside the quotes/brackets) and `is_angle` (`1` for `<...>`, `0` for `"..."`). Duplicates removed, order preserved.

- [ ] **Step 1: Write the failing test**

`tests/parse/test_includes.py`:

```python
from codeindex.parse.includes import extract_includes

SOURCE = '''
#include <stdio.h>
#include "eal/decoder.h"
  #  include   <vector>
#include "eal/decoder.h"
// #include "commented_out.h"
/* #include "block_commented.h" */
const char* s = "#include \\"in_a_string.h\\"";
#include "trailing.h"   // note
'''


def test_extracts_angle_and_quote_includes():
    got = extract_includes(SOURCE)
    assert {"raw": "stdio.h", "is_angle": 1} in got
    assert {"raw": "eal/decoder.h", "is_angle": 0} in got


def test_tolerates_whitespace_variants():
    assert {"raw": "vector", "is_angle": 1} in extract_includes(SOURCE)


def test_deduplicates_preserving_order():
    raws = [i["raw"] for i in extract_includes(SOURCE)]
    assert raws.count("eal/decoder.h") == 1
    assert raws.index("stdio.h") < raws.index("eal/decoder.h")


def test_ignores_commented_and_quoted_includes():
    raws = [i["raw"] for i in extract_includes(SOURCE)]
    assert "commented_out.h" not in raws
    assert "block_commented.h" not in raws
    assert "in_a_string.h" not in raws


def test_handles_trailing_comment():
    assert {"raw": "trailing.h", "is_angle": 0} in extract_includes(SOURCE)


def test_empty_source():
    assert extract_includes("") == []
```

- [ ] **Step 2: Run test to verify it fails**

Run: `pytest tests/parse/test_includes.py -v`
Expected: FAIL with `ModuleNotFoundError: No module named 'codeindex.parse.includes'`

- [ ] **Step 3: Write the implementation**

`codeindex/parse/includes.py`:

```python
from __future__ import annotations

import re

# Anchored at line start (allowing leading whitespace) so that #include
# appearing inside a string literal or after code on the same line is ignored.
INCLUDE_RE = re.compile(
    r'^[ \t]*#[ \t]*include[ \t]*(?:<(?P<angle>[^>\r\n]+)>|"(?P<quote>[^"\r\n]+)")',
    re.MULTILINE,
)

LINE_COMMENT_RE = re.compile(r'^[ \t]*(?://|/\*|\*)')


def extract_includes(content: str) -> list[dict]:
    seen: set[tuple[str, int]] = set()
    out: list[dict] = []
    for line in content.splitlines():
        if LINE_COMMENT_RE.match(line):
            continue
        match = INCLUDE_RE.match(line)
        if match is None:
            continue
        angle = match.group("angle")
        raw = angle if angle is not None else match.group("quote")
        key = (raw, 1 if angle is not None else 0)
        if key in seen:
            continue
        seen.add(key)
        out.append({"raw": key[0], "is_angle": key[1]})
    return out
```

- [ ] **Step 4: Run tests**

Run: `pytest tests/parse/test_includes.py -v`
Expected: 6 passed

- [ ] **Step 5: Commit**

```bash
git add codeindex/parse/includes.py tests/parse/test_includes.py
git commit -m "feat: add #include extraction"
```

---

### Task 8: GitLab project enumeration

**Files:**
- Create: `codeindex/gitlab.py`
- Test: `tests/test_gitlab.py`

**Interfaces:**
- Consumes: `codeindex.config.GitLabConfig`.
- Produces: `Project` dataclass (`gitlab_id: int`, `path_with_namespace: str`, `default_branch: str`, `http_url: str`); `GitLabError`; `list_projects(cfg: GitLabConfig, *, client=None) -> list[Project]`, paginating `GET /api/v4/projects` with `membership=false&simple=true&archived=false&per_page=100` using the service token. Projects with a null `default_branch` (empty repos) are skipped.

- [ ] **Step 1: Write the failing test**

`tests/test_gitlab.py`:

```python
import httpx
import pytest

from codeindex.config import GitLabConfig
from codeindex.gitlab import list_projects, GitLabError

CFG = GitLabConfig(url="https://gl.test", token="tok")


def _project(pid, ns, branch="main"):
    return {
        "id": pid, "path_with_namespace": ns, "default_branch": branch,
        "http_url_to_repo": f"https://gl.test/{ns}.git",
    }


def _client(handler):
    return httpx.Client(transport=httpx.MockTransport(handler))


def test_paginates_until_empty_page():
    pages = {
        "1": [_project(1, "g/a"), _project(2, "g/b")],
        "2": [_project(3, "g/c")],
        "3": [],
    }
    seen = []

    def handler(request):
        page = dict(request.url.params).get("page", "1")
        seen.append(page)
        return httpx.Response(200, json=pages[page])

    projects = list_projects(CFG, client=_client(handler))
    assert [p.gitlab_id for p in projects] == [1, 2, 3]
    assert seen == ["1", "2", "3"]


def test_sends_private_token_header():
    captured = {}

    def handler(request):
        captured.update(request.headers)
        return httpx.Response(200, json=[])

    list_projects(CFG, client=_client(handler))
    assert captured["private-token"] == "tok"


def test_skips_projects_without_default_branch():
    def handler(request):
        if dict(request.url.params).get("page", "1") == "1":
            return httpx.Response(200, json=[_project(1, "g/a", None), _project(2, "g/b")])
        return httpx.Response(200, json=[])

    projects = list_projects(CFG, client=_client(handler))
    assert [p.gitlab_id for p in projects] == [2]


def test_raises_on_auth_failure():
    def handler(request):
        return httpx.Response(401, json={"message": "401 Unauthorized"})

    with pytest.raises(GitLabError, match="401"):
        list_projects(CFG, client=_client(handler))
```

- [ ] **Step 2: Run test to verify it fails**

Run: `pytest tests/test_gitlab.py -v`
Expected: FAIL with `ModuleNotFoundError: No module named 'codeindex.gitlab'`

- [ ] **Step 3: Write the implementation**

`codeindex/gitlab.py`:

```python
from __future__ import annotations

from dataclasses import dataclass

import httpx

from .config import GitLabConfig

PER_PAGE = 100
MAX_PAGES = 1000


class GitLabError(RuntimeError):
    """GitLab returned an error or unusable response."""


@dataclass(frozen=True)
class Project:
    gitlab_id: int
    path_with_namespace: str
    default_branch: str
    http_url: str


def list_projects(cfg: GitLabConfig, *,
                  client: httpx.Client | None = None) -> list[Project]:
    owns_client = client is None
    client = client or httpx.Client(timeout=30.0)
    projects: list[Project] = []
    try:
        for page in range(1, MAX_PAGES + 1):
            response = client.get(
                f"{cfg.url}/api/v4/projects",
                params={
                    "membership": "false", "simple": "true",
                    "archived": "false", "per_page": PER_PAGE, "page": page,
                },
                headers={"PRIVATE-TOKEN": cfg.token},
            )
            if response.status_code != 200:
                raise GitLabError(
                    f"GET /projects returned {response.status_code}: "
                    f"{response.text[:200]}"
                )
            batch = response.json()
            if not batch:
                break
            for item in batch:
                if not item.get("default_branch"):
                    continue  # empty repo, nothing to index
                projects.append(Project(
                    gitlab_id=int(item["id"]),
                    path_with_namespace=item["path_with_namespace"],
                    default_branch=item["default_branch"],
                    http_url=item["http_url_to_repo"],
                ))
    finally:
        if owns_client:
            client.close()
    return projects
```

- [ ] **Step 4: Run tests**

Run: `pytest tests/test_gitlab.py -v`
Expected: 4 passed

- [ ] **Step 5: Commit**

```bash
git add codeindex/gitlab.py tests/test_gitlab.py
git commit -m "feat: add GitLab project enumeration"
```

---

### Task 9: Git mirrors, worktrees and change detection

**Files:**
- Create: `codeindex/mirror.py`
- Test: `tests/test_mirror.py`

**Interfaces:**
- Consumes: `codeindex.gitlab.Project`, `codeindex.config.IndexConfig`.
- Produces:
  - `GitError`
  - `Change` dataclass: `status: str` (`"A"`, `"M"`, `"D"`), `path: str`
  - `mirror_path(index_cfg, gitlab_id) -> Path`, `tree_path(index_cfg, gitlab_id) -> Path`
  - `ensure_mirror(index_cfg, project, *, clone_url) -> Path` — clone `--mirror` if absent, else `fetch --prune`
  - `head_sha(mirror: Path, branch: str) -> str`
  - `is_ancestor(mirror: Path, old: str, new: str) -> bool`
  - `changed_files(mirror: Path, old_sha: str | None, new_sha: str) -> list[Change]` — full tree listing as `"A"` when `old_sha` is `None` or not an ancestor
  - `sync_worktree(index_cfg, gitlab_id, mirror: Path, sha: str) -> Path`
  - `blob_shas(mirror: Path, sha: str) -> dict[str, str]`

Renames are emitted as delete + add, per spec — `--no-renames` on the diff makes git do this for us.

- [ ] **Step 1: Write the failing test**

`tests/test_mirror.py`:

```python
import subprocess
from pathlib import Path

import pytest

from codeindex.config import IndexConfig
from codeindex import mirror
from codeindex.gitlab import Project


def git(cwd, *args):
    subprocess.run(["git", *args], cwd=cwd, check=True,
                   capture_output=True, text=True)


@pytest.fixture
def origin(tmp_path):
    """A real git repo standing in for GitLab."""
    repo = tmp_path / "origin"
    repo.mkdir()
    git(repo, "init", "-b", "main")
    git(repo, "config", "user.email", "t@t.test")
    git(repo, "config", "user.name", "Test")
    (repo / "a.c").write_text("int a(void){return 1;}\n")
    git(repo, "add", "-A")
    git(repo, "commit", "-m", "first")
    return repo


@pytest.fixture
def cfg(tmp_path):
    return IndexConfig(data_dir=tmp_path / "data", db_path=tmp_path / "data" / "i.db")


@pytest.fixture
def project():
    return Project(gitlab_id=42, path_with_namespace="g/a",
                   default_branch="main", http_url="https://unused")


def test_ensure_mirror_clones_then_fetches(cfg, project, origin):
    m1 = mirror.ensure_mirror(cfg, project, clone_url=str(origin))
    assert (m1 / "HEAD").exists()
    first = mirror.head_sha(m1, "main")

    (origin / "b.c").write_text("int b(void){return 2;}\n")
    git(origin, "add", "-A")
    git(origin, "commit", "-m", "second")

    m2 = mirror.ensure_mirror(cfg, project, clone_url=str(origin))
    assert m2 == m1
    assert mirror.head_sha(m2, "main") != first


def test_changed_files_first_index_lists_everything(cfg, project, origin):
    m = mirror.ensure_mirror(cfg, project, clone_url=str(origin))
    changes = mirror.changed_files(m, None, mirror.head_sha(m, "main"))
    assert changes == [mirror.Change(status="A", path="a.c")]


def test_changed_files_reports_add_modify_delete(cfg, project, origin):
    m = mirror.ensure_mirror(cfg, project, clone_url=str(origin))
    old = mirror.head_sha(m, "main")

    (origin / "a.c").write_text("int a(void){return 99;}\n")
    (origin / "c.c").write_text("int c(void){return 3;}\n")
    git(origin, "add", "-A")
    git(origin, "commit", "-m", "edit+add")
    git(origin, "rm", "-q", "a.c")
    (origin / "d.c").write_text("int d(void){return 4;}\n")
    git(origin, "add", "-A")
    git(origin, "commit", "-m", "delete+add")

    m = mirror.ensure_mirror(cfg, project, clone_url=str(origin))
    new = mirror.head_sha(m, "main")
    got = {(c.status, c.path) for c in mirror.changed_files(m, old, new)}
    assert got == {("D", "a.c"), ("A", "c.c"), ("A", "d.c")}


def test_force_push_falls_back_to_full_listing(cfg, project, origin):
    m = mirror.ensure_mirror(cfg, project, clone_url=str(origin))
    orphan = mirror.head_sha(m, "main")

    git(origin, "checkout", "-q", "--orphan", "fresh")
    git(origin, "rm", "-q", "-rf", ".")
    (origin / "z.c").write_text("int z(void){return 0;}\n")
    git(origin, "add", "-A")
    git(origin, "commit", "-m", "rewritten")
    git(origin, "branch", "-M", "fresh", "main")

    m = mirror.ensure_mirror(cfg, project, clone_url=str(origin))
    new = mirror.head_sha(m, "main")
    assert mirror.is_ancestor(m, orphan, new) is False
    assert mirror.changed_files(m, orphan, new) == [mirror.Change(status="A", path="z.c")]


def test_sync_worktree_materializes_files(cfg, project, origin):
    m = mirror.ensure_mirror(cfg, project, clone_url=str(origin))
    tree = mirror.sync_worktree(cfg, project.gitlab_id, m, mirror.head_sha(m, "main"))
    assert (tree / "a.c").read_text().startswith("int a")


def test_sync_worktree_updates_existing(cfg, project, origin):
    m = mirror.ensure_mirror(cfg, project, clone_url=str(origin))
    mirror.sync_worktree(cfg, project.gitlab_id, m, mirror.head_sha(m, "main"))

    (origin / "e.c").write_text("int e(void){return 5;}\n")
    git(origin, "add", "-A")
    git(origin, "commit", "-m", "third")

    m = mirror.ensure_mirror(cfg, project, clone_url=str(origin))
    tree = mirror.sync_worktree(cfg, project.gitlab_id, m, mirror.head_sha(m, "main"))
    assert (tree / "e.c").exists()


def test_blob_shas_maps_every_path(cfg, project, origin):
    m = mirror.ensure_mirror(cfg, project, clone_url=str(origin))
    shas = mirror.blob_shas(m, mirror.head_sha(m, "main"))
    assert set(shas) == {"a.c"}
    assert len(shas["a.c"]) == 40
```

- [ ] **Step 2: Run test to verify it fails**

Run: `pytest tests/test_mirror.py -v`
Expected: FAIL with `ModuleNotFoundError: No module named 'codeindex.mirror'`

- [ ] **Step 3: Write the implementation**

`codeindex/mirror.py`:

```python
from __future__ import annotations

import subprocess
from dataclasses import dataclass
from pathlib import Path

from .config import IndexConfig
from .gitlab import Project


class GitError(RuntimeError):
    """A git command failed."""


@dataclass(frozen=True)
class Change:
    status: str  # "A" | "M" | "D"
    path: str


def _git(cwd: Path, *args: str) -> str:
    proc = subprocess.run(
        ["git", *args], cwd=cwd, capture_output=True,
        text=True, encoding="utf-8", errors="replace",
    )
    if proc.returncode != 0:
        raise GitError(f"git {' '.join(args)} failed: {proc.stderr.strip()[:500]}")
    return proc.stdout


def mirror_path(index_cfg: IndexConfig, gitlab_id: int) -> Path:
    return index_cfg.mirrors_dir / f"{gitlab_id}.git"


def tree_path(index_cfg: IndexConfig, gitlab_id: int) -> Path:
    return index_cfg.trees_dir / str(gitlab_id)


def ensure_mirror(index_cfg: IndexConfig, project: Project, *,
                  clone_url: str) -> Path:
    path = mirror_path(index_cfg, project.gitlab_id)
    if path.exists():
        _git(path, "fetch", "--prune", "--quiet", "origin",
             "+refs/heads/*:refs/heads/*")
        return path
    path.parent.mkdir(parents=True, exist_ok=True)
    _git(path.parent, "clone", "--mirror", "--quiet", clone_url, str(path))
    return path


def head_sha(mirror: Path, branch: str) -> str:
    return _git(mirror, "rev-parse", branch).strip()


def is_ancestor(mirror: Path, old: str, new: str) -> bool:
    proc = subprocess.run(
        ["git", "merge-base", "--is-ancestor", old, new],
        cwd=mirror, capture_output=True, text=True,
    )
    return proc.returncode == 0


# -z is mandatory on every command whose output we parse as paths. Without it,
# git applies core.quotePath=true and returns any path containing non-ASCII
# bytes, a quote, a backslash, or a control char C-escaped and double-quoted --
# e.g. файл.c comes back as the literal "\321\204\320\260\320\271\320\273.c",
# which would then be stored verbatim as the indexed path.
def _full_listing(mirror: Path, sha: str) -> list[Change]:
    out = _git(mirror, "ls-tree", "-r", "--name-only", "-z", sha)
    return [Change(status="A", path=p) for p in out.split("\0") if p]


def changed_files(mirror: Path, old_sha: str | None, new_sha: str) -> list[Change]:
    if old_sha is None or not is_ancestor(mirror, old_sha, new_sha):
        # First index, or history was rewritten: reindex the whole tree.
        return _full_listing(mirror, new_sha)

    out = _git(mirror, "diff", "--name-status", "--no-renames", "-z",
               f"{old_sha}..{new_sha}")
    # With -z the output is flat alternating NUL-terminated fields
    # (<status>\0<path>\0...), NOT the status<TAB>path lines of the plain form.
    # --no-renames guarantees no three-field rename records.
    fields = [f for f in out.split("\0") if f]
    changes: list[Change] = []
    for status, path in zip(fields[0::2], fields[1::2]):
        if status[0] in ("A", "M", "D") and path:
            changes.append(Change(status=status[0], path=path))
    return changes


def sync_worktree(index_cfg: IndexConfig, gitlab_id: int,
                  mirror: Path, sha: str) -> Path:
    tree = tree_path(index_cfg, gitlab_id)
    if tree.exists():
        _git(tree, "checkout", "--force", "--detach", sha)
    else:
        tree.parent.mkdir(parents=True, exist_ok=True)
        _git(mirror, "worktree", "add", "--force", "--detach", str(tree), sha)
    return tree


def blob_shas(mirror: Path, sha: str) -> dict[str, str]:
    """Map every path in the tree to its blob sha, in one git call."""
    out = _git(mirror, "ls-tree", "-r", "-z", sha)
    result: dict[str, str] = {}
    for record in out.split("\0"):
        if not record:
            continue
        meta, _, path = record.partition("\t")
        parts = meta.split()
        if len(parts) >= 3 and path:
            result[path] = parts[2]
    return result
```

- [ ] **Step 4: Run tests**

Run: `pytest tests/test_mirror.py -v`
Expected: 7 passed

- [ ] **Step 5: Commit**

```bash
git add codeindex/mirror.py tests/test_mirror.py
git commit -m "feat: add git mirror, worktree sync and change detection"
```

---

### Task 10: Repo indexing orchestration

**Files:**
- Create: `codeindex/worker.py`
- Test: `tests/test_worker.py`

**Interfaces:**
- Consumes: everything above.
- Produces: `IndexResult` dataclass (`repo_id: int`, `indexed: int`, `deleted: int`, `skipped: int`, `errors: int`, `sha: str`, `timed_out: bool`); `index_repo(conn, index_cfg, project, mirror_path, tree, new_sha, old_sha, *, now=None) -> IndexResult`.

`last_indexed_sha` advances **only after every changed file for the repo has been committed**, so a crash mid-repo replays the same diff on restart. A file that fails to parse is recorded in `index_errors` and skipped; it must never abort the repo. Parse work stops when the per-repo time budget is exceeded, leaving `last_indexed_sha` untouched so the next pass resumes.

- [ ] **Step 1: Write the failing test**

`tests/test_worker.py`:

```python
import subprocess
from pathlib import Path

import pytest

from codeindex.config import IndexConfig
from codeindex.gitlab import Project
from codeindex.store.db import open_db
from codeindex.store import writes, queries
from codeindex import mirror, worker


def git(cwd, *args):
    subprocess.run(["git", *args], cwd=cwd, check=True, capture_output=True, text=True)


@pytest.fixture
def origin(tmp_path):
    repo = tmp_path / "origin"
    repo.mkdir()
    git(repo, "init", "-b", "main")
    git(repo, "config", "user.email", "t@t.test")
    git(repo, "config", "user.name", "Test")
    (repo / "decoder.h").write_text("int DecodeFrame(const char* b, int n);\n")
    (repo / "decoder.c").write_text('#include "decoder.h"\nint DecodeFrame(const char* b, int n){return n;}\n')
    (repo / "build").mkdir()
    (repo / "build" / "gen.c").write_text("int gen(void){return 0;}\n")
    (repo / "logo.bin").write_bytes(b"\x00\x01\x02")
    git(repo, "add", "-A")
    git(repo, "commit", "-m", "first")
    return repo


@pytest.fixture
def env(tmp_path, origin):
    cfg = IndexConfig(data_dir=tmp_path / "data", db_path=tmp_path / "data" / "i.db")
    conn = open_db(cfg.db_path)
    project = Project(gitlab_id=42, path_with_namespace="g/eal",
                      default_branch="main", http_url="https://unused")
    repo_id = writes.upsert_repo(
        conn, gitlab_id=project.gitlab_id,
        path_with_namespace=project.path_with_namespace,
        default_branch=project.default_branch, http_url=project.http_url,
    )
    m = mirror.ensure_mirror(cfg, project, clone_url=str(origin))
    return conn, cfg, project, repo_id, m, origin


def _run(env, old_sha=None):
    conn, cfg, project, repo_id, m, _ = env
    m = mirror.ensure_mirror(cfg, project, clone_url=str(env[5]))
    sha = mirror.head_sha(m, "main")
    tree = mirror.sync_worktree(cfg, project.gitlab_id, m, sha)
    return worker.index_repo(conn, cfg, project, m, tree, sha, old_sha)


def test_indexes_source_and_skips_filtered_files(env):
    result = _run(env)
    conn, _, _, repo_id, _, _ = env
    paths = {r["path"] for r in conn.execute("SELECT path FROM files")}
    assert paths == {"decoder.h", "decoder.c"}
    assert result.indexed == 2
    assert result.skipped == 2  # build/gen.c and logo.bin


def test_symbols_are_queryable_after_index(env):
    _run(env)
    conn, _, _, repo_id, _, _ = env
    rows = queries.find_symbol([repo_id], conn, "DecodeFrame")
    assert len(rows) >= 1
    assert rows[0]["path_with_namespace"] == "g/eal"


def test_includes_are_stored(env):
    _run(env)
    conn = env[0]
    raws = {r["raw"] for r in conn.execute("SELECT raw FROM includes")}
    assert "decoder.h" in raws


def test_last_indexed_sha_advances(env):
    result = _run(env)
    conn, _, _, repo_id, _, _ = env
    row = conn.execute("SELECT last_indexed_sha FROM repos WHERE id = ?",
                       (repo_id,)).fetchone()
    assert row["last_indexed_sha"] == result.sha


def test_incremental_pass_applies_delete(env):
    first = _run(env)
    conn, cfg, project, repo_id, _, origin = env
    git(origin, "rm", "-q", "decoder.c")
    git(origin, "commit", "-m", "drop impl")

    second = _run(env, old_sha=first.sha)
    assert second.deleted == 1
    paths = {r["path"] for r in conn.execute("SELECT path FROM files")}
    assert paths == {"decoder.h"}


def test_unreadable_file_is_recorded_and_does_not_abort(env, monkeypatch):
    conn, cfg, project, repo_id, _, _ = env
    real = Path.read_bytes
    def flaky(self):
        if self.name == "decoder.c":
            raise OSError("simulated read failure")
        return real(self)
    monkeypatch.setattr(Path, "read_bytes", flaky)

    result = _run(env)
    assert result.errors == 1
    assert result.indexed == 1  # decoder.h still made it
    errs = conn.execute("SELECT path, stage FROM index_errors").fetchall()
    assert errs[0]["path"] == "decoder.c"


def test_time_budget_stops_work_without_advancing_sha(env, monkeypatch):
    conn, cfg, project, repo_id, _, _ = env
    budget_cfg = IndexConfig(data_dir=cfg.data_dir, db_path=cfg.db_path,
                             repo_time_budget_seconds=0)
    m = mirror.ensure_mirror(budget_cfg, project, clone_url=str(env[5]))
    sha = mirror.head_sha(m, "main")
    tree = mirror.sync_worktree(budget_cfg, project.gitlab_id, m, sha)

    result = worker.index_repo(conn, budget_cfg, project, m, tree, sha, None)
    assert result.timed_out is True
    row = conn.execute("SELECT last_indexed_sha FROM repos WHERE id = ?",
                       (repo_id,)).fetchone()
    assert row["last_indexed_sha"] is None
```

- [ ] **Step 2: Run test to verify it fails**

Run: `pytest tests/test_worker.py -v`
Expected: FAIL with `ModuleNotFoundError: No module named 'codeindex.worker'`

- [ ] **Step 3: Write the implementation**

`codeindex/worker.py`:

```python
from __future__ import annotations

import time
from dataclasses import dataclass
from pathlib import Path

from .config import IndexConfig
from .gitlab import Project
from .mirror import blob_shas, changed_files
from .parse import ctags, filters
from .parse.includes import extract_includes
from .store import writes


@dataclass
class IndexResult:
    repo_id: int
    indexed: int = 0
    deleted: int = 0
    skipped: int = 0
    errors: int = 0
    sha: str = ""
    timed_out: bool = False
    # Distinct from timed_out on purpose: a missing ctags binary is not a
    # timeout, and the CLI reports the two differently to the operator.
    symbols_failed: bool = False


def _repo_id(conn, gitlab_id: int) -> int:
    return conn.execute(
        "SELECT id FROM repos WHERE gitlab_id = ?", (gitlab_id,)
    ).fetchone()["id"]


def index_repo(conn, index_cfg: IndexConfig, project: Project,
               mirror_path: Path, tree: Path, new_sha: str,
               old_sha: str | None, *, now=None) -> IndexResult:
    now = now or time.time
    started = now()
    repo_id = _repo_id(conn, project.gitlab_id)
    result = IndexResult(repo_id=repo_id, sha=new_sha)

    changes = changed_files(mirror_path, old_sha, new_sha)
    shas = blob_shas(mirror_path, new_sha)

    # Deletions first: they are cheap and always safe to apply.
    for change in changes:
        if change.status == "D":
            writes.delete_file(conn, repo_id, change.path)
            result.deleted += 1

    pending = [c for c in changes if c.status in ("A", "M")]
    to_parse: list[str] = []

    for change in pending:
        if now() - started > index_cfg.repo_time_budget_seconds:
            result.timed_out = True
            break

        abs_path = tree / change.path
        try:
            data = abs_path.read_bytes()
        except OSError as exc:
            writes.record_error(conn, repo_id, change.path, "read",
                                str(exc), int(now()))
            result.errors += 1
            continue

        if not filters.should_index(
            change.path, len(data), data,
            max_bytes=index_cfg.max_file_bytes,
            exclude_dirs=index_cfg.exclude_dirs,
        ):
            result.skipped += 1
            continue

        try:
            content = data.decode("utf-8", errors="replace")
            file_id = writes.upsert_file(
                conn, repo_id=repo_id, path=change.path,
                lang=filters.detect_lang(change.path), size=len(data),
                blob_sha=shas.get(change.path, ""), content=content,
            )
            writes.replace_includes(conn, repo_id, file_id,
                                    extract_includes(content))
        except Exception as exc:  # one bad file must not abort the repo
            writes.record_error(conn, repo_id, change.path, "store",
                                repr(exc), int(now()))
            result.errors += 1
            continue

        to_parse.append(change.path)
        result.indexed += 1

    _apply_symbols(conn, repo_id, tree, to_parse, result, now)

    # The SHA must not advance if ANY part of the changed set is incomplete.
    # Advancing after a ctags failure would mark these files fully indexed with
    # zero symbols, and they would never reappear in a future diff.
    if not result.timed_out and not result.symbols_failed:
        writes.set_last_indexed(conn, repo_id, new_sha, int(now()))
    return result


def _apply_symbols(conn, repo_id: int, tree: Path, paths: list[str],
                   result: IndexResult, now) -> None:
    if not paths:
        return
    try:
        by_path = ctags.extract_symbols(tree, paths)
    except ctags.CtagsUnavailable as exc:
        writes.record_error(conn, repo_id, None, "ctags", str(exc), int(now()))
        result.errors += 1
        result.symbols_failed = True
        return

    for path in paths:
        row = conn.execute(
            "SELECT id FROM files WHERE repo_id = ? AND path = ?", (repo_id, path)
        ).fetchone()
        if row is None:
            continue
        writes.replace_symbols(conn, repo_id, row["id"], by_path.get(path, []))
```

- [ ] **Step 4: Run tests**

Run: `pytest tests/test_worker.py -v`
Expected: 7 passed

- [ ] **Step 5: Commit**

```bash
git add codeindex/worker.py tests/test_worker.py
git commit -m "feat: add per-repo indexing orchestration"
```

---

### Task 11: CLI — `codeindex index` and `codeindex status`

**Files:**
- Create: `codeindex/cli.py`
- Create: `config.example.yaml`
- Modify: `README.md` (create if absent)
- Test: `tests/test_cli.py`

**Interfaces:**
- Consumes: everything above.
- Produces: `main(argv: list[str] | None = None) -> int`; subcommands `index` (`--config PATH`, optional `--repo PATH_WITH_NAMESPACE` to index one repo) and `status` (`--config PATH`); `preflight() -> str | None` returning an error message when the environment is unusable.

**Preflight is required before any indexing.** `codeindex index` must verify `ctags` is on PATH and is Universal Ctags before it mirrors anything, and exit `4` with an actionable message otherwise. Without this, a host missing ctags produces a complete-looking index with no symbol layer — the exact failure Task 10's `symbols_failed` flag catches at runtime, caught here one step earlier and far more legibly. Exuberant Ctags must be rejected too: it has no `--output-format=json`, so every symbol extraction would fail.

`status` is the only place in Phase 1 that reads through `queries.py`. Since the ACL module does not exist yet, the CLI is a **service-side operator tool** and passes the full set of known repo ids explicitly — it never bypasses the allowlist parameter.

- [ ] **Step 1: Write the failing test**

`tests/test_cli.py`:

```python
import subprocess
import types
from pathlib import Path

import pytest

from codeindex import cli
from codeindex.gitlab import Project


def git(cwd, *args):
    subprocess.run(["git", *args], cwd=cwd, check=True, capture_output=True, text=True)


@pytest.fixture
def origin(tmp_path):
    repo = tmp_path / "origin"
    repo.mkdir()
    git(repo, "init", "-b", "main")
    git(repo, "config", "user.email", "t@t.test")
    git(repo, "config", "user.name", "Test")
    (repo / "a.c").write_text("int Alpha(void){return 1;}\n")
    git(repo, "add", "-A")
    git(repo, "commit", "-m", "first")
    return repo


@pytest.fixture
def config_file(tmp_path, origin):
    path = tmp_path / "config.yaml"
    path.write_text(
        f"gitlab:\n  url: https://gl.test\n  token: tok\n"
        f"index:\n  data_dir: {(tmp_path / 'data').as_posix()}\n"
        f"  db_path: {(tmp_path / 'data' / 'i.db').as_posix()}\n"
    )
    return path


@pytest.fixture
def fake_projects(monkeypatch, origin):
    project = Project(gitlab_id=42, path_with_namespace="g/eal",
                      default_branch="main", http_url=str(origin))
    monkeypatch.setattr(cli, "list_projects", lambda cfg: [project])
    return [project]


def test_index_then_status(config_file, fake_projects, capsys):
    assert cli.main(["index", "--config", str(config_file)]) == 0
    assert cli.main(["status", "--config", str(config_file)]) == 0
    out = capsys.readouterr().out
    assert "g/eal" in out
    assert "files=1" in out


def test_index_single_repo_filter(config_file, fake_projects, capsys):
    assert cli.main(
        ["index", "--config", str(config_file), "--repo", "g/nope"]
    ) == 0
    assert "no repos matched" in capsys.readouterr().out


def test_status_on_empty_index_is_not_an_error(config_file, capsys):
    assert cli.main(["status", "--config", str(config_file)]) == 0
    assert "no repos indexed" in capsys.readouterr().out


def test_bad_config_path_returns_nonzero(capsys):
    assert cli.main(["status", "--config", "/nonexistent/c.yaml"]) == 2


def test_preflight_passes_with_universal_ctags():
    """This host has Universal Ctags installed, so preflight must be silent."""
    assert cli.preflight() is None


def test_index_refuses_to_run_without_ctags(config_file, fake_projects,
                                            monkeypatch, capsys):
    monkeypatch.setattr(cli.shutil, "which", lambda name: None)
    assert cli.main(["index", "--config", str(config_file)]) == 4
    err = capsys.readouterr().err
    assert "ctags not found" in err
    assert "UniversalCtags.Ctags" in err


def test_index_refuses_exuberant_ctags(config_file, fake_projects,
                                       monkeypatch, capsys):
    """Exuberant Ctags has no --output-format=json; it must be rejected."""
    monkeypatch.setattr(cli.shutil, "which", lambda name: "/usr/bin/ctags")
    monkeypatch.setattr(
        cli.subprocess, "run",
        lambda *a, **k: types.SimpleNamespace(stdout="Exuberant Ctags 5.9~svn\n"),
    )
    assert cli.main(["index", "--config", str(config_file)]) == 4
    assert "not Universal Ctags" in capsys.readouterr().err
```

- [ ] **Step 2: Run test to verify it fails**

Run: `pytest tests/test_cli.py -v`
Expected: FAIL with `ModuleNotFoundError: No module named 'codeindex.cli'`

- [ ] **Step 3: Write the implementation**

`codeindex/cli.py`:

```python
from __future__ import annotations

import argparse
import shutil
import subprocess
import sys
import time
from pathlib import Path

from .config import Config, ConfigError
from .gitlab import GitLabError, list_projects
from .mirror import GitError, ensure_mirror, head_sha, sync_worktree
from .store import queries, writes
from .store.db import open_db
from .worker import index_repo


def preflight() -> str | None:
    """Return an error message if the environment cannot index, else None."""
    exe = shutil.which("ctags")
    if exe is None:
        return (
            "ctags not found on PATH. Install Universal Ctags:\n"
            "  Linux:   sudo apt install universal-ctags\n"
            "  Windows: winget install UniversalCtags.Ctags"
        )
    try:
        out = subprocess.run(
            [exe, "--version"], capture_output=True, text=True, timeout=10
        ).stdout
    except (OSError, subprocess.SubprocessError) as exc:
        return f"could not run ctags --version: {exc}"
    if "Universal Ctags" not in out:
        return (
            f"{exe} is not Universal Ctags (reported: {out.splitlines()[0] if out else '?'}).\n"
            "Exuberant Ctags has no --output-format=json and cannot be used."
        )
    return None


def _index(cfg: Config, only: str | None) -> int:
    problem = preflight()
    if problem:
        print(problem, file=sys.stderr)
        return 4

    conn = open_db(cfg.index.db_path)
    projects = list_projects(cfg.gitlab)
    if only:
        projects = [p for p in projects if p.path_with_namespace == only]
    if not projects:
        print("no repos matched")
        return 0

    for project in projects:
        repo_id = writes.upsert_repo(
            conn, gitlab_id=project.gitlab_id,
            path_with_namespace=project.path_with_namespace,
            default_branch=project.default_branch, http_url=project.http_url,
        )
        old = conn.execute(
            "SELECT last_indexed_sha FROM repos WHERE id = ?", (repo_id,)
        ).fetchone()["last_indexed_sha"]

        started = time.time()
        try:
            mirror_dir = ensure_mirror(cfg.index, project,
                                       clone_url=project.http_url)
            sha = head_sha(mirror_dir, project.default_branch)
            if sha == old:
                print(f"{project.path_with_namespace}: up to date")
                continue
            tree = sync_worktree(cfg.index, project.gitlab_id, mirror_dir, sha)
            result = index_repo(conn, cfg.index, project, mirror_dir, tree, sha, old)
        except GitError as exc:
            writes.record_error(conn, repo_id, None, "git", str(exc), int(time.time()))
            print(f"{project.path_with_namespace}: FAILED ({exc})", file=sys.stderr)
            continue

        flags = ""
        if result.timed_out:
            flags += " TIMED-OUT"
        if result.symbols_failed:
            flags += " SYMBOLS-FAILED"
        print(
            f"{project.path_with_namespace}: indexed={result.indexed} "
            f"deleted={result.deleted} skipped={result.skipped} "
            f"errors={result.errors}{flags} "
            f"({time.time() - started:.1f}s)"
        )
    return 0


def _status(cfg: Config) -> int:
    conn = open_db(cfg.index.db_path)
    # Operator tool: pass the full known set explicitly rather than bypassing
    # the allowlist parameter. The ACL module arrives in Phase 2.
    all_ids = [r["id"] for r in conn.execute("SELECT id FROM repos")]
    rows = queries.index_status(all_ids, conn)
    if not rows:
        print("no repos indexed")
        return 0
    for row in rows:
        when = (
            time.strftime("%Y-%m-%d %H:%M", time.localtime(row["last_indexed_at"]))
            if row["last_indexed_at"] else "never"
        )
        sha = (row["last_indexed_sha"] or "-")[:8]
        print(
            f"{row['path_with_namespace']:<40} sha={sha} at={when} "
            f"files={row['files']} symbols={row['symbols']} errors={row['errors']}"
        )
    return 0


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(prog="codeindex")
    sub = parser.add_subparsers(dest="command", required=True)

    p_index = sub.add_parser("index", help="Mirror and index repositories")
    p_index.add_argument("--config", required=True, type=Path)
    p_index.add_argument("--repo", help="Index only this path_with_namespace")

    p_status = sub.add_parser("status", help="Show per-repo index freshness")
    p_status.add_argument("--config", required=True, type=Path)

    args = parser.parse_args(argv)

    try:
        cfg = Config.load(args.config)
    except (ConfigError, OSError) as exc:
        print(f"config error: {exc}", file=sys.stderr)
        return 2

    try:
        if args.command == "index":
            return _index(cfg, args.repo)
        return _status(cfg)
    except GitLabError as exc:
        print(f"gitlab error: {exc}", file=sys.stderr)
        return 3


if __name__ == "__main__":
    raise SystemExit(main())
```

`config.example.yaml`:

```yaml
gitlab:
  url: https://gitlab.internal
  # Prefer the CODEINDEX_GITLAB_TOKEN environment variable over this field.
  # Requires read_api + read_repository scopes.
  token: ""

index:
  data_dir: /var/lib/codeindex
  db_path: /var/lib/codeindex/index.db
  max_file_bytes: 1048576
  repo_time_budget_seconds: 600
  exclude_dirs:
    - third_party
    - vendor
    - node_modules
    - build
    - out
    - x64
    - Debug
    - Release
```

`README.md`:

````markdown
# CodeIndex

Indexes self-hosted GitLab repositories and serves access-controlled code
retrieval to Hermes Agent over MCP.

Design: [`docs/superpowers/specs/2026-07-28-local-code-assistant-design.md`](docs/superpowers/specs/2026-07-28-local-code-assistant-design.md)

## Phase 1 (current) — the indexer

Mirrors every repo, extracts symbols and includes, stores them in SQLite.
No server yet; the MCP surface arrives in Phase 2.

### Requirements

- Python 3.11+
- git
- universal-ctags (`apt install universal-ctags` / `winget install UniversalCtags.Ctags`)

### Setup

```bash
pip install -e ".[dev]"
cp config.example.yaml config.yaml   # then edit
export CODEINDEX_GITLAB_TOKEN=glpat-...
```

### Usage

```bash
codeindex index --config config.yaml
codeindex index --config config.yaml --repo group/one-repo
codeindex status --config config.yaml
```

### Tests

```bash
pytest -v
```
````

- [ ] **Step 4: Run the full suite**

Run: `pytest -v`
Expected: 74 passed. Tasks 1–10 contribute 67 (the plan's original 66 plus five
regression tests added during review — anonymous-namespace classification,
malformed GitLab JSON, non-ASCII path round-trip, `GitError` coverage, and the
ctags-unavailable SHA guard — minus one from an original miscount). This task
adds 7: the 4 CLI tests plus the 3 preflight tests.

No test may be skipped. A skipped ctags test means ctags is missing, which
this task's own preflight check exists to prevent.

- [ ] **Step 5: Commit**

```bash
git add codeindex/cli.py config.example.yaml README.md tests/test_cli.py
git commit -m "feat: add codeindex CLI with index and status commands"
```

---

### Task 12: First real run and measurement

**Files:**
- Create: `docs/phase1-measurements.md`

**Interfaces:**
- Consumes: the whole Phase 1 system.
- Produces: measured numbers that size Phases 2–4 — full index wall-clock time, database size on disk, file/symbol counts, and the top parse-error categories.

This task has no tests: it runs the finished system against the real GitLab instance and records what actually happened. The spec's estimates (70–90k vectors, ~700 MB database, sub-hour rebuild) are projections. Phase 4's design depends on knowing the real figures.

- [ ] **Step 1: Verify prerequisites on the index host**

```bash
python3 --version && git --version && ctags --version | head -1
```
Expected: Python 3.11+, git 2.x, "Universal Ctags". If `ctags` prints "Exuberant Ctags", the wrong package is installed — `--output-format=json` will fail.

- [ ] **Step 2: Run the full index against real GitLab**

```bash
export CODEINDEX_GITLAB_TOKEN=glpat-...
time codeindex index --config config.yaml
```
Expected: one line per repo. Failures print to stderr and do not stop the run.

- [ ] **Step 3: Collect the numbers**

```bash
codeindex status --config config.yaml
du -sh /var/lib/codeindex /var/lib/codeindex/index.db
sqlite3 /var/lib/codeindex/index.db \
  "SELECT COUNT(*) files FROM files;
   SELECT COUNT(*) symbols FROM symbols;
   SELECT COUNT(*) public_symbols FROM symbols WHERE is_public = 1;
   SELECT stage, COUNT(*) n FROM index_errors GROUP BY stage ORDER BY n DESC;"
```

- [ ] **Step 4: Write up the results**

Create `docs/phase1-measurements.md` recording: wall-clock time for the full index, total repos indexed and failed, file count, symbol count, **public symbol count** (this is the Phase 4 vector count estimate), database size, mirrors+worktrees size, and the error breakdown by stage. Note any repo that took disproportionately long.

- [ ] **Step 5: Commit**

```bash
git add docs/phase1-measurements.md
git commit -m "docs: record phase 1 index measurements from first full run"
```

---

## Phase 1 Completion Criteria

- [ ] `pytest -v` passes with no skips on a host with ctags installed
- [ ] `codeindex index` completes a full pass over every GitLab repo
- [ ] `codeindex status` shows a non-null SHA and non-zero symbol count per repo
- [ ] A second `codeindex index` run reports "up to date" for unchanged repos
- [ ] `docs/phase1-measurements.md` records the real numbers

## What Phase 2 Adds

The `acl` module (GitLab PAT → project allowlist, TTL-cached, fail-closed), the `mcpsrv` HTTP MCP
server with the five non-semantic tools, the TLS reverse proxy, and the systemd unit. Phase 2's plan
gets written once Phase 1's measurements are in, since index timing determines whether the webhook
path needs to land at the same time.
