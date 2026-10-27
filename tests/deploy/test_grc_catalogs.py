"""
tools/grc-catalogs.py --texts: the shareable clauses in their own words, as the grc knowledge pack's pages: NIST's
parameters printed as the publication prints them, its items under their labels, and the AI Act's points on their lines.
"""
from __future__ import annotations

import importlib.util
import tempfile
import unittest
from pathlib import Path

HERE = Path(__file__).resolve().parent
SCRIPT = HERE.parents[1] / "tools" / "grc-catalogs.py"
spec = importlib.util.spec_from_file_location("grc_catalogs", SCRIPT)
gc = importlib.util.module_from_spec(spec)
spec.loader.exec_module(gc)

PARAMS = {
    "ac-02_odp.06": {"id": "ac-02_odp.06", "label": "time period"},
    "ac-01_odp.03": {"id": "ac-01_odp.03", "select": {"how-many": "one-or-more", "choice": ["organization-level", "system-level"]}},
    "A.03.01.01.ODP.01": {"id": "A.03.01.01.ODP.01", "label": "time period", "usage": "organization-defined time period"},
}


class GrcTexts(unittest.TestCase):
    def test_Parameters_read_as_the_publication_prints_them(self):
        self.assertEqual("within [Assignment: organization-defined time period]",
                         gc.oscal_prose("within {{ insert: param, ac-02_odp.06 }}", PARAMS))
        self.assertEqual("[Selection (one or more): organization-level; system-level] policy",
                         gc.oscal_prose("{{ insert: param, ac-01_odp.03 }} policy", PARAMS))
        self.assertEqual("[Assignment: organization-defined time period]", gc.oscal_prose("{{ insert: param, A.03.01.01.ODP.01 }}", PARAMS))

    def test_Items_are_lines_under_their_labels_and_800_171s_labels_are_short(self):
        statement = {"name": "statement", "parts": [
            {"name": "item", "props": [{"name": "label", "value": "a."}], "prose": "Notify:", "parts": [
                {"name": "item", "props": [{"name": "label", "value": "1."}], "prose": "within {{ insert: param, ac-02_odp.06 }};"}]},
            {"name": "item", "props": [{"name": "label", "value": "SR-03.01.01.c.02"}], "prose": "Group  and role membership"}]}
        self.assertEqual(["a. Notify:", "  1. within [Assignment: organization-defined time period];", "2. Group and role membership"],
                         gc.oscal_items(statement, PARAMS))

    def test_An_articles_points_lead_their_lines_and_the_next_chapter_is_not_in_it(self):
        xhtml = """<div id="art_5"><p class="oj-ti-art">Article 5</p><div class="eli-title" id="art_5.tit_1"><p class="oj-sti-art">Prohibited
          AI practices</p></div><div><p class="oj-normal">1.   The following:</p><table><tbody><tr><td valign="top"><p class="oj-normal">(a)</p></td>
          <td valign="top"><p class="oj-normal">the first&nbsp;practice;</p></td></tr></tbody></table></div></div>
          <div id="cpt_III"><p class="oj-ti-section-1">CHAPTER III</p></div>"""
        with tempfile.TemporaryDirectory() as d:
            src, out = Path(d) / "src", Path(d) / "out"
            src.mkdir()
            (src / "ai-act.xhtml").write_text(xhtml, encoding="utf-8")
            [doc] = gc.text_ai_act(src, out)
            self.assertEqual({"path": "euaiact/art_5.md", "title": "Art.5 Prohibited AI practices", "url": gc.TEXT_URL["euaiact"] + "#art_5",
                              "symbols": [{"name": "Art.5", "kind": "article", "namespace": "EU AI Act", "signature": "EU AI Act Art.5: Prohibited AI practices"}]}, doc)
            self.assertEqual("# Article 5: Prohibited AI practices\n\nEU Artificial Intelligence Act, Regulation (EU) 2024/1689.\n\n"
                             "1. The following:\n\n(a) the first practice;\n", (out / doc["path"]).read_text(encoding="utf-8"))


if __name__ == "__main__":
    unittest.main()
