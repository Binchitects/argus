#!/usr/bin/env python3
"""The security knowledge pack's pages, written from the sources tools/fetch-security-sources.sh fetched.

    python3 tools/security-texts.py --from .packwork/docsrc/security --out .packwork/docsrc/security-texts

A page per weakness (CWE), attack pattern (CAPEC), OWASP cheat sheet, ASVS 5.0 section, WSTG test, Top 10 risk (web
2025 and 2021, API 2023, LLM applications 2025), CodeQL security query (its help, never its engine), and analyzer rule
(.NET's security rules, Bandit's checks, gosec's rules, eslint-plugin-security's rules), each saying whose it is and
under what licence. Each names its ids as symbols: CWE-89, CAPEC-66, V6.2.1, WSTG-INPV-05, A05:2025, API1:2023,
LLM01:2025, cs/sql-injection, CA2100, B608, G201, security/detect-object-injection. OUT/index.json lists the pages and
their symbols, and the sources' versions and commits as the pack's provenance: the work directory of Argus's
`security` pack (argus pack build --source security --work-dir OUT).
"""
from __future__ import annotations

import argparse
import ast
import html.entities
import json
import re
import subprocess
import sys
import xml.etree.ElementTree as ET
from pathlib import Path

CWE_NS = "{http://cwe.mitre.org/cwe-7}"
CAPEC_NS = "{http://capec.mitre.org/capec-3}"
TOP25 = "1435"  # the 2025 CWE Top 25's view
GITHUB = {"cheatsheets": "OWASP/CheatSheetSeries", "asvs": "OWASP/ASVS", "wstg": "OWASP/wstg", "top10": "OWASP/Top10",
          "api-top10": "OWASP/API-Security", "llm-top10": "OWASP/www-project-top-10-for-large-language-model-applications",
          "codeql": "github/codeql", "dotnet-rules": "dotnet/docs", "bandit": "PyCQA/bandit", "gosec": "securego/gosec",
          "eslint-security": "eslint-community/eslint-plugin-security"}
SOURCE = {
    "cwe": "MITRE's CWE (Common Weakness Enumeration), used under MITRE's terms of use",
    "capec": "MITRE's CAPEC (Common Attack Pattern Enumeration and Classification), used under MITRE's terms of use",
    "cheatsheets": "the OWASP Cheat Sheet Series, CC BY-SA 4.0",
    "asvs": "the OWASP Application Security Verification Standard 5.0, CC BY-SA 4.0",
    "wstg": "the OWASP Web Security Testing Guide, CC BY-SA 4.0",
    "top10": "the OWASP Top 10, CC BY-SA 4.0",
    "api-top10": "the OWASP API Security Top 10 2023, CC BY-SA 4.0",
    "llm-top10": "the OWASP Top 10 for LLM Applications 2025, CC BY-SA 4.0",
    "codeql": "CodeQL's query help (github/codeql), MIT",
    "dotnet-rules": ".NET's code analysis documentation (dotnet/docs), CC BY 4.0",
    "bandit": "Bandit's documentation (PyCQA/bandit), Apache-2.0",
    "gosec": "gosec's rules (securego/gosec), Apache-2.0",
    "eslint-security": "eslint-plugin-security's rules (eslint-community), Apache-2.0",
}
CODEQL_LANGUAGES = {"actions": "GitHub Actions", "cpp": "C and C++", "csharp": "C#", "go": "Go", "java": "Java and Kotlin",
                    "javascript": "JavaScript and TypeScript", "python": "Python", "ruby": "Ruby", "rust": "Rust", "swift": "Swift"}
EXTENSIONS = {".cs": "csharp", ".java": "java", ".kt": "kotlin", ".js": "javascript", ".ts": "typescript", ".py": "python", ".rb": "ruby",
              ".go": "go", ".c": "c", ".cpp": "cpp", ".h": "c", ".rs": "rust", ".swift": "swift", ".yml": "yaml", ".yaml": "yaml",
              ".html": "html", ".xml": "xml", ".erb": "erb", ".jsx": "jsx", ".vue": "vue", ".json": "json", ".sql": "sql"}


class Pages:
    """The pages written, and what index.json lists of them."""

    def __init__(self, out: Path):
        self.out, self.docs = out, []

    def add(self, part: str, path: str, title: str, url: str, body: str, symbols: list[dict]):
        target = self.out / part / path
        target.parent.mkdir(parents=True, exist_ok=True)
        target.write_text(body.rstrip() + f"\n\n---\nSource: {SOURCE[part]}. {url}\n", encoding="utf-8")
        self.docs.append({"path": f"{part}/{path}", "title": title, "url": url, "symbols": symbols})


def symbol(name: str, kind: str, namespace: str, signature: str) -> dict:
    return {"name": name, "kind": kind, "namespace": namespace, "signature": " ".join(signature.split())[:300]}


def oneline(s: str | None) -> str:
    return " ".join((s or "").split())


