import sqlite3

import pytest

from argus.packs import format


def test_created_pack_has_every_table(tmp_path):
    conn = format.create_pack(tmp_path / "p.argus-pack")
    names = {r["name"] for r in conn.execute(
        "SELECT name FROM sqlite_master WHERE type IN ('table','view')")}
    assert {"pack_meta", "docs", "chunks", "api_symbols", "docs_fts",
            "vec_bin", "vec_i8"} <= names


def test_opened_pack_rejects_writes(tmp_path):
    p = tmp_path / "p.argus-pack"
    format.create_pack(p).close()
    ro = format.open_pack(p)
    with pytest.raises(sqlite3.OperationalError, match="readonly"):
        ro.execute("INSERT INTO pack_meta (key, value) VALUES ('x','y')")


def test_require_compatible_rejects_a_different_model(tmp_path):
    meta = {"embedding_model": "bge-m3", "embedding_dim": "1024",
            "pack_schema_version": "1"}
    with pytest.raises(format.PackMismatch, match="bge-m3"):
        format.require_compatible(meta, model="nomic-embed-text", dim=768)


def test_require_compatible_rejects_a_dimension_mismatch(tmp_path):
    """Same model name, wrong width -- still unservable, and the message must
    name the dimensions rather than only the model."""
    meta = {"embedding_model": "nomic-embed-text", "embedding_dim": "384",
            "pack_schema_version": "1"}
    with pytest.raises(format.PackMismatch, match="384"):
        format.require_compatible(meta, model="nomic-embed-text", dim=768)


def test_require_compatible_accepts_a_matching_pack():
    format.require_compatible(
        {"embedding_model": "nomic-embed-text", "embedding_dim": "768",
         "pack_schema_version": "1"},
        model="nomic-embed-text", dim=768)


# --- cases the brief's Step 1 tests don't cover ---------------------------


def test_require_compatible_rejects_a_schema_version_mismatch():
    """Same model, same dimension, but the pack was built by an incompatible
    format version -- also unservable, and the message must name the pack's
    actual schema version, not just say "incompatible"."""
    meta = {"embedding_model": "nomic-embed-text", "embedding_dim": "768",
            "pack_schema_version": "2"}
    with pytest.raises(format.PackMismatch, match="2"):
        format.require_compatible(meta, model="nomic-embed-text", dim=768)


def test_write_meta_then_read_meta_round_trips(tmp_path):
    conn = format.create_pack(tmp_path / "p.argus-pack")
    format.write_meta(
        conn,
        name="python",
        embedding_model="nomic-embed-text",
        embedding_dim=768,
        doc_count=42,
    )
    meta = format.read_meta(conn)
    assert meta["name"] == "python"
    assert meta["embedding_model"] == "nomic-embed-text"
    assert meta["embedding_dim"] == "768"
    assert meta["doc_count"] == "42"
    # create_pack itself stamps the format version -- callers don't have to.
    assert meta["pack_schema_version"] == str(format.PACK_SCHEMA_VERSION)


def test_write_meta_overwrites_an_existing_key(tmp_path):
    conn = format.create_pack(tmp_path / "p.argus-pack")
    format.write_meta(conn, doc_count=1)
    format.write_meta(conn, doc_count=2)
    assert format.read_meta(conn)["doc_count"] == "2"


def test_open_pack_can_query_a_populated_vec_bin_table(tmp_path):
    """The extension must load on the read-only, immutable connection that
    open_pack returns -- that connection is how every real query reaches a
    pack, so if vec0 tables aren't queryable through it the format is
    useless regardless of what create_pack can do."""
    p = tmp_path / "p.argus-pack"
    conn = format.create_pack(p)
    conn.execute("INSERT INTO docs (path, content, content_len) VALUES ('a','x',1)")
    conn.execute("INSERT INTO chunks (doc_id, text) VALUES (1, 'x')")
    conn.execute(
        "INSERT INTO vec_bin(chunk_id, embedding) VALUES (?, vec_bit(?))",
        (1, b"\x00" * 96),
    )
    conn.commit()
    conn.close()

    ro = format.open_pack(p)
    rows = ro.execute(
        "SELECT chunk_id, distance FROM vec_bin "
        "WHERE embedding MATCH vec_bit(?) AND k = 5",
        (b"\x00" * 96,),
    ).fetchall()
    assert [dict(r) for r in rows] == [{"chunk_id": 1, "distance": 0.0}]

