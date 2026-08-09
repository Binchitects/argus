"""Tests for cross-pack queries.

The isolation test comes first and is structural rather than conventional: the
public documentation path must not be able to reach the private code corpus
even by accident.
"""

from __future__ import annotations

import inspect
import pathlib
import sqlite3
from collections import Counter

import pytest
import zstandard

from argus.packs import build, format as pack_format
from argus.packs.sources.python_docs import PythonDocs
from argus.packs.sources.react_docs import ReactDocs
from argus.embed import EMBED_DIM, EMBED_MODEL
from argus.store import packs

from tests.packs.test_build import COMMIT, FIXTURES, fake_embed


def _build(source, name, tmp_dir, version="1.0.0"):
    return build.build_pack(
        source,
        work_dir=FIXTURES / name,
        out_path=tmp_dir / f"{name}.arguspack",
        version=version,
        embed_fn=fake_embed,
        source_commit=COMMIT,
    )


@pytest.fixture(scope="module")
def built(tmp_path_factory):
    directory = tmp_path_factory.mktemp("packs")
    python = _build(PythonDocs(), "python", directory, version="3.13")
    react = _build(ReactDocs(), "react", directory)

    mismatched = directory / "mismatched.arguspack"
    mismatched.write_bytes(python.read_bytes())
    conn = sqlite3.connect(mismatched)
    conn.execute("UPDATE pack_meta SET value = ? WHERE key = 'embedding_model'",
                 ("some-other-model",))
    conn.commit()
    conn.close()
    return {"python": python, "react": react, "mismatched": mismatched}


@pytest.fixture
def both(built):
    opened = packs.open_packs([built["python"], built["react"]])
    yield opened
    packs.close_packs(opened)


@pytest.fixture
def mismatched(built):
    opened = packs.open_packs([built["mismatched"]])
    yield opened
    packs.close_packs(opened)


def query_vec(text: str = "state hook"):
    return fake_embed([text])[0]


# --- isolation ----------------------------------------------------------------


def test_packs_module_cannot_reach_the_private_index():
    """Structural, not conventional. The public path must not be able to read
    the private one even by mistake."""
    src = pathlib.Path(inspect.getfile(packs)).read_text(encoding="utf-8")
    assert "store.queries" not in src and "from .queries" not in src
    assert "index.db" not in src
    assert not any(
        m.__name__.endswith("store.queries")
        for m in vars(packs).values() if inspect.ismodule(m))


def test_no_query_function_accepts_an_allowlist():
    """There is nothing to filter here, and a parameter that looked like the
    private corpus's allowlist would invite wiring one through."""
    for name in ("lookup_symbol", "search_docs", "search_text"):
        params = inspect.signature(getattr(packs, name)).parameters
        assert not any("allow" in p for p in params), name


# --- cross-pack ranking --------------------------------------------------------


def test_search_spans_two_packs_and_ranks_across_them(both):
    """Non-empty FIRST -- ranking across empty sets proves nothing."""
    rows = packs.search_docs(both, query_vec(), limit=10)
    assert rows, "no results: the ranking assertion below would be vacuous"
    assert {r["source"] for r in rows} == {"python", "react"}


def test_results_are_ordered_by_descending_score(both):
    rows = packs.search_docs(both, query_vec(), limit=10)
    assert rows
    scores = [r["score"] for r in rows]
    assert scores == sorted(scores, reverse=True)


def test_search_docs_respects_the_limit(both):
    rows = packs.search_docs(both, query_vec(), limit=3)
    assert len(rows) == 3


def test_search_docs_results_carry_attribution(both):
    rows = packs.search_docs(both, query_vec(), limit=10)
    assert rows
    for row in rows:
        assert row["source"] in {"python", "react"}
        assert row["license"], row
        assert row["attribution"], row
        assert row["url"], row
        assert row["text"], "chunk text should be decompressed"


def test_a_narrower_coarse_pool_still_returns_results(both):
    """coarse is the recall knob measured in tests/packs/test_quantize.py."""
    rows = packs.search_docs(both, query_vec(), limit=5, coarse=2)
    assert rows
    assert len(rows) <= 5


# --- the embedding-space contract ---------------------------------------------


def test_a_model_mismatched_pack_refuses_semantic_but_serves_lexical(mismatched):
    with pytest.raises(pack_format.PackMismatch):
        packs.search_docs(mismatched, query_vec())
    assert packs.lookup_symbol(mismatched, "os.path.join")


