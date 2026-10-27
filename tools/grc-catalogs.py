#!/usr/bin/env python3
"""The frameworks' catalogs written from their published sources: docs/compliance/catalogs/<framework>.yaml.

    python3 tools/grc-catalogs.py --from DIR
    python3 tools/grc-catalogs.py --from DIR --texts OUT


DIR holds the sources as downloaded (the names below; pdftotext reads the Common Criteria's PDFs). Each catalog
says its framework, where its source came from with the file's SHA-256, the source's licence, and every clause as
[id, title, the company's document that governs it] (the documents are frameworks.yaml's). frameworks.yaml names
each catalog; tools/compliance-docs.py writes the framework's page from it. A catalog is written again only when
its source changes: run this, then compliance-docs.py.

What is taken: identifiers and titles, and NIST's baselines. The texts themselves stay at their sources.

--texts writes, instead, the texts that may be shared (NIST's, which are public domain, and the AI Act's, whose reuse the
EU authorises) as a page per clause in its own words, with OUT/index.json listing them: the work directory of Argus's `grc`
knowledge pack (argus pack build --source grc --work-dir OUT). ISO's, PCI DSS's, the CCM's and the Common Criteria's texts
are not shared, so they are not in it.
"""
from __future__ import annotations

import argparse
import hashlib
import html
import json
import re
import subprocess
import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent
OUT = ROOT / "docs" / "compliance" / "catalogs"
OSCAL = "https://raw.githubusercontent.com/usnistgov/oscal-content/main/nist.gov"
SOURCES = {
    "800-53r5.json": f"{OSCAL}/SP800-53/rev5/json/NIST_SP-800-53_rev5_catalog.json",
    "800-53r5-LOW.json": f"{OSCAL}/SP800-53/rev5/json/NIST_SP-800-53_rev5_LOW-baseline_profile.json",
    "800-53r5-MODERATE.json": f"{OSCAL}/SP800-53/rev5/json/NIST_SP-800-53_rev5_MODERATE-baseline_profile.json",
    "800-53r5-HIGH.json": f"{OSCAL}/SP800-53/rev5/json/NIST_SP-800-53_rev5_HIGH-baseline_profile.json",
    "800-53r5-PRIVACY.json": f"{OSCAL}/SP800-53/rev5/json/NIST_SP-800-53_rev5_PRIVACY-baseline_profile.json",
    "800-171r3.json": f"{OSCAL}/SP800-171/rev3/json/NIST_SP800-171_rev3_catalog.json",
    "csf2.json": f"{OSCAL}/CSF/v2.0/json/NIST_CSF_v2.0_catalog.json",
    "airmf-playbook.json": "https://airc.nist.gov/docs/playbook.json",
    "ai-act.xhtml": "http://publications.europa.eu/resource/cellar/dc8116a1-3fe6-11ef-865a-01aa75ed71a1.0006.03/DOC_1",
    "CC2022PART2R1.pdf": "https://www.commoncriteriaportal.org/files/ccfiles/CC2022PART2R1.pdf",
    "CC2022PART3R1.pdf": "https://www.commoncriteriaportal.org/files/ccfiles/CC2022PART3R1.pdf",
}
NIST = "Public domain: a work of the United States government (17 U.S.C. 105), as NIST publishes it."

# The company's document for each family, class or chapter.
FAMILY_53 = {"ac": "access", "at": "people", "au": "logging", "ca": "isms", "cm": "change", "cp": "continuity", "ia": "access",
             "ir": "incidents", "ma": "change", "mp": "data", "pe": "physical", "pl": "information-security", "pm": "isms",
             "ps": "people", "pt": "data", "ra": "risk", "sa": "change", "sc": "information-security", "si": "change",
             "sr": "suppliers"}
FAMILY_171 = {"01": "access", "02": "people", "03": "logging", "04": "change", "05": "access", "06": "incidents", "07": "change",
              "08": "data", "09": "people", "10": "physical", "11": "risk", "12": "isms", "13": "information-security",
              "14": "change", "15": "information-security", "16": "change", "17": "suppliers"}
CSF_FUNCTION = {"GV": "isms", "ID": "risk", "PR": "information-security", "DE": "logging", "RS": "incidents", "RC": "continuity"}
CSF_CATEGORY = {"GV.RM": "risk", "GV.PO": "information-security", "GV.SC": "suppliers", "PR.AA": "access", "PR.AT": "people",
                "PR.DS": "data", "PR.PS": "change", "PR.IR": "continuity"}
