#!/usr/bin/env python3
"""End to end: the .NET Argus behind the real operator console, in a browser.

    python dotnet/tests/e2e/run.py --work /tmp/argus-e2e --corpus-cache /tmp/argus-corpus \
        --console-python /path/to/py3.13/bin/python

Nothing here is mocked on the Argus side. The run starts:

* a fake GitLab (REST + git over HTTP) serving real C projects and a few
  hand-made ones, with four people at different access levels;
* a deterministic fake Ollama, so semantic search has embeddings;
* a static server publishing knowledge packs and their index, as a release
  bucket would;
* the .NET Argus server, with admin, webhook and chat-client credentials;
* the admin console (stack/deploy/admin-panel), unmodified, pointed at it;

then drives the console with Chromium the way an operator would -- start an
index pass from the Indexing page, watch it finish, search the result on
Explore, install / update / remove packs -- and uses the MCP surface the way
the chat front end and desktop clients do: the official MCP Python SDK over
streamable HTTP (bearer tokens, and the chat client's forwarded-user header)
and over stdio. Webhooks, the Prometheus exposition and the CLI are checked
against the same live index.

Needs: the .NET build (dotnet build -c Release), git and universal-ctags; a
Python for this script with the mcp SDK, playwright and prometheus_client; and
for the console, the interpreter and pins of its own image (Python 3.12+ with
the packages stack/deploy/admin-panel/Dockerfile installs), given as
--console-python. Chromium comes from PLAYWRIGHT_BROWSERS_PATH.

Exit status is the number of failed checks. Screenshots of every page land in
<work>/screens.
"""
from __future__ import annotations

import argparse
import asyncio
import functools
import hashlib
import http.server as httpserver
import json
import os
import shutil
import statistics
import subprocess
import sys
import threading
import time
import urllib.error
import urllib.parse
import urllib.request
from pathlib import Path

HERE = Path(__file__).resolve().parent
REPO = HERE.parents[2]
sys.path.insert(0, str(HERE.parent / "conformance"))

import fakes  # noqa: E402
import fixtures  # noqa: E402
import pack_fixtures  # noqa: E402

ADMIN_TOKEN, WEBHOOK_TOKEN, CHAT_TOKEN = "e2e-admin", "e2e-hook", "e2e-chat"

USERS = [
    fakes.User(1, "svc", "svc-token", email="svc@example.invalid", is_admin=True, name="Service"),
    fakes.User(2, "alice", "alice-token", email="alice@example.invalid", public_email="alice@example.invalid", name="Alice"),
    fakes.User(3, "bob", "bob-token", email="bob@example.invalid", name="Bob"),
    fakes.User(4, "carol", "carol-token", email="carol@example.invalid", public_email="carol@example.invalid", name="Carol"),
]


def projects() -> list[fakes.ProjectDef]:
    """alice reads oss/* and docs-demo; carol reads everything; bob is only a
    guest on acme/secret, which is below Reporter, so he reads nothing."""
    out = [fakes.ProjectDef(i, f"oss/{name}", "main", {1: 50, 2: 20, 4: 40})
           for i, (name, _u, _t) in enumerate(fixtures.CORPUS, start=100)]
    out.append(fakes.ProjectDef(200, "acme/docs-demo", "main", {1: 50, 2: 30, 4: 40}))
    out.append(fakes.ProjectDef(201, "acme/secret", "main", {1: 50, 3: 10, 4: 40}))
    return out


# --- reporting -----------------------------------------------------------------------

RESULTS: list[tuple[str, str, bool, str]] = []


def check(area: str, name: str, ok: bool, detail: str = "") -> bool:
    RESULTS.append((area, name, bool(ok), detail))
    mark = "PASS" if ok else "FAIL"
    print(f"  [{mark}] {area:<8} {name}" + (f"  -- {detail}" if detail else ""), flush=True)
    return bool(ok)


def section(title: str) -> None:
    print(f"\n=== {title}", flush=True)


# --- processes -----------------------------------------------------------------------


def free_port() -> int:
    import socket
    with socket.socket() as s:
        s.bind(("127.0.0.1", 0))
        return s.getsockname()[1]


def wait_http(url: str, proc: subprocess.Popen, timeout: float = 60) -> None:
    deadline = time.time() + timeout
    while time.time() < deadline:
        if proc.poll() is not None:
            raise SystemExit(f"{url}: process exited {proc.returncode}")
        try:
            with urllib.request.urlopen(url, timeout=2):
                return
        except urllib.error.HTTPError:
            return                      # it answered; the status is the caller's business
        except OSError:
            time.sleep(0.2)
    raise SystemExit(f"{url} never answered")


