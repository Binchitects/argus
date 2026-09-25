#!/usr/bin/env python3
"""Python-vs-.NET conformance: same inputs, same services, compare everything.

    python dotnet/tests/conformance/run.py --work /tmp/argus-conformance

What it does, in order:

1. Builds bare git repositories (a real C corpus plus hand-made edge cases) and
   starts a fake GitLab that serves them, and a deterministic fake Ollama.
2. Indexes them with BOTH implementations into separate databases, then
   compares every table row by row -- files, symbols (with doc comments),
   includes and their resolution, the dependency graph, vendoring flags, retry
   state and the symbol embeddings.
3. Commits a second revision everywhere, re-indexes incrementally with both,
   and compares again.
4. Serves each database with its own implementation, and the Python-built one
   with .NET too, then calls every MCP tool as several users and compares the
   answers byte for byte, access-control refusals included.
5. Builds knowledge packs from the same corpora with both builders, compares
   their contents, and compares the documentation tools over them.

Exit status is the number of differences found; 0 means the implementations
are indistinguishable on everything this run can observe.
"""
from __future__ import annotations

import argparse
import json
import os
import shutil
import sqlite3
import subprocess
import sys
import time
from pathlib import Path

HERE = Path(__file__).resolve().parent
REPO = HERE.parents[2]
sys.path.insert(0, str(HERE))

import fakes  # noqa: E402
import fixtures  # noqa: E402
import pack_fixtures  # noqa: E402
from mcpclient import Session  # noqa: E402

DIFFS: list[str] = []


def diff(msg: str) -> None:
    DIFFS.append(msg)
    print("  DIFF", msg)


def section(title: str) -> None:
    print(f"\n=== {title}")


# --- implementations ---------------------------------------------------------------


class Impl:
    def __init__(self, name: str, argv: list[str], env: dict[str, str]):
        self.name, self.argv, self.env = name, argv, env

    def run(self, *args: str, check_codes=(0, 1)) -> subprocess.CompletedProcess:
        proc = subprocess.run([*self.argv, *args], env=self.env, capture_output=True, text=True, timeout=3600)
        if proc.returncode not in check_codes:
            print(proc.stdout[-4000:], proc.stderr[-4000:], sep="\n")
            raise SystemExit(f"{self.name} {' '.join(args)} exited {proc.returncode}")
        return proc

    def serve(self, config: Path, port: int, log: Path) -> subprocess.Popen:
        handle = open(log, "w")
        return subprocess.Popen([*self.argv, "serve", "--config", str(config), "--port", str(port)],
                                env=self.env, stdout=handle, stderr=subprocess.STDOUT)


def free_port() -> int:
    import socket
    with socket.socket() as s:
        s.bind(("127.0.0.1", 0))
        return s.getsockname()[1]


def wait_healthy(port: int, proc: subprocess.Popen, timeout=60) -> None:
    import urllib.request
    deadline = time.time() + timeout
    while time.time() < deadline:
        if proc.poll() is not None:
            raise SystemExit(f"server on {port} exited {proc.returncode}")
        try:
            with urllib.request.urlopen(f"http://127.0.0.1:{port}/healthz", timeout=2) as r:
                if r.status == 200:
                    return
        except OSError:
            time.sleep(0.2)
    raise SystemExit(f"server on {port} never became healthy")


# --- database comparison ---------------------------------------------------------------


def _vec_conn(path: Path) -> sqlite3.Connection:
    import sqlite_vec
    conn = sqlite3.connect(f"file:{path}?mode=ro", uri=True)
    conn.enable_load_extension(True)
    sqlite_vec.load(conn)
    return conn


