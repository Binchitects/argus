"""Tests for the public documentation MCP tools.

Two properties matter beyond "it returns rows": these tools must not be able
to reach the private code index, and they must do their synchronous work in a
single threadpool hop like every other tool on this server.
"""

from __future__ import annotations

import dataclasses
import sqlite3

import pytest

from argus import embed as embed_module
from argus.config import Config, GitLabConfig, IndexConfig, PacksConfig
from argus.mcpsrv import tools
from argus.mcpsrv.tools import DocsUnavailable
from argus.packs import build
from argus.packs.sources.python_docs import PythonDocs
from argus.packs.sources.react_docs import ReactDocs

from tests.packs.test_build import COMMIT, FIXTURES, fake_embed


@pytest.fixture(scope="module")
def packs_dir(tmp_path_factory):
    directory = tmp_path_factory.mktemp("installed")
    for source, name, version in ((PythonDocs(), "python", "3.13"),
                                  (ReactDocs(), "react", "1.0.0")):
        build.build_pack(
            source, work_dir=FIXTURES / name,
            out_path=directory / f"{name}.arguspack",
            version=version, embed_fn=fake_embed, source_commit=COMMIT,
        )
    return directory


@pytest.fixture
def stub_embedder(monkeypatch):
    """The tools embed the query themselves; no Ollama in tests."""
    monkeypatch.setattr(tools, "embed_batch", fake_embed)


def cfg_for(packs_dir, tmp_path) -> Config:
    return Config(
        gitlab=GitLabConfig(url="https://gitlab.invalid", token="t"),
        index=IndexConfig(data_dir=tmp_path, db_path=tmp_path / "index.db"),
        packs=PacksConfig(dir=packs_dir),
    )


# --- registration and descriptions ---------------------------------------------


def test_both_tools_are_registered(packs_dir, tmp_path):
    from mcp.server.fastmcp import FastMCP

    server = FastMCP("test")
    tools.register_tools(server, cfg_for(packs_dir, tmp_path))
    names = {t.name for t in server._tool_manager.list_tools()}
    assert {"docs_lookup", "docs_search"} <= names


@pytest.mark.parametrize("desc", [tools._DOCS_LOOKUP_DESC, tools._DOCS_SEARCH_DESC])
def test_descriptions_say_the_results_are_public_and_attributable(desc):
    """A description is what a 35B model reads to choose a tool and to decide
    whether to attribute. Both facts have to be in the text."""
    lowered = desc.lower()
    assert "public" in lowered
    assert "private" in lowered, "must distinguish itself from the code index"
    assert "license" in lowered
    assert "cite" in lowered or "attribut" in lowered


def test_docs_search_description_explains_the_lexical_fallback():
    assert "lexical" in tools._DOCS_SEARCH_DESC.lower()


# --- results -------------------------------------------------------------------


@pytest.mark.anyio
async def test_docs_lookup_returns_an_anchored_url_with_licence(packs_dir):
    rows = await tools.docs_lookup_impl(packs_dir, "os.path.join")
    assert rows, "expected a hit"
    [row] = rows
    assert row["url"] == "https://docs.python.org/3/library/os.path.html#os.path.join"
    assert row["source"] == "python"
    assert row["license"] == "PSF-2.0"
    assert row["attribution"]


@pytest.mark.anyio
async def test_docs_lookup_narrows_by_source(packs_dir):
    assert await tools.docs_lookup_impl(packs_dir, "useState", lang="react")
    assert await tools.docs_lookup_impl(packs_dir, "useState", lang="python") == []


@pytest.mark.anyio
async def test_docs_search_returns_semantic_results_with_attribution(packs_dir, stub_embedder):
    rows = await tools.docs_search_impl(packs_dir, "state hook", limit=5)
    assert rows, "expected results"
    for row in rows:
        assert row["retrieval"] == "semantic"
        assert row["url"] and row["license"] and row["attribution"]
        assert row["source"] in {"python", "react"}


