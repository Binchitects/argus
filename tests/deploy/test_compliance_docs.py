"""
tools/compliance-docs.py: the controls and each framework's clauses written from controls.yaml and frameworks.yaml,
current in the repository, every clause a control cites listed, every document the clauses name there.
"""
from __future__ import annotations

import importlib.util
import io
import unittest
from contextlib import redirect_stderr
from pathlib import Path

HERE = Path(__file__).resolve().parent
SCRIPT = HERE.parents[1] / "tools" / "compliance-docs.py"
spec = importlib.util.spec_from_file_location("compliance_docs", SCRIPT)
cd = importlib.util.module_from_spec(spec)
spec.loader.exec_module(cd)


class ComplianceDocs(unittest.TestCase):
    def test_The_written_documents_are_current(self):
        err = io.StringIO()
        with redirect_stderr(err):
            code = cd.main(["--check"])
        self.assertEqual(0, code, err.getvalue())

    def test_Every_clause_a_control_cites_is_listed_and_every_document_is_there(self):
        self.assertEqual([], cd.problems(cd.load("controls.yaml"), cd.load("frameworks.yaml")))

    def test_A_clause_counts_for_itself_or_the_nearest_one_it_is_part_of(self):
        known = {"Art.32": [], "164.308(a)(5)(ii)(C)": [], "164.308(a)(5)(i)": []}
        self.assertEqual("Art.32", cd.entry("Art.32(1)(a)", known))
        self.assertEqual("164.308(a)(5)(ii)(C)", cd.entry("164.308(a)(5)(ii)(C)", known))
        self.assertIsNone(cd.entry("164.308(a)(5)(ii)(D)", known))
        self.assertIsNone(cd.entry("Art.33", known))

    def test_A_clause_not_listed_and_a_missing_document_are_found(self):
        controls = {"controls": [{"id": "X-01", "frameworks": {"gdpr": ["Art.99"]}}]}
        catalog = {"documents": {"gone": ["policies/not-there.md", "Gone"]},
                   "frameworks": {"gdpr": {"title": "GDPR", "clauses": [["Art.5", "Principles", "nowhere"]]}}}
        wrong = cd.problems(controls, catalog)
        self.assertEqual(3, len(wrong), wrong)
        self.assertTrue(any("Art.99" in w for w in wrong))
        self.assertTrue(any("nowhere" in w for w in wrong))
        self.assertTrue(any("not-there.md" in w for w in wrong))

    def test_Each_framework_lists_every_clause_once_and_links_each_control_to_its_section(self):
        catalog, controls = cd.load("frameworks.yaml"), cd.load("controls.yaml")
        sections = {cd.slug(cd.heading(c)) for c in controls["controls"]}
        for key, fw in catalog["frameworks"].items():
            ids = [c[0] for c in fw["clauses"]]
            self.assertEqual(len(ids), len(set(ids)), key)
            text = cd.framework_md(key, controls, catalog)
            for anchor in __import__("re").findall(r"\.\./controls\.md#([\w-]+)", text):
                self.assertIn(anchor, sections, f"{key}: {anchor}")
        self.assertEqual(93, len(catalog["frameworks"]["iso27001"]["clauses"]))


class Weakened(unittest.TestCase):
    OLD = {"controls": [
        {"id": "IAM-01", "frameworks": {"gdpr": ["Art.32"], "soc2": ["CC6.1"]}, "tests": ["a", "b"], "checks": ["app:passwords"]},
        {"id": "LOG-01", "frameworks": {}, "tests": ["c"]},
        {"id": "NET-01", "frameworks": {}, "tests": ["d"]}]}

    def test_A_control_gone_a_clause_or_a_check_dropped_or_fewer_tests_is_weaker_and_a_renamed_test_is_not(self):
        new = {"controls": [
            {"id": "IAM-01", "frameworks": {"gdpr": ["Art.32"]}, "tests": ["a2"], "checks": []},
            {"id": "NET-01", "frameworks": {"gdpr": ["Art.32"]}, "tests": ["d renamed"]}]}
        found = cd.weakened(self.OLD, new)
        self.assertEqual({"IAM-01", "LOG-01"}, set(found))
        self.assertEqual(["soc2 CC6.1 no longer served", "the check app:passwords gone", "2 tests now 1"], found["IAM-01"])
        self.assertEqual(["removed"], found["LOG-01"])

    def test_The_next_releases_entry_names_each_under_Weakened_controls_or_it_fails(self):
        found = {"LOG-01": ["removed"], "IAM-01": ["2 tests now 1"]}
        released_only = "# CHANGELOG\n\n## v7.1.0 (2026-10-27)\n\n### :warning: Weakened controls\n\n- LOG-01, IAM-01\n"
        self.assertEqual(2, len(cd.unexplained(found, "v7.1.0", released_only)), "the released entry is not the next one")
        partly = "## v7.2.0 (2026-11-01)\n\n### :warning: Weakened controls\n\n- **LOG-01** folded into LOG-02\n\n### :bug: Bugs fixed\n\n- IAM-01 typo\n\n## v7.1.0\n"
        self.assertEqual(["IAM-01"], [w.split(" ")[0] for w in cd.unexplained(found, "v7.1.0", partly)])
        self.assertEqual([], cd.unexplained({}, "v7.1.0", ""))

    def test_Nothing_is_weaker_than_in_the_newest_release_unless_the_changelog_says_so(self):
        last = cd.released()
        if last is None:
            self.skipTest("the newest release has no controls.yaml")
        self.assertEqual([], cd.unexplained(cd.weakened(last[1], cd.load("controls.yaml")), last[0], (cd.ROOT / "CHANGELOG.md").read_text()))


if __name__ == "__main__":
    unittest.main()
