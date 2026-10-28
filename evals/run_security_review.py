#!/usr/bin/env python3
"""
The security review, measured on the Juliet test suite (NIST SARD, public domain) and the OWASP Benchmark (GPL-2.0, read
here, never shipped): precision and recall per weakness for four arms.

    scanners   the scanners alone (the scanners image's opengrep with Arena's rules), on every case of the categories
    model      the model alone: the review's sinks, its tracing question, its grounding on the cited line, and its
               skeptic, told only the CWE's id
    pack       the model with the security pack: as above, a finding kept only for a CWE the pack knows, and the
               skeptic given the pack's page
    both       the review as it ships: the scanners' hits traced too and named in the question, the pack, and every
               scanners' finding (one the model cleared too, kept with its doubt)

Each case is reviewed as a repository of its own, the way SecurityReview.cs reviews one: its sink patterns over the
case's files, at most three a file and thirty a review, the lines around each sink and its function's callers, the
same prompts, temperature and token limits, through Arena's gateway as a throwaway person (removed after) with the
person's default model. The suites name their answers (Juliet's comments say FLAW and FIX, its classes are named for
the CWE and its methods bad and good; the Benchmark's servlet paths name the category), so every case is read with its
comments removed and those names replaced (line numbers kept): neither the model nor the scanners can read the answer.
Scored per case and flow: a finding of the case's weakness (or one of its kin, CWE-23 for CWE-22) in Juliet's bad
flow is a true positive, in one of its good flows a false positive; a Benchmark case is positive when the finding is
in its file. The Benchmark's own score (true positive rate less false positive rate) is given beside.

    python3 evals/run_security_review.py --suite owasp --root DIR [--count 20] [--seed 7] [--jobs 3]
    python3 evals/run_security_review.py --suite juliet-java --root DIR [--count 12]
    python3 evals/run_security_review.py --suite juliet-csharp --root DIR [--count 12]

DIR is the unpacked suite:
    owasp          git clone --depth 1 https://github.com/OWASP-Benchmark/BenchmarkJava (f40c618c, 240 MB)
    juliet-java    samate.nist.gov/SARD/downloads/test-suites/2017-10-01-juliet-test-suite-for-java-v1-3.zip
                   (77 MB, sha256 d985f417...7e60), its Java/src/testcases
    juliet-csharp  samate.nist.gov/SARD/downloads/test-suites/2020-08-01-juliet-test-suite-for-csharp-v1-3.zip
                   (69 MB, sha256 2e6dbac4...e8f8), its src/testcases

--count is the cases per category the model reads (the Benchmark's: half real, half not); the scanners read them all.
The model's answers are kept in evals/.cache (by question), so a run stopped is resumed and the arms that ask the same
question share its answer. Writes evals/security-review-<suite>-results.json: per arm and category the counts, and per
case what each arm flagged (case names and lines, no code).
"""
from __future__ import annotations

import argparse
import concurrent.futures
import hashlib
import importlib.util
import io
import json
import os
import random
import re
import signal
import ssl
import subprocess
import sys
import tarfile
import threading
import time
import urllib.error
import urllib.request
import uuid
from dataclasses import dataclass, field
from pathlib import Path

HERE = Path(__file__).resolve().parent
ROOT = HERE.parent
CACHE = HERE / ".cache" / "security-review-answers.jsonl"
PACK_CACHE = HERE / ".cache" / "security-review-pack.json"

# ---- The review, as SecurityReview.cs has it (the suites' languages: Java and C#).
MOST_READ, MOST_PER_FILE, MOST_VERIFIED = 30, 3, 25
CS, JAVA = (".cs",), (".java", ".kt")


@dataclass(frozen=True)
class Sink:
    kind: str
    cwe: str
    what: str
    exts: tuple
    pattern: re.Pattern | None


def _s(kind, cwe, what, exts, pattern):
    return Sink(kind, cwe, what, exts, re.compile(pattern))


SINKS = [
    _s("sql", "CWE-89", "an SQL query", CS, r"(SqlCommand\(|\.CommandText *=|ExecuteSqlRaw|FromSqlRaw|SqlQueryRaw)"),
    _s("sql", "CWE-89", "an SQL query", JAVA, r"(createStatement\(|\.executeQuery\(|\.executeUpdate\(|\.addBatch\(|createNativeQuery\(|createQuery\(.*\+)"),
    _s("command", "CWE-78", "an OS command", CS, r"(Process\.Start\(|new ProcessStartInfo)"),
    _s("command", "CWE-78", "an OS command", JAVA, r"(Runtime\.getRuntime\(\)\.exec|new ProcessBuilder\()"),
    _s("deserialization", "CWE-502", "a deserialiser", CS, r"(BinaryFormatter|TypeNameHandling\.(All|Auto|Objects)|LosFormatter|NetDataContractSerializer|SoapFormatter)"),
    _s("deserialization", "CWE-502", "a deserialiser", JAVA, r"(ObjectInputStream|\.readObject\(|XMLDecoder|enableDefaultTyping)"),
    _s("code", "CWE-94", "code run from text", CS, r"(CSharpScript\.|Assembly\.Load\()"),
    _s("code", "CWE-94", "code run from text", JAVA, r"(ScriptEngine|SpelExpressionParser|OgnlUtil)"),
    _s("xxe", "CWE-611", "an XML parser", CS, r"(DtdProcessing\.Parse|XmlResolver *= *new|XmlUrlResolver)"),
    _s("xxe", "CWE-611", "an XML parser", JAVA, r"(DocumentBuilderFactory|SAXParserFactory|XMLInputFactory|TransformerFactory)"),
    _s("path", "CWE-22", "a file path", CS, r"(File\.(ReadAll|WriteAll|Open|Delete|Copy|Move)|new FileStream\(|PhysicalFile\()"),
    _s("path", "CWE-22", "a file path", JAVA, r"(new File\(|Paths\.get\(|Files\.(read|write|newInputStream|newOutputStream))"),
    _s("xss", "CWE-79", "HTML written", CS, r"(Html\.Raw\(|MarkupString\)|new HtmlString\()"),
    _s("xss", "CWE-79", "HTML written", JAVA, r"getWriter\(\)\.(print|write)"),
    _s("ssrf", "CWE-918", "a URL fetched by the server", CS, r"(\.GetAsync\(|\.GetStringAsync\(|\.PostAsync\(|WebRequest\.Create\()"),
    _s("ssrf", "CWE-918", "a URL fetched by the server", JAVA, r"(new URL\(|RestTemplate|WebClient\.create)"),
    _s("redirect", "CWE-601", "a redirect", CS, r"Redirect\("),
    _s("redirect", "CWE-601", "a redirect", JAVA, r"sendRedirect\("),
]
DANGER = {"sql": 0, "command": 1, "deserialization": 2, "code": 3, "xxe": 4, "path": 5, "xss": 6, "ssrf": 7, "redirect": 8}
TRACED_CWES = {"CWE-89": "sql", "CWE-78": "command", "CWE-77": "command", "CWE-502": "deserialization", "CWE-94": "code", "CWE-95": "code",
               "CWE-1336": "code", "CWE-611": "xxe", "CWE-22": "path", "CWE-98": "path", "CWE-79": "xss", "CWE-80": "xss", "CWE-918": "ssrf",
               "CWE-601": "redirect"}
