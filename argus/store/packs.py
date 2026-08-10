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

import re
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
#: **Now measured on real embeddings over real documents, and the synthetic
#: benchmark was pessimistic.** Against a full cosine scan of the 123,212-chunk
#: `cpp` pack -- the same metric `rescore` uses -- recall@10 is **1.000 at
#: every coarse value from 300 to 4000**. The coarse pass loses nothing here.
#:
#: Random synthetic vectors are far more uniformly distributed than real
#: document embeddings, so the Hamming pass separates real text much more
#: cleanly than the benchmark implied. Raising this buys no recall and costs
#: latency: 140 ms/query at 600 against 500 ms at 4000. It stays at 600, which
#: is now a measured choice rather than a provisional one.
#:
#: Note for anyone tempted to raise it far: sqlite-vec caps k at 4096.
#:
#: The first attempt at this measurement was wrong and reported a flat 0.700 at
#: every setting. It used `vec_i8 MATCH` as the baseline, but that table is
#: declared without a metric so sqlite-vec ranks it by L2 while `rescore` uses
#: cosine -- the two disagreed on 30% of results for reasons that had nothing
#: to do with the coarse cut. A recall curve that does not move with the knob
#: is measuring the wrong thing.
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
    for pack in select_packs(packs, lang):
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

    results.sort(key=lambda row: _authority(row, name))
    if len(results) > 1:
        results.sort(key=lambda row: (_authority(row, name)[:2],
                                      -_namespace_breadth(packs, row)))
    return results[:limit]



#: How many symbols each (pack, namespace) documents. Computed once per pack
#: and cached: it is a single GROUP BY over a table already indexed by name,
#: and lookup_symbol runs on every agent call.
_BREADTH_CACHE: dict[tuple[str, str], int] = {}


def _namespace_breadth(packs: Sequence[Pack], row: dict[str, Any]) -> int:
    """How many symbols the defining header documents, as a proxy for canonical.

    Microsoft documents the same macro under every header that happens to pull
    it in. `RtlZeroMemory` has five pages -- scsi.h, smclib.h, minitape.h,
    ntddstor.h and wdm.h -- all identical, and the ranking before this picked
    whichever had the shortest path, which is arbitrary. On a real minifilter
    it returned `Header: minitape.h` for a call in a file with no tape drive
    anywhere near it: exact, correctly attributed, and useless.

    Breadth is the signal that separates them without a hand-written list of
    "important" headers. wdm.h documents thousands of routines because it is
    the general kernel header; minitape.h documents a handful because it is
    for tape miniports. The header that documents more is the one a reader
    means, and this generalises to corpora nobody here has seen -- a Python
    pack would rank `os` above a niche module for the same reason.
    """
    source = str(row.get("source", ""))
    namespace = str(row.get("namespace", ""))
    if not namespace:
        return 0
    key = (source, namespace)
    if key in _BREADTH_CACHE:
        return _BREADTH_CACHE[key]

    total = 0
    for pack in packs:
        if pack.name != source:
            continue
        try:
            rows = _query(pack, "SELECT COUNT(*) AS n FROM api_symbols "
                                "WHERE namespace = ?", (namespace,))
            total = int(rows[0]["n"]) if rows else 0
        except PackQueryError:
            total = 0
        break
    _BREADTH_CACHE[key] = total
    return total


def _authority(row: dict[str, Any], name: str) -> tuple:
    """Rank the most authoritative document for a name first.

    A name is often mentioned on several pages, and the reference page is the
    one a developer asked for. On the real react.dev pack, `useState` matches
    both `learn/typescript.md` (which has a `useState` heading in its typing
    section) and `reference/react/useState.md`; without this, lookup returned
    the typing footnote -- exact, correctly anchored, fully attributed, and the
    wrong page.

    The signal is source-agnostic: a page named after the symbol is that
    symbol's reference page. Everything else falls back to path depth, so a
    top-level reference beats a deep guide.
    """
    stem = row["doc_path"].rsplit("/", 1)[-1]
    for suffix in (".md", ".mdx", ".rst", ".html"):
        if stem.endswith(suffix):
            stem = stem[: -len(suffix)]
            break
    return (
        row["name"] != name,                    # exact case first
        stem.lower() != name.rsplit(".", 1)[-1].lower(),   # named-after-it first
        row["doc_path"].count("/"),             # then shallower
        len(row["doc_path"]),
    )