CC_CLASS = {"FAU": "logging", "FCO": "logging", "FCS": "crypto", "FDP": "data", "FIA": "access", "FMT": "access", "FPR": "data",
            "FPT": "information-security", "FRU": "continuity", "FTA": "access", "FTP": "crypto"}


def sha(path: Path) -> str:
    return hashlib.sha256(path.read_bytes()).hexdigest()


def prop(x: dict, name: str, cls: str | None = None) -> str | None:
    return next((p["value"] for p in x.get("props", []) if p["name"] == name and p.get("class") == cls), None)


def withdrawn(x: dict) -> bool:
    return any(p["name"] == "status" and p["value"] in ("withdrawn", "Withdrawn") for p in x.get("props", []))


def q(s: str) -> str:
    """A YAML double-quoted scalar (JSON's), its letters as they are."""
    return json.dumps(s, ensure_ascii=False)


def catalog(key: str, title: str, files: list[str], src: Path, licence: str, clauses: list[list[str]], **more) -> str:
    out = [f"# Written by tools/grc-catalogs.py from the source below: run it again, do not edit. {len(clauses)} clauses.",
           f"title: {q(title)}",
           "source:"]
    out += [f"  - {{ url: {q(SOURCES[f])}, sha256: {sha(src / f)} }}" for f in files]
    out.append(f"licence: {q(licence)}")
    for name, value in more.items():
        out.append(f"{name}:")
        out += [f"  {k}: [{', '.join(q(v) for v in vs)}]" for k, vs in value.items()]
    out.append("clauses:")
    out += [f"  - [{q(i)}, {q(t)}, {d}]" for i, t, d in clauses]
    return "\n".join(out) + "\n"


def nist_800_53(src: Path) -> str:
    c = json.loads((src / "800-53r5.json").read_text())["catalog"]
    rows, ids = [], {}

    def walk(controls, family):
        for x in controls:
            if not withdrawn(x):
                label = prop(x, "label") or x["id"].upper()
                rows.append([label, x["title"], FAMILY_53[family]])
                ids[x["id"]] = label
            walk(x.get("controls", []), family)

    for g in c["groups"]:
        walk(g.get("controls", []), g["id"])
    baselines = {}
    for name in ("LOW", "MODERATE", "HIGH", "PRIVACY"):
        p = json.loads((src / f"800-53r5-{name}.json").read_text())["profile"]
        picked = [i for imp in p["imports"] for inc in imp.get("include-controls", []) for i in inc.get("with-ids", [])]
        baselines[name.lower()] = [ids[i] for i in picked if i in ids]
    version = c["metadata"].get("version", "")
    return catalog("nist80053", f"NIST SP 800-53 Rev. 5 ({version}), with the low, moderate, high and privacy baselines",
                   ["800-53r5.json", "800-53r5-LOW.json", "800-53r5-MODERATE.json", "800-53r5-HIGH.json", "800-53r5-PRIVACY.json"],
                   src, NIST, rows, baselines=baselines)


def nist_800_171(src: Path) -> str:
    c = json.loads((src / "800-171r3.json").read_text())["catalog"]
    rows = []
    for g in c["groups"]:
        family = g["id"].rsplit(".", 1)[1]
        for x in g.get("controls", []):
            if not withdrawn(x):
                rows.append([prop(x, "sort-id") or x["id"], x["title"], FAMILY_171[family]])
    return catalog("nist800171", "NIST SP 800-171 Rev. 3, protecting controlled unclassified information (CMMC builds on it)",
                   ["800-171r3.json"], src, NIST, rows)


def nist_csf(src: Path) -> str:
    c = json.loads((src / "csf2.json").read_text())["catalog"]
    rows = []
    for fn in c["groups"]:
        for cat in fn.get("controls", []):
            if withdrawn(cat):
                continue
            doc = CSF_CATEGORY.get(cat["id"], CSF_FUNCTION[fn["id"]])
            rows.append([cat["id"], cat["title"], doc])
            for sub in cat.get("controls", []):
                if withdrawn(sub):
                    continue
                statement = next((p["prose"] for p in sub.get("parts", []) if p["name"] == "statement"), sub["title"])
                rows.append([sub["id"], " ".join(statement.split()), doc])
    return catalog("nistcsf", "NIST Cybersecurity Framework 2.0: its categories and subcategories", ["csf2.json"], src, NIST, rows)


