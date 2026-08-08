"""Build a knowledge pack from a documentation source.

    parse via adapter -> chunk -> embed -> quantize -> write

Two things this module is strict about, both because a pack is an artifact
that gets distributed rather than a cache that gets rebuilt:

**It refuses to build an unlicensed pack.** Emitting something you cannot
lawfully share is a builder failure, not a surprise to discover downstream.
The check runs before anything is written.

**It never leaves a partial pack behind.** The build goes to a temporary file
alongside the destination and is renamed into place only after it completes,
so a failure part-way through leaves no output *and* does not destroy a
previously-good pack at that path. ``format.create_pack`` unlinks its target
on sight, so building straight to the destination would do exactly that.

Fetching is deliberately not part of ``build_pack``. A network side effect
entangled with the deterministic build would make the build untestable without
one; ``fetch_source`` is separate, and the CLI calls it first.
"""

from __future__ import annotations

import subprocess
from pathlib import Path
from typing import Callable, Sequence

import zstandard

from .. import embed as embed_module
from . import format as pack_format
from .chunk import Chunk, chunk_markdown, embed_text, rst_to_atx
from .quantize import to_bits, to_int8
from .sources.base import Doc, Source

BUILDER_VERSION = 1

#: How many chunks to accumulate before calling the embedder. Bounds peak
#: memory on a large source without making a request per chunk.
EMBED_FLUSH = 256

_ZSTD_LEVEL = 10

#: Suffixes stripped when matching a symbol's document to a doc row. The
#: adapters name the *published* document (".html" for Sphinx, extensionless
#: for react.dev) while iter_docs names the *source* file, so neither matches
#: the other without normalising both.
_DOC_SUFFIXES = (".html", ".rst", ".mdx", ".md")

EmbedFn = Callable[[list[str]], list[list[float]]]


class BuildError(Exception):
    """The pack could not be built, and nothing was written."""


def fetch_source(source: Source, dest: Path) -> str:
    """Clone or update ``source`` into ``dest``; return the resolved commit.

    Public documentation repositories need no credentials, so this does not go
    through the GitLab-shaped mirroring in ``argus.mirror`` -- that machinery
    exists for token injection and ACL, neither of which applies here.
    """
    dest = Path(dest)
    if (dest / ".git").is_dir():
        _git(dest, "fetch", "--depth", "1", "origin", source.branch)
        _git(dest, "checkout", "--force", "FETCH_HEAD")
    else:
        dest.parent.mkdir(parents=True, exist_ok=True)
        _git(
            dest.parent, "clone", "--depth", "1",
            "--branch", source.branch, source.repo_url, str(dest),
        )
    return resolve_commit(dest) or ""


def _resolve_source_commit(source: Source, work_dir: Path) -> str | None:
    """The commit(s) this pack is built from.

    A composite source spans several checkouts and is pointed at the parent
    directory holding them, which is not a git repository -- so the ordinary
    single-checkout lookup finds nothing and the build refuses to start. Such
    a source exposes `part_checkouts`, and its provenance is genuinely plural:
    recorded as "ddi=abc1234,samples=def5678" so a reader can verify or
    reproduce either half.

    Any part whose commit cannot be read makes the whole thing None. A
    provenance string that silently omits one of the corpora it shipped is
    worse than admitting the build cannot state where it came from.
    """
    parts = getattr(source, "part_checkouts", None)
    if parts is None:
        return resolve_commit(work_dir)

    recorded = []
    for name, checkout in parts(work_dir):
        commit = resolve_commit(checkout)
        if not commit:
            return None
        recorded.append(f"{name}={commit}")
    return ",".join(recorded) or None


