"""deploy/services/scanners/scan.py: what a scan unpacks, which files the rules read, how findings are merged, that a
secret's text never comes back, and the HTTP around it; the scanners themselves are stubbed (the image's build runs
the real engine on the rules' own examples)."""
from __future__ import annotations

import http.client
import importlib.util
import io
import json
import os
import shutil
import tarfile
import tempfile
import threading
import unittest
import urllib.error
import urllib.request
from http.server import ThreadingHTTPServer
from pathlib import Path
from unittest import mock

REPO = Path(__file__).resolve().parents[2]
spec = importlib.util.spec_from_file_location("scanners_scan", REPO / "deploy/services/scanners/scan.py")
scan = importlib.util.module_from_spec(spec)
spec.loader.exec_module(scan)

SHA = "a" * 40


def tar_of(members: list[tuple[tarfile.TarInfo, bytes | None]]) -> bytes:
    out = io.BytesIO()
    with tarfile.open(fileobj=out, mode="w") as tar:
        for info, data in members:
            if data is not None:
                info.size = len(data)
            tar.addfile(info, io.BytesIO(data) if data is not None else None)
    return out.getvalue()


def regular(name: str, text: str = "x = 1\n") -> tuple[tarfile.TarInfo, bytes]:
    return tarfile.TarInfo(name), text.encode()


class Unpacking(unittest.TestCase):
    def setUp(self):
        self.dir = tempfile.mkdtemp()
        self.addCleanup(shutil.rmtree, self.dir, True)

    def test_Only_regular_files_inside_the_folder_are_unpacked(self):
        link = tarfile.TarInfo("app/passwd")
        link.type, link.linkname = tarfile.SYMTYPE, "/etc/passwd"
        hard = tarfile.TarInfo("app/hard")
        hard.type, hard.linkname = tarfile.LNKTYPE, "app/views.py"
        body = tar_of([regular("./app/views.py"), regular(".github/workflows/ci.yml", "on: push\n"), regular("../escape.py"),
                       regular("/abs.py"), (link, None), (hard, None)])
        scan.unpack_tar(io.BytesIO(body), self.dir)
        found = sorted(str(p.relative_to(self.dir)) for p in Path(self.dir).rglob("*") if p.is_file())
        self.assertEqual([".github/workflows/ci.yml", "app/views.py"], found)
        self.assertFalse(os.path.exists(os.path.join(os.path.dirname(self.dir), "escape.py")))

    def test_A_commit_larger_than_a_scan_reads_is_refused(self):
        with mock.patch.object(scan, "MOST_BYTES", 10):
            with self.assertRaisesRegex(ValueError, "larger than a scan reads"):
                scan.unpack_tar(io.BytesIO(tar_of([regular("a.py", "x" * 20)])), self.dir)

    def test_The_rules_read_source_files_and_not_vendored_built_or_minified_ones(self):
        for name in ("app/views.py", "web/src/App.tsx", "Api/Program.cs", "node_modules/x/index.js", "vendor/lib.go",
                     "web/dist/app.js", "web/app.min.js", "README.md", ".git/config", "go/main.go"):
            path = Path(self.dir, name)
            path.parent.mkdir(parents=True, exist_ok=True)
            path.write_text("x\n")
        Path(self.dir, "big.py").write_text("x" * (scan.MOST_FILE_BYTES + 1))
        found = {t[1]["path"]: t[1]["analyzer"] for t in scan.targets(self.dir)}
        self.assertEqual({"app/views.py": "python", "web/src/App.tsx": "ts", "Api/Program.cs": "csharp", "go/main.go": "go"}, found)
        self.assertTrue(all(t[0] == "CodeTarget" and t[1]["products"] == ["sast"] for t in scan.targets(self.dir)))


