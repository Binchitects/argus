#!/usr/bin/env python3
"""A release's software bill of materials and its known vulnerabilities: the release gate.

    python3 tools/release-scan.py                 dist/argus-arena-VERSION.cdx.json (CycloneDX 1.5) and its .sha256;
                                                  exits 1 when a critical vulnerability is not accepted
    python3 tools/release-scan.py --no-dotnet     without asking .NET for its packages (no SDK here)

What it lists: the .NET packages of the app, Argus and Code Arena (every transitive one, as `dotnet list package`
resolves them), the web apps' npm packages (their lock files, without the build tools), the Python packages the
images pin (`pip install name==version` in their Dockerfiles), and the base images. What it asks: OSV
(api.osv.dev), which gathers GitHub's, PyPI's, npm's and NuGet's advisories, for each package at its version.

A critical finding fails the release unless docs/compliance/accepted-vulnerabilities.yaml accepts it, with why and
until when (an acceptance past its date counts no more). High ones are listed, and do not fail it.
"""
from __future__ import annotations

import argparse
import hashlib
import json
import re
import subprocess
import sys
import urllib.error
import urllib.request
from datetime import date, datetime, timezone
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent
OSV = "https://api.osv.dev/v1"
DOTNET_PROJECTS = ("src/Llm.Api", "src/Argus", "src/CodeArena")
LOCKS = ("src/web/package-lock.json", "src/argus-web/package-lock.json")
ORDER = {"critical": 0, "high": 1, "medium": 2, "low": 3, "unknown": 4}


def purl(kind: str, name: str, version: str) -> str:
    return f"pkg:{kind}/{name}@{version}"


def dotnet_packages(run=subprocess.run) -> list[dict]:
    """Every package each project resolves, from `dotnet list package --include-transitive --format json` (tools/dn)."""
    found: dict[str, dict] = {}
    for project in DOTNET_PROJECTS:
        out = run([str(ROOT / "tools" / "dn"), "list", project, "package", "--include-transitive", "--format", "json"],
                  capture_output=True, text=True, cwd=ROOT, timeout=600)
        if out.returncode != 0:
            raise RuntimeError(f"dotnet list package failed for {project}: {out.stderr.strip()[:300]}")
        data = json.loads(out.stdout[out.stdout.index("{"):])
        for p in data.get("projects", []):
            for fw in p.get("frameworks", []):
                for pkg in fw.get("topLevelPackages", []) + fw.get("transitivePackages", []):
                    found[f"{pkg['id']}@{pkg['resolvedVersion']}"] = {"ecosystem": "nuget", "name": pkg["id"], "version": pkg["resolvedVersion"]}
    return sorted(found.values(), key=lambda c: (c["name"].lower(), c["version"]))


def npm_packages(lock: Path) -> list[dict]:
    """The packages a lock file installs for the app itself (dev-only ones, the build tools, are not shipped)."""
    data = json.loads(lock.read_text())
    found = {}
    for path, pkg in data.get("packages", {}).items():
        if not path or pkg.get("dev") or pkg.get("link") or "version" not in pkg:
            continue
        name = pkg.get("name") or path.rsplit("node_modules/", 1)[-1]
        found[f"{name}@{pkg['version']}"] = {"ecosystem": "npm", "name": name, "version": pkg["version"]}
    return sorted(found.values(), key=lambda c: (c["name"], c["version"]))


PIN = re.compile(r"(?<![\w.-])([A-Za-z0-9][A-Za-z0-9._-]*)(?:\[[\w,]+\])?==([0-9][A-Za-z0-9.+!-]*)")


def python_packages(dockerfiles: list[Path]) -> list[dict]:
    """`name==version` in each Dockerfile's pip install lines (a line continued with a backslash is one line)."""
    found = {}
    for f in dockerfiles:
        text = f.read_text().replace("\\\n", " ")
        for line in text.splitlines():
            if "pip install" not in line:
                continue
            for name, version in PIN.findall(line):
                found[f"{name.lower()}@{version}"] = {"ecosystem": "pypi", "name": name.lower(), "version": version}
    return sorted(found.values(), key=lambda c: (c["name"], c["version"]))