QUERIES = {
    "repos": "SELECT gitlab_id, path_with_namespace, default_branch, branch, http_url, last_indexed_sha,"
             " last_run_timed_out, last_run_symbols_failed, last_run_error IS NOT NULL FROM repos"
             " ORDER BY gitlab_id, branch",
    "files": "SELECT r.path_with_namespace, r.branch, f.path, f.lang, f.size, f.blob_sha, f.content, f.symbols_sha,"
             " f.is_vendored, f.basename FROM files f JOIN repos r ON r.id = f.repo_id"
             " ORDER BY r.path_with_namespace, r.branch, f.path",
    "symbols": "SELECT r.path_with_namespace, r.branch, f.path, s.name, s.kind, s.line, s.end_line, s.signature,"
               " s.scope, s.is_public, s.doc FROM symbols s JOIN files f ON f.id = s.file_id"
               " JOIN repos r ON r.id = s.repo_id"
               " ORDER BY r.path_with_namespace, r.branch, f.path, s.line, s.name, s.kind, s.scope, s.signature",
    "includes": "SELECT r.path_with_namespace, r.branch, f.path, i.raw, i.is_angle, i.is_external, i.resolution,"
                " (SELECT path FROM files WHERE id = i.resolved_file_id),"
                " (SELECT path_with_namespace || '@' || branch FROM repos WHERE id = i.resolved_repo_id)"
                " FROM includes i JOIN files f ON f.id = i.file_id JOIN repos r ON r.id = i.repo_id"
                " ORDER BY r.path_with_namespace, r.branch, f.path, i.raw, i.is_angle",
    "repo_deps": "SELECT a.path_with_namespace || '@' || a.branch, b.path_with_namespace || '@' || b.branch, d.weight"
                 " FROM repo_deps d JOIN repos a ON a.id = d.from_repo_id JOIN repos b ON b.id = d.to_repo_id"
                 " ORDER BY 1, 2",
    "index_errors": "SELECT r.path_with_namespace, r.branch, e.path, e.stage FROM index_errors e"
                    " JOIN repos r ON r.id = e.repo_id ORDER BY 1, 2, 3, 4",
    "index_queue": "SELECT r.path_with_namespace, r.branch, q.reason FROM index_queue q"
                   " JOIN repos r ON r.id = q.repo_id ORDER BY 1, 2",
    "retry_attempts": "SELECT r.path_with_namespace, r.branch, a.path, a.attempts FROM retry_attempts a"
                      " JOIN repos r ON r.id = a.repo_id ORDER BY 1, 2, 3",
    "argus_meta": "SELECT m.key LIKE 'symbol_contract:%', m.value, r.path_with_namespace, r.branch FROM argus_meta m"
                  " LEFT JOIN repos r ON 'symbol_contract:' || r.id = m.key ORDER BY 3, 4",
    "symbol_embeddings": "SELECT r.path_with_namespace, r.branch, f.path, s.name, s.line, e.embed_text, e.model, e.dim,"
                         " e.text_version, hex(b.embedding), hex(i.embedding)"
                         " FROM symbol_embeddings e JOIN symbols s ON s.id = e.symbol_id"
                         " JOIN files f ON f.id = s.file_id JOIN repos r ON r.id = e.repo_id"
                         " LEFT JOIN vec_symbols_bin b ON b.symbol_id = e.symbol_id"
                         " LEFT JOIN vec_symbols_i8 i ON i.symbol_id = e.symbol_id"
                         " ORDER BY 1, 2, 3, 5, 4",
}


def compare_databases(a: Path, b: Path, label: str) -> None:
    section(f"index tables: {label}")
    ca, cb = _vec_conn(a), _vec_conn(b)
    try:
        for table, sql in QUERIES.items():
            ra = ca.execute(sql).fetchall()
            rb = cb.execute(sql).fetchall()
            if ra == rb:
                print(f"  ok   {table:<18} {len(ra)} rows")
                continue
            sa, sb = set(ra), set(rb)
            only_a, only_b = sorted(sa - sb, key=repr)[:5], sorted(sb - sa, key=repr)[:5]
            diff(f"{label}: {table}: {len(ra)} vs {len(rb)} rows; {len(sa - sb)} only in python, {len(sb - sa)} only in .NET")
            for row in only_a:
                print("       python:", repr(row)[:400])
            for row in only_b:
                print("       .NET  :", repr(row)[:400])
        # The FTS index is external content: check it answers the same.
        for query in ("inflate", "png_read_image", "LZ4_compress", "\"jpeg_start_decompress\"", "expire keys"):
            fa = ca.execute("SELECT f.path FROM files_fts JOIN files f ON f.id = files_fts.rowid WHERE files_fts MATCH ? ORDER BY 1", (query,)).fetchall()
            fb = cb.execute("SELECT f.path FROM files_fts JOIN files f ON f.id = files_fts.rowid WHERE files_fts MATCH ? ORDER BY 1", (query,)).fetchall()
            if fa != fb:
                diff(f"{label}: files_fts MATCH {query!r}: {len(fa)} vs {len(fb)}")
        print("  ok   files_fts          5 probe queries" if not any("files_fts" in d for d in DIFFS) else "")
    finally:
        ca.close()
        cb.close()