def commit(part_dir: Path) -> str:
    return subprocess.run(["git", "-C", str(part_dir), "rev-parse", "HEAD"], capture_output=True, text=True, check=True).stdout.strip()


def blob(part: str, sha: str, path: str) -> str:
    return f"https://github.com/{GITHUB[part]}/blob/{sha}/{path}"


# MITRE's structured text: XHTML (p, div, ul, ol, li, br, b, i, table) inside their elements.

def local(tag: str) -> str:
    return tag.rsplit("}", 1)[-1]


def prose(e: ET.Element | None) -> str:
    """An element's mixed content as Markdown paragraphs and lists."""
    if e is None:
        return ""
    blocks: list[str] = []
    inline: list[str] = []

    def flush():
        text = oneline("".join(inline))
        if text:
            blocks.append(text)
        inline.clear()

    def walk(node: ET.Element, depth: int):
        if node.text:
            inline.append(node.text)
        for c in node:
            tag = local(c.tag)
            if tag in ("p", "div"):
                flush()
                walk(c, depth)
                flush()
            elif tag in ("ul", "ol"):
                flush()
                for i, li in enumerate(x for x in c if local(x.tag) == "li"):
                    text = oneline(" ".join(li.itertext()))
                    if text:
                        blocks.append(("  " * depth) + (f"{i + 1}. " if tag == "ol" else "- ") + text)
            elif tag == "br":
                flush()
            elif tag == "table":
                flush()
                rows = [[oneline(" ".join(cell.itertext())) for cell in tr if local(cell.tag) in ("td", "th")] for tr in c.iter() if local(tr.tag) == "tr"]
                rows = [r for r in rows if any(r)]
                if rows:
                    width = max(len(r) for r in rows)
                    rows = [r + [""] * (width - len(r)) for r in rows]
                    blocks.append("\n".join([f"| {' | '.join(rows[0])} |", "|" + " --- |" * width] + [f"| {' | '.join(r)} |" for r in rows[1:]]))
            elif tag in ("b", "strong"):
                inline.append(f"**{oneline(' '.join(c.itertext()))}**")
            elif tag in ("i", "em"):
                inline.append(f"*{oneline(' '.join(c.itertext()))}*")
            else:
                walk(c, depth)
            if c.tail:
                inline.append(c.tail)

    walk(e, 0)
    flush()
    return "\n\n".join(blocks)


def code(e: ET.Element) -> str:
    """An example's code: its br are lines, its indented divs indentation."""
    out: list[str] = []

    def walk(node: ET.Element, indent: int):
        if node.text:
            out.append(node.text)
        for c in node:
            tag = local(c.tag)
            if tag == "br":
                out.append("\n" + "    " * indent)
            elif tag == "div":
                deeper = indent + (1 if "margin-left" in (c.get("style") or "") else 0)
                out.append("\n" + "    " * deeper)
                walk(c, deeper)
                out.append("\n" + "    " * indent)
            else:
                walk(c, indent)
            if c.tail:
                out.append(c.tail)

    walk(e, 0)
    lines = [line.rstrip() for line in "".join(out).replace("\t", "    ").split("\n")]
    while lines and not lines[0].strip():
        lines.pop(0)
    while lines and not lines[-1].strip():
        lines.pop()
    collapsed = []
    for line in lines:
        if line.strip() or (collapsed and collapsed[-1].strip()):
            collapsed.append(line)
    return "\n".join(collapsed)