FROM = re.compile(r"^\s*FROM\s+(?:--platform=\S+\s+)?(\S+)", re.M | re.I)


def images(dockerfiles: list[Path]) -> list[dict]:
    """The base images (listed, not scanned here: Trivy scans images where it is installed)."""
    found = {}
    for f in dockerfiles:
        for ref in FROM.findall(f.read_text()):
            if "$" in ref or ":" not in ref.split("/")[-1]:
                continue
            name, version = ref.rsplit(":", 1)
            found[ref] = {"ecosystem": "docker", "name": name, "version": version}
    return sorted(found.values(), key=lambda c: c["name"])


def severity(vuln: dict) -> str:
    """GitHub's severity when the advisory carries it (most do); else the CVSS base score's band; else unknown."""
    def named(said) -> str | None:
        s = {"moderate": "medium"}.get(said.lower(), said.lower()) if isinstance(said, str) else None
        return s if s in ORDER else None

    if s := named((vuln.get("database_specific") or {}).get("severity")):
        return s
    for affected in vuln.get("affected", []):
        if s := named((affected.get("ecosystem_specific") or {}).get("severity") or (affected.get("database_specific") or {}).get("severity")):
            return s
    for s in vuln.get("severity", []):
        m = re.search(r"(\d+(?:\.\d+)?)$", str(s.get("score", "")))
        if m:
            score = float(m.group(1))
            return "critical" if score >= 9 else "high" if score >= 7 else "medium" if score >= 4 else "low"
    return "unknown"


def post(url: str, body: dict, opener=urllib.request.urlopen) -> dict:
    req = urllib.request.Request(url, json.dumps(body).encode(), {"Content-Type": "application/json"})
    with opener(req, timeout=60) as r:
        return json.loads(r.read())


def get(url: str, opener=urllib.request.urlopen) -> dict:
    with opener(urllib.request.Request(url), timeout=60) as r:
        return json.loads(r.read())


ECOSYSTEM = {"nuget": "NuGet", "npm": "npm", "pypi": "PyPI"}


def scan(components: list[dict], opener=urllib.request.urlopen) -> list[dict]:
    """Each known vulnerability of the packages, from OSV: id, aliases, severity, the package it affects, its summary."""
    scanned = [c for c in components if c["ecosystem"] in ECOSYSTEM]
    findings, details = [], {}
    for start in range(0, len(scanned), 500):
        batch = scanned[start:start + 500]
        answer = post(f"{OSV}/querybatch", {"queries": [{"package": {"name": c["name"], "ecosystem": ECOSYSTEM[c["ecosystem"]]}, "version": c["version"]} for c in batch]}, opener)
        for c, result in zip(batch, answer.get("results", [])):
            for v in result.get("vulns", []) or []:
                if v["id"] not in details:
                    details[v["id"]] = get(f"{OSV}/vulns/{v['id']}", opener)
                d = details[v["id"]]
                findings.append({"id": v["id"], "aliases": d.get("aliases", []), "severity": severity(d), "package": c, "summary": d.get("summary") or ""})
    return sorted(findings, key=lambda f: (ORDER[f["severity"]], f["package"]["name"], f["id"]))


def accepted(path: Path, today: date) -> dict[str, dict]:
    """The accepted findings, by id, while their date has not passed (a small YAML list: id, package, reason, until)."""
    if not path.exists():
        return {}
    items, current = [], None
    for line in path.read_text().splitlines():
        if not line.strip() or line.lstrip().startswith("#"):
            continue
        m = re.match(r"^\s*(-\s+)?(\w+):\s*(.*?)\s*$", line)
        if not m:
            continue
        if m.group(1):
            current = {}
            items.append(current)
        if current is not None:
            current[m.group(2)] = m.group(3).strip('"\'')
    return {i["id"]: i for i in items if "id" in i and date.fromisoformat(i.get("until", "1970-01-01")) >= today}