# --- setup ------------------------------------------------------------------------------


USERS = [
    fakes.User(1, "svc", "svc-token", email="svc@example.invalid", is_admin=True, name="Service"),
    fakes.User(2, "alice", "alice-token", email="alice@example.invalid", public_email="alice@example.invalid", name="Alice"),
    fakes.User(3, "bob", "bob-token", email="bob@example.invalid", name="Bob"),
    fakes.User(4, "carol", "carol-token", email="carol@example.invalid", public_email="carol@example.invalid", name="Carol"),
]


def projects() -> list[fakes.ProjectDef]:
    out = []
    for i, (name, _url, _tag) in enumerate(fixtures.CORPUS, start=100):
        out.append(fakes.ProjectDef(i, f"oss/{name}", "main", {1: 50, 2: 20, 4: 40}))
    out.append(fakes.ProjectDef(200, "acme/docs-demo", "main", {1: 50, 2: 30, 4: 40}))
    # bob is a guest here: below Reporter, so he may not read it.
    out.append(fakes.ProjectDef(201, "acme/secret", "main", {1: 50, 3: 10, 4: 40}))
    return out


def write_config(root: Path, gitlab_url: str) -> Path:
    root.mkdir(parents=True, exist_ok=True)
    cfg = root / "config.yaml"
    cfg.write_text(
        "gitlab:\n"
        f"  url: {gitlab_url}\n"
        "  token: svc-token\n"
        "index:\n"
        f"  data_dir: {root / 'data'}\n"
        f"  db_path: {root / 'data' / 'index.db'}\n"
        "  max_file_bytes: 1048576\n"
        "  branches: ['release/*']\n"
        "packs:\n"
        f"  dir: {root / 'packs'}\n", encoding="utf-8")
    return cfg


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--work", type=Path, default=Path("/tmp/argus-conformance"))
    parser.add_argument("--corpus-cache", type=Path, default=None)
    parser.add_argument("--python", default=os.environ.get("ARGUS_PYTHON", sys.executable))
    parser.add_argument("--dotnet", default=os.environ.get("ARGUS_DOTNET",
                        str(REPO / "dotnet/src/Argus/bin/Release/net10.0/argus")))
    parser.add_argument("--skip", action="append", default=[], help="index | serve | packs")
    args = parser.parse_args()

    work = args.work
    if work.exists():
        shutil.rmtree(work)
    work.mkdir(parents=True)
    corpus = fixtures.fetch_corpus(args.corpus_cache or (work.parent / "argus-conformance-corpus"))

    section("fixtures")
    repos_dir = work / "repos"
    fixtures.build(work / "trees", repos_dir, corpus)
    print(f"  {len(list(repos_dir.rglob('*.git')))} bare repositories")

    gitlab = fakes.FakeGitLab(repos_dir, USERS, projects())
    ollama = fakes.FakeOllama()
    base_env = {**os.environ, "ARGUS_OLLAMA_URL": ollama.url, "ARGUS_AUDIT_LOG": "0",
                "ARGUS_EMBED_PER_PASS": "100000", "GIT_TERMINAL_PROMPT": "0"}
    for key in ("ARGUS_GITLAB_URL", "ARGUS_GITLAB_TOKEN", "ARGUS_GITLAB_USERNAME", "ARGUS_GITLAB_PASSWORD"):
        base_env.pop(key, None)
    py = Impl("python", [args.python, "-m", "argus.cli"], {**base_env, "PYTHONPATH": str(REPO / "src")})
    net = Impl(".NET", [args.dotnet], base_env)
    py_cfg = write_config(work / "py", gitlab.url)
    net_cfg = write_config(work / "net", gitlab.url)

    try:
        if "index" not in args.skip:
            for rev in ("first revision", "second revision"):
                section(f"index: {rev}")
                if rev == "second revision":
                    fixtures.mutate(work / "trees", repos_dir)
                for impl, cfg in ((py, py_cfg), (net, net_cfg)):
                    started = time.time()
                    proc = impl.run("index", "--config", str(cfg))
                    tail = [l for l in proc.stdout.splitlines() if l.startswith(("includes:", "repo graph:"))]
                    print(f"  {impl.name:<7} exit {proc.returncode} in {time.time() - started:.1f}s  {' | '.join(tail)}")
                compare_databases(work / "py/data/index.db", work / "net/data/index.db", rev)
        if "serve" not in args.skip:
            compare_tools(work, py, net, py_cfg, net_cfg)
        if "packs" not in args.skip:
            compare_packs(work, py, net, py_cfg, net_cfg, corpus)
    finally:
        gitlab.close()
        ollama.close()

    section("result")
    print(f"  {len(DIFFS)} difference(s)")
    for d in DIFFS:
        print("   -", d)
    return len(DIFFS)


