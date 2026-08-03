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
from argus.packs import build
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
