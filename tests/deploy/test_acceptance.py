"""The embedder's device check in the acceptance script.

`deploy/scripts/acceptance.py` is not part of the argus package, but it is the
script an operator runs to decide whether a deployment is fit to hand over, so
its parsing is tested here with everything else rather than only by running it
against a live stack.

The bug these tests pin: reading `ollama ps` backwards. Columns are
NAME ID SIZE PROCESSOR UNTIL, so the obvious `split()[-2]` returns the
PROCESSOR when UNTIL is `Forever` -- and returns `from` when UNTIL is
`4 minutes from now`. That would report a perfectly healthy GPU embedder as a
CPU fallback, on the one check whose entire purpose is to notice a silent CPU
fallback. A false alarm here is worse than no check: it is the kind of thing
that gets a check deleted.
"""
from __future__ import annotations

import importlib.util
from pathlib import Path

import pytest

_MODULE_PATH = (Path(__file__).resolve().parents[2]
                / "deploy" / "scripts" / "acceptance.py")


@pytest.fixture(scope="module")
def acc():
    spec = importlib.util.spec_from_file_location("acceptance", _MODULE_PATH)
    assert spec and spec.loader
    module = importlib.util.module_from_spec(spec)
    # Not executed as __main__, so nothing runs on import; the module only
    # defines functions and reads constants.
    spec.loader.exec_module(module)
    return module


@pytest.mark.parametrize("line,expected", [
    # The shape the reference host actually printed.
    ("nomic-embed-text:latest  0a109f422b47  849 MB  100% GPU  Forever",
     "100% GPU"),
    # A CPU fallback: one token, no percentage.
    ("nomic-embed-text:latest  0a109f422b47  849 MB  CPU  Forever", "CPU"),
    # A partial offload. Contains GPU, which is what the check tests for.
    ("nomic-embed-text:latest  0a109f422b47  849 MB  48%/52% CPU/GPU  Forever",
     "48%/52% CPU/GPU"),
    # The case that broke `[-2]`: a multi-word UNTIL.
    ("model:latest  abc123  1.2 GB  100% GPU  4 minutes from now", "100% GPU"),
    ("model:latest  abc123  1.2 GB  CPU  4 minutes from now", "CPU"),
    # A different size unit, and a larger model.
    ("qwen3:32b  deadbeef  19 GB  100% GPU  2 hours from now", "100% GPU"),
    ("tiny:1b  aa11  512 B  CPU  Forever", "CPU"),
])
def test_the_processor_column_is_read_from_the_size_anchor(acc, line, expected):
    assert acc._ollama_processor(line) == expected


def test_a_cpu_fallback_is_not_reported_as_healthy(acc):
    """The whole point of the check. `GPU` must be the thing tested for, so a
    bare `CPU` -- and a column read wrongly off the end of the line -- both
    fail rather than pass."""
    assert "GPU" not in acc._ollama_processor(
        "nomic-embed-text:latest  0a109f422b47  849 MB  CPU  Forever")
    assert "GPU" in acc._ollama_processor(
        "nomic-embed-text:latest  0a109f422b47  849 MB  100% GPU  4 minutes from now")


def test_a_line_with_no_size_column_does_not_raise(acc):
    """`ollama ps` prints a header and, between loads, nothing else. A parser
    that raised here would take down the acceptance run at the moment it has
    the least to report."""
    assert acc._ollama_processor("NAME  ID  SIZE  PROCESSOR  UNTIL") == "?"
    assert acc._ollama_processor("") == "?"
