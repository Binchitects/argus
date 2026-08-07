from argus import whichrepo
from argus.whichrepo import Shape


def test_a_diff_is_detected_before_anything_else():
    text = "diff --git a/src/eal/x.c b/src/eal/x.c\n@@ -1,3 +1,4 @@\n+int x;\n"
    assert whichrepo.detect_shape(text) == Shape.DIFF


def test_a_diff_containing_frame_shaped_lines_is_still_a_diff():
    """A diff whose hunk body happens to contain stack-frame-shaped text
    (e.g. reviewing a change to a crash log fixture) must still be
    classified as DIFF. This is only discriminating if the diff check runs
    before the stack-frame check: the hunk below has two frame matches and
    an `at ...` line, so it would be misread as Shape.STACK if the stack
    check ran first."""
    text = (
        "diff --git a/tests/fixtures/crash.log b/tests/fixtures/crash.log\n"
        "@@ -1,3 +1,4 @@\n"
        "-  at decode_frame (src/codec/decode.c:412)\n"
        "+  at decode_frame (src/codec/decode.c:413)\n"
        "+  at main (src/app/main.c:88)\n"
    )
    assert whichrepo.detect_shape(text) == Shape.DIFF


def test_a_stack_trace_is_detected():
    text = ("Traceback:\n"
            "  at decode_frame (src/codec/decode.c:412)\n"
            "  at main (src/app/main.c:88)\n")
    assert whichrepo.detect_shape(text) == Shape.STACK


def test_a_bare_identifier_is_a_symbol():
    assert whichrepo.detect_shape("decode_frame") == Shape.SYMBOL
    assert whichrepo.detect_shape("eal::Thread") == Shape.SYMBOL


def test_a_sentence_is_prose():
    assert whichrepo.detect_shape("add H.265 support to the decoder") == Shape.PROSE


def test_a_question_is_prose_even_if_it_contains_an_identifier():
    assert whichrepo.detect_shape("where do I add decode_frame support") == Shape.PROSE


def test_diff_paths_are_extracted_without_the_a_b_prefixes():
    text = "diff --git a/src/eal/x.c b/src/eal/x.c\n@@ -1 +1 @@\n"
    assert whichrepo.extract_paths(text) == ["src/eal/x.c"]


def test_stack_frame_paths_are_extracted():
    text = "  at decode_frame (src/codec/decode.c:412)\n  at main (src/app/main.c:88)\n"
    assert whichrepo.extract_paths(text) == ["src/codec/decode.c", "src/app/main.c"]


def test_symbols_are_extracted_from_a_stack_trace():
    text = "  at decode_frame (src/codec/decode.c:412)\n"
    assert "decode_frame" in whichrepo.extract_symbols(text)


def test_prose_stopwords_are_not_treated_as_symbols():
    """'the', 'add' and 'support' are identifier-shaped and would otherwise
    swamp the real terms."""
    got = whichrepo.extract_symbols("add H.265 support to the decoder")
    assert "the" not in got and "add" not in got


def test_a_c_filename_is_not_thrown_away_by_its_extension():
    """The defect this guards is severe for a C/C++ index: `inflate.c` was
    split on '.', the leaf 'c' failed the 3-character minimum, and the whole
    token was discarded. Every .c and .h filename a developer typed extracted
    to nothing, so `which_repo("inflate.c")` -- the most natural way to name a
    file -- returned no answer at all rather than a wrong one.
    """
    assert whichrepo.extract_symbols("inflate.c") == ["inflate.c"]
    assert "psintrp.c" in whichrepo.extract_symbols("the crash is in psintrp.c")
    assert "pngrtran.h" in whichrepo.extract_symbols("pngrtran.h")


def test_a_qualified_name_still_survives():
    """The leaf rule exists for these; keep them working."""
    assert "std::vector" in whichrepo.extract_symbols("uses std::vector here")
    assert "obj.method" in whichrepo.extract_symbols("calls obj.method twice")


def test_a_token_whose_every_part_is_noise_is_still_dropped():
    """Qualifying on *any* part must not become qualifying on nothing --
    'i.e' and 'a.b' carry no retrieval value and would be pure noise."""
    got = whichrepo.extract_symbols("i.e. a.b is the it.of case")
    assert "i.e" not in got and "a.b" not in got and "it.of" not in got, got


def test_a_filename_in_prose_is_looked_up_as_a_file():
    """`extract_paths` only recognised diff headers and stack frames, so a
    filename mentioned in ordinary prose was neither a path nor a symbol:
    `find_symbol("inflate.c")` matches no symbol, and nothing else looked it
    up. `which_repo("inflate.c")` therefore returned [] -- which a caller
    reads as "that code is not indexed here", the most misleading answer
    available.
    """
    assert whichrepo.extract_paths("inflate.c") == ["inflate.c"]
    assert whichrepo.extract_paths("the crash is in src/psaux/psintrp.c") == [
        "src/psaux/psintrp.c"]


def test_prose_without_a_filename_yields_no_paths():
    """The extension is what makes a token a filename. Ordinary prose, and a
    sentence-ending period, must not manufacture one."""
    assert whichrepo.extract_paths("adjust the deflate compression level") == []
    assert whichrepo.extract_paths("fix the decoder. it crashes") == []


def test_a_diff_header_still_wins_over_a_bare_filename():
    """A diff names the file it changes in its header; the same name also
    appears in the hunk body. The header is the authoritative one and must
    not be diluted by a second, less precise source."""
    text = ("diff --git a/src/psaux/psintrp.c b/src/psaux/psintrp.c\n"
            "@@ -1 +1 @@\n-#include \"other.h\"\n+#include \"new.h\"\n")
    assert whichrepo.extract_paths(text) == ["src/psaux/psintrp.c"]