def search_text(
    packs: Sequence[Pack], query: str, lang: str | None = None, limit: int = 20,
) -> list[dict[str, Any]]:
    """Lexical search over document text.

    Independent of the embedding space, so it serves packs that semantic
    search refuses. bm25 is comparable across packs because it is computed per
    pack over the same kind of corpus; results are merged on it directly.
    """
    results: list[tuple[float, dict[str, Any]]] = []
    for pack in select_packs(packs, lang):
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


#: Tokens distinctive enough that a relevant chunk should contain one:
#: CamelCase or snake_case identifiers, command flags, dotted header names.
#: Ordinary prose words are excluded -- requiring "which" or "driver" would
#: filter nothing and requiring both would filter everything.
_IDENT_RE = re.compile(
    r"\b(?:[A-Za-z]+_[A-Za-z0-9_]+"          # snake / SCREAMING_CASE
    r"|(?:[A-Z][a-z0-9]+){2,}[A-Za-z0-9]*"    # CamelCase
    r"|[A-Za-z][A-Za-z0-9]*\.(?:h|lib|dll)"   # winuser.h, User32.lib
    r"|/[A-Za-z][A-Za-z0-9:]*)\b"            # /MIR, /R:2
)


def query_terms(text: str) -> list[str]:
    """Identifiers a relevant chunk ought to mention, lowercased."""
    return [t.lower() for t in dict.fromkeys(_IDENT_RE.findall(text or ""))]


def _mentioning(scored: list[tuple[float, dict[str, Any]]],
                terms: list[str]) -> list[tuple[float, dict[str, Any]]]:
    """Keep rows whose text or title contains one of `terms`.

    Returns the input unchanged when the query named no identifiers -- prose
    questions name nothing to require, and filtering them would be guesswork.

    When the query DOES name identifiers and nothing mentions any of them,
    returns empty. That is the uncomfortable half, and it is deliberate: the
    first version fell back to returning everything, which is exactly the
    behaviour being fixed. Asked the maximum IRQL for IoCreateDevice, the
    corpus returned a USB sample header, IoCsqRemoveIrp and KeInitializeSpinLock
    -- nothing about IoCreateDevice at all -- and qwen3.6:35b, which had the
    right answer unaided, adopted one of them instead.

    Nothing found is a true statement and a model can act on it. Four
    confident, high-scoring, unrelated pages are a false one. `docs_lookup`
    still answers named APIs exactly, and it needs no embedding to do it.
    """
    if not terms:
        return scored
    kept = [
        (score, row) for score, row in scored
        if any(term in str(row.get("text", "")).lower()
               or term in str(row.get("title", "")).lower()
               or term in str(row.get("doc_path", "")).lower()
               for term in terms)
    ]
    return kept


#: How many chunks from one document may appear in a single result set.
#: Two, not one: a long page can genuinely answer different halves of a
#: question in different sections. Five -- what the old code allowed, since it
#: only truncated -- is never useful.
MAX_CHUNKS_PER_DOC = 2


