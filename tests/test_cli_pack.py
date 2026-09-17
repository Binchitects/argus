"""Tests for `argus pack ...`.

The failure tests check the filesystem as well as the exit code: a non-zero
exit that still left a corrupted pack installed would be the worst of both.
"""

from __future__ import annotations

import dataclasses
import hashlib
import sqlite3

import pytest

from argus import embed as embed_module
from argus.cli import EXIT_PACK, main
from argus.packs import build, registry
from argus.packs.sources.python_docs import PythonDocs
from argus.packs.sources.react_docs import ReactDocs

from tests.packs.test_build import COMMIT, FIXTURES, fake_embed


@pytest.fixture(scope="module")
def good_pack(tmp_path_factory):
    out = tmp_path_factory.mktemp("built") / "react.arguspack"
    return build.build_pack(
        ReactDocs(), work_dir=FIXTURES / "react", out_path=out,
        version="1.0.0", embed_fn=fake_embed, source_commit=COMMIT,
    )


@pytest.fixture
def dest(tmp_path):
    directory = tmp_path / "packs"
    directory.mkdir()
    return directory


def run(*argv) -> int:
    return main([str(a) for a in argv])


def installed(dest):
    return sorted(p.name for p in dest.iterdir())


# --- list ----------------------------------------------------------------------


def test_list_on_an_empty_registry_is_not_an_error(dest, capsys):
    """A script that lists before installing must not be broken by an empty
    directory being treated as a failure."""
    assert run("pack", "list", "--packs-dir", dest) == 0
    assert "no packs installed" in capsys.readouterr().out


def test_list_shows_name_version_model_and_licence(good_pack, dest, capsys):
    assert run("pack", "install", good_pack, "--packs-dir", dest) == 0
    capsys.readouterr()

    assert run("pack", "list", "--packs-dir", dest) == 0
    out = capsys.readouterr().out
    assert "react" in out
    assert "1.0.0" in out
    assert embed_module.EMBED_MODEL in out
    assert "CC-BY-4.0" in out


# --- install -------------------------------------------------------------------


def test_install_from_a_path_registers_the_pack(good_pack, dest, capsys):
    assert run("pack", "install", good_pack, "--packs-dir", dest) == 0
    assert installed(dest) == ["react.arguspack"]
    assert "installed react 1.0.0" in capsys.readouterr().out


def test_install_verifies_a_supplied_checksum(good_pack, dest):
    digest = hashlib.sha256(good_pack.read_bytes()).hexdigest()
    assert run("pack", "install", good_pack, "--sha256", digest,
               "--packs-dir", dest) == 0
    assert installed(dest) == ["react.arguspack"]


def test_install_on_a_corrupted_file_exits_non_zero_and_registers_nothing(
    good_pack, dest, tmp_path, capsys,
):
    corrupt = tmp_path / "corrupt.arguspack"
    corrupt.write_bytes(good_pack.read_bytes()[:4096])

    assert run("pack", "install", corrupt, "--packs-dir", dest) == EXIT_PACK
    assert installed(dest) == [], "a corrupted pack was left installed"
    assert "pack error" in capsys.readouterr().err


def test_install_on_a_checksum_mismatch_exits_non_zero_and_registers_nothing(
    good_pack, dest, capsys,
):
    assert run("pack", "install", good_pack, "--sha256", "00" * 32,
               "--packs-dir", dest) == EXIT_PACK
    assert installed(dest) == []
    assert "checksum mismatch" in capsys.readouterr().err


def test_install_warns_but_succeeds_for_a_mismatched_model(good_pack, dest, tmp_path, capsys):
    other = tmp_path / "other.arguspack"
    other.write_bytes(good_pack.read_bytes())
    conn = sqlite3.connect(other)
    conn.execute("UPDATE pack_meta SET value = ? WHERE key = 'embedding_model'",
                 ("some-other-model",))
    conn.commit()
    conn.close()

    assert run("pack", "install", other, "--packs-dir", dest) == 0
    captured = capsys.readouterr()
    assert installed(dest) == ["react.arguspack"]
    assert "warning" in captured.err
    assert "still work" in captured.err


# --- info: the redistribution obligation ---------------------------------------


def test_info_prints_the_licence_and_the_full_attribution(good_pack, dest, capsys):
    """This output is how a user meets the redistribution obligation, so the
    exact strings matter, not merely that something was printed."""
    run("pack", "install", good_pack, "--packs-dir", dest)
    capsys.readouterr()

    assert run("pack", "info", "react", "--packs-dir", dest) == 0
    out = capsys.readouterr().out

    assert "CC-BY-4.0" in out
    assert "https://github.com/reactjs/react.dev/blob/main/LICENSE-DOCS.md" in out
    assert ReactDocs().attribution in out, "attribution must be printed in full"
    assert "Meta Platforms" in out