def resolve_commit(work_dir: Path) -> str | None:
    """HEAD of the checkout rooted *at* ``work_dir``, or None.

    The root check is the point. ``git rev-parse HEAD`` searches upwards, so a
    work_dir that merely sits inside some other repository -- a scratch
    directory under a monorepo, say -- yields that repository's HEAD. The pack
    would then record a commit that is real, verifiable, and from entirely the
    wrong project, which is worse than recording none at all.
    """
    work_dir = Path(work_dir)

    def git(*args: str) -> str | None:
        try:
            result = subprocess.run(
                ["git", *args], cwd=work_dir,
                capture_output=True, text=True, timeout=30,
            )
        except (OSError, subprocess.SubprocessError):
            return None
        return result.stdout.strip() if result.returncode == 0 else None

    toplevel = git("rev-parse", "--show-toplevel")
    if toplevel is None:
        return None
    try:
        if Path(toplevel).resolve() != work_dir.resolve():
            return None
    except OSError:
        return None
    return git("rev-parse", "HEAD")


def _git(cwd: Path, *args: str) -> None:
    result = subprocess.run(
        ["git", *args], cwd=cwd, capture_output=True, text=True, timeout=600,
    )
    if result.returncode != 0:
        raise BuildError(f"git {' '.join(args)} failed: {result.stderr.strip()[:400]}")


def build_pack(
    source: Source, *, work_dir: Path, out_path: Path, version: str,
    embed_fn: EmbedFn | None = None, source_commit: str | None = None,
) -> Path:
    """Build a pack for ``source`` from the checkout at ``work_dir``."""
    work_dir = Path(work_dir)
    out_path = Path(out_path)

    _require_licence(source)

    commit = source_commit or _resolve_source_commit(source, work_dir)
    if not commit:
        # A redistributable artifact whose provenance cannot be stated is not
        # one anybody can verify or update.
        raise BuildError(
            f"cannot determine the source commit for {source.name!r}: "
            f"{work_dir} is not a git checkout and no source_commit was given"
        )

    embed_fn = embed_fn or embed_module.embed_batch

    # Same directory as the destination so the final rename is atomic rather
    # than a cross-filesystem copy.
    out_path.parent.mkdir(parents=True, exist_ok=True)
    temp_path = out_path.with_name(out_path.name + ".building")

    try:
        _write_pack(
            source, work_dir, temp_path, embed_fn,
            version=version, commit=commit,
        )
        temp_path.replace(out_path)
    except BaseException:
        # Includes KeyboardInterrupt: a half-written pack left on disk is
        # indistinguishable from a complete one until something queries it.
        temp_path.unlink(missing_ok=True)
        raise

    return out_path


def _require_licence(source: Source) -> None:
    missing = [
        field for field in ("license", "license_url", "attribution")
        if not str(getattr(source, field, "") or "").strip()
    ]
    if missing:
        raise BuildError(
            f"source {source.name!r} records no {', '.join(missing)}; refusing "
            f"to build a pack that cannot lawfully be shared"
        )


def _write_pack(
    source: Source, work_dir: Path, temp_path: Path, embed_fn: EmbedFn,
    *, version: str, commit: str,
) -> None:
    compressor = zstandard.ZstdCompressor(level=_ZSTD_LEVEL)
    conn = pack_format.create_pack(temp_path)
    try:
        doc_ids: dict[str, int] = {}
        pending: list[tuple[int, str]] = []
        chunk_total = 0

        for doc in source.iter_docs(work_dir):
            doc_id = _insert_doc(conn, doc, compressor)
            doc_ids[_doc_key(doc.path)] = doc_id
            for chunk in _chunks_for(doc):
                chunk_id = _insert_chunk(conn, doc_id, chunk, compressor)
                pending.append((chunk_id, embed_text(chunk)))
                chunk_total += 1
                if len(pending) >= EMBED_FLUSH:
                    _flush_embeddings(conn, pending, embed_fn)
                    pending.clear()

        _flush_embeddings(conn, pending, embed_fn)

        symbols, skipped = _insert_symbols(conn, source, work_dir, doc_ids)

        # Written on the same connection, inside the same build, so a pack can
        # never exist with content but no provenance.
        pack_format.write_meta(
            conn,
            source_name=source.name,
            source_repo=source.repo_url,
            source_branch=source.branch,
            source_commit=commit,
            license=source.license,
            license_url=source.license_url,
            attribution=source.attribution,
            embedding_model=embed_module.EMBED_MODEL,
            embedding_dim=embed_module.EMBED_DIM,
            builder_version=BUILDER_VERSION,
            pack_version=version,
            doc_count=len(doc_ids),
            chunk_count=chunk_total,
            symbol_count=symbols,
            unresolved_symbol_count=skipped,
        )
        conn.commit()
    finally:
        conn.close()


