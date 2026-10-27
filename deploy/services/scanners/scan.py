"""The security scanners (compose profile "scanners"): a commit Argus sends, scanned by opengrep's engine with Arena's
own rules (rules/), Bandit, gitleaks and Trivy's checks of configuration, and what they found merged.

Only Argus reaches it (an internal network with no way out), and it reaches nothing: no scanner fetches anything, so
the rules, Bandit's checks, gitleaks' patterns and Trivy's checks are those built into the image. Nothing it is sent is
run: the scanners read the code, and git only checks it out (no hooks, symbolic links as plain files).

  POST /scan?commit=<sha>[&ref=<name>][&tools=opengrep,bandit,gitleaks,trivy]
      The body is the commit's files as a tar (Content-Type: application/x-tar), or a git bundle of its history
      (application/x-git-bundle, with ref naming the bundle's ref), when gitleaks is to read the history too.
      Answers {"commit", "history", "tools": [{name, version, ran, seconds, findings, error}], "findings": [...],
      "more": n}. A finding is {tools, rules, kind (code, secret or config), path, line, end_line, cwe, cwes,
      severity (critical, high, medium or low), confidence, title, message, code, commit, date}; a line of code that
      several scanners (or rules) found is one finding, with each of them named. A secret's text is never in the
      answer, nor the line it is on.
  GET /health
      {"ok": true, "tools": {name: version}, "rules": n, "busy": bool}

One scan at a time (SCANNERS_SLOTS): the next waits, at most SCANNERS_WAIT seconds, and is told to come back later.
`python scan.py --build-rules` makes the engine's rules.json from the rule files, and `python scan.py --self-test`
runs the engine on the rules' own examples (each `ruleid:` line found, each `ok:` line not), as the image's build
does.
"""

import http.server
import io
import json
import os
import re
import shutil
import signal
import subprocess
import sys
import tarfile
import tempfile
import threading
import time
import urllib.parse

PORT = int(os.environ.get("SCANNERS_PORT", "7800"))
WORK = os.environ.get("SCANNERS_WORK", "/work")
RULES = os.environ.get("SCANNERS_RULES", "/opt/rules")
ENGINE = os.environ.get("SCANNERS_ENGINE", "/opt/opengrep/opengrep-core")
SLOTS = threading.BoundedSemaphore(max(1, int(os.environ.get("SCANNERS_SLOTS", "1"))))
WAIT = int(os.environ.get("SCANNERS_WAIT", "900"))
# The most one scan is sent (a tar or a bundle), and the most a tar unpacks to.
MOST_BYTES = int(os.environ.get("SCANNERS_MOST_MB", "1024")) * 1024 * 1024
MOST_FILES = 200_000
# The longest one scanner may take, and the most findings one answer carries (the most severe first).
TOOL_TIMEOUT = int(os.environ.get("SCANNERS_TOOL_TIMEOUT", "900"))
MOST_FINDINGS = 2000
THREADS = str(max(1, int(os.environ.get("SCANNERS_THREADS", "2"))))
TOOLS = ("opengrep", "bandit", "gitleaks", "trivy")
SEVERITY_RANK = {"critical": 0, "high": 1, "medium": 2, "low": 3}
CONFIDENCE_RANK = {"high": 0, "medium": 1, "low": 2}

# The files each language's rules read, by extension, and what is never read: other people's code kept here
# (vendored packages, built bundles) and minified files.
ANALYZERS = {
    ".py": "python", ".js": "js", ".jsx": "js", ".mjs": "js", ".cjs": "js", ".ts": "ts", ".tsx": "ts", ".mts": "ts",
    ".cts": "ts", ".cs": "csharp", ".java": "java", ".go": "go", ".php": "php", ".rb": "ruby",
}
SKIPPED = re.compile(
    r"(^|/)(node_modules|bower_components|vendor|third_party|third-party|site-packages|\.venv|venv|\.git|dist|"
    r"packages|Pods|\.yarn|\.pnpm-store)/|\.min\.(js|css)$|\.bundle\.js$")
MOST_FILE_BYTES = 1_000_000