def search_docs(
    packs: Sequence[Pack], query_vec: Sequence[float], lang: str | None = None,
    limit: int = 10, coarse: int = DEFAULT_COARSE, query_text: str = "",
) -> list[dict[str, Any]]:
    """Semantic search: binary coarse pass per pack, then int8 rescoring.

    Raises ``PackMismatch`` if any selected pack was built with a different
    embedding model or dimension. It refuses rather than skipping, because
    silently searching a subset would return fewer results with no indication
    that a whole source was missing -- and the caller already knows which packs
    are incompatible, since the registry reports it at install time.
    """
    selected = select_packs(packs, lang)
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
        # Over-fetch per pack. The merge below keeps at most MAX_CHUNKS_PER_DOC
        # rows from any one document, so taking exactly `limit` here would let
        # a single page's chunks consume the budget and then be discarded,
        # returning fewer results than asked for.
        top = ranked[:limit * MAX_CHUNKS_PER_DOC * 3]
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

    # Drop results that never mention the identifier the question is about.
    #
    # Cosine similarity ranks by topic, and in an API corpus every routine's
    # neighbours are other routines that do almost the same thing. Measured
    # against qwen3.6:35b: asked which routine releases a lock taken with
    # KeAcquireSpinLockAtDpcLevel, search returned
    # KeReleaseInStackQueuedSpinLockFromDpcLevel at 0.887, then
    # KeAcquireInStackQueuedSpinLock, then KeAcquireSpinLockForDpc -- four
    # confident, high-scoring hits, none of which mentioned the routine asked
    # about. The model had the right answer unaided and gave it up for one of
    # these. Asked the maximum IRQL for IoCreateDevice it got a sample header
    # and KeInitializeSpinLock, again mentioning IoCreateDevice nowhere.
    #
    # So: when a query names identifiers, a chunk that contains none of them
    # is not a weak answer, it is a different subject, and handing it to a
    # model is worse than handing it nothing. Prose queries name no
    # identifiers and are untouched -- there is nothing to require.
    scored = _mentioning(scored, query_terms(query_text))

    # Cap how much of the answer any single document may occupy.
    #
    # Measured across eight questions against four installed packs: the median
    # top-5 held only 0.40 distinct documents, and **four of the eight returned
    # five chunks of one page**. "Design a URL shortener" filled every slot
    # with the same pastebin page; four of the five results were places the
    # caller already had.
    #
    # This is not a ranking tweak. A search tool's job is to return distinct
    # places to look -- depth comes from fetching the page, which the caller
    # can already do. The cap is above 1 because two passages from a long page
    # genuinely can answer different halves of a question; it is well below 5
    # because nothing needs five.
    #
    # It also relieves near-duplicate crowding across packs: "cache
    # invalidation strategies" put five variants of the same D3D API
    # (the struct, its typedef, its callback) above the System Design Primer,
    # which is the actual answer. Those are separate documents, so the cap does
    # not fix that case on its own -- recorded in docs/pack-measurements.md
    # rather than tuned away.
    per_doc: dict[tuple[str, str], int] = {}
    out: list[dict[str, Any]] = []
    for _, row in scored:
        key = (str(row.get("source", "")), str(row.get("doc_path", "")))
        seen = per_doc.get(key, 0)
        if seen >= MAX_CHUNKS_PER_DOC:
            continue
        per_doc[key] = seen + 1
        out.append(row)
        if len(out) >= limit:
            break
    return out


def get_doc(packs: Sequence[Pack], doc_path: str, source: str | None = None,
            max_chars: int = 60_000) -> dict[str, Any] | None:
    """The full text of one documentation page.

    Search returns chunks, and a chunk is a fragment. Measured against
    qwen3.6:35b: asked which robocopy option mirrors a tree, retrieval
    correctly ranked `cmd/robocopy.md` first -- and handed over its Syntax and
    Examples sections, because a 40 KB reference page is many chunks and the
    per-document cap admits two. `/MIR` lives in the options table, in a chunk
    that never made the cut, so the model answered worse than it had with no
    help at all.

    Identifying the right page and then being unable to read it is the gap
    this closes: `docs_lookup` and `docs_search` both return `doc_path` and
    `source`, which is exactly what this takes. It is the packs' counterpart
    to `get_file` on the private index.

    `source` disambiguates the same path in two packs. Truncation is reported
    rather than silent, because a caller that does not know the tail was cut
    will conclude the document does not mention what it was looking for.
    """
    for pack in packs:
        if source and pack.name.lower() != source.lower():
            continue
        rows = _query(pack, """
            SELECT path, title, url, lang, content, content_len
            FROM docs WHERE path = ?
        """, (doc_path,))
        if not rows:
            continue
        row = rows[0]
        text = _decompress(row["content"])
        truncated = len(text) > max_chars
        return _attributed(pack, {
            "doc_path": row["path"],
            "title": row["title"],
            "url": row["url"],
            "lang": row["lang"],
            "text": text[:max_chars],
            "truncated": truncated,
            "full_length": len(text),
        })
    return None


