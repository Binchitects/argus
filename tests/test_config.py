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