NOT_PRODUCT = re.compile(r"(^|/)(tests?|spec|specs|__tests__|testdata|fixtures?|examples?|samples?|node_modules|vendor|third_party|dist|build|e2e|migrations)/"
                         r"|\.(test|spec)\.[a-z]+$|_test\.go$|Tests?\.cs$|test_[^/]*\.py$", re.IGNORECASE)
COMMENT = re.compile(r"^\s*(//|#|\*|/\*|--|<!--)")
FUNCTION = re.compile(r"^\s*(?:def|async def)\s+(\w+)\s*\(|^\s*func\s+(?:\([^)]*\)\s*)?(\w+)\s*\(|^\s*(?:export\s+)?(?:async\s+)?function\s*\*?\s*(\w+)\s*\("
                      r"|^\s*(?:export\s+)?(?:const|let|var)\s+(\w+)\s*=\s*(?:async\s*)?(?:\([^)]*\)|\w+)\s*=>"
                      r"|^\s*(?:public|private|protected|internal|static|async|override|virtual|final|synchronized|suspend|fun|\s)+[\w<>\[\],.? ]*?\s(\w+)\s*\([^;]*$"
                      r"|^\s*def\s+(?:self\.)?(\w+)")
KEYWORDS = {"if", "for", "while", "switch", "catch", "using", "return", "foreach", "lock", "new", "else", "elif", "with", "function", "await"}
CWE_ID = re.compile(r"^\s*(?:CWE)?[\s-]*(\d{1,5})(?:\s*$|\s*[:(,]|\s+-)", re.IGNORECASE)
SEVERITY_RANK = {"critical": 0, "high": 1, "medium": 2}

ANALYZE_PROMPT = (
    "You are an application security engineer reviewing one place in a repository where a dangerous call is made. You get the code "
    "around it, its lines numbered (the call's line marked >), and the places that call its function. Decide whether data from "
    "outside the program (an HTTP request, a message, an uploaded file, command-line arguments, a webhook) can reach the call "
    "without what the weakness needs (parameters for a query, an allow-list for a path or URL, encoding for HTML, a safe parser "
    "setting). Say vulnerable only when the code shown proves the path, or a caller shown clearly passes outside data; when it "
    "depends on code not shown, say not vulnerable and what to check. Constant strings, configuration and data the program made "
    "itself are not outside data.\n\n"
    "Answer with JSON only: {\"vulnerable\": true|false, \"cwe\": \"CWE-<number>\", \"severity\": \"critical|high|medium|low\", "
    "\"title\": \"short\", \"sink_line\": 123, \"sink_code\": \"the sink's line, copied exactly as shown\", "
    "\"source\": \"where the outside data comes in: path:line and what\", \"flow\": [\"each step from the source to the sink\"], "
    "\"exploit\": \"how an attacker would use it\", \"fix\": \"the change that removes it\", \"check\": \"what to look at when not proven\"}.")
VERIFY_PROMPT = (
    "You check another engineer's security finding as a skeptic: try to REFUTE it. It is real only if the code shown proves that "
    "outside data reaches the sink without what the weakness (its CWE page is given) needs, and nothing shown (validation, a "
    "parameterised call, an allow-list, encoding, a framework's protection, the data being the program's own) prevents it. When "
    "unsure, it is not real. Answer with JSON only: {\"real\": true|false, \"why\": \"one or two sentences\"}.")


def cut(s: str, most: int) -> str:
    return s if len(s) <= most else s[:most]


def squash(s: str) -> str:
    return " ".join(s.split())


def cwe_of(said) -> str | None:
    m = CWE_ID.match(said) if isinstance(said, str) else None
    return f"CWE-{int(m.group(1))}" if m else None


def as_int(v) -> int | None:
    return v if isinstance(v, int) and not isinstance(v, bool) else None


def as_text(v) -> str:
    """A JSON value as System.Text.Json's ToString gives it: text as is, anything else as JSON, null as nothing."""
    if v is None:
        return ""
    return v if isinstance(v, str) else json.dumps(v, ensure_ascii=False)


def json_of(text: str) -> dict | None:
    start, end = text.find("{"), text.rfind("}")
    if start < 0 or end <= start:
        return None
    try:
        got = json.loads(text[start:end + 1])
    except ValueError:
        return None
    return got if isinstance(got, dict) else None


def enclosing_function(above: list[str]) -> str | None:
    for line in reversed(above):
        m = FUNCTION.match(line)
        if m:
            name = next((g for g in m.groups() if g), None)
            if name and name not in KEYWORDS:
                return name
    return None


# ---- A case: its files as read (comments out, the answers' names replaced), and what is true of it.
@dataclass
class Case:
    id: str
    category: str
    family: frozenset
    files: dict = field(default_factory=dict)  # path -> lines, as read
    units: dict = field(default_factory=dict)  # path -> {line: unit}, from the original names
    positive: set = field(default_factory=set)  # the units that are the weakness
    negative: set = field(default_factory=set)  # the units that are not


@dataclass(frozen=True)
class Candidate:
    path: str
    line: int
    text: str
    sink: Sink


