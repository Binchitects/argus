from argus.packs import chunk

MD = """\
# fetch()
Intro text.

## Parameters
Some parameters.

### options
A string indicating how to handle a redirect response.

## Return value
A Promise.
"""


def test_heading_trail_is_built_from_nesting():
    chunks = chunk.chunk_markdown(MD)
    trails = [c.heading_path for c in chunks]
    assert "fetch() > Parameters > options" in trails


def test_embedded_text_carries_the_heading_trail():
    """The whole point. A bare section body embeds to nothing useful."""
    c = next(c for c in chunk.chunk_markdown(MD)
             if c.heading_path.endswith("options"))
    text = chunk.embed_text(c)
    assert text.startswith("fetch() > Parameters > options")
    assert "redirect response" in text


def test_a_deeper_heading_does_not_inherit_a_sibling():
    trails = [c.heading_path for c in chunk.chunk_markdown(MD)]
    assert "fetch() > Return value" in trails
    assert not any("options > Return value" in t for t in trails)


def test_oversized_section_is_split_but_keeps_its_trail():
    big = "# Top\n\n## Sec\n\n" + ("word " * 2000)
    parts = [c for c in chunk.chunk_markdown(big, max_chars=500)
             if c.heading_path == "Top > Sec"]
    assert len(parts) > 1
    assert all(chunk.embed_text(p).startswith("Top > Sec") for p in parts)


def test_code_fences_are_not_split_mid_block():
    src = "# T\n\n## S\n\n```python\n" + "x = 1\n" * 400 + "```\n"
    for c in chunk.chunk_markdown(src, max_chars=500):
        assert c.body.count("```") % 2 == 0, "split inside a fenced block"


# --- cases the brief's Step 1 tests don't cover ---------------------------


def test_preamble_before_any_heading_is_still_chunked():
    """Text with no heading above it at all must not be silently dropped."""
    md = "Just some intro text with no heading at all.\n"
    chunks = chunk.chunk_markdown(md)
    assert len(chunks) == 1
    assert chunks[0].heading_path == ""
    assert "intro text" in chunks[0].body


def test_embed_text_with_no_heading_path_is_just_the_body():
    """No trail to prepend -- embed_text must not inject a stray blank line."""
    c = chunk.Chunk(heading_path="", anchor=None, start_line=1, body="plain body")
    assert chunk.embed_text(c) == "plain body"


def test_anchor_is_a_slug_of_the_heading():
    chunks = chunk.chunk_markdown(MD)
    options_chunk = next(c for c in chunks if c.heading_path.endswith("options"))
    assert options_chunk.anchor == "options"

    top_chunk = next(c for c in chunks if c.heading_path == "fetch()")
    # parens are stripped, matching the usual GitHub-style slug.
    assert top_chunk.anchor == "fetch"

    return_chunk = next(c for c in chunks if c.heading_path == "fetch() > Return value")
    assert return_chunk.anchor == "return-value"


def test_same_level_siblings_do_not_nest():
    """A same-level heading must pop its sibling, not append to it -- the
    off-by-one version of the bug the brief calls out (level > n instead of
    level >= n)."""
    md = "# Top\n\n## A\nBody A.\n\n## B\nBody B.\n"
    trails = [c.heading_path for c in chunk.chunk_markdown(md)]
    assert "Top > B" in trails
    assert not any("A > B" in t for t in trails)


def test_small_section_is_not_split():
    """Two short paragraphs that easily both fit under max_chars must be
    packed into one chunk, not needlessly split per-paragraph."""
    md = "# Top\n\n## Sec\nFirst short paragraph.\n\nSecond short paragraph.\n"
    parts = [c for c in chunk.chunk_markdown(md, max_chars=1200)
             if c.heading_path == "Top > Sec"]
    assert len(parts) == 1
    assert "First short paragraph." in parts[0].body
    assert "Second short paragraph." in parts[0].body


def test_code_fence_survives_a_split_forced_by_surrounding_content():
    """The brief's own fence test never actually forces a split -- the whole
    section *is* the fence, so a buggy splitter that ignores fences entirely
    would still pass by accident. Here a paragraph before and after the fence
    forces real splitting, and the fence must land intact in whichever chunk
    it ends up in."""
    src = ("# T\n\n## S\n\n" + ("para " * 200) +
           "\n\n```python\n" + "x = 1\n" * 100 + "```\n\n" +
           ("more " * 200) + "\n")
    parts = [c for c in chunk.chunk_markdown(src, max_chars=500)
             if c.heading_path == "T > S"]
    assert len(parts) > 1, "this content must not fit in a single chunk"
    for p in parts:
        assert p.body.count("```") % 2 == 0, "split inside a fenced block"