def _chunks_for(doc: Doc) -> list[Chunk]:
    body = rst_to_atx(doc.body) if doc.lang == "rst" else doc.body
    return chunk_markdown(body)


def _insert_doc(conn, doc: Doc, compressor) -> int:
    payload = doc.body.encode("utf-8")
    cursor = conn.execute(
        "INSERT INTO docs (path, title, url, lang, content, content_len) "
        "VALUES (?, ?, ?, ?, ?, ?)",
        (doc.path, doc.title, doc.url, doc.lang,
         compressor.compress(payload), len(payload)),
    )
    doc_id = cursor.lastrowid
    # docs_fts is contentless: the terms live in the index and the text is
    # recovered by decompressing docs.content, so the row must be supplied
    # explicitly rather than proxied.
    conn.execute(
        "INSERT INTO docs_fts (rowid, title, body) VALUES (?, ?, ?)",
        (doc_id, doc.title, doc.body),
    )
    return doc_id


def _insert_chunk(conn, doc_id: int, chunk: Chunk, compressor) -> int:
    cursor = conn.execute(
        "INSERT INTO chunks (doc_id, heading_path, anchor, start_line, text) "
        "VALUES (?, ?, ?, ?, ?)",
        (doc_id, chunk.heading_path, chunk.anchor, chunk.start_line,
         compressor.compress(chunk.body.encode("utf-8"))),
    )
    return cursor.lastrowid


def _flush_embeddings(conn, pending: Sequence[tuple[int, str]], embed_fn: EmbedFn) -> None:
    if not pending:
        return
    vectors = embed_fn([text for _, text in pending])
    if len(vectors) != len(pending):
        raise BuildError(
            f"embedder returned {len(vectors)} vectors for {len(pending)} "
            f"chunks -- refusing to store misaligned embeddings"
        )
    for (chunk_id, _), vector in zip(pending, vectors):
        conn.execute(
            "INSERT INTO vec_bin (chunk_id, embedding) VALUES (?, vec_bit(?))",
            (chunk_id, to_bits(vector)),
        )
        conn.execute(
            "INSERT INTO vec_i8 (chunk_id, embedding) VALUES (?, vec_int8(?))",
            (chunk_id, to_int8(vector)),
        )


def _insert_symbols(
    conn, source: Source, work_dir: Path, doc_ids: dict[str, int],
) -> tuple[int, int]:
    written = skipped = 0
    for symbol in source.iter_symbols(work_dir):
        doc_id = doc_ids.get(_doc_key(symbol.doc_path))
        if doc_id is None:
            # The symbol names a page this pack does not contain (an inventory
            # covers a whole site; a subtree may not). Storing it would give a
            # lookup that resolves to nothing -- a confident wrong answer.
            skipped += 1
            continue
        conn.execute(
            "INSERT INTO api_symbols (name, kind, namespace, doc_id, anchor, "
            "signature) VALUES (?, ?, ?, ?, ?, ?)",
            (symbol.name, symbol.kind, symbol.namespace, doc_id,
             symbol.anchor, symbol.signature),
        )
        written += 1
    return written, skipped


def _doc_key(path: str) -> str:
    for suffix in _DOC_SUFFIXES:
        if path.endswith(suffix):
            return path[: -len(suffix)]
    return path
