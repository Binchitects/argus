"""Ollama embedding client, shared by knowledge packs and the private index.

The model and dimension here are the ones a pack records in its metadata and
that ``packs.format.require_compatible`` refuses to serve a mismatch against.
Changing either constant invalidates every pack already built.

This module's real job is refusing to half-succeed. A pack is built once and
distributed; an embedding client that returns a short or misaligned batch
produces a pack that builds cleanly, queries cleanly, and returns the wrong
documents forever. Every response is therefore checked for count, dimension
and degeneracy before any vector is returned, and a failure anywhere discards
the whole call rather than handing back the batches that did succeed.
"""

from __future__ import annotations

import json
import math
import os

import httpx

EMBED_MODEL = "nomic-embed-text"
EMBED_DIM = 768

# Ollama accepts an arbitrarily long input list, but a local model on CPU can
# take minutes over one, and a timeout mid-way costs the whole request. Batches
# keep each request's worst case bounded.
BATCH_SIZE = 64

DEFAULT_BASE_URL = "http://localhost:11434"

# Generous: first call after a cold start pays for loading the model into
# memory, which on CPU can take tens of seconds before any token is produced.
TIMEOUT = 120.0


class EmbeddingUnavailable(RuntimeError):
    """Embeddings could not be produced, so no embeddings are returned.

    Raised for transport failures, error responses, and responses that arrive
    intact but cannot be trusted -- wrong count, wrong dimension, or a zero
    vector. Callers must treat this as "the pack cannot be built" rather than
    retrying past it, because the alternative is a silently incomplete pack.
    """


def embed_batch(
    texts: list[str], *, client: httpx.Client | None = None,
    base_url: str | None = None,
) -> list[list[float]]:
    """Embed ``texts``, returning one L2-normalized vector per input, in order.

    Normalization is applied even though nothing downstream currently requires
    it: ``quantize.to_bits`` reads signs, ``quantize.to_int8`` scales
    per-vector, and ``quantize.rescore`` computes cosine, all of which are
    invariant under a positive scalar. It is done because a pack is a shared
    artifact -- other tooling, and any future path that lets sqlite-vec compute
    an L2 distance itself, will assume unit vectors, and nomic-embed-text does
    not return them normalized. The invariant is pinned by a test in
    ``tests/test_embed.py``, so a quantiser cannot quietly acquire a scale
    dependence without something failing.
    """
    if not texts:
        return []

    base = (base_url or os.environ.get("ARGUS_OLLAMA_URL")
            or DEFAULT_BASE_URL).rstrip("/")
    owns_client = client is None
    client = client or httpx.Client(timeout=TIMEOUT)

    vectors: list[list[float]] = []
    try:
        for start in range(0, len(texts), BATCH_SIZE):
            batch = texts[start:start + BATCH_SIZE]
            vectors.extend(_embed_one(client, base, batch))
    finally:
        if owns_client:
            client.close()
    return vectors


def _embed_one(
    client: httpx.Client, base: str, batch: list[str]
) -> list[list[float]]:
    try:
        response = client.post(
            f"{base}/api/embed",
            json={"model": EMBED_MODEL, "input": batch},
        )
    except httpx.HTTPError as exc:
        raise EmbeddingUnavailable(
            f"POST {base}/api/embed failed: {exc}"
        ) from exc

    if response.status_code != 200:
        raise EmbeddingUnavailable(
            f"POST /api/embed returned {response.status_code}: "
            f"{response.text[:200]}"
        )

    try:
        body = response.json()
    except json.JSONDecodeError as exc:
        raise EmbeddingUnavailable(
            f"POST /api/embed: failed to decode JSON: {response.text[:200]}"
        ) from exc

    raw = body.get("embeddings")
    if raw is None:
        # Ollama reports a missing or unpulled model as a 200 with an error
        # body, so the message has to carry it or the operator sees nothing
        # actionable.
        raise EmbeddingUnavailable(
            f"POST /api/embed returned no embeddings: {body.get('error', body)}"
        )

    if len(raw) != len(batch):
        # The silent-corruption case: a short batch would shift every later
        # chunk onto its neighbour's vector.
        raise EmbeddingUnavailable(
            f"POST /api/embed: expected {len(batch)} embeddings, got "
            f"{len(raw)} -- refusing to return a misaligned batch"
        )

    return [_normalise(vec, index) for index, vec in enumerate(raw)]


def _normalise(vec: list[float], index: int) -> list[float]:
    if len(vec) != EMBED_DIM:
        raise EmbeddingUnavailable(
            f"embedding {index} has {len(vec)} dimensions, expected "
            f"{EMBED_DIM} -- is {EMBED_MODEL!r} the model actually serving?"
        )

    norm = math.sqrt(sum(x * x for x in vec))
    if norm == 0.0:
        raise EmbeddingUnavailable(
            f"embedding {index} is the zero vector, which has no direction "
            f"and would match everything and nothing"
        )
    return [x / norm for x in vec]
