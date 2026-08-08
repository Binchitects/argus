"""Binary and int8 quantization for pack embeddings.

A pack stores each chunk's embedding twice, and neither copy is float32:

    bit[768]   96 bytes   the coarse pass, scanned for every query
    int8[768]  768 bytes  read only for the coarse pass's top-k

float32 would be 3072 bytes. At a million chunks that is the difference between
a ~96 MB scan and a ~3 GB one, which is the entire reason a pack can be a file
you download rather than a service you host.

That saving is only honest if the two-stage search actually finds what float32
would have found. ``tests/packs/test_quantize.py`` measures it against an exact
baseline. The measured result, which the search layer needs to know:

**Recall is bounded by the coarse cut, not by quantization.** End-to-end
recall@10 sits within ~0.01 of the coarse ceiling at every pool size tried, so
int8 rescoring recovers essentially everything the Hamming pass surfaces. What
recall costs is *overfetch*: on a 2000-vector synthetic corpus, a 300-candidate
pool scored 0.838 -- below the 0.85 the design assumes -- while 600 scored
0.946. Whatever calls ``rescore`` therefore owns the recall number, and 300 is
known to be too few. Overfetch is cheap: the 96-byte coarse scan is unchanged
and only the count of 768-byte int8 rows read grows.

The synthetic figure validates the mechanism, not the product. Recall on real
embeddings over real documents is measured in Task 12.
"""

from __future__ import annotations

import array
import math
from typing import Sequence

from ..embed import EMBED_DIM

#: Follows the configured embedder rather than being fixed, so a
#: deployment can build with a different model. Existing packs are
#: unaffected: each carries its own vector-table declaration and the
#: dimension it was built at, and require_compatible refuses to rank
#: across two spaces.
DIM = EMBED_DIM
_BYTES_BIN = DIM // 8
_INT8_MAX = 127


def _check_dim(vec: Sequence[float]) -> None:
    if len(vec) != DIM:
        raise ValueError(f"expected {DIM} dimensions, got {len(vec)}")


def to_bits(vec: Sequence[float]) -> bytes:
    """Pack the sign of each component into one bit: 768 floats -> 96 bytes.

    This is a sign random projection (SimHash): for unit vectors the fraction of
    differing bits estimates the angle between them, so Hamming distance is a
    usable stand-in for cosine during the coarse pass.

    Bits are packed LSB-first within each byte. The convention is arbitrary --
    Hamming distance is invariant under any fixed permutation of bit positions
    -- but it must match between stored and query vectors, so it lives here and
    nowhere else.
    """
    _check_dim(vec)
    out = bytearray(_BYTES_BIN)
    for i, value in enumerate(vec):
        if value > 0:
            out[i >> 3] |= 1 << (i & 7)
    return bytes(out)


def to_int8(vec: Sequence[float]) -> bytes:
    """Scale a vector to signed 8-bit, using the full range for every vector.

    The scale is per-vector (largest absolute component maps to 127) rather than
    a single global constant. That sounds like it would break comparability
    between differently-scaled candidates, and under a raw dot product it would.
    Under cosine it does not: the scale factor appears in both the dot product
    and the candidate's norm and cancels exactly. So per-vector scaling spends
    the whole [-127, 127] range on every vector at no cost in ranking fidelity,
    and no scale factor needs storing alongside the bytes.

    A global scale would be far coarser. Components of a unit vector in 768
    dimensions sit around 1/sqrt(768) ~= 0.036, so scaling by 127 would confine
    almost every value to +/-19 of the available range.
    """
    _check_dim(vec)
    peak = max((abs(x) for x in vec), default=0.0)
    if peak == 0.0:
        # A zero vector has no direction to preserve; scaling it is meaningless
        # and dividing by the peak would raise.
        return bytes(DIM)
    scale = _INT8_MAX / peak
    return array.array("b", [_clamp(round(x * scale)) for x in vec]).tobytes()


def _clamp(value: int) -> int:
    # round() can reach 128 for a component exactly at the peak on some inputs;
    # array('b') would raise rather than saturate.
    return max(-_INT8_MAX, min(_INT8_MAX, value))


def rescore(
    query_vec: Sequence[float], candidates: list[tuple[int, bytes]]
) -> list[tuple[int, float]]:
    """Cosine-rank int8 candidates against a float query, best first.

    The query stays float32: it is one vector, so there is nothing to save by
    quantizing it, and keeping it exact removes one source of error from the
    stage whose whole job is to correct the coarse pass's mistakes.
    """
    if not candidates:
        return []

    _check_dim(query_vec)
    qnorm = math.sqrt(sum(x * x for x in query_vec))
    if qnorm == 0.0:
        return [(chunk_id, 0.0) for chunk_id, _ in candidates]

    scored = []
    for chunk_id, raw in candidates:
        values = array.array("b")
        values.frombytes(raw)
        dot = sum(q * v for q, v in zip(query_vec, values))
        norm = math.sqrt(sum(v * v for v in values))
        # A directionless candidate is neither similar nor opposed, so it scores
        # 0 -- the cosine of orthogonality. Returning nan here would poison the
        # sort; returning -1 would claim a maximal dissimilarity we cannot know.
        scored.append((chunk_id, dot / (qnorm * norm) if norm else 0.0))

    scored.sort(key=lambda pair: -pair[1])
    return scored
