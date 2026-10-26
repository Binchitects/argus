"""
tools/release-scan.py: the SBOM (CycloneDX) of what a release ships, its known vulnerabilities from OSV, and the
gate (a critical one not accepted fails the release), against fakes of OSV and of `dotnet list package`.
"""
from __future__ import annotations

import importlib.util
import io
import json
import tempfile
import unittest
import urllib.error
from contextlib import redirect_stderr, redirect_stdout
from datetime import date
from pathlib import Path

HERE = Path(__file__).resolve().parent
SCRIPT = HERE.parents[1] / "tools" / "release-scan.py"
spec = importlib.util.spec_from_file_location("release_scan", SCRIPT)
rs = importlib.util.module_from_spec(spec)
spec.loader.exec_module(rs)


class Answer(io.BytesIO):
    def __enter__(self):
        return self

    def __exit__(self, *a):
        return False


def osv(vulns_by_package: dict[str, list[dict]]):
    """A fake OSV: querybatch answers each package's ids, /vulns/ID the advisory."""
    details = {v["id"]: v for vs in vulns_by_package.values() for v in vs}

    def opener(req, timeout=None):
        if req.full_url.endswith("/querybatch"):
            queries = json.loads(req.data)["queries"]
            results = [{"vulns": [{"id": v["id"]} for v in vulns_by_package.get(q["package"]["name"], [])]} for q in queries]
            return Answer(json.dumps({"results": results}).encode())
        return Answer(json.dumps(details[req.full_url.rsplit("/", 1)[1]]).encode())
    return opener


def advisory(id_: str, severity: str) -> dict:
    return {"id": id_, "aliases": ["CVE-2026-0001"], "summary": f"{id_} summary", "database_specific": {"severity": severity}}


class Lists(unittest.TestCase):
    def test_What_ships_is_listed_and_the_build_tools_are_not(self):
        with tempfile.TemporaryDirectory() as d:
            lock = Path(d) / "package-lock.json"
            lock.write_text(json.dumps({"packages": {
                "": {"name": "web"}, "node_modules/react": {"version": "19.2.0"}, "node_modules/vite": {"version": "7.0.0", "dev": True},
                "node_modules/@scope/pkg": {"version": "1.0.0"}, "node_modules/a/node_modules/react": {"version": "18.0.0"}}}))
            self.assertEqual([("@scope/pkg", "1.0.0"), ("react", "18.0.0"), ("react", "19.2.0")], [(c["name"], c["version"]) for c in rs.npm_packages(lock)])
            docker = Path(d) / "Dockerfile"
            docker.write_text("FROM python:3.13-slim AS base\nRUN pip install --no-cache-dir \\\n      numpy==2.5.3 Pillow==13.0.0 \\\n      uvicorn[standard]==0.54.0\nRUN echo x==1\nFROM ${BASE}\n")
            self.assertEqual([("numpy", "2.5.3"), ("pillow", "13.0.0"), ("uvicorn", "0.54.0")], [(c["name"], c["version"]) for c in rs.python_packages([docker])])
            self.assertEqual([("python", "3.13-slim")], [(c["name"], c["version"]) for c in rs.images([docker])])

    def test_The_dotnet_packages_are_every_one_each_project_resolves(self):
        listed = {"projects": [{"frameworks": [{"topLevelPackages": [{"id": "Npgsql", "resolvedVersion": "10.0.0"}],
                                                "transitivePackages": [{"id": "System.Text.Json", "resolvedVersion": "10.0.1"}]}]}]}

        class Ran:
            returncode, stderr = 0, ""
            stdout = "noise\n" + json.dumps(listed)
        self.assertEqual(["Npgsql", "System.Text.Json"], [c["name"] for c in rs.dotnet_packages(lambda *a, **k: Ran())])


class Severity(unittest.TestCase):
    def test_GitHubs_severity_then_the_CVSS_score_then_unknown(self):
        self.assertEqual("critical", rs.severity({"database_specific": {"severity": "CRITICAL"}}))
        self.assertEqual("medium", rs.severity({"database_specific": {"severity": "MODERATE"}}))
        self.assertEqual("high", rs.severity({"severity": [{"type": "CVSS_V3", "score": "7.5"}]}))
        self.assertEqual("unknown", rs.severity({"severity": [{"type": "CVSS_V3", "score": "CVSS:3.1/AV:N"}]}))

    def test_An_acceptance_counts_until_its_date(self):
        with tempfile.TemporaryDirectory() as d:
            f = Path(d) / "accepted.yaml"
            f.write_text("# why\n- id: GHSA-aaaa\n  package: npm:x\n  reason: not used\n  until: 2026-11-30\n- id: GHSA-bbbb\n  until: 2026-01-01\n")
            self.assertEqual(["GHSA-aaaa"], list(rs.accepted(f, date(2026, 10, 26))))
            self.assertEqual([], list(rs.accepted(f, date(2026, 12, 1))))


class Gate(unittest.TestCase):
    def run_scan(self, opener, accepted: str = "") -> tuple[int, str, dict]:
        with tempfile.TemporaryDirectory() as d:
            out = io.StringIO()
            original = rs.ROOT / "docs" / "compliance" / "accepted-vulnerabilities.yaml"
            keep = original.read_text()
            try:
                original.write_text(accepted)
                with redirect_stdout(out), redirect_stderr(out):
                    code = rs.main(["--no-dotnet", "--out", d], opener=opener, today=date(2026, 10, 26))
            finally:
                original.write_text(keep)
            boms = list(Path(d).glob("*.cdx.json"))
            return code, out.getvalue(), json.loads(boms[0].read_text()) if boms else {}

    def test_A_critical_finding_fails_the_release_and_is_in_the_SBOM(self):
        code, said, bom = self.run_scan(osv({"react": [advisory("GHSA-crit", "CRITICAL")], "zod": [advisory("GHSA-high", "HIGH")]}))
        self.assertEqual(1, code, said)
        self.assertIn("does not ship", said)
        self.assertEqual("CycloneDX", bom["bomFormat"])
        self.assertTrue(any(c["purl"].startswith("pkg:npm/react@") for c in bom["components"]))
        self.assertTrue(any(c["purl"].startswith("pkg:pypi/") for c in bom["components"]))
        self.assertEqual({"GHSA-crit", "GHSA-high"}, {v["id"] for v in bom["vulnerabilities"]})

    def test_Accepted_with_a_date_not_past_it_ships_and_high_ones_never_stop_it(self):
        code, said, _ = self.run_scan(osv({"react": [advisory("GHSA-crit", "CRITICAL")], "zod": [advisory("GHSA-high", "HIGH")]}),
                                      "- id: GHSA-crit\n  reason: not reachable\n  until: 2026-11-30\n")
        self.assertEqual(0, code, said)
        self.assertIn("(accepted)", said)
        code, _, _ = self.run_scan(osv({"react": [advisory("GHSA-crit", "CRITICAL")]}), "- id: GHSA-crit\n  until: 2026-10-01\n")
        self.assertEqual(1, code)

    def test_OSV_out_of_reach_fails_it_too(self):
        def down(req, timeout=None):
            raise urllib.error.URLError("no route")
        code, said, _ = self.run_scan(down)
        self.assertEqual(2, code)
        self.assertIn("could not be asked", said)


if __name__ == "__main__":
    unittest.main()
