#!/usr/bin/env python3
"""Import real public C/C++ projects into the throwaway GitLab, at pinned refs.

`seed.py` builds the tiny hand-written fixture that makes the access-control
claim falsifiable. This script builds the *measurement* corpus: real projects,
large enough that the numbers in `docs/index-measurements.md` mean something.

    python deploy/test-gitlab/seed_corpus.py --tier baseline
    python deploy/test-gitlab/seed_corpus.py --tier scale
    python deploy/test-gitlab/seed_corpus.py --list

**Why pinned refs.** The first measurement corpus was cloned from each
project's default branch and squashed to a single commit, so the upstream
revision was unrecoverable -- the surviving clones report `1.3.2.1-motley` and
`1.8.0.git`, which are moving development versions, not releases. Every figure
derived from that corpus was therefore unreproducible: re-cloning a month
later gives different code, and a changed number could not be attributed to a
code change in Argus rather than a change upstream. A measurement you cannot
rebuild is an anecdote. Every entry below names a tag.

**Tokens.** Pushing needs credentials, and this reuses `argus.mirror`'s
GIT_ASKPASS helper rather than embedding the token in a remote URL. A URL
credential would land in `.git/config` on disk and in `ps` output, which is
exactly what the production path goes to some length to avoid; a seeding
script that does it the easy way teaches the wrong pattern and leaves a real
token on disk in a directory people copy from.
"""
from __future__ import annotations

import argparse
import json
import os
import pathlib
import shutil
import stat
import subprocess
import sys
import time

import httpx

sys.path.insert(0, str(pathlib.Path(__file__).resolve().parents[2]))
from argus.mirror import ARGUS_TOKEN_ENV, _askpass_program  # noqa: E402

GITLAB = "http://localhost:8929"
HERE = pathlib.Path(__file__).parent
WORK = HERE / "work"
CLONES = pathlib.Path(__file__).resolve().parents[2] / ".packwork" / "real"