@dataclass
class Finding:
    path: str
    line: int
    cwe: str
    by: str
    verified: bool | None = None
    severity: str = "medium"
    title: str = ""
    source: str = ""
    flow: str = ""
    exploit: str = ""
    cwe_title: str = ""


def strip_comments(text: str) -> str:
    """Java and C# without their comments; strings and line breaks kept, so every line keeps its number."""
    out, i, n = [], 0, len(text)
    while i < n:
        c = text[i]
        if text.startswith("//", i):
            j = text.find("\n", i)
            i = n if j < 0 else j
        elif text.startswith("/*", i):
            j = text.find("*/", i + 2)
            j = n if j < 0 else j + 2
            out.append("\n" * text.count("\n", i, j))
            i = j
        elif c == '"' and i > 0 and text[i - 1] == "@" or text.startswith('$@"', i) or text.startswith('@$"', i):
            # A C# verbatim string: "" is its quote.
            start = i
            i = text.index('"', i) + 1
            while i < n:
                if text[i] == '"':
                    if i + 1 < n and text[i + 1] == '"':
                        i += 2
                        continue
                    i += 1
                    break
                i += 1
            out.append(text[start:i])
        elif c in "\"'":
            start, i = i, i + 1
            while i < n and text[i] != c and text[i] != "\n":
                i += 2 if text[i] == "\\" else 1
            i += 1
            out.append(text[start:i])
        else:
            out.append(c)
            i += 1
    return "".join(out)


DECLARED = re.compile(r"^\s*(?:(?:public|private|protected|internal|static|override|virtual|final|synchronized|abstract|sealed|async|new)\s+)+"
                      r"[\w<>\[\],.? ]+?\s+(\w+)\s*\(")


def methods(lines: list[str]) -> dict[int, str]:
    """Each line's method, by the declaration above it (Juliet's methods all have a modifier)."""
    at, name = {}, None
    for i, line in enumerate(lines, 1):
        m = DECLARED.match(line)
        if m and m.group(1) not in KEYWORDS:
            name = m.group(1)
        at[i] = name
    return at


def juliet_unit(method: str | None, file_base: str) -> str:
    """Juliet's flow a method is in: bad, or one of the good ones (goodG2B, goodB2G1, good1...)."""
    name = method or ""
    while True:
        trimmed = re.sub(r"_?(?:Sink|Source|Private|Public|Static|base)$", "", name, flags=re.IGNORECASE)
        if trimmed == name:
            break
        name = trimmed
    low = name[:1].lower() + name[1:]
    if low.startswith("bad"):
        return "bad"
    if low.startswith("good"):
        return low
    # The 81 variants put each flow in a file of its own (..._81_bad, ..._81_goodG2B): the method is "action".
    m = re.search(r"_(bad|good\w*)$", file_base)
    if m:
        return m.group(1)
    return "other"


JULIET = {
    "juliet-java": ("java", ["CWE89_SQL_Injection", "CWE78_OS_Command_Injection", "CWE23_Relative_Path_Traversal", "CWE36_Absolute_Path_Traversal",
                             "CWE80_XSS", "CWE81_XSS_Error_Message", "CWE83_XSS_Attribute", "CWE601_Open_Redirect", "CWE90_LDAP_Injection",
                             "CWE643_Xpath_Injection", "CWE327_Use_Broken_Crypto", "CWE328_Reversible_One_Way_Hash", "CWE338_Weak_PRNG",
                             "CWE614_Sensitive_Cookie_Without_Secure"]),
    "juliet-csharp": ("cs", ["CWE89_SQL_Injection", "CWE78_OS_Command_Injection", "CWE23_Relative_Path_Traversal", "CWE36_Absolute_Path_Traversal",
                             "CWE80_XSS", "CWE81_XSS_Error_Message", "CWE83_XSS_Attribute", "CWE601_Open_Redirect", "CWE90_LDAP_Injection",
                             "CWE643_Xpath_Injection", "CWE327_Use_Broken_Crypto", "CWE328_Reversible_One_Way_Hash", "CWE338_Weak_PRNG",
                             "CWE614_Sensitive_Cookie_Without_Secure", "CWE94_Improper_Control_of_Generation_of_Code"]),
}
# A weakness and its kin: a finding of any of them is a finding of the case's.
FAMILIES = {89: {89, 564, 943}, 78: {78, 77, 88}, 22: {22, 23, 36, 73, 98}, 23: {22, 23, 36, 73, 98}, 36: {22, 23, 36, 73, 98},
            79: {79, 80, 81, 83, 87}, 80: {79, 80, 81, 83, 87}, 81: {79, 80, 81, 83, 87}, 83: {79, 80, 81, 83, 87}, 601: {601},
            90: {90}, 643: {643, 91}, 327: {327, 326}, 328: {328, 916}, 330: {330, 338, 331, 335, 336}, 338: {330, 338, 331, 335, 336},
            614: {614}, 501: {501}, 94: {94, 95}}
CASE_FILE = re.compile(r"^(CWE\d+_\w+?_\d{2})(?:[a-z]|_base|_bad|_good\w*)?\.(java|cs)$")
NAMES_FIXED = [(re.compile(r"^[ \t]*#(?:if \(!OMIT\w+\)|endif)[^\n]*$", re.MULTILINE), ""), (re.compile(r"\bjuliet\.support\b"), "app.support"),
               (re.compile(r"\btestcasesupport\b"), "appsupport"), (re.compile(r"\btestcases(?=[\\\\/])"), "data"), (re.compile(r"\bCWE\d+_\w+ Injection\b"), "files"), (re.compile(r"\btestcases\."), "app."), (re.compile(r"\bTestCaseSupport\b"), "AppSupport"),
               (re.compile(r"\bAbstractTestCaseServlet\b"), "AbstractServletHandler"), (re.compile(r"\bAbstractTestCaseWeb\b"), "AbstractWebHandler"),
               (re.compile(r"\bAbstractTestCase(\w*)\b"), r"AbstractHandler\1")]
FLOW_NAME = re.compile(r"\b(?:bad|good|Bad|Good)[A-Za-z0-9_]*\b")
CWE_NAME = re.compile(r"\bCWE\d+_\w*")


