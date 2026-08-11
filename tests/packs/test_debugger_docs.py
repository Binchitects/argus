"""Tests for the Debugging Tools for Windows adapter.

The title cases below are not invented: each is a real title taken from the
977 pages of ``debuggercmds``, chosen because it breaks a simpler rule. The
naive "use api_name" and "use the whole title" approaches both produce a
usable-looking inventory on the common case and prose on these.
"""

from __future__ import annotations

from pathlib import Path

from argus.packs.sources.debugger_docs import (
    DebuggerDocs, alias_names, command_names,
)


class TestCommandNames:
    def test_strips_the_parenthetical_gloss(self):
        """540 of the 977 pages carry one; it is not part of the command."""
        assert command_names("!analyze (WinDbg)") == ["!analyze"]
        assert command_names(".abandon (Abandon Process)") == [".abandon"]
        assert command_names("tct (Trace to Next Call or Return)") == ["tct"]

    def test_keeps_a_bare_title_intact(self):
        assert command_names("!acpiirqarb") == ["!acpiirqarb"]
        assert command_names("!bthkd.bthdevinfo") == ["!bthkd.bthdevinfo"]

    def test_drops_a_trailing_prose_tail(self):
        """The rest of the line is description, not part of what you type.

        Without this the pack would answer docs_lookup for
        "* Asterisk character Comment Line Specifier" and miss "*".
        """
        assert command_names("!amli debugger") == ["!amli"]
        assert command_names(
            "* Asterisk character Comment Line Specifier") == ["*"]

    def test_splits_a_title_listing_several_spellings(self):
        """Each spelling is a real thing to look up, pointing at one page."""
        assert command_names("$<, $><, $$<, $$><, $$ >a< (Run Script File)") == [
            "$<", "$><", "$$<", "$$><", "$$",
        ]

    def test_handles_punctuation_only_commands(self):
        assert command_names("?? (Evaluate C++ Expression)") == ["??"]
        assert command_names("; (Command Separator)") == [";"]
        assert command_names("|| (System Status)") == ["||"]

    def test_empty_title_yields_nothing(self):
        assert command_names("") == []
        assert command_names(None) == []


class TestAliasNames:
    def test_keeps_a_bare_single_token(self):
        assert alias_names({"api_name": ["analyze", "NA"]}) == ["analyze"]
        assert alias_names({"api_name": ["pcitree", "NA"]}) == ["pcitree"]

    def test_rejects_a_title_copied_into_api_name(self):
        """The reason the inventory is not built from api_name at all.

        For dot and plain commands the field is the title verbatim, gloss
        included -- not a name anyone types.
        """
        assert alias_names(
            {"api_name": [".abandon (Abandon Process)", "NA"]}) == []
        assert alias_names(
            {"api_name": ["Activating a Debugging Client"]}) == []

    def test_missing_or_malformed_api_name(self):
        assert alias_names({}) == []
        assert alias_names({"api_name": "analyze"}) == []
        assert alias_names({"api_name": ["NA"]}) == []


def _fixture(root: Path) -> Path:
    """A checkout with both docsets, shaped like the real repository."""
    cmds = root / "windows-driver-docs-pr" / "debuggercmds"
    arts = root / "windows-driver-docs-pr" / "debugger"
    cmds.mkdir(parents=True)
    arts.mkdir(parents=True)
    (cmds / "-analyze.md").write_text(
        # Block-sequence items at column 0, exactly as the real files write
        # them -- the shape that a parser demanding indentation drops.
        '---\ntitle: "!analyze (WinDbg)"\n'
        'description: "Displays information about the current exception."\n'
        "topic_type:\n- apiref\n"
        "api_name:\n- analyze\n- NA\n---\n\n# !analyze\n\nBody text.\n",
        encoding="utf-8",
    )
    # A landing page: no topic_type, so no symbol. Its title would otherwise
    # be indexed as a command called "Debugger".
    (cmds / "index.md").write_text(
        "---\ntitle: Debugger Commands\ndescription: Command index.\n---\n\nIndex.\n",
        encoding="utf-8",
    )
    (arts / "index.md").write_text(
        "---\ntitle: Debugging Overview\ndescription: How to debug.\n---\n\nProse.\n",
        encoding="utf-8",
    )
    return root