PNG_SOURCE = """
#include "png.h"
void load(png_structp png_ptr, png_infop info_ptr) {
    png_read_info(png_ptr, info_ptr);
    png_set_expand(png_ptr);
    png_read_image(png_ptr, NULL);
    inflateInit2_(NULL, 15, "1.3", 0);
    png_read_image(png_ptr, NULL);
}
"""

STACK = """Traceback (most recent call last):
  at inflate (inflate.c:622)
  at png_read_IDAT_data (pngrutil.c:4178)
  at png_read_row (pngread.c:543)
"""

DIFF_TEXT = """diff --git a/lib/lz4.c b/lib/lz4.c
--- a/lib/lz4.c
+++ b/lib/lz4.c
@@ -1,3 +1,4 @@
+/* touched */
"""


def tool_calls(repo_ids: dict[str, int]) -> list[tuple[str, dict]]:
    zlib = repo_ids.get("oss/zlib", 0)
    png = repo_ids.get("oss/libpng", 0)
    secret = repo_ids.get("acme/secret", 0)
    demo = repo_ids.get("acme/docs-demo", 0)
    calls = [
        ("index_status", {}),
        ("overview", {}),
        ("overview", {"repo": "oss/zlib"}),
        ("find_symbol", {"name": "png_read_image"}),
        ("find_symbol", {"name": "inflate", "kind": "function"}),
        ("find_symbol", {"name": "deflateInit2_"}),
        ("find_symbol", {"name": "vault_unlock"}),
        ("find_symbol", {"name": "DecodeFrame"}),
        ("find_symbol", {"name": "release_only", "branch": "release/v2"}),
        ("find_symbol", {"name": "upload_chunk", "branch": "nope"}),
        ("find_symbol", {"name": "hidden"}),
        ("find_references", {"name": "LZ4_compress_default"}),
        ("find_references", {"name": "vault_unlock"}),
        ("find_references", {"name": "adler32"}),
        ("search_code", {"query": "jpeg_start_decompress"}),
        ("search_code", {"query": "retry backoff"}),
        ("search_code", {"query": "Key handling"}),
        ("search_code", {"query": "NEAR(\"a\""}),
        ("search_code", {"query": "AND"}),
        ("get_file", {"repo_id": zlib, "path": "zlib.h"}),
        ("get_file", {"repo_id": zlib, "path": "missing.c"}),
        ("get_file", {"repo_id": secret, "path": "vault.c"}),
        ("get_file", {"repo_id": demo, "path": "docs/with space/notes.md"}),
        ("repo_map", {"repo_id": zlib}),
        ("repo_map", {"repo_id": png}),
        ("repo_map", {"repo_id": secret}),
        ("which_repo", {"description": "add a faster checksum to the deflate stream"}),
        ("which_repo", {"description": "inflate.c"}),
        ("which_repo", {"description": STACK}),
        ("which_repo", {"description": DIFF_TEXT}),
        ("which_repo", {"description": "png_read_image"}),
        ("which_repo", {"description": "decrypt the vault key"}),
        ("which_repo", {"description": "   "}),
        ("impact_of", {"repo_id": zlib, "path": "zlib.h"}),
        ("impact_of", {"repo_id": zlib, "path": "zconf.h", "max_depth": 1}),
        ("impact_of", {"repo_id": secret, "path": "vault.c"}),
        ("code_contracts", {"source": PNG_SOURCE}),
        ("code_contracts", {"source": "int main() { return vault_unlock(1); }"}),
        ("semantic_search", {"query": "remove keys whose time to live has elapsed"}),
        ("semantic_search", {"query": "decompress a jpeg scanline", "limit": 5}),
        ("semantic_search", {"query": "upload with retries", "branch": "release/v2"}),
        ("semantic_search", {"query": ""}),
        ("find_symbol", {}),
        ("get_file", {"repo_id": "x", "path": 3}),
        ("no_such_tool", {}),
    ]
    return calls