def juliet_cases(suite: str, root: Path) -> list[Case]:
    ext, folders = JULIET[suite]
    cases = []
    for folder in folders:
        number = int(re.match(r"CWE(\d+)_", folder).group(1))
        groups: dict[str, list[Path]] = {}
        for path in sorted((root / folder).rglob(f"*.{ext}")):
            m = CASE_FILE.match(path.name)
            if m:
                groups.setdefault(m.group(1), []).append(path)
        for base, paths in sorted(groups.items()):
            cases.append(Case(base, f"CWE-{number}", frozenset(FAMILIES[number])))
            cases[-1].files = {p: p.read_text(encoding="utf-8", errors="replace") for p in paths}
    return cases


def read_juliet(case: Case, index: int) -> None:
    """The case's files as read: comments out, the CWE's names and the flows' names replaced, the same everywhere."""
    originals = case.files
    texts = {p: strip_comments(t) for p, t in originals.items()}
    for pattern, to in NAMES_FIXED:
        texts = {p: pattern.sub(to, t) for p, t in texts.items()}
    cwe_names = sorted({m for t in texts.values() for m in CWE_NAME.findall(t)} | {p.stem for p in originals})
    renamed = {name: f"Part{k + 1}" for k, name in enumerate(cwe_names)}
    flows = sorted({m for t in texts.values() for m in FLOW_NAME.findall(t)})
    renamed.update({name: f"step{k + 1}" if name[0].islower() else f"Step{k + 1}" for k, name in enumerate(flows)})
    names = re.compile(r"\b(" + "|".join(re.escape(n) for n in sorted(renamed, key=len, reverse=True)) + r")\b")
    case.files, case.units = {}, {}
    for p, text in texts.items():
        path = f"c{index:05d}/{renamed[p.stem]}{p.suffix}"
        lines = names.sub(lambda m: renamed[m.group(1)], text).split("\n")
        original = originals[p].split("\n")
        assert len(lines) == len(original), p
        case.files[path] = lines
        at = methods(original)
        case.units[path] = {i: juliet_unit(at[i], p.stem) for i in at}
    units = {u for per in case.units.values() for u in per.values()}
    case.positive = {"bad"}
    case.negative = {u for u in units if u.startswith("good") and u != "good"} or {"good"}


BENCHMARK_CATEGORIES = {"cmdi": 78, "crypto": 327, "hash": 328, "ldapi": 90, "pathtraver": 22, "securecookie": 614, "sqli": 89, "trustbound": 501,
                        "weakrand": 330, "xpathi": 643, "xss": 79}


def owasp_cases(root: Path) -> list[Case]:
    cases = []
    for line in (root / "expectedresults-1.2.csv").read_text().splitlines():
        if line.startswith("#") or not line.strip():
            continue
        name, category, real, _cwe = line.split(",")[:4]
        number = BENCHMARK_CATEGORIES[category]
        case = Case(name, category, frozenset(FAMILIES[number]))
        path = root / "src/main/java/org/owasp/benchmark/testcode" / f"{name}.java"
        case.files = {path: path.read_text(encoding="utf-8")}
        (case.positive if real == "true" else case.negative).add("case")
        cases.append(case)
    return cases


CATEGORY_PATH = re.compile(r"/(?:" + "|".join(BENCHMARK_CATEGORIES) + r")-\d+/")


def read_owasp(case: Case, index: int) -> None:
    (p, text), = case.files.items()
    text = strip_comments(text)
    text = CATEGORY_PATH.sub("/app/", text)
    text = re.sub(r"\bBenchmarkTest(\d{5})?\b", r"Handler\1", text)
    text = re.sub(r"\borg\.owasp\.benchmark\b", "com.example.app", text)
    text = re.sub(r"\bbenchmark(props|\.properties)\b", r"app\1", text)
    text = re.sub(r"Problem executing [^\"]*", "Problem running it", text)
    text = re.sub(r"\btestCaseNumber\b", "handlerNumber", text)
    lines = text.split("\n")
    path = f"c{index:05d}/{re.sub(r'BenchmarkTest', 'Handler', p.stem)}.java"
    case.files = {path: lines}
    case.units = {path: {i: "case" for i in range(1, len(lines) + 1)}}


# ---- The scanners: the image's server, on a network of its own with no way out.
class Scanners:
    def __init__(self, image: str):
        self.name = "arena-measure-scanners-" + uuid.uuid4().hex[:6]
        self.network = self.name
        subprocess.run(["docker", "network", "create", "--internal", self.network], check=True, capture_output=True)
        subprocess.run(["docker", "run", "-d", "--rm", "--name", self.name, "--network", self.network, "--user", "65534:65534", "--read-only",
                        "--tmpfs", "/work:size=2g,uid=65534,gid=65534,mode=0700", "--tmpfs", "/tmp:size=256m,uid=65534,gid=65534,mode=0700",
                        "--cap-drop", "ALL", "--security-opt", "no-new-privileges:true", "-e", "SCANNERS_THREADS=4", image],
                       check=True, capture_output=True)
        ip = subprocess.run(["docker", "inspect", "-f", "{{range .NetworkSettings.Networks}}{{.IPAddress}}{{end}}", self.name],
                            check=True, capture_output=True, text=True).stdout.strip()
        self.url = f"http://{ip}:7800"
        for _ in range(60):
            try:
                with urllib.request.urlopen(self.url + "/health", timeout=5) as r:
                    self.health = json.loads(r.read())
                    return
            except OSError:
                time.sleep(1)
        raise RuntimeError("the scanners did not start")

    def scan(self, files: dict[str, list[str]]) -> list[dict]:
        buf = io.BytesIO()
        with tarfile.open(fileobj=buf, mode="w") as tar:
            for path, lines in files.items():
                data = "\n".join(lines).encode()
                info = tarfile.TarInfo(path)
                info.size = len(data)
                tar.addfile(info, io.BytesIO(data))
        # The server names what it scanned by a commit: the batch's own hash stands for one.
        commit = hashlib.sha1(buf.getvalue()).hexdigest()
        req = urllib.request.Request(self.url + f"/scan?tools=opengrep&commit={commit}", data=buf.getvalue(), headers={"Content-Type": "application/x-tar"}, method="POST")
        try:
            with urllib.request.urlopen(req, timeout=1800) as r:
                answer = json.loads(r.read())
        except OSError as e:
            said = subprocess.run(["docker", "logs", "--tail", "5", self.name], capture_output=True, text=True).stderr
            raise RuntimeError(f"the scanners did not answer ({e}); they said:\n{said}") from e
        assert not answer.get("more"), f"the scanners left {answer.get('more')} findings out: scan fewer files at once"
        errors = [t for t in answer.get("tools", []) if t.get("error")]
        assert not errors, errors
        return answer.get("findings", [])

    def close(self):
        subprocess.run(["docker", "rm", "-f", self.name], capture_output=True)
        subprocess.run(["docker", "network", "rm", self.network], capture_output=True)