def ai_rmf(src: Path) -> str:
    rows = []
    for x in json.loads((src / "airmf-playbook.json").read_text()):
        rows.append([x["title"].strip(), " ".join(x["description"].split()), "ai"])
    order = {"GOVERN": 0, "MAP": 1, "MEASURE": 2, "MANAGE": 3}
    rows.sort(key=lambda r: (order[r[0].split()[0]], [int(n) for n in r[0].split()[1].split(".")]))
    return catalog("airmf", "NIST AI Risk Management Framework 1.0 (AI 100-1): Govern, Map, Measure and Manage, as its Playbook lists them",
                   ["airmf-playbook.json"], src, NIST, rows)


def ai_act(src: Path) -> str:
    t = (src / "ai-act.xhtml").read_text(encoding="utf-8")

    def text(s: str) -> str:
        return " ".join(html.unescape(re.sub(r"<[^>]+>", " ", s)).replace("\xa0", " ").split()).rstrip("`")

    rows = []
    for num, title in re.findall(r'class="oj-ti-art"[^>]*>(.*?)</p>\s*(?:<div[^>]*>\s*)?<p[^>]*class="oj-sti-art"[^>]*>(.*?)</p>', t, re.S):
        n = int(text(num).split()[-1])
        rows.append([f"Art.{n}", text(title), "incidents" if n == 73 else "ai"])
    for num, title in re.findall(r'<p[^>]*class="oj-doc-ti"[^>]*>\s*(ANNEX[^<]*)</p>\s*<p[^>]*class="oj-doc-ti"[^>]*>(.*?)</p>', t, re.S):
        rows.append([f"Annex {text(num).split()[-1]}", text(title), "ai"])
    if len([r for r in rows if r[0].startswith("Art.")]) != 113 or len([r for r in rows if r[0].startswith("Annex")]) != 13:
        sys.exit("grc-catalogs: the AI Act's text did not give 113 articles and 13 annexes")
    return catalog("euaiact", "EU Artificial Intelligence Act, Regulation (EU) 2024/1689: its articles and annexes", ["ai-act.xhtml"], src,
                   "© European Union, https://eur-lex.europa.eu: reuse authorised, the source acknowledged (Decision 2011/833/EU)", rows)


def common_criteria(src: Path) -> str:
    rows = []
    for f, pre in (("CC2022PART2R1.pdf", "F"), ("CC2022PART3R1.pdf", "A")):
        t = subprocess.run(["pdftotext", "-layout", str(src / f), "-"], capture_output=True, text=True, check=True).stdout
        seen = set()
        for m in re.finditer(r"^\s*\d+\.\d+\s+([A-Z][^\n(]*?)\s*\((" + pre + r"[A-Z]{2}_[A-Z]{3})\)", t, re.M):
            fam = m.group(2)
            if fam not in seen:
                seen.add(fam)
                rows.append([fam, " ".join(m.group(1).split()), CC_CLASS.get(fam[:3], "evaluation")])
    if len(rows) < 100:
        sys.exit("grc-catalogs: the Common Criteria's parts gave fewer families than they have")
    return catalog("commoncriteria", "Common Criteria CC:2022 (ISO/IEC 15408): the functional (Part 2) and assurance (Part 3) families",
                   ["CC2022PART2R1.pdf", "CC2022PART3R1.pdf"], src,
                   "The CCRA's: the family identifiers and names only are taken; the criteria are at commoncriteriaportal.org", rows)


# Each text's page: where a reader finds it, and the attribution its licence asks for.
TEXT_URL = {"nist80053": "https://doi.org/10.6028/NIST.SP.800-53r5", "nist800171": "https://doi.org/10.6028/NIST.SP.800-171r3",
            "nistcsf": "https://doi.org/10.6028/NIST.CSWP.29", "airmf": "https://doi.org/10.6028/NIST.AI.100-1",
            "euaiact": "https://eur-lex.europa.eu/legal-content/EN/TXT/HTML/?uri=OJ:L_202401689"}
