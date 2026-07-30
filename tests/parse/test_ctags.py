import logging
import shutil
import subprocess
import types
from pathlib import Path

import pytest

from argus.parse import ctags

FIXTURES = Path(__file__).parent.parent / "fixtures" / "ctags"
pytestmark = pytest.mark.skipif(
    shutil.which("ctags") is None, reason="universal-ctags not installed"
)


@pytest.fixture(scope="module")
def parsed():
    return ctags.extract_symbols(
        FIXTURES, ["decoder.h", "decoder.cpp", "anon_namespace.cpp"]
    ).symbols


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
    assert ctags.extract_symbols(tmp_path, ["nope.c"]).symbols == {}


def test_clean_run_covers_every_listed_path_including_symbol_free_ones(tmp_path):
    """A file ctags parsed cleanly is covered even when it yields no tags.

    ctags emits nothing at all for a symbol-free translation unit, so
    `symbols` alone cannot distinguish "parsed, nothing to report" from
    "never opened". Only `covered` can.
    """
    (tmp_path / "has_symbols.c").write_text("int Alpha(void){return 1;}\n")
    (tmp_path / "quiet.c").write_text('#include "decoder.h"\n')

    batch = ctags.extract_symbols(tmp_path, ["has_symbols.c", "quiet.c"])

    assert batch.covered == frozenset({"has_symbols.c", "quiet.c"})
    assert batch.uncovered == {}
    assert "quiet.c" not in batch.symbols  # no tags, yet still covered


def test_partial_batch_reports_the_blamed_path_as_uncovered(monkeypatch, tmp_path):
    """A non-zero exit with partial stdout must not imply the whole batch ran.

    good.c produced tags, quiet.c produced none but was never complained
    about, bad.c is named in stderr. Only bad.c is uncovered -- reporting
    quiet.c as uncovered too would make one unparseable file poison the
    completion marker of every symbol-free file in the same batch.
    """
    for name in ("good.c", "quiet.c", "bad.c"):
        (tmp_path / name).write_text("int x(void){return 1;}\n")

    fake_proc = types.SimpleNamespace(
        returncode=1,
        stdout='{"_type":"tag","name":"good","path":"good.c","kind":"function","line":1}\n',
        stderr='ctags: Warning: cannot open input file "bad.c" : Permission denied\n',
    )
    monkeypatch.setattr(ctags.subprocess, "run", lambda *a, **k: fake_proc)

    batch = ctags.extract_symbols(tmp_path, ["good.c", "quiet.c", "bad.c"])

    assert batch.covered == frozenset({"good.c", "quiet.c"})
    assert set(batch.uncovered) == {"bad.c"}
    assert "Permission denied" in batch.uncovered["bad.c"]


def test_root_path_is_not_blamed_for_a_subdirectory_namesake(monkeypatch, tmp_path):
    """Blame must be attributed by path, not by naive substring.

    `main.c` at the root and `sub/main.c` are ordinary in C repos. A plain
    `p in stderr` test blames the root file for the subdirectory file's
    diagnostic, and being blamed is destructive AND budgeted: the caller
    deletes the symbols it just extracted, NULLs symbols_sha and charges a
    retry attempt, so three such passes strand a perfectly healthy file
    permanently symbol-less.
    """
    (tmp_path / "main.c").write_text("int MainRoot(void){return 0;}\n")
    (tmp_path / "util.c").write_text("int Util(void){return 0;}\n")
    (tmp_path / "sub").mkdir()
    (tmp_path / "sub" / "main.c").write_text("int MainSub(void){return 1;}\n")

    fake_proc = types.SimpleNamespace(
        returncode=1,
        stdout='{"_type":"tag","name":"MainRoot","path":"main.c",'
               '"kind":"function","line":1}\n',
        stderr='ctags: Warning: cannot open input file "sub/main.c"'
               " : Permission denied\n",
    )
    monkeypatch.setattr(ctags.subprocess, "run", lambda *a, **k: fake_proc)

    batch = ctags.extract_symbols(tmp_path, ["main.c", "sub/main.c", "util.c"])

    assert set(batch.uncovered) == {"sub/main.c"}
    assert batch.covered == frozenset({"main.c", "util.c"})


