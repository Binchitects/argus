#!/usr/bin/env python3
"""The advisories knowledge pack's pages and its table of what each advisory affects, written from what
tools/fetch-advisories.sh fetched.

    python3 tools/advisories-texts.py --from .packwork/docsrc/advisories --out .packwork/docsrc/advisories-texts

A page per GitHub-reviewed advisory that is not withdrawn: its summary, CVE, severity (and CVSS vector), CWEs, whether
CISA lists it as exploited (and since when, and whether ransomware uses it), the packages and version ranges it
affects with the versions that fix them, its details and references. Its GHSA id and each CVE are symbols; the page
lies under its first package's ecosystem (a facet: npm, nuget, pypi, maven, go, crates, rubygems, packagist, other).
OUT/index.json lists the pages, and, as "affected", a row per package and range (ecosystem, package, introduced, fixed
or last affected, the advisory, its severity, CWEs and whether it is exploited) for Argus's deps_check, which matches a
repository's locked versions against it with no model. The provenance is the database's commit and the KEV catalog's
version.
"""
from __future__ import annotations

import argparse
import json
import subprocess
import sys
from pathlib import Path

FACETS = {"npm": "npm", "NuGet": "nuget", "PyPI": "pypi", "Maven": "maven", "Go": "go", "crates.io": "crates", "RubyGems": "rubygems",
          "Packagist": "packagist"}
SEVERITY = {"LOW": "low", "MODERATE": "medium", "MEDIUM": "medium", "HIGH": "high", "CRITICAL": "critical"}


def ranges(affected: dict) -> list[tuple[str | None, str | None, str | None]]:
    """(introduced, fixed, last_affected) intervals, as OSV's events give them in order; a listed version is its own."""
    out = []
    for r in affected.get("ranges", []):
        introduced = None
        for e in r.get("events", []):
            if "introduced" in e:
                if introduced is not None:
                    out.append((introduced, None, None))
                introduced = e["introduced"]
            elif "fixed" in e:
                out.append((introduced or "0", e["fixed"], None))
                introduced = None
            elif "last_affected" in e:
                out.append((introduced or "0", None, e["last_affected"]))
                introduced = None
        if introduced is not None:
            out.append((introduced, None, None))
    if not out:
        out = [(v, None, v) for v in affected.get("versions", [])]
    return out


def describe(interval: tuple[str | None, str | None, str | None]) -> str:
    introduced, fixed, last = interval
    low = "" if introduced in (None, "0") else f">= {introduced}"
    high = f"< {fixed}" if fixed else f"<= {last}" if last else ""
    return ", ".join(x for x in (low, high) if x) or "every version"


def page(d: dict, kev: dict) -> tuple[str, str, list[dict]]:
    ghsa = d["id"]
    cves = [a for a in d.get("aliases", []) if a.startswith("CVE-")]
    db = d.get("database_specific", {})
    severity = SEVERITY.get(str(db.get("severity", "")).upper(), "unknown")
    vectors = [s["score"] for s in d.get("severity", []) if s.get("type", "").startswith("CVSS")]
    cwes = db.get("cwe_ids", [])
    exploited = [kev[c] for c in cves if c in kev]
    summary = " ".join((d.get("summary") or ghsa).split())
    facts = [", ".join(cves) if cves else "no CVE", f"severity {severity}" + (f" ({vectors[0]})" if vectors else "")]
    if cwes:
        facts.append(", ".join(cwes))
    facts.append(f"published {d.get('published', '')[:10]}")
    body = [f"# {ghsa}: {summary}", "", " · ".join(facts) + "."]
    if exploited:
        k = exploited[0]
        body += ["", f"**Known exploited** (CISA KEV, added {k.get('dateAdded')}; ransomware use: {k.get('knownRansomwareCampaignUse', 'Unknown')}). "
                 f"{k.get('requiredAction', '')}".strip()]
    rows = []
    for a in d.get("affected", []):
        p = a["package"]
        for interval in ranges(a):
            rows.append(f"| {p['ecosystem']} | `{p['name']}` | {describe(interval)} | {interval[1] or 'none yet'} |")
    if rows:
        body += ["", "## Affected", "", "| Ecosystem | Package | Versions | Fixed in |", "| --- | --- | --- | --- |"] + rows
    if d.get("details"):
        body += ["", "## Details", "", d["details"].strip()]
    refs = [r["url"] for r in d.get("references", [])][:12]
    if refs:
        body += ["", "## References", ""] + [f"- {u}" for u in refs]
    packages = sorted({f"{a['package']['ecosystem']} {a['package']['name']}" for a in d.get("affected", [])})
    fixed = sorted({i[1] for a in d.get("affected", []) for i in ranges(a) if i[1]})
    signature = f"{ghsa}: {summary} ({'; '.join(packages[:3])}{'…' if len(packages) > 3 else ''}; {severity}" + \
                (f"; fixed in {', '.join(fixed[:3])}" if fixed else "; no fix yet") + ("; KNOWN EXPLOITED" if exploited else "") + ")"
    symbols = [{"name": ghsa, "kind": "advisory", "namespace": "GitHub Advisory Database", "signature": signature[:300]}]
    symbols += [{"name": c, "kind": "cve", "namespace": "GitHub Advisory Database", "signature": f"{c} = {signature}"[:300]} for c in cves]
    return "\n".join(body), f"{ghsa}: {summary}", symbols


