"""Tests for pack installation, listing and removal.

The failure tests assert on the filesystem as well as on the exception. The
guarantee is that a rejected pack leaves *nothing* -- not registered, not
staged, not half-written -- and "it raised" does not establish any of that.
"""

from __future__ import annotations

import hashlib
import json
import sqlite3
from pathlib import Path

import httpx
import pytest

from argus import embed as embed_module
from argus.packs import build, format as pack_format, registry
from argus.packs.registry import RegistryError
from argus.packs.sources.react_docs import ReactDocs

from .test_build import COMMIT, FIXTURES, fake_embed


@pytest.fixture
def good_pack(tmp_path_factory) -> Path:
    """One real, built pack, reused across tests."""
    out = tmp_path_factory.mktemp("built") / "react.arguspack"
    return build.build_pack(
        ReactDocs(), work_dir=FIXTURES / "react", out_path=out,
        version="1.0.0", embed_fn=fake_embed, source_commit=COMMIT,
    )


def sha256_of(path: Path) -> str:
    return hashlib.sha256(path.read_bytes()).hexdigest()


def rewrite_meta(path: Path, **kv: str) -> None:
    """Edit a built pack's pack_meta in place, to simulate a hostile or
    mismatched publisher."""
    conn = sqlite3.connect(path)
    try:
        conn.executemany(
            "INSERT INTO pack_meta (key, value) VALUES (?, ?) "
            "ON CONFLICT(key) DO UPDATE SET value = excluded.value",
            list(kv.items()),
        )
        conn.commit()
    finally:
        conn.close()


def leftovers(dest: Path) -> list[str]:
    return sorted(p.name for p in dest.iterdir())


def mock_client(handler) -> httpx.Client:
    return httpx.Client(transport=httpx.MockTransport(handler))


# --- install from a local path ------------------------------------------------


def test_install_verifies_and_registers(good_pack, tmp_path):
    dest = tmp_path / "packs"
    installed = registry.install(good_pack, dest_dir=dest, expected_sha256=sha256_of(good_pack))

    assert installed.name == "react"
    assert installed.version == "1.0.0"
    assert installed.path == dest / "react.arguspack"
    assert installed.path.is_file()
    assert installed.compatible
    assert [p.name for p in registry.list_installed(dest)] == ["react"]


def test_install_leaves_no_staging_file_behind(good_pack, tmp_path):
    dest = tmp_path / "packs"
    registry.install(good_pack, dest_dir=dest)
    assert leftovers(dest) == ["react.arguspack"]


def test_install_without_an_expected_checksum_still_validates_the_pack(good_pack, tmp_path):
    dest = tmp_path / "packs"
    assert registry.install(good_pack, dest_dir=dest).compatible


def test_installing_a_missing_file_raises_and_writes_nothing(tmp_path):
    dest = tmp_path / "packs"
    with pytest.raises(RegistryError, match="no such pack file"):
        registry.install(tmp_path / "absent.arguspack", dest_dir=dest)
    assert leftovers(dest) == []


# --- corruption is rejected, and leaves nothing --------------------------------


def test_a_checksum_mismatch_is_rejected_and_leaves_nothing(good_pack, tmp_path):
    """A truncated download that silently becomes a half-empty knowledge base
    is the failure this check exists for."""
    dest = tmp_path / "packs"
    with pytest.raises(RegistryError, match="checksum mismatch"):
        registry.install(good_pack, dest_dir=dest, expected_sha256="00" * 32)

    assert registry.list_installed(dest) == []
    assert leftovers(dest) == [], "a rejected pack left a file behind"


def test_a_truncated_pack_is_rejected_even_without_a_checksum(good_pack, tmp_path):
    truncated = tmp_path / "truncated.arguspack"
    truncated.write_bytes(good_pack.read_bytes()[: 4096])

    dest = tmp_path / "packs"
    with pytest.raises(RegistryError):
        registry.install(truncated, dest_dir=dest)
    assert registry.list_installed(dest) == []
    assert leftovers(dest) == []


def test_a_file_that_is_not_a_pack_is_rejected(tmp_path):
    junk = tmp_path / "junk.arguspack"
    junk.write_bytes(b"this is not a sqlite database")

    dest = tmp_path / "packs"
    with pytest.raises(RegistryError, match="not a readable pack|pack_meta"):
        registry.install(junk, dest_dir=dest)
    assert leftovers(dest) == []