def http(method: str, url: str, body=None, headers=None, form: bool = False):
    data = None
    if body is not None:
        data = (urllib.parse.urlencode(body) if form else json.dumps(body)).encode()
    req = urllib.request.Request(url, data=data, method=method)
    req.add_header("Content-Type", "application/x-www-form-urlencoded" if form else "application/json")
    for k, v in (headers or {}).items():
        req.add_header(k, v)
    try:
        with urllib.request.urlopen(req, timeout=30) as r:
            return r.status, r.read().decode(), dict(r.headers)
    except urllib.error.HTTPError as e:
        return e.code, e.read().decode(), dict(e.headers)


class Static:
    """A release bucket: serves a directory over HTTP."""

    def __init__(self, root: Path):
        handler = functools.partial(_QuietHandler, directory=str(root))
        self.httpd = httpserver.ThreadingHTTPServer(("127.0.0.1", 0), handler)
        self.url = f"http://127.0.0.1:{self.httpd.server_address[1]}/"
        threading.Thread(target=self.httpd.serve_forever, daemon=True).start()

    def close(self):
        self.httpd.shutdown()


class _QuietHandler(httpserver.SimpleHTTPRequestHandler):
    def log_message(self, *args):
        pass


def sha256(path: Path) -> str:
    return hashlib.sha256(path.read_bytes()).hexdigest()


# --- the run -------------------------------------------------------------------------


class Env:
    def __init__(self, args):
        self.args = args
        self.work: Path = args.work
        self.argus = args.dotnet
        self.procs: list[subprocess.Popen] = []
        self.closers = []

    def start(self, argv, log: Path, env, cwd=None) -> subprocess.Popen:
        proc = subprocess.Popen(argv, env=env, cwd=cwd, stdout=open(log, "w"), stderr=subprocess.STDOUT)
        self.procs.append(proc)
        return proc

    def stop(self):
        for proc in self.procs:
            proc.terminate()
        for proc in self.procs:
            try:
                proc.wait(timeout=10)
            except subprocess.TimeoutExpired:
                proc.kill()
        for close in self.closers:
            close()


def argus_get(env: Env, path: str) -> dict:
    status, body, _ = http("GET", env.argus_url + path, headers={"x-argus-admin-token": ADMIN_TOKEN})
    if status != 200:
        raise RuntimeError(f"{path}: HTTP {status}: {body[:200]}")
    return json.loads(body)


def wait_job(env: Env, kind: str, since: float, timeout: float = 900) -> dict:
    """Until the index or pack job that started at or after `since` is idle again."""
    path = "/admin/index/status" if kind == "index" else "/admin/packs"
    deadline = time.time() + timeout
    while time.time() < deadline:
        job = argus_get(env, path).get("job") or {}
        if job.get("state") != "running" and (job.get("started") or 0) >= since - 1 and job.get("finished"):
            return job
        time.sleep(0.5)
    raise SystemExit(f"{kind} job did not finish within {timeout}s")