#: name -> (upstream URL, tag). Tags, never branches -- see the module docstring.
#:
#: `baseline` is the four projects behind every figure in
#: docs/index-measurements.md. They deliberately share header basenames
#: (`config.h`, `zconf.h`) and vendor copies of each other, which is what
#: makes include resolution and which_repo non-trivial on them.
#:
#: `scale` exists to answer one question the roadmap has deferred twice:
#: whether SQLite holds up at real size. It is ordered so the ladder can be
#: climbed one rung at a time and stopped when something breaks.
TIERS: dict[str, list[tuple[str, str, str]]] = {
    "baseline": [
        ("zlib", "https://github.com/madler/zlib.git", "v1.3.1"),
        ("libpng", "https://github.com/pnggroup/libpng.git", "v1.6.44"),
        ("freetype", "https://gitlab.freedesktop.org/freetype/freetype.git", "VER-2-13-3"),
        ("libjpeg-turbo", "https://github.com/libjpeg-turbo/libjpeg-turbo.git", "3.0.4"),
    ],
    "scale": [
        ("curl", "https://github.com/curl/curl.git", "curl-8_11_0"),
        ("redis", "https://github.com/redis/redis.git", "7.4.1"),
        ("git", "https://github.com/git/git.git", "v2.47.0"),
        ("openssl", "https://github.com/openssl/openssl.git", "openssl-3.4.0"),
        ("postgres", "https://github.com/postgres/postgres.git", "REL_17_2"),
        ("ffmpeg", "https://github.com/FFmpeg/FFmpeg.git", "n7.1"),
    ],
    # An estate rather than a sample: 46 more projects, so the index is asked
    # the question a real deployment asks -- can it enumerate, mirror, index
    # and then ANSWER across dozens of repositories at once, where a symbol
    # name is no longer unique and `whichrepo` has to earn its place.
    #
    # Every tag here was read from the remote with `git ls-remote --tags`
    # rather than recalled. The first attempt sorted tags by "digits found
    # anywhere" and pinned sqlite to `bug-2026-08-12T09_26_04Z` and zstd to
    # `fuzz-corpora2` -- both real tags, neither a release, and a corpus
    # pinned to a fuzzing corpus measures nothing anyone means.
    "estate": [
("sqlite", "https://github.com/sqlite/sqlite.git", "version-3.53.4"),
        ("nginx", "https://github.com/nginx/nginx.git", "release-1.31.4"),
        ("vim", "https://github.com/vim/vim.git", "v9.2.0995"),
        ("tmux", "https://github.com/tmux/tmux.git", "3.7c"),
        ("zstd", "https://github.com/facebook/zstd.git", "v1.5.7"),
        ("lz4", "https://github.com/lz4/lz4.git", "v1.10.0"),
        ("protobuf", "https://github.com/protocolbuffers/protobuf.git", "v36.0"),
        ("grpc", "https://github.com/grpc/grpc.git", "v1.83.0"),
        ("leveldb", "https://github.com/google/leveldb.git", "1.23"),
        ("rocksdb", "https://github.com/facebook/rocksdb.git", "v11.8.1"),
        ("libuv", "https://github.com/libuv/libuv.git", "v1.52.1"),
        ("libgit2", "https://github.com/libgit2/libgit2.git", "v1.9.7"),
        ("jq", "https://github.com/jqlang/jq.git", "jq-1.8.2"),
        ("htop", "https://github.com/htop-dev/htop.git", "3.5.3"),
        ("cmake", "https://github.com/Kitware/CMake.git", "v4.4.2"),
        ("busybox", "https://github.com/mirror/busybox.git", "1_36_1"),
        ("openssh", "https://github.com/openssh/openssh-portable.git", "V_2_1_0"),
        ("imagemagick", "https://github.com/ImageMagick/ImageMagick.git", "7.0.7.7"),
        ("sdl", "https://github.com/libsdl-org/SDL.git", "release-3.4.14"),
        ("glfw", "https://github.com/glfw/glfw.git", "3.5.1"),
        ("assimp", "https://github.com/assimp/assimp.git", "v6.0.5"),
        ("bullet3", "https://github.com/bulletphysics/bullet3.git", "3.25"),
        ("box2d", "https://github.com/erincatto/box2d.git", "v3.1.1"),
        ("spdlog", "https://github.com/gabime/spdlog.git", "v1.17.0"),
        ("fmt", "https://github.com/fmtlib/fmt.git", "12.2.0"),
        ("catch2", "https://github.com/catchorg/Catch2.git", "v3.15.3"),
        ("googletest", "https://github.com/google/googletest.git", "v1.18.0"),
        ("abseil-cpp", "https://github.com/abseil/abseil-cpp.git", "20260817.0"),
        ("snappy", "https://github.com/google/snappy.git", "1.2.2"),
        ("brotli", "https://github.com/google/brotli.git", "v1.2.0"),
        ("pcre2", "https://github.com/PCRE2Project/pcre2.git", "pcre2-10.47"),
        ("expat", "https://github.com/libexpat/libexpat.git", "R_2_8_3"),
        ("libxml2", "https://gitlab.gnome.org/GNOME/libxml2.git", "LIBXML2_6_0"),
        ("harfbuzz", "https://github.com/harfbuzz/harfbuzz.git", "14.3.1"),
        ("opencv", "https://github.com/opencv/opencv.git", "5.0.0"),
        # llvm-project is deliberately absent. Even shallow it is ~2 GB, and
        # the transfer dropped with "unexpected eof" on ten consecutive
        # attempts here -- and because this script stops at the first clone
        # failure, that one repository blocked the ten listed after it. An
        # estate of 55 measures name collision and routing exactly as well
        # as 56 does, and does not depend on one 2 GB transfer succeeding.
        ("php-src", "https://github.com/php/php-src.git", "php-8.5.9"),
        ("ruby", "https://github.com/ruby/ruby.git", "v4.0.6"),
        ("node", "https://github.com/nodejs/node.git", "v26.7.0"),
        ("mruby", "https://github.com/mruby/mruby.git", "4.0.0"),
        ("lua", "https://github.com/lua/lua.git", "v5.5.1"),
        ("wolfssl", "https://github.com/wolfSSL/wolfssl.git", "v5.2.1"),
        ("mbedtls", "https://github.com/Mbed-TLS/mbedtls.git", "v4.2.0"),
        ("libsodium", "https://github.com/jedisct1/libsodium.git", "1.0.22"),
        ("czmq", "https://github.com/zeromq/libzmq.git", "v4.3.5"),
        ("duckdb", "https://github.com/duckdb/duckdb.git", "v1.5.5"),
    ],
}


