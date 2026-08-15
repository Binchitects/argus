"""Tests for the pack builder.

No network and no Ollama: ``embed_fn`` is injected. The failure tests assert on
the filesystem, not merely that an exception was raised -- the guarantee being
made is that a failed build leaves nothing behind, and "it raised" does not
establish that.
"""

from __future__ import annotations

import dataclasses
import shutil
import sqlite3
import subprocess
from pathlib import Path

import pytest
import zstandard

from argus import embed as embed_module
from argus.packs import build, format as pack_format
from argus.packs.sources.python_docs import PythonDocs
from argus.packs.sources.react_docs import ReactDocs

FIXTURES = Path(__file__).parent / "fixtures"
COMMIT = "0123456789abcdef0123456789abcdef01234567"


def fake_embed(texts: list[str]) -> list[list[float]]:
    """Deterministic stand-in. Stable across runs, unlike hash()."""
    vectors = []
    for text in texts:
        seed = (sum(ord(c) for c in text) % 997) + 1
        vectors.append([((seed * (i + 1)) % 211) / 211 - 0.5 for i in range(embed_module.EMBED_DIM)])
    return vectors


def build_react(tmp_path: Path, **kwargs) -> Path:
    return build.build_pack(
        kwargs.pop("source", ReactDocs()),
        work_dir=FIXTURES / "react",
        out_path=tmp_path / "react.arguspack",
        version="1.0.0",
        embed_fn=fake_embed,
        source_commit=COMMIT,
        **kwargs,
    )


@pytest.fixture
def react_pack(tmp_path: Path) -> Path:
    return build_react(tmp_path)


def rows(pack: Path, sql: str, *params):
    conn = pack_format.open_pack(pack)
    try:
        return conn.execute(sql, params).fetchall()
    finally:
        conn.close()


def scalar(pack: Path, sql: str, *params):
    return rows(pack, sql, *params)[0][0]


# --- a built pack ------------------------------------------------------------


def test_a_built_pack_opens_and_has_content(react_pack):
    assert react_pack.exists()
    assert scalar(react_pack, "SELECT count(*) FROM docs") > 0
    assert scalar(react_pack, "SELECT count(*) FROM chunks") > 0
    assert scalar(react_pack, "SELECT count(*) FROM api_symbols") > 0


def test_pack_meta_carries_provenance_and_licence(react_pack):
    conn = pack_format.open_pack(react_pack)
    try:
        meta = pack_format.read_meta(conn)
    finally:
        conn.close()

    assert meta["source_commit"] == COMMIT
    assert meta["source_name"] == "react"
    assert meta["source_repo"] == "https://github.com/reactjs/react.dev"
    assert meta["license"] == "CC-BY-4.0"
    assert "Meta Platforms" in meta["attribution"]
    assert meta["pack_version"] == "1.0.0"
    assert meta["builder_version"] == str(build.BUILDER_VERSION)


def test_pack_meta_pins_the_embedding_model_and_dimension(react_pack):
    """require_compatible refuses to serve a pack whose vectors came from a
    different model. That check is only as good as what the builder records."""
    conn = pack_format.open_pack(react_pack)
    try:
        meta = pack_format.read_meta(conn)
        pack_format.require_compatible(
            meta, model=embed_module.EMBED_MODEL, dim=embed_module.EMBED_DIM
        )
    finally:
        conn.close()
    assert meta["embedding_model"] == embed_module.EMBED_MODEL
    assert meta["embedding_dim"] == str(embed_module.EMBED_DIM)


def test_counts_in_meta_match_what_is_actually_stored(react_pack):
    conn = pack_format.open_pack(react_pack)
    try:
        meta = pack_format.read_meta(conn)
        actual = {
            "doc_count": conn.execute("SELECT count(*) FROM docs").fetchone()[0],
            "chunk_count": conn.execute("SELECT count(*) FROM chunks").fetchone()[0],
            "symbol_count": conn.execute("SELECT count(*) FROM api_symbols").fetchone()[0],
        }
    finally:
        conn.close()
    for key, value in actual.items():
        assert meta[key] == str(value), key