def setup(env: Env) -> None:
    work = env.work
    if work.exists():
        shutil.rmtree(work)
    (work / "screens").mkdir(parents=True)
    corpus = fixtures.fetch_corpus(env.args.corpus_cache or (work.parent / "argus-e2e-corpus"))
    env.corpus = corpus

    section("fixtures")
    env.trees, env.repos = work / "trees", work / "repos"
    fixtures.build(env.trees, env.repos, corpus)
    print(f"  {len(list(env.repos.rglob('*.git')))} bare repositories")
    gitlab = fakes.FakeGitLab(env.repos, USERS, projects())
    ollama = fakes.FakeOllama()
    env.closers += [gitlab.close, ollama.close]

    base_env = {k: v for k, v in os.environ.items() if not k.startswith("ARGUS_")}
    base_env.update({"ARGUS_OLLAMA_URL": ollama.url, "GIT_TERMINAL_PROMPT": "0", "ARGUS_EMBED_PER_PASS": "100000",
                     "ARGUS_AUDIT_LOG": "0"})
    env.base_env = base_env

    # Packs: python at 1.0 to install, 1.1 published; win32 published at 1.0.
    section("knowledge packs (built with the .NET builder)")
    sources = pack_fixtures.build(work / "packsrc", corpus)
    staging, published = work / "staging", work / "published"
    staging.mkdir()
    published.mkdir()

    def build(source: str, version: str, out: Path) -> Path:
        proc = subprocess.run([env.argus, "pack", "build", "--source", source, "--work-dir", str(sources[source][1]),
                               "--out", str(out), "--version", version, "--commit", f"e2e-{version}"],
                              env=base_env, capture_output=True, text=True, timeout=600)
        if proc.returncode != 0:
            raise SystemExit(f"pack build {source}: {proc.stderr[-500:]}")
        return out

    env.python_10 = build("python", "1.0", staging / "python.arguspack")
    build("python", "1.1", published / "python.arguspack")
    env.win32 = build("win32-docs", "1.0", published / "win32.arguspack")
    bucket = Static(published)
    env.closers.append(bucket.close)
    proc = subprocess.run([env.argus, "pack", "index", "--packs-dir", str(published), "--out", str(published / "index.json"),
                           "--base-url", bucket.url], env=base_env, capture_output=True, text=True)
    check("setup", "pack index published", proc.returncode == 0, proc.stderr.strip()[-200:])
    env.pack_index_url = bucket.url + "index.json"

    # Argus.
    root = work / "argus"
    (root / "data").mkdir(parents=True)
    (root / "packs").mkdir()
    env.cfg = root / "config.yaml"
    env.cfg.write_text(
        "gitlab:\n"
        f"  url: {gitlab.url}\n"
        "  token: svc-token\n"
        "index:\n"
        f"  data_dir: {root / 'data'}\n"
        f"  db_path: {root / 'data' / 'index.db'}\n"
        "  max_file_bytes: 1048576\n"
        "packs:\n"
        f"  dir: {root / 'packs'}\n", encoding="utf-8")
    env.argus_env = {**base_env, "ARGUS_ADMIN_TOKEN": ADMIN_TOKEN, "ARGUS_WEBHOOK_TOKEN": WEBHOOK_TOKEN,
                     "ARGUS_CHAT_CLIENT_TOKEN": CHAT_TOKEN, "ARGUS_INDEX_INTERVAL": "0",
                     "ARGUS_PACK_INDEX_URL": env.pack_index_url}
    port = free_port()
    env.argus_url = f"http://127.0.0.1:{port}"
    proc = env.start([env.argus, "serve", "--config", str(env.cfg), "--port", str(port)], work / "argus.log", env.argus_env)
    wait_http(env.argus_url + "/healthz", proc)

    # The console, exactly as shipped; only its environment is ours.
    auth = work / "authelia"
    auth.mkdir()
    users = auth / "users.yml"
    users.write_text("users:\n  admin:\n    displayname: Admin\n    email: admin@example.invalid\n"
                     "    groups: [admins]\n    password: \"$argon2id$v=19$m=65536,t=3,p=4$c2FsdHNhbHQ$aGFzaA\"\n",
                     encoding="utf-8")
    users.chmod(0o600)
    dead = f"http://127.0.0.1:{free_port()}"          # nothing listens: LiteLLM, Prometheus... are out of scope
    panel_env = {**base_env, "ARGUS_URL": env.argus_url, "ARGUS_ADMIN_TOKEN": ADMIN_TOKEN,
                 "LITELLM_URL": dead, "LITELLM_MASTER_KEY": "unused", "AUTHELIA_USERS_FILE": str(users),
                 "PROMETHEUS_URL": dead, "GRAFANA_PROBE_URL": dead, "AUTHELIA_PROBE_URL": dead,
                 "REQUIRE_FORWARDED": "1", "LLM_DOMAIN": "llm.localhost"}
    port = free_port()
    env.panel_url = f"http://127.0.0.1:{port}"
    proc = env.start([env.args.console_python, "-m", "uvicorn", "app:app", "--host", "127.0.0.1", "--port", str(port)],
                     work / "admin-panel.log", panel_env, cwd=REPO / "stack/deploy/admin-panel")
    wait_http(env.panel_url + "/healthz", proc)
    print(f"  argus   {env.argus_url}\n  console {env.panel_url}\n  packs   {env.pack_index_url}")


# --- the browser ---------------------------------------------------------------------

ADMIN_HEADERS = {"x-forwarded-host": "admin.llm.localhost", "remote-user": "admin",
                 "remote-email": "admin@example.invalid", "remote-groups": "admins"}
USER_HEADERS = {"x-forwarded-host": "admin.llm.localhost", "remote-user": "alice",
                "remote-email": "alice@example.invalid", "remote-groups": "users"}