SHA = re.compile(r"\A[0-9a-f]{40}([0-9a-f]{24})?\Z")
REF = re.compile(r"\Arefs/[A-Za-z0-9_][A-Za-z0-9_./-]{0,200}\Z")


def log(*parts):
    print(time.strftime("%Y-%m-%dT%H:%M:%S"), *parts, file=sys.stderr, flush=True)


def run(args, cwd, timeout=TOOL_TIMEOUT, env=None):
    """A scanner run in its own process group, killed with all it started at its time limit."""
    proc = subprocess.Popen(args, cwd=cwd, stdin=subprocess.DEVNULL, stdout=subprocess.PIPE, stderr=subprocess.PIPE,
                            env={**os.environ, **(env or {})}, start_new_session=True)
    try:
        out, err = proc.communicate(timeout=timeout)
    except subprocess.TimeoutExpired:
        os.killpg(proc.pid, signal.SIGKILL)
        proc.communicate()
        raise TimeoutError(f"{os.path.basename(args[0])} did not finish within {timeout} s")
    return proc.returncode, out.decode("utf-8", "replace"), err.decode("utf-8", "replace")


_VERSIONS = {}


def versions():
    """Each scanner's version, as it says (asked once)."""
    if _VERSIONS:
        return _VERSIONS
    found = _VERSIONS
    for name, args in (("opengrep", [ENGINE, "-version"]), ("bandit", ["bandit", "--version"]),
                       ("gitleaks", ["gitleaks", "version"]), ("trivy", ["trivy", "--version"])):
        try:
            _, out, err = run(args, "/", timeout=30)
            match = re.search(r"\d+\.\d+\.\d+", out + err)
            found[name] = match.group(0) if match else "?"
        except (OSError, TimeoutError):
            found[name] = None
    return found


def rule_count():
    try:
        with open(os.path.join(RULES, "rules.json"), encoding="utf-8") as f:
            return len(json.load(f)["rules"])
    except (OSError, ValueError, KeyError):
        return 0


# --- the checkout ------------------------------------------------------------------------------------------------

def unpack_tar(body, src):
    """A commit's files from a tar: regular files only (no links or devices), each inside src, within the limits."""
    total = files = 0
    with tarfile.open(fileobj=body, mode="r|*") as tar:
        for member in tar:
            if not member.isreg():
                continue
            name = member.name
            while name.startswith("./"):
                name = name[2:]
            if not name or name.startswith("/") or ".." in name.split("/"):
                continue
            files += 1
            total += member.size
            if files > MOST_FILES or total > MOST_BYTES:
                raise ValueError("the commit is larger than a scan reads")
            target = os.path.join(src, name)
            os.makedirs(os.path.dirname(target), exist_ok=True)
            with tar.extractfile(member) as data, open(target, "wb") as out:
                shutil.copyfileobj(data, out)


def git(src, *args):
    env = {"GIT_CONFIG_NOSYSTEM": "1", "GIT_CONFIG_GLOBAL": "/dev/null", "GIT_TERMINAL_PROMPT": "0", "HOME": src}
    code, out, err = run(["git", "-c", "core.hooksPath=/dev/null", "-c", "core.symlinks=false", "-c", "core.fsmonitor=false",
                          "-c", "protocol.allow=never", "-c", "protocol.file.allow=always", *args], src, timeout=600, env=env)
    if code != 0:
        raise ValueError(f"git {args[0]} failed: {err.strip()[-300:]}")
    return out


def unpack_bundle(bundle, ref, commit, src):
    """A commit and its history from a git bundle, checked out (symbolic links as plain files, no hooks)."""
    os.makedirs(src)
    git(src, "init", "--quiet")
    git(src, "fetch", "--quiet", "--no-tags", bundle, f"+{ref}:refs/heads/scan")
    git(src, "checkout", "--quiet", "--force", commit)


def targets(src):
    """The files the rules read, as the engine takes them: [["CodeTarget", {path, analyzer, products}]]."""
    found = []
    for root, dirs, names in os.walk(src):
        dirs[:] = [d for d in dirs if d != ".git"]
        for name in names:
            path = os.path.relpath(os.path.join(root, name), src)
            analyzer = ANALYZERS.get(os.path.splitext(name)[1].lower())
            if analyzer is None or SKIPPED.search(path):
                continue
            try:
                if os.path.getsize(os.path.join(src, path)) > MOST_FILE_BYTES:
                    continue
            except OSError:
                continue
            found.append(["CodeTarget", {"path": path, "analyzer": analyzer, "products": ["sast"]}])
    return found


