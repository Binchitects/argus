"""Read-only queries across installed knowledge packs.

This is the public corpus. It has no allowlist parameter because there is
nothing to filter: every pack is world-readable documentation, and the access
control that governs the private code corpus has no meaning here. That absence
is deliberate and is enforced structurally -- a test asserts this module cannot
reach the private side even by accident, so nothing here may import the
private query layer or name its database file.

**Cross-pack ranking is free, and the reason is worth recording.** The
embedding model is pinned globally and every pack records the model it was
built with, so vectors from different packs occupy the same space. Cosine
scores are therefore directly comparable between a Python pack and a React one
without any per-pack normalization, and the merged ranking means something.
``require_compatible`` is what keeps that true: a pack from a different model
is refused rather than silently ranked against vectors it shares no space with.

Every result carries ``source``, ``url``, ``license`` and ``attribution`` so a
model can cite what it used and under whose terms.
"""

from __future__ import annotations

import sqlite3
from dataclasses import dataclass
from pathlib import Path
from typing import Any, Iterable, Sequence

import zstandard

from ..embed import EMBED_DIM, EMBED_MODEL
from ..packs import format as pack_format
from ..packs.quantize import rescore, to_bits

#: Candidates pulled from the binary index per pack before int8 rescoring.
#:
#: Measured, not chosen. On the synthetic benchmark in
#: ``tests/packs/test_quantize.py`` recall@10 against an exact float32 baseline
#: runs 300 -> 0.838, 400 -> 0.882, 600 -> 0.946, 1000 -> 0.970. The plan's
#: provisional 300 lands below the 0.85 the design assumes, and the shortfall
#: is the coarse cut rather than quantization: end-to-end recall sits within
#: 0.002 of the ceiling set by which candidates survive the Hamming pass. So
#: recall is bought with overfetch, and overfetch is cheap -- the 96-byte scan
#: is unchanged and only the count of 768-byte rows read grows, about 460 KB
#: per pack per query at this setting.
#:
#: Provisional pending a measurement on real embeddings over real documents.
DEFAULT_COARSE = 600

_DECOMPRESSOR = zstandard.ZstdDecompressor()


class PackQueryError(Exception):
    """A query could not be run against a pack."""


@dataclass(frozen=True)
class Pack:
    """An opened pack, held across queries.

    Opening a SQLite file and loading the vector extension per query would be
    paid on every request, so a server opens each pack once and keeps it. Packs
    are immutable by construction, so a long-lived read handle is safe.
    """

    name: str
    path: Path
    conn: sqlite3.Connection
    meta: dict[str, str]

    @property
    def license(self) -> str:
        return self.meta.get("license", "")

    @property
    def attribution(self) -> str:
        return self.meta.get("attribution", "")

    @property
    def url_base(self) -> str:
        return self.meta.get("source_repo", "")


def open_packs(paths: Iterable[Path | str]) -> list[Pack]:
    """Open each pack read-only. Raises if one cannot be read."""
    opened: list[Pack] = []
    try:
        for raw in paths:
            path = Path(raw)
            try:
                conn = pack_format.open_pack(path)
            except sqlite3.Error as exc:
                raise PackQueryError(f"cannot open pack {path}: {exc}") from exc
            meta = pack_format.read_meta(conn)
            opened.append(Pack(
                name=meta.get("source_name", path.stem),
                path=path, conn=conn, meta=meta,
            ))
    except BaseException:
        close_packs(opened)
        raise
    return opened


def close_packs(packs: Iterable[Pack]) -> None:
    for pack in packs:
        try:
            pack.conn.close()
        except sqlite3.Error:
            pass


def lookup_symbol(
    packs: Sequence[Pack], name: str, lang: str | None = None, limit: int = 20,
) -> list[dict[str, Any]]:
    """Resolve an API name to its exact documented location.

    Deliberately exact: this is the precise path, and the adapters went to some
    trouble (Sphinx's inventory, react.dev's pinned anchors) to make it so. A
    fuzzy match here would return a confident wrong location, which is worse
    than returning nothing and letting semantic search answer instead.

    Works on packs whose embedding space does not match this instance --
    symbols do not depend on it.
    """
    results: list[dict[str, Any]] = []
    for pack in _selected(packs, lang):
        rows = _query(pack, """
            SELECT s.name, s.kind, s.namespace, s.anchor, s.signature,
                   d.title, d.url, d.path
            FROM api_symbols s JOIN docs d ON d.id = s.doc_id
            WHERE s.name = ? OR lower(s.name) = lower(?)
            ORDER BY (s.name = ?) DESC, s.name
            LIMIT ?
        """, (name, name, name, limit))
        for row in rows:
            results.append(_attributed(pack, {
                "name": row["name"],
                "kind": row["kind"],
                "namespace": row["namespace"],
                "signature": row["signature"],
                "title": row["title"],
                "doc_path": row["path"],
                "anchor": row["anchor"],
                "url": _anchored(row["url"], row["anchor"]),
            }))
    return results[:limit]


