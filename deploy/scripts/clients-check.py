#!/usr/bin/env python3
"""
Clients against the stack, as a developer points them: the API (OpenAI- and
Anthropic-compatible, through the gateway's TLS with the stack's CA) and Argus
over MCP, directly and through Qwen Code and DeepSeek Harness.

    python3 scripts/clients-check.py
    python3 scripts/clients-check.py --qwen ~/.local/lib/qwen-code/bin/qwen --dsh /path/to/node_modules/.bin/dsh

It signs in as the admin (ADMIN_PASSWORD in .env), makes a test person with an
API key (removed at the end unless --keep-person), and uses the test GitLab's
dev_alpha token for Argus (tools/test-gitlab/seeded.json). Each client runs with
a throwaway home, so your own ~/.qwen and DeepSeek Harness settings are never
read or changed. Secrets are never printed. Exit code 0 only if every check that
ran passed; a check that cannot apply here says SKIP.
"""
from __future__ import annotations

import argparse
import http.cookiejar
import json
import os
import shutil
import ssl
import subprocess
import sys
import tempfile
import time
import urllib.error
import urllib.request
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent
REPO = ROOT.parent
CA = ROOT / "config" / "traefik" / "certs" / "ca.crt"
GREEN, RED, YELLOW, DIM, OFF = "\033[32m", "\033[31m", "\033[33m", "\033[90m", "\033[0m"
results: list[tuple[str, str]] = []


def env(key: str, default: str = "") -> str:
    for line in (ROOT / ".env").read_text().splitlines():
        if line.startswith(key + "="):
            return line.split("=", 1)[1].strip()
    return default


def record(name: str, state: str, detail: str = "") -> bool:
    colour = {"PASS": GREEN, "FAIL": RED, "SKIP": YELLOW}[state]
    print(f"  {colour}{state}{OFF}  {name}" + (f"  {DIM}({detail[:300]}){OFF}" if detail else ""))
    results.append((name, state))
    return state == "PASS"


def check(name: str, ok: bool, detail: str = "") -> bool:
    return record(name, "PASS" if ok else "FAIL", detail)


TLS = ssl.create_default_context(cafile=str(CA)) if CA.exists() else ssl.create_default_context()
jar = http.cookiejar.CookieJar()
session = urllib.request.build_opener(urllib.request.HTTPCookieProcessor(jar), urllib.request.HTTPSHandler(context=TLS))


def call(url: str, body: object | None = None, headers: dict | None = None, method: str | None = None, opener=None, timeout: int = 180):
    """Status, headers and body (parsed JSON when it is JSON)."""
    data = json.dumps(body).encode() if body is not None else None
    req = urllib.request.Request(url, data=data, method=method or ("POST" if body is not None else "GET"))
    req.add_header("Content-Type", "application/json")
    for k, v in (headers or {}).items():
        req.add_header(k, v)
    try:
        with (opener or session).open(req, timeout=timeout) as res:
            raw = res.read().decode("utf-8", "replace")
            status, hdrs = res.status, dict(res.headers)
    except urllib.error.HTTPError as e:
        raw, status, hdrs = e.read().decode("utf-8", "replace"), e.code, dict(e.headers)
    try:
        return status, hdrs, json.loads(raw)
    except json.JSONDecodeError:
        return status, hdrs, raw


def text_of(completion: dict) -> str:
    return ((completion.get("choices") or [{}])[0].get("message") or {}).get("content") or ""


def mcp_session(url: str, token: str):
    """A streamable-HTTP MCP session: initialize, then calls; answers may come as SSE."""
    sid: dict[str, str] = {}

    def rpc(method: str, params: dict | None = None, notify: bool = False):
        body = {"jsonrpc": "2.0", "method": method, **({"params": params} if params is not None else {})}
        if not notify:
            body["id"] = int(time.time() * 1000)
        headers = {"Authorization": f"Bearer {token}", "Accept": "application/json, text/event-stream"}
        if "id" in sid:
            headers["Mcp-Session-Id"] = sid["id"]
        status, hdrs, raw = call(url, body, headers)
        if "Mcp-Session-Id" in hdrs:
            sid["id"] = hdrs["Mcp-Session-Id"]
        if isinstance(raw, str) and "data:" in raw:
            for line in raw.splitlines():
                if line.startswith("data:"):
                    raw = json.loads(line[5:])
        return status, raw

    return rpc


