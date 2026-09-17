"""The Claude Code verify-after hook.

WHY THIS EXISTS

Forced verify-after is the last measured failure in this project. A model was
asked to review kernel code and answered in 2.2 seconds with ZERO tool calls,
naming a user-mode function, header and library. Telling the model to verify did
not work -- `SERVER_INSTRUCTIONS` already says to, and this is the model that
ignored it. So the check has to happen outside the model's judgement, which
means a hook: Claude Code runs a command when the model tries to finish, and a
non-zero exit with a message on stderr blocks the stop and hands the message
back as the reason.

The hook's whole job is the exit-code mapping, and these are the cases:

  argus 2  -> hook 2, blocking, with the contradictions on stderr
  argus 0  -> hook 0, the turn ends
  argus 6  -> hook 0. "I could not check" must NOT block, or a deployment with
              no documentation packs becomes an agent that can never finish a
              sentence.
  argus 1  -> hook 0. An unexpected failure is not a contradiction either. A
              verifier fails OPEN: the cost of not checking is a wrong answer
              somebody may catch, and the cost of blocking wrongly is an agent
              that cannot answer at all.

It also has to extract the draft from a transcript, and get nothing out of a
turn that produced no prose -- a tool-only turn or an interrupted one.
"""
from __future__ import annotations

import json
import os
import shutil
import subprocess
from pathlib import Path

import pytest

ROOT = Path(__file__).resolve().parent.parent
HOOK = ROOT / "clients" / "claude-code" / "verify-after.sh"

pytestmark = pytest.mark.skipif(
    shutil.which("bash") is None, reason="the hook is a bash script")


@pytest.fixture
def stub_argus(tmp_path):
    """A stand-in for `argus verify` that exits whatever the test asks for."""
    path = tmp_path / "argus"
    path.write_text(
        "#!/usr/bin/env bash\n"
        "echo \"The documentation contradicts 1 claim(s) in your draft.\" >&2\n"
        "echo \"  - wcscpy_s header: you said 'string.h'; the documentation \" >&2\n"
        "echo \"says 'wchar.h'\" >&2\n"
        "exit \"${FAKE_RC:-0}\"\n",
        encoding="utf-8")
    path.chmod(0o755)
    return path


def transcript(tmp_path, *blocks, name="t.jsonl"):
    """A transcript file in the shape the hook reads."""
    path = tmp_path / name
    with path.open("w", encoding="utf-8") as fh:
        for block in blocks:
            fh.write(json.dumps(block) + "\n")
    return path


def assistant(text):
    return {"type": "assistant", "message": {"role": "assistant", "content": text}}


def assistant_blocks(*blocks):
    return {"type": "assistant",
            "message": {"role": "assistant", "content": list(blocks)}}


def run_hook(tmp_path, path, rc, stub):
    payload = json.dumps({"transcript_path": str(path)})
    env = dict(os.environ, FAKE_RC=str(rc), ARGUS_BIN=str(stub),
               ARGUS_CONFIG="/dev/null")
    return subprocess.run(["bash", str(HOOK)], input=payload, env=env,
                          capture_output=True, text=True, timeout=60)


# --- the exit-code contract ----------------------------------------------------

@pytest.mark.parametrize("argus_rc,hook_rc", [
    (0, 0),   # clean: the turn ends
    (2, 2),   # contradicted: block
    (6, 0),   # could not check: do NOT block
    (1, 0),   # unexpected: do NOT block
    (3, 0),
])
def test_the_hook_blocks_only_on_a_contradiction(tmp_path, stub_argus,
                                                 argus_rc, hook_rc):
    path = transcript(tmp_path, assistant("wcscpy_s is in <string.h>."))
    out = run_hook(tmp_path, path, argus_rc, stub_argus)
    assert out.returncode == hook_rc, (
        f"argus exited {argus_rc} and the hook exited {out.returncode}; "
        f"stderr={out.stderr!r}")


def test_the_contradiction_is_handed_back_verbatim(tmp_path, stub_argus):
    """Claude Code shows this to the model as the reason the turn did not end,
    so it is the whole mechanism -- a block with no message is a turn that
    silently restarts with no idea what to fix."""
    path = transcript(tmp_path, assistant("wcscpy_s is in <string.h>."))
    out = run_hook(tmp_path, path, 2, stub_argus)
    assert out.returncode == 2
    assert "contradicts" in out.stderr
    assert "wchar.h" in out.stderr


def test_a_turn_with_no_prose_never_blocks(tmp_path, stub_argus):
    """A turn can end on a tool call, on a thinking block, or on nothing. None
    of those is a claim to check, and blocking them would make the agent unable
    to stop -- the hook fires on EVERY turn."""
    empty = transcript(tmp_path, name="empty.jsonl")
    assert run_hook(tmp_path, empty, 2, stub_argus).returncode == 0

    tool_only = transcript(
        tmp_path, assistant_blocks({"type": "tool_use", "name": "docs_verify"}),
        name="tool.jsonl")
    out = run_hook(tmp_path, tool_only, 2, stub_argus)
    assert out.returncode == 0, "a tool-only turn was blocked"
    assert out.stderr == "", "a tool-only turn produced a message"

    blank = transcript(tmp_path, assistant("   \n\t "), name="blank.jsonl")
    assert run_hook(tmp_path, blank, 2, stub_argus).returncode == 0


def test_a_missing_transcript_never_blocks(tmp_path, stub_argus):
    """The transcript shape is the part of this that could differ between
    Claude Code versions. Failing open means a version that renames a field
    stops checking, rather than blocking every answer for ever."""
    missing = tmp_path / "does-not-exist.jsonl"
    out = run_hook(tmp_path, missing, 2, stub_argus)
    assert out.returncode == 0


def test_the_last_assistant_message_is_the_draft(tmp_path, stub_argus):
    """The draft is what the model is about to say, not everything it has said.
    Verifying an earlier, already-corrected answer would block on a claim the
    model had since fixed."""
    seen = tmp_path / "seen.txt"
    stub = tmp_path / "argus"
    stub.write_text(
        "#!/usr/bin/env bash\n"
        f"cat > {seen}\n"
        "exit 0\n", encoding="utf-8")
    stub.chmod(0o755)

    path = transcript(
        tmp_path,
        assistant("wcscpy_s is in <string.h>"),
        {"type": "user", "message": {"role": "user", "content": "no it is not"}},
        assistant("You are right: wcscpy_s is in <wchar.h>."),
        name="multi.jsonl")

    # The hook pipes the draft on stdin, so the stub has to consume it.
    payload = json.dumps({"transcript_path": str(path)})
    subprocess.run(["bash", str(HOOK)], input=payload, timeout=60,
                   env=dict(os.environ, ARGUS_BIN=str(stub), ARGUS_CONFIG="/dev/null"),
                   capture_output=True, text=True)
    assert "wchar.h" in seen.read_text()
    assert "string.h" not in seen.read_text(), \
        "the hook verified an earlier answer the model had already corrected"