# --- vectors ------------------------------------------------------------------


def test_every_chunk_has_a_vector_in_both_tables(react_pack):
    chunks = scalar(react_pack, "SELECT count(*) FROM chunks")
    assert chunks > 0
    assert scalar(react_pack, "SELECT count(*) FROM vec_bin") == chunks
    assert scalar(react_pack, "SELECT count(*) FROM vec_i8") == chunks


def test_vectors_are_stored_at_the_declared_widths(react_pack):
    conn = pack_format.open_pack(react_pack)
    try:
        binary = conn.execute("SELECT embedding FROM vec_bin LIMIT 1").fetchone()[0]
        int8 = conn.execute("SELECT embedding FROM vec_i8 LIMIT 1").fetchone()[0]
    finally:
        conn.close()
    assert len(binary) == 96
    assert len(int8) == 768


# --- lexical search -----------------------------------------------------------


def test_docs_fts_matches_known_text(react_pack):
    """Contentless FTS5 only proves it indexed anything when MATCH is used --
    count(*) on a contentless table says nothing about the terms."""
    hits = rows(react_pack, "SELECT rowid FROM docs_fts WHERE docs_fts MATCH ?", "Hook")
    assert hits, "expected a lexical hit for a term that is in the fixture"


def test_docs_fts_does_not_match_absent_text(react_pack):
    hits = rows(
        react_pack, "SELECT rowid FROM docs_fts WHERE docs_fts MATCH ?", "zzznotpresent"
    )
    assert hits == []


def test_doc_content_round_trips_through_zstd(react_pack):
    row = rows(
        react_pack,
        "SELECT content, content_len FROM docs WHERE path = ?",
        "reference/react/useState.md",
    )[0]
    body = zstandard.ZstdDecompressor().decompress(row[0]).decode("utf-8")
    assert len(body.encode("utf-8")) == row[1]
    assert "is a React Hook" in body


# --- symbols across the path mismatch -----------------------------------------


def test_symbols_resolve_to_their_document_despite_differing_extensions(react_pack):
    """The adapters name the published document while iter_docs names the
    source file, so nothing matches without normalising both."""
    row = rows(
        react_pack,
        "SELECT d.path FROM api_symbols s JOIN docs d ON d.id = s.doc_id "
        "WHERE s.name = ?",
        "useState",
    )
    assert row, "useState did not resolve to a document"
    # Several documents mention it, as on the real site; the reference page
    # must be among them with its source-file extension intact.
    assert "reference/react/useState.md" in {r[0] for r in row}


def test_no_symbol_is_stored_without_a_document(react_pack):
    assert scalar(react_pack, "SELECT count(*) FROM api_symbols WHERE doc_id IS NULL") == 0


# --- the Python source, which needs reST heading normalisation ----------------


def test_python_pack_chunks_carry_heading_trails(tmp_path):
    """Without rst_to_atx the Python half of the pack would size-split with no
    heading trail at all -- identical in every metric except the one that
    matters."""
    pack = build.build_pack(
        PythonDocs(),
        work_dir=FIXTURES / "python",
        out_path=tmp_path / "python.arguspack",
        version="3.13",
        embed_fn=fake_embed,
        source_commit=COMMIT,
    )
    trails = [r[0] for r in rows(pack, "SELECT heading_path FROM chunks")]
    assert trails, "no chunks produced"
    assert any(t and "os.path" in t for t in trails), trails