def line_of(src, path, line):
    """The text of a file's line, as the finding cites it (300 characters at most)."""
    try:
        with open(os.path.join(src, path), encoding="utf-8", errors="replace") as f:
            for n, text in enumerate(f, 1):
                if n == line:
                    return text.rstrip("\n")[:300]
    except OSError:
        pass
    return ""


# --- the scanners ------------------------------------------------------------------------------------------------

def first_sentence(text):
    text = " ".join((text or "").split())
    match = re.match(r"(.+?[.:])(\s|$)", text)
    return (match.group(1) if match else text)[:200].rstrip(":.")


def opengrep(src, scratch):
    files = targets(src)
    if not files:
        return []
    listing = os.path.join(scratch, "targets.json")
    with open(listing, "w", encoding="utf-8") as f:
        json.dump(files, f)
    code, out, err = run([ENGINE, "-json", "-rules", os.path.join(RULES, "rules.json"), "-targets", listing, "-j", THREADS,
                          "-timeout", "10", "-timeout_threshold", "3", "-max_target_bytes", str(MOST_FILE_BYTES),
                          "-max_match_per_file", "200", "-fast"], src)
    start = out.find("{")
    if start < 0:
        raise RuntimeError(f"opengrep answered nothing (exit {code}): {err.strip()[-300:]}")
    answer = json.loads(out[start:])
    broken = [e for e in answer.get("errors", []) if "Rule" in e.get("error_type", "")]
    if broken:
        raise RuntimeError(f"opengrep could not read rule {broken[0].get('rule_id')}: {broken[0].get('message')}")
    found = []
    for r in answer.get("results", []):
        meta = r.get("extra", {}).get("metadata", {}) or {}
        severity = meta.get("severity") or {"ERROR": "high", "WARNING": "medium"}.get(r.get("extra", {}).get("severity"), "low")
        found.append({
            "tool": "opengrep", "rule": r["check_id"].rsplit(".", 1)[-1], "kind": "code", "path": r["path"],
            "line": r["start"]["line"], "end_line": r["end"]["line"], "cwe": meta.get("cwe", ""), "severity": severity,
            "confidence": meta.get("confidence", "medium"), "message": " ".join(r.get("extra", {}).get("message", "").split()),
        })
    return found


def bandit(src, scratch):
    out_file = os.path.join(scratch, "bandit.json")
    code, _, err = run(["bandit", "-r", ".", "-f", "json", "-o", out_file, "-q", "--severity-level", "medium",
                        "--confidence-level", "medium", "-x",
                        "./.git,*/node_modules/*,*/vendor/*,*/site-packages/*,*/.venv/*,*/venv/*,*/third_party/*"], src)
    if not os.path.exists(out_file):
        raise RuntimeError(f"bandit wrote no report (exit {code}): {err.strip()[-300:]}")
    with open(out_file, encoding="utf-8") as f:
        answer = json.load(f)
    found = []
    for r in answer.get("results", []):
        cwe = r.get("issue_cwe", {}).get("id")
        found.append({
            "tool": "bandit", "rule": f"{r.get('test_id')} {r.get('test_name')}", "kind": "code",
            "path": os.path.normpath(r["filename"]), "line": r["line_number"],
            "end_line": max(r.get("line_range") or [r["line_number"]]), "cwe": f"CWE-{cwe}" if cwe else "",
            "severity": r.get("issue_severity", "MEDIUM").lower(), "confidence": r.get("issue_confidence", "MEDIUM").lower(),
            "message": r.get("issue_text", ""),
        })
    return found