def search_text(
    packs: Sequence[Pack], query: str, lang: str | None = None, limit: int = 20,
) -> list[dict[str, Any]]:
    """Lexical search over document text.

    Independent of the embedding space, so it serves packs that semantic
    search refuses. bm25 is comparable across packs because it is computed per
    pack over the same kind of corpus; results are merged on it directly.
    """
    results: list[tuple[float, dict[str, Any]]] = []
    for pack in _selected(packs, lang):
        try:
            rows = pack.conn.execute("""
                SELECT d.id, d.title, d.url, d.path, d.content,
                       bm25(docs_fts) AS rank
                FROM docs_fts JOIN docs d ON d.id = docs_fts.rowid
                WHERE docs_fts MATCH ?
                ORDER BY rank
                LIMIT ?
            """, (query, limit)).fetchall()
        except sqlite3.OperationalError as exc:
            # FTS5 rejects malformed match expressions (an unbalanced quote is
            # enough). Surface it as a query problem rather than a crash.
            raise PackQueryError(
                f"invalid search query {query!r}: {exc}"
            ) from exc

        for row in rows:
            results.append((row["rank"], _attributed(pack, {
                "title": row["title"],
                "doc_path": row["path"],
                "url": row["url"],
                "excerpt": _excerpt(row["content"], query),
                "score": -float(row["rank"]),
            })))

    # bm25 returns a negative score where more negative is better.
    results.sort(key=lambda pair: pair[0])
    return [row for _, row in results[:limit]]


def search_docs(
    packs: Sequence[Pack], query_vec: Sequence[float], lang: str | None = None,
    limit: int = 10, coarse: int = DEFAULT_COARSE,
) -> list[dict[str, Any]]:
    """Semantic search: binary coarse pass per pack, then int8 rescoring.

    Raises ``PackMismatch`` if any selected pack was built with a different
    embedding model or dimension. It refuses rather than skipping, because
    silently searching a subset would return fewer results with no indication
    that a whole source was missing -- and the caller already knows which packs
    are incompatible, since the registry reports it at install time.
    """
    selected = _selected(packs, lang)
    for pack in selected:
        pack_format.require_compatible(pack.meta, model=EMBED_MODEL, dim=EMBED_DIM)

    query_bits = to_bits(query_vec)
    scored: list[tuple[float, dict[str, Any]]] = []

    for pack in selected:
        candidates = [
            row["chunk_id"] for row in _query(pack, """
                SELECT chunk_id FROM vec_bin
                WHERE embedding MATCH vec_bit(?) AND k = ?
            """, (query_bits, coarse))
        ]
        if not candidates:
            continue

        placeholders = ",".join("?" * len(candidates))
        vectors = _query(pack, f"""
            SELECT chunk_id, embedding FROM vec_i8
            WHERE chunk_id IN ({placeholders})
        """, tuple(candidates))

        ranked = rescore(query_vec, [(r["chunk_id"], r["embedding"]) for r in vectors])
        top = ranked[:limit]
        if not top:
            continue

        by_id = dict(top)
        ids = ",".join("?" * len(by_id))
        rows = _query(pack, f"""
            SELECT c.id, c.heading_path, c.anchor, c.start_line, c.text,
                   d.title, d.url, d.path
            FROM chunks c JOIN docs d ON d.id = c.doc_id
            WHERE c.id IN ({ids})
        """, tuple(by_id))

        for row in rows:
            score = by_id[row["id"]]
            scored.append((score, _attributed(pack, {
                "title": row["title"],
                "doc_path": row["path"],
                "heading_path": row["heading_path"],
                "anchor": row["anchor"],
                "start_line": row["start_line"],
                "url": _anchored(row["url"], row["anchor"]),
                "text": _decompress(row["text"]),
                "score": score,
            })))

    # Comparable across packs without normalization: one pinned model, one
    # shared vector space.
    scored.sort(key=lambda pair: -pair[0])
    return [row for _, row in scored[:limit]]


def _selected(packs: Sequence[Pack], lang: str | None) -> list[Pack]:
    """Filter packs by source.

    ``lang`` names the *source* ("python", "react"), not the markup. The
    ``docs.lang`` column holds "rst"/"md", which is what the chunker dispatches
    on and is not what a caller asking for the Python documentation means.
    """
    if not lang:
        return list(packs)
    wanted = lang.strip().lower()
    return [pack for pack in packs if pack.name.lower() == wanted]


def _query(pack: Pack, sql: str, params: tuple) -> list[sqlite3.Row]:
    try:
        return pack.conn.execute(sql, params).fetchall()
    except sqlite3.Error as exc:
        raise PackQueryError(f"query against pack {pack.name!r} failed: {exc}") from exc


def _attributed(pack: Pack, row: dict[str, Any]) -> dict[str, Any]:
    row["source"] = pack.name
    row["license"] = pack.license
    row["attribution"] = pack.attribution
    return row


def _anchored(url: str | None, anchor: str | None) -> str:
    if not url:
        return ""
    return f"{url}#{anchor}" if anchor else url


def _decompress(blob: bytes | None) -> str:
    if not blob:
        return ""
    return _DECOMPRESSOR.decompress(blob).decode("utf-8", errors="replace")


def _excerpt(blob: bytes | None, query: str, width: int = 320) -> str:
    """A window of document text around the first matching term.

    The full-text index is contentless -- it stores terms, not text -- so
    ``snippet()`` has nothing to read and the excerpt is cut from the
    decompressed document instead.
    """
    text = _decompress(blob)
    if not text:
        return ""
    terms = [t for t in query.replace('"', " ").split() if t.isalnum()]
    lowered = text.lower()
    position = -1
    for term in terms:
        position = lowered.find(term.lower())
        if position >= 0:
            break
    if position < 0:
        return text[:width].strip()
    start = max(0, position - width // 3)
    return text[start:start + width].strip()