#: Fields a requirement line states, and the shape each value takes. Used to
#: tell "the draft says a DIFFERENT header" from "the draft does not mention a
#: header at all" -- only the first is a contradiction worth raising.
#: A claim about an API lives in the sentence that names it. A character
#: window is the wrong unit: at 160 characters, "linked from User32.lib. For
#: drivers, IoCreateDevice is in wdm.h" made User32.lib look like a claim about
#: IoCreateDevice, and the tool would have "corrected" a right answer.
_SENTENCES = re.compile(r"(?<=[.!?])\s+|\n+")

_FIELD_SHAPES = {
    "header": re.compile(r"\b[a-z0-9_]+\.h\b", re.I),
    "library": re.compile(r"\b[a-z0-9_]+\.lib\b", re.I),
    "dll": re.compile(r"\b[a-z0-9_]+\.dll\b", re.I),
    "irql": re.compile(r"\b[A-Z]+_LEVEL\b"),
}

#: Identifiers worth checking: CamelCase API names, SCREAMING_CASE constants,
#: Verb-Noun cmdlets, and MSVC diagnostic codes.
_IDENTIFIER_RE = re.compile(
    r"\b(?:[A-Z][a-z0-9]+){2,}[A-Za-z0-9_]*\b"
    r"|\b[A-Z][A-Z0-9]{2,}(?:_[A-Z0-9]+)+\b"
    r"|\b[A-Z][a-z]+-[A-Z][a-z]+\b"
    r"|\bC[0-9]{4,5}\b")