def cwe(src: Path, pages: Pages) -> str:
    root = ET.parse(src / "mitre" / "cwec.xml").getroot()
    n = CWE_NS
    top25 = {m.get("CWE_ID") for v in root.iter(n + "View") if v.get("ID") == TOP25 for m in v.iter(n + "Has_Member")}
    count = 0
    for w in root.iter(n + "Weakness"):
        if w.get("Status") == "Deprecated":
            continue
        wid, name = w.get("ID"), w.get("Name")
        platforms = sorted({x.get("Name") or x.get("Class") for x in w.iter(n + "Language")} - {None})
        technologies = sorted({x.get("Name") or x.get("Class") for x in w.iter(n + "Technology")} - {None})
        head = [f"# CWE-{wid}: {name}", "",
                f"A weakness of MITRE's CWE {root.get('Version')}: {w.get('Abstraction')} abstraction, {w.get('Status')}."
                + (" In the 2025 CWE Top 25 Most Dangerous Software Weaknesses." if wid in top25 else "")
                + (f" Languages: {', '.join(platforms)}." if platforms else "") + (f" Technologies: {', '.join(technologies)}." if technologies else "")]
        body = head + ["", prose(w.find(n + "Description"))]
        if (ext := prose(w.find(n + "Extended_Description"))):
            body += ["", ext]
        terms = [(oneline(t.findtext(n + "Term")), prose(t.find(n + "Description"))) for t in w.iter(n + "Alternate_Term")]
        if terms:
            body += ["", "## Alternate terms", ""] + [f"- **{t}**" + (f": {d}" if d else "") for t, d in terms]
        if (likely := w.findtext(n + "Likelihood_Of_Exploit")):
            body += ["", f"Likelihood of exploit: {likely}."]
        consequences = []
        for c in w.iter(n + "Consequence"):
            scopes = ", ".join(x.text for x in c.findall(n + "Scope"))
            impacts = ", ".join(x.text for x in c.findall(n + "Impact"))
            note = prose(c.find(n + "Note"))
            consequences.append(f"- **{scopes}**: {impacts}." + (f" {note}" if note else ""))
        if consequences:
            body += ["", "## Common consequences", ""] + consequences
        introduced = [f"- **{oneline(m.findtext(n + 'Phase'))}**" + (f": {prose(m.find(n + 'Note'))}" if m.find(n + "Note") is not None else "")
                      for m in w.iter(n + "Introduction")]
        if introduced:
            body += ["", "## Modes of introduction", ""] + introduced
        detection = []
        for d in w.iter(n + "Detection_Method"):
            effect = d.findtext(n + "Effectiveness")
            detection += [f"### {oneline(d.findtext(n + 'Method'))}" + (f" (effectiveness: {effect})" if effect else ""), "", prose(d.find(n + "Description")), ""]
        if detection:
            body += ["", "## Detection methods", ""] + detection
        mitigations = []
        for m in w.iter(n + "Mitigation"):
            phases = ", ".join(x.text for x in m.findall(n + "Phase"))
            strategy = m.findtext(n + "Strategy")
            effect = m.findtext(n + "Effectiveness")
            mitigations += [f"### {phases or 'Any phase'}" + (f": {strategy}" if strategy else ""), "", prose(m.find(n + "Description"))]
            if effect:
                mitigations += ["", f"Effectiveness: {effect}." + (f" {prose(m.find(n + 'Effectiveness_Notes'))}" if m.find(n + "Effectiveness_Notes") is not None else "")]
            mitigations.append("")
        if mitigations:
            body += ["", "## Potential mitigations", ""] + mitigations
        examples = list(w.iter(n + "Demonstrative_Example"))
        if examples:
            body += ["", "## Demonstrative examples"]
            for i, x in enumerate(examples, 1):
                body += ["", f"### Example {i}"]
                for part in x:
                    tag = local(part.tag)
                    if tag in ("Intro_Text", "Body_Text"):
                        body += ["", prose(part)]
                    elif tag == "Example_Code":
                        nature, language = part.get("Nature", ""), part.get("Language", "")
                        body += ["", f"{nature} code" + (f" ({language})" if language else "") + ":", "",
                                 f"```{language.lower().replace('#', 'sharp').replace('+', 'p') if language else ''}", code(part), "```"]
        observed = [f"- {oneline(o.findtext(n + 'Reference'))}: {prose(o.find(n + 'Description'))}" for o in w.iter(n + "Observed_Example")]
        if observed:
            body += ["", "## Observed examples", ""] + observed
        related = [f"{r.get('Nature')} CWE-{r.get('CWE_ID')}" for r in w.iter(n + "Related_Weakness") if r.get("View_ID") == "1000"]
        if related:
            body += ["", "## Related weaknesses", "", "; ".join(related) + "."]
        patterns = [f"CAPEC-{r.get('CAPEC_ID')}" for r in w.iter(n + "Related_Attack_Pattern")]
        if patterns:
            body += ["", "## Related attack patterns", "", ", ".join(patterns) + "."]
        mappings = [f"- {m.get('Taxonomy_Name')}: {oneline(m.findtext(n + 'Entry_ID') or '')} {oneline(m.findtext(n + 'Entry_Name') or '')}".rstrip()
                    for m in w.iter(n + "Taxonomy_Mapping") if m.get("Taxonomy_Name") in ("OWASP Top Ten 2021", "OWASP Top Ten 2004", "SEI CERT Oracle Coding Standard for Java", "Software Fault Patterns")]
        if mappings:
            body += ["", "## Taxonomy mappings", ""] + mappings
        url = f"https://cwe.mitre.org/data/definitions/{wid}.html"
        pages.add("cwe", f"{'top25' if wid in top25 else 'all'}/CWE-{wid}.md", f"CWE-{wid}: {name}", url, "\n".join(body),
                  [symbol(f"CWE-{wid}", "weakness", "CWE", f"CWE-{wid}: {name} ({w.get('Abstraction')})" + (" — 2025 Top 25" if wid in top25 else ""))])
        count += 1
    print(f"security-texts: cwe: {count} weaknesses, {len(top25)} in the Top 25")
    return f"cwe={root.get('Version')}"


