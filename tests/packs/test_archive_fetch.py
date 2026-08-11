"""Tests for the archive fetch path.

No network: ``urlopen`` is replaced with a local file. What is exercised is
what the code actually decides -- digest, extraction safety, provenance -- not
whether urllib works.

The traversal cases build REAL malicious archives rather than asserting on a
name-checking helper in isolation. A guard that is correct in the abstract and
never reached is the failure mode worth catching here.
"""

from __future__ import annotations

import hashlib
import io
import tarfile
import zipfile
from dataclasses import dataclass
from pathlib import Path

import pytest

from argus.packs import build


@dataclass(frozen=True)
class ArchiveSource:
    name: str = "demo"
    archive_url: str = "https://example.invalid/docs.zip"
    archive_sha256: str = ""
    license: str = "CC-BY-4.0"


def _serve(monkeypatch, payload: bytes):
    """Point urlopen at bytes, so the test covers our code and not the net."""
    class _Response(io.BytesIO):
        # Content-Length is optional in HTTP and absent here on purpose: the
        # length check must not fail a server that declines to declare one.
        headers: dict[str, str] = {}

        def __enter__(self):
            return self

        def __exit__(self, *exc):
            self.close()
            return False

    monkeypatch.setattr(
        build.urllib.request, "urlopen",
        lambda url, timeout=None: _Response(payload),
    )


def _zip_bytes(entries: dict[str, str]) -> bytes:
    buf = io.BytesIO()
    with zipfile.ZipFile(buf, "w") as zf:
        for name, text in entries.items():
            zf.writestr(name, text)
    return buf.getvalue()


def _tar_bytes(entries: dict[str, str]) -> bytes:
    buf = io.BytesIO()
    with tarfile.open(fileobj=buf, mode="w") as tf:
        for name, text in entries.items():
            data = text.encode()
            info = tarfile.TarInfo(name)
            info.size = len(data)
            tf.addfile(info, io.BytesIO(data))
    return buf.getvalue()


class TestExtraction:
    def test_unpacks_a_zip_and_records_the_digest(self, tmp_path, monkeypatch):
        payload = _zip_bytes({"docs/index.html": "<h1>hi</h1>"})
        _serve(monkeypatch, payload)

        stamp = build.fetch_archive(ArchiveSource(), tmp_path / "work")

        assert (tmp_path / "work" / "docs" / "index.html").read_text() == "<h1>hi</h1>"
        assert stamp == "sha256:" + hashlib.sha256(payload).hexdigest()

    def test_unpacks_a_tarball(self, tmp_path, monkeypatch):
        _serve(monkeypatch, _tar_bytes({"doc/lang.html": "select"}))
        src = ArchiveSource(archive_url="https://example.invalid/docs.tar.gz")

        build.fetch_archive(src, tmp_path / "work")

        assert (tmp_path / "work" / "doc" / "lang.html").read_text() == "select"

    def test_the_downloaded_archive_is_not_left_behind(self, tmp_path, monkeypatch):
        _serve(monkeypatch, _zip_bytes({"a.html": "x"}))
        build.fetch_archive(ArchiveSource(), tmp_path / "work")
        leftovers = [p.name for p in (tmp_path / "work").iterdir()
                     if p.name.startswith(".download")]
        assert leftovers == []


class TestDigest:
    def test_a_declared_digest_that_matches_is_accepted(self, tmp_path, monkeypatch):
        payload = _zip_bytes({"a.html": "x"})
        _serve(monkeypatch, payload)
        src = ArchiveSource(archive_sha256=hashlib.sha256(payload).hexdigest())

        assert build.fetch_archive(src, tmp_path / "work").startswith("sha256:")

    def test_a_mismatched_digest_fails_the_build_and_writes_nothing(
        self, tmp_path, monkeypatch
    ):
        """Substituting the archive upstream must not silently repackage."""
        _serve(monkeypatch, _zip_bytes({"a.html": "x"}))
        src = ArchiveSource(archive_sha256="00" * 32)

        with pytest.raises(build.BuildError, match="digest mismatch"):
            build.fetch_archive(src, tmp_path / "work")

        assert not (tmp_path / "work" / "a.html").exists()
        assert not list((tmp_path / "work").glob(".download*"))


