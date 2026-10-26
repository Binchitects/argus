#!/usr/bin/env python3
"""The compliance documents written from docs/compliance/controls.yaml and frameworks.yaml.

    python3 tools/compliance-docs.py           writes docs/compliance/controls.md and frameworks/<framework>.md
    python3 tools/compliance-docs.py --check   exits 1, naming them, when any is not current

controls.md is every control as an auditor reads it: what meets it, the clauses it serves, where it is documented,
the tests that prove it, the checks that watch it and the company's part. Each frameworks/<framework>.md is every
clause of that framework, Arena's controls that serve it, and the company's document that governs it.

Both fail when a control is weaker than in the newest release (gone, a clause or a check dropped, fewer tests) and
CHANGELOG.md's entry for the next release does not name it under Weakened controls.
"""
from __future__ import annotations

import argparse
import os
import re
import subprocess
import sys
from pathlib import Path

import yaml

ROOT = Path(__file__).resolve().parent.parent
HERE = ROOT / "docs" / "compliance"
AREAS = {"IAM": "People and access", "LOG": "Records", "MON": "Monitoring", "DAT": "Data", "NET": "Network",
         "OPS": "Operations", "GOV": "Governance"}
NOTE = "<!-- Written by tools/compliance-docs.py from controls.yaml and frameworks.yaml: change those and run it, not this. -->"


def load(name: str) -> dict:
    """Every scalar as a string (a clause such as 164.410 is not a number)."""
    return yaml.load((HERE / name).read_text(), Loader=yaml.BaseLoader)


def slug(heading: str) -> str:
    """The anchor GitHub gives a heading."""
    return re.sub(r"[^\w\- ]", "", heading.strip().lower()).replace(" ", "-")


def entry(clause: str, known: dict[str, list]) -> str | None:
    """The catalog entry a cited clause counts for: itself, or the nearest one it is part of."""
    while clause not in known:
        shorter = re.sub(r"\([^()]*\)$", "", clause)
        if shorter == clause:
            return None
        clause = shorter
    return clause


def heading(c: dict) -> str:
    return f"{c['id']}: {c['title']}"


def link(doc: str, at: Path) -> str:
    """A link from the file at `at` to docs/<doc> (file.md#anchor)."""
    path, _, anchor = doc.partition("#")
    rel = os.path.relpath(ROOT / "docs" / path, at.parent).replace(os.sep, "/")
    return f"{rel}#{anchor}" if anchor else rel


def problems(controls: dict, catalog: dict) -> list[str]:
    """Clauses cited that the catalog does not list, and documents named that are not there."""
    wrong = []
    for key, fw in catalog["frameworks"].items():
        known = {c[0]: c for c in fw["clauses"]}
        for c in fw["clauses"]:
            if c[2] not in catalog["documents"]:
                wrong.append(f"frameworks.yaml: {key} {c[0]} names the document {c[2]}, which is not in documents")
        for control in controls["controls"]:
            for clause in control.get("frameworks", {}).get(key, []):
                if entry(clause, known) is None:
                    wrong.append(f"controls.yaml: {control['id']} cites {key} {clause}, which frameworks.yaml does not list")
    for key, (path, _) in catalog["documents"].items():
        if not (HERE / path).exists():
            wrong.append(f"frameworks.yaml: the document {key} is docs/compliance/{path}, which is not there")
    return wrong


def controls_md(controls: dict, catalog: dict) -> str:
    at = HERE / "controls.md"
    titles = {key: {c[0]: c[1] for c in fw["clauses"]} for key, fw in catalog["frameworks"].items()}
    out = ["# The controls", "", NOTE, "",
           f"The {len(controls['controls'])} controls Arena provides, from [controls.yaml](controls.yaml). How they are kept: "
           "[README](README.md#controls-and-how-they-are-kept). Each framework's clauses and the controls that serve them: "
           + ", ".join(f"[{fw['title']}](frameworks/{key}.md)" for key, fw in catalog["frameworks"].items()) + ".", ""]
    area = None
    for c in controls["controls"]:
        if c["id"][:3] != area:
            area = c["id"][:3]
            out += [f"## {AREAS[area]} ({area})", ""]
        out += [f"### {heading(c)}", "", " ".join(c["meets"].split()), ""]
        serves = []
        for key, clauses in c.get("frameworks", {}).items():
            known = titles[key]
            named = [f"{cl} {known[entry(cl, known)]}" for cl in clauses]
            serves.append(f"- [{catalog['frameworks'][key]['title']}](frameworks/{key}.md): " + "; ".join(named))
        out += ["**Serves**", "", *serves, ""]
        out += ["**Documented in** " + ", ".join(f"[{d}]({link(d, at)})" for d in c.get("docs", [])), ""]
        out += ["**Proved by**", "", *[f"- `{t}`" for t in c.get("tests", [])], ""]
        if c.get("checks"):
            out += ["**Watched by** " + ", ".join(f"`{x}`" for x in c["checks"]) + " (Admin → Compliance, and `deploy/scripts/compliance-check.py`)", ""]
        out += ["**The company's part** " + " ".join(c.get("operator", "").split()), ""]
    return "\n".join(out).rstrip() + "\n"


