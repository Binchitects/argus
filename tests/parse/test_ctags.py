import logging
import shutil
import subprocess
import types
from pathlib import Path

import pytest

from codeindex.parse import ctags

FIXTURES = Path(__file__).parent.parent / "fixtures" / "ctags"
pytestmark = pytest.mark.skipif(
    shutil.which("ctags") is None, reason="universal-ctags not installed"
)


@pytest.fixture(scope="module")
def parsed():
    return ctags.extract_symbols(
        FIXTURES, ["decoder.h", "decoder.cpp", "anon_namespace.cpp"]
    )


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


def test_anonymous_namespace_function_is_not_public(parsed):
    helper = _by_name(parsed["anon_namespace.cpp"])["HiddenHelper"]
    assert (helper["scope"] or "").startswith("__anon")
    assert helper["is_public"] == 0


def test_signature_captured(parsed):
    sig = _by_name(parsed["decoder.h"])["DecodeFrame"]["signature"]
    assert sig is not None and "const char" in sig


def test_missing_files_do_not_raise(tmp_path):
    assert ctags.extract_symbols(tmp_path, ["nope.c"]) == {}


def test_subprocess_timeout_raises_ctags_unavailable(monkeypatch, tmp_path):
    (tmp_path / "a.c").write_text("int a(void){return 1;}\n")

    def fake_run(*args, **kwargs):
        raise subprocess.TimeoutExpired(cmd="ctags", timeout=kwargs.get("timeout"))

    monkeypatch.setattr(ctags.subprocess, "run", fake_run)
    with pytest.raises(ctags.CtagsUnavailable):
        ctags.extract_symbols(tmp_path, ["a.c"])


def test_partial_failure_logs_stderr(monkeypatch, tmp_path, caplog):
    (tmp_path / "a.c").write_text("int a(void){return 1;}\n")

    fake_proc = types.SimpleNamespace(
        returncode=1,
        stdout='{"_type":"tag","name":"a","path":"a.c","kind":"function","line":1}\n',
        stderr="ctags: some.c: parse error\n",
    )
    monkeypatch.setattr(ctags.subprocess, "run", lambda *a, **k: fake_proc)

    with caplog.at_level(logging.WARNING, logger=ctags.__name__):
        results = ctags.extract_symbols(tmp_path, ["a.c"])

    assert "a.c" in results  # the partial output is still returned
    assert any("parse error" in r.message for r in caplog.records)


def test_is_public_symbol_rules():
    assert ctags.is_public_symbol("a/b.h", None, False) is True
    assert ctags.is_public_symbol("a/b.h", "eal::detail", False) is False
    assert ctags.is_public_symbol("a/b.cpp", None, False) is True
    assert ctags.is_public_symbol("a/b.cpp", None, True) is False
    assert ctags.is_public_symbol("a/b.h", "__anon1ef920e20111", False) is False
