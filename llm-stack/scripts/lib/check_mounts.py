#!/usr/bin/env python3
"""Fail when this stack's bind mounts resolve to nothing.

    scripts/lib/check_mounts.py planned    <project-dir> <compose-config.json>
    scripts/lib/check_mounts.py containers <project-dir> <docker-inspect.json>

Called by `scripts/preflight.sh`; runnable by hand, which is how it is tested.

--------------------------------------------------------------------------------
The failure this exists for
--------------------------------------------------------------------------------

Docker CREATES a missing bind-mount source directory. So when this checkout
MOVES -- a renamed disk, a second machine, a USB volume that was not mounted
when dockerd started -- the containers keep the OLD absolute paths. Docker has
already created empty, root-owned directories there to satisfy them, so nothing
errors. Compose reports every container "up".

What it looks like afterwards is four unrelated failures, three of them naming
files that plainly exist on the host:

  * Authelia crash-looping on a missing configuration.template.yml, which it
    then tries to GENERATE and reports as "read-only file system";
  * Alertmanager crash-looping on a missing alertmanager.yml;
  * the CPU temperature exporter crash-looping on a missing exporter.py;
  * Traefik exiting 127 without ever logging a reason.

Every one of those diagnoses is wrong, because nothing about the containers is
wrong. The mount is. Observed exactly that after this checkout moved from
".../New Volume/..." to ".../New Volume1/...".

--------------------------------------------------------------------------------
Why checking the CURRENT directory's mounts is not enough
--------------------------------------------------------------------------------

A `docker compose config` rendered from the checkout you are standing in names
the paths of the checkout you are standing in, and those exist. That check
catches a checkout missing files from a bad clone, and nothing else.

The stale mounts live on the EXISTING CONTAINERS. So `containers` mode reads
each container's recorded working directory and compares it with the current
one. That comparison, not any filesystem test, is what catches the move -- and
it is reported once per container rather than once per mount, because it has one
cause and one fix.

--------------------------------------------------------------------------------
What is checked, and what is deliberately not
--------------------------------------------------------------------------------

A bind source must exist. A source that is a regular file must be non-empty. A
source that is a DIRECTORY under the project directory must contain at least one
regular file at some depth -- an empty directory inside the checkout is the
signature of the failure above.

Directories OUTSIDE the project are only required to exist. A bind of /proc,
/sys or a model directory belongs to somebody else, may legitimately be empty
here, and scanning it could cost more than the whole deployment.

Non-regular sources -- the Docker socket, a device -- are only required to
exist. `os.path.isfile` is False for a socket, so treating "not a file" as
"missing" would fail on a perfectly healthy `/var/run/docker.sock`.
"""
from __future__ import annotations

import json
import os
import sys

#: Stop looking for a file after this many entries. A bind mount can point at a
#: directory holding hundreds of GB of model weights; the answer is knowable
#: from the first entry, and walking the rest would be a slow way to learn it.
_SCAN_LIMIT = 5000

WORKING_DIR_LABEL = "com.docker.compose.project.working_dir"


def first_regular_file(directory: str, limit: int = _SCAN_LIMIT) -> bool:
    """True as soon as one regular file exists at any depth under `directory`."""
    seen = 0
    stack = [directory]
    while stack:
        try:
            entries = list(os.scandir(stack.pop()))
        except OSError:
            # An unreadable subdirectory is not evidence of an empty mount.
            continue
        for entry in entries:
            seen += 1
            if seen > limit:
                return True
            try:
                if entry.is_file(follow_symlinks=False):
                    return True
                if entry.is_dir(follow_symlinks=False):
                    stack.append(entry.path)
            except OSError:
                continue
    return False


def why_not_real(source: str, project: str | None) -> str | None:
    """Why `source` is not usable bind-mount content, or None if it is fine."""
    if not os.path.exists(source):
        return f"{source} does not exist"

    if os.path.isdir(source):
        # Only INSIDE a project directory can an empty directory mean "the
        # mount did not resolve". /proc and /sys are somebody else's business.
        if project and os.path.realpath(source).startswith(
                os.path.realpath(project) + os.sep):
            if not first_regular_file(source):
                return f"{source} is an EMPTY directory (no files at any depth)"
        return None

    if os.path.isfile(source):
        if os.path.getsize(source) == 0:
            return f"{source} is an empty file"
        return None

    # A socket or a device: existing is the whole requirement. Docker
    # publishes /var/run/docker.sock this way.
    return None


def check_planned(project: str, cfg: dict) -> list[tuple[str, str, str]]:
    """Problems with the mounts the config in THIS directory asks for."""
    problems: list[tuple[str, str, str]] = []
    for service in sorted(cfg.get("services", {})):
        for mount in cfg["services"][service].get("volumes", None) or []:
            if mount.get("type") != "bind":
                continue
            source = mount.get("source")
            if not source:
                continue
            reason = why_not_real(source, project)
            if reason:
                problems.append((service, mount.get("target", "?"), reason))
    return problems