def gitleaks(src, scratch, history, commit):
    out_file = os.path.join(scratch, "gitleaks.json")
    mode = ["git", f"--log-opts={commit}"] if history else ["dir"]
    code, _, err = run(["gitleaks", *mode, ".", "--report-format", "json", "--report-path", out_file, "--redact",
                        "--no-banner", "--exit-code", "0", "--log-level", "error", "--max-target-megabytes", "5"], src,
                       env={"HOME": scratch})
    if not os.path.exists(out_file):
        raise RuntimeError(f"gitleaks wrote no report (exit {code}): {err.strip()[-300:]}")
    with open(out_file, encoding="utf-8") as f:
        answer = json.load(f) or []
    found = []
    for r in answer:
        # Never the secret: its rule, where, and in which commit (the history's), only.
        rule, commit = r.get("RuleID", "secret"), r.get("Commit") or None
        found.append({
            "tool": "gitleaks", "rule": rule, "kind": "secret", "path": os.path.normpath(r["File"]),
            "line": r.get("StartLine", 0), "end_line": r.get("EndLine", r.get("StartLine", 0)), "cwe": "CWE-798",
            "severity": "high", "confidence": "medium",
            "title": f"A secret ({rule}) in {'the history, commit ' + commit[:12] if commit else 'the code'}",
            "message": f"{r.get('Description') or 'A secret.'} Revoke it and make a new one: whoever could read the "
                       f"repository has it, and taking it out of the code does not take it back.",
            "commit": commit, "date": r.get("Date") or None,
        })
    return found


def trivy(src, scratch):
    out_file = os.path.join(scratch, "trivy.json")
    code, _, err = run(["trivy", "config", "--format", "json", "--output", out_file, "--skip-check-update",
                        "--skip-version-check", "--disable-telemetry", "--quiet", "--cache-dir", os.path.join(scratch, "trivy"),
                        "--severity", "MEDIUM,HIGH,CRITICAL", "--skip-dirs", "**/node_modules", "--skip-dirs", "**/vendor",
                        "--timeout", f"{TOOL_TIMEOUT}s", "."], src, env={"HOME": scratch, "TRIVY_NO_PROGRESS": "true"})
    if not os.path.exists(out_file):
        raise RuntimeError(f"trivy wrote no report (exit {code}): {err.strip()[-300:]}")
    with open(out_file, encoding="utf-8") as f:
        answer = json.load(f)
    found = []
    for result in answer.get("Results") or []:
        for m in result.get("Misconfigurations") or []:
            if m.get("Status") != "FAIL":
                continue
            cause = m.get("CauseMetadata") or {}
            line = cause.get("StartLine") or 1
            found.append({
                "tool": "trivy", "rule": f"{m.get('AVDID') or m.get('ID')} {m.get('Title', '')}".strip(), "kind": "config",
                "path": os.path.normpath(result.get("Target", "")), "line": line, "end_line": cause.get("EndLine") or line,
                "cwe": "", "severity": (m.get("Severity") or "MEDIUM").lower(), "confidence": "high",
                "message": " ".join(f"{m.get('Message') or m.get('Description') or ''} {m.get('Resolution') or ''}".split()),
            })
    return found


def merge(found, src):
    """One finding a place and weakness: what several scanners (or several rules) found at the same line, folded."""
    merged = {}
    # Arena's rules first, so a place's weakness and words are theirs when another scanner found it too.
    for f in sorted(found, key=lambda f: f["tool"] != "opengrep"):
        # Code: a line is one finding, whichever weakness each scanner calls it. Configuration and secrets: each check.
        key = (f["path"], f["line"], f.get("commit"), "" if f["kind"] == "code" else f["rule"])
        m = merged.get(key)
        if m is None:
            merged[key] = m = {
                "tools": [], "rules": [], "cwes": [], "kind": f["kind"], "path": f["path"], "line": f["line"],
                "end_line": f["end_line"], "cwe": f["cwe"], "severity": f["severity"], "confidence": f["confidence"],
                "title": f.get("title") or first_sentence(f["message"]), "message": f["message"][:2000],
                "code": "" if f["kind"] == "secret" or f.get("commit") else line_of(src, f["path"], f["line"]),
                "commit": f.get("commit"), "date": f.get("date"),
            }
        if f["tool"] not in m["tools"]:
            m["tools"].append(f["tool"])
        if f["rule"] not in m["rules"]:
            m["rules"].append(f["rule"])
        if f["cwe"] and f["cwe"] not in m["cwes"]:
            m["cwes"].append(f["cwe"])
        m["cwe"] = m["cwe"] or f["cwe"]
        if SEVERITY_RANK.get(f["severity"], 4) < SEVERITY_RANK.get(m["severity"], 4):
            m["severity"] = f["severity"]
        if CONFIDENCE_RANK.get(f["confidence"], 3) < CONFIDENCE_RANK.get(m["confidence"], 3):
            m["confidence"] = f["confidence"]
    # Found by more than one scanner is surer.
    for m in merged.values():
        if len(m["tools"]) > 1:
            m["confidence"] = "high"
    return sorted(merged.values(), key=lambda m: (SEVERITY_RANK.get(m["severity"], 4), CONFIDENCE_RANK.get(m["confidence"], 3),
                                                   m["path"], m["line"]))


