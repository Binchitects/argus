#!/usr/bin/env python3
"""Check a downloaded model against the checksums HuggingFace publishes.

Size is not proof. A proxy that answers a ranged request with the whole file
makes `curl -C -` append rather than resume, and the result is a file that is
the right kind of wrong: present, plausible, and larger than it should be.
Measured on this stack, every shard of a 19.6 GB download finished 5-20% too
large with duplicated bytes interleaved -- and the download reported 15/15
files complete, because both checks asked "at least the expected size".

A corrupt shard does not announce itself. It surfaces an hour later as a vLLM
crash during weight loading, or worse, as a model that loads and generates
nonsense. Hashing is the only way to know.

    python3 scripts/verify-model.py models/Qwen3.8-27B-AWQ-INT4
    python3 scripts/verify-model.py models/... --delete-bad

`--delete-bad` removes files that fail, so re-running get-models.sh refetches
exactly those and leaves the good ones alone.
"""
from __future__ import annotations

import argparse
import hashlib
import json
import os
import sys
import urllib.request

CHUNK = 1 << 22  # 4 MiB
GREEN, RED, YELLOW, DIM, OFF = (
    "\033[32m", "\033[31m", "\033[33m", "\033[90m", "\033[0m")


def repo_of(target: str) -> str | None:
    """The upstream repo, from the marker get-models.sh leaves behind."""
    for name in (".repo", ".source"):
        path = os.path.join(target, name)
        if os.path.exists(path):
            with open(path, encoding="utf-8") as fh:
                return fh.read().strip()
    return None


def published(repo: str) -> dict[str, tuple[int, str]]:
    """path -> (size, sha256) for every file the API lists."""
    url = f"https://huggingface.co/api/models/{repo}/tree/main?recursive=1"
    with urllib.request.urlopen(url, timeout=120) as resp:
        tree = json.load(resp)
    out = {}
    for entry in tree:
        if entry.get("type") != "file":
            continue
        lfs = entry.get("lfs") or {}
        # LFS files carry the real sha256; small plain files expose `oid`,
        # which is a git blob hash and NOT a sha256 of the content -- hashing
        # against it would fail every time and teach people to ignore this.
        sha = lfs.get("oid")
        out[entry["path"]] = (lfs.get("size") or entry.get("size", 0), sha)
    return out


def sha256_of(path: str) -> str:
    digest = hashlib.sha256()
    with open(path, "rb") as fh:
        while chunk := fh.read(CHUNK):
            digest.update(chunk)
    return digest.hexdigest()


def main() -> int:
    ap = argparse.ArgumentParser()
    ap.add_argument("target")
    ap.add_argument("--repo", help="override the upstream repo id")
    ap.add_argument("--delete-bad", action="store_true",
                    help="remove files that fail, so a refetch replaces them")
    args = ap.parse_args()

    repo = args.repo or repo_of(args.target)
    if not repo:
        return int(bool(sys.stderr.write(
            f"{RED}cannot tell which repo {args.target} came from{OFF}\n"
            f"  pass --repo <org/name>\n")))

    want = published(repo)
    ok = bad = skipped = 0
    for path, (size, sha) in sorted(want.items()):
        local = os.path.join(args.target, path)
        if not os.path.exists(local):
            continue                      # not fetched; get-models will get it
        actual = os.path.getsize(local)
        if actual != size:
            print(f"  {RED}SIZE{OFF}  {path}  {actual:,} != {size:,}")
            bad += 1
            if args.delete_bad:
                os.remove(local)
            continue
        if not sha:
            print(f"  {DIM}skip{OFF}  {path}  {DIM}(no published sha256){OFF}")
            skipped += 1
            continue
        got = sha256_of(local)
        if got == sha:
            print(f"  {GREEN}ok{OFF}    {path}")
            ok += 1
        else:
            print(f"  {RED}HASH{OFF}  {path}  content does not match")
            bad += 1
            if args.delete_bad:
                os.remove(local)

    print(f"\n  {ok} verified, {bad} bad, {skipped} unhashable")
    if bad and args.delete_bad:
        print(f"  {YELLOW}removed the bad files -- re-run get-models.sh to "
              f"refetch them{OFF}")
    return 1 if bad else 0


if __name__ == "__main__":
    sys.exit(main())