def test_info_prints_provenance(good_pack, dest, capsys):
    run("pack", "install", good_pack, "--packs-dir", dest)
    capsys.readouterr()

    run("pack", "info", "react", "--packs-dir", dest)
    out = capsys.readouterr().out
    assert COMMIT in out, "the source commit is the provenance"
    assert "https://github.com/reactjs/react.dev" in out


def test_info_on_an_unknown_pack_exits_non_zero(dest, capsys):
    assert run("pack", "info", "nosuch", "--packs-dir", dest) == EXIT_PACK
    assert "no installed pack" in capsys.readouterr().err


# --- remove --------------------------------------------------------------------


def test_remove_deletes_the_pack(good_pack, dest, capsys):
    run("pack", "install", good_pack, "--packs-dir", dest)
    capsys.readouterr()

    assert run("pack", "remove", "react", "--packs-dir", dest) == 0
    assert installed(dest) == []


def test_remove_of_an_absent_pack_exits_non_zero(dest):
    assert run("pack", "remove", "react", "--packs-dir", dest) == EXIT_PACK


def test_remove_rejects_a_traversing_name(dest, capsys):
    assert run("pack", "remove", "../../etc/passwd", "--packs-dir", dest) == EXIT_PACK
    assert "invalid pack name" in capsys.readouterr().err


# --- build ---------------------------------------------------------------------


def test_build_without_a_licence_fails(tmp_path, monkeypatch, capsys):
    """A pack you cannot lawfully share must not be produced."""
    from argus.packs import sources

    unlicensed = dataclasses.replace(ReactDocs(), license="")
    monkeypatch.setitem(sources.SOURCES, "react", lambda: unlicensed)
    monkeypatch.setattr("argus.cli.SOURCES", sources.SOURCES)
    monkeypatch.setattr(build, "embed_module", embed_module)

    out = tmp_path / "react.arguspack"
    code = run("pack", "build", "--source", "react", "--work-dir", FIXTURES / "react",
               "--out", out, "--version", "1.0.0", "--commit", COMMIT)

    assert code == EXIT_PACK
    assert not out.exists(), "an unlicensed pack was written"
    assert "license" in capsys.readouterr().err


def test_build_with_an_unknown_source_fails(tmp_path, capsys):
    code = run("pack", "build", "--source", "cobol", "--work-dir", tmp_path,
               "--out", tmp_path / "x.arguspack", "--version", "1")
    assert code == EXIT_PACK
    err = capsys.readouterr().err
    assert "unknown source" in err
    assert "python" in err and "react" in err, "must list what is available"


def test_build_reports_an_unreachable_embedder_actionably(tmp_path, monkeypatch, capsys):
    def dead(texts, **kwargs):
        raise embed_module.EmbeddingUnavailable("connection refused")

    monkeypatch.setattr("argus.packs.build.embed_module.embed_batch", dead)
    out = tmp_path / "react.arguspack"
    code = run("pack", "build", "--source", "react", "--work-dir", FIXTURES / "react",
               "--out", out, "--version", "1.0.0", "--commit", COMMIT)

    assert code == EXIT_PACK
    err = capsys.readouterr().err
    assert "ollama" in err.lower(), "must say where to look"
    assert not out.exists()


def test_build_produces_an_installable_pack(tmp_path, monkeypatch, capsys):
    monkeypatch.setattr("argus.packs.build.embed_module.embed_batch", fake_embed)
    out = tmp_path / "python.arguspack"

    assert run("pack", "build", "--source", "python", "--work-dir", FIXTURES / "python",
               "--out", out, "--version", "3.13", "--commit", COMMIT) == 0
    assert out.is_file()
    printed = capsys.readouterr().out
    assert "docs" in printed and "chunks" in printed

    dest = tmp_path / "installed"
    assert run("pack", "install", out, "--packs-dir", dest) == 0


# --- where packs live ----------------------------------------------------------


def test_pack_commands_work_without_a_gitlab_config(dest):
    """--packs-dir exists so the public tooling stands alone. Config.load
    demands a GitLab URL and token, which nobody installing a public
    documentation pack should have to supply."""
    assert run("pack", "list", "--packs-dir", dest) == 0


def test_pack_commands_require_being_told_where_packs_live(capsys):
    assert run("pack", "list") == 2
    assert "--packs-dir or --config" in capsys.readouterr().err


def test_packs_dir_can_come_from_a_config_file(good_pack, tmp_path, capsys):
    config = tmp_path / "argus.yml"
    config.write_text(
        "gitlab:\n  url: https://gitlab.invalid\n  token: t\n"
        f"index:\n  data_dir: {tmp_path.as_posix()}\n"
        f"  db_path: {(tmp_path / 'index.db').as_posix()}\n"
        f"packs:\n  dir: {(tmp_path / 'mypacks').as_posix()}\n",
        encoding="utf-8",
    )
    assert run("pack", "install", good_pack, "--config", config) == 0
    assert (tmp_path / "mypacks" / "react.arguspack").is_file()


