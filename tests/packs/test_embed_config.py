"""The embedding model is a deployment choice, not a constant to edit.

Checked in a subprocess rather than by reloading modules in-process. An
earlier version of this file used importlib.reload on argus.embed and its
dependants, which passed on its own and broke an unrelated CLI test when the
suite ran as a whole: other modules bind EMBED_DIM at import time and keep the
old value, so the reload leaves the process in a state no real deployment is
ever in. A subprocess is also what an operator actually does -- set the
variables, run the build.
"""

from __future__ import annotations

import os
import subprocess
import sys

PROBE = (
    "from argus import embed;"
    "from argus.packs import format as f, quantize as q;"
    "print(embed.EMBED_MODEL, embed.EMBED_DIM,"
    "      'bit[%d]' % embed.EMBED_DIM in f._CREATE_VEC_BIN,"
    "      'int8[%d]' % embed.EMBED_DIM in f._CREATE_VEC_I8,"
    "      q.DIM)"
)


def _probe(**overrides: str) -> list[str]:
    env = {**os.environ, **overrides}
    out = subprocess.run([sys.executable, "-c", PROBE], env=env,
                         capture_output=True, text=True, timeout=120)
    assert out.returncode == 0, out.stderr
    return out.stdout.split()


def test_the_model_and_dimension_come_from_the_environment():
    """The vector tables and the quantizer must follow the model. If they do
    not, a build writes 1024-dimension vectors into a table declared for 768
    and fails deep inside a run that has already cost real time."""
    model, dim, bin_ok, i8_ok, qdim = _probe(
        ARGUS_EMBED_MODEL="mxbai-embed-large", ARGUS_EMBED_DIM="1024")
    assert model == "mxbai-embed-large"
    assert dim == "1024"
    assert bin_ok == "True" and i8_ok == "True"
    assert qdim == "1024"


def test_the_default_is_unchanged_without_the_environment():
    env = {k: v for k, v in os.environ.items()
           if k not in ("ARGUS_EMBED_MODEL", "ARGUS_EMBED_DIM")}
    out = subprocess.run([sys.executable, "-c", PROBE], env=env,
                         capture_output=True, text=True, timeout=120)
    assert out.returncode == 0, out.stderr
    model, dim, bin_ok, i8_ok, qdim = out.stdout.split()
    assert model == "nomic-embed-text"
    assert dim == "768" and qdim == "768"
    assert bin_ok == "True" and i8_ok == "True"
