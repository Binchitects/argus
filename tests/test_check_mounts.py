"""The preflight bind-mount check.

`stack/scripts/lib/check_mounts.py` is not part of the argus package, but it
is the guard that stands between an operator and four unrelated-looking crash
loops, so it is tested here with everything else rather than only by running it.

The bug these tests pin: "cannot see it" is not "it is not there".
"""
from __future__ import annotations

import importlib.util
import os
from pathlib import Path

import pytest

_MODULE_PATH = (Path(__file__).resolve().parent.parent
                / "stack" / "scripts" / "lib" / "check_mounts.py")


def _load():
    spec = importlib.util.spec_from_file_location("check_mounts", _MODULE_PATH)
    assert spec and spec.loader
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    return module


check_mounts = _load()


# --- the false positive ----------------------------------------------------


@pytest.fixture
def unsearchable(monkeypatch):
    """An `os` where exactly one directory exists and is not searchable.

    Stubbed rather than chmod-ed: the suite runs as root in the container, and
    root is not subject to the permission bits this is about.
    """
    class FakePath:
        @staticmethod
        def abspath(p): return p

        @staticmethod
        def dirname(p): return "/var/lib/docker"

        @staticmethod
        def exists(p): return p == "/var/lib/docker"

    class FakeOS:
        path = FakePath
        X_OK = os.X_OK

        @staticmethod
        def access(p, mode):
            return p != "/var/lib/docker"

    monkeypatch.setattr(check_mounts, "os", FakeOS)


def test_a_path_under_an_unsearchable_directory_is_not_called_missing(unsearchable):
    """Stock Docker: /var/lib/docker is 0710 root:root, so an ordinary user
    cannot stat /var/lib/docker/containers at all and os.path.exists says False
    for a directory that is plainly there. The daemon binds it fine, because
    the daemon is root.

    Treating that as missing made Promtail's log mount look broken, which turned
    "enable the logging profile" into "preflight says your checkout has moved" --
    with instructions to tear down the stack and delete directories that do not
    need deleting.
    """
    assert check_mounts.invisible("/var/lib/docker/containers") is True
    assert check_mounts.why_not_real("/var/lib/docker/containers", None) is None


def test_absence_is_still_reported_when_every_ancestor_is_searchable(tmp_path):
    """The move detection must not be weakened: inside a directory we CAN read,
    a missing path is genuinely missing, and that is the whole point."""
    missing = tmp_path / "not-there"
    reason = check_mounts.why_not_real(str(missing), None)
    assert reason and "does not exist" in reason


def test_the_real_docker_root_is_handled_on_this_host():
    """Where Docker's root is both present and unsearchable -- the stock Linux
    install -- the mount must not be reported. Skipped wherever that is not the
    situation, which includes this suite running inside a container, where the
    host's /var/lib/docker does not exist in its namespace at all.

    The end-to-end version of this is `scripts/preflight.sh` on the host, which
    is what the operator actually runs.
    """
    docker_root = "/var/lib/docker"
    containers = f"{docker_root}/containers"
    if not os.path.isdir(docker_root) or os.path.exists(containers):
        pytest.skip("no unsearchable Docker root in this namespace")
    if os.access(docker_root, os.X_OK):
        pytest.skip("Docker's root is readable to this user")
    assert check_mounts.why_not_real(containers, None) is None


# --- the behaviour that was already there ----------------------------------


def test_an_empty_directory_inside_the_project_is_a_problem(tmp_path):
    """The signature of a moved checkout: Docker created an empty stub."""
    empty = tmp_path / "config" / "authelia"
    empty.mkdir(parents=True)
    reason = check_mounts.why_not_real(str(empty), str(tmp_path))
    assert reason and "EMPTY" in reason


def test_an_empty_directory_outside_the_project_is_not(tmp_path):
    """Somebody else's directory may legitimately be empty, and scanning it can
    cost more than the deployment."""
    outside = tmp_path / "models"
    outside.mkdir()
    assert check_mounts.why_not_real(str(outside), str(tmp_path / "elsewhere")) is None


def test_an_empty_file_is_a_problem(tmp_path):
    blank = tmp_path / "alertmanager.yml"
    blank.write_text("")
    reason = check_mounts.why_not_real(str(blank), str(tmp_path))
    assert reason and "empty file" in reason


def test_a_nonempty_file_is_fine(tmp_path):
    real = tmp_path / "promtail-config.yml"
    real.write_text("server: {}\n")
    assert check_mounts.why_not_real(str(real), str(tmp_path)) is None