# --- update --------------------------------------------------------------------
#
# `argus pack update --index-url ...` compares installed packs against a
# published index and installs the newer ones. Every part of it was tested
# separately -- `fetch_index`, `install`, the checksum check -- and the command
# that puts them together was not tested at all, which is the shape that lets a
# release path rot quietly. A pack an operator cannot update is a pack they
# rebuild by hand, and a hand-rebuilt pack is one nobody rebuilds.
#
# The property that matters most here is not "it installs the new one". It is
# that a FAILED update leaves the working pack working: updating is the one
# operation in this file that can destroy something that currently works.


def build_version(out_dir, version: str):
    """A second build of the same source at a different version."""
    return build.build_pack(
        ReactDocs(), work_dir=FIXTURES / "react",
        out_path=out_dir / "react.arguspack",
        version=version, embed_fn=fake_embed, source_commit=COMMIT,
    )


def index_for(monkeypatch, entry):
    """Point `pack update` at an index of our choosing, without a network."""
    monkeypatch.setattr(registry, "fetch_index",
                        lambda url, **kw: [entry] if entry else [])


def entry_for(pack: "Path", version: str, *, sha256: str | None = None):
    return registry.IndexEntry(
        name="react", version=version, url=str(pack),
        sha256=sha256 or hashlib.sha256(pack.read_bytes()).hexdigest(),
        size_bytes=pack.stat().st_size, license="CC-BY-4.0")


def test_update_installs_a_newer_version(tmp_path, good_pack, dest, monkeypatch, capsys):
    assert run("pack", "install", good_pack, "--packs-dir", dest) == 0
    newer = build_version(tmp_path / "v2", "2.0.0")
    index_for(monkeypatch, entry_for(newer, "2.0.0"))

    assert run("pack", "update", "--index-url", "https://example.invalid/i.json",
               "--packs-dir", dest) == 0
    out = capsys.readouterr().out
    assert "1.0.0 -> 2.0.0" in out, out

    listed = [p for p in registry.list_installed(dest) if p.name == "react"]
    assert [p.version for p in listed] == ["2.0.0"]


def test_update_leaves_a_current_pack_byte_identical(dest, good_pack, monkeypatch, capsys):
    """The common case. It must not re-download, and it must not rewrite the
    file -- a no-op update that touches the file would invalidate every open
    handle and every cache keyed on it."""
    assert run("pack", "install", good_pack, "--packs-dir", dest) == 0
    before = (dest / "react.arguspack").read_bytes()
    capsys.readouterr()
    index_for(monkeypatch, entry_for(good_pack, "1.0.0"))

    assert run("pack", "update", "--index-url", "https://example.invalid/i.json",
               "--packs-dir", dest) == 0
    assert "is current" in capsys.readouterr().out
    assert (dest / "react.arguspack").read_bytes() == before


def test_a_checksum_mismatch_keeps_the_working_pack(tmp_path, good_pack, dest,
                                                    monkeypatch, capsys):
    """The one that matters. An update that can destroy a working pack while
    failing is worse than no update at all, and the failure has to be visible:
    exit non-zero, and the old pack still installed and readable."""
    assert run("pack", "install", good_pack, "--packs-dir", dest) == 0
    before = (dest / "react.arguspack").read_bytes()

    newer = build_version(tmp_path / "v2", "2.0.0")
    # An index that promises a digest this file does not have -- a corrupted
    # upload, or an index built against a different artifact.
    index_for(monkeypatch, entry_for(newer, "2.0.0", sha256="ab" * 32))
    capsys.readouterr()

    # It must not report success. `main` converts the raised RegistryError into
    # this code rather than a traceback; either is a failure, but a script
    # checking $? needs the code.
    assert run("pack", "update", "--index-url", "https://example.invalid/i.json",
               "--packs-dir", dest) != 0

    assert (dest / "react.arguspack").read_bytes() == before, \
        "a failed update replaced the working pack"
    listed = [p for p in registry.list_installed(dest) if p.name == "react"]
    assert [p.version for p in listed] == ["1.0.0"]
    assert not [f for f in dest.iterdir() if f.name.startswith(".incoming")], \
        "a failed update left its staging file behind"


def test_a_pack_missing_from_the_index_is_left_alone(good_pack, dest, monkeypatch, capsys):
    """Not every pack comes from the published index -- a locally built one has
    no entry, and removing or failing on it would make the command unusable for
    exactly the operator who built their own."""
    assert run("pack", "install", good_pack, "--packs-dir", dest) == 0
    capsys.readouterr()
    index_for(monkeypatch, None)

    assert run("pack", "update", "--index-url", "https://example.invalid/i.json",
               "--packs-dir", dest) == 0
    assert "not in the index" in capsys.readouterr().out
    assert (dest / "react.arguspack").is_file()