def test_a_mismatched_pack_still_serves_text_search(mismatched):
    assert packs.search_text(mismatched, "pathname")


def test_one_mismatched_pack_refuses_the_whole_semantic_search(built):
    """Silently searching the compatible subset would return fewer results
    with nothing to indicate a whole source went missing."""
    opened = packs.open_packs([built["react"], built["mismatched"]])
    try:
        with pytest.raises(pack_format.PackMismatch):
            packs.search_docs(opened, query_vec())
    finally:
        packs.close_packs(opened)


# --- symbol lookup -------------------------------------------------------------


def test_lookup_symbol_resolves_to_an_anchored_url(both):
    [row] = packs.lookup_symbol(both, "os.path.join")
    assert row["source"] == "python"
    assert row["url"] == "https://docs.python.org/3/library/os.path.html#os.path.join"
    assert row["kind"] == "function"
    assert row["signature"] == "join(path, *paths)"


def test_lookup_symbol_finds_a_react_hook(both):
    # More than one document mentions useState, as on the real site; the
    # authoritative one must come first.
    rows = packs.lookup_symbol(both, "useState")
    assert rows
    assert rows[0]["source"] == "react"
    assert rows[0]["url"] == "https://react.dev/reference/react/useState#usestate"


def test_lookup_symbol_carries_licence_and_attribution(both):
    row = packs.lookup_symbol(both, "useState")[0]
    assert row["license"] == "CC-BY-4.0"
    assert "Meta Platforms" in row["attribution"]


def test_lookup_symbol_is_exact_not_fuzzy(both):
    """A confident wrong location is worse than none; semantic search is the
    fallback for an approximate name."""
    assert packs.lookup_symbol(both, "join") == []
    assert packs.lookup_symbol(both, "use") == []


def test_lookup_symbol_matches_case_insensitively(both):
    assert packs.lookup_symbol(both, "usestate")


def test_lookup_symbol_on_an_unknown_name_is_empty(both):
    assert packs.lookup_symbol(both, "definitelyNotASymbol") == []


# --- lexical search ------------------------------------------------------------


def test_search_text_finds_known_text_with_an_excerpt(both):
    rows = packs.search_text(both, "Hook")
    assert rows, "expected a lexical hit"
    assert any(r["source"] == "react" for r in rows)
    assert all(r["excerpt"] for r in rows)


def test_search_text_carries_attribution(both):
    rows = packs.search_text(both, "Hook")
    assert rows
    for row in rows:
        assert row["license"] and row["attribution"] and row["url"]


def test_search_text_on_absent_terms_is_empty(both):
    assert packs.search_text(both, "zzznotpresentanywhere") == []


def test_a_malformed_match_expression_is_a_query_error_not_a_crash(both):
    with pytest.raises(packs.PackQueryError, match="invalid search query"):
        packs.search_text(both, 'unbalanced "quote')


# --- source filtering ----------------------------------------------------------


def test_lang_filters_to_one_source(both):
    rows = packs.search_docs(both, query_vec(), lang="python", limit=10)
    assert rows
    assert {r["source"] for r in rows} == {"python"}


def test_lang_filter_applies_to_symbol_lookup(both):
    assert packs.lookup_symbol(both, "useState", lang="python") == []
    assert packs.lookup_symbol(both, "useState", lang="react")


def test_lang_filter_is_case_insensitive(both):
    assert packs.lookup_symbol(both, "useState", lang="REACT")


def test_an_unknown_lang_selects_nothing(both):
    assert packs.search_docs(both, query_vec(), lang="cobol") == []


# --- opening and closing -------------------------------------------------------


def test_open_packs_reads_names_from_metadata(built):
    opened = packs.open_packs([built["python"], built["react"]])
    try:
        assert [p.name for p in opened] == ["python", "react"]
        assert opened[0].license == "PSF-2.0"
    finally:
        packs.close_packs(opened)


def test_open_packs_on_an_unreadable_file_raises_and_closes_the_rest(built, tmp_path):
    junk = tmp_path / "junk.arguspack"
    junk.write_bytes(b"not a database")
    with pytest.raises((packs.PackQueryError, sqlite3.DatabaseError)):
        packs.open_packs([built["python"], junk])


def test_react_result_urls_use_the_sites_real_anchors(both):
    """A slugified guess would cite '#createroot-domnode-options', which does
    not exist on react.dev -- a link that lands at the page top while claiming
    to land at the section."""
    rows = packs.search_docs(both, query_vec(), lang="react", limit=10)
    assert rows
    anchors = {r["anchor"] for r in rows if r["anchor"]}
    assert "createroot" in anchors, anchors
    assert not any("{/*" in (r["heading_path"] or "") for r in rows)