def verify_text(packs: Sequence[Pack], text: str,
                limit: int = 40) -> list[dict[str, Any]]:
    """Check a draft answer against the packs, reporting only what it gets wrong.

    The inverse of retrieve-then-answer, and it exists because that order was
    measured doing harm: handing a model pack context up front took win32 from
    5/5 to 1/5, because a retrieval miss displaces knowledge the model already
    had. Here the model answers first and the packs only speak when they
    contradict it.

    For every identifier the draft mentions that the packs know, each
    documented field is classified:

    * ``contradicted`` -- the draft states a different value of the same shape
      (it says `user32.h`, the documentation says `winuser.h`). This is the
      only class that should change an answer.
    * ``confirmed`` -- the draft already states the documented value.
    * ``unstated`` -- the draft does not mention this field. NOT an error: an
      answer is allowed to be about something else, and flagging every
      unmentioned fact would recreate the noise this design avoids.

    Identifiers the packs do not know are absent from the result entirely.
    Silence means "no authority here", which is the honest thing for a
    reference to say and leaves the model's own answer standing.
    """
    # Resolve first, then scope. Which names the packs actually recognise
    # decides whether the text is about one API or several, and that decision
    # changes how a claim is attributed.
    resolved: list[tuple[str, dict[str, Any]]] = []
    for name in dict.fromkeys(_IDENTIFIER_RE.findall(text)):
        if len(name) < 4:
            continue
        hits = lookup_symbol(packs, name, limit=1)
        if hits:
            resolved.append((name, hits[0]))
        if len(resolved) >= limit:
            break
    known = {name for name, _ in resolved}

    seen: dict[str, dict[str, Any]] = {}
    for name, hit in resolved:
        hits = [hit]
        # Only the text AROUND this identifier counts as a claim about it.
        # Scanning the whole draft made a mention of User32.lib anywhere flag
        # IoCreateDevice's library as contradicted -- a correction the model
        # would have applied, turning a right answer into a wrong one. That is
        # exactly the harm this tool exists to avoid, so the window is narrow.
        # Scope a claim to the sentence naming the API -- unless the text is
        # about exactly one API, in which case all of it is a claim about that
        # one.
        #
        # Both halves are load-bearing and each was found by measurement.
        # Without the sentence rule, "linked from User32.lib. For drivers,
        # IoCreateDevice is in wdm.h" reads User32.lib as a claim about
        # IoCreateDevice and "corrects" an answer that was right. Without the
        # single-API exception, the realistic shape -- the name in the question,
        # the claim in the answer, two different sentences -- matches nothing:
        # measured, docs_verify fired on 0 of 120 drafts and verify-after
        # scored identically to no packs at all.
        near = (text if len(known) == 1
                else " ".join(s for s in _SENTENCES.split(text) if name in s))

        documented = {k: v.strip() for k, v in
                      (part.split(":", 1) for part in
                       str(hits[0].get("signature", "")).split(";")
                       if ":" in part)}
        fields = []
        for field, shape in _FIELD_SHAPES.items():
            value = next((v for k, v in documented.items()
                          if k.strip().lower() == field), "")
            if not value:
                continue
            stated = {m.group(0).lower() for m in shape.finditer(near)}
            wanted = {m.group(0).lower() for m in shape.finditer(value)}
            if not wanted:
                continue
            if wanted & stated:
                status = "confirmed"
            elif stated:
                status = "contradicted"
            else:
                status = "unstated"
            fields.append({"field": field, "documented": value,
                           "status": status})
        if fields:
            seen[name] = {
                "name": hits[0].get("name", name),
                "source": hits[0].get("source"),
                "url": hits[0].get("url"),
                "doc_path": hits[0].get("doc_path"),
                "fields": fields,
                "corrections": [f for f in fields
                                if f["status"] == "contradicted"],
            }
    return list(seen.values())


#: Words too common to discriminate between thousands of command descriptions.
_STOPWORDS = frozenset("""
a an the and or of to in on for from with by is are be as at it its this that
which what when where how do does use used using return returns value values
one all any if then than into out about over can may must should will would
command cmdlet function specifies specified given only also such more most
""".split())


def query_terms_for_symbols(query: str) -> list[str]:
    """Significant words from a natural-language description."""
    words = [w.strip(".,;:!?()[]{}\"'`").lower() for w in query.split()]
    return [w for w in words if len(w) > 2 and w not in _STOPWORDS]


def search_symbols(packs: Sequence[Pack], query: str, lang: str | None = None,
                   limit: int = 10) -> list[dict[str, Any]]:
    """Find an API by what it DOES, when its name is not known.

    The gap this closes, measured: on the scripting pack the best any strategy
    reached was 18/30, and every miss had the same shape -- "which command is
    described as X". The name is absent from the question, so `docs_lookup`
    can never fire, and the description was reachable only through chunk
    embeddings, which rank whole pages rather than commands.

    Each adapter already stores a one-line description in
    `api_symbols.signature`. Nothing searched it. This does.

    Scoring is term overlap, not bm25: a description is one short line, so
    term frequency carries no signal and document length is constant. What
    matters is how many of the asked-for words appear, and matching a rarer
    word is worth more -- so a term found in few symbols scores above one
    found in thousands.

    A scan rather than an index, deliberately. Packs are immutable, adding an
    FTS5 table would mean rebuilding every one, and the whole symbol table is
    9,302 rows for scripting and 87,297 for win32 -- measured at 7 ms for a
    full LIKE pass. An index would buy milliseconds and cost five hours of
    embedding.
    """
    terms = query_terms_for_symbols(query)
    if not terms:
        return []

    scored: list[tuple[float, dict[str, Any]]] = []
    for pack in select_packs(packs, lang):
        where = " OR ".join("lower(s.signature) LIKE ?" for _ in terms)
        rows = _query(pack, f"""
            SELECT s.name, s.kind, s.namespace, s.anchor, s.signature,
                   d.title, d.url, d.path
            FROM api_symbols s JOIN docs d ON d.id = s.doc_id
            WHERE s.signature != '' AND ({where})
        """, tuple(f"%{t}%" for t in terms))
        if not rows:
            continue

        # How many symbols in THIS pack mention each term, so a word that
        # appears everywhere cannot outweigh a distinctive one.
        frequency = {t: 0 for t in terms}
        for row in rows:
            low = str(row["signature"]).lower()
            for t in terms:
                if t in low:
                    frequency[t] += 1

        for row in rows:
            low = str(row["signature"]).lower()
            score = 0.0
            for t in terms:
                if t in low:
                    score += 1.0 / (1.0 + frequency[t] / 50.0)
            if not score:
                continue
            scored.append((score, _attributed(pack, {
                "name": row["name"],
                "kind": row["kind"],
                "namespace": row["namespace"],
                "signature": row["signature"],
                "title": row["title"],
                "doc_path": row["path"],
                "anchor": row["anchor"],
                "url": _anchored(row["url"], row["anchor"]),
                "score": round(score, 4),
            })))

    scored.sort(key=lambda pair: -pair[0])
    return [row for _, row in scored[:limit]]