def test_unquoted_diagnostic_still_attributes_at_a_path_boundary(
    monkeypatch, tmp_path
):
    """Not every ctags diagnostic quotes the offending path.

    When none does, attribution falls back to a boundary-anchored search --
    which must still refuse to blame `main.c` for `sub/main.c`.
    """
    (tmp_path / "main.c").write_text("int MainRoot(void){return 0;}\n")
    (tmp_path / "sub").mkdir()
    (tmp_path / "sub" / "main.c").write_text("int MainSub(void){return 1;}\n")

    fake_proc = types.SimpleNamespace(
        returncode=1,
        stdout='{"_type":"tag","name":"MainRoot","path":"main.c",'
               '"kind":"function","line":1}\n',
        stderr="ctags: sub/main.c: unexpected end of file\n",
    )
    monkeypatch.setattr(ctags.subprocess, "run", lambda *a, **k: fake_proc)

    batch = ctags.extract_symbols(tmp_path, ["main.c", "sub/main.c"])

    assert set(batch.uncovered) == {"sub/main.c"}
    assert batch.covered == frozenset({"main.c"})


def test_unattributable_failure_covers_only_paths_that_produced_tags(
    monkeypatch, tmp_path
):
    """When stderr names no path, only tag-producing paths are provably covered."""
    for name in ("good.c", "quiet.c"):
        (tmp_path / name).write_text("int x(void){return 1;}\n")

    fake_proc = types.SimpleNamespace(
        returncode=1,
        stdout='{"_type":"tag","name":"good","path":"good.c","kind":"function","line":1}\n',
        stderr="ctags: internal error\n",
    )
    monkeypatch.setattr(ctags.subprocess, "run", lambda *a, **k: fake_proc)

    batch = ctags.extract_symbols(tmp_path, ["good.c", "quiet.c"])

    assert batch.covered == frozenset({"good.c"})
    assert set(batch.uncovered) == {"quiet.c"}


def test_path_absent_from_disk_is_uncovered_not_silently_complete(tmp_path):
    """git can check out a name the filesystem will not hand back.

    Windows reserved names (aux.c), >260-char paths and trailing-dot names
    all fail is_file(); they are dropped from the ctags argument list, so
    they must be reported uncovered rather than looking like a clean run
    that happened to find no symbols.
    """
    (tmp_path / "real.c").write_text("int Real(void){return 1;}\n")

    batch = ctags.extract_symbols(tmp_path, ["real.c", "ghost.c"])

    assert batch.covered == frozenset({"real.c"})
    assert set(batch.uncovered) == {"ghost.c"}


def test_every_listed_path_is_either_covered_or_uncovered(tmp_path):
    """The contract callers rely on: no path may fall through both sets."""
    (tmp_path / "real.c").write_text("int Real(void){return 1;}\n")
    listed = ["real.c", "ghost.c"]

    batch = ctags.extract_symbols(tmp_path, listed)

    assert batch.covered | set(batch.uncovered) == set(listed)
    assert batch.covered & set(batch.uncovered) == set()


def test_subprocess_timeout_raises_ctags_unavailable(monkeypatch, tmp_path):
    (tmp_path / "a.c").write_text("int a(void){return 1;}\n")
    seen = {}

    def fake_run(*args, **kwargs):
        # Raising unconditionally would only exercise the handler: deleting
        # timeout=CTAGS_TIMEOUT_SECONDS from the real subprocess.run call
        # would leave this test green. Capture what was actually passed.
        seen["timeout"] = kwargs.get("timeout", "not passed")
        raise subprocess.TimeoutExpired(cmd="ctags", timeout=kwargs.get("timeout"))

    monkeypatch.setattr(ctags.subprocess, "run", fake_run)
    with pytest.raises(ctags.CtagsUnavailable):
        ctags.extract_symbols(tmp_path, ["a.c"])
    assert seen["timeout"] == ctags.CTAGS_TIMEOUT_SECONDS


def test_partial_failure_logs_stderr(monkeypatch, tmp_path, caplog):
    (tmp_path / "a.c").write_text("int a(void){return 1;}\n")

    fake_proc = types.SimpleNamespace(
        returncode=1,
        stdout='{"_type":"tag","name":"a","path":"a.c","kind":"function","line":1}\n',
        stderr="ctags: some.c: parse error\n",
    )
    monkeypatch.setattr(ctags.subprocess, "run", lambda *a, **k: fake_proc)

    with caplog.at_level(logging.WARNING, logger=ctags.__name__):
        results = ctags.extract_symbols(tmp_path, ["a.c"]).symbols

    assert "a.c" in results  # the partial output is still returned
    assert any("parse error" in r.message for r in caplog.records)


def test_is_public_symbol_rules():
    assert ctags.is_public_symbol("a/b.h", None, False) is True
    assert ctags.is_public_symbol("a/b.h", "eal::detail", False) is False
    assert ctags.is_public_symbol("a/b.cpp", None, False) is True
    assert ctags.is_public_symbol("a/b.cpp", None, True) is False
    assert ctags.is_public_symbol("a/b.h", "__anon1ef920e20111", False) is False