def capec(src: Path, pages: Pages) -> str:
    root = ET.parse(src / "mitre" / "capec_latest.xml").getroot()
    n = CAPEC_NS
    count = 0
    for a in root.iter(n + "Attack_Pattern"):
        if a.get("Status") == "Deprecated":
            continue
        aid, name = a.get("ID"), a.get("Name")
        body = [f"# CAPEC-{aid}: {name}", "",
                f"An attack pattern of MITRE's CAPEC {root.get('Version')}: {a.get('Abstraction')} abstraction, {a.get('Status')}."
                + (f" Likelihood of attack: {a.findtext(n + 'Likelihood_Of_Attack')}." if a.findtext(n + "Likelihood_Of_Attack") else "")
                + (f" Typical severity: {a.findtext(n + 'Typical_Severity')}." if a.findtext(n + "Typical_Severity") else ""),
                "", prose(a.find(n + "Description"))]
        if (ext := prose(a.find(n + "Extended_Description"))):
            body += ["", ext]
        steps = []
        for s in a.iter(n + "Attack_Step"):
            steps += [f"{s.findtext(n + 'Step')}. **{s.findtext(n + 'Phase')}**: {prose(s.find(n + 'Description'))}"]
            steps += [f"   - {prose(t)}" for t in s.findall(n + "Technique")]
        if steps:
            body += ["", "## Execution flow", ""] + steps
        for section, heading in (("Prerequisite", "Prerequisites"), ("Indicator", "Indicators"), ("Resource", "Resources required")):
            items = [f"- {prose(x)}" for x in a.iter(n + section)]
            if items:
                body += ["", f"## {heading}", ""] + items
        skills = [f"- **{s.get('Level')}**: {prose(s)}" for s in a.iter(n + "Skill")]
        if skills:
            body += ["", "## Skills required", ""] + skills
        consequences = [f"- **{', '.join(x.text for x in c.findall(n + 'Scope'))}**: {', '.join(x.text for x in c.findall(n + 'Impact'))}."
                        + (f" {prose(c.find(n + 'Note'))}" if c.find(n + "Note") is not None else "") for c in a.iter(n + "Consequence")]
        if consequences:
            body += ["", "## Consequences", ""] + consequences
        mitigations = [f"- {prose(m)}" for m in a.iter(n + "Mitigation")]
        if mitigations:
            body += ["", "## Mitigations", ""] + mitigations
        examples = [prose(x) for x in a.iter(n + "Example")]
        if examples:
            body += ["", "## Example instances", ""] + [x + "\n" for x in examples]
        weaknesses = [f"CWE-{r.get('CWE_ID')}" for r in a.iter(n + "Related_Weakness")]
        if weaknesses:
            body += ["", "## Related weaknesses", "", ", ".join(weaknesses) + "."]
        related = [f"{r.get('Nature')} CAPEC-{r.get('CAPEC_ID')}" for r in a.iter(n + "Related_Attack_Pattern")]
        if related:
            body += ["", "## Related attack patterns", "", "; ".join(related) + "."]
        url = f"https://capec.mitre.org/data/definitions/{aid}.html"
        pages.add("capec", f"CAPEC-{aid}.md", f"CAPEC-{aid}: {name}", url, "\n".join(body),
                  [symbol(f"CAPEC-{aid}", "attack pattern", "CAPEC", f"CAPEC-{aid}: {name}" + (f" (uses {', '.join(weaknesses[:4])})" if weaknesses else ""))])
        count += 1
    print(f"security-texts: capec: {count} attack patterns")
    return f"capec={root.get('Version')}"


def heading(text: str, fallback: str) -> str:
    """The page's first heading, without its pictures and attribute lists."""
    m = re.search(r"^#{1,2}\s+(.+?)\s*#*\s*$", text, re.M)
    if not m:
        return fallback
    return oneline(re.sub(r"!\[[^\]]*\]\([^)]*\)|\{:[^}]*\}", "", m.group(1))) or fallback


def cheatsheets(src: Path, pages: Pages) -> str:
    part = src / "cheatsheets"
    sha = commit(part)
    files = sorted((part / "cheatsheets").glob("*.md"))
    for f in files:
        text = f.read_text(encoding="utf-8")
        title = heading(text, f.stem.replace("_", " "))
        pages.add("cheatsheets", f.name, title, f"https://cheatsheetseries.owasp.org/cheatsheets/{f.stem}.html", text,
                  [symbol(title, "cheat sheet", "OWASP Cheat Sheet Series", f"OWASP cheat sheet: {title}")])
    print(f"security-texts: cheatsheets: {len(files)}")
    return f"cheatsheets={sha[:12]}"