def browser_flows(env: Env) -> None:
    from playwright.sync_api import sync_playwright

    shots = env.work / "screens"
    with sync_playwright() as p:
        browser = p.chromium.launch()
        ctx = browser.new_context(extra_http_headers=ADMIN_HEADERS, viewport={"width": 1400, "height": 1000})
        page = ctx.new_page()
        page.on("dialog", lambda d: d.accept())
        errors: list[str] = []
        page.on("pageerror", lambda e: errors.append(str(e)))
        url = env.panel_url

        def text() -> str:
            return page.inner_text("body")

        def shot(name: str) -> None:
            page.screenshot(path=str(shots / f"{name}.png"), full_page=True)

        section("web UI: overview before any index")
        page.goto(url + "/")
        body = text()
        # innerText applies text-transform, and the tile labels are uppercased by CSS.
        check("ui", "overview renders for an admin", "code index" in body.lower())
        check("ui", "empty index is called out", "No repository is indexed" in body, "alert text")
        shot("01-overview-empty")

        section("web UI: index from the Indexing page")
        page.goto(url + "/indexing")
        body = text()
        check("ui", "indexing card present", "Index all repos" in body)
        check("ui", "schedule state read from Argus", "Automatic reindexing is off" in body)
        page.fill("input[name=branches]", "release/*")
        since = time.time()
        page.click("text=Index all repos")
        page.wait_for_load_state()
        check("ui", "start acknowledged", "Indexing started across all repos (release/*)" in text())
        first = argus_get(env, "/admin/index/status").get("job") or {}
        if first.get("state") == "running":
            shot("02-indexing-running")
            check("ui", "running pass shown with its trigger", "started by this console" in text())
            again = ctx.request.post(url + "/admin/index", form={"branches": ""}, max_redirects=0)
            page.goto(url + again.headers.get("location", "/indexing"))
            check("ui", "second start refused while running", "already in progress" in text(),
                  again.headers.get("location", "")[:80])
        started = time.time()
        job = wait_job(env, "index", since)
        env.index_seconds = time.time() - since
        page.goto(url + "/indexing")
        body = text()
        check("ui", "pass finished with exit 0", job.get("returncode") == 0 and "exit 0: completed" in body,
              f"rc={job.get('returncode')} in {time.time() - started:.1f}s")
        for repo in ("oss/zlib", "oss/libpng", "oss/freetype", "acme/docs-demo", "acme/secret"):
            check("ui", f"{repo} listed", repo in body)
        check("ui", "release/v2 indexed as an extra branch", "release/v2" in body)
        check("ui", "run log kept after the pass", "Log from the last run" in body)
        shot("03-indexing-done")

        page.goto(url + "/")
        body = text()
        refs = len(argus_get(env, "/admin/index/status").get("repos") or [])
        check("ui", "overview tile shows every ref current", f"{refs}/{refs}" in body and "repositories current" in body,
              f"{refs} refs")
        check("ui", "no stale alert", "stale index" not in body)
        shot("04-overview-indexed")

        section("web UI: Explore")
        page.goto(url + "/explore")
        body = text()
        check("ui", "explore lists repositories with counts", "oss/zlib" in body and "symbols" in body)
        page.fill("input[name=q]", "png_read_image")
        page.click("button:has-text('Search')")
        page.wait_for_load_state()
        body = text()
        check("ui", "symbol search finds png_read_image in libpng", "png_read_image" in body and "oss/libpng" in body)
        shot("05-explore-symbol")
        page.fill("input[name=q]", "adler32")
        page.select_option("select[name=repo]", "oss/zlib")
        page.click("button:has-text('Search')")
        page.wait_for_load_state()
        rows = page.locator("table").first.inner_text()
        check("ui", "repository filter narrows to zlib", "oss/zlib" in rows and "oss/libpng" not in rows)
        page.goto(url + "/explore?q=vault_unlock")
        check("ui", "estate-wide view shows the private repo to the operator", "acme/secret" in text())
        page.goto(url + "/explore?q=" + urllib.parse.quote("100%"))
        check("ui", "a LIKE wildcard in the query is literal, not an error", "could not read" not in text().lower())

        section("web UI: knowledge packs")
        page.goto(url + "/packs")
        body = text()
        check("ui", "no packs yet", "packs installed" in body.lower() and "none yet" in body)
        check("ui", "pack index URL shown", env.pack_index_url in body)

        def submit_install(source: str, digest: str) -> dict:
            since = time.time()
            page.fill("input[name=source]", source)
            page.fill("input[name=sha256]", digest)
            page.click("button:has-text('Install pack')")
            page.wait_for_load_state()
            job = wait_job(env, "packs", since)
            page.goto(url + "/packs")
            return job

        job = submit_install(str(env.python_10), "0" * 64)
        body = text()
        check("ui", "digest mismatch refused", job.get("returncode") != 0 and "failed" in body,
              (job.get("tail") or ["?"])[-1][:100])
        check("ui", "nothing installed after a refused install", "none yet" in body)
        job = submit_install(str(env.python_10), sha256(env.python_10))
        check("ui", "install with the right digest", job.get("returncode") == 0 and "finished cleanly" in text())
        job = submit_install(env.pack_index_url.replace("index.json", "win32.arguspack"), "")
        body = text()
        check("ui", "install over HTTP without a digest", job.get("returncode") == 0)
        rows = page.locator("table").first.inner_text()
        check("ui", "both packs listed with versions", "python" in rows and "win32" in rows and "1.0" in rows)
        shot("06-packs-installed")

        since = time.time()
        page.click("button:has-text('Update all packs')")
        page.wait_for_load_state()
        job = wait_job(env, "packs", since)
        page.goto(url + "/packs")
        rows = page.locator("table").first.inner_text()
        python_row = next((line for line in rows.splitlines() if line.startswith("python")), "")
        check("ui", "update moved python to 1.1", job.get("returncode") == 0 and "1.1" in python_row, python_row.strip())
        shot("07-packs-updated")

        since = time.time()
        page.locator("tr", has_text="python").locator("button:has-text('Remove')").click()
        page.wait_for_load_state()
        try:
            wait_job(env, "packs", since, timeout=60)
        except SystemExit:
            pass                                    # removal may be synchronous
        page.goto(url + "/packs")
        rows = page.locator("table").first.inner_text()
        check("ui", "remove (after the confirm dialog) drops the pack", "python" not in rows and "win32" in rows)
        shot("08-packs-removed")

        section("web UI: a GitLab push reaches the index")
        fixtures.mutate(env.trees, env.repos)
        head = subprocess.run(["git", "--git-dir", str(env.repos / "oss/zlib.git"), "rev-parse", "main"],
                              capture_output=True, text=True).stdout.strip()
        since = time.time()
        status, body, _ = http("POST", env.argus_url + "/hook/gitlab",
                               {"object_kind": "push", "after": head, "ref": "refs/heads/main",
                                "project": {"path_with_namespace": "oss/zlib"}}, {"x-gitlab-token": WEBHOOK_TOKEN})
        check("api", "webhook accepted", status in (200, 202), f"{status} {body[:80]}")
        job = wait_job(env, "index", since)
        check("api", "webhook pass recorded as such", job.get("trigger") == "webhook" and job.get("returncode") == 0,
              f"trigger={job.get('trigger')} rc={job.get('returncode')}")
        page.goto(url + "/explore?q=adler32_whole")
        check("ui", "the pushed function is searchable", "adler32_whole" in text())
        shot("09-explore-after-push")

        section("web UI: who may see what")
        user_ctx = browser.new_context(extra_http_headers=USER_HEADERS)
        upage = user_ctx.new_page()
        upage.goto(url + "/indexing")
        body = upage.inner_text("body")
        check("ui", "a non-admin gets no indexing controls", "Index all repos" not in body and "oss/zlib" not in body)
        status, _, _ = http("POST", url + "/admin/index", {"branches": ""}, USER_HEADERS, form=True)
        check("ui", "a non-admin POST is refused", status == 403, str(status))
        status, _, _ = http("POST", url + "/admin/packs", {"action": "remove", "name": "win32"}, USER_HEADERS, form=True)
        check("ui", "a non-admin cannot remove packs", status == 403, str(status))
        status, _, _ = http("GET", url + "/", headers={"remote-user": "admin", "remote-groups": "admins"})
        check("ui", "no proxy headers, no page", status == 403, str(status))
        user_ctx.close()

        check("ui", "no JavaScript errors on any page", not errors, "; ".join(errors)[:200])
        browser.close()


