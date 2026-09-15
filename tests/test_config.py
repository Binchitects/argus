import pytest
from pathlib import Path
from argus.config import Config, ConfigError, GitLabConfig

YAML = """
gitlab:
  url: https://gitlab.internal
  token: from-file
index:
  data_dir: /var/lib/argus
  db_path: /var/lib/argus/index.db
"""


def gitlab_yaml(extra: str = "") -> str:
    """A minimal valid config with `extra` spliced into the gitlab block."""
    return ("gitlab:\n  url: https://gitlab.internal\n  token: t\n" + extra
            + "index:\n  data_dir: /d\n  db_path: /d/i.db\n")


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


def test_env_overrides_url(tmp_path, monkeypatch):
    p = tmp_path / "c.yaml"
    p.write_text(YAML)
    monkeypatch.setenv("ARGUS_GITLAB_URL", "https://gitlab.example.internal/")
    assert Config.load(p).gitlab.url == "https://gitlab.example.internal"


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


def test_url_env_overrides_the_config_file(tmp_path, monkeypatch):
    # The shipped container config names a throwaway test GitLab, so this is
    # the setting a real deployment is actually told to change. It used to be
    # read from the file ONLY, which meant ARGUS_GITLAB_URL in .env -- named in
    # the README and present in every sample -- was silently ignored, and
    # "drop-in deployment with only .env edited" could not work.
    p = tmp_path / "c.yaml"
    p.write_text(gitlab_yaml())
    monkeypatch.setenv("ARGUS_GITLAB_URL", "https://gitlab.real/")
    # Trailing slash stripped, so a URL copied from a browser still matches the
    # f"{url}/api/v4/..." joins everywhere else.
    assert Config.load(p).gitlab.url == "https://gitlab.real"


def test_url_falls_back_to_the_config_file(tmp_path, monkeypatch):
    monkeypatch.delenv("ARGUS_GITLAB_URL", raising=False)
    p = tmp_path / "c.yaml"
    p.write_text(gitlab_yaml())
    assert Config.load(p).gitlab.url == "https://gitlab.internal"


def test_verification_is_on_by_default(tmp_path, monkeypatch):
    monkeypatch.delenv("ARGUS_GITLAB_VERIFY", raising=False)
    p = tmp_path / "c.yaml"
    p.write_text(gitlab_yaml())
    gl = Config.load(p).gitlab
    assert gl.verify is True
    assert gl.ca_cert == ""


def test_verify_false_in_yaml_is_honoured(tmp_path):
    p = tmp_path / "c.yaml"
    p.write_text(gitlab_yaml("  verify: false\n"))
    assert Config.load(p).gitlab.verify is False


def test_verify_env_overrides_yaml(tmp_path, monkeypatch):
    p = tmp_path / "c.yaml"
    p.write_text(gitlab_yaml("  verify: true\n"))
    monkeypatch.setenv("ARGUS_GITLAB_VERIFY", "no")
    assert Config.load(p).gitlab.verify is False


@pytest.mark.parametrize("text,expected", [
    ("0", False), ("off", False), ("FALSE", False), ("No", False),
    ("1", True), ("yes", True), ("On", True), ("TRUE", True),
])
def test_verify_accepts_every_spelling_shell_users_write(tmp_path, monkeypatch,
                                                         text, expected):
    p = tmp_path / "c.yaml"
    p.write_text(gitlab_yaml())
    monkeypatch.setenv("ARGUS_GITLAB_VERIFY", text)
    assert Config.load(p).gitlab.verify is expected


def test_verify_refuses_a_value_that_is_not_a_boolean(tmp_path, monkeypatch):
    # Refused rather than defaulted. `verify: of` quietly meaning "verify" is a
    # security surprise; quietly meaning "do not verify" is a worse one. There
    # is no spelling of this option that should be guessed at.
    p = tmp_path / "c.yaml"
    p.write_text(gitlab_yaml())
    monkeypatch.setenv("ARGUS_GITLAB_VERIFY", "yep")
    with pytest.raises(ConfigError, match="boolean"):
        Config.load(p)