def asvs(src: Path, pages: Pages) -> str:
    part = src / "asvs"
    sha = commit(part)
    count = 0
    for f in sorted((part / "5.0" / "en").glob("0x*.md")):
        text = f.read_text(encoding="utf-8")
        rel = f"5.0/en/{f.name}"
        chapter = re.match(r"0x\d+-(V\d+)-", f.name)
        if not chapter:
            title = heading(text, f.stem)
            pages.add("asvs", f.name, f"ASVS 5.0: {title}", blob("asvs", sha, rel), text, [])
            continue
        title = heading(text, f.stem)
        sections = re.split(r"^(?=## V\d+\.\d+ )", text, flags=re.M)
        intro = sections[0]
        pages.add("asvs", f"{chapter.group(1)}/index.md", f"ASVS 5.0 {title}", blob("asvs", sha, rel), intro,
                  [symbol(chapter.group(1), "chapter", "OWASP ASVS 5.0", f"ASVS 5.0 {title}")])
        for section in sections[1:]:
            sid, sname = re.match(r"## (V\d+\.\d+) (.+)", section).groups()
            symbols = [symbol(sid, "section", "OWASP ASVS 5.0", f"ASVS 5.0 {sid} {sname}")]
            for rid, words, level in re.findall(r"^\|\s*\*\*(\d+\.\d+\.\d+)\*\*\s*\|\s*(.+?)\s*\|\s*(\d)\s*\|\s*$", section, re.M):
                symbols.append(symbol(f"V{rid}", "requirement", "OWASP ASVS 5.0", f"ASVS 5.0 V{rid} (level {level}): {words}"))
                count += 1
            pages.add("asvs", f"{chapter.group(1)}/{sid}.md", f"ASVS 5.0 {sid} {sname}", blob("asvs", sha, rel),
                      f"# ASVS 5.0 {sid} {sname}\n\nFrom {title}. Each requirement's level (1 to 3) is in its last column.\n\n" + section.split("\n", 1)[1], symbols)
    print(f"security-texts: asvs: {count} requirements")
    return f"asvs={sha[:12]}"


def wstg(src: Path, pages: Pages) -> str:
    part = src / "wstg"
    sha = commit(part)
    count = 0
    for f in sorted((part / "document").rglob("*.md")):
        rel = f.relative_to(part / "document").as_posix()
        if f.name == "README.md" and rel.count("/") == 0:
            continue
        text = f.read_text(encoding="utf-8")
        title = heading(text, f.stem.replace("_", " "))
        ids = re.findall(r"^\|\s*(WSTG-[A-Z]{4}-\d{2})\s*\|", text, re.M)
        page = "latest/" + rel[:-3]
        symbols = [symbol(i, "test", "OWASP WSTG", f"{i}: {title}") for i in dict.fromkeys(ids)]
        count += len(symbols)
        pages.add("wstg", rel, f"WSTG {ids[0] + ': ' if ids else ''}{title}", f"https://owasp.org/www-project-web-security-testing-guide/{page}", text, symbols)
    print(f"security-texts: wstg: {count} tests")
    return f"wstg={sha[:12]}"


def top10s(src: Path, pages: Pages) -> list[str]:
    out = []
    part = src / "top10"
    sha = commit(part)
    for year in ("2025", "2021"):
        for f in sorted((part / year / "docs" / "en").glob("A[0-9][0-9]_*.md")):
            text = f.read_text(encoding="utf-8")
            rid = f"{f.name[:3]}:{year}"
            title = re.sub(rf"^{re.escape(rid)}\s*[-–:]?\s*", "", heading(text, f.stem))
            pages.add("top10", f"{year}/{f.name}", f"OWASP Top 10 {year} {rid} {title}", blob("top10", sha, f"{year}/docs/en/{f.name}"), text,
                      [symbol(rid, "risk", f"OWASP Top 10 {year}", f"OWASP Top 10 {year} {rid}: {title}")])
    out.append(f"top10={sha[:12]}")
    part = src / "api-top10"
    sha = commit(part)
    for f in sorted((part / "editions" / "2023" / "en").glob("0xa*.md")):
        text = f.read_text(encoding="utf-8")
        number = int(f.name[3], 16)
        rid = f"API{number}:2023"
        title = re.sub(rf"^{re.escape(rid)}\s*[-–:]?\s*", "", heading(text, f.stem))
        pages.add("api-top10", f.name, f"OWASP API Security Top 10 {rid} {title}", blob("api-top10", sha, f"editions/2023/en/{f.name}"), text,
                  [symbol(rid, "risk", "OWASP API Security Top 10 2023", f"{rid}: {title}")])
    out.append(f"api-top10={sha[:12]}")
    part = src / "llm-top10"
    sha = commit(part)
    for f in sorted((part / "2_0_vulns").glob("LLM[0-9][0-9]_*.md")):
        if f.name.startswith("LLM00"):  # the preface
            continue
        text = f.read_text(encoding="utf-8")
        rid = f"{f.name[:5]}:2025"
        title = re.sub(rf"^{re.escape(rid)}\s*[-–:]?\s*", "", heading(text, f.stem))
        pages.add("llm-top10", f.name, f"OWASP Top 10 for LLM Applications {rid} {title}", blob("llm-top10", sha, f"2_0_vulns/{f.name}"), text,
                  [symbol(rid, "risk", "OWASP Top 10 for LLM Applications 2025", f"{rid}: {title}")])
    out.append(f"llm-top10={sha[:12]}")
    print(f"security-texts: top 10s: {sum(1 for d in pages.docs if d['path'].split('/')[0] in ('top10', 'api-top10', 'llm-top10'))} risks")
    return out


