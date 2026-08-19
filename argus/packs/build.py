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

import hashlib
import shutil
import sqlite3
import subprocess
import tarfile
import urllib.request
import zipfile
from pathlib import Path
from typing import Callable, Sequence

import zstandard

from .. import embed as embed_module
from . import format as pack_format
from .chunk import Chunk, chunk_markdown, embed_text, rst_to_atx
from .embcache import EmbeddingCache, cache_key
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
    # A source published as a release artifact rather than a repository takes
    # the archive path; there is no clone to make and no commit to resolve.
    if getattr(source, "archive_url", ""):
        return fetch_archive(source, dest)

    # Resolved because the clone below runs with ``cwd=dest.parent``: git
    # would interpret a RELATIVE dest against that cwd and clone into
    # ``dest.parent / dest``. With `--work-dir deploy/work/sources/algorithms`
    # the checkout landed at
    # ``deploy/work/sources/deploy/work/sources/algorithms`` -- a correct
    # clone at a path nothing looks in, so the build then reported the
    # work-dir "is not a git checkout". Absolute paths were never affected,
    # which is why every earlier build was fine.
    dest = Path(dest).resolve()
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


#: Written into an extracted archive so a rebuild can state provenance without
#: downloading again. A git checkout answers that question with `git rev-parse`;
#: an unpacked tarball has nowhere else to keep it.
ARCHIVE_STAMP = ".argus-archive"

#: Read in chunks: a documentation archive can be hundreds of MB, and hashing
#: it by slurping the whole thing into memory would be the largest allocation
#: in the builder.
_HASH_CHUNK = 1 << 20


def _safe_members(names: Sequence[str], kind: str) -> None:
    """Refuse an archive that would write outside its destination.

    Archive formats let a member name any path, including ``../..`` and
    absolute roots, so extracting an untrusted archive can overwrite files
    anywhere the process can write. The packs are built from public
    documentation, which is exactly the kind of input that is easy to
    substitute upstream, so this is checked rather than trusted.
    """
    for name in names:
        posix = name.replace("\\", "/")
        if posix.startswith("/") or posix.startswith("../") or "/../" in posix:
            raise BuildError(
                f"refusing to extract {kind} member {name!r}: escapes the "
                f"destination directory"
            )
        if len(posix) > 1 and posix[1] == ":":
            raise BuildError(
                f"refusing to extract {kind} member {name!r}: absolute path"
            )