def check_containers(project: str,
                     containers: list[dict]) -> list[tuple[str, str, str]]:
    """Problems with the mounts this stack's EXISTING containers actually hold."""
    problems: list[tuple[str, str, str]] = []
    project = os.path.realpath(project)

    for container in containers:
        name = str(container.get("Name") or "?").lstrip("/")
        labels = (container.get("Config") or {}).get("Labels") or {}
        created_in = labels.get(WORKING_DIR_LABEL)

        if created_in and os.path.realpath(created_in) != project:
            # One problem, one cause, one fix. Reporting each of this
            # container's empty mounts as well would bury the sentence that
            # explains all of them.
            problems.append((
                name, "every bind mount",
                f"was created from {created_in}, which is not {project}. "
                f"Recreate the stack from this directory."))
            continue

        # Relative to where the container was created, which is the current
        # directory whenever the check above passed.
        base = created_in or project
        for mount in container.get("Mounts") or []:
            if mount.get("Type") != "bind":
                continue
            source = mount.get("Source")
            if not source:
                continue
            reason = why_not_real(source, base)
            if reason:
                problems.append((name, mount.get("Destination", "?"), reason))
    return problems


def check_staged(original: str, staged: str,
                 cfg: dict) -> list[tuple[str, str, str]]:
    """Problems with an airgap bundle, checked BEFORE it is zipped.

    The config was rendered from the real checkout, so its bind sources are
    absolute paths under `original`. This maps each of those into `staged` and
    asks whether the bundle actually carries it.

    Worth doing at bundle time because the alternative is discovering it at the
    far end of an airgap transfer: a missing `config/authelia/secrets`
    directory, or a `.gitkeep` that a `.gitignore`-respecting copy dropped,
    produces exactly the empty-mount crash loop this module exists to catch --
    except now it is on a machine with no way to fetch what is missing.
    """
    original_root = os.path.realpath(original)
    problems: list[tuple[str, str, str]] = []

    for service in sorted(cfg.get("services", {})):
        for mount in cfg["services"][service].get("volumes", None) or []:
            if mount.get("type") != "bind":
                continue
            source = mount.get("source")
            if not source:
                continue
            real = os.path.realpath(source)
            if not real.startswith(original_root + os.sep):
                # Outside the checkout: a model directory, /proc, the Docker
                # socket. Not the bundle's job to carry it.
                continue

            relative = os.path.relpath(real, original_root)
            landed = os.path.join(staged, relative)

            if not os.path.exists(landed):
                problems.append((
                    service, mount.get("target", "?"),
                    f"{relative} is NOT in the bundle"))

    return problems


#: What to say when a LIVE deployment has mounts pointing at nothing.
_MOVED_ADVICE = (
    "  The files are almost certainly present in the checkout you are in,",
    "  which is what a MOVED CHECKOUT looks like: containers were created",
    "  from an older absolute path, Docker created empty directories there,",
    "  and the mounts point at the old location.",
    "",
    "  Recreate the stack from THIS directory:",
    "      docker compose down --remove-orphans && docker compose up -d",
    "  Then delete the stale directories, so the next `up` cannot silently",
    "  bind to them again.",
)

#: What to say when a bundle is missing files it will need at the far end.
_BUNDLE_ADVICE = (
    "  The bundle is INCOMPLETE. Do not ship it.",
    "",
    "  On the far side of an airgap transfer there is no way to fetch what is",
    "  missing, and an empty config directory produces exactly the crash loops",
    "  the preflight exists to catch -- Authelia regenerating a config it",
    "  cannot find, Alertmanager missing its alertmanager.yml.",
    "",
    "  Almost always this is a placeholder a .gitignore-respecting copy",
    "  dropped. Check that the source checkout still has them, then rebuild:",
    "      llm-stack/scripts/airgap-bundle.sh",
)


def _report(problems: list[tuple[str, str, str]], noun: str,
            advice: tuple[str, ...]) -> int:
    if not problems:
        return 0
    print()
    print("\033[31mpreflight: bind mounts resolve to nothing.\033[0m")
    print()
    for service, target, detail in problems:
        print(f"  {service:20} {target}")
        print(f"  {'':20} -> {detail}")
    print()
    print(f"  These are the mounts {noun}.")
    for line in advice:
        print(line)
    print()
    return 1


def main(argv: list[str]) -> int:
    usage = ("usage: check_mounts.py planned|containers <project-dir> <json>\n"
             "       check_mounts.py staged <original-dir> <staged-dir> <json>")
    mode = argv[1] if len(argv) > 1 else ""
    if mode not in ("planned", "containers", "staged"):
        print(usage, file=sys.stderr)
        return 2

    if mode == "staged":
        if len(argv) != 5:
            print(usage, file=sys.stderr)
            return 2
        original, staged, path = argv[2], argv[3], argv[4]
    else:
        if len(argv) != 4:
            print(usage, file=sys.stderr)
            return 2
        original, staged, path = argv[2], None, argv[3]

    try:
        with open(path, encoding="utf-8") as handle:
            payload = json.load(handle)
    except (OSError, ValueError) as exc:
        print(f"preflight: could not read {path}: {exc}", file=sys.stderr)
        return 1

    if mode == "planned":
        return _report(check_planned(original, payload),
                       "the configuration in this directory asks for",
                       _MOVED_ADVICE)
    if mode == "containers":
        return _report(check_containers(original, payload),
                       "the existing containers actually hold",
                       _MOVED_ADVICE)
    return _report(check_staged(original, staged, payload),
                   "the bundle must carry",
                   _BUNDLE_ADVICE)


if __name__ == "__main__":
    raise SystemExit(main(sys.argv))