def qhelp_markdown(path: Path, seen: set | None = None) -> str:
    """A .qhelp file as Markdown: its sections, samples included as code, references as links."""
    seen = seen or set()
    text = path.read_text(encoding="utf-8", errors="replace")
    # HTML's named entities (&ntilde;), which XML does not know, as their letters.
    text = re.sub(r"&([A-Za-z][A-Za-z0-9]*);", lambda m: m.group(0) if m.group(1) in ("lt", "gt", "amp", "quot", "apos")
                  else chr(html.entities.name2codepoint.get(m.group(1), 0x3F)), text)
    root = ET.fromstring(text)
    out: list[str] = []
    titles = {"overview": "", "recommendation": "## Recommendation", "example": "## Example", "references": "## References", "section": ""}

    def inline(e: ET.Element) -> str:
        parts = [e.text or ""]
        for c in e:
            tag = c.tag
            text = "".join(c.itertext())
            if tag == "code":
                parts.append(f"`{text.strip()}`")
            elif tag == "a":
                parts.append(f"[{oneline(text)}]({c.get('href')})")
            elif tag in ("b", "strong"):
                parts.append(f"**{oneline(text)}**")
            elif tag in ("i", "em"):
                parts.append(f"*{oneline(text)}*")
            else:
                parts.append(inline(c))
            parts.append(c.tail or "")
        return "".join(parts)

    def block(e: ET.Element):
        for c in e:
            tag = c.tag
            if tag in titles:
                if titles[tag]:
                    out.extend(["", titles[tag]])
                if c.get("title"):
                    out.extend(["", f"### {c.get('title')}"])
                block(c)
            elif tag == "p":
                out.extend(["", oneline(inline(c))])
            elif tag in ("ul", "ol"):
                out.append("")
                out.extend(("1. " if tag == "ol" else "- ") + oneline(inline(li)) for li in c if li.tag == "li")
            elif tag == "li":
                out.extend(["", "- " + oneline(inline(c))])
            elif tag == "sample":
                if c.get("src"):
                    sample = (path.parent / c.get("src"))
                    if sample.is_file():
                        out.extend(["", f"```{c.get('language') or EXTENSIONS.get(sample.suffix, '')}", sample.read_text(encoding="utf-8", errors="replace").rstrip(), "```"])
                elif c.text:
                    out.extend(["", f"```{c.get('language', '')}", c.text.strip("\n"), "```"])
            elif tag == "include":
                inc = (path.parent / c.get("src")).resolve()
                if inc.is_file() and inc not in seen:
                    seen.add(inc)
                    out.append(qhelp_markdown(inc, seen))
            elif tag == "pre":
                out.extend(["", "```", (c.text or "").strip("\n"), "```"])
            elif tag == "table":
                rows = [[oneline(inline(td)) for td in tr] for tr in c.iter("tr")]
                if rows:
                    out.extend(["", f"| {' | '.join(rows[0])} |", "|" + " --- |" * len(rows[0])] + [f"| {' | '.join(r)} |" for r in rows[1:]])
            else:
                block(c)

    block(root)
    return "\n".join(out).strip()


def ql_metadata(path: Path) -> dict:
    head = path.read_text(encoding="utf-8", errors="replace").split("*/", 1)[0]
    meta, key = {}, None
    for line in head.splitlines():
        line = line.strip().lstrip("/*").strip()
        m = re.match(r"@([\w.-]+)\s*(.*)", line)
        if m:
            key, value = m.groups()
            meta[key] = value.strip()
        elif key and line:
            meta[key] += " " + line
    return meta


