from argus.config import DEFAULT_EXCLUDE_DIRS
from argus.parse import filters

KW = {"max_bytes": 1000, "exclude_dirs": DEFAULT_EXCLUDE_DIRS}


def test_is_binary_detects_null_byte():
    assert filters.is_binary(b"abc\x00def")
    assert not filters.is_binary(b"plain text")


def test_is_binary_only_scans_first_8k():
    assert not filters.is_binary(b"a" * 8192 + b"\x00")


def test_detect_lang():
    assert filters.detect_lang("src/a.cpp") == "cpp"
    assert filters.detect_lang("src/a.h") == "cpp"
    assert filters.detect_lang("src/a.c") == "c"
    assert filters.detect_lang("s.py") == "python"
    assert filters.detect_lang("README.md") == "markdown"
    assert filters.detect_lang("a.bin") is None


def test_should_index_accepts_source():
    assert filters.should_index("src/main.cpp", 100, b"int main(){}", **KW)


def test_should_index_rejects_oversize():
    assert not filters.should_index("src/main.cpp", 2000, b"x", **KW)


def test_should_index_rejects_binary():
    assert not filters.should_index("src/main.cpp", 10, b"\x00\x01", **KW)


def test_should_index_rejects_unknown_extension():
    assert not filters.should_index("assets/logo.bin", 10, b"x", **KW)


def test_should_index_rejects_excluded_dirs():
    for path in (
        "third_party/zlib/zlib.c",
        "src/vendor/x.c",
        "build/gen.cpp",
        "x64/Release/thing.c",
        "node_modules/pkg/i.js",
    ):
        assert not filters.should_index(path, 10, b"x", **KW), path


def test_excluded_dir_matches_whole_component_only():
    """'outbound' must not be excluded just because 'out' is on the list."""
    assert filters.should_index("src/outbound/net.c", 10, b"x", **KW)
