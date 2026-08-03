"""Tests for binary and int8 quantization.

The recall test is the point of this module. The pack format stores 96 bytes
per chunk instead of 3072 precisely because binary-coarse plus int8-rescore is
claimed to retain most of float32's recall. That claim is measured here, not
assumed.
"""

from __future__ import annotations

import math
import random

import pytest

from argus.packs import quantize

DIM = 768


def _unit(vec: list[float]) -> list[float]:
    norm = math.sqrt(sum(x * x for x in vec))
    return [x / norm for x in vec]


def _cos(a: list[float], b: list[float]) -> float:
    return sum(x * y for x, y in zip(a, b))  # both unit-normalised


def _topk_float(query: list[float], corpus: list[list[float]], k: int) -> list[tuple[int, float]]:
    """float32 ground truth: exact cosine over the whole corpus."""
    scored = [(i, _cos(query, c)) for i, c in enumerate(corpus)]
    scored.sort(key=lambda pair: -pair[1])
    return scored[:k]


def _topk_hamming(query: list[float], corpus_bits: list[bytes], k: int) -> list[int]:
    """The coarse pass: rank by Hamming distance over packed sign bits."""
    qbits = quantize.to_bits(query)
    scored = [(i, _hamming(qbits, bits)) for i, bits in enumerate(corpus_bits)]
    scored.sort(key=lambda pair: pair[1])
    return [i for i, _ in scored[:k]]


def _hamming(a: bytes, b: bytes) -> int:
    return sum(bin(x ^ y).count("1") for x, y in zip(a, b))


def _rng_unit(rng: random.Random) -> list[float]:
    return _unit([rng.gauss(0, 1) for _ in range(DIM)])


# --- to_bits -----------------------------------------------------------------


def test_to_bits_produces_exactly_96_bytes_for_768_dims():
    """bit[768] in the vec0 schema is 96 bytes. A different width is a hard error
    at insert time, so the width is worth pinning here."""
    assert len(quantize.to_bits([0.1] * DIM)) == 96


def test_to_bits_packs_the_sign_of_each_component():
    vec = [1.0] * DIM
    assert quantize.to_bits(vec) == b"\xff" * 96

    vec = [-1.0] * DIM
    assert quantize.to_bits(vec) == b"\x00" * 96


def test_to_bits_distinguishes_vectors_that_differ_in_one_component():
    """A single sign flip must change exactly one bit -- otherwise the Hamming
    ordering that the coarse pass depends on is not measuring what it claims."""
    a = [1.0] * DIM
    b = [1.0] * DIM
    b[5] = -1.0
    assert _hamming(quantize.to_bits(a), quantize.to_bits(b)) == 1


def test_to_bits_rejects_wrong_dimension():
    with pytest.raises(ValueError, match="768"):
        quantize.to_bits([0.1] * 512)


# --- to_int8 -----------------------------------------------------------------


def test_to_int8_produces_exactly_768_bytes():
    assert len(quantize.to_int8([0.1] * DIM)) == 768


def test_to_int8_uses_the_full_signed_range():
    """Per-vector scaling exists to spend the whole [-127, 127] range on every
    vector. If the largest component does not reach 127 the scaling is not
    happening and precision is being thrown away."""
    rng = random.Random(7)
    raw = quantize.to_int8(_rng_unit(rng))
    values = [b - 256 if b > 127 else b for b in raw]
    assert max(abs(v) for v in values) == 127


def test_to_int8_handles_the_zero_vector_without_dividing_by_zero():
    assert quantize.to_int8([0.0] * DIM) == b"\x00" * 768


def test_to_int8_rejects_wrong_dimension():
    with pytest.raises(ValueError, match="768"):
        quantize.to_int8([0.1] * 100)


# --- rescore -----------------------------------------------------------------


def test_rescore_on_empty_candidates_returns_empty_list():
    """The coarse pass can legitimately return nothing on a fresh pack. That is
    an empty result, not an error."""
    assert quantize.rescore([0.1] * DIM, []) == []


def test_rescore_preserves_ordering_for_clearly_separated_vectors():
    """Round-tripping through int8 must not reorder candidates that float32
    ranks unambiguously."""
    rng = random.Random(99)
    query = _rng_unit(rng)

    near = _unit([q + rng.gauss(0, 0.05) for q in query])
    mid = _unit([q + rng.gauss(0, 0.5) for q in query])
    far = _unit([-q + rng.gauss(0, 0.05) for q in query])

    got = quantize.rescore(
        query,
        [(2, quantize.to_int8(far)), (0, quantize.to_int8(near)), (1, quantize.to_int8(mid))],
    )
    assert [cid for cid, _ in got] == [0, 1, 2]


def test_rescore_returns_scores_in_descending_order():
    rng = random.Random(3)
    query = _rng_unit(rng)
    candidates = [(i, quantize.to_int8(_rng_unit(rng))) for i in range(20)]

    scores = [score for _, score in quantize.rescore(query, candidates)]
    assert scores == sorted(scores, reverse=True)