def bom(version: str, components: list[dict], findings: list[dict]) -> dict:
    """CycloneDX 1.5, its components with their package URLs, and the vulnerabilities found."""
    kind = {"nuget": "nuget", "npm": "npm", "pypi": "pypi", "docker": "docker"}
    refs = {}
    comps = []
    for c in components:
        ref = purl(kind[c["ecosystem"]], c["name"], c["version"])
        refs[(c["ecosystem"], c["name"], c["version"])] = ref
        comps.append({"type": "container" if c["ecosystem"] == "docker" else "library", "bom-ref": ref, "name": c["name"], "version": c["version"], "purl": ref})
    return {
        "bomFormat": "CycloneDX", "specVersion": "1.5", "version": 1,
        "metadata": {"timestamp": datetime.now(timezone.utc).strftime("%Y-%m-%dT%H:%M:%SZ"),
                     "component": {"type": "application", "name": "argus-arena", "version": version, "bom-ref": f"argus-arena@{version}"}},
        "components": comps,
        "vulnerabilities": [{"id": f["id"], "source": {"name": "OSV", "url": f"https://osv.dev/vulnerability/{f['id']}"},
                             "ratings": [{"severity": f["severity"]}], "description": f["summary"],
                             "affects": [{"ref": refs[(f["package"]["ecosystem"], f["package"]["name"], f["package"]["version"])]}]} for f in findings],
    }


def main(argv: list[str] | None = None, opener=urllib.request.urlopen, run=subprocess.run, today: date | None = None) -> int:
    p = argparse.ArgumentParser(description=__doc__.split("\n\n")[0])
    p.add_argument("--no-dotnet", action="store_true", help="leave out the .NET packages (no SDK here)")
    p.add_argument("--out", default=str(ROOT / "dist"))
    a = p.parse_args(argv)
    version = (ROOT / "VERSION").read_text().strip()
    dockerfiles = sorted([*ROOT.glob("src/*/Dockerfile"), *ROOT.glob("deploy/services/*/Dockerfile"), *ROOT.glob("deploy/services/*.Dockerfile")])
    components = ([] if a.no_dotnet else dotnet_packages(run)) + [c for lock in LOCKS if (ROOT / lock).exists() for c in npm_packages(ROOT / lock)]
    components += python_packages(dockerfiles) + images(dockerfiles)
    print(f"release-scan: {version}, {len(components)} components "
          f"({', '.join(f'{sum(1 for c in components if c['ecosystem'] == e)} {e}' for e in ('nuget', 'npm', 'pypi', 'docker'))})")
    try:
        findings = scan(components, opener)
    except (urllib.error.URLError, TimeoutError, OSError) as e:
        print(f"release-scan: OSV could not be asked ({e}): nothing is known of the vulnerabilities, so the release does not pass", file=sys.stderr)
        return 2
    ok = accepted(ROOT / "docs" / "compliance" / "accepted-vulnerabilities.yaml", today or date.today())
    out = Path(a.out)
    out.mkdir(parents=True, exist_ok=True)
    sbom = out / f"argus-arena-{version}.cdx.json"
    sbom.write_text(json.dumps(bom(version, components, findings), indent=1) + "\n")
    digest = hashlib.sha256(sbom.read_bytes()).hexdigest()
    (out / f"{sbom.name}.sha256").write_text(f"{digest}  {sbom.name}\n")
    blocking = []
    for f in findings:
        mark = "accepted" if f["id"] in ok or any(x in ok for x in f["aliases"]) else ""
        print(f"  {f['severity']:8} {f['id']:22} {f['package']['ecosystem']}:{f['package']['name']}@{f['package']['version']}  {f['summary'][:90]}{'  (' + mark + ')' if mark else ''}")
        if f["severity"] == "critical" and not mark:
            blocking.append(f)
    counts = {s: sum(1 for f in findings if f["severity"] == s) for s in ORDER}
    print(f"release-scan: {sbom} ({digest[:16]}); found {', '.join(f'{n} {s}' for s, n in counts.items() if n) or 'nothing known'}")
    if blocking:
        print(f"release-scan: {len(blocking)} critical, not accepted: the release does not ship. Upgrade, or accept it with why and until when "
              f"in docs/compliance/accepted-vulnerabilities.yaml", file=sys.stderr)
        return 1
    return 0


if __name__ == "__main__":
    sys.exit(main())
