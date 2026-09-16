"""Argus — a self-hosted code index and documentation server.

The version is read from the installed distribution rather than written here,
so it cannot drift from `pyproject.toml`. It falls back to `0+unknown` when
imported from a source tree that was never installed, which is what a bare
`PYTHONPATH=src python -c "import argus"` does — an honest answer rather than a
plausible wrong one.
"""
from __future__ import annotations

from importlib.metadata import PackageNotFoundError, version

try:
    __version__ = version("argus")
except PackageNotFoundError:          # not installed; running from a source tree
    __version__ = "0+unknown"

__all__ = ["__version__"]
