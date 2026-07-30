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