class Merging(unittest.TestCase):
    def finding(self, tool, rule, line=10, cwe="CWE-89", severity="high", confidence="medium", kind="code", **extra):
        return {"tool": tool, "rule": rule, "kind": kind, "path": "app/views.py", "line": line, "end_line": line, "cwe": cwe,
                "severity": severity, "confidence": confidence, "message": f"{rule} says so. Fix it.", **extra}

    def test_A_line_two_scanners_found_is_one_finding_in_Arenas_words_and_surer(self):
        merged = scan.merge([self.finding("bandit", "B608 hardcoded_sql_expressions", severity="medium", cwe="CWE-89"),
                             self.finding("opengrep", "python-sql-built-from-strings"),
                             self.finding("opengrep", "python-weak-hash", line=20, cwe="CWE-328", severity="low", confidence="low")], "/nowhere")
        self.assertEqual(2, len(merged))
        first = merged[0]
        self.assertEqual(["opengrep", "bandit"], first["tools"])
        self.assertEqual("python-sql-built-from-strings says so", first["title"])
        self.assertEqual(("high", "high", "CWE-89"), (first["severity"], first["confidence"], first["cwe"]))
        self.assertEqual("low", merged[1]["severity"])

    def test_Different_weaknesses_on_a_line_keep_every_CWE_and_each_configuration_check_stays_apart(self):
        merged = scan.merge([self.finding("opengrep", "python-code-from-data", cwe="CWE-95"),
                             self.finding("bandit", "B307 eval", cwe="CWE-78"),
                             self.finding("trivy", "AVD-KSV-0017 privileged", kind="config", cwe=""),
                             self.finding("trivy", "AVD-KSV-0014 read-only root", kind="config", cwe="")], "/nowhere")
        code = [m for m in merged if m["kind"] == "code"]
        self.assertEqual(1, len(code))
        self.assertEqual(("CWE-95", ["CWE-95", "CWE-78"]), (code[0]["cwe"], code[0]["cwes"]))
        self.assertEqual(2, len([m for m in merged if m["kind"] == "config"]))

    def test_A_secret_comes_back_without_its_text_or_its_line(self):
        work = tempfile.mkdtemp()
        self.addCleanup(shutil.rmtree, work, True)
        report = [{"RuleID": "aws-access-token", "Description": "An AWS key.", "File": "app/settings.py", "StartLine": 1,
                   "EndLine": 1, "Secret": "AKIAREALLYSECRETXYZ", "Match": "key = AKIAREALLYSECRETXYZ", "Commit": "b" * 40,
                   "Date": "2026-10-27T00:00:00Z", "Author": "Someone", "Email": "someone@example.com"}]
        Path(work, "app").mkdir()
        Path(work, "app/settings.py").write_text("key = AKIAREALLYSECRETXYZ\n")

        def fake_run(args, cwd, timeout=0, env=None):
            Path(args[args.index("--report-path") + 1]).write_text(json.dumps(report))
            return 0, "", ""

        with mock.patch.object(scan, "run", fake_run):
            found = scan.gitleaks(work, work, True, "b" * 40)
        merged = scan.merge(found, work)
        text = json.dumps(merged)
        self.assertNotIn("AKIAREALLYSECRETXYZ", text)
        self.assertNotIn("someone@example.com", text)
        self.assertEqual(("secret", "CWE-798", "", "b" * 40), (merged[0]["kind"], merged[0]["cwe"], merged[0]["code"], merged[0]["commit"]))
        self.assertIn("history, commit bbbbbbbbbbbb", merged[0]["title"])


class Rules(unittest.TestCase):
    def test_Every_rule_names_its_weakness_Arenas_severity_and_is_tested_by_its_examples(self):
        rules_dir = REPO / "deploy/services/scanners/rules"
        work = tempfile.mkdtemp()
        self.addCleanup(shutil.rmtree, work, True)
        for f in rules_dir.iterdir():
            shutil.copy(f, work)
        with mock.patch.object(scan, "RULES", work):
            scan.build_rules()
        rules = json.loads(Path(work, "rules.json").read_text())["rules"]
        self.assertGreater(len(rules), 80)
        examples = "".join(p.read_text() for p in rules_dir.iterdir() if p.suffix in scan.ANALYZERS)
        for r in rules:
            meta = r["metadata"]
            self.assertRegex(meta["cwe"], r"^CWE-\d+$", r["id"])
            self.assertIn(meta["severity"], scan.SEVERITY_RANK, r["id"])
            self.assertIn(meta["confidence"], scan.CONFIDENCE_RANK, r["id"])
            self.assertIn(f"ruleid: {r['id']}", examples.replace(", ", ", ruleid: "), f"{r['id']} has no weak example")
            self.assertNotIn("$1", r["message"], f"{r['id']}: the engine fills $1 in from a regex")