@pytest.mark.anyio
async def test_docs_search_spans_both_installed_packs(packs_dir, stub_embedder):
    rows = await tools.docs_search_impl(packs_dir, "state hook", limit=10)
    assert rows
    assert {r["source"] for r in rows} == {"python", "react"}


# --- isolation from the private index -------------------------------------------


@pytest.mark.anyio
async def test_neither_tool_can_reach_the_private_index(packs_dir, stub_embedder, tmp_path):
    """A private-repo symbol must be absent from documentation results even
    when a populated private index sits right beside the packs."""
    private = tmp_path / "index.db"
    conn = sqlite3.connect(private)
    conn.executescript(
        "CREATE TABLE symbols (name TEXT); "
        "INSERT INTO symbols VALUES ('AcmeInternalSecretWidget');"
    )
    conn.commit()
    conn.close()

    assert await tools.docs_lookup_impl(packs_dir, "AcmeInternalSecretWidget") == []
    rows = await tools.docs_search_impl(packs_dir, "AcmeInternalSecretWidget", limit=10)
    blob = repr(rows)
    assert "AcmeInternalSecretWidget" not in blob


def test_the_docs_tools_take_no_identity_or_allowlist():
    """Growing an identity parameter would mean an access decision is being
    made somewhere, and there is none to make over public documentation."""
    import inspect

    for fn in (tools.docs_lookup_impl, tools.docs_search_impl):
        params = inspect.signature(fn).parameters
        assert not any(p in params for p in ("identity", "allowed_repo_ids", "ctx"))


# --- actionable failures --------------------------------------------------------


@pytest.mark.anyio
async def test_a_mismatched_pack_yields_an_actionable_message_not_a_traceback(
    packs_dir, stub_embedder, tmp_path,
):
    mismatched_dir = tmp_path / "mismatched"
    mismatched_dir.mkdir()
    target = mismatched_dir / "python.arguspack"
    target.write_bytes((packs_dir / "python.arguspack").read_bytes())
    conn = sqlite3.connect(target)
    conn.execute("UPDATE pack_meta SET value = ? WHERE key = 'embedding_model'",
                 ("some-other-model",))
    conn.commit()
    conn.close()

    with pytest.raises(DocsUnavailable) as caught:
        await tools.docs_search_impl(mismatched_dir, "state hook")

    message = str(caught.value)
    assert "python" in message, "must name the offending pack"
    assert "some-other-model" in message, "must name the model it was built with"
    assert embed_module.EMBED_MODEL in message, "must name the model we serve"
    assert "docs_lookup" in message, "must say what still works"
    assert "Traceback" not in message


@pytest.mark.anyio
async def test_a_mismatched_pack_still_serves_lookup(packs_dir, tmp_path):
    mismatched_dir = tmp_path / "mismatched2"
    mismatched_dir.mkdir()
    target = mismatched_dir / "python.arguspack"
    target.write_bytes((packs_dir / "python.arguspack").read_bytes())
    conn = sqlite3.connect(target)
    conn.execute("UPDATE pack_meta SET value = ? WHERE key = 'embedding_model'",
                 ("some-other-model",))
    conn.commit()
    conn.close()

    assert await tools.docs_lookup_impl(mismatched_dir, "os.path.join")


@pytest.mark.anyio
async def test_no_installed_packs_says_so_rather_than_returning_empty(tmp_path):
    """An empty list would read as 'not documented'. It is a configuration
    problem, and the model should not retry it."""
    empty = tmp_path / "empty"
    empty.mkdir()
    with pytest.raises(DocsUnavailable, match="No documentation packs"):
        await tools.docs_lookup_impl(empty, "os.path.join")


