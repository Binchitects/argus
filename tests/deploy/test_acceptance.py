"""The embedder check in the acceptance script.

`deploy/scripts/acceptance.py` is the script an operator runs to decide whether
a deployment is fit to hand over, so its parsing is tested here with everything
else rather than only by running it against a live stack.

What these pin: the embedder's answer is read for the width of its vectors,
and anything that is not an embedding (an error body, a proxy's HTML, nothing
at all) reads as "no dimension" -- a FAIL that says what came back -- never as
a crash that takes the acceptance run down, and never as a pass.
"""
from __future__ import annotations

import importlib.util
import json
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


def test_the_width_of_the_first_vector_is_read(acc):
    body = json.dumps({"object": "list", "data": [{"embedding": [0.1] * 768, "index": 0}], "model": "nomic-embed-text"})
    assert acc._embed_dimension(body) == 768


@pytest.mark.parametrize("body", [
    "",
    "<html>502 Bad Gateway</html>",
    json.dumps({"error": {"message": "model not found"}}),
    json.dumps({"data": []}),
    json.dumps({"data": [{"index": 0}]}),
    json.dumps([1, 2, 3]),
])
def test_anything_but_an_embedding_has_no_dimension(acc, body):
    assert acc._embed_dimension(body) is None