# ---- The security pack, from Argus, as the app reaches it (its key, on the stack's network); the key never on a command line.
PACK_CLIENT = r"""
import json, os, sys, urllib.request
base = "http://argus:7700/mcp"
H = {"x-llm-user-email": os.environ["EMAIL"], "Authorization": "Bearer " + os.environ["KEY"], "Accept": "application/json, text/event-stream", "Content-Type": "application/json"}
session = None
def post(body):
    r = urllib.request.Request(base, data=json.dumps(body).encode(), headers={**H, **({"mcp-session-id": session} if session else {})}, method="POST")
    with urllib.request.urlopen(r, timeout=120) as resp:
        return resp.headers.get("mcp-session-id"), resp.read().decode()
def call(tool, args):
    global session
    if session is None:
        session, _ = post({"jsonrpc": "2.0", "id": 1, "method": "initialize", "params": {"protocolVersion": "2025-06-18", "capabilities": {}, "clientInfo": {"name": "eval", "version": "1"}}})
    _, text = post({"jsonrpc": "2.0", "id": 2, "method": "tools/call", "params": {"name": tool, "arguments": args}})
    data = json.loads(text.split("data: ", 1)[1]) if "data: " in text else json.loads(text)
    result = data.get("result") or {}
    if result.get("isError"):
        return {"error": "".join(c.get("text", "") for c in result.get("content", []))[:500]}
    if result.get("structuredContent") is not None:
        return {"value": result["structuredContent"]}
    return {"value": json.loads("".join(c.get("text", "") for c in result.get("content", [])))}
for line in sys.stdin:
    req = json.loads(line)
    try:
        out = call(req["tool"], req["args"])
    except Exception as e:
        session = None
        out = {"error": f"{type(e).__name__}: {e}"[:500]}
    print(json.dumps(out), flush=True)
"""


class Pack:
    def __init__(self, key: str, email: str):
        self.lock = threading.Lock()
        self.known = json.loads(PACK_CACHE.read_text()) if PACK_CACHE.exists() else {}
        env = {**os.environ, "KEY": key, "EMAIL": email}
        self.proc = subprocess.Popen(["docker", "run", "-i", "--rm", "--network", "arena_default", "-e", "KEY", "-e", "EMAIL", "python:3.13-slim",
                                      "python", "-u", "-c", PACK_CLIENT], stdin=subprocess.PIPE, stdout=subprocess.PIPE, text=True, env=env)

    def _call(self, tool: str, args: dict):
        key = json.dumps([tool, args], sort_keys=True)
        with self.lock:
            if key not in self.known:
                self.proc.stdin.write(json.dumps({"tool": tool, "args": args}) + "\n")
                self.proc.stdin.flush()
                got = json.loads(self.proc.stdout.readline())
                if "error" in got:
                    return None
                self.known[key] = got["value"]
                PACK_CACHE.write_text(json.dumps(self.known))
            return self.known[key]

    def cwe(self, cwe: str) -> dict | None:
        found = self._call("docs_lookup", {"name": cwe, "lang": "cwe"})
        rows = found if isinstance(found, list) else (found or {}).get("result") if isinstance(found, dict) else None
        return next((r for r in rows or [] if isinstance(r, dict) and str(r.get("name", "")).lower() == cwe.lower()), None)

    def text(self, doc_path: str) -> str | None:
        doc = self._call("docs_get", {"doc_path": doc_path, "source": "security"})
        if not isinstance(doc, dict):
            return None
        body = doc.get("result") if isinstance(doc.get("result"), dict) else doc
        return body.get("text") if isinstance(body.get("text"), str) else None

    def close(self):
        self.proc.stdin.close()
        self.proc.wait(timeout=30)


# ---- The model, through the gateway as the person.
class Model:
    def __init__(self, url: str, key: str, model: str):
        self.url, self.key, self.model = url, key, model
        self.lock = threading.Lock()
        self.ctx = ssl._create_unverified_context()
        self.answers = {}
        if CACHE.exists():
            for line in CACHE.read_text(encoding="utf-8").splitlines():
                row = json.loads(line)
                self.answers[row["key"]] = row["text"]
        self.asked = self.cached = self.refused = 0
        self.tokens = [0, 0]

    def ask(self, system: str, user: str, max_tokens: int) -> str:
        key = hashlib.sha256(json.dumps([self.model, system, user, max_tokens]).encode()).hexdigest()
        with self.lock:
            if key in self.answers:
                self.cached += 1
                return self.answers[key]
        body = {"model": self.model, "messages": [{"role": "system", "content": system}, {"role": "user", "content": user}], "max_tokens": max_tokens,
                "temperature": 0.1, "chat_template_kwargs": {"enable_thinking": False}}
        for attempt in range(4):
            try:
                req = urllib.request.Request(self.url, data=json.dumps(body).encode(), method="POST",
                                             headers={"Authorization": f"Bearer {self.key}", "Content-Type": "application/json"})
                with urllib.request.urlopen(req, timeout=600, context=self.ctx) as r:
                    answer = json.loads(r.read())
                break
            except urllib.error.HTTPError as e:
                # The person's safeguards refused it (the review's own requests pass them): no answer, counted.
                if e.code == 400 and b"refused" in e.read():
                    with self.lock:
                        self.refused += 1
                    return ""
                if attempt == 3:
                    raise
                print(f"  the gateway: {e}; again", flush=True)
                time.sleep(10 * (attempt + 1))
            except OSError as e:
                if attempt == 3:
                    raise
                print(f"  the gateway: {e}; again", flush=True)
                time.sleep(10 * (attempt + 1))
        text = answer["choices"][0]["message"].get("content") or ""
        usage = answer.get("usage") or {}
        with self.lock:
            self.asked += 1
            self.tokens[0] += usage.get("prompt_tokens", 0)
            self.tokens[1] += usage.get("completion_tokens", 0)
            self.answers[key] = text
            with CACHE.open("a", encoding="utf-8") as f:
                f.write(json.dumps({"key": key, "text": text}) + "\n")
        return text


