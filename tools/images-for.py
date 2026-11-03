#!/usr/bin/env python3
"""The stack's images a change is in: the services of deploy/docker-compose.yml built here whose Dockerfile copies a
file that changed (or is the file that changed). Those are the images to build again, and the services to start again,
for the change to run: a change to Code Arena is in the app's image (it serves Code Arena's builds) and in Code Studio's
workspaces (each workspace's engine), not in one alone.

    python3 tools/images-for.py origin/main          what changed since origin/main, committed or not
    python3 tools/images-for.py A..B                 what changed from A to B
    git diff --name-only A B | python3 tools/images-for.py -

It prints the services, one a line, for docker compose in deploy/ (nothing when the change is in none: docker compose
given no service would build and start them all):

    images=$(python3 tools/images-for.py origin/main)
    [ -n "$images" ] && (cd deploy && docker compose build $images && docker compose up -d $images)
"""
from __future__ import annotations

import argparse
import fnmatch
import json
import posixpath
import re
import subprocess
import sys
from pathlib import Path

import yaml

ROOT = Path(__file__).resolve().parent.parent
DEPLOY = ROOT / "deploy"


def copied(dockerfile: str) -> list[str]:
    """What a Dockerfile's COPY and ADD take from its build context (not from another stage: --from)."""
    sources = []
    for line in re.sub(r"\\\n", " ", dockerfile).splitlines():
        m = re.match(r"\s*(COPY|ADD)\s+(.*)", line, re.IGNORECASE)
        if not m:
            continue
        words = m.group(2).split()
        flags = [w for w in words if w.startswith("--")]
        if any(f.startswith("--from=") for f in flags):
            continue
        rest = " ".join(w for w in words if not w.startswith("--"))
        args = json.loads(rest) if rest.startswith("[") else rest.split()
        sources += args[:-1]
    return sources


def images(compose: Path = DEPLOY / "docker-compose.yml") -> dict[str, list[str]]:
    """Each service built here: its Dockerfile and what it copies, as paths from the repository's root."""
    services = yaml.safe_load(compose.read_text())["services"]
    found = {}
    for name, service in services.items():
        build = service.get("build")
        if build is None:
            continue
        build = {"context": build} if isinstance(build, str) else build
        context = posixpath.normpath(posixpath.join(compose.parent.relative_to(ROOT).as_posix(), build.get("context", ".")))
        dockerfile = posixpath.normpath(posixpath.join(context, build.get("dockerfile", "Dockerfile")))
        paths = [posixpath.normpath(posixpath.join(context, s)) for s in copied((ROOT / dockerfile).read_text())]
        found[name] = [dockerfile, *paths]
    return found


def holds(source: str, path: str) -> bool:
    """Whether what a COPY takes (a file, a folder, a pattern) holds this file."""
    return source == "." or path == source or path.startswith(source + "/") or fnmatch.fnmatchcase(path, source)


def services_for(changed: list[str], built: dict[str, list[str]]) -> list[str]:
    """The services whose images hold any of the changed files."""
    return sorted(name for name, sources in built.items() if any(holds(s, p) for s in sources for p in changed))


def changed_since(rev: str) -> list[str]:
    """The files changed in a range (A..B), or since a commit: committed, staged, not staged, and new ones not ignored."""
    git = ["git", "-C", str(ROOT)]
    files = subprocess.run([*git, "diff", "--name-only", rev, "--"], check=True, capture_output=True, text=True).stdout.split()
    if ".." not in rev:
        files += subprocess.run([*git, "ls-files", "--others", "--exclude-standard"], check=True, capture_output=True, text=True).stdout.split()
    return files


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(description="The stack's images a change is in, to build and start again.")
    parser.add_argument("since", help="a commit (what changed since it), a range A..B, or - for the paths on stdin")
    args = parser.parse_args(argv)
    changed = sys.stdin.read().split() if args.since == "-" else changed_since(args.since)
    for name in services_for(changed, images()):
        print(name)
    return 0


if __name__ == "__main__":
    sys.exit(main())