#: Identifiers worth looking up in a source file: CamelCase API names with a
#: subsystem prefix, and PowerShell-style Verb-Noun cmdlets. Deliberately not
#: every capitalised word -- a struct field or a local named `Status` is noise.
_SOURCE_API_RE = re.compile(
    r"\b(?:[A-Z][a-z0-9]+){2,}[A-Za-z0-9_]*\b"
    r"|\b[A-Z][a-z]+-[A-Z][a-z]+\b"
)

#: Called constantly, documented nowhere useful, and they crowd out the APIs a
#: reviewer actually needs the contract for.
_CONTRACT_NOISE = frozenset({
    "ContainingRecord", "InitializeListHead", "InsertHeadList", "RemoveEntryList",
    "RemoveHeadList", "IsListEmpty", "InsertTailList", "ListEntry",
    "InterlockedIncrement", "InterlockedDecrement", "InterlockedExchange",
})


def api_contracts(packs: Sequence[Pack], source: str,
                  limit: int = 40) -> list[dict[str, Any]]:
    """Every documented API named in a source file, with its real contract.

    The measured problem this solves. Asked to review a real minifilter, the
    model produced seven findings, all resting on one remembered claim --
    that ExAllocateFromLookasideListEx requires PASSIVE_LEVEL. It is
    documented `<= DISPATCH_LEVEL`. Seven bug reports from one unchecked fact.

    `docs_verify` catches exactly that claim, and would have caught all seven.
    It was never called: the model had no reason to doubt itself, which is the
    one thing a confident model never spontaneously does. A tool that must be
    invoked *because you suspect an error* is invoked least when the error is
    most confident.

    So this inverts it. One call, before reviewing, returns the header,
    library, DLL and IRQL of every API the file mentions -- facts rather than
    a judgement, obtained without the model having to suspect anything. It is
    the reviewer's own first step, which is reading the contracts of what the
    code calls.

    Ordered by how often each name appears, so a file's central APIs lead
    rather than whatever sorts first.
    """
    counts: dict[str, int] = {}
    for name in _SOURCE_API_RE.findall(source or ""):
        if len(name) < 6 or name in _CONTRACT_NOISE:
            continue
        counts[name] = counts.get(name, 0) + 1

    out: list[dict[str, Any]] = []
    for name in sorted(counts, key=lambda n: (-counts[n], n)):
        hits = lookup_symbol(packs, name, limit=1)
        if not hits:
            continue
        row = dict(hits[0])
        row["mentions"] = counts[name]
        out.append(row)
        if len(out) >= limit:
            break
    return out


def select_packs(packs: Sequence[Pack], lang: str | None) -> list[Pack]:
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
