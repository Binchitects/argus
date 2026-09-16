"""The Open WebUI thinking-level preset seeder.

`stack/deploy/seed-presets.py` is not part of the Argus package, but it is the
thing that makes the thinking level settable per chat, and it writes to a
database it does not own. The two properties worth pinning are that `off` maps
to the switch that actually turns thinking off, and that re-running replaces
presets instead of accumulating them -- a fresh deployment runs it on every
boot.
"""
from __future__ import annotations

import importlib.util
import json
import sqlite3
from pathlib import Path

import pytest

_MODULE_PATH = (Path(__file__).resolve().parent.parent
                / "stack" / "deploy" / "seed-presets.py")


def _load():
    spec = importlib.util.spec_from_file_location("seed_presets", _MODULE_PATH)
    assert spec and spec.loader
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    return module


seed = _load()


def _db(tmp_path):
    """The two columns this script depends on, plus a row to hang presets off."""
    path = tmp_path / "webui.db"
    conn = sqlite3.connect(path)
    conn.execute("CREATE TABLE user (id TEXT PRIMARY KEY, role TEXT, created_at INT)")
    conn.execute(
        "CREATE TABLE model (id TEXT PRIMARY KEY, user_id TEXT, base_model_id TEXT,"
        " name TEXT, params TEXT, meta TEXT, created_at INT, updated_at INT,"
        " is_active BOOLEAN)")
    conn.execute("INSERT INTO user VALUES ('admin-1', 'admin', 1)")
    conn.commit()
    conn.close()
    return path


@pytest.fixture
def configured(tmp_path, monkeypatch):
    db = _db(tmp_path)
    monkeypatch.setattr(seed, "DB", str(db))
    monkeypatch.setenv("MODEL_NAME", "Test-Model")
    monkeypatch.setenv("THINKING_PRESETS", "xhigh:Deep,low:Quick,off:Off")
    monkeypatch.setenv("THINKING_PRESETS_WAIT", "1")
    return db


def test_levels_parses_level_and_label(monkeypatch):
    monkeypatch.setenv("THINKING_PRESETS", "xhigh:Deep think, low : Quick ,,off:Off")
    assert seed.levels() == [("xhigh", "Deep think"), ("low", "Quick"), ("off", "Off")]


def test_a_level_with_no_label_uses_the_level(monkeypatch):
    monkeypatch.setenv("THINKING_PRESETS", "medium")
    assert seed.levels() == [("medium", "medium")]


def test_off_is_the_thinking_switch_not_an_effort():
    """`off` is not an effort the engine knows. Sending the literal string
    "off" would raise "Unexpected reasoning effort"; the switch that turns
    thinking off is enable_thinking."""
    assert seed.preset_params("off") == {
        "custom_params": {"chat_template_kwargs": {"enable_thinking": False}}}
    assert seed.preset_params("low") == {
        "custom_params": {"chat_template_kwargs": {"reasoning_effort": "low"}}}


def test_it_creates_one_preset_per_level(configured):
    assert seed.seed() == 3
    rows = sqlite3.connect(configured).execute(
        "SELECT id, base_model_id, params FROM model ORDER BY id").fetchall()
    assert [r[0] for r in rows] == [
        "test-model-think-low", "test-model-think-off", "test-model-think-xhigh"]
    assert all(r[1] == "Test-Model" for r in rows)
    kwargs = [json.loads(r[2])["custom_params"]["chat_template_kwargs"] for r in rows]
    assert {"reasoning_effort": "low"} in kwargs
    assert {"reasoning_effort": "xhigh"} in kwargs
    assert {"enable_thinking": False} in kwargs


def test_running_it_again_replaces_rather_than_accumulates(configured):
    """It runs on every container start, so a second run must not add a second
    copy of every preset to the picker."""
    seed.seed()
    seed.seed()
    assert sqlite3.connect(configured).execute(
        "SELECT COUNT(*) FROM model").fetchone()[0] == 3


def test_every_preset_is_owned(configured):
    """Open WebUI does not list a model with no owner -- measured. A preset
    created before anyone has signed in is invisible, which is why the entrypoint
    runs this in the background and waits."""
    seed.seed()
    owners = [r[0] for r in sqlite3.connect(configured).execute(
        "SELECT user_id FROM model")]
    assert owners == ["admin-1"] * 3


def test_a_database_with_no_schema_yet_is_not_an_error(tmp_path, monkeypatch, capsys):
    """Open WebUI creates the file first and the schema moments later, so the
    window where the file exists and `user` does not is real -- and it is
    exactly the fresh-deployment window this script exists for."""
    monkeypatch.setattr(seed, "DB", str(tmp_path / "webui.db"))
    monkeypatch.setenv("MODEL_NAME", "Test-Model")
    monkeypatch.setenv("THINKING_PRESETS", "low:Quick")
    monkeypatch.setenv("THINKING_PRESETS_WAIT", "0")
    sqlite3.connect(tmp_path / "webui.db").close()
    assert seed.seed() == 0
    assert "no admin user" in capsys.readouterr().out


def test_an_empty_setting_is_a_no_op(tmp_path, monkeypatch, capsys):
    monkeypatch.setattr(seed, "DB", str(tmp_path / "webui.db"))
    monkeypatch.setenv("MODEL_NAME", "Test-Model")
    monkeypatch.setenv("THINKING_PRESETS", "")
    assert seed.seed() == 0
    assert "nothing to do" in capsys.readouterr().out