def scan(src, scratch, commit, history, tools):
    known = versions()
    report, found = [], []
    for name in TOOLS:
        if name not in tools:
            continue
        started = time.monotonic()
        entry = {"name": name, "version": known.get(name), "ran": False, "seconds": 0, "findings": 0, "error": None}
        try:
            if known.get(name) is None:
                raise RuntimeError(f"{name} is not installed here")
            hits = {"opengrep": lambda: opengrep(src, scratch), "bandit": lambda: bandit(src, scratch),
                    "gitleaks": lambda: gitleaks(src, scratch, history, commit), "trivy": lambda: trivy(src, scratch)}[name]()
            entry.update(ran=True, findings=len(hits))
            found += hits
        except (OSError, ValueError, RuntimeError, TimeoutError, KeyError) as exc:
            entry["error"] = str(exc)[:500]
            log(f"{name} on {commit[:12]}: {exc}")
        entry["seconds"] = round(time.monotonic() - started, 1)
        report.append(entry)
    findings = merge(found, src)
    return {"commit": commit, "history": history and "gitleaks" in tools, "tools": report,
            "findings": findings[:MOST_FINDINGS], "more": max(0, len(findings) - MOST_FINDINGS)}


# --- the server --------------------------------------------------------------------------------------------------

class Handler(http.server.BaseHTTPRequestHandler):
    server_version = "arena-scanners"
    protocol_version = "HTTP/1.1"

    def log_message(self, fmt, *args):
        log(self.address_string(), fmt % args)

    def answer(self, status, body):
        data = json.dumps(body).encode()
        self.send_response(status)
        self.send_header("Content-Type", "application/json")
        self.send_header("Content-Length", str(len(data)))
        self.end_headers()
        self.wfile.write(data)

    def do_GET(self):
        if self.path != "/health":
            return self.answer(404, {"error": "not found"})
        busy = not SLOTS.acquire(blocking=False)
        if not busy:
            SLOTS.release()
        self.answer(200, {"ok": True, "tools": versions(), "rules": rule_count(), "busy": busy})

    def do_POST(self):
        url = urllib.parse.urlparse(self.path)
        if url.path != "/scan":
            return self.answer(404, {"error": "not found"})
        query = urllib.parse.parse_qs(url.query)
        commit = (query.get("commit") or [""])[0]
        ref = (query.get("ref") or [""])[0]
        tools = [t for t in ",".join(query.get("tools") or [",".join(TOOLS)]).split(",") if t]
        kind = (self.headers.get("Content-Type") or "").split(";")[0].strip()
        size = int(self.headers.get("Content-Length") or "-1")
        if not SHA.match(commit):
            return self.answer(400, {"error": "commit is a whole commit id"})
        if any(t not in TOOLS for t in tools):
            return self.answer(400, {"error": f"tools are some of {', '.join(TOOLS)}"})
        if kind not in ("application/x-tar", "application/x-git-bundle") or (kind == "application/x-git-bundle" and not REF.match(ref)):
            return self.answer(400, {"error": "send the commit's files as a tar, or a git bundle with its ref"})
        if size < 0 or size > MOST_BYTES:
            return self.answer(413, {"error": f"a scan is sent {MOST_BYTES // 1024 // 1024} MB at most"})
        if not SLOTS.acquire(timeout=WAIT):
            self.rfile.read(size)
            return self.answer(503, {"error": "the scanners are busy with other scans: try again later"})
        scratch = tempfile.mkdtemp(dir=WORK)
        try:
            src = os.path.join(scratch, "src")
            started = time.monotonic()
            if kind == "application/x-tar":
                os.makedirs(src)
                unpack_tar(LimitedReader(self.rfile, size), src)
            else:
                bundle = os.path.join(scratch, "commit.bundle")
                with open(bundle, "wb") as out:
                    shutil.copyfileobj(LimitedReader(self.rfile, size), out)
                unpack_bundle(bundle, ref, commit, src)
                os.remove(bundle)
            status, result = 200, scan(src, scratch, commit, kind == "application/x-git-bundle", tools)
            log(f"scanned {commit[:12]} in {time.monotonic() - started:.0f} s: {len(result['findings'])} findings")
        except (ValueError, tarfile.TarError) as exc:
            status, result = 400, {"error": str(exc)[:500]}
        finally:
            # Nothing of the commit is kept: its folder goes before the answer does.
            shutil.rmtree(scratch, ignore_errors=True)
            SLOTS.release()
        self.answer(status, result)


