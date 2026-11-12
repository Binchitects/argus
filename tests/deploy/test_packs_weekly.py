"""
tools/packs-weekly.py: which pack of a name is the newest (a week's date after 1.0, 11.12 after 11.5), what keeps a new
pack out (another embedding model, far fewer pages), the index of the newest of each name with where each is served (a
pack unchanged this week where it was released before), and pruning to the two newest of each name.
"""
from __future__ import annotations

import importlib.util
import json
import sqlite3
import tempfile
import unittest
from pathlib import Path

HERE = Path(__file__).resolve().parent
SCRIPT = HERE.parents[1] / "tools" / "packs-weekly.py"
spec = importlib.util.spec_from_file_location("packs_weekly", SCRIPT)
pw = importlib.util.module_from_spec(spec)
spec.loader.exec_module(pw)


def pack(path: Path, name: str, version: str, docs: int = 100, model: str = "nomic-embed-text", commit: str = "abc") -> Path:
    con = sqlite3.connect(path)
    con.execute("create table pack_meta (key text primary key, value text)")
    con.executemany("insert into pack_meta values (?, ?)", [("source_name", name), ("pack_version", version), ("doc_count", str(docs)),
                    ("embedding_model", model), ("embedding_dim", "768"), ("source_commit", commit), ("license", "MIT"), ("attribution", "them")])
    con.commit()
    con.close()
    return path


class PacksWeekly(unittest.TestCase):
    def setUp(self):
        self.tmp = tempfile.TemporaryDirectory()
        self.packs = Path(self.tmp.name)
        pw.PACKS = self.packs

    def tearDown(self):
        self.tmp.cleanup()

    def test_The_newest_of_each_name_is_by_the_version_it_records(self):
        pack(self.packs / "cpp.kpack", "cpp", "1.0")
        pack(self.packs / "cpp-2026.11.5.kpack", "cpp", "2026.11.5")
        pack(self.packs / "cpp-2026.11.12.kpack", "cpp", "2026.11.12")
        pack(self.packs / "sqlite.kpack", "sqlite", "1.0")
        (self.packs / "broken.kpack").write_text("not a pack")
        newest = pw.newest()
        self.assertEqual({"cpp": "cpp-2026.11.12.kpack", "sqlite": "sqlite.kpack"}, {n: p.name for n, (p, _) in newest.items()})

    def test_A_pack_with_another_model_or_far_fewer_pages_is_kept_out_unless_forced(self):
        old = (self.packs / "a.kpack", {"embedding_model": "nomic-embed-text", "embedding_dim": "768", "doc_count": "1000"})
        self.assertIsNone(pw.check(pack(self.packs / "b.kpack", "x", "2026.11.12", docs=900), old, False))
        self.assertIn("pages", pw.check(pack(self.packs / "c.kpack", "x", "2026.11.12", docs=500), old, False))
        self.assertIn("embedding model", pw.check(pack(self.packs / "d.kpack", "x", "2026.11.12", model="other"), old, False))
        self.assertIsNone(pw.check(self.packs / "c.kpack", old, True))
        # The first of its name, or one before of a handful of pages (a fixture), is no measure.
        self.assertIsNone(pw.check(self.packs / "c.kpack", None, False))
        self.assertIsNone(pw.check(self.packs / "c.kpack", (old[0], {**old[1], "doc_count": "5"}), False))

    def test_The_index_lists_the_newest_of_each_with_where_it_is_served(self):
        unchanged = pack(self.packs / "sqlite.kpack", "sqlite", "1.0")
        (self.packs / "published.json").write_text(json.dumps({"sqlite.kpack": {
            "url": "https://example.test/packs-2026.11.05/sqlite.kpack", "sha256": pw.sha256(unchanged), "release": "packs-2026.11.05"}}))
        pack(self.packs / "cpp.kpack", "cpp", "1.0")
        new = pack(self.packs / "cpp-2026.11.12.kpack", "cpp", "2026.11.12")
        index, sums = pw.write_index("2026.11.12", [new], publish=True)
        # Remembered only once the release holds them.
        self.assertNotIn("cpp-2026.11.12.kpack", json.loads((self.packs / "published.json").read_text()))
        pw.remember_published()
        entries = {e["name"]: e for e in json.loads(index.read_text())["packs"]}
        self.assertEqual("2026.11.12", entries["cpp"]["version"])
        self.assertTrue(entries["cpp"]["url"].endswith("/releases/download/packs-2026.11.12/cpp-2026.11.12.kpack"))
        self.assertEqual("https://example.test/packs-2026.11.05/sqlite.kpack", entries["sqlite"]["url"])
        self.assertIn(f"{pw.sha256(new)}  cpp-2026.11.12.kpack", sums.read_text())
        published = json.loads((self.packs / "published.json").read_text())
        self.assertEqual("packs-2026.11.12", published["cpp-2026.11.12.kpack"]["release"])
        self.assertEqual("packs-2026.11.05", published["sqlite.kpack"]["release"])

    def test_Pruning_keeps_the_two_newest_of_each_name(self):
        for v in ("1.0", "2026.11.5", "2026.11.12"):
            pack(self.packs / f"cpp-{v}.kpack", "cpp", v)
        pw.prune()
        self.assertEqual(["cpp-2026.11.12.kpack", "cpp-2026.11.5.kpack"], sorted(p.name for p in self.packs.glob("*.kpack")))
        self.assertEqual(["cpp-1.0.kpack"], [p.name for p in (self.packs / "superseded").iterdir()])


if __name__ == "__main__":
    unittest.main()