def run_client(argv: list[str], env_add: dict, cwd: Path, timeout: int = 420) -> tuple[int, str]:
    home = cwd / ".home"
    home.mkdir(exist_ok=True)
    e = {"PATH": os.environ.get("PATH", ""), "HOME": str(home), "NODE_EXTRA_CA_CERTS": str(CA), "LANG": "C.UTF-8", **env_add}
    try:
        p = subprocess.run(argv, cwd=cwd, env=e, capture_output=True, text=True, timeout=timeout)
        return p.returncode, p.stdout + "\n" + p.stderr
    except subprocess.TimeoutExpired as t:
        return -1, f"timed out after {timeout} s: {(t.stdout or b'')!s:.300}"


def main() -> int:
    ap = argparse.ArgumentParser()
    ap.add_argument("--qwen", default=str(Path.home() / ".local/lib/qwen-code/bin/qwen"))
    ap.add_argument("--dsh", default=shutil.which("dsh") or "")
    ap.add_argument("--keep-person", action="store_true")
    args = ap.parse_args()

    domain = env("LLM_DOMAIN", "llm.localhost") or "llm.localhost"
    app, base, argus = f"https://{domain}", f"https://gateway.{domain}/v1", f"https://argus.{domain}/mcp"
    xhr = {"X-Requested-With": "fetch"}
    print(f"Clients against {domain} (TLS with {CA.relative_to(REPO) if CA.exists() else 'system roots'})")

    status, _, _ = call(f"{app}/api/auth/login", {"userName": "admin", "password": env("ADMIN_PASSWORD")}, xhr)
    if not check("the admin signs in", status == 200, str(status)):
        return 1
    _, _, cfg = call(f"{app}/api/chat/config", headers=xhr)
    chat_models = [m for m in cfg.get("models", []) if m.get("loaded")]
    if not chat_models:
        record("a model is serving", "FAIL", "no loaded chat model")
        return 1
    model = (cfg.get("model") and next((m for m in chat_models if m["name"] == cfg["model"]), None)) or chat_models[0]
    name = f"clients-{int(time.time())}"
    status, _, made = call(f"{app}/api/admin/people", {"userName": name, "email": f"{name}@example.test", "displayName": "Clients check"}, xhr)
    check("a test person is made", status in (200, 201), str(status))
    pid = made.get("id")
    _, _, secrets = call(f"{app}/api/admin/people/{pid}/key", {}, xhr)
    key = secrets.get("apiKey") or ""
    check("the person gets an API key", key.startswith("sk-"), "key not shown")
    auth = {"Authorization": f"Bearer {key}"}

    try:
        print("API")
        status, _, listed = call(f"{base}/models", headers=auth)
        ids = [m.get("id") for m in (listed.get("data", []) if isinstance(listed, dict) else [])]
        check("GET /v1/models lists the models the key may use", status == 200 and model["name"] in ids, f"{status}: {', '.join(map(str, ids))[:200]}")
        check("no key is refused", call(f"{base}/models")[0] == 401)
        check("a wrong key is refused", call(f"{base}/models", headers={"Authorization": "Bearer sk-wrong"})[0] == 401)
        ask = {"model": model["name"], "messages": [{"role": "user", "content": "Reply with the single word PONG."}], "max_tokens": 400,
               "chat_template_kwargs": {"enable_thinking": False}}
        status, _, done = call(f"{base}/chat/completions", ask, auth)
        check("a chat completion answers", status == 200 and "PONG" in text_of(done).upper(), f"{status}: {text_of(done)[:80] if isinstance(done, dict) else done[:120]}")
        check("and reports its tokens", isinstance(done, dict) and (done.get("usage") or {}).get("total_tokens", 0) > 0)

        req = urllib.request.Request(f"{base}/chat/completions", data=json.dumps({**ask, "stream": True}).encode(), method="POST",
                                     headers={"Content-Type": "application/json", **auth})
        chunks, said = 0, ""
        with urllib.request.urlopen(req, context=TLS, timeout=180) as res:
            for line in res:
                line = line.decode().strip()
                if line.startswith("data:") and line != "data: [DONE]":
                    chunks += 1
                    said += ((json.loads(line[5:]).get("choices") or [{}])[0].get("delta") or {}).get("content") or ""
        check("streaming arrives in pieces", chunks > 1 and "PONG" in said.upper(), f"{chunks} chunks: {said[:60]}")

        tools = [{"type": "function", "function": {"name": "get_weather", "description": "The weather in a city now.",
                                                   "parameters": {"type": "object", "properties": {"city": {"type": "string"}}, "required": ["city"]}}}]
        status, _, toolish = call(f"{base}/chat/completions", {**ask, "messages": [{"role": "user", "content": "What is the weather in Tehran now? Use the tool."}], "tools": tools}, auth)
        calls = ((toolish.get("choices") or [{}])[0].get("message") or {}).get("tool_calls") if isinstance(toolish, dict) else None
        args_ok = bool(calls) and calls[0]["function"]["name"] == "get_weather" and "tehran" in json.loads(calls[0]["function"]["arguments"] or "{}").get("city", "").lower()
        check("tool calling returns a call with JSON arguments", status == 200 and args_ok, f"{status}: {json.dumps(calls)[:160]}")

        status, _, anth = call(f"https://gateway.{domain}/v1/messages", {"model": model["name"], "max_tokens": 400,
                                                                          "messages": [{"role": "user", "content": "Reply with the single word PONG."}]},
                               {"x-api-key": key, "anthropic-version": "2023-06-01"})
        said = "".join(b.get("text", "") for b in anth.get("content", [])) if isinstance(anth, dict) else ""
        check("the Anthropic Messages API answers too (Claude Code)", status == 200 and "PONG" in said.upper(), f"{status}: {said[:60] or str(anth)[:120]}")

        time.sleep(3)
        _, _, people = call(f"{app}/api/admin/people", headers=xhr)
        spent = next((p.get("spend") for p in people.get("people", []) if p.get("id") == pid), None)
        priced = model.get("prices", {}).get("input")
        if priced:
            check("the requests are the person's spend", (spent or 0) > 0, f"spend {spent}")
        else:
            record("the requests are the person's spend", "SKIP", "the model has no price")

        print("Argus over MCP")
        seeded = REPO / "tools" / "test-gitlab" / "seeded.json"
        token = json.loads(seeded.read_text())["users"]["dev_alpha"]["token"] if seeded.exists() else ""
        if not token:
            record("Argus MCP", "SKIP", "no test GitLab token (tools/test-gitlab/run.sh)")
        else:
            rpc = mcp_session(argus, token)
            status, init = rpc("initialize", {"protocolVersion": "2025-06-18", "capabilities": {}, "clientInfo": {"name": "clients-check", "version": "1"}})
            check("MCP initialize, with a GitLab token", status == 200 and "result" in init, f"{status}")
            rpc("notifications/initialized", notify=True)
            _, listed = rpc("tools/list", {})
            names = [t["name"] for t in listed.get("result", {}).get("tools", [])]
            check("tools/list", "find_symbol" in names, f"{len(names)} tools")
            _, found = rpc("tools/call", {"name": "find_symbol", "arguments": {"name": "DecodeFrame"}})
            check("find_symbol finds DecodeFrame in root/eal-core", "root/eal-core" in json.dumps(found), json.dumps(found)[:160])
            docs = next((n for n in names if "doc" in n and ("search" in n or "lookup" in n)), None)
            if docs:
                _, hit = rpc("tools/call", {"name": docs, "arguments": {"query": "CreateFileW", "name": "CreateFileW"}})
                check(f"the Windows packs answer ({docs} CreateFileW)", "CreateFile" in json.dumps(hit), json.dumps(hit)[:160])
            status, _, _ = call(argus, {"jsonrpc": "2.0", "id": 1, "method": "tools/list"}, {"Authorization": "Bearer glpat-wrong", "Accept": "application/json, text/event-stream"})
            check("a wrong GitLab token is refused", status in (401, 403), str(status))

        work = Path(tempfile.mkdtemp(prefix="clients-"))
        openai = {"OPENAI_API_KEY": key, "OPENAI_BASE_URL": base, "OPENAI_MODEL": model["name"]}
        print("Qwen Code")
        if not Path(args.qwen).exists():
            record("Qwen Code", "SKIP", f"not found at {args.qwen}")
        else:
            q = work / "qwen"
            q.mkdir()
            code, out = run_client([args.qwen, "-p", "Reply with the single word PONG."], openai, q)
            check("Qwen Code answers through the gateway", code == 0 and "PONG" in out.upper(), out.strip()[-160:])
            code, out = run_client([args.qwen, "--yolo", "-p", "Create a file named answer.txt whose whole content is the number 42. Use your file tools, then say done."], openai, q)
            wrote = (q / "answer.txt").read_text().strip() if (q / "answer.txt").exists() else ""
            check("Qwen Code uses its tools (writes a file)", wrote == "42", f"exit {code}; answer.txt: {wrote!r}")
            if token:
                (q / ".qwen").mkdir(exist_ok=True)
                (q / ".qwen" / "settings.json").write_text(json.dumps({"mcpServers": {"argus": {
                    "httpUrl": argus, "headers": {"Authorization": f"Bearer {token}"}, "trust": True}}}))
                code, out = run_client([args.qwen, "--yolo", "-p", "Use the argus find_symbol tool to find the symbol DecodeFrame. Reply with the repository path it is in, only."], openai, q)
                check("Qwen Code calls Argus over MCP", "root/eal-core" in out, out.strip()[-160:])

        print("DeepSeek Harness")
        if not args.dsh or not Path(args.dsh).exists():
            record("DeepSeek Harness", "SKIP", "not found (pass --dsh)")
        else:
            d = work / "dsh"
            d.mkdir()
            patch = d / "stack.patch.yml"
            patch.write_text(f"""- id: llm-pi-ai
  config:
    providers:
      llm-service:
        apiKeyEnv: LLM_API_KEY
        api: openai-completions
        baseURL: {base}
        compat:
          supportsDeveloperRole: false
          maxTokensField: max_tokens
        models:
          - id: {model['name']}
            contextWindow: {model.get('context') or 32768}
            maxTokens: {model.get('maxOutput') or 8192}
- id: agent-default-model
  config:
    provider: llm-service
    model: {model['name']}
- insert:
  - name: '@deepseek-ai/dsh-mcp-client'
    config:
      serverName: argus
      transport: streamable-http
      url: {argus}
      headers:
        Authorization: !!js '`Bearer ${{process.env.ARGUS_TOKEN}}`'
""")
            dsh_env = {"LLM_API_KEY": key, "ARGUS_TOKEN": token, "DSH_HOME": str(d / ".dsh")}
            code, out = run_client([args.dsh, "--profile", "headless", "--patch", str(patch), "Reply with the single word PONG."], dsh_env, d)
            check("DeepSeek Harness answers through the gateway", code == 0 and "PONG" in out.upper(), out.strip()[-200:])
            if token:
                code, out = run_client([args.dsh, "--profile", "headless", "--patch", str(patch),
                                        "Use the mcp__argus__find_symbol tool with name=DecodeFrame. Reply with the repository path it is in, only."], dsh_env, d)
                check("DeepSeek Harness calls Argus over MCP", "root/eal-core" in out, out.strip()[-200:])
        shutil.rmtree(work, ignore_errors=True)
    finally:
        if pid and not args.keep_person:
            call(f"{app}/api/admin/people/{pid}", method="DELETE", headers=xhr)

    failed = [n for n, s in results if s == "FAIL"]
    print(f"\n{sum(1 for _, s in results if s == 'PASS')} passed, {len(failed)} failed, {sum(1 for _, s in results if s == 'SKIP')} skipped")
    return 1 if failed else 0


if __name__ == "__main__":
    sys.exit(main())