def fetch_archive(source: Source, dest: Path) -> str:
    """Download and unpack ``source``'s archive into ``dest``.

    The fetch model for documentation that is published as a release artifact
    rather than a repository. cppreference ships rendered HTML that way -- its
    git repo holds the build tooling, not the pages -- and SQLite has no docs
    repository on GitHub at all.

    Provenance is the archive's sha256 rather than a commit. That is what a
    reader can actually verify: re-download, re-hash, compare. A source may
    also declare ``archive_sha256``, in which case a mismatch fails the build
    rather than silently packaging something else.
    """
    dest = Path(dest).resolve()
    url = getattr(source, "archive_url", "")
    if not url:
        raise BuildError(f"source {source.name!r} declares no archive_url")

    dest.mkdir(parents=True, exist_ok=True)
    archive = dest / f".download{_archive_suffix(url)}"
    try:
        with urllib.request.urlopen(url, timeout=600) as response, \
                open(archive, "wb") as handle:
            declared = response.headers.get("Content-Length")
            digest = hashlib.sha256()
            received = 0
            while True:
                chunk = response.read(_HASH_CHUNK)
                if not chunk:
                    break
                digest.update(chunk)
                received += len(chunk)
                handle.write(chunk)
    except BuildError:
        raise
    except Exception as exc:
        raise BuildError(f"could not download {url}: {exc}") from exc

    # A short read is not an error to urllib: the stream simply ends, and
    # everything downstream then reports the wrong problem -- observed here
    # against an intercepting proxy that truncated an 11.8 MB archive at
    # 720,896 bytes, after which the unpack blamed the archive format. Verify
    # the length the server promised, so a cut transfer fails as a cut
    # transfer.
    if declared and declared.isdigit() and received != int(declared):
        archive.unlink(missing_ok=True)
        raise BuildError(
            f"truncated download of {url}: got {received:,} bytes of "
            f"{int(declared):,}. A proxy or network interruption cut the "
            f"transfer; the archive was not unpacked."
        )

    actual = digest.hexdigest()
    expected = getattr(source, "archive_sha256", "") or ""
    if expected and expected.lower() != actual:
        archive.unlink(missing_ok=True)
        raise BuildError(
            f"archive digest mismatch for {source.name!r}: expected "
            f"{expected.lower()}, got {actual}"
        )

    try:
        if zipfile.is_zipfile(archive):
            with zipfile.ZipFile(archive) as zf:
                _safe_members(zf.namelist(), "zip")
                zf.extractall(dest)
        else:
            with tarfile.open(archive) as tf:
                _safe_members(tf.getnames(), "tar")
                for member in tf.getmembers():
                    # Symlinks can point outside the tree even when the member
                    # name itself is clean, so they are dropped rather than
                    # validated -- no documentation corpus needs them.
                    if member.issym() or member.islnk():
                        continue
                    # filter="data" is a second, independent check: it rejects
                    # traversal, absolute paths and special files inside
                    # tarfile itself. Kept alongside _safe_members rather than
                    # instead of it -- one covers zip, which has no such
                    # filter, and both must agree before anything is written.
                    tf.extract(member, dest, filter="data")
    except BuildError:
        raise
    except Exception as exc:
        raise BuildError(f"could not unpack {url}: {exc}") from exc
    finally:
        archive.unlink(missing_ok=True)

    stamp = f"sha256:{actual}"
    (dest / ARCHIVE_STAMP).write_text(stamp + "\n", encoding="utf-8")
    return stamp


def _archive_suffix(url: str) -> str:
    lowered = url.lower().split("?", 1)[0]
    for suffix in (".tar.gz", ".tgz", ".tar.xz", ".tar.bz2", ".tar", ".zip"):
        if lowered.endswith(suffix):
            return suffix
    return ".bin"


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
    # An unpacked archive is not a checkout, so `git rev-parse` finds nothing
    # and the build would refuse to start. The stamp written at fetch time
    # carries the digest, which lets a rebuild -- the common case, since the
    # embedding cache makes those cheap -- state provenance without
    # re-downloading hundreds of MB.
    stamp = Path(work_dir) / ARCHIVE_STAMP
    if stamp.is_file():
        recorded = stamp.read_text(encoding="utf-8").strip()
        if recorded:
            return recorded

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
    cache_path: Path | str | None = None, use_cache: bool = True,
    incremental: bool = True,
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

    # Beside the pack by default, and shared by every pack built into the
    # same directory: the key is the embed text, so two packs covering the
    # same corpus reuse each other's work.
    #
    # `use_cache=False` is for tests that assert on what a build leaves on
    # disk, or that a failing embedder fails the build -- with a warm cache
    # the embedder is never called, so such a test would pass vacuously.
    if not use_cache:
        cache_path = None
    elif cache_path is None:
        cache_path = out_path.parent / ".embcache.db"

    # Incremental when a usable previous pack is already at the destination.
    # Automatic rather than a flag: for a SOURCE change the two produce the
    # same pack, so there is nothing for an operator to decide.
    # `_existing_shas` returns empty for anything missing, corrupt, or
    # predating content_sha, and each of those falls back to a full build.
    #
    # They do NOT produce the same pack when the ADAPTER changes. content_sha
    # covers the document, so an adapter that derives symbols differently
    # leaves every unchanged document's symbols exactly as they were --
    # `_insert_symbols_for` re-emits only the changed ones. Measured the hard
    # way: a cpp rebuild after teaching the adapter to read page ledes would
    # have kept the old title-echo descriptions for all but the handful of
    # pages upstream had touched, reported a healthy symbol count, and shipped
    # the fix applied to nothing. Delete the destination to force a full build
    # after changing an adapter.
    reusable = bool(_existing_shas(out_path)) if incremental else False

    try:
        with EmbeddingCache(cache_path) as cache:
            if reusable:
                kept, rebuilt, removed = _write_pack_incremental(
                    source, work_dir, temp_path, out_path, embed_fn,
                    version=version, commit=commit, cache=cache,
                )
                print(f"  incremental: {kept} unchanged, {rebuilt} rebuilt, "
                      f"{removed} removed")
            else:
                _write_pack(
                    source, work_dir, temp_path, embed_fn,
                    version=version, commit=commit, cache=cache,
                )
            if cache.enabled and (cache.hits or cache.misses):
                print(f"  embeddings: {cache.hits} reused, "
                      f"{cache.misses} computed")
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
    *, version: str, commit: str, cache: EmbeddingCache | None = None,
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
                    _flush_embeddings(conn, pending, embed_fn, cache)
                    pending.clear()

        _flush_embeddings(conn, pending, embed_fn, cache)

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