def framework_md(key: str, controls: dict, catalog: dict) -> str:
    fw = catalog["frameworks"][key]
    known = {c[0]: c for c in fw["clauses"]}
    serving: dict[str, list[dict]] = {c[0]: [] for c in fw["clauses"]}
    for control in controls["controls"]:
        for clause in control.get("frameworks", {}).get(key, []):
            e = entry(clause, known)
            if e and control not in serving[e]:
                serving[e].append(control)
    served = sum(1 for v in serving.values() if v)
    out = [f"# {fw['title']}", "", NOTE, "",
           f"Arena's controls serve {served} of the {len(fw['clauses'])} clauses here. Every clause is met by the company that runs "
           "Arena, under the document beside it; where Arena has a control, the control is the technical part and its "
           "evidence is in Admin → Compliance. The titles are short names, not the clauses' text. "
           "[Shared responsibility](../README.md#shared-responsibility), [the controls](../controls.md).", "",
           "| Clause | | Arena | The company's document |", "|---|---|---|---|"]
    for clause, title, doc in fw["clauses"]:
        path, name = catalog["documents"][doc]
        arena = ", ".join(f"[{c['id']}](../controls.md#{slug(heading(c))})" for c in serving[clause])
        out.append(f"| {clause} | {title} | {arena} | [{name}](../{path}) |")
    return "\n".join(out) + "\n"


def weakened(old: dict, new: dict) -> dict[str, list[str]]:
    """What a change made weaker, by control: the control gone, a framework's clause, a check or tests fewer."""
    now = {c["id"]: c for c in new["controls"]}
    found: dict[str, list[str]] = {}
    for c in old["controls"]:
        n = now.get(c["id"])
        if n is None:
            found[c["id"]] = ["removed"]
            continue
        why = [f"{key} {cl} no longer served" for key, cls in c.get("frameworks", {}).items() for cl in cls
               if cl not in n.get("frameworks", {}).get(key, [])]
        why += [f"the check {x} gone" for x in c.get("checks", []) if x not in n.get("checks", [])]
        if len(n.get("tests", [])) < len(c.get("tests", [])):
            why.append(f"{len(c['tests'])} tests now {len(n.get('tests', []))}")
        if why:
            found[c["id"]] = why
    return found


def released(run=subprocess.run) -> tuple[str, dict] | None:
    """The newest release tag and its controls.yaml, when it has one."""
    tag = run(["git", "describe", "--tags", "--abbrev=0", "--match", "v*"], capture_output=True, text=True, cwd=ROOT)
    if tag.returncode != 0:
        return None
    shown = run(["git", "show", f"{tag.stdout.strip()}:docs/compliance/controls.yaml"], capture_output=True, text=True, cwd=ROOT)
    return (tag.stdout.strip(), yaml.load(shown.stdout, Loader=yaml.BaseLoader)) if shown.returncode == 0 else None


def unexplained(found: dict[str, list[str]], tag: str, changelog: str) -> list[str]:
    """The weakened controls the changelog's entry for the next release does not name under Weakened controls."""
    if not found:
        return []
    top = re.search(r"^## v?(\d+\.\d+\.\d+)[^\n]*\n(.*?)(?=^## |\Z)", changelog, re.M | re.S)
    entry_ = top.group(2) if top and top.group(1) != tag.lstrip("v") else ""
    part = re.search(r"^### [^\n]*Weakened controls[^\n]*\n(.*?)(?=^##|\Z)", entry_, re.M | re.S)
    named = part.group(1) if part else ""
    return [f"{cid} ({'; '.join(why)}) is weaker than in {tag}: say so, and why, under Weakened controls in CHANGELOG.md's entry for the next release"
            for cid, why in found.items() if cid not in named]


def written() -> dict[Path, str]:
    controls, catalog = load("controls.yaml"), load("frameworks.yaml")
    files = {HERE / "controls.md": controls_md(controls, catalog)}
    for key in catalog["frameworks"]:
        files[HERE / "frameworks" / f"{key}.md"] = framework_md(key, controls, catalog)
    return files


def main(argv: list[str] | None = None) -> int:
    p = argparse.ArgumentParser(description=__doc__.split("\n\n")[0])
    p.add_argument("--check", action="store_true", help="exit 1 when a document is not current, writing nothing")
    a = p.parse_args(argv)
    wrong = problems(load("controls.yaml"), load("frameworks.yaml"))
    if (last := released()) is not None:
        wrong += unexplained(weakened(last[1], load("controls.yaml")), last[0], (ROOT / "CHANGELOG.md").read_text())
    if wrong:
        print("\n".join(wrong), file=sys.stderr)
        return 1
    stale = []
    for path, text in written().items():
        if not path.exists() or path.read_text() != text:
            stale.append(path)
            if not a.check:
                path.parent.mkdir(parents=True, exist_ok=True)
                path.write_text(text)
    if a.check and stale:
        print("compliance-docs: not current: " + ", ".join(str(s.relative_to(ROOT)) for s in stale)
              + " (run python3 tools/compliance-docs.py)", file=sys.stderr)
        return 1
    print(f"compliance-docs: {'checked' if a.check else 'wrote'} {len(written())} documents" + (f", {len(stale)} changed" if stale and not a.check else ""))
    return 0


if __name__ == "__main__":
    sys.exit(main())