def main(argv: list[str] | None = None) -> int:
    p = argparse.ArgumentParser(description=__doc__.split("\n\n")[0])
    p.add_argument("--from", dest="src", required=True, type=Path, help="the folder tools/fetch-advisories.sh fetched into")
    p.add_argument("--out", required=True, type=Path, help="the pack's work directory to write")
    a = p.parse_args(argv)
    advisories = a.src / "github" / "advisories" / "github-reviewed"
    if not advisories.is_dir() or not (a.src / "kev.json").is_file():
        print(f"advisories-texts: {a.src} lacks github/advisories/github-reviewed or kev.json (run tools/fetch-advisories.sh)", file=sys.stderr)
        return 1
    catalog = json.loads((a.src / "kev.json").read_text(encoding="utf-8"))
    kev = {v["cveID"]: v for v in catalog["vulnerabilities"]}
    commit = subprocess.run(["git", "-C", str(a.src / "github"), "rev-parse", "HEAD"], capture_output=True, text=True, check=True).stdout.strip()
    a.out.mkdir(parents=True, exist_ok=True)
    docs, affected, withdrawn = [], [], 0
    for f in sorted(advisories.rglob("*.json")):
        d = json.loads(f.read_text(encoding="utf-8"))
        if d.get("withdrawn"):
            withdrawn += 1
            continue
        first = next((x["package"]["ecosystem"] for x in d.get("affected", [])), "")
        facet = FACETS.get(first, "other")
        path = f"{facet}/{d['id']}.md"
        text, title, symbols = page(d, kev)
        target = a.out / path
        target.parent.mkdir(parents=True, exist_ok=True)
        url = f"https://github.com/advisories/{d['id']}"
        target.write_text(text.rstrip() + f"\n\n---\nSource: the GitHub Advisory Database, CC BY 4.0. {url}\n", encoding="utf-8")
        docs.append({"path": path, "title": title, "url": url, "symbols": symbols})
        db = d.get("database_specific", {})
        exploited = any(c in kev for c in d.get("aliases", []))
        for x in d.get("affected", []):
            for introduced, fixed, last in ranges(x):
                affected.append({"ecosystem": x["package"]["ecosystem"], "package": x["package"]["name"], "introduced": introduced, "fixed": fixed,
                                 "last_affected": last, "advisory": d["id"], "aliases": ",".join(d.get("aliases", [])),
                                 "severity": SEVERITY.get(str(db.get("severity", "")).upper(), "unknown"), "cwes": ",".join(db.get("cwe_ids", [])),
                                 "exploited": exploited, "summary": " ".join((d.get("summary") or "").split())[:300], "doc_path": path})
    (a.out / "index.json").write_text(json.dumps({"provenance": f"github={commit[:12]},kev={catalog['catalogVersion']}", "docs": docs, "affected": affected},
                                                 ensure_ascii=False) + "\n", encoding="utf-8")
    print(f"advisories-texts: {len(docs)} advisories ({withdrawn} withdrawn left out), {len(affected)} affected ranges, "
          f"{sum(1 for x in docs if any(s['kind'] == 'cve' for s in x['symbols']))} with a CVE")
    return 0


if __name__ == "__main__":
    sys.exit(main())
