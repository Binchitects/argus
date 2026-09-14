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