def repo_ids_from(session: Session) -> dict[str, int]:
    res = session.call("index_status", {})["result"]
    rows = res.get("structuredContent", {}).get("result", [])
    return {r["path_with_namespace"]: r["repo_id"] for r in rows if r["branch"] == r["default_branch"]}


TIME_KEYS = ("last_indexed_at", "last_run_at", "started", "finished", "collected_at")


def normalise(value, times: bool):
    """Drop wall-clock fields when the two sides were indexed seconds apart."""
    if not times:
        return value
    if isinstance(value, dict):
        out = {}
        for k, v in value.items():
            if k in TIME_KEYS:
                continue
            if k == "text" and isinstance(v, str):
                try:
                    v = json.dumps(normalise(json.loads(v), times))
                except ValueError:
                    pass
            out[k] = normalise(v, times)
        return out
    if isinstance(value, list):
        return [normalise(v, times) for v in value]
    return value


def compare_sessions(label: str, a: Session, b: Session, calls, times: bool = False) -> None:
    ta, tb = a.rpc("tools/list")["result"]["tools"], b.rpc("tools/list")["result"]["tools"]
    if ta != tb:
        diff(f"{label}: tools/list differs")
    if a.init["result"].get("instructions") != b.init["result"].get("instructions"):
        diff(f"{label}: server instructions differ")
    same = 0
    for name, arguments in calls:
        ra, rb = a.call(name, arguments), b.call(name, arguments)
        va, vb = ra.get("result", ra.get("error")), rb.get("result", rb.get("error"))
        if normalise(va, times) == normalise(vb, times):
            same += 1
            continue
        diff(f"{label}: {name} {json.dumps(arguments)[:80]}")
        print("       python:", json.dumps(va)[:700])
        print("       .NET  :", json.dumps(vb)[:700])
    print(f"  {label}: {same}/{len(calls)} identical")


def http(method: str, url: str, body=None, headers=None):
    import urllib.request
    data = json.dumps(body).encode() if body is not None else None
    req = urllib.request.Request(url, data=data, method=method)
    for k, v in (headers or {}).items():
        req.add_header(k, v)
    if data is not None:
        req.add_header("Content-Type", "application/json")
    try:
        with urllib.request.urlopen(req, timeout=60) as r:
            return r.status, r.read().decode(), dict(r.headers)
    except urllib.error.HTTPError as e:
        return e.code, e.read().decode(), dict(e.headers)


def _without_times(node):
    if isinstance(node, dict):
        out = {k: _without_times(v) for k, v in node.items()
               if k not in ("started", "finished", "last_run_at", "last_indexed_at", "collected_at", "version", "packs_dir")}
        # /admin/index/status orders repos by last_run_at, which is wall-clock
        # time; with the times removed the order carries no information.
        if isinstance(out.get("repos"), list) and all(isinstance(r, dict) for r in out["repos"]):
            out["repos"] = sorted(out["repos"], key=lambda r: json.dumps(r, sort_keys=True))
        return out
    if isinstance(node, list):
        return [_without_times(v) for v in node]
    return node


def _samples(text: str) -> list[str]:
    return sorted(l for l in text.splitlines() if l and not l.startswith("#") and "build_info" not in l
                  and "last_run_timestamp" not in l and "last_indexed_timestamp" not in l)


