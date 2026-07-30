import pytest
from pathlib import Path
from argus.config import Config, ConfigError

YAML = """
gitlab:
  url: https://gitlab.internal
  token: from-file
index:
  data_dir: /var/lib/argus
  db_path: /var/lib/argus/index.db
"""


def test_loads_yaml(tmp_path):
    p = tmp_path / "c.yaml"
    p.write_text(YAML)
    cfg = Config.load(p)
    assert cfg.gitlab.url == "https://gitlab.internal"
    assert cfg.gitlab.token == "from-file"
    assert cfg.index.db_path == Path("/var/lib/argus/index.db")


def test_env_overrides_token(tmp_path, monkeypatch):
    p = tmp_path / "c.yaml"
    p.write_text(YAML)
    monkeypatch.setenv("ARGUS_GITLAB_TOKEN", "from-env")
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


def test_non_integer_max_file_bytes_raises_config_error(tmp_path):
    p = tmp_path / "c.yaml"
    p.write_text(
        "gitlab:\n  url: https://x\n  token: t\n"
        "index:\n  data_dir: /d\n  db_path: /d/i.db\n  max_file_bytes: not-a-number\n"
    )
    with pytest.raises(ConfigError):
        Config.load(p)


def test_config_file_is_read_as_utf8(tmp_path, monkeypatch):
    # Windows defaults read_text() to cp1252, not UTF-8; a non-ASCII value
    # (here, in the token) must round-trip correctly regardless of locale.
    # Asserting only on the round-trip would pass with the bug reintroduced
    # anywhere the ambient default encoding already is UTF-8 (Linux, or under
    # PEP 686), so assert the encoding is passed explicitly.
    p = tmp_path / "c.yaml"
    p.write_text(
        "gitlab:\n  url: https://x\n  token: tökén-ü\n"
        "index:\n  data_dir: /d\n  db_path: /d/i.db\n",
        encoding="utf-8",
    )

    seen = {}
    real_read_text = Path.read_text

    def spy(self, *args, **kwargs):
        seen["encoding"] = kwargs.get("encoding", "not passed")
        return real_read_text(self, *args, **kwargs)

    monkeypatch.setattr(Path, "read_text", spy)
    cfg = Config.load(p)

    assert seen["encoding"] == "utf-8"
    assert cfg.gitlab.token == "tökén-ü"