# --- MCP, as the chat front end and desktop clients use it -----------------------------


def mcp_flows(env: Env) -> None:
    from mcp import ClientSession, StdioServerParameters
    from mcp.client.stdio import stdio_client
    from mcp.client.streamable_http import streamablehttp_client

    url = env.argus_url + "/mcp"

    async def session(token: str, extra: dict | None = None, fn=None):
        headers = {"Authorization": f"Bearer {token}", **(extra or {})}
        async with streamablehttp_client(url, headers=headers) as (read, write, _):
            async with ClientSession(read, write) as s:
                init = await s.initialize()
                return await fn(s, init)

    def rows(result) -> list:
        if result.isError:
            return []
        sc = result.structuredContent or {}
        value = sc.get("result", sc)
        return value if isinstance(value, list) else [value]

    def denied(result, repo: str) -> bool:
        """Refused the way Argus refuses: a tool error naming where the thing
        exists and how to get access -- with no row of it leaked."""
        text = result.content[0].text if result.content else ""
        return bool(result.isError and "cannot read" in text and repo in text and not result.structuredContent)

    async def as_alice(s, init):
        out = {"init": init, "tools": (await s.list_tools()).tools}
        out["png"] = await s.call_tool("find_symbol", {"name": "png_read_image"})
        out["vault"] = await s.call_tool("find_symbol", {"name": "vault_unlock"})
        out["sem"] = await s.call_tool("semantic_search", {"query": "compute a checksum over a buffer", "limit": 5})
        out["overview"] = await s.call_tool("overview", {})
        out["which"] = await s.call_tool("which_repo", {"description": "add a faster checksum to the deflate stream"})
        out["refs"] = await s.call_tool("find_references", {"name": "adler32"})
        out["pydoc"] = await s.call_tool("find_symbol", {"name": "expire_keys"})
        out["search"] = await s.call_tool("search_code", {"query": "jpeg_start_decompress"})
        out["lookup"] = await s.call_tool("docs_lookup", {"name": "MessageBox"})
        out["verify"] = await s.call_tool("docs_verify", {"text": "MessageBoxW is declared in winuser.h and lives in shell32.dll."})
        out["unknown"] = await s.call_tool("no_such_tool", {})
        out["badargs"] = await s.call_tool("find_symbol", {})
        status = rows(await s.call_tool("index_status", {}))
        zlib = next(r["repo_id"] for r in status if r["path_with_namespace"] == "oss/zlib" and r["branch"] == r["default_branch"])
        out["file"] = await s.call_tool("get_file", {"repo_id": zlib, "path": "zlib.h"})
        out["impact"] = await s.call_tool("impact_of", {"repo_id": zlib, "path": "zlib.h"})
        # Latency, over the wire, for the calls a chat turn makes most.
        timings = []
        for _ in range(60):
            t = time.perf_counter()
            await s.call_tool("find_symbol", {"name": "inflate"})
            timings.append((time.perf_counter() - t) * 1000)
        out["latency"] = timings
        return out

    section("MCP over streamable HTTP (official Python SDK)")
    a = asyncio.run(session("alice-token", fn=as_alice))
    check("mcp", "initialize names the server", a["init"].serverInfo.name == "argus", a["init"].serverInfo.name)
    check("mcp", "server instructions sent", bool(a["init"].instructions) and "overview" in a["init"].instructions)
    check("mcp", "17 tools listed", len(a["tools"]) == 17, str(len(a["tools"])))
    check("mcp", "every tool has an output schema", all(t.outputSchema for t in a["tools"]))
    png = rows(a["png"])
    check("mcp", "find_symbol returns structured rows", png and png[0].get("path_with_namespace") == "oss/libpng",
          f"{len(png)} rows")
    check("mcp", "text content mirrors structured content", len(a["png"].content) == len(png))
    check("mcp", "alice is told acme/secret exists but is not hers", denied(a["vault"], "acme/secret"),
          a["vault"].content[0].text[:90] if a["vault"].content else "")
    sem = rows(a["sem"])
    check("mcp", "semantic search answers with docs", sem and all("doc" in r for r in sem),
          ", ".join(r.get("name", "?") for r in sem[:3]))
    ov = rows(a["overview"])
    check("mcp", "overview lists alice's repositories only",
          ov and "acme/secret" not in json.dumps(ov) and "oss/zlib" in json.dumps(ov))
    which = rows(a["which"])
    check("mcp", "which_repo ranks zlib first for a deflate change",
          which and which[0].get("path_with_namespace") == "oss/zlib", which[0].get("path_with_namespace") if which else "none")
    pydoc = rows(a["pydoc"])
    check("mcp", "a Python docstring is served as the symbol's doc",
          pydoc and pydoc[0].get("doc", "").startswith("Remove every key whose time to live has elapsed"),
          (pydoc[0].get("doc") or "")[:60] if pydoc else "none")
    check("mcp", "find_references finds adler32 callers", len(rows(a["refs"])) > 0, f"{len(rows(a['refs']))} rows")
    check("mcp", "search_code full-text hit", any("jpeg" in json.dumps(r) for r in rows(a["search"])))
    lookup_rows = rows(a["lookup"])
    check("mcp", "docs_lookup served from the installed win32 pack",
          lookup_rows and lookup_rows[0].get("source") == "win32" and lookup_rows[0].get("url"),
          lookup_rows[0].get("url", "") if lookup_rows else "none")
    fixes = [c for f in rows(a["verify"]) for c in f.get("corrections", [])]
    check("mcp", "docs_verify flags the wrong DLL, quoting both sides",
          fixes == [{"field": "dll", "documented": "User32.dll", "status": "contradicted", "stated": "shell32.dll"}],
          json.dumps(fixes)[:120])
    check("mcp", "unknown tool is a tool error", a["unknown"].isError and "Unknown tool: no_such_tool" in a["unknown"].content[0].text)
    check("mcp", "missing argument is a validation error", a["badargs"].isError and "name" in a["badargs"].content[0].text)
    f = rows(a["file"])[0] if rows(a["file"]) else {}
    check("mcp", "get_file returns the file", "ZLIB_VERSION" in (f.get("content") or ""), f.get("path", "none"))
    check("mcp", "impact_of walks includes", len(json.dumps(rows(a["impact"]))) > 50)
    lat = sorted(a["latency"])
    env.latency = (statistics.median(lat), lat[int(len(lat) * 0.95) - 1])
    check("mcp", "find_symbol median latency under 50 ms", env.latency[0] < 50,
          f"p50 {env.latency[0]:.1f} ms, p95 {env.latency[1]:.1f} ms")

    async def vault(s, init):
        return await s.call_tool("find_symbol", {"name": "vault_unlock"})

    async def png_lookup(s, init):
        return await s.call_tool("find_symbol", {"name": "png_read_image"})

    carol = rows(asyncio.run(session("carol-token", fn=vault)))
    check("mcp", "carol (maintainer) sees acme/secret", carol and carol[0].get("path_with_namespace") == "acme/secret")
    bob = asyncio.run(session("bob-token", fn=png_lookup))
    check("mcp", "bob (guest only) reads nothing of oss/libpng", denied(bob, "oss/libpng"))

    section("MCP as the chat front end calls it (service token + forwarded user)")
    chat = rows(asyncio.run(session(CHAT_TOKEN, {"x-openwebui-user-email": "alice@example.invalid"}, fn=png_lookup)))
    check("mcp", "chat client acts as alice", chat and chat[0].get("path_with_namespace") == "oss/libpng")
    chat_secret = asyncio.run(session(CHAT_TOKEN, {"x-openwebui-user-email": "alice@example.invalid"}, fn=vault))
    check("mcp", "and inherits alice's limits", denied(chat_secret, "acme/secret"))
    chat_carol = rows(asyncio.run(session(CHAT_TOKEN, {"x-openwebui-user-email": "carol@example.invalid"}, fn=vault)))
    check("mcp", "a different forwarded user gets their own view", bool(chat_carol))

    def refused(token, extra=None) -> str:
        try:
            asyncio.run(session(token, extra, fn=png_lookup))
            return ""
        except BaseException as exc:            # the SDK raises from inside a task group
            return f"{type(exc).__name__}: {exc}"[:120] or "refused"

    why = refused(CHAT_TOKEN, {"x-openwebui-user-email": "alice@example.invalid", "x-forwarded-for": "203.0.113.9"})
    check("mcp", "chat credential refused through the public proxy", bool(why), why[:80])
    why = refused("not-a-token")
    check("mcp", "an unknown bearer is refused", bool(why), why[:80])

    section("MCP over stdio (desktop clients)")

    async def stdio():
        params = StdioServerParameters(command=env.argus, args=["serve", "--stdio", "--config", str(env.cfg)],
                                       env={**env.argus_env, "ARGUS_TOKEN": "alice-token"})
        async with stdio_client(params) as (read, write):
            async with ClientSession(read, write) as s:
                await s.initialize()
                tools = (await s.list_tools()).tools
                hit = await s.call_tool("find_symbol", {"name": "png_read_image"})
                secret = await s.call_tool("find_symbol", {"name": "vault_unlock"})
                return tools, hit, secret

    tools, hit, secret = asyncio.run(stdio())
    check("mcp", "stdio: 17 tools", len(tools) == 17)
    check("mcp", "stdio: same answer as HTTP", rows(hit) == png)
    check("mcp", "stdio: same access limits", denied(secret, "acme/secret"))