def compare_http(label: str, pa: int, pb: int) -> None:
    admin = {"x-argus-admin-token": "admin-secret"}
    checks = [
        ("GET", "/healthz", None, {}),
        ("GET", "/mcp", None, {}),
        ("POST", "/mcp", {"jsonrpc": "2.0", "id": 1, "method": "ping"}, {"Authorization": "Bearer nope"}),
        ("POST", "/mcp", {"jsonrpc": "2.0", "id": 1, "method": "ping"}, {"Authorization": "Basic x"}),
        ("POST", "/mcp", {"jsonrpc": "2.0", "id": 1, "method": "ping"},
         {"Authorization": "Bearer alice-token", "Host": "evil.example"}),
        ("GET", "/admin/explore?q=png_read&limit=5", None, admin),
        ("GET", "/admin/explore?q=zlib&repo=oss/zlib", None, admin),
        ("GET", "/admin/explore?q=100%25", None, admin),
        ("GET", "/admin/explore", None, {"x-argus-admin-token": "wrong"}),
        ("GET", "/admin/packs", None, admin),
        ("GET", "/admin/index/status", None, admin),
        ("POST", "/admin/packs/remove", {"name": "nope"}, admin),
        ("POST", "/admin/packs/install", {}, admin),
        ("POST", "/hook/gitlab", {"object_kind": "tag_push"}, {"x-gitlab-token": "hook-secret"}),
        ("POST", "/hook/gitlab", {"object_kind": "push", "project": {}}, {"x-gitlab-token": "hook-secret"}),
        ("POST", "/hook/gitlab", {"object_kind": "push", "after": "0000000000", "project": {"path_with_namespace": "oss/zlib"}},
         {"x-gitlab-token": "hook-secret"}),
        ("POST", "/hook/gitlab", {"object_kind": "push"}, {"x-gitlab-token": "wrong"}),
    ]
    same = 0
    for method, path, body, headers in checks:
        sa, ta, ha = http(method, f"http://127.0.0.1:{pa}{path}", body, headers)
        sb, tb, hb = http(method, f"http://127.0.0.1:{pb}{path}", body, headers)
        try:
            ja, jb = _without_times(json.loads(ta)), _without_times(json.loads(tb))
        except ValueError:
            ja, jb = ta, tb
        if sa == sb and ja == jb:
            same += 1
            continue
        diff(f"{label}: {method} {path} -> {sa} vs {sb}")
        print("       python:", str(ja)[:500])
        print("       .NET  :", str(jb)[:500])
    sa, ta, _ = http("GET", f"http://127.0.0.1:{pa}/admin/metrics", None, admin)
    sb, tb, _ = http("GET", f"http://127.0.0.1:{pb}/admin/metrics", None, admin)
    if sa != sb or _samples(ta) != _samples(tb):
        diff(f"{label}: /admin/metrics samples differ")
        print("       python:", _samples(ta)[:6])
        print("       .NET  :", _samples(tb)[:6])
    else:
        same += 1
    print(f"  {label}: {same}/{len(checks) + 1} HTTP checks identical")


def compare_tools(work: Path, py: Impl, net: Impl, py_cfg: Path, net_cfg: Path) -> None:
    section("MCP tools and HTTP surface")
    extra = {"ARGUS_ADMIN_TOKEN": "admin-secret", "ARGUS_WEBHOOK_TOKEN": "hook-secret",
             "ARGUS_CHAT_CLIENT_TOKEN": "chat-secret"}
    py.env.update(extra)
    net.env.update(extra)
    servers = []
    try:
        ports = {}
        for key, impl, cfg in (("py", py, py_cfg), ("net", net, net_cfg), ("net-on-py", net, py_cfg)):
            port = free_port()
            proc = impl.serve(cfg, port, work / f"{key}-server.log")
            servers.append(proc)
            wait_healthy(port, proc)
            ports[key] = port
        base = lambda k: f"http://127.0.0.1:{ports[k]}"
        ids = repo_ids_from(Session(base("py"), "svc-token"))
        ids_net = repo_ids_from(Session(base("net"), "svc-token"))
        if ids != ids_net:
            diff(f"repo ids differ between the two indexes: {ids} vs {ids_net}")
        calls = tool_calls(ids)
        identities = [
            ("svc (admin)", "svc-token", {}),
            ("alice (reporter)", "alice-token", {}),
            ("bob (guest)", "bob-token", {}),
            ("chat as alice", "chat-secret", {"x-openwebui-user-email": "alice@example.invalid"}),
        ]
        for who, token, headers in identities:
            a = Session(base("py"), token, headers)
            for other in ("net", "net-on-py"):
                b = Session(base(other), token, headers)
                # Separate indexes were built seconds apart; the shared one was not.
                compare_sessions(f"{who} / {other}", a, b, calls, times=(other == "net"))
        for other in ("net", "net-on-py"):
            compare_http(f"http / {other}", ports["py"], ports[other])
    finally:
        for proc in servers:
            proc.terminate()
        for proc in servers:
            try:
                proc.wait(timeout=10)
            except subprocess.TimeoutExpired:
                proc.kill()


