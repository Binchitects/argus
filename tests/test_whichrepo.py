from argus import whichrepo
from argus.whichrepo import Shape


def test_a_diff_is_detected_before_anything_else():
    text = "diff --git a/src/eal/x.c b/src/eal/x.c\n@@ -1,3 +1,4 @@\n+int x;\n"
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
