"""The embedding cache that makes an interrupted build resumable."""
from pathlib import Path

import pytest

from argus.packs import build
from argus.packs.embcache import CACHE_FORMAT, EmbeddingCache, cache_key
from argus.packs.sources import ReactDocs

FIXTURES = Path(__file__).parent / "fixtures"
COMMIT = "0" * 40


def _counting_embed():
    calls = {"texts": 0}

    def embed(texts):
        calls["texts"] += len(texts)
        return [[0.1] * 768 for _ in texts]

    return embed, calls


def test_a_rebuild_pays_for_no_embedding_twice(tmp_path):
    """The whole point. Embedding runs at ~53 chunks/sec on CPU, which puts
    the win32 composite past ten hours, and build_pack deletes its partial
    pack on any failure. The cache is the partial credit that deletion throws
    away."""
    embed, calls = _counting_embed()
    out = tmp_path / "react.arguspack"
    build.build_pack(ReactDocs(), work_dir=FIXTURES / "react", out_path=out,
                     version="1.0.0", embed_fn=embed, source_commit=COMMIT)
    first = calls["texts"]
    assert first > 0, "nothing was embedded -- the test proves nothing"

    out.unlink()
    build.build_pack(ReactDocs(), work_dir=FIXTURES / "react", out_path=out,
                     version="1.0.0", embed_fn=embed, source_commit=COMMIT)
    assert calls["texts"] == first, "the rebuild re-embedded cached chunks"
    assert out.exists()


def test_the_cache_survives_a_failed_build(tmp_path, monkeypatch):
    """A build killed mid-way must keep what it already paid for -- that is
    the difference between resuming in minutes and starting an eleven-hour
    job again."""
    out = tmp_path / "react.arguspack"
    # EMBED_FLUSH is 256 and the fixture is ~18 chunks, so by default the whole
    # build is a single flush -- it would die during the first embed call
    # having paid for nothing, and this test would assert on an empty cache.
    # Shrinking the flush is what creates a "some work already done" state.
    monkeypatch.setattr(build, "EMBED_FLUSH", 4)
    calls = {"n": 0}

    def flaky(texts):
        calls["n"] += 1
        if calls["n"] > 2:
            raise RuntimeError("killed mid-build")
        return [[0.1] * 768 for _ in texts]

    with pytest.raises(RuntimeError):
        build.build_pack(ReactDocs(), work_dir=FIXTURES / "react",
                         out_path=out, version="1.0.0", embed_fn=flaky,
                         source_commit=COMMIT)
    assert not out.exists(), "a partial pack survived"
    # Row count, not file size: an empty schema is also a non-empty file, so
    # "it exists" would pass even if every embedding had been lost with the
    # uncommitted transaction.
    import sqlite3

    cache = tmp_path / ".embcache.db"
    assert cache.exists(), "the cache was discarded with the failed build"
    conn = sqlite3.connect(f"file:{cache}?mode=ro", uri=True)
    try:
        kept = conn.execute("SELECT COUNT(*) FROM embeddings").fetchone()[0]
    finally:
        conn.close()
    assert kept > 0, "the killed build kept no embeddings it had already paid for"


def test_a_different_model_does_not_reuse_the_old_vectors(tmp_path):
    """The same text embedded by another model is a different vector. Without
    the model in the key, switching models would silently reuse the old one's
    output and produce a pack whose vectors contradict its own recorded
    embedding_model."""
    a = cache_key("hello", "nomic-embed-text", 768)
    b = cache_key("hello", "other-model", 768)
    c = cache_key("hello", "nomic-embed-text", 1024)
    assert len({a, b, c}) == 3
    assert a.startswith(f"{CACHE_FORMAT}:")


def test_an_unusable_cache_never_fails_the_build(tmp_path):
    """A cache is an optimisation. If it cannot be opened, the build must
    still run -- just slowly."""
    unusable = tmp_path / "not-a-dir" / "x.db"
    unusable.parent.write_text("i am a file, not a directory", encoding="utf-8")
    cache = EmbeddingCache(unusable)
    assert not cache.enabled
    assert cache.get("k") is None
    cache.put_many([("k", b"1", b"2")])   # must not raise
    cache.close()


def test_use_cache_false_writes_no_sidecar(tmp_path):
    embed, _ = _counting_embed()
    out = tmp_path / "react.arguspack"
    build.build_pack(ReactDocs(), work_dir=FIXTURES / "react", out_path=out,
                     version="1.0.0", embed_fn=embed, source_commit=COMMIT,
                     use_cache=False)
    assert not (tmp_path / ".embcache.db").exists()