def _pack_rows(path: Path) -> dict[str, list]:
    import zstandard
    dec = zstandard.ZstdDecompressor()
    z = lambda b: dec.decompress(b).decode("utf-8", "replace") if b else ""
    conn = _vec_conn(path)
    try:
        meta = dict(conn.execute("SELECT key, value FROM pack_meta"))
        meta.pop("source_commit", None)
        docs = [(r[0], r[1], r[2], r[3], z(r[4]), r[5], r[6]) for r in
                conn.execute("SELECT path, title, url, lang, content, content_len, content_sha FROM docs ORDER BY id")]
        chunks = [(r[0], r[1], r[2], r[3], z(r[4])) for r in conn.execute(
            "SELECT d.path, c.heading_path, c.anchor, c.start_line, c.text FROM chunks c JOIN docs d ON d.id = c.doc_id ORDER BY c.id")]
        symbols = conn.execute("SELECT s.name, s.kind, s.namespace, d.path, s.anchor, s.signature FROM api_symbols s"
                               " JOIN docs d ON d.id = s.doc_id ORDER BY s.id").fetchall()
        vectors = conn.execute("SELECT c.id, hex(b.embedding), hex(i.embedding) FROM chunks c"
                               " JOIN vec_bin b ON b.chunk_id = c.id JOIN vec_i8 i ON i.chunk_id = c.id ORDER BY c.id").fetchall()
        fts = conn.execute("SELECT rowid FROM docs_fts WHERE docs_fts MATCH 'the' ORDER BY rowid").fetchall()
        return {"meta": sorted(meta.items()), "docs": docs, "chunks": chunks, "symbols": symbols,
                "vectors": vectors, "fts": fts}
    finally:
        conn.close()


DOCS_CALLS = [
    ("docs_lookup", {"name": "MessageBox"}),
    ("docs_lookup", {"name": "messageboxw"}),
    ("docs_lookup", {"name": "IMFCaptureSource::GetMirrorState"}),
    ("docs_lookup", {"name": "os.path.join"}),
    ("docs_lookup", {"name": "useState"}),
    ("docs_lookup", {"name": "useState", "lang": "python"}),
    ("docs_lookup", {"name": "useState", "lang": "golang"}),
    ("docs_lookup", {"name": "std::vector"}),
    ("docs_lookup", {"name": "String.Concat"}),
    ("docs_lookup", {"name": "lm"}),
    ("docs_lookup", {"name": "SELECT"}),
    ("docs_lookup", {"name": "nothing_like_this"}),
    ("docs_find", {"description": "which command mirrors a directory tree"}),
    ("docs_find", {"description": "write objects to a CSV file", "lang": "scripting"}),
    ("docs_find", {"description": "display a modal dialog box", "lang": "rust"}),
    ("docs_find", {"description": "join path components"}),
    ("docs_search", {"query": "how do I reset state when a prop changes"}),
    ("docs_search", {"query": "allocate from a lookaside list", "lang": "wdk"}),
    ("docs_search", {"query": "MessageBox dialog"}),
    ("docs_get", {"doc_path": "winuser/nf-winuser-messagebox.md"}),
    ("docs_get", {"doc_path": "lang_select.html", "source": "sqlite"}),
    ("docs_get", {"doc_path": "missing"}),
    ("docs_contracts", {"source": "void f() { MessageBoxW(0,0,0,0); ExAllocateFromLookasideListEx(&l); InitializeListHead(&h); }"}),
    ("docs_verify", {"text": "Call ExAllocateFromLookasideListEx at PASSIVE_LEVEL; include wdm.h and link NtosKrnl.lib."}),
    ("docs_verify", {"text": "MessageBoxW lives in shell32.dll and needs winuser.h. Also ExAllocateFromLookasideListEx is fine at DISPATCH_LEVEL."}),
    ("docs_verify", {"text": "nothing to see"}),
]


