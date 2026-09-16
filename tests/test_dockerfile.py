"""The test stage must carry every repo file the suite reads.

WHY THIS EXISTS

The Dockerfile's test stage copies `tests/` wholesale but not `stack/` or the
top-level `scripts/` -- deliberately, because `stack/` holds the deployment's
`.env` and the fixture's real GitLab tokens, and image layers keep whatever a
later step deletes. So each file a test needs from over there is copied **by
hand**, and forgetting one produces a failure with no trace of the cause:

    FileNotFoundError: [Errno 2] No such file or directory: '.../stack/...'
    ERROR tests/test_acceptance.py - during collection

That is a collection error inside `docker build`. The host run passes, so
nothing warns; the image simply stops building, and the airgap bundle then
cannot build its own images. It has happened four times --
`scripts/agent_client_example.py`, `stack/scripts/lib/check_mounts.py`,
`stack/deploy/seed-presets.py` and `stack/scripts/acceptance.py` -- and each
time the fix was one `COPY` line plus a comment explaining it again.

So this test computes the list instead of trusting it. It scans every test
module for paths outside the package, and requires each one to be reachable
from a `COPY` in the test stage. The fifth occurrence fails here, on the
developer's machine, with the line to add.
"""
from __future__ import annotations

import re
from pathlib import Path

import pytest

ROOT = Path(__file__).resolve().parent.parent
DOCKERFILE = ROOT / "Dockerfile"

#: Directories the test stage already copies wholesale.
COPIED_WHOLESALE = ("tests/", "src/argus/")

#: A path literal in a test that points into the repository but outside the
#: package -- `stack/...`, `scripts/...`. Deliberately only these two trees:
#: `tests/` travels with the suite and `src/argus/` is the package itself.
_PATH_LITERAL = re.compile(
    r"""["']((?:stack|scripts)/[A-Za-z0-9_][A-Za-z0-9_./-]*\.[A-Za-z0-9]+)["']""")


def stage_text() -> str:
    if not DOCKERFILE.is_file():
        # Inside the image, where this file does not travel. The check is a
        # development-time one: it exists to fail BEFORE `docker build`, on the
        # machine where the COPY line can still be added. Running it during the
        # build could only report a problem the build can no longer fix, while
        # making every build depend on shipping its own Dockerfile.
        pytest.skip("no Dockerfile here: this guard runs on the host, not in the image")
    text = DOCKERFILE.read_text(encoding="utf-8")
    assert "FROM base AS test" in text, "the test stage disappeared from the Dockerfile"
    stage = text.split("FROM base AS test", 1)[1]
    # Up to the next stage, so a COPY in `runtime` cannot satisfy this by
    # accident -- the test suite never runs there.
    return stage.split("\nFROM ", 1)[0]


def copied_sources() -> set[str]:
    """Every source path the test stage COPYs, as written."""
    return {m.group(1) for m in re.finditer(r"^COPY\s+(\S+)", stage_text(), re.M)}


def reachable(path: str, copies: set[str]) -> bool:
    for src in copies:
        if src == path:
            return True
        # A directory COPY (`COPY tests/ ./tests/`) carries everything under it.
        if src.endswith("/") and path.startswith(src):
            return True
        if path.startswith(src.rstrip("/") + "/"):
            return True
    return False


def referenced_stack_files() -> dict[str, list[str]]:
    """Repo files the suite reads, mapped to the test modules that name them."""
    found: dict[str, list[str]] = {}
    for module in sorted((ROOT / "tests").rglob("*.py")):
        text = module.read_text(encoding="utf-8", errors="replace")
        for match in _PATH_LITERAL.finditer(text):
            path = match.group(1)
            if not (ROOT / path).exists():
                # A path that is not in the repository is not a file the image
                # needs to carry; it may be generated, or the assertion of its
                # absence may be the point.
                continue
            found.setdefault(path, []).append(module.name)
    return found


def test_every_required_stack_file_is_copied_into_the_test_stage():
    copies = copied_sources()
    referenced = referenced_stack_files()
    assert referenced, ("found no repo file references in tests/ at all, which "
                        "means this scan has stopped working rather than that "
                        "there is nothing to check")
    missing = {p: mods for p, mods in referenced.items() if not reachable(p, copies)}
    detail = "\n".join(
        f"  {p}  (read by {', '.join(sorted(set(mods)))})\n"
        f"    add to the test stage:  COPY {p} ./{Path(p).parent}/"
        for p, mods in sorted(missing.items()))
    assert not missing, (
        "the Dockerfile's test stage does not carry every repo file the suite "
        f"reads. These will fail ONLY inside `docker build`:\n{detail}")


def test_the_scan_would_notice_a_file_that_is_not_copied():
    """The check above is only worth having if it can fail.

    This pins the two halves of it against a real example, so a refactor that
    quietly makes `reachable` always return True is caught here rather than by
    the next person whose image will not build.
    """
    copies = copied_sources()
    # A file the stage genuinely does not copy, at a path the regex would find.
    assert not reachable("stack/deploy/does-not-exist.py", copies)
    # ...and one it demonstrably does, from the list this test maintains.
    assert "stack/scripts/lib/check_mounts.py" in copies, (
        "the known-good COPY line vanished; this test's premise is gone")
    assert reachable("stack/scripts/lib/check_mounts.py", copies)
    assert reachable("tests/mcpsrv/test_metrics.py", {"tests/"})


@pytest.mark.parametrize("path", sorted(referenced_stack_files()))
def test_the_referenced_file_exists(path):
    assert (ROOT / path).is_file(), f"{path} is referenced by a test but missing"