TEXT_TITLE = {"nist80053": "NIST SP 800-53 Rev. 5", "nist800171": "NIST SP 800-171 Rev. 3", "nistcsf": "NIST CSF 2.0", "airmf": "NIST AI RMF 1.0",
              "euaiact": "EU AI Act"}
TEXT_FROM = {"nist80053": ["800-53r5.json", "800-53r5-LOW.json", "800-53r5-MODERATE.json", "800-53r5-HIGH.json", "800-53r5-PRIVACY.json"],
             "nist800171": ["800-171r3.json"], "nistcsf": ["csf2.json"], "airmf": ["airmf-playbook.json"], "euaiact": ["ai-act.xhtml"]}
INSERT = re.compile(r"\{\{\s*insert:\s*param,\s*([^\s}]+)\s*\}\}")


def oscal_params(controls) -> dict:
    """Every parameter of a catalog by its id, for the statements' {{ insert: param, ... }}."""
    out = {}
    for x in controls:
        for prm in x.get("params", []):
            out[prm["id"]] = prm
        out.update(oscal_params(x.get("controls", [])))
    return out


def oscal_prose(s: str, params: dict) -> str:
    """A statement's prose with its parameters as the publication prints them: [Assignment: ...] or [Selection: ...]."""
    def one(m):
        prm = params.get(m.group(1))
        if prm is None:
            return "[Assignment: organization-defined value]"
        if "select" in prm:
            how = " (one or more)" if prm["select"].get("how-many") == "one-or-more" else ""
            return f"[Selection{how}: " + "; ".join(oscal_prose(c, params) for c in prm["select"].get("choice", [])) + "]"
        return f"[Assignment: {prm.get('usage') or 'organization-defined ' + prm.get('label', 'value')}]"
    return INSERT.sub(one, s)


def oscal_items(part: dict, params: dict, depth: int = 0) -> list[str]:
    """A statement part and its items, each on its own line, indented by its depth (800-171's labels, "SR-03.01.01.c.01", as
    its publication prints them: "c." and "1.")."""
    label = prop(part, "label")
    if label and label.startswith("SR-"):
        last = label.rsplit(".", 1)[-1]
        label = f"{int(last)}." if last.isdigit() else f"{last}."
    prose = " ".join(oscal_prose(part.get("prose", ""), params).split())
    lines = [("  " * (depth - 1)) + (f"{label} " if label else "") + prose] if prose and depth else ([prose] if prose else [])
    for sub in part.get("parts", []):
        if sub["name"] == "item":
            lines += oscal_items(sub, params, depth + 1)
    return lines


def part_of(x: dict, name: str) -> dict | None:
    return next((p for p in x.get("parts", []) if p["name"] == name), None)


def text_800_53(src: Path, out: Path) -> list[dict]:
    c = json.loads((src / "800-53r5.json").read_text())["catalog"]
    params, labels, baselines = {}, {}, {}
    for g in c["groups"]:
        params.update(oscal_params(g.get("controls", [])))

    def label_all(controls):
        for x in controls:
            labels[x["id"]] = prop(x, "label") or x["id"].upper()
            label_all(x.get("controls", []))
    for g in c["groups"]:
        label_all(g.get("controls", []))
    for name in ("LOW", "MODERATE", "HIGH", "PRIVACY"):
        profile = json.loads((src / f"800-53r5-{name}.json").read_text())["profile"]
        for imp in profile["imports"]:
            for inc in imp.get("include-controls", []):
                for i in inc.get("with-ids", []):
                    baselines.setdefault(i, []).append(name.lower())
    docs = []

    def walk(controls, group, parent):
        for x in controls:
            if not withdrawn(x):
                label = labels[x["id"]]
                title = f"{parent}: {x['title']}" if parent else x["title"]
                body = [f"# {label} {title}", "",
                        f"NIST SP 800-53 Rev. 5, the {group['title']} family. Baselines: {', '.join(baselines.get(x['id'], [])) or 'none'}.", "",
                        "## Control", ""]
                body += oscal_items(part_of(x, "statement") or {"prose": x["title"]}, params)
                guidance = part_of(x, "guidance")
                if guidance and guidance.get("prose"):
                    body += ["", "## Discussion", "", " ".join(oscal_prose(guidance["prose"], params).split())]
                related = [labels[link["href"][1:]] for link in x.get("links", []) if link.get("rel") == "related" and link["href"][1:] in labels]
                if related:
                    body += ["", "## Related controls", "", ", ".join(related)]
                docs.append(write_text(out, "nist80053", f"{group['id']}/{x['id']}.md", label, title, "control", "\n".join(body)))
            walk(x.get("controls", []), group, x["title"] if not parent else parent)
    for g in c["groups"]:
        walk(g.get("controls", []), g, "")
    return docs