def test_rest_markup_does_not_leak_into_heading_trails_or_anchors(tmp_path):
    """The trail is prepended to the embedded text and slugged into the
    anchor, so ':mod:`json` --- ...' left intact would both pollute what the
    embedder sees and produce 'modjson-----json-encoder-and-decoder'."""
    pack = build.build_pack(
        PythonDocs(),
        work_dir=FIXTURES / "python",
        out_path=tmp_path / "python.arguspack",
        version="3.13",
        embed_fn=fake_embed,
        source_commit=COMMIT,
    )
    found = rows(pack, "SELECT heading_path, anchor FROM chunks")
    assert found, "no chunks produced"
    for trail, anchor in found:
        assert ":mod:" not in (trail or ""), trail
        assert "`" not in (trail or ""), trail
        assert not (anchor or "").startswith("mod"), anchor


def test_python_symbols_resolve_from_html_paths_to_rst_docs(tmp_path):
    pack = build.build_pack(
        PythonDocs(),
        work_dir=FIXTURES / "python",
        out_path=tmp_path / "python.arguspack",
        version="3.13",
        embed_fn=fake_embed,
        source_commit=COMMIT,
    )
    row = rows(
        pack,
        "SELECT d.path, s.anchor FROM api_symbols s JOIN docs d ON d.id = s.doc_id "
        "WHERE s.name = ?",
        "os.path.join",
    )
    assert row, "os.path.join did not resolve to a document"
    assert row[0][0] == "library/os.path.rst"
    assert row[0][1] == "os.path.join"


def test_symbols_for_absent_documents_are_counted_not_stored(tmp_path):
    """objects.inv covers a whole site; a subtree does not. Storing a symbol
    whose page is missing would give a lookup that resolves to nothing."""
    pack = build.build_pack(
        PythonDocs(),
        work_dir=FIXTURES / "python",
        out_path=tmp_path / "python.arguspack",
        version="3.13",
        embed_fn=fake_embed,
        source_commit=COMMIT,
    )
    conn = pack_format.open_pack(pack)
    try:
        meta = pack_format.read_meta(conn)
        names = {r[0] for r in conn.execute("SELECT name FROM api_symbols")}
    finally:
        conn.close()
    assert int(meta["unresolved_symbol_count"]) > 0, "fixture should have unresolved symbols"
    assert "PyList_Append" not in names, "c-api/list.html is not in the fixture tree"


# --- refusals leave nothing behind --------------------------------------------


@pytest.mark.parametrize("field", ["license", "license_url", "attribution"])
def test_missing_licence_metadata_raises_before_anything_is_written(tmp_path, field):
    source = dataclasses.replace(ReactDocs(), **{field: ""})
    out = tmp_path / "react.arguspack"

    with pytest.raises(build.BuildError, match=field):
        build.build_pack(
            source, work_dir=FIXTURES / "react", out_path=out,
            version="1.0.0", embed_fn=fake_embed, source_commit=COMMIT,
        )

    assert not out.exists(), "an unlicensed pack was written to disk"
    assert list(tmp_path.iterdir()) == [], f"left behind: {list(tmp_path.iterdir())}"


def test_unknown_source_commit_raises_before_anything_is_written(tmp_path):
    out = tmp_path / "react.arguspack"
    with pytest.raises(build.BuildError, match="commit"):
        build.build_pack(
            ReactDocs(), work_dir=FIXTURES / "react", out_path=out,
            version="1.0.0", embed_fn=fake_embed,
        )
    assert list(tmp_path.iterdir()) == []


def _init_repo(path: Path) -> str:
    """A throwaway git repo, so the test does not depend on where it runs.

    The earlier version asserted things about this project's own .git, which
    the container image does not copy -- so both tests failed there while
    passing locally.
    """
    path.mkdir(parents=True, exist_ok=True)
    subprocess.run(["git", "init", "-q"], cwd=path, check=True)
    (path / "seed.txt").write_text("seed", encoding="utf-8")
    subprocess.run(["git", "add", "."], cwd=path, check=True)
    subprocess.run(
        ["git", "-c", "user.email=t@example.invalid", "-c", "user.name=t",
         "commit", "-qm", "seed"],
        cwd=path, check=True,
    )
    return subprocess.run(
        ["git", "rev-parse", "HEAD"], cwd=path, capture_output=True, text=True,
    ).stdout.strip()