@pytest.mark.anyio
async def test_an_unreachable_embedder_falls_back_to_labelled_lexical(packs_dir, monkeypatch):
    def dead(texts):
        raise embed_module.EmbeddingUnavailable("connection refused")

    monkeypatch.setattr(tools, "embed_batch", dead)
    rows = await tools.docs_search_impl(packs_dir, "Hook", limit=5)

    assert rows, "expected the lexical fallback to return something"
    for row in rows:
        assert row["retrieval"] == "lexical", "a fallback must never be labelled semantic"
        assert "less precise" in row["note"]


# --- the threadpool invariant ---------------------------------------------------


@pytest.mark.anyio
async def test_each_tool_call_uses_exactly_one_threadpool_hop(packs_dir, stub_embedder, monkeypatch):
    """Every sqlite tool on this server does its work in one hop. Embedding the
    query is a blocking HTTP call, so it has to be inside that same hop rather
    than on the event loop before it."""
    calls = []
    real = tools.run_in_threadpool

    async def counting(fn, *args, **kwargs):
        calls.append(fn)
        return await real(fn, *args, **kwargs)

    monkeypatch.setattr(tools, "run_in_threadpool", counting)

    await tools.docs_lookup_impl(packs_dir, "os.path.join")
    assert len(calls) == 1, f"docs_lookup used {len(calls)} threadpool hops"

    calls.clear()
    await tools.docs_search_impl(packs_dir, "state hook")
    assert len(calls) == 1, f"docs_search used {len(calls)} threadpool hops"


@pytest.mark.anyio
async def test_the_query_is_embedded_inside_the_threadpool_not_on_the_loop(
    packs_dir, monkeypatch,
):
    """Pins where the embedding happens, not merely that it happens."""
    import threading

    loop_thread = threading.current_thread().ident
    embedded_on = []

    def recording(texts):
        embedded_on.append(threading.current_thread().ident)
        return fake_embed(texts)

    monkeypatch.setattr(tools, "embed_batch", recording)
    await tools.docs_search_impl(packs_dir, "state hook")

    assert embedded_on, "the embedder was never called"
    assert embedded_on[0] != loop_thread, "the query was embedded on the event loop"


def test_the_registered_docs_tools_declare_no_context_parameter(packs_dir, tmp_path):
    """Declaring ctx would advertise an identity these tools neither have nor
    need, and would invite someone to start resolving one."""
    from mcp.server.fastmcp import FastMCP

    server = FastMCP("test")
    tools.register_tools(server, cfg_for(packs_dir, tmp_path))
    listed = {t.name: t for t in server._tool_manager.list_tools()}
    for name in ("docs_lookup", "docs_search"):
        schema = listed[name].parameters
        assert "ctx" not in schema.get("properties", {}), name


@pytest.mark.anyio
async def test_docs_lookup_ignores_a_lang_that_names_no_installed_source(packs_dir):
    """A guessed source name must not launder into "not documented".

    Measured against the real estate: `CryptAcquireContextW` is present under
    `win32`, but a model guessing `lang="windows"` got an empty list and --
    following the server's own instruction that silence means no authority --
    reported the API as undocumented. The filter was wrong, not the corpus.

    Matching is exact, so widening can only find the same name in a pack the
    caller failed to name.
    """
    rows = await tools.docs_lookup_impl(packs_dir, "os.path.join", lang="windows")
    assert rows, "a nonexistent source filter must not suppress an exact hit"
    assert rows[0]["source"] == "python"
    assert rows[0]["lang_filter_ignored"] == "windows"


@pytest.mark.anyio
async def test_docs_lookup_still_honours_narrowing_to_a_real_source(packs_dir):
    """Scoping to an INSTALLED source stays exact -- empty is the true answer.

    This is the half the widening must not break: `useState` genuinely is not
    in the Python pack, and saying so is correct rather than a miss.
    """
    assert await tools.docs_lookup_impl(packs_dir, "useState", lang="python") == []
    hits = await tools.docs_lookup_impl(packs_dir, "useState", lang="react")
    assert hits and "lang_filter_ignored" not in hits[0]