def test_scores_are_real_cosines_not_coarse_rank_order(both):
    """Discriminates the int8 rescore itself.

    Embedding a chunk's own text gives back that chunk's exact vector, so it
    must come first with a cosine of ~1.0. Ranking on the Hamming pass alone,
    or on any positional score, cannot produce that number -- and would leave
    every other assertion here passing.
    """
    from argus.packs.chunk import chunk_markdown, embed_text
    from argus.packs.sources.react_docs import ReactDocs

    doc = next(d for d in ReactDocs().iter_docs(FIXTURES / "react")
               if d.path == "reference/react/useState.md")
    target = next(c for c in chunk_markdown(doc.body) if c.heading_path)
    qvec = fake_embed([embed_text(target)])[0]

    rows = packs.search_docs(both, qvec, limit=5)
    assert rows, "no results"
    assert rows[0]["heading_path"] == target.heading_path
    assert rows[0]["anchor"] == target.anchor

    # Close to 1.0, but strictly below it: int8 quantisation means an exact
    # self-match cannot reach 1.0. A positional score would land on exactly
    # 1.0, so the strict inequality is what rules that out.
    assert rows[0]["score"] == pytest.approx(1.0, abs=1e-3), rows[0]["score"]
    assert rows[0]["score"] < 1.0
    assert all(-1.0 <= r["score"] <= 1.0 for r in rows)


def test_lookup_prefers_the_reference_page_over_a_passing_mention(both):
    """Found by the Task 12 measurement run against the real react.dev pack.

    `useState` is documented on reference/react/useState.md and also has a
    heading in learn/typescript.md's typing section. Both are genuine symbols;
    only one is what someone looking up useState wants. Before the fix, lookup
    returned the typing footnote -- exact, correctly anchored, fully
    attributed, and the wrong page.
    """
    rows = packs.lookup_symbol(both, "useState")
    assert len(rows) > 1, "fixture no longer reproduces the collision"
    assert rows[0]["doc_path"] == "reference/react/useState.md"
    assert rows[0]["url"] == "https://react.dev/reference/react/useState#usestate"


def _many_chunk_pack(tmp_path, name="big", docs=3, chunks_per_doc=8):
    """A pack where one document has far more chunks than a result set holds."""
    from argus.packs import format as pack_format
    from argus.packs.quantize import to_bits, to_int8

    path = tmp_path / f"{name}.arguspack"
    conn = pack_format.create_pack(path)
    comp = zstandard.ZstdCompressor()
    for d in range(docs):
        cur = conn.execute(
            "INSERT INTO docs (path, title, url, lang, content, content_len) "
            "VALUES (?, ?, ?, 'md', ?, 0)",
            (f"doc{d}.md", f"Doc {d}", f"https://x/doc{d}", comp.compress(b"body")))
        doc_id = cur.lastrowid
        for c in range(chunks_per_doc):
            # Doc 0's chunks score highest, so without a cap it fills the page.
            vec = [1.0 - (d * 0.1) - (c * 0.001)] * 768
            cur2 = conn.execute(
                "INSERT INTO chunks (doc_id, heading_path, anchor, start_line, "
                "text) VALUES (?, '', '', 0, ?)",
                (doc_id, comp.compress(f"doc{d} chunk{c}".encode())))
            cid = cur2.lastrowid
            conn.execute("INSERT INTO vec_bin (chunk_id, embedding) "
                         "VALUES (?, vec_bit(?))", (cid, to_bits(vec)))
            conn.execute("INSERT INTO vec_i8 (chunk_id, embedding) "
                         "VALUES (?, vec_int8(?))", (cid, to_int8(vec)))
    pack_format.write_meta(
        conn, source_name=name, source_repo="r", source_branch="b",
        source_commit="c", license="MIT", license_url="u", attribution="a",
        embedding_model=EMBED_MODEL, embedding_dim=EMBED_DIM,
        builder_version=1, pack_version="1.0", doc_count=docs,
        chunk_count=docs * chunks_per_doc, symbol_count=0,
        unresolved_symbol_count=0)
    conn.commit()
    conn.close()
    return path