def test_a_work_dir_merely_inside_a_repo_does_not_borrow_its_commit(tmp_path):
    """git rev-parse searches upwards. Without a root check, a work directory
    that merely sits inside some other checkout records THAT repository's HEAD
    as the pack's provenance -- a commit that is real, verifiable, and from the
    wrong project entirely."""
    repo = tmp_path / "outer"
    _init_repo(repo)
    nested = repo / "docs" / "subtree"
    nested.mkdir(parents=True)

    assert build.resolve_commit(nested) is None


def test_the_repository_root_itself_still_resolves(tmp_path):
    """The check must not reject the case it exists to serve."""
    repo = tmp_path / "root"
    expected = _init_repo(repo)

    resolved = build.resolve_commit(repo)
    assert resolved == expected
    assert len(resolved) == 40


def test_a_failure_mid_build_leaves_no_output_and_no_temp_file(tmp_path):
    def exploding(texts):
        raise RuntimeError("ollama went away")

    out = tmp_path / "react.arguspack"
    with pytest.raises(RuntimeError, match="ollama"):
        build.build_pack(
            ReactDocs(), work_dir=FIXTURES / "react", out_path=out,
            version="1.0.0", embed_fn=exploding, source_commit=COMMIT, use_cache=False,
        )
    assert not out.exists()
    assert list(tmp_path.iterdir()) == [], f"left behind: {list(tmp_path.iterdir())}"


def test_a_failed_rebuild_does_not_destroy_the_existing_pack(tmp_path):
    """create_pack unlinks its target on sight, so a build straight to the
    destination would delete a working pack before discovering it cannot
    replace it."""
    out = build_react(tmp_path)
    before = out.read_bytes()

    def exploding(texts):
        raise RuntimeError("ollama went away")

    # incremental=False on purpose. Rebuilding the same source incrementally
    # keeps every document, so the embedder is never called and the failure
    # this test is about cannot occur -- it would pass without exercising
    # anything. The property still has to hold on the full-build path, which
    # is what create_pack's unlink makes dangerous.
    with pytest.raises(RuntimeError):
        build.build_pack(
            ReactDocs(), work_dir=FIXTURES / "react", out_path=out,
            version="2.0.0", embed_fn=exploding, source_commit=COMMIT,
            use_cache=False, incremental=False,
        )

    assert out.exists(), "the previously-good pack was destroyed"
    assert out.read_bytes() == before


def test_a_failed_incremental_rebuild_does_not_destroy_the_existing_pack(tmp_path):
    """The same guarantee on the incremental path, which reaches it differently.

    A full build writes a fresh temp file; an incremental one COPIES the live
    pack to that temp path first. If the copy were edited in place instead, a
    failure part-way would leave the only good pack half-rewritten -- so this
    pins that the original is still byte-identical after a failure that
    happens mid-rebuild.
    """
    out = build_react(tmp_path)
    before = out.read_bytes()

    calls = {"n": 0}

    def exploding(texts):
        calls["n"] += 1
        raise RuntimeError("ollama went away")

    # A changed document, so the incremental path has real work to do and
    # actually reaches the embedder.
    changed = tmp_path / "react-changed"
    shutil.copytree(FIXTURES / "react", changed)
    target = next(changed.rglob("*.md"), None) or next(changed.rglob("*.mdx"))
    target.write_text(target.read_text(encoding="utf-8") + "\n\nAdded.\n",
                      encoding="utf-8")

    with pytest.raises(RuntimeError):
        build.build_pack(
            ReactDocs(), work_dir=changed, out_path=out,
            version="2.0.0", embed_fn=exploding, source_commit=COMMIT,
            use_cache=False,
        )

    assert calls["n"] > 0, "the incremental path never reached the embedder"
    assert out.exists(), "the previously-good pack was destroyed"
    assert out.read_bytes() == before