def admin_token() -> str:
    """Read the admin token seed.py wrote. Never printed in full."""
    seeded = HERE / "seeded.json"
    if not seeded.exists():
        sys.exit("seeded.json missing -- run deploy/test-gitlab/seed.py first")
    data = json.loads(seeded.read_text())
    token = data.get("admin_token") or data.get("admin")
    if not token:
        sys.exit("no admin token in seeded.json")
    return token


def _rmtree(path: pathlib.Path) -> None:
    """Remove a tree, including git's read-only pack files.

    Not `ignore_errors=True`. git marks objects under `.git` read-only, and on
    Windows that makes unlink fail outright -- so ignoring errors leaves a
    `.git` directory behind, reports success, and the next `git clone` dies
    with "destination path already exists and is not an empty directory". The
    failure surfaces one step away from its cause, which is the worst place
    for it. Clear the read-only bit and retry instead.
    """
    def _retry(func, target, _exc):
        os.chmod(target, stat.S_IWRITE)
        func(target)

    if path.exists():
        shutil.rmtree(path, onexc=_retry)


def clone(name: str, url: str, ref: str) -> pathlib.Path:
    """Fetch exactly one tag, no history, into .packwork/real/<name>.

    `--depth 1 --branch <tag>` is the whole point: these projects carry a
    decade of history that costs gigabytes and contributes nothing, because
    Argus indexes a working tree, not a log.

    The clone lands in a sibling `.incoming` directory and replaces the real
    one only once it has succeeded. An earlier version deleted the existing
    tree first and left nothing behind when the clone then failed -- a corpus
    is expensive to rebuild over a slow link, and destroying the old copy
    before the new one exists trades a re-run for a re-download.
    """
    dest = CLONES / name
    stamp = dest / ".argus-corpus-ref"
    if stamp.exists() and stamp.read_text().strip() == ref:
        print(f"  {name}: already at {ref}")
        return dest

    staging = CLONES / f".{name}.incoming"
    _rmtree(staging)
    staging.parent.mkdir(parents=True, exist_ok=True)
    print(f"  {name}: cloning {ref}")
    proc = subprocess.run(
        ["git", "clone", "--depth", "1", "--branch", ref, url, str(staging)],
        capture_output=True, text=True, timeout=1800,
    )
    if proc.returncode != 0:
        _rmtree(staging)
        raise SystemExit(f"clone of {name} at {ref} failed:\n{proc.stderr.strip()[:1500]}")

    # The upstream history is dropped so the push is small, but the ref it came
    # from must survive -- that is the reproducibility this file exists for.
    _rmtree(staging / ".git")
    (staging / ".argus-corpus-ref").write_text(ref)
    _rmtree(dest)
    staging.replace(dest)
    return dest


