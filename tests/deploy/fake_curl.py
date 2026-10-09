#!/usr/bin/env python3
"""
A stand-in for curl on PATH, for the installer's tests: the app's /api/info and
Argus's metrics answer with the version of the image the fake engine
(FAKE_STATE) runs for them: the tag of the reference it was loaded as, when that
is a version, else FAKE_OLD_VERSION. No running container: no answer (exit 7).
"""
from __future__ import annotations

import json
import os
import re
import sys

VERSION = re.compile(r"^\d+\.\d+\.\d+(-[0-9A-Za-z.-]+)?$")


def running(service: str) -> str | None:
    """The version the service's running container says it is."""
    try:
        with open(os.environ["FAKE_STATE"]) as f:
            s = json.load(f)
    except (KeyError, OSError, ValueError):
        return None
    for c in s.get("containers", []):
        if c["service"] == service and c["state"] == "running":
            tag = s.get("origins", {}).get(c["image_id"], "").rsplit(":", 1)[-1]
            return tag if VERSION.match(tag) else os.environ.get("FAKE_OLD_VERSION", "5.2.0")
    return None


def main() -> int:
    with open(os.environ["FAKE_LOG"], "a") as log:
        log.write(json.dumps(["curl"] + sys.argv[1:]) + "\n")
    url = next((a for a in sys.argv[1:] if a.startswith("https://")), "")
    if url.endswith("/api/info"):
        v = running("app")
        if v is None:
            return 7
        print(json.dumps({"name": "Argus Arena", "version": v}))
        return 0
    if url.endswith("/admin/metrics"):
        v = running("argus")
        if v is None:
            return 7
        print(f'argus_index_build_info{{version="{v}"}} 1')
        return 0
    return 7


if __name__ == "__main__":
    sys.exit(main())
