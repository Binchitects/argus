"""Tests for the Ollama embedding client.

No network: every test drives httpx.MockTransport. The failure tests matter
more than the happy path here -- an embedding client that half-succeeds
produces a pack that builds, queries, and is quietly wrong.
"""

from __future__ import annotations

import json
import math

import httpx
import pytest

from argus import embed
from argus.packs import quantize


def _client(handler) -> httpx.Client:
    return httpx.Client(transport=httpx.MockTransport(handler))


def _vec(seed: float, dim: int = embed.EMBED_DIM) -> list[float]:
    """A non-unit vector, so normalization has something to do."""
    return [seed + i * 0.001 for i in range(dim)]


def _ok(vectors: list[list[float]]) -> httpx.Response:
    return httpx.Response(200, json={"embeddings": vectors})


def _norm(vec: list[float]) -> float:
    return math.sqrt(sum(x * x for x in vec))


# --- happy path --------------------------------------------------------------


def test_returns_one_vector_per_input_in_order():
    def handler(request: httpx.Request) -> httpx.Response:
        payload = json.loads(request.content)
        # Echo a distinguishable vector per input so order is checkable.
        return _ok([_vec(float(i + 1)) for i in range(len(payload["input"]))])

    got = embed.embed_batch(["a", "b", "c"], client=_client(handler))
    assert len(got) == 3
    # _vec(1) < _vec(2) < _vec(3) componentwise before normalization; after
    # normalization the ordering of the first component is preserved.
    assert got[0][0] < got[1][0] < got[2][0]


def test_vectors_are_l2_normalised():
    handler = lambda r: _ok([_vec(5.0)])
    [got] = embed.embed_batch(["x"], client=_client(handler))
    assert _norm(got) == pytest.approx(1.0, abs=1e-9)


def test_sends_the_pinned_model_and_the_batch_as_input():
    seen = {}

    def handler(request: httpx.Request) -> httpx.Response:
        seen.update(json.loads(request.content))
        seen["url"] = str(request.url)
        return _ok([_vec(1.0), _vec(2.0)])

    embed.embed_batch(["one", "two"], client=_client(handler))
    assert seen["model"] == embed.EMBED_MODEL
    assert seen["input"] == ["one", "two"]
    assert seen["url"].endswith("/api/embed")


def test_empty_input_returns_empty_without_calling_ollama():
    def handler(request: httpx.Request) -> httpx.Response:
        raise AssertionError("must not issue a request for an empty batch")

    assert embed.embed_batch([], client=_client(handler)) == []


def test_splits_large_input_into_batches_covering_every_text_in_order():
    """A single request with thousands of inputs risks a timeout on a local
    model. Batching must not drop, duplicate, or reorder anything."""
    texts = [f"t{i}" for i in range(embed.BATCH_SIZE * 2 + 5)]
    sent: list[list[str]] = []

    def handler(request: httpx.Request) -> httpx.Response:
        payload = json.loads(request.content)
        sent.append(payload["input"])
        return _ok([_vec(float(i + 1)) for i in range(len(payload["input"]))])

    got = embed.embed_batch(texts, client=_client(handler))

    assert len(sent) == 3, "expected the input to be split across three requests"
    assert [t for batch in sent for t in batch] == texts
    assert len(got) == len(texts)


# --- failure: never return partial results ------------------------------------


def test_http_error_raises_rather_than_returning_partial_results():
    handler = lambda r: httpx.Response(500, text="ollama exploded")
    with pytest.raises(embed.EmbeddingUnavailable, match="500"):
        embed.embed_batch(["a"], client=_client(handler))


def test_connection_error_raises_embedding_unavailable():
    def handler(request: httpx.Request) -> httpx.Response:
        raise httpx.ConnectError("connection refused")

    with pytest.raises(embed.EmbeddingUnavailable, match="connection refused"):
        embed.embed_batch(["a"], client=_client(handler))