def test_update_with_a_name_that_is_not_installed_fails(dest, monkeypatch):
    """`--name` naming nothing is a typo or a wrong --packs-dir, and reporting
    "0 packs updated" for either hides it."""
    index_for(monkeypatch, None)
    assert run("pack", "update", "nosuchpack",
               "--index-url", "https://example.invalid/i.json",
               "--packs-dir", dest) == EXIT_PACK


# --- index ---------------------------------------------------------------------
#
# The producer for `--index-url`. Without it the update path had no producer at
# all: an operator had to hand-write a JSON file with a MANDATORY checksum and
# an absolute URL, from a format that existed only in `fetch_index`'s parsing
# code. A release step whose first move is hand-writing that is one that gets
# done wrong once and abandoned.


def test_index_publishes_the_fields_update_needs(tmp_path, good_pack, dest, capsys):
    assert run("pack", "install", good_pack, "--packs-dir", dest) == 0
    out = tmp_path / "index.json"
    capsys.readouterr()

    assert run("pack", "index", "--out", out,
               "--base-url", "https://packs.example.org/",
               "--packs-dir", dest) == 0

    import json
    body = json.loads(out.read_text(encoding="utf-8"))
    assert body["schema"] == 1
    entry = body["packs"][0]
    assert entry["name"] == "react"
    assert entry["version"] == "1.0.0"
    assert entry["url"] == "https://packs.example.org/react.arguspack"
    assert entry["sha256"] == hashlib.sha256(good_pack.read_bytes()).hexdigest(), \
        "the published checksum is not the artifact's"
    assert entry["size_bytes"] == good_pack.stat().st_size
    assert entry["license"] == "CC-BY-4.0"


def test_a_trailing_slash_on_the_base_url_does_not_double_up(tmp_path, good_pack,
                                                             dest, capsys):
    """`https://host/packs/` and `https://host/packs` are the same place, and a
    doubled slash is a 404 on some servers and not others -- the kind of thing
    that works on the machine it was written on."""
    assert run("pack", "install", good_pack, "--packs-dir", dest) == 0
    out = tmp_path / "index.json"
    assert run("pack", "index", "--out", out, "--base-url",
               "https://packs.example.org/", "--packs-dir", dest) == 0
    import json
    assert json.loads(out.read_text())["packs"][0]["url"] == \
        "https://packs.example.org/react.arguspack"


def test_the_published_index_round_trips_through_fetch_index(tmp_path, good_pack,
                                                             dest, capsys):
    """The producer and the consumer have to agree. They are separate functions
    reading and writing the same format, which is exactly the pair that drifts
    apart unnoticed."""
    import json
    assert run("pack", "install", good_pack, "--packs-dir", dest) == 0
    out = tmp_path / "index.json"
    assert run("pack", "index", "--out", out, "--base-url",
               "https://packs.example.org", "--packs-dir", dest) == 0

    import httpx
    body = json.loads(out.read_text())
    client = httpx.Client(transport=httpx.MockTransport(
        lambda request: httpx.Response(200, json=body)))
    entries = registry.fetch_index("https://packs.example.org/index.json",
                                   client=client)
    assert [e.name for e in entries] == ["react"]
    assert entries[0].sha256 == hashlib.sha256(good_pack.read_bytes()).hexdigest()


def test_index_skips_a_file_that_is_not_a_pack(tmp_path, good_pack, dest,
                                               capsys):
    """A stray file in the packs directory must not become a published entry.
    An entry pointing at something uninstallable fails on the CONSUMER's
    machine, where the cause is far harder to see than it is here."""
    import json
    assert run("pack", "install", good_pack, "--packs-dir", dest) == 0
    (dest / "junk.arguspack").write_bytes(b"this is not a pack at all")
    out = tmp_path / "index.json"
    capsys.readouterr()

    assert run("pack", "index", "--out", out, "--base-url",
               "https://packs.example.org", "--packs-dir", dest) == 0

    body = json.loads(out.read_text())
    assert [e["name"] for e in body["packs"]] == ["react"]
    assert body["skipped"] and body["skipped"][0]["file"] == "junk.arguspack"
    assert "junk.arguspack" in capsys.readouterr().err


def test_index_filters_by_name(tmp_path, good_pack, dest, capsys):
    """One directory can hold several packs, and a release usually publishes a
    chosen subset rather than whatever happens to be there."""
    import json
    assert run("pack", "install", good_pack, "--packs-dir", dest) == 0
    out = tmp_path / "index.json"
    assert run("pack", "index", "python", "--out", out, "--base-url",
               "https://packs.example.org", "--packs-dir", dest) == 0
    assert json.loads(out.read_text())["packs"] == []