def test_a_misaligned_embedder_is_refused(tmp_path):
    """The same misalignment guard as the embed client, at the point where a
    short batch would be written into the pack instead of returned."""
    def short(texts):
        return fake_embed(texts)[:-1]

    out = tmp_path / "react.arguspack"
    with pytest.raises(build.BuildError, match="misaligned"):
        build.build_pack(
            ReactDocs(), work_dir=FIXTURES / "react", out_path=out,
            version="1.0.0", embed_fn=short, source_commit=COMMIT,
        )
    assert not out.exists()


def test_rebuilding_over_a_good_pack_replaces_it(tmp_path):
    out = build_react(tmp_path)
    first = out.read_bytes()
    build.build_pack(
        ReactDocs(), work_dir=FIXTURES / "react", out_path=out,
        version="2.0.0", embed_fn=fake_embed, source_commit=COMMIT,
    )
    assert out.read_bytes() != first
    conn = pack_format.open_pack(out)
    try:
        assert pack_format.read_meta(conn)["pack_version"] == "2.0.0"
    finally:
        conn.close()
    # The embedding cache is a deliberate sidecar and legitimately survives a
    # build; what must not survive is a .building temp file. Filtering it here
    # rather than turning the cache off keeps this test covering the real
    # default path.
    leftover = [p for p in tmp_path.iterdir() if p.name != ".embcache.db"]
    assert leftover == [out], leftover
    assert not list(tmp_path.glob("*.building")), "a temp pack survived"


class _FakeSource:
    """Enough of a Source for fetch_source; no adapter behaviour is exercised."""

    repo_url = "https://example.invalid/repo.git"
    branch = "main"


def test_fetch_source_clones_into_the_given_dir_not_a_nested_copy(
    tmp_path, monkeypatch
):
    """A RELATIVE work-dir must not clone into ``dest.parent / dest``.

    The clone runs with ``cwd=dest.parent``, so git resolves a relative target
    against that cwd. A work-dir of ``sources/algorithms`` therefore produced
    ``sources/sources/algorithms``: a correct checkout at a path the builder
    never looks in, so the build failed with "is not a git checkout" while the
    clone had plainly succeeded. Absolute work-dirs were never affected, which
    is how this survived every real build.
    """
    calls: list[tuple[Path, tuple[str, ...]]] = []
    monkeypatch.setattr(build, "_git", lambda cwd, *a: calls.append((Path(cwd), a)))
    monkeypatch.setattr(build, "resolve_commit", lambda d: "a" * 40)
    monkeypatch.chdir(tmp_path)
    (tmp_path / "sources").mkdir()

    build.fetch_source(_FakeSource(), Path("sources/algorithms"))

    cwd, args = calls[0]
    assert args[0] == "clone"
    # Resolve the target the way git itself would -- against the clone's cwd.
    # An absolute target is unaffected by the join; a relative one doubles.
    landed = (cwd / Path(args[-1])).resolve()
    assert landed == (tmp_path / "sources" / "algorithms").resolve(), (
        f"clone would land at {landed}"
    )


def test_fetch_source_updates_an_existing_checkout_in_place(tmp_path, monkeypatch):
    """An existing checkout is fetched and reset, never re-cloned."""
    calls: list[tuple[Path, tuple[str, ...]]] = []
    monkeypatch.setattr(build, "_git", lambda cwd, *a: calls.append((Path(cwd), a)))
    monkeypatch.setattr(build, "resolve_commit", lambda d: "b" * 40)
    monkeypatch.chdir(tmp_path)
    (tmp_path / "sources" / "algorithms" / ".git").mkdir(parents=True)

    build.fetch_source(_FakeSource(), Path("sources/algorithms"))

    assert [a[0] for _, a in calls] == ["fetch", "checkout"]
    for cwd, _ in calls:
        assert cwd == (tmp_path / "sources" / "algorithms").resolve()