def compare_packs(work: Path, py: Impl, net: Impl, py_cfg: Path, net_cfg: Path, corpus: dict[str, Path]) -> None:
    section("knowledge packs: build")
    sources = pack_fixtures.build(work / "packsrc", corpus)
    for source, (label, workdir) in sources.items():
        outs = {}
        for impl, key in ((py, "py"), (net, "net")):
            out = work / f"built-{key}" / f"{source}.arguspack"
            proc = impl.run("pack", "build", "--source", source, "--work-dir", str(workdir), "--out", str(out),
                            "--version", "1.0", "--commit", "fixture", check_codes=(0, 5))
            if proc.returncode != 0:
                diff(f"pack build {source}: {impl.name} exited {proc.returncode}: {proc.stderr.strip()[-300:]}")
                break
            outs[key] = out
        if len(outs) != 2:
            continue
        ra, rb = _pack_rows(outs["py"]), _pack_rows(outs["net"])
        bad = [k for k in ra if ra[k] != rb[k]]
        if not bad:
            print(f"  ok   {source:<14} {len(ra['docs'])} docs, {len(ra['chunks'])} chunks, {len(ra['symbols'])} symbols")
            continue
        for k in bad:
            diff(f"pack {source}: {k} differ ({len(ra[k])} vs {len(rb[k])})")
            for x, y in zip(ra[k], rb[k]):
                if x != y:
                    print("       python:", repr(x)[:500])
                    print("       .NET  :", repr(y)[:500])
                    break

    section("knowledge packs: incremental rebuild")
    for impl, key in ((py, "py"), (net, "net")):
        target = work / f"built-{key}" / "system-design.arguspack"
        pack_fixtures.w(sources["system-design"][1], "solutions/system_design/pastebin/README.md", "# Design Pastebin\n\nChanged.\n")
        proc = impl.run("pack", "build", "--source", "system-design", "--work-dir", str(sources["system-design"][1]),
                        "--out", str(target), "--version", "1.1", "--commit", "fixture2")
        print(f"  {impl.name:<7} {[l.strip() for l in proc.stdout.splitlines() if 'incremental' in l]}")
    ra = _pack_rows(work / "built-py" / "system-design.arguspack")
    rb = _pack_rows(work / "built-net" / "system-design.arguspack")
    for k in ra:
        if ra[k] != rb[k]:
            diff(f"incremental system-design pack: {k} differ")

    section("knowledge packs: CLI")
    for key, cfg in (("py", py_cfg), ("net", net_cfg)):
        dest = cfg.parent / "packs"
        dest.mkdir(parents=True, exist_ok=True)
        for pack in sorted((work / "built-py").glob("*.arguspack")):
            shutil.copy2(pack, dest / pack.name)
    for args in (["pack", "list"], ["pack", "info", "win32"], ["pack", "info", "nope"],
                 ["pack", "index", "--out", "{out}", "--base-url", "https://packs.example/v1/"]):
        results = []
        for impl, cfg in ((py, py_cfg), (net, net_cfg)):
            out = cfg.parent / "index.json"
            real = [a.replace("{out}", str(out)) for a in args]
            proc = impl.run(*real, "--packs-dir", str(cfg.parent / "packs"), check_codes=(0, 5))
            text = (proc.stdout + proc.stderr).replace(str(cfg.parent), "<root>")
            if "index" in args:
                text += out.read_text()
            results.append((proc.returncode, text))
        if results[0] != results[1]:
            diff(f"CLI {' '.join(args)}")
            print("       python:", results[0][1][:600])
            print("       .NET  :", results[1][1][:600])
        else:
            print(f"  ok   argus {' '.join(args)}")
    for text in ("MessageBoxW is declared in winuser.h and lives in shell32.dll.", "all fine here", ""):
        results = []
        for impl, cfg in ((py, py_cfg), (net, net_cfg)):
            proc = impl.run("verify", "--config", str(cfg), "--text", text, "--json", check_codes=(0, 2, 6))
            results.append((proc.returncode, proc.stdout, proc.stderr))
        if results[0] != results[1]:
            diff(f"CLI verify {text!r}")
            print("       python:", results[0])
            print("       .NET  :", results[1])
        else:
            print(f"  ok   argus verify {text[:30]!r} -> exit {results[0][0]}")

    section("knowledge packs: MCP documentation tools")
    servers = []
    try:
        ports = {}
        for key, impl, cfg in (("py", py, py_cfg), ("net", net, net_cfg)):
            port = free_port()
            proc = impl.serve(cfg, port, work / f"{key}-docs-server.log")
            servers.append(proc)
            wait_healthy(port, proc)
            ports[key] = port
        a = Session(f"http://127.0.0.1:{ports['py']}", "alice-token")
        b = Session(f"http://127.0.0.1:{ports['net']}", "alice-token")
        compare_sessions("docs tools", a, b, DOCS_CALLS)
    finally:
        for proc in servers:
            proc.terminate()
            proc.wait(timeout=10)


if __name__ == "__main__":
    raise SystemExit(main())