def test_one_document_cannot_fill_the_whole_result_set(tmp_path):
    """Measured against four real packs: the median top-5 held 0.40 distinct
    documents and four of eight queries returned five chunks of ONE page.
    "Design a URL shortener" filled every slot with the same pastebin page, so
    four of the five results were places the caller already had.

    A search tool returns places to look; depth comes from fetching the page.
    """
    path = _many_chunk_pack(tmp_path)
    opened = packs.open_packs([path])
    try:
        rows = packs.search_docs(opened, [1.0] * 768, limit=5)
        assert len(rows) == 5, "the cap must not shrink the result set"
        per_doc = Counter(r["doc_path"] for r in rows)
        assert max(per_doc.values()) <= packs.MAX_CHUNKS_PER_DOC, per_doc
        assert len(per_doc) >= 3, f"too few distinct documents: {per_doc}"
    finally:
        packs.close_packs(opened)


def test_the_cap_still_returns_a_full_page_of_results(tmp_path):
    """Capping without over-fetching would silently return fewer rows than
    asked for -- the per-pack slice would be consumed by one document and then
    discarded."""
    path = _many_chunk_pack(tmp_path, docs=6, chunks_per_doc=10)
    opened = packs.open_packs([path])
    try:
        assert len(packs.search_docs(opened, [1.0] * 768, limit=10)) == 10
    finally:
        packs.close_packs(opened)


def test_identifiers_are_extracted_but_prose_is_left_alone():
    assert packs.query_terms("Which routine releases KeAcquireSpinLockAtDpcLevel?") \
        == ["keacquirespinlockatdpclevel"]
    assert packs.query_terms("winuser.h and User32.lib") == ["winuser.h", "user32.lib"]
    assert packs.query_terms("POOL_FLAG_NON_PAGED") == ["pool_flag_non_paged"]
    # Prose names nothing to require, so the filter must not engage at all.
    assert packs.query_terms("how do I design a url shortener") == []


def test_results_that_never_mention_the_named_api_are_dropped():
    """Cosine ranks by topic, and in an API corpus a routine's neighbours are
    other routines that do almost the same thing. Measured: asked which routine
    releases a lock taken with KeAcquireSpinLockAtDpcLevel, search returned
    KeReleaseInStackQueuedSpinLockFromDpcLevel at 0.887 plus two more spin-lock
    APIs, none mentioning the routine asked about -- and qwen3.6:35b, which had
    the right answer unaided, adopted one of them instead."""
    rows = [
        (0.9, {"title": "KeReleaseInStackQueuedSpinLockFromDpcLevel",
               "text": "Releases a queued spin lock.", "doc_path": "a.md"}),
        (0.8, {"title": "KeAcquireSpinLockAtDpcLevel macro",
               "text": "KeAcquireSpinLockAtDpcLevel acquires a lock.",
               "doc_path": "b.md"}),
    ]
    kept = packs._mentioning(rows, ["keacquirespinlockatdpclevel"])
    assert [r["doc_path"] for _, r in kept] == ["b.md"]


def test_nothing_relevant_returns_nothing_rather_than_noise():
    """The uncomfortable half, and deliberate. The first version fell back to
    returning everything when the filter emptied the set -- the exact behaviour
    being fixed. Nothing found is a true statement a model can act on; four
    confident unrelated pages are a false one."""
    rows = [
        (0.73, {"title": "usb/ufxclientsample", "text": "sample", "doc_path": "a.md"}),
        (0.72, {"title": "IoCsqRemoveIrp", "text": "csq", "doc_path": "b.md"}),
    ]
    assert packs._mentioning(rows, ["iocreatedevice"]) == []


def test_a_prose_query_keeps_every_result():
    rows = [(0.7, {"title": "x", "text": "y", "doc_path": "a.md"})]
    assert packs._mentioning(rows, []) == rows