# --- a hostile pack cannot name its own location -------------------------------


@pytest.mark.parametrize("hostile", [
    "../escaped",
    "../../.ssh/authorized_keys",
    "/absolute",
    "with space",
    "UPPER",
    "",
])
def test_a_pack_cannot_install_itself_outside_the_destination(good_pack, tmp_path, hostile):
    """source_name comes from pack_meta inside a file that may have been
    downloaded from anyone, and it becomes the installed filename."""
    hostile_pack = tmp_path / "hostile.arguspack"
    hostile_pack.write_bytes(good_pack.read_bytes())
    rewrite_meta(hostile_pack, source_name=hostile)

    dest = tmp_path / "packs"
    with pytest.raises(RegistryError, match="invalid pack name"):
        registry.install(hostile_pack, dest_dir=dest)

    assert leftovers(dest) == []
    assert not (tmp_path / "escaped.arguspack").exists()
    assert not (tmp_path.parent / "escaped.arguspack").exists()


def test_remove_rejects_a_traversing_name(tmp_path):
    with pytest.raises(RegistryError, match="invalid pack name"):
        registry.remove("../../etc/passwd", tmp_path)


# --- a mismatched embedding space installs, but is flagged ---------------------


def test_a_pack_built_with_another_model_installs_but_is_flagged(good_pack, tmp_path):
    """Lexical and symbol search do not depend on the embedding space, so a
    mismatched pack degrades rather than dies."""
    other = tmp_path / "other.arguspack"
    other.write_bytes(good_pack.read_bytes())
    rewrite_meta(other, embedding_model="some-other-model")

    dest = tmp_path / "packs"
    installed = registry.install(other, dest_dir=dest)

    assert installed.path.is_file(), "a mismatched pack should still install"
    assert not installed.compatible
    assert "some-other-model" in installed.incompatible_reason
    assert embed_module.EMBED_MODEL in installed.incompatible_reason


def test_a_pack_with_a_different_dimension_is_flagged(good_pack, tmp_path):
    other = tmp_path / "other.arguspack"
    other.write_bytes(good_pack.read_bytes())
    rewrite_meta(other, embedding_dim="384")

    installed = registry.install(other, dest_dir=tmp_path / "packs")
    assert not installed.compatible
    assert "384" in installed.incompatible_reason


# --- listing -------------------------------------------------------------------


def test_list_installed_reports_the_fields_an_operator_needs(good_pack, tmp_path):
    dest = tmp_path / "packs"
    registry.install(good_pack, dest_dir=dest)

    [pack] = registry.list_installed(dest)
    assert pack.name == "react"
    assert pack.version == "1.0.0"
    assert pack.embedding_model == embed_module.EMBED_MODEL
    assert pack.embedding_dim == str(embed_module.EMBED_DIM)
    assert pack.size_bytes == good_pack.stat().st_size
    assert pack.license == "CC-BY-4.0"
    assert pack.source_commit == COMMIT


def test_list_installed_on_an_absent_directory_is_empty(tmp_path):
    assert registry.list_installed(tmp_path / "nope") == []


def test_an_unreadable_pack_is_reported_not_silently_skipped(good_pack, tmp_path):
    """Omitting it would present a knowledge base quietly missing a source."""
    dest = tmp_path / "packs"
    registry.install(good_pack, dest_dir=dest)
    (dest / "broken.arguspack").write_bytes(b"not a database")

    listed = {p.name: p for p in registry.list_installed(dest)}
    assert set(listed) == {"react", "broken"}
    assert listed["broken"].compatible is False
    assert "unreadable" in listed["broken"].incompatible_reason
    assert listed["react"].compatible is True


# --- removal -------------------------------------------------------------------


def test_remove_deletes_an_installed_pack(good_pack, tmp_path):
    dest = tmp_path / "packs"
    registry.install(good_pack, dest_dir=dest)

    assert registry.remove("react", dest) is True
    assert registry.list_installed(dest) == []
    assert leftovers(dest) == []


def test_remove_returns_false_when_absent(tmp_path):
    dest = tmp_path / "packs"
    dest.mkdir()
    assert registry.remove("react", dest) is False


