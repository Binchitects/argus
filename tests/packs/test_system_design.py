"""The system-design-primer adapter."""
from pathlib import Path

from argus.packs.sources import SystemDesignPrimer
from argus.packs.sources.system_design import is_translation


def _w(root: Path, rel: str, text: str = "# Heading\n\nbody\n") -> None:
    p = root / rel
    p.parent.mkdir(parents=True, exist_ok=True)
    p.write_text(text, encoding="utf-8")


def test_language_tagged_filenames_are_recognised():
    assert is_translation("README-ja.md")
    assert is_translation("README-zh-Hans.md")
    assert is_translation("README-pt-BR.md")
    assert not is_translation("README.md")
    assert not is_translation("CONTRIBUTING.md")


def test_translations_are_not_indexed(tmp_path):
    """11 of the repo's 23 markdown files translate another file already in
    the pack. Indexing them puts near-duplicate content in another language
    into the same vector space: an English query matches a Chinese chunk it
    cannot use, and the duplicate crowds out a distinct result."""
    _w(tmp_path, "README.md")
    _w(tmp_path, "README-ja.md")
    _w(tmp_path, "README-zh-Hans.md")
    _w(tmp_path, "solutions/system_design/pastebin/README.md")
    _w(tmp_path, "solutions/system_design/pastebin/README-zh-Hans.md")
    paths = {d.path for d in SystemDesignPrimer().iter_docs(tmp_path)}
    assert paths == {"README.md", "solutions/system_design/pastebin/README.md"}


def test_case_studies_are_titled_by_subject_not_by_filename(tmp_path):
    """Every case study file is literally README.md, so filename-derived
    titles would be nine identical entries. The directory is the subject."""
    _w(tmp_path, "solutions/system_design/web_crawler/README.md")
    doc = next(iter(SystemDesignPrimer().iter_docs(tmp_path)))
    assert doc.title == "web crawler"


def test_only_case_studies_become_symbols(tmp_path):
    """Concept headings in the main README are prose section titles
    ("Latency numbers every programmer should know"). Turning those into API
    symbols fills docs_lookup with entries that are really search results."""
    _w(tmp_path, "README.md", "# Primer\n\n## Latency numbers everyone knows\n\ntext\n")
    _w(tmp_path, "solutions/system_design/twitter/README.md")
    names = {s.name for s in SystemDesignPrimer().iter_symbols(tmp_path)}
    assert names == {"twitter"}


def test_repository_housekeeping_is_skipped(tmp_path):
    _w(tmp_path, "README.md")
    _w(tmp_path, "CONTRIBUTING.md")
    _w(tmp_path, "TRANSLATIONS.md")
    assert {d.path for d in SystemDesignPrimer().iter_docs(tmp_path)} == {"README.md"}