def ensure_project(client: httpx.Client, name: str) -> tuple[int, str]:
    """Create the private project, or reuse it. Returns (id, http_url).

    Re-seeding force-pushes over whatever is there, so the default branch has
    to be unprotected first: GitLab refuses a force-push to a protected branch
    in a pre-receive hook, and does it even for an admin token. The rejection
    arrives as a bare "pre-receive hook declined" with no mention of branch
    protection, which is a long way from the cause.
    """
    r = client.post("/projects", json={
        "name": name, "path": name, "visibility": "private",
        "initialize_with_readme": False,
    })
    if r.status_code == 400 and "already been taken" in r.text:
        found = client.get("/projects", params={"search": name, "simple": True})
        found.raise_for_status()
        proj = next(p for p in found.json() if p["path"] == name)
    else:
        r.raise_for_status()
        proj = r.json()

    pid = proj["id"]
    for branch in ("main", "master"):
        client.delete(f"/projects/{pid}/protected_branches/{branch}")
    return pid, proj["http_url_to_repo"]


def push(tree: pathlib.Path, http_url: str, ref: str, env: dict[str, str]) -> None:
    """Commit the tree as a single revision and push it to GitLab."""
    git = ["git", "-C", str(tree)]
    subprocess.run(git + ["init", "-q", "-b", "main"], check=True, timeout=300)
    subprocess.run(git + ["config", "user.email", "corpus@argus.test"], check=True)
    subprocess.run(git + ["config", "user.name", "argus corpus"], check=True)
    subprocess.run(git + ["add", "-A"], check=True, timeout=1800)
    # `git commit` exits non-zero when there is nothing staged, which is the
    # normal state of a re-run: the ref stamp skipped the re-clone, so the
    # tree still carries last run's commit. Only commit when something is
    # actually staged, and treat an existing HEAD as already-committed.
    staged = subprocess.run(git + ["diff", "--cached", "--quiet"], capture_output=True)
    if staged.returncode != 0:
        subprocess.run(
            git + ["commit", "-q", "-m",
                   f"import {tree.name} at {ref} for indexing measurement"],
            check=True, timeout=1800,
        )
    elif subprocess.run(git + ["rev-parse", "HEAD"],
                        capture_output=True).returncode != 0:
        sys.exit(f"{tree.name}: nothing staged and no commit -- empty clone?")
    # `remote add` fails if a previous run already added it, and a re-run is
    # the normal case here -- the ref stamp skips the re-clone, so the tree
    # arrives carrying the .git from last time.
    subprocess.run(git + ["remote", "remove", "origin"], capture_output=True)
    subprocess.run(git + ["remote", "add", "origin", http_url], check=True)
    subprocess.run(git + ["push", "-q", "--force", "origin", "main"],
                   check=True, env=env, timeout=3600)


def main() -> int:
    ap = argparse.ArgumentParser(description=__doc__)
    ap.add_argument("--tier", choices=sorted(TIERS), default="baseline")
    ap.add_argument("--only", help="comma-separated subset of the tier")
    ap.add_argument("--list", action="store_true", help="print the tier and exit")
    args = ap.parse_args()

    entries = TIERS[args.tier]
    if args.only:
        want = {s.strip() for s in args.only.split(",")}
        entries = [e for e in entries if e[0] in want]
        missing = want - {e[0] for e in entries}
        if missing:
            sys.exit(f"not in tier {args.tier}: {', '.join(sorted(missing))}")

    if args.list:
        for name, url, ref in entries:
            print(f"{name:16} {ref:16} {url}")
        return 0

    token = admin_token()
    env = dict(os.environ)
    env["GIT_ASKPASS"] = str(_askpass_program(WORK / ".askpass"))
    env[ARGUS_TOKEN_ENV] = token

    client = httpx.Client(base_url=f"{GITLAB}/api/v4",
                          headers={"PRIVATE-TOKEN": token}, timeout=120)
    started = time.time()
    for name, url, ref in entries:
        t0 = time.time()
        tree = clone(name, url, ref)
        _pid, http_url = ensure_project(client, name)
        push(tree, http_url, ref, env)
        files = sum(1 for p in tree.rglob("*") if p.is_file())
        print(f"  {name}: pushed {files} files in {time.time() - t0:.1f}s")

    print(f"\ntier {args.tier}: {len(entries)} projects in {time.time() - started:.1f}s")
    print("now run: python -m argus.cli index --config deploy/test-gitlab/work/config.yaml")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