def test_rescore_scores_a_vector_against_itself_near_one():
    rng = random.Random(11)
    vec = _rng_unit(rng)
    [(_, score)] = quantize.rescore(vec, [(0, quantize.to_int8(vec))])
    assert score == pytest.approx(1.0, abs=0.01)


def test_rescore_tolerates_a_zero_candidate_without_raising():
    """A zero vector has no direction, so cosine is undefined. It must score
    finite and sort last rather than producing nan and poisoning the sort."""
    rng = random.Random(5)
    query = _rng_unit(rng)
    got = quantize.rescore(
        query, [(0, b"\x00" * 768), (1, quantize.to_int8(query))]
    )
    assert [cid for cid, _ in got] == [1, 0]
    assert all(math.isfinite(score) for _, score in got)


# --- the measurement ---------------------------------------------------------


def _measure_recall(pool: int, seed: int = 1234, size: int = 2000) -> tuple[float, float]:
    """Return (end-to-end recall@10, coarse ceiling) for a synthetic corpus.

    The ceiling is how many of the true top-10 survived the Hamming cut at all.
    End-to-end recall cannot exceed it, so the gap between the two is precisely
    the damage done by int8 -- which is the only part this module controls.
    """
    rng = random.Random(seed)
    corpus = [_rng_unit(rng) for _ in range(size)]
    queries = [_rng_unit(rng) for _ in range(50)]

    # Quantize the corpus once. A pack quantizes at build time, so recomputing
    # per query would also misrepresent what the search actually does.
    corpus_bits = [quantize.to_bits(c) for c in corpus]
    corpus_i8 = [quantize.to_int8(c) for c in corpus]

    end_to_end, ceiling = [], []
    for q in queries:
        truth = {i for i, _ in _topk_float(q, corpus, 10)}
        coarse = _topk_hamming(q, corpus_bits, pool)
        ceiling.append(len(truth & set(coarse)) / 10)
        got = {
            i
            for i, _ in quantize.rescore(q, [(i, corpus_i8[i]) for i in coarse])[:10]
        }
        end_to_end.append(len(truth & got) / 10)

    return sum(end_to_end) / len(end_to_end), sum(ceiling) / len(ceiling)


# 30% of the synthetic corpus. Chosen from the measured curve in the recall
# test below for margin (0.946, comfortably clear of 0.85) rather than at the
# thinnest pool that would pass: pool=400 also clears it, but by only 0.032,
# which is inside the seed-to-seed variation this benchmark shows.
_POOL = 600


def test_int8_rescoring_recovers_what_the_coarse_pass_hands_it():
    """This is the claim *this module* is accountable for.

    Recall is bounded by the coarse Hamming cut, which is a search-layer choice
    of how many candidates to overfetch. What quantization must not do is lose
    the good candidates that the coarse pass did surface. Measured, it barely
    loses any: end-to-end recall sits within ~0.01 of the ceiling.

    That is the result which justifies storing 768-byte int8 rows instead of
    3072-byte float32 ones.

    The 0.01 tolerance is set from measurement, not taste. Per-vector scaling
    loses 0.002; replacing it with a fixed global scale of 127 loses 0.018, a
    9x difference. The threshold sits between the two so that this test defends
    the scaling decision rather than merely describing it.
    """
    end_to_end, ceiling = _measure_recall(pool=300)
    assert ceiling - end_to_end <= 0.01, (
        f"int8 rescoring lost {ceiling - end_to_end:.3f} recall below the "
        f"coarse ceiling {ceiling:.3f} -- quantization, not the coarse cut, "
        f"is now the bottleneck"
    )


def test_binary_coarse_plus_rescore_retains_recall_against_float_baseline():
    """The design rests on this number. Measure it, do not assume it.

    Deterministic synthetic corpus: 2000 vectors, 50 queries, compare the
    top-10 float32 ground truth against binary-coarse -> int8-rescore.

    Measured recall@10 against candidate pool size (2000-vector corpus)::

        pool   100    200    300    400    600    800   1000
        recall 0.592  0.736  0.838  0.882  0.946  0.956  0.970

    The plan's provisional pool of 300 yields **0.838, below the 0.85 the
    design assumes**. That is a property of the overfetch, not of quantization:
    at every pool size the end-to-end figure sits within ~0.01 of the coarse
    ceiling (see the test above). Raising the overfetch is what buys recall,
    and it is cheap -- the 96-byte coarse scan is unchanged and only the count
    of 768-byte int8 rows read grows.

    Uniform random vectors are close to a worst case: in 768 dimensions every
    pair is near-orthogonal, so the top-10 are separated by razor-thin margins.
    A clustered corpus resembling real topic structure was *also* measured and
    came out no better (0.804 at pool 300), so the pessimism is not an artifact
    that real data automatically removes.

    The honest scope of this number: it validates the mechanism on synthetic
    data. The figure that decides the design is recall on real embeddings over
    real documents, which Task 12 measures.
    """
    mean, _ = _measure_recall(pool=_POOL)
    assert mean >= 0.85, f"recall@10 {mean:.3f} below the 0.85 the design assumes"