def test_malformed_json_raises_embedding_unavailable():
    handler = lambda r: httpx.Response(200, text="<html>not json</html>")
    with pytest.raises(embed.EmbeddingUnavailable, match="JSON"):
        embed.embed_batch(["a"], client=_client(handler))


def test_a_failure_in_a_later_batch_discards_the_earlier_ones():
    """The whole point of raising: a caller that received batch 1 and an
    exception could still write batch 1 into a pack, leaving chunks silently
    unembedded."""
    calls = {"n": 0}

    def handler(request: httpx.Request) -> httpx.Response:
        calls["n"] += 1
        if calls["n"] == 1:
            payload = json.loads(request.content)
            return _ok([_vec(1.0)] * len(payload["input"]))
        return httpx.Response(503, text="model unloaded")

    texts = [f"t{i}" for i in range(embed.BATCH_SIZE + 1)]
    with pytest.raises(embed.EmbeddingUnavailable):
        embed.embed_batch(texts, client=_client(handler))
    assert calls["n"] == 2, "expected the second batch to have been attempted"


# --- failure: silent corruption guards ----------------------------------------


def test_fewer_embeddings_than_inputs_raises_instead_of_misaligning():
    """The dangerous one. Returning 2 vectors for 3 chunks would shift every
    subsequent chunk onto its neighbour's vector -- a pack that builds and
    queries cleanly while returning the wrong documents."""
    handler = lambda r: _ok([_vec(1.0), _vec(2.0)])
    with pytest.raises(embed.EmbeddingUnavailable, match="3"):
        embed.embed_batch(["a", "b", "c"], client=_client(handler))


def test_more_embeddings_than_inputs_also_raises():
    handler = lambda r: _ok([_vec(1.0), _vec(2.0), _vec(3.0)])
    with pytest.raises(embed.EmbeddingUnavailable):
        embed.embed_batch(["a", "b"], client=_client(handler))


def test_wrong_dimension_raises_naming_the_dimension_seen():
    """Pointing the client at a different model is a configuration mistake that
    would otherwise produce a whole pack of unusable vectors."""
    handler = lambda r: _ok([[0.1] * 384])
    with pytest.raises(embed.EmbeddingUnavailable, match="384"):
        embed.embed_batch(["a"], client=_client(handler))


def test_zero_vector_raises_rather_than_dividing_by_zero():
    """A zero vector cannot be normalized, and stored as-is it would match
    everything and nothing. Better to fail the build."""
    handler = lambda r: _ok([[0.0] * embed.EMBED_DIM])
    with pytest.raises(embed.EmbeddingUnavailable, match="zero"):
        embed.embed_batch(["a"], client=_client(handler))


def test_missing_embeddings_key_raises():
    handler = lambda r: httpx.Response(200, json={"error": "model not found"})
    with pytest.raises(embed.EmbeddingUnavailable, match="model not found"):
        embed.embed_batch(["a"], client=_client(handler))


# --- the invariant normalization actually protects ----------------------------


def test_normalisation_does_not_change_what_the_quantisers_produce():
    """Documents why normalization is here, since it is *not* for the reasons
    the pipeline might suggest.

    to_bits reads signs, to_int8 scales per-vector, and rescore computes
    cosine -- all three are invariant under multiplication by a positive
    scalar, so normalizing changes none of their output. Normalization exists
    because a pack is a shared artifact: third-party tooling, or any future
    query path that lets sqlite-vec compute an L2 distance itself, will assume
    unit vectors. nomic-embed-text does not return them normalized.

    If this test ever fails, a quantiser has acquired a scale dependence and
    the stored vectors' magnitude has quietly become load-bearing.
    """
    raw = _vec(3.0)
    scaled = [x * 17.0 for x in raw]

    assert quantize.to_bits(raw) == quantize.to_bits(scaled)
    assert quantize.to_int8(raw) == quantize.to_int8(scaled)

    query = _vec(1.0)
    [(_, a)] = quantize.rescore(query, [(0, quantize.to_int8(raw))])
    [(_, b)] = quantize.rescore(query, [(0, quantize.to_int8(scaled))])
    assert a == pytest.approx(b)