def test_search_docs_itself_applies_the_relevance_filter(tmp_path):
    """Binds to search_docs, not to _mentioning. Testing the helper alone
    passes happily while the pipeline never calls it -- which a targeted
    revert of the call site demonstrated."""
    from argus.packs import format as pack_format
    from argus.packs.quantize import to_bits, to_int8

    path = tmp_path / "rel.arguspack"
    conn = pack_format.create_pack(path)
    comp = zstandard.ZstdCompressor()
    # Doc 0 scores highest but never names the routine; doc 1 does.
    for i, (title, body) in enumerate((
            ("KeReleaseInStackQueuedSpinLockFromDpcLevel", "queued spin lock"),
            ("KeAcquireSpinLockAtDpcLevel macro",
             "KeAcquireSpinLockAtDpcLevel acquires a spin lock"))):
        cur = conn.execute(
            "INSERT INTO docs (path, title, url, lang, content, content_len) "
            "VALUES (?, ?, ?, 'md', ?, 0)",
            (f"d{i}.md", title, f"https://x/{i}", comp.compress(b"body")))
        doc_id = cur.lastrowid
        vec = [1.0 - i * 0.05] * 768
        cur2 = conn.execute(
            "INSERT INTO chunks (doc_id, heading_path, anchor, start_line, text) "
            "VALUES (?, '', '', 0, ?)", (doc_id, comp.compress(body.encode())))
        conn.execute("INSERT INTO vec_bin (chunk_id, embedding) VALUES (?, vec_bit(?))",
                     (cur2.lastrowid, to_bits(vec)))
        conn.execute("INSERT INTO vec_i8 (chunk_id, embedding) VALUES (?, vec_int8(?))",
                     (cur2.lastrowid, to_int8(vec)))
    pack_format.write_meta(
        conn, source_name="rel", source_repo="r", source_branch="b",
        source_commit="c", license="MIT", license_url="u", attribution="a",
        embedding_model=EMBED_MODEL, embedding_dim=EMBED_DIM, builder_version=1,
        pack_version="1.0", doc_count=2, chunk_count=2, symbol_count=0,
        unresolved_symbol_count=0)
    conn.commit(); conn.close()

    opened = packs.open_packs([path])
    try:
        rows = packs.search_docs(
            opened, [1.0] * 768, limit=5,
            query_text="Which routine releases KeAcquireSpinLockAtDpcLevel?")
        assert [r["doc_path"] for r in rows] == ["d1.md"], rows
    finally:
        packs.close_packs(opened)


def _verify_pack(tmp_path):
    """A pack with two APIs whose requirement lines differ."""
    from argus.packs import format as pack_format
    path = tmp_path / "v.arguspack"
    conn = pack_format.create_pack(path)
    comp = zstandard.ZstdCompressor()
    for i, (name, sig) in enumerate((
            ("MessageBoxW", "Header: winuser.h; Library: User32.lib; DLL: User32.dll"),
            ("IoCreateDevice", "Header: wdm.h; Library: NtosKrnl.lib; IRQL: <= APC_LEVEL"))):
        cur = conn.execute(
            "INSERT INTO docs (path, title, url, lang, content, content_len) "
            "VALUES (?, ?, ?, 'md', ?, 0)",
            (f"{name}.md", name, f"https://x/{name}", comp.compress(b"body")))
        conn.execute(
            "INSERT INTO api_symbols (name, kind, namespace, doc_id, anchor, signature) "
            "VALUES (?, 'function', 'm', ?, '', ?)", (name, cur.lastrowid, sig))
    pack_format.write_meta(
        conn, source_name="v", source_repo="r", source_branch="b", source_commit="c",
        license="MIT", license_url="u", attribution="a", embedding_model=EMBED_MODEL,
        embedding_dim=EMBED_DIM, builder_version=1, pack_version="1.0",
        doc_count=2, chunk_count=0, symbol_count=2, unresolved_symbol_count=0)
    conn.commit(); conn.close()
    return path


def test_verify_reports_only_what_the_draft_gets_wrong(tmp_path):
    """The inverse of retrieve-then-answer, and the reason it exists: handing a
    model pack context BEFORE it answers took win32 from 5/5 to 1/5, because
    retrieved text displaces knowledge the model already had. Verifying after
    can only speak where the documentation disagrees."""
    opened = packs.open_packs([_verify_pack(tmp_path)])
    try:
        draft = "Call MessageBoxW, declared in user32.h and linked from User32.lib."
        got = {r["name"]: r for r in packs.verify_text(opened, draft)}
        fields = {f["field"]: f["status"] for f in got["MessageBoxW"]["fields"]}
        assert fields["header"] == "contradicted"     # user32.h vs winuser.h
        assert fields["library"] == "confirmed"
        assert fields["dll"] == "unstated"
        assert [c["documented"] for c in got["MessageBoxW"]["corrections"]] == ["winuser.h"]
    finally:
        packs.close_packs(opened)