# ---- One review of one case.
def candidates(case: Case, traced: list[dict]) -> list[Candidate]:
    found = [Candidate(h["path"], h["line"], h.get("code") or "", Sink(TRACED_CWES[h["cwe"]], h["cwe"], h.get("title") or "", (), None)) for h in traced]
    for sink in SINKS:
        hits = 0
        for path in sorted(case.files):
            if not path.endswith(sink.exts):
                continue
            for i, line in enumerate(case.files[path], 1):
                if hits < 60 and sink.pattern.search(line):
                    found.append(Candidate(path, i, line, sink))
                    hits += 1
    return order(found, {(h["path"], h["line"]) for h in traced})


def order(found: list[Candidate], flagged: set) -> list[Candidate]:
    def flag(c):
        return 0 if (c.path, c.line) in flagged else 1
    best: dict = {}
    for c in found:
        if NOT_PRODUCT.search(c.path) or COMMENT.match(c.text):
            continue
        key = (c.path, c.line)
        if key not in best or DANGER[c.sink.kind] < DANGER[best[key].sink.kind]:
            best[key] = c
    ranked = sorted(best.values(), key=lambda c: (flag(c), DANGER[c.sink.kind], c.path, c.line))
    per_file: dict[str, int] = {}
    taken = []
    for c in ranked:
        i = per_file.get(c.path, 0)
        per_file[c.path] = i + 1
        if i < MOST_PER_FILE:
            taken.append((c, i))
    taken.sort(key=lambda x: (flag(x[0]), x[1], DANGER[x[0].sink.kind]))
    return [c for c, _ in taken]


def numbered(lines: list[str], start: int, end: int, mark: int) -> tuple[str, dict[int, str]]:
    shown, out = {}, []
    for n in range(max(1, start), min(end, len(lines)) + 1):
        shown[n] = lines[n - 1]
        out.append(str(n).rjust(5) + (" >" if n == mark else "  ") + lines[n - 1] + "\n")
    return "".join(out), shown


def callers(case: Case, c: Candidate, shown: dict[int, str]) -> str:
    name = enclosing_function([shown[k] for k in sorted(shown) if k <= c.line])
    if name is None:
        return ""
    pattern = re.compile(r"[^A-Za-z0-9_]" + re.escape(name) + r" *\(")
    hits = [(p, i, line) for p in sorted(case.files) for i, line in enumerate(case.files[p], 1) if pattern.search(line)][:20]
    out, n = [f"The sink is in `{name}`. Its callers:\n"], 0
    for path, line, text in hits:
        if n >= 4 or (path == c.path and line in shown) or NOT_PRODUCT.search(path) or FUNCTION.match(text):
            continue
        around, _ = numbered(case.files[path], line - 12, line + 6, line)
        out.append(f"\n{path}:\n{around}")
        n += 1
    return (f"The sink is in `{name}`; no caller was found outside it (it may be an entry point itself, or called by name from elsewhere)."
            if n == 0 else "".join(out))


CALL = re.compile(r"\b([A-Za-z_]\w*)\s*\(")
MOST_CALLEES, CALLEE_LINES = 3, 40


def callees(case: Case, c: Candidate, shown: dict[int, str]) -> str:
    """The functions the lines shown call before the sink that this file defines out of view, at most three: the data's way through them."""
    lines = case.files[c.path]
    own = enclosing_function([shown[k] for k in sorted(shown) if k <= c.line])
    names = []
    for n in sorted(k for k in shown if k <= c.line):
        for m in CALL.finditer(shown[n]):
            if m.group(1) not in KEYWORDS and m.group(1) != own and m.group(1) not in names:
                names.append(m.group(1))
    defined = {}
    for i, line in enumerate(lines, 1):
        m = FUNCTION.match(line)
        name = next((g for g in m.groups() if g), None) if m else None
        if name in names and i not in shown and name not in defined:
            defined[name] = i
    blocks = []
    for name in names:
        if name not in defined or len(blocks) >= MOST_CALLEES:
            continue
        start = end = defined[name]
        depth, opened = 0, False
        while end <= len(lines) and end < start + CALLEE_LINES:
            depth += lines[end - 1].count("{") - lines[end - 1].count("}")
            opened = opened or "{" in lines[end - 1]
            if opened and depth <= 0:
                break
            end += 1
        text, _ = numbered(lines, start, end, 0)
        blocks.append(f"`{name}`, lines {start}-{min(end, len(lines))}:\n```\n{text}```")
    return "" if not blocks else "Functions the code above calls, defined further in this file:\n\n" + "\n\n".join(blocks)


def question(c: Candidate, context: str, called: str, flags: list[dict], calls: str = "") -> str:
    q = (f"Repository file {c.path}, the call at line {c.line} is {c.sink.what} (as {c.sink.cwe} would be): `{cut(c.text.strip(), 300)}`\n\n"
         f"```\n{cut(context, 12_000)}```\n\n{cut(called, 10_000)}") + (f"\n\n{cut(calls, 8_000)}" if calls else "")
    if flags:
        q += "\n\nScanners matched a pattern here (a pattern is not proof: judge the code):\n" + "\n".join(
            f"- line {f['line']}, {', '.join(f.get('tools') or [])} ({', '.join(f.get('rules') or [])}): {f.get('title') or ''}" for f in flags)
    return q


