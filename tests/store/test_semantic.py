"""Tests for the private semantic layer.

No Ollama: ``embed_fn`` is injected. What is exercised is what the module
decides -- which symbols are candidates, what text stands in for them, and
that re-embedding replaces a vector rather than adding a second one.
"""

from __future__ import annotations

import pytest

from argus import semantic
from argus.embed import EMBED_DIM
from argus.store import writes
from argus.store.db import open_db


def fake_embed(texts: list[str]) -> list[list[float]]:
    """Deterministic and text-dependent, so equal inputs embed equally."""
    out = []
    for text in texts:
        vector = [0.0] * EMBED_DIM
        vector[abs(hash(text)) % EMBED_DIM] = 1.0
        out.append(vector)
    return out


@pytest.fixture()
def db(tmp_path):
    conn = open_db(tmp_path / "index.db")
    repo_id = writes.upsert_repo(
        conn, gitlab_id=1, path_with_namespace="grp/one",
        http_url="https://example.invalid/grp/one.git", default_branch="main")
    file_id = writes.upsert_file(
        conn, repo_id=repo_id, path="media/decode/h265.c", lang="c",
        size=1, blob_sha="a1", content="")
    conn.execute(
        "INSERT INTO symbols (repo_id, file_id, name, kind, line, end_line,"
        " signature, scope, is_public) VALUES (?,?,?,?,?,?,?,?,?)",
        (repo_id, file_id, "DecodeFrame", "function", 10, 20,
         "int DecodeFrame(Ctx*, Buf*)", "", 1))
    # A private helper and a signature-less symbol: both must be skipped.
    conn.execute(
        "INSERT INTO symbols (repo_id, file_id, name, kind, line, end_line,"
        " signature, scope, is_public) VALUES (?,?,?,?,?,?,?,?,?)",
        (repo_id, file_id, "init_local", "function", 30, 31,
         "static void init_local(void)", "", 0))
    conn.execute(
        "INSERT INTO symbols (repo_id, file_id, name, kind, line, end_line,"
        " signature, scope, is_public) VALUES (?,?,?,?,?,?,?,?,?)",
        (repo_id, file_id, "NoSig", "function", 40, 41, "", "", 1))
    conn.commit()
    yield conn, repo_id
    conn.close()


class TestEmbedText:
    def test_the_path_contributes_domain_vocabulary(self):
        """A signature says nothing about the domain; the path often does."""
        text = semantic.embed_text_for(
            "Parse", "function", "int Parse(Ctx*, Buf*)", "",
            "media/decode/h265.c")
        assert "media" in text and "decode" in text and "h265" in text

    def test_underscores_are_split_so_they_tokenise(self):
        text = semantic.embed_text_for("X", "function", "void X()", "",
                                       "net/tcp_retry.c")
        assert "tcp retry" in text


class TestBuild:
    def test_only_public_symbols_with_a_signature_are_embedded(self, db):
        """A file-local `init` is noise, and a bare name carries no intent."""
        conn, _ = db
        count = semantic.build_symbol_embeddings(conn, embed_fn=fake_embed)
        assert count == 1
        names = [r[0] for r in conn.execute(
            "SELECT s.name FROM symbol_embeddings e"
            " JOIN symbols s ON s.id = e.symbol_id")]
        assert names == ["DecodeFrame"]

    def test_a_second_run_does_no_work(self, db):
        """Incremental by construction, so an interrupted run resumes."""
        conn, _ = db
        assert semantic.build_symbol_embeddings(conn, embed_fn=fake_embed) == 1
        assert semantic.build_symbol_embeddings(conn, embed_fn=fake_embed) == 0

    def test_re_embedding_replaces_the_vector_rather_than_adding_one(self, db):
        """Two vectors for one symbol would let it occupy two KNN slots."""
        conn, _ = db
        semantic.build_symbol_embeddings(conn, embed_fn=fake_embed)
        conn.execute("DELETE FROM symbol_embeddings")
        conn.commit()
        semantic.build_symbol_embeddings(conn, embed_fn=fake_embed)
        for table in ("vec_symbols_bin", "vec_symbols_i8"):
            rows = conn.execute(f"SELECT count(*) FROM {table}").fetchone()[0]
            assert rows == 1, f"{table} holds {rows} vectors for one symbol"

    def test_an_embedder_returning_the_wrong_count_fails_loudly(self, db):
        """Silently zipping short would pair vectors with the wrong symbols."""
        conn, _ = db
        with pytest.raises(ValueError, match="vectors for"):
            semantic.build_symbol_embeddings(conn, embed_fn=lambda t: [])

    def test_stale_rows_are_reported_not_silently_rebuilt(self, db):
        """Re-embedding a corpus is hours; it should be a decision."""
        conn, _ = db
        semantic.build_symbol_embeddings(conn, embed_fn=fake_embed)
        assert semantic.stale_count(conn) == 0
        conn.execute("UPDATE symbol_embeddings SET model = 'other-model'")
        conn.commit()
        assert semantic.stale_count(conn) == 1


class TestSearch:
    def test_a_query_finds_the_symbol_it_describes(self, db):
        """End to end through the real query path, ACL included."""
        from argus.store import queries

        conn, repo_id = db
        semantic.build_symbol_embeddings(conn, embed_fn=fake_embed)
        target = semantic.embed_text_for(
            "DecodeFrame", "function", "int DecodeFrame(Ctx*, Buf*)", "",
            "media/decode/h265.c")
        [vector] = fake_embed([target])

        hits = queries.semantic_search([repo_id], conn, vector, limit=5)

        assert hits and hits[0]["name"] == "DecodeFrame"
        assert hits[0]["path"] == "media/decode/h265.c"
        assert hits[0]["score"] > 0.9

    def test_an_empty_allowlist_returns_nothing(self, db):
        from argus.store import queries

        conn, _ = db
        semantic.build_symbol_embeddings(conn, embed_fn=fake_embed)
        assert queries.semantic_search([], conn, [0.0] * EMBED_DIM) == []