class LimitedReader(io.RawIOBase):
    """The request's body, no further than its Content-Length."""

    def __init__(self, raw, size):
        self.raw, self.left = raw, size

    def readable(self):
        return True

    def readinto(self, buffer):
        if self.left <= 0:
            return 0
        data = self.raw.read(min(len(buffer), self.left, 1024 * 1024))
        self.left -= len(data)
        buffer[:len(data)] = data
        return len(data)


# --- the rules' own examples -------------------------------------------------------------------------------------

def self_test():
    """The engine, as the scanner runs it, on each rule file's examples: every `ruleid:` line found, no `ok:` line."""
    scratch = tempfile.mkdtemp()
    failures = 0
    try:
        found = opengrep(RULES, scratch)
        hits = {(f["path"], f["line"], f["rule"]) for f in found}
        for name in sorted(os.listdir(RULES)):
            if os.path.splitext(name)[1].lower() not in ANALYZERS:
                continue
            with open(os.path.join(RULES, name), encoding="utf-8") as f:
                lines = f.read().split("\n")
            for n, text in enumerate(lines, 1):
                match = re.search(r"(ruleid|ok):\s*([\w\-, ]+)", text)
                if not match:
                    continue
                for rule in (r.strip() for r in match.group(2).split(",")):
                    present = (name, n + 1, rule) in hits
                    if present != (match.group(1) == "ruleid"):
                        failures += 1
                        print(f"{name}:{n + 1}: {rule} {'missed' if match.group(1) == 'ruleid' else 'found where it should not be'}")
        print(f"{rule_count()} rules, {len(found)} findings on their examples, {failures} wrong")
    finally:
        shutil.rmtree(scratch, ignore_errors=True)
    return 1 if failures else 0


def build_rules():
    """rules.json from the rule files: one list, YAML's anchors resolved (the engine reads JSON, not YAML's references)."""
    import yaml
    rules = []
    for name in sorted(os.listdir(RULES)):
        if name.endswith(".yaml"):
            with open(os.path.join(RULES, name), encoding="utf-8") as f:
                rules += yaml.safe_load(f)["rules"]
    ids = [r["id"] for r in rules]
    duplicates = sorted({i for i in ids if ids.count(i) > 1})
    if duplicates:
        sys.exit(f"rule ids used twice: {', '.join(duplicates)}")
    with open(os.path.join(RULES, "rules.json"), "w", encoding="utf-8") as f:
        json.dump({"rules": rules}, f)
    print(f"{len(rules)} rules")


def main():
    if sys.argv[1:] == ["--self-test"]:
        sys.exit(self_test())
    if sys.argv[1:] == ["--build-rules"]:
        return build_rules()
    os.makedirs(WORK, exist_ok=True)
    server = http.server.ThreadingHTTPServer(("0.0.0.0", PORT), Handler)
    log(f"scanners on :{PORT}: {versions()}, {rule_count()} rules")
    server.serve_forever()


if __name__ == "__main__":
    main()