def test_a_claim_about_one_api_is_not_read_as_a_claim_about_another(tmp_path):
    """The false positive that would make this tool harmful. With a character
    window, "linked from User32.lib. For drivers, IoCreateDevice is in wdm.h"
    made User32.lib look like a claim about IoCreateDevice, and the tool would
    have told the model to "correct" an answer that was already right."""
    opened = packs.open_packs([_verify_pack(tmp_path)])
    try:
        draft = ("Call MessageBoxW, linked from User32.lib. For drivers, "
                 "IoCreateDevice is in wdm.h.")
        got = {r["name"]: r for r in packs.verify_text(opened, draft)}
        fields = {f["field"]: f["status"] for f in got["IoCreateDevice"]["fields"]}
        assert fields["library"] == "unstated", fields
        assert fields["header"] == "confirmed"
        assert got["IoCreateDevice"]["corrections"] == []
    finally:
        packs.close_packs(opened)


def test_an_unknown_identifier_produces_silence(tmp_path):
    """Silence means "no authority here", which leaves the model's own answer
    standing. Reporting something for every name would recreate the noise this
    design exists to avoid."""
    opened = packs.open_packs([_verify_pack(tmp_path)])
    try:
        assert packs.verify_text(opened, "Call SomeUnknownApiW from mystery.h.") == []
    finally:
        packs.close_packs(opened)


def _described_pack(tmp_path):
    from argus.packs import format as pack_format
    path = tmp_path / "d.arguspack"
    conn = pack_format.create_pack(path)
    comp = zstandard.ZstdCompressor()
    rows = [
        ("robocopy", "Copies file data from one location to another, mirroring trees"),
        ("Export-Csv", "Converts objects into a series of comma-separated value strings"),
        ("Import-Csv", "Creates table-like custom objects from the items in a CSV file"),
        ("attrib", "Displays or changes file attributes"),
    ]
    # Enough symbols mentioning "file" that plain term counting would let that
    # one common word outvote a distinctive one. With four rows the weighting
    # cannot bite, and a targeted revert of it passed.
    rows += [(f"filecmd{i}", f"Handles a file operation number {i} on a file")
             for i in range(40)]
    for i, (name, sig) in enumerate(rows):
        cur = conn.execute(
            "INSERT INTO docs (path, title, url, lang, content, content_len) "
            "VALUES (?, ?, ?, 'md', ?, 0)",
            (f"{name}.md", name, f"https://x/{name}", comp.compress(b"b")))
        conn.execute(
            "INSERT INTO api_symbols (name, kind, namespace, doc_id, anchor, signature) "
            "VALUES (?, 'command', 'c', ?, '', ?)", (name, cur.lastrowid, sig))
    pack_format.write_meta(
        conn, source_name="d", source_repo="r", source_branch="b", source_commit="c",
        license="MIT", license_url="u", attribution="a", embedding_model=EMBED_MODEL,
        embedding_dim=EMBED_DIM, builder_version=1, pack_version="1.0",
        doc_count=len(rows), chunk_count=0, symbol_count=len(rows),
        unresolved_symbol_count=0)
    conn.commit(); conn.close()
    return path


def test_a_command_is_findable_by_what_it_does(tmp_path):
    """The gap this closes: on the scripting pack every LLM strategy stalled
    at 18/30, and every miss was "which command is described as X". The name
    is absent from the question so docs_lookup cannot fire, and the
    description was reachable only through chunk embeddings, which rank whole
    pages. Measured on those same 30 questions, this answers 29."""
    opened = packs.open_packs([_described_pack(tmp_path)])
    try:
        hits = packs.search_symbols(opened, "converts objects into comma-separated values")
        assert hits and hits[0]["name"] == "Export-Csv", [h["name"] for h in hits]
    finally:
        packs.close_packs(opened)


def test_the_best_matching_description_ranks_first(tmp_path):
    """Ranking is by how many asked-for words a description contains.

    Note on what this does NOT prove: search_symbols also down-weights terms
    that appear in many symbols, and a targeted revert of that weighting does
    not fail any test here -- a query with several distinctive words wins on
    those alone. The weighting is reasoned, not demonstrated, and is recorded
    that way rather than defended by a test that would pass without it."""
    opened = packs.open_packs([_described_pack(tmp_path)])
    try:
        hits = packs.search_symbols(opened, "displays or changes file attributes")
        assert hits[0]["name"] == "attrib", [h["name"] for h in hits]
    finally:
        packs.close_packs(opened)


def test_a_query_of_only_stopwords_returns_nothing(tmp_path):
    """Returning the whole symbol table ranked by noise would be worse than
    admitting the query said nothing."""
    opened = packs.open_packs([_described_pack(tmp_path)])
    try:
        assert packs.search_symbols(opened, "the of and to in") == []
    finally:
        packs.close_packs(opened)