def codeql(src: Path, pages: Pages) -> str:
    part = src / "codeql"
    sha = commit(part)
    count = 0
    for help_file in sorted(part.rglob("*.qhelp")):
        query = help_file.with_suffix(".ql")
        if not query.is_file() or ".inc." in help_file.name:
            continue
        meta = ql_metadata(query)
        qid = meta.get("id")
        if not qid or "security" not in meta.get("tags", ""):
            continue
        language = help_file.relative_to(part).parts[0]
        cwes = sorted({f"CWE-{int(c)}" for c in re.findall(r"external/cwe/cwe-(\d+)", meta.get("tags", ""))}, key=lambda c: int(c[4:]))
        facts = [f"Language: {CODEQL_LANGUAGES.get(language, language)}", f"id `{qid}`", f"kind {meta.get('kind', '?')}"]
        if meta.get("security-severity"):
            facts.append(f"security severity {meta['security-severity']}")
        if meta.get("precision"):
            facts.append(f"precision {meta['precision']}")
        if cwes:
            facts.append("weaknesses " + ", ".join(cwes))
        body = f"# {meta.get('name', qid)} (CodeQL, {CODEQL_LANGUAGES.get(language, language)})\n\n{oneline(meta.get('description'))}\n\n{'; '.join(facts)}.\n\n{qhelp_markdown(help_file)}"
        url = f"https://codeql.github.com/codeql-query-help/{language}/{qid.replace('/', '-')}/"
        pages.add("codeql", f"{language}/{qid.replace('/', '__')}.md", f"CodeQL {qid}: {meta.get('name', qid)}", url, body,
                  [symbol(qid, "codeql query", f"CodeQL {CODEQL_LANGUAGES.get(language, language)}",
                          f"CodeQL {qid}: {meta.get('name', qid)}" + (f" ({', '.join(cwes)})" if cwes else ""))])
        count += 1
    print(f"security-texts: codeql: {count} security queries")
    return f"codeql={sha[:12]}"


def dotnet_rules(src: Path, pages: Pages) -> str:
    part = src / "dotnet-rules"
    sha = commit(part)
    count = 0
    for f in sorted((part / "docs" / "fundamentals" / "code-analysis" / "quality-rules").glob("ca*.md")):
        text = f.read_text(encoding="utf-8")
        if not re.search(r"\*\*Category\*\*\s*\|\s*\[Security\]", text):
            continue
        text = re.sub(r"\A---\n.*?\n---\n", "", text, flags=re.S)
        text = re.sub(r"^\s*(\[!INCLUDE[^\]]*\]|:::code[^\n]*:::)\s*$", "", text, flags=re.M)
        text = re.sub(r"<xref:([^>?]+)(\?[^>]*)?>", lambda m: f"`{m.group(1)}`", text)
        title = heading(text, f.stem.upper())
        rule = title.split(":", 1)[0].strip()
        pages.add("dotnet-rules", f.name, title, f"https://learn.microsoft.com/dotnet/fundamentals/code-analysis/quality-rules/{f.stem}", text,
                  [symbol(rule, "analyzer rule", ".NET security analyzers", f".NET {title}")])
        count += 1
    print(f"security-texts: .NET: {count} security rules")
    return f"dotnet-rules={sha[:12]}"


def rst(text: str) -> str:
    """Bandit's reStructuredText, read as Markdown: headings, code blocks and directives."""
    text = re.sub(r"^\.\. code-block::[ \t]*(\w*)[ \t]*\n\n((?:(?:[ \t]+.*)?\n)+)", lambda m: f"```{m.group(1) if m.group(1) != 'none' else ''}\n"
                  + "\n".join(line[4:] if line.startswith("    ") else line.strip() for line in m.group(2).rstrip().split("\n")) + "\n```\n", text, flags=re.M)
    text = re.sub(r"^\.\. (seealso|versionadded|versionchanged)::\s*(.*)$", lambda m: f"**{m.group(1)}** {m.group(2)}", text, flags=re.M)
    text = re.sub(r"^:Example:[ \t]*$", "**Example:**", text, flags=re.M)
    text = re.sub(r"^:Config Options:[ \t]*$", "**Config options:**", text, flags=re.M)
    text = re.sub(r"^[=\-~^]{3,}\n(.+)\n[=\-~^]{3,}$", r"# \1", text, flags=re.M)
    text = re.sub(r"^(.+)\n[-=~^]{3,}$", r"## \1", text, flags=re.M)
    return text


def bandit(src: Path, pages: Pages) -> str:
    part = src / "bandit"
    sha = commit(part)
    count = 0
    for f in sorted((part / "bandit" / "plugins").glob("*.py")):
        tree = ast.parse(f.read_text(encoding="utf-8"))
        module = ast.get_docstring(tree) or ""
        for fn in (n for n in tree.body if isinstance(n, ast.FunctionDef)):
            ids = [d.args[0].value for d in fn.decorator_list if isinstance(d, ast.Call) and getattr(d.func, "attr", "") == "test_id"
                   and d.args and isinstance(d.args[0], ast.Constant)]
            if not ids:
                continue
            check = ids[0]
            # Each check is documented in its function's docstring, or in the module's when the module holds one check.
            doc = ast.get_docstring(fn) or (module if check in module else "")
            m = re.search(rf"{check}: ([^\n*]+)", doc)
            title = m.group(1).strip() if m else fn.name.replace("_", " ")
            cwes = sorted(set(re.findall(r"CWE-(\d+)", doc)), key=int)
            pages.add("bandit", f"{check}.md", f"Bandit {check}: {title}", f"https://bandit.readthedocs.io/en/latest/plugins/{check.lower()}_{fn.name}.html",
                      rst(doc) if doc else f"# {check}: {title}\n\nBandit's check {fn.name}, in {f.name}.",
                      [symbol(check, "bandit check", "Bandit", f"Bandit {check}: {title}" + (f" (CWE-{', CWE-'.join(cwes)})" if cwes else ""))])
            count += 1
    for kind in ("calls", "imports"):
        f = part / "bandit" / "blacklists" / f"{kind}.py"
        doc = ast.get_docstring(ast.parse(f.read_text(encoding="utf-8"))) or ""
        for m in re.finditer(r"^(B\d{3}): ([^\n]+)\n-+\n(.*?)(?=^B\d{3}: |\Z)", doc, re.M | re.S):
            check, title, body = m.groups()
            pages.add("bandit", f"{check}.md", f"Bandit {check}: {title}", f"https://bandit.readthedocs.io/en/latest/blacklists/blacklist_{kind}.html",
                      f"# {check}: {title}\n\nA Bandit blacklist check of {kind}.\n\n{rst(body)}",
                      [symbol(check, "bandit check", "Bandit", f"Bandit {check}: blacklisted {kind[:-1]} {title}")])
            count += 1
    print(f"security-texts: bandit: {count} checks")
    return f"bandit={sha[:12]}"


