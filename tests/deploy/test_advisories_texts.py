"""
tools/advisories-texts.py: OSV's ranges read as intervals (introduced up to fixed or last affected), and each advisory's
page and symbols, marked known-exploited when CISA lists its CVE.
"""
from __future__ import annotations

import importlib.util
import unittest
from pathlib import Path

HERE = Path(__file__).resolve().parent
SCRIPT = HERE.parents[1] / "tools" / "advisories-texts.py"
spec = importlib.util.spec_from_file_location("advisories_texts", SCRIPT)
at = importlib.util.module_from_spec(spec)
spec.loader.exec_module(at)


class AdvisoriesTexts(unittest.TestCase):
    def test_OSV_events_are_intervals(self):
        affected = {"ranges": [{"type": "ECOSYSTEM", "events": [{"introduced": "0"}, {"fixed": "1.2.0"}, {"introduced": "2.0.0"}, {"last_affected": "2.1.0"},
                                                                {"introduced": "3.0.0"}]}]}
        self.assertEqual([("0", "1.2.0", None), ("2.0.0", None, "2.1.0"), ("3.0.0", None, None)], at.ranges(affected))
        self.assertEqual([("1.0", None, "1.0")], at.ranges({"versions": ["1.0"]}))
        self.assertEqual(">= 2.0.0, <= 2.1.0", at.describe(("2.0.0", None, "2.1.0")))
        self.assertEqual("< 1.2.0", at.describe(("0", "1.2.0", None)))

    def test_a_page_says_its_fix_and_whether_it_is_exploited(self):
        d = {"id": "GHSA-x", "aliases": ["CVE-2021-1"], "summary": "Prototype  pollution", "published": "2021-02-15T00:00:00Z",
             "severity": [{"type": "CVSS_V3", "score": "CVSS:3.1/AV:N"}], "database_specific": {"severity": "HIGH", "cwe_ids": ["CWE-1321"]},
             "affected": [{"package": {"ecosystem": "npm", "name": "lodash"}, "ranges": [{"type": "ECOSYSTEM", "events": [{"introduced": "0"}, {"fixed": "4.17.21"}]}]}],
             "details": "Details.", "references": [{"url": "https://example.test/a"}]}
        text, title, symbols = at.page(d, {"CVE-2021-1": {"dateAdded": "2022-01-10", "knownRansomwareCampaignUse": "Known", "requiredAction": "Patch."}})
        self.assertEqual("GHSA-x: Prototype pollution", title)
        self.assertIn("**Known exploited** (CISA KEV, added 2022-01-10; ransomware use: Known). Patch.", text)
        self.assertIn("| npm | `lodash` | < 4.17.21 | 4.17.21 |", text)
        self.assertEqual(["GHSA-x", "CVE-2021-1"], [s["name"] for s in symbols])
        self.assertEqual("GHSA-x: Prototype pollution (npm lodash; high; fixed in 4.17.21; KNOWN EXPLOITED)", symbols[0]["signature"])


if __name__ == "__main__":
    unittest.main()
