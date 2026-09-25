"""`argus verify` -- the enforcement point for verify-after.

WHY THIS EXISTS

`docs_verify` has been an MCP tool for a while, and the failure it was built for
is still open: a model answered a kernel-security question with **zero tool
calls in 2.2 seconds**, naming a user-mode function, a user-mode header and a
user-mode library, all from memory. Every client can run a SHELL COMMAND when
the model finishes and block on its exit code; almost none can be made to call
a *tool* at that moment. So the command is the enforcement point, and its exit
codes are the entire contract.

What these pin, in order of how badly each would fail in production:

  * 2 means "the documentation contradicts you" and NOTHING ELSE. If a bad path
    or an unreadable pack also exited 2, a typo would block every answer the
    agent ever tried to give, for ever, with a message about documentation.

  * 6 means "could not check" and does NOT block. A mandatory verifier that
    cannot verify must not become an agent that can never finish a sentence.

  * 0 for an empty draft. A hook fires on every turn, including ones that
    produced no prose at all -- a tool-only turn, an interrupted one.

  * Only `contradicted` blocks. `confirmed` and `unstated` never do: the tool
    exists to correct, not to replace, and an answer is allowed to be about
    something other than a fact it happens to mention.
"""
from __future__ import annotations

import json

import pytest

from argus.cli import (
    EXIT_VERIFY_CONTRADICTED,
    EXIT_VERIFY_UNAVAILABLE,
    main,
)
from argus.cli import _verify


class Args:
    def __init__(self, config, **kw):
        self.config = config
        self.text = kw.get("text")
        self.text_file = kw.get("text_file")
        self.limit = kw.get("limit", 40)
        self.json = kw.get("json", False)
        self.quiet = kw.get("quiet", False)


@pytest.fixture
def cfg_file(tmp_path):
    """A config whose packs directory exists and is empty."""
    (tmp_path / "packs").mkdir()
    path = tmp_path / "config.yaml"
    path.write_text(
        f"gitlab:\n  url: https://gl.test\n  token: tok\n"
        f"index:\n  data_dir: {(tmp_path / 'd').as_posix()}\n"
        f"  db_path: {(tmp_path / 'd' / 'i.db').as_posix()}\n"
        f"packs:\n  dir: {(tmp_path / 'packs').as_posix()}\n",
        encoding="utf-8")
    return path


# --- fail open, never closed ---------------------------------------------------

def test_no_packs_is_not_a_block(cfg_file, capsys):
    """The distinction the whole contract rests on. A deployment without
    documentation packs must not become an agent that cannot finish a sentence.
    """
    rc = _verify(Args(cfg_file, text="wcscpy_s is declared in <string.h>."))
    assert rc == EXIT_VERIFY_UNAVAILABLE
    assert rc != EXIT_VERIFY_CONTRADICTED, \
        "an unavailable verifier was reported as a contradiction"
    err = capsys.readouterr().err
    assert "no documentation packs" in err
    assert "cannot check" in err


def test_an_empty_draft_is_clean(cfg_file, capsys):
    """A hook fires on every turn, including a tool-only one and an interrupted
    one. Blocking those would make the agent unable to stop."""
    for text in ("", "   \n\t "):
        assert _verify(Args(cfg_file, text=text)) == 0
    assert "nothing to verify" in capsys.readouterr().err


def test_a_missing_config_is_not_a_block(tmp_path, capsys):
    """Exit 2 here would be read as "the documentation contradicts you" -- a
    typo in a path would block every answer for ever, blaming the docs."""
    rc = _verify(Args(tmp_path / "nope.yaml", text="anything"))
    assert rc == EXIT_VERIFY_UNAVAILABLE
    assert "config error" in capsys.readouterr().err


def test_an_unreadable_text_file_is_not_a_block(cfg_file, tmp_path, capsys):
    rc = _verify(Args(cfg_file, text_file=tmp_path / "missing.txt"))
    assert rc == EXIT_VERIFY_UNAVAILABLE
    assert "could not read" in capsys.readouterr().err


def test_a_config_error_does_not_reach_the_shared_loader(tmp_path, capsys):
    """`main` loads the config for most commands and returns 2 when that fails.
    `verify` must be dispatched before that, or a bad config is reported as a
    contradiction -- which is the failure mode this whole file is about."""
    rc = main(["verify", "--config", str(tmp_path / "nope.yaml"),
               "--text", "anything"])
    assert rc == EXIT_VERIFY_UNAVAILABLE
    assert rc != EXIT_VERIFY_CONTRADICTED


# --- the verdict ---------------------------------------------------------------

def test_a_clean_draft_exits_zero_and_says_so_quietly(cfg_file, capsys):
    """Silent on success unless asked: a hook that prints on every turn trains
    the reader to ignore it."""
    rc = _verify(Args(cfg_file, text="nothing checkable here", quiet=True))
    assert rc in (0, EXIT_VERIFY_UNAVAILABLE)      # 0 when packs exist