class Serving(unittest.TestCase):
    def setUp(self):
        self.work = tempfile.mkdtemp()
        self.addCleanup(shutil.rmtree, self.work, True)
        patcher = mock.patch.multiple(scan, WORK=self.work, SLOTS=threading.BoundedSemaphore(1), WAIT=1)
        patcher.start()
        self.addCleanup(patcher.stop)
        self.server = ThreadingHTTPServer(("127.0.0.1", 0), scan.Handler)
        threading.Thread(target=self.server.serve_forever, daemon=True).start()
        self.addCleanup(self.server.server_close)
        self.addCleanup(self.server.shutdown)
        self.url = f"http://127.0.0.1:{self.server.server_address[1]}"

    def post(self, query, body, kind="application/x-tar"):
        req = urllib.request.Request(f"{self.url}/scan?{query}", data=body, method="POST", headers={"Content-Type": kind})
        try:
            with urllib.request.urlopen(req, timeout=10) as r:
                return r.status, json.loads(r.read())
        except urllib.error.HTTPError as e:
            return e.code, json.loads(e.read())

    def test_A_tar_is_scanned_by_the_tools_asked_and_its_folder_removed_after(self):
        seen = {}

        def fake_scan(src, scratch, commit, history, tools):
            seen.update(files=sorted(os.listdir(os.path.join(src, "app"))), commit=commit, history=history, tools=tools)
            return {"commit": commit, "history": history, "tools": [], "findings": [], "more": 0}

        with mock.patch.object(scan, "scan", fake_scan):
            status, body = self.post(f"commit={SHA}&tools=opengrep,bandit", tar_of([regular("app/views.py")]))
        self.assertEqual(200, status, body)
        self.assertEqual({"files": ["views.py"], "commit": SHA, "history": False, "tools": ["opengrep", "bandit"]}, seen)
        self.assertEqual([], os.listdir(self.work))

    def test_A_tars_padding_is_read_so_a_kept_connection_answers_the_next_scan(self):
        # tarfile reads a stream's first block alone, then whole records: a tar whose end falls early in its last record
        # (one file of 9,216 bytes) leaves 9,728 bytes of padding unread unless the body is read to its end. Unread, they
        # began the next request on the connection, or reset it as the server closed.
        def fake_scan(src, scratch, commit, history, tools):
            return {"commit": commit, "history": history, "tools": [], "findings": [], "more": 0}

        body = tar_of([regular("app/a.py", "x" * 9216)])
        self.assertEqual(20480, len(body))
        conn = http.client.HTTPConnection("127.0.0.1", self.server.server_address[1], timeout=10)
        self.addCleanup(conn.close)
        with mock.patch.object(scan, "scan", fake_scan):
            for _ in range(2):
                conn.request("POST", f"/scan?commit={SHA}", body=body, headers={"Content-Type": "application/x-tar"})
                answer = conn.getresponse()
                self.assertEqual(200, answer.status)
                self.assertEqual(SHA, json.loads(answer.read())["commit"])

    def test_What_is_not_a_scan_is_refused(self):
        self.assertEqual(400, self.post("commit=abc", b"")[0])
        self.assertEqual(400, self.post(f"commit={SHA}&tools=semgrep", b"")[0])
        self.assertEqual(400, self.post(f"commit={SHA}", b"x", kind="application/octet-stream")[0])
        self.assertEqual(400, self.post(f"commit={SHA}&ref=HEAD", b"x", kind="application/x-git-bundle")[0])
        with mock.patch.object(scan, "MOST_BYTES", 4):
            self.assertEqual(413, self.post(f"commit={SHA}", b"0123456789")[0])

    def test_A_scan_waits_for_the_one_before_it_and_is_told_to_come_back_later(self):
        scan.SLOTS.acquire()
        try:
            status, body = self.post(f"commit={SHA}", tar_of([regular("a.py")]))
        finally:
            scan.SLOTS.release()
        self.assertEqual(503, status)
        self.assertIn("busy", body["error"])


if __name__ == "__main__":
    unittest.main()
