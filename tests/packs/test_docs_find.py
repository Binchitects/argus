"""Ranking guarantees for docs_find.

Each pins a defect that was measured on the real 12-pack corpus, not a
hypothetical: scores that meant different things in different packs, one pack
occupying every slot, and a name match being invisible to the scorer.
"""

from __future__ import annotations

import pytest

from argus.store import packs as packs_store


def test_names_do_not_outweigh_descriptions():
    """The reverse of what this test originally asserted, and the reversal was
    measured rather than reasoned.

    It used to require ``_NAME_WEIGHT > 1.0``, on the argument that a symbol
    CALLED JsonSerializer answers "serialize JSON" better than one merely
    describing it. On the 25-question set that scored **0% top-1**: .NET
    identifiers are long English phrases -- DataFormats.CommaSeparatedValue,
    Process.GetProcesses, FormattedText.WidthIncludingTrailingWhitespace --
    so weighting names above prose inverts the premise of a tool whose job is
    answering "which API does X" from a description. At 0.0 the same corpus
    scores 4% top-1 and 16% top-3.

    Pinned as an upper bound rather than an equality, so raising it to
    experiment is possible but exceeding a description match is not.
    """
    assert packs_store._NAME_WEIGHT < 1.0


def test_a_single_pack_cannot_occupy_every_slot():
    """dotnet holds 215,269 of 394,545 symbols. Without a cap a .NET reading
    of any query fills the answer and a 36-symbol pack is unreachable."""
    scored = [(10.0 - i, {"name": f"n{i}", "source": "dotnet"}) for i in range(10)]
    scored.append((0.1, {"name": "robocopy", "source": "scripting"}))

    rows = packs_store._capped(scored, limit=5)

    # The guarantee is that the smaller pack SURVIVES, not that the larger one
    # is held to exactly _PACK_CAP: once the capped pass runs out of distinct
    # packs, overflow backfills rather than returning a short answer. So
    # dotnet may take a fourth slot here -- what it may not do is take the one
    # robocopy needs, and robocopy scores 0.1 against its 10.0.
    assert any(r["name"] == "robocopy" for r in rows)
    assert rows.index(next(r for r in rows if r["name"] == "robocopy")) < 5


def test_the_cap_does_not_punish_a_pack_that_owns_the_question():
    """When only one pack has answers, a full result set still comes back --
    overflow is appended rather than discarded."""
    scored = [(10.0 - i, {"name": f"n{i}", "source": "dotnet"}) for i in range(10)]

    rows = packs_store._capped(scored, limit=5)

    assert len(rows) == 5
    assert [r["name"] for r in rows] == ["n0", "n1", "n2", "n3", "n4"]


def test_coverage_beats_one_strong_match():
    """A symbol hitting three asked-for words is answering the question; one
    hitting a single rare word is a coincidence with a good score."""
    assert packs_store._COVERAGE_BONUS > 0