class TestIteration:
    def test_docs_come_from_both_docsets_without_colliding(self, tmp_path):
        """Both docsets have their own index.md; the prefix keeps them apart."""
        docs = list(DebuggerDocs().iter_docs(_fixture(tmp_path)))
        paths = sorted(d.path for d in docs)
        assert paths == [
            "debugger/index.md",
            "debuggercmds/-analyze.md",
            "debuggercmds/index.md",
        ]

    def test_url_points_at_the_right_public_docset(self, tmp_path):
        by_path = {d.path: d for d in DebuggerDocs().iter_docs(_fixture(tmp_path))}
        assert by_path["debuggercmds/-analyze.md"].url.endswith(
            "/drivers/debuggercmds/-analyze")
        assert by_path["debugger/index.md"].url.endswith("/drivers/debugger/index")

    def test_description_is_prepended_to_the_body(self, tmp_path):
        by_path = {d.path: d for d in DebuggerDocs().iter_docs(_fixture(tmp_path))}
        assert by_path["debuggercmds/-analyze.md"].body.startswith(
            "Displays information about the current exception.")

    def test_both_spellings_are_indexed(self, tmp_path):
        """docs_lookup is exact-match, so '!analyze' and 'analyze' must both hit."""
        symbols = list(DebuggerDocs().iter_symbols(_fixture(tmp_path)))
        assert sorted(s.name for s in symbols) == ["!analyze", "analyze"]

    def test_a_landing_page_produces_no_symbol(self, tmp_path):
        """"Debugger Commands" is a page title, not a command called Debugger."""
        names = {s.name for s in DebuggerDocs().iter_symbols(_fixture(tmp_path))}
        assert "Debugger" not in names

    def test_symbol_doc_path_matches_its_document(self, tmp_path):
        """The link the builder resolves: symbol -> page, modulo the suffix."""
        root = _fixture(tmp_path)
        docs = {d.path for d in DebuggerDocs().iter_docs(root)}
        for symbol in DebuggerDocs().iter_symbols(root):
            assert f"{symbol.doc_path}.md" in docs, symbol

    def test_signature_carries_the_description_docs_find_searches(self, tmp_path):
        symbols = {s.name: s for s in DebuggerDocs().iter_symbols(_fixture(tmp_path))}
        assert symbols["!analyze"].signature == (
            "Displays information about the current exception.")

    def test_no_symbols_from_the_articles_docset(self, tmp_path):
        """An article title is not a command, however it is spelled."""
        symbols = list(DebuggerDocs().iter_symbols(_fixture(tmp_path)))
        assert all(s.doc_path.startswith("debuggercmds/") for s in symbols)

    def test_missing_checkout_yields_nothing_rather_than_raising(self, tmp_path):
        assert list(DebuggerDocs().iter_docs(tmp_path / "absent")) == []
        assert list(DebuggerDocs().iter_symbols(tmp_path / "absent")) == []


def _collision_fixture(root: Path) -> Path:
    """Two pages: the real `dt` command, and `!dt` whose alias would shadow it."""
    cmds = root / "windows-driver-docs-pr" / "debuggercmds"
    cmds.mkdir(parents=True)
    (cmds / "dt--display-type-.md").write_text(
        "---\ntitle: dt (Display Type)\ndescription: Displays a variable.\n"
        "topic_type:\n- apiref\napi_name:\n- dt\n- NA\n---\n\nBody.\n",
        encoding="utf-8",
    )
    (cmds / "-dt.md").write_text(
        '---\ntitle: "!dt (WinDbg)"\ndescription: Displays a CSR thread.\n'
        "topic_type:\n- apiref\napi_name:\n- dt\n- NA\n---\n\nBody.\n",
        encoding="utf-8",
    )
    return root


class TestAliasCollisions:
    def test_an_alias_never_shadows_another_pages_command(self, tmp_path):
        """`!dt` carries the bare alias `dt`, which is a different command.

        `dt` cannot invoke `!dt`, so the alias is a spelling that does not
        exist. Left in, docs_lookup("dt") returns both and the one you asked
        for is no longer distinguishable from the one you cannot type.
        """
        symbols = list(DebuggerDocs().iter_symbols(_collision_fixture(tmp_path)))
        dt = [s for s in symbols if s.name == "dt"]
        assert len(dt) == 1, [s.doc_path for s in dt]
        assert dt[0].doc_path.endswith("dt--display-type-")
        assert dt[0].signature == "Displays a variable."

    def test_the_page_still_keeps_its_own_command(self, tmp_path):
        """Suppressing the alias must not suppress `!dt` itself."""
        names = {s.name for s in DebuggerDocs().iter_symbols(_collision_fixture(tmp_path))}
        assert "!dt" in names and "dt" in names

    def test_a_non_colliding_alias_survives(self, tmp_path):
        """The 567 aliases that add a genuinely new spelling are unaffected."""
        names = {s.name for s in DebuggerDocs().iter_symbols(_fixture(tmp_path))}
        assert {"!analyze", "analyze"} <= names