def ground(c: Candidate, answer: dict, shown: dict[int, str], pack: Pack | None) -> Finding | None:
    cited = squash(as_text(answer.get("sink_code")))
    said = as_int(answer.get("sink_line"))
    said = c.line if said is None else said
    if len(cited) < 6:
        return None
    line = next((x for x in sorted(range(said - 3, said + 4), key=lambda x: abs(x - said))
                 if x in shown and (t := squash(shown[x])) and (cited in t or (t in cited and len(t) >= 6))), 0)
    if line == 0:
        return None
    cwe = cwe_of(as_text(answer.get("cwe"))) or c.sink.cwe
    # Another sink's kind at this one (the prompt's example copied) is the sink's own: SecurityReview.FindingCwe.
    if cwe in TRACED_CWES and TRACED_CWES[cwe] != c.sink.kind and c.sink.cwe in TRACED_CWES:
        cwe = c.sink.cwe
    title = cwe
    if pack is not None:
        page = pack.cwe(cwe)
        if page is None and cwe != c.sink.cwe:
            cwe = c.sink.cwe
            page = pack.cwe(cwe)
        if page is None:
            return None
        title = page.get("signature") or cwe
    flow = answer.get("flow")
    flow = "\n".join(as_text(s) for s in flow if as_text(s).strip()) if isinstance(flow, list) else as_text(flow)
    severity = answer.get("severity") if answer.get("severity") in ("critical", "high", "medium", "low") else "medium"
    return Finding(c.path, line, cwe, "model", severity=severity, title=as_text(answer.get("title")) or f"{c.sink.what} from outside data",
                   source=as_text(answer.get("source")), flow=cut(flow, 4_000), exploit=cut(as_text(answer.get("exploit")), 4_000), cwe_title=title)


def verify(case: Case, f: Finding, model: Model, pack: Pack | None) -> None:
    text = ""
    if pack is not None and (page := pack.cwe(f.cwe)) and page.get("doc_path"):
        text = cut(pack.text(page["doc_path"]) or f.cwe_title, 6_000)
    lines = case.files[f.path]
    start = max(1, f.line - 40)
    content = "\n".join(lines[start - 1:f.line + 15])
    ask = (f"The weakness, as the security pack has it:\n{text or f.cwe_title}\n\nThe file {f.path}, from line {start}:\n```\n{cut(content, 10_000)}```\n\n"
           f"The finding ({f.cwe}, {f.severity}) at line {f.line}: {f.title}\nSource: {f.source}\nFlow:\n{f.flow}\nExploit: {f.exploit}")
    verdict = json_of(model.ask(VERIFY_PROMPT, ask, 400))
    if verdict is not None and isinstance(verdict.get("real"), bool):
        f.verified = verdict["real"]


SHOW_CALLEES = False


def same_kind(a: str | None, b: str | None) -> bool:
    """Two CWEs of one weakness: the same, or traced as one kind (CWE-79 and CWE-80)."""
    return a == b or (a in TRACED_CWES and TRACED_CWES.get(a) == TRACED_CWES.get(b))


def review(case: Case, arm: str, model: Model, pack: Pack | None, scanned: list[dict]) -> list[Finding]:
    """One arm's review of a case; the findings it keeps (a refuted one is not kept, as the review's summary leaves it out)."""
    hits = scanned if arm == "both" else []
    traced = [h for h in hits if h.get("kind", "code") == "code" and h.get("cwe") in TRACED_CWES]
    use_pack = pack if arm in ("pack", "both") else None
    findings = []
    for c in candidates(case, traced)[:MOST_READ]:
        context, shown = numbered(case.files[c.path], c.line - 60, c.line + 15, c.line)
        called = callers(case, c, shown)
        flags = [h for h in hits if h["path"] == c.path and abs(h["line"] - c.line) <= 3 and h.get("kind", "code") == "code"]
        answer = json_of(model.ask(ANALYZE_PROMPT, question(c, context, called, flags, callees(case, c, shown) if SHOW_CALLEES else ""), 1_500))
        if not answer or answer.get("vulnerable") is not True:
            continue
        f = ground(c, answer, shown, use_pack)
        if f is not None:
            findings.append(f)
    ranked = sorted(findings, key=lambda f: SEVERITY_RANK.get(f.severity, 3))
    for f in ranked[:MOST_VERIFIED]:
        verify(case, f, model, use_pack)
    kept = [f for f in ranked if f.verified is not False]
    if arm == "both":
        for h in hits:
            if h.get("kind", "code") == "code" and any(p.path == h["path"] and abs(p.line - h["line"]) <= 3 and p.verified is not False and same_kind(p.cwe, h.get("cwe"))
                                                       for p in ranked):
                continue
            kept.append(Finding(h["path"], h["line"], h.get("cwe") or "", "scanners"))
    return kept


# ---- Scoring.
def flagged(case: Case, findings: list[Finding]) -> dict[str, list]:
    """The case's units each finding of its weakness falls in."""
    out: dict[str, list] = {}
    for f in findings:
        m = re.match(r"CWE-(\d+)$", f.cwe or "")
        if not m or int(m.group(1)) not in case.family:
            continue
        unit = case.units.get(f.path, {}).get(f.line, "other")
        out.setdefault(unit, []).append(f"{f.path}:{f.line} {f.cwe} ({f.by})")
    return out


def score(cases: list[Case], flags: dict[str, dict]) -> dict:
    tp = fp = fn = tn = 0
    for case in cases:
        got = flags.get(case.id, {})
        for u in case.positive:
            tp, fn = (tp + 1, fn) if u in got else (tp, fn + 1)
        negatives = case.negative | {u for u in got if u not in case.positive}
        for u in negatives:
            fp, tn = (fp + 1, tn) if u in got else (fp, tn + 1)
    p, n = tp + fn, fp + tn
    return {"cases": len(cases), "tp": tp, "fp": fp, "fn": fn, "tn": tn, "precision": round(tp / (tp + fp), 3) if tp + fp else None,
            "recall": round(tp / p, 3) if p else None, "fpr": round(fp / n, 3) if n else None,
            "youden": round(tp / p - fp / n, 3) if p and n else None}


# ---- Arena: the throwaway person, and the run.
def browser():
    spec = importlib.util.spec_from_file_location("ft", ROOT / "deploy" / "scripts" / "functional-test.py")
    ft = importlib.util.module_from_spec(spec)
    argv, sys.argv = sys.argv, ["ft"]
    spec.loader.exec_module(ft)
    sys.argv = argv
    return ft