def gosec(src: Path, pages: Pages) -> str:
    part = src / "gosec"
    sha = commit(part)
    rules = (part / "RULES.md").read_text(encoding="utf-8")
    cwe_of = dict(re.findall(r'"(G\d{3})":\s*"(\d+)"', (part / "issue" / "issue.go").read_text(encoding="utf-8")))
    listed = re.findall(r"^- (?:\[(G\d{3})\]\([^)]*\)|(G\d{3})) — (.+?)(?: \(\*\*(\w+)\*\*\))?\s*$", rules, re.M)
    symbols, lines = [], []
    for a, b, title, engine in listed:
        rule = a or b
        cwe_id = cwe_of.get(rule)
        lines.append(f"- **{rule}**: {title}" + (f" ({engine} analysis)" if engine else "") + (f"; CWE-{cwe_id}" if cwe_id else ""))
        symbols.append(symbol(rule, "gosec rule", "gosec", f"gosec {rule}: {re.sub(r'`', '', title)}" + (f" (CWE-{cwe_id})" if cwe_id else "")))
    details = rules.split("## Rules configuration", 1)[-1]
    body = "# gosec rules\n\ngosec, the Go security checker: each rule with the weakness it maps to.\n\n" + "\n".join(lines) + "\n\n## Rules configuration" + details
    pages.add("gosec", "rules.md", "gosec rules", blob("gosec", sha, "RULES.md"), body, symbols)
    print(f"security-texts: gosec: {len(symbols)} rules")
    return f"gosec={sha[:12]}"


def eslint(src: Path, pages: Pages) -> str:
    part = src / "eslint-security"
    sha = commit(part)
    files = sorted((part / "docs" / "rules").glob("*.md"))
    for f in files:
        text = f.read_text(encoding="utf-8")
        title = heading(text, f.stem)
        rule = f"security/{f.stem}"
        pages.add("eslint-security", f.name, f"eslint-plugin-security {rule}", blob("eslint-security", sha, f"docs/rules/{f.name}"), text,
                  [symbol(rule, "eslint rule", "eslint-plugin-security", f"ESLint {rule}: {re.sub(r' *[(]`[^`]+`[)]$', '', title)}")])
    print(f"security-texts: eslint-plugin-security: {len(files)} rules")
    return f"eslint-security={sha[:12]}"


def main(argv: list[str] | None = None) -> int:
    p = argparse.ArgumentParser(description=__doc__.split("\n\n")[0])
    p.add_argument("--from", dest="src", required=True, type=Path, help="the folder tools/fetch-security-sources.sh fetched into")
    p.add_argument("--out", required=True, type=Path, help="the pack's work directory to write")
    a = p.parse_args(argv)
    missing = [d for d in ["mitre", *GITHUB] if not (a.src / d).is_dir()]
    if missing:
        print(f"security-texts: not in {a.src}: {', '.join(missing)} (run tools/fetch-security-sources.sh)", file=sys.stderr)
        return 1
    a.out.mkdir(parents=True, exist_ok=True)
    pages = Pages(a.out)
    provenance = [cwe(a.src, pages), capec(a.src, pages), cheatsheets(a.src, pages), asvs(a.src, pages), wstg(a.src, pages),
                  *top10s(a.src, pages), codeql(a.src, pages), dotnet_rules(a.src, pages), bandit(a.src, pages), gosec(a.src, pages), eslint(a.src, pages)]
    (a.out / "index.json").write_text(json.dumps({"provenance": ",".join(provenance), "docs": pages.docs}, ensure_ascii=False, indent=1) + "\n", encoding="utf-8")
    print(f"security-texts: {len(pages.docs)} pages, {sum(len(d['symbols']) for d in pages.docs)} symbols")
    return 0


if __name__ == "__main__":
    sys.exit(main())
