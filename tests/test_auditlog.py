"""The audit stream: one JSON line per tool call or refusal, on stdout."""
from __future__ import annotations

import asyncio
import json

import pytest

from argus import acl, auditlog
from argus.mcpsrv import tools


def _lines(capsys):
    return [json.loads(line) for line in capsys.readouterr().out.splitlines() if line.startswith("{")]


def test_tool_call_line_has_who_what_and_outcome(capsys):
    auditlog.tool_call(tool="find_symbol", user="alice", user_id=7, args={"name": "DecodeFrame"},
                       repos_visible=3, duration_ms=12.345, error=None)
    (line,) = _lines(capsys)
    assert line["event"] == "tool_call"
    assert (line["tool"], line["user"], line["user_id"]) == ("find_symbol", "alice", 7)
    assert line["outcome"] == "ok" and line["error"] is None
    assert line["duration_ms"] == 12.3 and line["repos_visible"] == 3
    assert line["args"] == {"name": "DecodeFrame"}


def test_access_notice_is_no_access_and_other_errors_are_errors(capsys):
    AccessNotice = type("AccessNotice", (LookupError,), {})
    auditlog.tool_call(tool="get_file", user="bob", user_id=8, args={}, repos_visible=1,
                       duration_ms=1, error=AccessNotice("nope"))
    auditlog.tool_call(tool="get_file", user="bob", user_id=8, args={}, repos_visible=1,
                       duration_ms=1, error=ValueError("boom"))
    first, second = _lines(capsys)
    assert (first["outcome"], first["error"]) == ("no_access", "AccessNotice")
    assert (second["outcome"], second["error"]) == ("error", "ValueError")


def test_long_arguments_are_clipped(capsys):
    auditlog.tool_call(tool="search_code", user="a", user_id=1, args={"query": "x" * 5000},
                       repos_visible=0, duration_ms=0, error=None)
    (line,) = _lines(capsys)
    assert len(line["args"]["query"]) < 400


def test_denied_line(capsys):
    auditlog.denied(reason="missing_token", path="/mcp")
    (line,) = _lines(capsys)
    assert (line["event"], line["reason"], line["path"]) == ("denied", "missing_token", "/mcp")


def test_can_be_turned_off(capsys, monkeypatch):
    monkeypatch.setenv("ARGUS_AUDIT_LOG", "0")
    auditlog.denied(reason="missing_token", path="/mcp")
    assert _lines(capsys) == []


def test_with_audit_emits_one_line_for_success_and_for_failure(capsys, tmp_path):
    identity = acl.Identity(7, "dev", [1, 2])

    async def ok():
        return "result"

    async def fails():
        raise ValueError("query failed")

    assert asyncio.run(tools._with_audit(tmp_path / "index.db", "repo_map", identity, {"q": 1}, ok)) == "result"
    with pytest.raises(ValueError):
        asyncio.run(tools._with_audit(tmp_path / "index.db", "repo_map", identity, {"q": 2}, fails))
    first, second = [l for l in _lines(capsys) if l["event"] == "tool_call"]
    assert (first["outcome"], first["user"], first["repos_visible"]) == ("ok", "dev", 2)
    assert (second["outcome"], second["error"], second["args"]) == ("error", "ValueError", {"q": 2})


# --- indexing --------------------------------------------------------------
#
# A pass used to leave nothing behind but an exit code: the child's output was
# captured for the panel's live tail and never written to the container log, so
# there was no history to look at afterwards. These events are what makes a run
# queryable in Loki, and `outcome` is what a dashboard groups by.


def test_index_start_records_whether_the_index_may_be_partial(capsys):
    auditlog.index_start(branches=["main", "release/*"], allow_partial=True,
                         repos=42)
    (line,) = _lines(capsys)
    assert line["event"] == "index_start"
    assert (line["branches"], line["repos"]) == (["main", "release/*"], 42)
    assert line["allow_partial"] is True


def test_index_repo_carries_the_numbers_a_dashboard_needs(capsys):
    auditlog.index_repo(repo="g/alpha", branch="main", outcome="ok",
                        duration_ms=1234.5, indexed=7, deleted=1, skipped=90,
                        errors=0, timed_out=False, symbols_failed=False)
    (line,) = _lines(capsys)
    assert line["event"] == "index_repo"
    assert (line["repo"], line["branch"], line["outcome"]) == ("g/alpha", "main", "ok")
    assert (line["indexed"], line["deleted"], line["skipped"]) == (7, 1, 90)
    assert line["duration_ms"] == 1234.5


def test_up_to_date_is_its_own_outcome(capsys):
    """A pass where nothing changed and a pass that reindexed everything both
    exit 0. Without a distinct outcome an operator cannot tell them apart
    without reading the log text."""
    auditlog.index_repo(repo="g/alpha", branch="main", outcome="up_to_date")
    (line,) = _lines(capsys)
    assert line["outcome"] == "up_to_date"
    assert line["indexed"] is None


def test_a_failed_repo_names_the_reason(capsys):
    auditlog.index_repo(repo="g/alpha", branch="main", outcome="failed",
                        error="fatal: could not read from remote")
    (line,) = _lines(capsys)
    assert line["outcome"] == "failed"
    assert "could not read from remote" in line["error"]


def test_index_end_sets_outcome_from_the_exit_code(capsys):
    """`outcome` is a Promtail label, so "how many runs failed this week" is a
    one-line query rather than parsing a number out of the line."""
    auditlog.index_end(returncode=0, duration_ms=10, repos=3, failed=0,
                       up_to_date=3)
    auditlog.index_end(returncode=1, duration_ms=10, repos=3, failed=1,
                       up_to_date=2)
    ok, bad = _lines(capsys)
    assert (ok["event"], ok["outcome"], ok["returncode"]) == ("index_end", "ok", 0)
    assert (bad["outcome"], bad["returncode"]) == ("error", 1)