def doc_sha(doc: Doc) -> str:
    """Identity of a document for change detection.

    Covers everything a rebuild would regenerate from: the body drives chunks
    and vectors, and title/url/lang land in the row itself. A title fix with
    an unchanged body still has to rewrite the row, so it has to change the
    hash.
    """
    digest = hashlib.sha256()
    for part in (doc.path, doc.title or "", doc.url or "", doc.lang or "",
                 doc.body):
        digest.update(part.encode("utf-8"))
        digest.update(b"\x00")
    return digest.hexdigest()


def _insert_doc(conn, doc: Doc, compressor) -> int:
    payload = doc.body.encode("utf-8")
    cursor = conn.execute(
        "INSERT INTO docs (path, title, url, lang, content, content_len, "
        "content_sha) VALUES (?, ?, ?, ?, ?, ?, ?)",
        (doc.path, doc.title, doc.url, doc.lang,
         compressor.compress(payload), len(payload), doc_sha(doc)),
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


def _flush_embeddings(conn, pending: Sequence[tuple[int, str]],
                      embed_fn: EmbedFn,
                      cache: EmbeddingCache | None = None) -> None:
    if not pending:
        return

    model, dim = embed_module.EMBED_MODEL, embed_module.EMBED_DIM
    # Resolved per chunk, in order, so a partially-cached flush still writes
    # every row in the same sequence it would have without a cache.
    resolved: list[tuple[int, bytes, bytes]] = []
    to_embed: list[tuple[int, str, str]] = []      # (chunk_id, key, text)

    for chunk_id, text in pending:
        key = cache_key(text, model, dim) if cache is not None else ""
        hit = cache.get(key) if cache is not None else None
        if hit is not None:
            resolved.append((chunk_id, hit[0], hit[1]))
        else:
            to_embed.append((chunk_id, key, text))

    if to_embed:
        vectors = embed_fn([text for _, _, text in to_embed])
        if len(vectors) != len(to_embed):
            raise BuildError(
                f"embedder returned {len(vectors)} vectors for {len(to_embed)} "
                f"chunks -- refusing to store misaligned embeddings"
            )
        fresh: list[tuple[str, bytes, bytes]] = []
        for (chunk_id, key, _), vector in zip(to_embed, vectors):
            bits, i8 = to_bits(vector), to_int8(vector)
            resolved.append((chunk_id, bits, i8))
            if cache is not None:
                fresh.append((key, bits, i8))
        if cache is not None:
            # Written before the pack rows and committed immediately: this is
            # the work worth saving, and a build killed a second from now
            # should not have to buy it again.
            cache.put_many(fresh)

    for chunk_id, bits, i8 in resolved:
        conn.execute(
            "INSERT INTO vec_bin (chunk_id, embedding) VALUES (?, vec_bit(?))",
            (chunk_id, bits),
        )
        conn.execute(
            "INSERT INTO vec_i8 (chunk_id, embedding) VALUES (?, vec_int8(?))",
            (chunk_id, i8),
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


def _insert_symbols_for(
    conn, source: Source, work_dir: Path, doc_ids: dict[str, int],
    changed: set[str],
) -> int:
    """Insert symbols belonging only to documents that were rebuilt.

    ``_drop_doc`` already removed the symbols of every document that changed
    or vanished, so the survivors are exactly the ones whose pages are
    untouched. Re-inserting those too would duplicate every symbol in the pack
    on each rebuild -- ``api_symbols`` has no unique constraint to catch it,
    so `docs_lookup` would simply start returning the same page twice, then
    three times.

    Matching is on ``_doc_key`` because ``changed`` holds source paths while a
    symbol names the published document; the two differ by suffix.
    """
    changed_keys = {_doc_key(path) for path in changed}
    written = 0
    for symbol in source.iter_symbols(work_dir):
        key = _doc_key(symbol.doc_path)
        if key not in changed_keys:
            continue
        doc_id = doc_ids.get(key)
        if doc_id is None:
            continue
        conn.execute(
            "INSERT INTO api_symbols (name, kind, namespace, doc_id, anchor, "
            "signature) VALUES (?, ?, ?, ?, ?, ?)",
            (symbol.name, symbol.kind, symbol.namespace, doc_id,
             symbol.anchor, symbol.signature),
        )
        written += 1
    return written


def _doc_key(path: str) -> str:
    for suffix in _DOC_SUFFIXES:
        if path.endswith(suffix):
            return path[: -len(suffix)]
    return path


def _existing_shas(path: Path) -> dict[str, tuple[int, str]]:
    """``{doc path: (doc_id, content_sha)}`` from a pack already on disk.

    Returns empty for anything that is not a usable pack -- absent, corrupt,
    or built before ``content_sha`` existed. Every one of those degrades to a
    full rebuild, which is correct but slow, rather than to a partial pack,
    which would be fast and wrong.
    """
    if not Path(path).is_file():
        return {}
    try:
        conn = sqlite3.connect(f"file:{Path(path).as_posix()}?mode=ro", uri=True)
    except sqlite3.Error:
        return {}
    try:
        columns = {r[1] for r in conn.execute("PRAGMA table_info(docs)")}
        if "content_sha" not in columns:
            return {}
        return {
            row[0]: (row[1], row[2])
            for row in conn.execute(
                "SELECT path, id, content_sha FROM docs WHERE content_sha IS NOT NULL")
        }
    except sqlite3.Error:
        return {}
    finally:
        conn.close()


def _drop_doc(conn, doc_id: int) -> None:
    """Remove a document and everything derived from it.

    Four dependents, and three of them have no foreign key to do it for us:

    - ``chunks`` cascades by hand (the FK is declarative only; SQLite needs
      ``PRAGMA foreign_keys`` and even then would RESTRICT, not cascade).
    - ``vec_bin`` / ``vec_i8`` are vec0 virtual tables keyed by chunk id with
      no relationship to docs at all, so their rows must be deleted before the
      chunk ids they reference are gone and unfindable.
    - ``docs_fts`` is contentless (``content=''``), so a plain DELETE cannot
      remove its terms. FTS5 requires the original column values back, via the
      'delete' command, or the index keeps terms pointing at a rowid that no
      longer exists -- and a later search returns a hit whose document cannot
      be read.
    """
    chunk_ids = [r[0] for r in conn.execute(
        "SELECT id FROM chunks WHERE doc_id = ?", (doc_id,))]
    for table in ("vec_bin", "vec_i8"):
        for chunk_id in chunk_ids:
            conn.execute(f"DELETE FROM {table} WHERE chunk_id = ?", (chunk_id,))
    conn.execute("DELETE FROM chunks WHERE doc_id = ?", (doc_id,))
    conn.execute("DELETE FROM api_symbols WHERE doc_id = ?", (doc_id,))

    row = conn.execute(
        "SELECT title, content FROM docs WHERE id = ?", (doc_id,)).fetchone()
    if row is not None:
        body = zstandard.ZstdDecompressor().decompress(row[1]).decode(
            "utf-8", errors="replace")
        conn.execute(
            "INSERT INTO docs_fts (docs_fts, rowid, title, body) "
            "VALUES ('delete', ?, ?, ?)", (doc_id, row[0], body))
    conn.execute("DELETE FROM docs WHERE id = ?", (doc_id,))


def _write_pack_incremental(
    source: Source, work_dir: Path, temp_path: Path, previous: Path,
    embed_fn: EmbedFn, *, version: str, commit: str,
    cache: EmbeddingCache | None = None,
) -> tuple[int, int, int]:
    """Rebuild ``previous`` in place at ``temp_path``, touching only what moved.

    Returns ``(kept, rebuilt, removed)`` document counts.

    The pack file is COPIED rather than rebuilt row by row. Copying 786 MB is
    seconds of sequential I/O; re-inserting 530,559 chunks is 44 minutes, and
    the copy also keeps every id stable, so nothing has to be remapped and
    unchanged rows are never touched at all.

    Embedding is not what this saves -- the embedding cache already made that
    free. What it saves is everything else done per chunk regardless: parsing,
    chunking, zstd compression, cache lookups, and the FTS and vector inserts.
    """
    shutil.copy2(previous, temp_path)
    known = _existing_shas(temp_path)
    compressor = zstandard.ZstdCompressor(level=_ZSTD_LEVEL)
    conn = pack_format.open_pack_writable(temp_path)
    try:
        seen: set[str] = set()
        changed: set[str] = set()
        doc_ids: dict[str, int] = {}
        pending: list[tuple[int, str]] = []
        kept = rebuilt = 0

        for doc in source.iter_docs(work_dir):
            seen.add(doc.path)
            entry = known.get(doc.path)
            if entry is not None and entry[1] == doc_sha(doc):
                doc_ids[_doc_key(doc.path)] = entry[0]
                kept += 1
                continue

            if entry is not None:
                _drop_doc(conn, entry[0])
            doc_id = _insert_doc(conn, doc, compressor)
            doc_ids[_doc_key(doc.path)] = doc_id
            changed.add(doc.path)
            rebuilt += 1
            for chunk in _chunks_for(doc):
                chunk_id = _insert_chunk(conn, doc_id, chunk, compressor)
                pending.append((chunk_id, embed_text(chunk)))
                if len(pending) >= EMBED_FLUSH:
                    _flush_embeddings(conn, pending, embed_fn, cache)
                    pending.clear()
        _flush_embeddings(conn, pending, embed_fn, cache)

        # Upstream deletions. Without this a pack keeps answering with pages
        # that no longer exist, which is worse than not having them: the
        # answer is confident, cited, and gone from the real documentation.
        removed = 0
        for path, (doc_id, _sha) in known.items():
            if path not in seen:
                _drop_doc(conn, doc_id)
                removed += 1

        # Symbols belong to documents, and _drop_doc already removed those of
        # every document that changed or vanished. Re-inserting only the
        # changed documents' symbols keeps the rest untouched.
        _insert_symbols_for(conn, source, work_dir, doc_ids, changed)

        chunk_total = conn.execute("SELECT count(*) FROM chunks").fetchone()[0]
        symbol_total = conn.execute(
            "SELECT count(*) FROM api_symbols").fetchone()[0]
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
            # Counted from the tables, not tallied while inserting: an
            # incremental run only sees what it rebuilt, so a running total
            # would report the delta and label it the whole pack.
            chunk_count=chunk_total,
            symbol_count=symbol_total,
            unresolved_symbol_count=0,
        )
        conn.commit()
        return kept, rebuilt, removed
    finally:
        conn.close()
