from codeindex.parse.includes import extract_includes

SOURCE = '''
#include <stdio.h>
#include "eal/decoder.h"
  #  include   <vector>
#include "eal/decoder.h"
// #include "commented_out.h"
/* #include "block_commented.h" */
const char* s = "#include \\"in_a_string.h\\"";
#include "trailing.h"   // note
'''


def test_extracts_angle_and_quote_includes():
    got = extract_includes(SOURCE)
    assert {"raw": "stdio.h", "is_angle": 1} in got
    assert {"raw": "eal/decoder.h", "is_angle": 0} in got


def test_tolerates_whitespace_variants():
    assert {"raw": "vector", "is_angle": 1} in extract_includes(SOURCE)


def test_deduplicates_preserving_order():
    raws = [i["raw"] for i in extract_includes(SOURCE)]
    assert raws.count("eal/decoder.h") == 1
    assert raws.index("stdio.h") < raws.index("eal/decoder.h")


def test_ignores_commented_and_quoted_includes():
    raws = [i["raw"] for i in extract_includes(SOURCE)]
    assert "commented_out.h" not in raws
    assert "block_commented.h" not in raws
    assert "in_a_string.h" not in raws


def test_handles_trailing_comment():
    assert {"raw": "trailing.h", "is_angle": 0} in extract_includes(SOURCE)


def test_empty_source():
    assert extract_includes("") == []