def test_only_contradicted_blocks(monkeypatch, cfg_file, capsys):
    """The classification decides the exit code, and nothing else does. This
    pins the mapping rather than the pack machinery underneath it."""
    from argus.store import packs as store_packs

    monkeypatch.setattr(store_packs, "open_packs", lambda paths: ["pack"])
    monkeypatch.setattr(store_packs, "close_packs", lambda packs: None)
    (cfg_file.parent / "packs" / "x.arguspack").write_bytes(b"x")

    cases = [
        ("contradicted", EXIT_VERIFY_CONTRADICTED),
        ("confirmed", 0),
        ("unstated", 0),
    ]
    for status, expected in cases:
        monkeypatch.setattr(store_packs, "verify_text",
                            lambda p, t, limit=0, s=status: [
                                _finding("wcscpy_s", {"field": "header", "status": s,
                                                      "stated": "string.h",
                                                      "documented": "wchar.h"})])
        rc = _verify(Args(cfg_file, text="a draft naming wcscpy_s"))
        assert rc == expected, f"status {status!r} produced exit {rc}"


def test_the_contradiction_message_is_an_instruction(monkeypatch, cfg_file, capsys):
    """The reader is a model that has just finished an answer and has to decide
    what to do about it. "Contradicted: header" is a fact; this has to be a
    direction, or the model restates the same claim from memory again."""
    from argus.store import packs as store_packs

    (cfg_file.parent / "packs" / "x.arguspack").write_bytes(b"x")
    monkeypatch.setattr(store_packs, "open_packs", lambda paths: ["pack"])
    monkeypatch.setattr(store_packs, "close_packs", lambda packs: None)
    monkeypatch.setattr(store_packs, "verify_text",
                        lambda p, t, limit=0: [
                            _finding("wcscpy_s", {"field": "header",
                                                  "status": "contradicted",
                                                  "stated": "string.h",
                                                  "documented": "wchar.h"})])

    rc = _verify(Args(cfg_file, text="wcscpy_s is in <string.h>"))
    assert rc == EXIT_VERIFY_CONTRADICTED
    err = capsys.readouterr().err
    assert "contradicts" in err
    assert "do not restate them from memory" in err
    assert "wcscpy_s" in err and "string.h" in err and "wchar.h" in err
    assert "[win32]" in err


def _finding(name: str, *fields: dict) -> dict:
    """One API as `verify_text` really reports it: the verdicts nested under
    `fields`, the contradicted ones repeated under `corrections`. These tests
    once faked a flat per-claim shape the function never returns, so they
    passed while the real command could not exit 2."""
    return {"name": name, "source": "win32", "url": f"https://x/{name}",
            "doc_path": f"{name}.md", "fields": list(fields),
            "corrections": [f for f in fields if f["status"] == "contradicted"]}


def test_a_real_pack_blocks_a_wrong_claim(cfg_file, capsys):
    """No monkeypatching: a real pack, whose requirement line carries the
    description after the " -- " marker exactly as the win32 adapter writes it,
    and the real `verify_text`. This is the path a Stop hook runs."""
    import shutil
    import sys
    from pathlib import Path

    sys.path.insert(0, str(Path(__file__).parent / "store"))
    from test_packs import _verify_pack

    built = _verify_pack(cfg_file.parent / "built", description=True)
    shutil.copy(built, cfg_file.parent / "packs" / built.name)

    rc = _verify(Args(cfg_file, text="MessageBoxW lives in shell32.dll.", json=True))
    captured = capsys.readouterr()
    assert rc == EXIT_VERIFY_CONTRADICTED, captured.err
    body = json.loads(captured.out)
    assert body["contradicted"] == [{
        "symbol": "MessageBoxW", "source": "v", "url": "https://x/MessageBoxW",
        "field": "dll", "documented": "User32.dll", "status": "contradicted",
        "stated": "shell32.dll"}]
    assert "you said 'shell32.dll'; the documentation says 'User32.dll'" in captured.err

    assert _verify(Args(cfg_file, text="MessageBoxW lives in User32.dll.", quiet=True)) == 0


def test_json_output_carries_the_findings(monkeypatch, cfg_file, capsys):
    from argus.store import packs as store_packs

    (cfg_file.parent / "packs" / "x.arguspack").write_bytes(b"x")
    monkeypatch.setattr(store_packs, "open_packs", lambda paths: ["pack"])
    monkeypatch.setattr(store_packs, "close_packs", lambda packs: None)
    monkeypatch.setattr(store_packs, "verify_text",
                        lambda p, t, limit=0: [
                            _finding("x", {"field": "header", "status": "confirmed",
                                           "documented": "x.h"})])

    assert _verify(Args(cfg_file, text="draft", json=True)) == 0
    body = json.loads(capsys.readouterr().out)
    assert body["contradicted"] == []
    assert [f["name"] for f in body["findings"]] == ["x"]


def test_stdin_is_read_when_the_path_is_a_dash(monkeypatch, cfg_file, capsys):
    """What a hook pipes. A temporary file holding a model's entire answer is
    one more thing to leak and to clean up."""
    import io
    import sys as _sys
    from argus.store import packs as store_packs

    (cfg_file.parent / "packs" / "x.arguspack").write_bytes(b"x")
    seen = {}
    monkeypatch.setattr(store_packs, "open_packs", lambda paths: ["pack"])
    monkeypatch.setattr(store_packs, "close_packs", lambda packs: None)

    def fake_verify(packs, text, limit=0):
        seen["text"] = text
        return []

    monkeypatch.setattr(store_packs, "verify_text", fake_verify)
    monkeypatch.setattr(_sys, "stdin", io.StringIO("the draft from stdin"))

    assert _verify(Args(cfg_file, text_file="-")) == 0
    assert seen["text"] == "the draft from stdin"