def test_reinstalling_replaces_the_previous_version(good_pack, tmp_path):
    dest = tmp_path / "packs"
    registry.install(good_pack, dest_dir=dest)

    newer = tmp_path / "newer.arguspack"
    newer.write_bytes(good_pack.read_bytes())
    rewrite_meta(newer, pack_version="2.0.0")

    installed = registry.install(newer, dest_dir=dest)
    assert installed.version == "2.0.0"
    assert leftovers(dest) == ["react.arguspack"]


# --- install over HTTP ---------------------------------------------------------


def test_install_from_a_url(good_pack, tmp_path):
    payload = good_pack.read_bytes()
    handler = lambda request: httpx.Response(200, content=payload)

    dest = tmp_path / "packs"
    installed = registry.install(
        "https://example.invalid/react.arguspack", dest_dir=dest,
        expected_sha256=sha256_of(good_pack), client=mock_client(handler),
    )
    assert installed.name == "react"
    assert installed.path.read_bytes() == payload


def test_a_download_that_fails_leaves_nothing(tmp_path):
    handler = lambda request: httpx.Response(404, text="gone")
    dest = tmp_path / "packs"

    with pytest.raises(RegistryError, match="404"):
        registry.install(
            "https://example.invalid/react.arguspack", dest_dir=dest,
            client=mock_client(handler),
        )
    assert leftovers(dest) == []


def test_a_truncated_download_is_caught_by_the_checksum(good_pack, tmp_path):
    truncated = good_pack.read_bytes()[:5000]
    handler = lambda request: httpx.Response(200, content=truncated)
    dest = tmp_path / "packs"

    with pytest.raises(RegistryError, match="checksum mismatch"):
        registry.install(
            "https://example.invalid/react.arguspack", dest_dir=dest,
            expected_sha256=sha256_of(good_pack), client=mock_client(handler),
        )
    assert leftovers(dest) == []


# --- the published index -------------------------------------------------------


INDEX = {
    "schema": 1,
    "packs": [
        {"name": "python", "version": "3.13", "url": "https://example.invalid/python.arguspack",
         "sha256": "ab" * 32, "size_bytes": 12345, "license": "PSF-2.0"},
        {"name": "react", "version": "1.0.0", "url": "https://example.invalid/react.arguspack",
         "sha256": "cd" * 32},
    ],
}


def test_fetch_index_parses_entries():
    handler = lambda request: httpx.Response(200, json=INDEX)
    entries = registry.fetch_index("https://example.invalid/index.json", client=mock_client(handler))

    assert [e.name for e in entries] == ["python", "react"]
    assert entries[0].sha256 == "ab" * 32
    assert entries[0].size_bytes == 12345
    assert entries[0].license == "PSF-2.0"
    assert entries[1].size_bytes == 0


def test_an_index_entry_without_a_checksum_is_malformed():
    """Without a checksum the download cannot be verified, so this is a broken
    index rather than an optional field."""
    body = {"packs": [{"name": "python", "version": "3.13",
                       "url": "https://example.invalid/p.arguspack"}]}
    handler = lambda request: httpx.Response(200, json=body)
    with pytest.raises(RegistryError, match="sha256"):
        registry.fetch_index("https://example.invalid/index.json", client=mock_client(handler))


def test_a_non_json_index_raises():
    handler = lambda request: httpx.Response(200, text="<html>nope</html>")
    with pytest.raises(RegistryError, match="not JSON"):
        registry.fetch_index("https://example.invalid/index.json", client=mock_client(handler))


def test_an_index_without_a_packs_list_raises():
    handler = lambda request: httpx.Response(200, json={"schema": 1})
    with pytest.raises(RegistryError, match="packs"):
        registry.fetch_index("https://example.invalid/index.json", client=mock_client(handler))


def test_an_index_http_error_raises():
    handler = lambda request: httpx.Response(500, text="boom")
    with pytest.raises(RegistryError, match="500"):
        registry.fetch_index("https://example.invalid/index.json", client=mock_client(handler))


def test_an_index_connection_error_raises():
    def handler(request):
        raise httpx.ConnectError("refused")
    with pytest.raises(RegistryError, match="refused"):
        registry.fetch_index("https://example.invalid/index.json", client=mock_client(handler))