class TestTraversal:
    """Real malicious archives, not a unit test of the name checker."""

    def test_a_zip_escaping_the_destination_is_refused(self, tmp_path, monkeypatch):
        _serve(monkeypatch, _zip_bytes({"../escaped.html": "pwned"}))

        with pytest.raises(build.BuildError, match="escapes the destination"):
            build.fetch_archive(ArchiveSource(), tmp_path / "work")

        assert not (tmp_path / "escaped.html").exists()

    def test_a_nested_traversal_is_refused(self, tmp_path, monkeypatch):
        _serve(monkeypatch, _zip_bytes({"docs/../../escaped.html": "pwned"}))

        with pytest.raises(build.BuildError, match="escapes the destination"):
            build.fetch_archive(ArchiveSource(), tmp_path / "work")

        assert not (tmp_path / "escaped.html").exists()

    def test_an_absolute_member_is_refused(self, tmp_path, monkeypatch):
        _serve(monkeypatch, _tar_bytes({"/etc/passwd": "root"}))
        src = ArchiveSource(archive_url="https://example.invalid/d.tar")

        with pytest.raises(build.BuildError, match="escapes the destination"):
            build.fetch_archive(src, tmp_path / "work")

    def test_a_windows_absolute_member_is_refused(self, tmp_path, monkeypatch):
        _serve(monkeypatch, _zip_bytes({"C:/windows/system32/evil.dll": "x"}))

        with pytest.raises(build.BuildError, match="absolute path"):
            build.fetch_archive(ArchiveSource(), tmp_path / "work")


class TestProvenance:
    def test_a_rebuild_states_provenance_without_downloading_again(
        self, tmp_path, monkeypatch
    ):
        """The stamp is what makes a cheap rebuild possible.

        Rebuilds are the common case because the embedding cache makes them
        near-free, and re-downloading hundreds of MB to restate a digest we
        already know would undo that.
        """
        payload = _zip_bytes({"a.html": "x"})
        _serve(monkeypatch, payload)
        work = tmp_path / "work"
        stamp = build.fetch_archive(ArchiveSource(), work)

        resolved = build._resolve_source_commit(ArchiveSource(), work)

        assert resolved == stamp
        assert resolved == "sha256:" + hashlib.sha256(payload).hexdigest()

    def test_a_directory_with_no_stamp_falls_through_to_git(self, tmp_path):
        """Archive handling must not swallow the git path."""
        assert build._resolve_source_commit(ArchiveSource(), tmp_path) is None


class TestDispatch:
    def test_fetch_source_routes_an_archive_source_to_the_archive_path(
        self, tmp_path, monkeypatch
    ):
        """A source declaring archive_url must never attempt a clone."""
        _serve(monkeypatch, _zip_bytes({"a.html": "x"}))
        monkeypatch.setattr(
            build, "_git",
            lambda *a, **k: pytest.fail("archive source attempted a git clone"),
        )

        stamp = build.fetch_source(ArchiveSource(), tmp_path / "work")

        assert stamp.startswith("sha256:")
        assert (tmp_path / "work" / "a.html").is_file()

    def test_a_source_without_an_archive_url_is_rejected_clearly(self, tmp_path):
        with pytest.raises(build.BuildError, match="declares no archive_url"):
            build.fetch_archive(ArchiveSource(archive_url=""), tmp_path / "w")


class TestTruncation:
    """A short read is not an error to urllib -- the stream just ends."""

    def _serve_short(self, monkeypatch, payload, declared):
        class _Response(io.BytesIO):
            headers = {"Content-Length": str(declared)}

            def __enter__(self):
                return self

            def __exit__(self, *exc):
                self.close()
                return False

        monkeypatch.setattr(
            build.urllib.request, "urlopen",
            lambda url, timeout=None: _Response(payload),
        )

    def test_a_truncated_download_fails_as_a_truncated_download(
        self, tmp_path, monkeypatch
    ):
        """Observed for real: a proxy cut an 11.8 MB archive at 720,896 bytes.

        Without this the bytes are accepted, the unpack fails, and the error
        blames the archive format -- pointing the reader at the wrong problem
        entirely.
        """
        payload = _zip_bytes({"a.html": "x"})
        self._serve_short(monkeypatch, payload, len(payload) + 5_000_000)

        with pytest.raises(build.BuildError, match="truncated download"):
            build.fetch_archive(ArchiveSource(), tmp_path / "work")

        assert not list((tmp_path / "work").glob(".download*"))
        assert not (tmp_path / "work" / "a.html").exists()

    def test_a_complete_download_passes_the_length_check(self, tmp_path, monkeypatch):
        payload = _zip_bytes({"a.html": "x"})
        self._serve_short(monkeypatch, payload, len(payload))

        assert build.fetch_archive(ArchiveSource(), tmp_path / "work")
        assert (tmp_path / "work" / "a.html").is_file()

    def test_a_server_that_declares_no_length_is_still_accepted(
        self, tmp_path, monkeypatch
    ):
        """Content-Length is optional; absence must not fail the build."""
        _serve(monkeypatch, _zip_bytes({"a.html": "x"}))
        assert build.fetch_archive(ArchiveSource(), tmp_path / "work")