# --- HTTP surface, metrics and CLI --------------------------------------------------------


def http_and_cli(env: Env) -> None:
    from prometheus_client.parser import text_string_to_metric_families

    section("HTTP surface")
    status, body, _ = http("GET", env.argus_url + "/healthz")
    check("api", "healthz", status == 200 and json.loads(body).get("status") == "ok", body[:80])
    status, _, headers = http("POST", env.argus_url + "/mcp", {"jsonrpc": "2.0", "id": 1, "method": "ping"})
    check("api", "mcp without a bearer is 401 with a challenge",
          status == 401 and any(k.lower() == "www-authenticate" for k in headers), str(status))
    status, _, _ = http("POST", env.argus_url + "/mcp", {"jsonrpc": "2.0", "id": 1, "method": "ping"},
                        {"Authorization": "Bearer alice-token", "Host": "evil.example"})
    check("api", "DNS-rebinding Host is refused", status in (400, 403, 421), str(status))
    status, _, _ = http("GET", env.argus_url + "/admin/packs", headers={"x-argus-admin-token": "wrong"})
    check("api", "admin surface refuses a wrong token", status in (401, 403), str(status))
    status, _, _ = http("POST", env.argus_url + "/hook/gitlab", {"object_kind": "push"}, {"x-gitlab-token": "wrong"})
    check("api", "webhook refuses a wrong secret", status in (401, 403), str(status))

    status, text, headers = http("GET", env.argus_url + "/admin/metrics", headers={"x-argus-admin-token": ADMIN_TOKEN})
    families = {f.name: f for f in text_string_to_metric_families(text)}
    refs = len(argus_get(env, "/admin/index/status").get("repos") or [])
    check("api", "metrics parse with the Prometheus parser", status == 200 and "argus_index_repos" in families,
          f"{len(families)} families")
    check("api", "metrics: one freshness sample per ref",
          len(families["argus_index_last_run_timestamp_seconds"].samples) == refs, f"{refs} refs")
    check("api", "metrics: nothing stale or errored",
          families["argus_index_stale_repos"].samples[0].value == 0 and families["argus_index_errored_repos"].samples[0].value == 0)
    ctype = next((v for k, v in headers.items() if k.lower() == "content-type"), "")
    check("api", "metrics content type is the text exposition", ctype.startswith("text/plain"), ctype)

    section("CLI against the live index")

    def cli(*args, codes=(0,)):
        proc = subprocess.run([env.argus, *args], env=env.argus_env, capture_output=True, text=True, timeout=300)
        return proc.returncode in codes, proc

    ok, proc = cli("status", "--config", str(env.cfg))
    check("cli", "status", ok and "oss/zlib" in proc.stdout, proc.stdout.splitlines()[0][:80] if proc.stdout else proc.stderr[:80])
    ok, proc = cli("kpi", "--config", str(env.cfg), "--json")
    k = json.loads(proc.stdout) if ok else {}
    check("cli", "kpi --json", ok and k.get("repos", 0) > 0, f"repos={k.get('repos')} symbols={k.get('symbols')}")
    ok, proc = cli("verify", "--config", str(env.cfg), "--text", "MessageBoxW lives in shell32.dll.", "--json", codes=(0, 2, 6))
    check("cli", "verify blocks a contradicted draft (exit 2)",
          proc.returncode == 2 and "you said 'shell32.dll'; the documentation says 'User32.dll'" in proc.stderr,
          f"exit {proc.returncode}")
    ok, proc = cli("verify", "--config", str(env.cfg), "--text", "MessageBoxW lives in User32.dll.", "--quiet")
    check("cli", "verify passes a right draft (exit 0)", ok, f"exit {proc.returncode}")
    ok, proc = cli("pack", "list", "--config", str(env.cfg))
    check("cli", "pack list shows what the console installed", ok and "win32" in proc.stdout)
    ok, proc = cli("healthcheck", "--url", env.argus_url + "/healthz")
    check("cli", "healthcheck", ok)


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__.splitlines()[0])
    parser.add_argument("--work", type=Path, default=Path("/tmp/argus-e2e"))
    parser.add_argument("--corpus-cache", type=Path, default=None)
    parser.add_argument("--console-python", default=sys.executable,
                        help="Interpreter for the admin console: 3.12+ with its image's packages")
    parser.add_argument("--dotnet", default=os.environ.get("ARGUS_DOTNET",
                        str(REPO / "dotnet/src/Argus/bin/Release/net10.0/argus")))
    args = parser.parse_args()
    env = Env(args)
    try:
        setup(env)
        browser_flows(env)
        mcp_flows(env)
        http_and_cli(env)
    finally:
        env.stop()

    failed = [r for r in RESULTS if not r[2]]
    section("result")
    by_area: dict[str, list[bool]] = {}
    for area, _n, ok, _d in RESULTS:
        by_area.setdefault(area, []).append(ok)
    for area, oks in by_area.items():
        print(f"  {area:<6} {sum(oks)}/{len(oks)}")
    print(f"  first index pass (5 C projects + 2 repos, 3 branches): {getattr(env, 'index_seconds', 0):.1f}s")
    if hasattr(env, "latency"):
        print(f"  find_symbol over MCP/HTTP: p50 {env.latency[0]:.1f} ms, p95 {env.latency[1]:.1f} ms")
    print(f"  screenshots: {args.work / 'screens'}")
    for area, name, _ok, detail in failed:
        print(f"   - FAIL {area}: {name} {detail}")
    (args.work / "results.json").write_text(json.dumps(
        {"checks": [dict(zip(("area", "name", "ok", "detail"), r)) for r in RESULTS],
         "index_seconds": getattr(env, "index_seconds", None),
         "latency_ms": getattr(env, "latency", None)}, indent=2))
    return len(failed)


if __name__ == "__main__":
    raise SystemExit(main())