def sample(cases: list[Case], count: int, seed: int, suite: str) -> list[Case]:
    rng = random.Random(seed)
    chosen = []
    for category in sorted({c.category for c in cases}):
        mine = [c for c in cases if c.category == category]
        if suite == "owasp":
            real = [c for c in mine if c.positive]
            fake = [c for c in mine if c.negative]
            rng.shuffle(real)
            rng.shuffle(fake)
            chosen += real[:count - count // 2] + fake[:count // 2]
        else:
            rng.shuffle(mine)
            chosen += mine[:count]
    return chosen


def main() -> int:
    p = argparse.ArgumentParser()
    p.add_argument("--suite", required=True, choices=["owasp", "juliet-java", "juliet-csharp"])
    p.add_argument("--root", required=True, type=Path)
    p.add_argument("--count", type=int, default=12)
    p.add_argument("--seed", type=int, default=7)
    p.add_argument("--jobs", type=int, default=3)
    p.add_argument("--arms", default="scanners,model,pack,both")
    p.add_argument("--model", default="", help="the model asked (the person's default when not named)")
    p.add_argument("--scanners-image", default="arena-scanners")
    p.add_argument("--batch", type=int, default=300, help="files sent to the scanners at once")
    p.add_argument("--callees", action="store_true", help="also show the functions the code above the sink calls, defined further in the file")
    a = p.parse_args()
    arms = a.arms.split(",")
    global SHOW_CALLEES
    SHOW_CALLEES = a.callees
    out = HERE / f"security-review-{a.suite}{'-callees' if a.callees else ''}-results.json"
    signal.signal(signal.SIGTERM, lambda *_: sys.exit(143))

    cases = owasp_cases(a.root) if a.suite == "owasp" else juliet_cases(a.suite, a.root)
    for i, case in enumerate(cases):
        (read_owasp if a.suite == "owasp" else read_juliet)(case, i)
    chosen = sample(cases, a.count, a.seed, a.suite)
    print(f"{a.suite}: {len(cases)} cases in {len({c.category for c in cases})} categories; the model reads {len(chosen)}", flush=True)

    # The scanners, on every case.
    scanned: dict[str, list[dict]] = {c.id: [] for c in cases}
    health = None
    if "scanners" in arms or "both" in arms:
        scanners = Scanners(a.scanners_image)
        health = scanners.health
        try:
            owner = {path: c.id for c in cases for path in c.files}
            batch: dict = {}
            done = 0
            for c in cases:
                batch.update(c.files)
                if len(batch) >= a.batch or c is cases[-1]:
                    for h in scanners.scan(batch):
                        scanned[owner[h["path"]]].append(h)
                    done += len(batch)
                    batch = {}
                    print(f"  scanned {done} files", flush=True)
        finally:
            scanners.close()

    results = {"suite": a.suite, "seed": a.seed, "count": a.count, "scanners": health, "cases": len(cases), "read": len(chosen), "arms": {}}
    flags: dict[str, dict[str, dict]] = {}
    if "scanners" in arms:
        flags["scanners"] = {c.id: flagged(c, [Finding(h["path"], h["line"], h.get("cwe") or "", "scanners") for h in scanned[c.id]]) for c in cases}

    model_arms = [arm for arm in arms if arm != "scanners"]
    person = None
    if model_arms:
        ft = browser()
        password = ft.env("ADMIN_PASSWORD")
        admin = ft.Browser()
        assert admin.login(ft.env("ADMIN_USERNAME", "admin"), password), "the admin could not sign in"
        name = "seceval" + uuid.uuid4().hex[:6]
        code, person = admin.app("POST", "/api/admin/people", {"userName": name, "email": f"{name}@example.test", "displayName": "Security review eval",
                                                               "credits": {"chat": 5, "api": 50}})
        assert code in (200, 201) and person.get("apiKey"), (code, {k: v for k, v in person.items() if k not in ("password", "apiKey")})
        pack = None
        try:
            code, config = admin.app("GET", "/api/chat/config")
            model_name = a.model or config["model"]
            model = Model(ft.u("gateway", "/v1/chat/completions"), person["apiKey"], model_name)
            pack = Pack(ft.env("ARGUS_KEY"), ft.env("ADMIN_EMAIL"))
            results["model"] = model_name
            started = time.time()
            for arm in model_arms:
                flags[arm] = {}

                def one(case, arm=arm):
                    return case.id, flagged(case, review(case, arm, model, pack, scanned[case.id]))
                with concurrent.futures.ThreadPoolExecutor(a.jobs) as pool:
                    for k, (cid, got) in enumerate(pool.map(one, chosen), 1):
                        flags[arm][cid] = got
                        if k % 10 == 0 or k == len(chosen):
                            print(f"  {arm}: {k}/{len(chosen)} ({model.asked} asked, {model.cached} answered before, {time.time() - started:.0f} s)", flush=True)
            results["asked"], results["answered_before"], results["refused"] = model.asked, model.cached, model.refused
            results["tokens"] = {"prompt": model.tokens[0], "completion": model.tokens[1]}
            results["seconds"] = round(time.time() - started)
        finally:
            if pack is not None:
                pack.close()
            admin.app("DELETE", f"/api/admin/people/{person['id']}")

    for arm in arms:
        on = cases if arm == "scanners" else chosen
        per = {cat: score([c for c in on if c.category == cat], flags[arm]) for cat in sorted({c.category for c in on})}
        results["arms"][arm] = {"all": score(on, flags[arm]), "categories": per}
        if arm == "scanners":
            # The scanners on the cases the model read too, to set beside the model's arms.
            results["arms"]["scanners-read"] = {"all": score(chosen, flags[arm]),
                                                "categories": {cat: score([c for c in chosen if c.category == cat], flags[arm]) for cat in sorted({c.category for c in chosen})}}
    results["flagged"] = {arm: {cid: got for cid, got in per.items() if got} for arm, per in flags.items()}
    out.write_text(json.dumps(results, indent=1, ensure_ascii=False))
    for arm, r in results["arms"].items():
        s = r["all"]
        print(f"{arm:14} cases {s['cases']:5}  TP {s['tp']:4} FP {s['fp']:4} FN {s['fn']:4} TN {s['tn']:4}  precision {s['precision']}  recall {s['recall']}  "
              f"FPR {s['fpr']}  score {s['youden']}")
    return 0


if __name__ == "__main__":
    sys.exit(main())