def text_800_171(src: Path, out: Path) -> list[dict]:
    c = json.loads((src / "800-171r3.json").read_text())["catalog"]
    params = {}
    for g in c["groups"]:
        params.update(oscal_params(g.get("controls", [])))
    docs = []
    for g in c["groups"]:
        for x in g.get("controls", []):
            if withdrawn(x):
                continue
            rid = prop(x, "sort-id") or x["id"]
            body = [f"# {rid} {x['title']}", "", f"NIST SP 800-171 Rev. 3, the {g['title']} family.", "", "## Requirement", ""]
            body += oscal_items(part_of(x, "statement") or {"prose": x["title"]}, params)
            guidance = part_of(x, "guidance")
            if guidance and guidance.get("prose"):
                body += ["", "## Discussion", "", " ".join(oscal_prose(guidance["prose"], params).split())]
            docs.append(write_text(out, "nist800171", f"{rid}.md", rid, x["title"], "requirement", "\n".join(body)))
    return docs


def text_csf(src: Path, out: Path) -> list[dict]:
    c = json.loads((src / "csf2.json").read_text())["catalog"]
    docs = []
    for fn in c["groups"]:
        for cat in fn.get("controls", []):
            if withdrawn(cat):
                continue
            live = [s for s in cat.get("controls", []) if not withdrawn(s)]
            statement = " ".join(((part_of(cat, "statement") or {}).get("prose") or cat["title"]).split())
            body = [f"# {cat['id']} {cat['title']}", "", f"NIST Cybersecurity Framework 2.0, the {fn['title'].title()} function.", "", statement, "",
                    "## Subcategories", ""] + [f"- {s['id']}: {' '.join(((part_of(s, 'statement') or {}).get('prose') or '').split())}" for s in live]
            docs.append(write_text(out, "nistcsf", f"{fn['id']}/{cat['id']}.md", cat["id"], cat["title"], "category", "\n".join(body)))
            for s in live:
                says = " ".join(((part_of(s, "statement") or {}).get("prose") or s["title"]).split())
                examples = [" ".join(p["prose"].split()) for p in s.get("parts", []) if p["name"] == "guidance" and p.get("prose")]
                body = [f"# {s['id']}", "", f"NIST Cybersecurity Framework 2.0, {cat['id']} {cat['title']} ({fn['title'].title()}).", "", says]
                if examples:
                    body += ["", "## Implementation examples", ""] + [f"- {e}" for e in examples]
                docs.append(write_text(out, "nistcsf", f"{fn['id']}/{s['id']}.md", s["id"], says, "subcategory", "\n".join(body)))
    return docs


def text_ai_rmf(src: Path, out: Path) -> list[dict]:
    docs = []
    for x in json.loads((src / "airmf-playbook.json").read_text()):
        rid = x["title"].strip()
        says = " ".join(x["description"].split())
        body = [f"# {rid}", "", f"NIST AI Risk Management Framework 1.0, the {x['type']} function, with its Playbook.", "", says]
        for key, heading in (("section_about", "About"), ("section_actions", "Suggested actions"),
                             ("section_doc", "Transparency and documentation"), ("section_ref", "References")):
            if (x.get(key) or "").strip():
                body += ["", f"## {heading}", "", re.sub(r"^#{1,6}\s+", "", x[key].strip(), flags=re.M)]
        docs.append(write_text(out, "airmf", f"{x['type'].lower()}/{rid.replace(' ', '-')}.md", rid, says, "subcategory", "\n".join(body)))
    return docs