@pytest.mark.parametrize("blank", ["", "   "])
def test_an_empty_verify_variable_means_not_set(tmp_path, monkeypatch, blank):
    # Every shipped .env has the line `ARGUS_GITLAB_VERIFY=` -- that is how a
    # sample documents a setting it is leaving at its default. Treating the
    # empty string as a VALUE crash-looped Argus on its first start with
    # "gitlab.verify must be a boolean, not ''", which is a drop-in deployment
    # failing on its own sample configuration.
    p = tmp_path / "c.yaml"
    p.write_text(gitlab_yaml())
    monkeypatch.setenv("ARGUS_GITLAB_VERIFY", blank)
    assert Config.load(p).gitlab.verify is True


def test_an_empty_verify_variable_does_not_override_a_yaml_false(
        tmp_path, monkeypatch):
    # "Not set" must mean "fall through to the file", not "fall through to the
    # built-in default" -- otherwise exporting an empty variable would silently
    # re-enable verification on a GitLab that needs it off.
    p = tmp_path / "c.yaml"
    p.write_text(gitlab_yaml("  verify: false\n"))
    monkeypatch.setenv("ARGUS_GITLAB_VERIFY", "")
    assert Config.load(p).gitlab.verify is False


def test_a_deliberate_zero_in_verify_is_not_treated_as_blank(
        tmp_path, monkeypatch):
    # The reason blankness is checked explicitly instead of with `or`: `or`
    # discards "0", which would turn verification back ON under an operator who
    # had turned it off.
    p = tmp_path / "c.yaml"
    p.write_text(gitlab_yaml())
    monkeypatch.setenv("ARGUS_GITLAB_VERIFY", "0")
    assert Config.load(p).gitlab.verify is False


def test_an_empty_ca_cert_variable_means_not_set(tmp_path, monkeypatch):
    p = tmp_path / "c.yaml"
    p.write_text(gitlab_yaml())
    monkeypatch.setenv("ARGUS_GITLAB_CA_CERT", "")
    assert Config.load(p).gitlab.ca_cert == ""


def test_ca_cert_is_read_from_yaml(tmp_path):
    ca = tmp_path / "private-ca.pem"
    ca.write_text("-----BEGIN CERTIFICATE-----\n", encoding="utf-8")
    p = tmp_path / "c.yaml"
    p.write_text(gitlab_yaml(f"  ca_cert: {ca}\n"))
    assert Config.load(p).gitlab.ca_cert == str(ca)


def test_missing_ca_cert_file_is_refused_at_load(tmp_path):
    # Caught here, while the operator is looking at the config, rather than at
    # the first API call where CERTIFICATE_VERIFY_FAILED reads like a bad token
    # or a wrong URL and sends them somewhere else entirely.
    p = tmp_path / "c.yaml"
    p.write_text(gitlab_yaml(f"  ca_cert: {tmp_path / 'absent.pem'}\n"))
    with pytest.raises(ConfigError, match="not a file"):
        Config.load(p)


def test_ca_cert_together_with_verification_off_is_refused(tmp_path):
    ca = tmp_path / "private-ca.pem"
    ca.write_text("-----BEGIN CERTIFICATE-----\n", encoding="utf-8")
    p = tmp_path / "c.yaml"
    p.write_text(gitlab_yaml(f"  ca_cert: {ca}\n  verify: false\n"))
    with pytest.raises(ConfigError, match="contradict"):
        Config.load(p)


def test_redacted_names_disabled_verification():
    # Every credential and transport error quotes this string, so an operator
    # reading "could not reach GitLab ... verification DISABLED" is told the
    # cause instead of having to remember a setting from weeks ago.
    assert "DISABLED" in GitLabConfig(
        url="https://g", token="t", verify=False).redacted()
    assert "DISABLED" not in GitLabConfig(url="https://g", token="t").redacted()