def text_ai_act(src: Path, out: Path) -> list[dict]:
    t = (src / "ai-act.xhtml").read_text(encoding="utf-8")
    starts = sorted((m.start(), m.group(1), m.group(2)) for m in re.finditer(r'id="(art|anx)_([0-9]+|[IVX]+)"', t))
    stops = sorted(m.start() for m in re.finditer(r'id="(?:art|anx|cpt)_[^".]+"', t))

    def lines(s: str) -> list[str]:
        s = " ".join(s.split())
        # A point's label is a cell of its own, "(a)" beside its text: it leads the text's line instead.
        s = re.sub(r"<td[^>]*>\s*<p[^>]*>\s*([^<]{1,8}?)\s*</p>\s*</td>", r"\1 ", s)
        s = re.sub(r"</(?:p|tr|div)>|<br\s*/?>", "\n", s)
        s = html.unescape(re.sub(r"<[^>]+>", " ", s)).replace("\xa0", " ")
        return [" ".join(x.split()) for x in s.split("\n") if x.strip()]

    docs = []
    for at, kind, num in starts:
        stop = next((s for s in stops if s > at), None)
        end = t.rfind("<", at, stop) if stop else len(t)
        got = lines(t[t.index(">", at) + 1:end])
        if len(got) < 2:
            continue
        head, title, text = got[0], got[1], got[2:]
        rid = f"Art.{num}" if kind == "art" else f"Annex {num}"
        body = [f"# {head}: {title}", "", "EU Artificial Intelligence Act, Regulation (EU) 2024/1689.", ""] + "\n\n".join(text).split("\n")
        docs.append(write_text(out, "euaiact", f"{kind}_{num}.md", rid, title, "article" if kind == "art" else "annex", "\n".join(body),
                               f"{TEXT_URL['euaiact']}#{kind}_{num}"))
    return docs


def write_text(out: Path, framework: str, path: str, rid: str, title: str, kind: str, body: str, url: str | None = None) -> dict:
    target = out / framework / path
    target.parent.mkdir(parents=True, exist_ok=True)
    target.write_text(body.rstrip() + "\n", encoding="utf-8")
    name = TEXT_TITLE[framework]
    return {"path": f"{framework}/{path}", "title": f"{rid} {title}", "url": url or TEXT_URL[framework],
            "symbols": [{"name": rid, "kind": kind, "namespace": name, "signature": f"{name} {rid}: {title}"}]}


TEXTS = {"nist80053": text_800_53, "nist800171": text_800_171, "nistcsf": text_csf, "airmf": text_ai_rmf, "euaiact": text_ai_act}


def texts(src: Path, out: Path) -> int:
    """The shareable texts as the grc pack's work directory; each framework's pages must be as many as its catalog's clauses."""
    out.mkdir(parents=True, exist_ok=True)
    docs, provenance = [], []
    for key, write in TEXTS.items():
        got = write(src, out)
        want = (OUT / f"{key}.yaml").read_text().count("\n  - [")
        if len(got) != want:
            sys.exit(f"grc-catalogs: {key} gave {len(got)} texts for its {want} clauses")
        docs += got
        provenance.append(f"{key}=" + "+".join(sha(src / f)[:12] for f in TEXT_FROM[key]))
        print(f"grc-catalogs: {key}: {len(got)} texts")
    (out / "index.json").write_text(json.dumps({"provenance": ",".join(provenance), "docs": docs}, ensure_ascii=False, indent=1) + "\n", encoding="utf-8")
    return 0


WRITERS = {"nist80053": nist_800_53, "nist800171": nist_800_171, "nistcsf": nist_csf, "airmf": ai_rmf, "euaiact": ai_act,
           "commoncriteria": common_criteria}


def main(argv: list[str] | None = None) -> int:
    p = argparse.ArgumentParser(description=__doc__.split("\n\n")[0])
    p.add_argument("--from", dest="src", required=True, type=Path, help="the folder of the downloaded sources")
    p.add_argument("--texts", type=Path, help="write the shareable texts here (the grc pack's work directory) instead of the catalogs")
    a = p.parse_args(argv)
    missing = [f for f in SOURCES if not (a.src / f).is_file()]
    if missing:
        print("grc-catalogs: not in " + str(a.src) + ": " + ", ".join(missing) + "\n  " +
              "\n  ".join(f"{f}: {SOURCES[f]}" for f in missing), file=sys.stderr)
        return 1
    if a.texts:
        return texts(a.src, a.texts)
    OUT.mkdir(parents=True, exist_ok=True)
    for key, write in WRITERS.items():
        text = write(a.src)
        (OUT / f"{key}.yaml").write_text(text)
        print(f"grc-catalogs: {key}: {text.count(chr(10) + '  - [')} clauses")
    return 0


if __name__ == "__main__":
    sys.exit(main())
