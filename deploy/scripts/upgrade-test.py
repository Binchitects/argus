#!/usr/bin/env python3
"""
Upgrade and from-zero deployment test, with fresh volumes beside the live ones.

    python3 scripts/upgrade-test.py --from v3.0.0 --stop-live     # old release from zero, data in, this checkout over it
    python3 scripts/upgrade-test.py --zero --stop-live            # this checkout from zero

The test stack is its own compose project (its own volumes, its own copy of the
clean config and CA), with this deployment's .env otherwise: the same domain,
secrets and profiles. Container names are fixed, so the live stack must be down
meanwhile: --stop-live takes it down (`docker compose down`, volumes kept) and
brings it back at the end. The test project and its volumes are removed at the
end unless --keep.

Upgrade: the old release is checked out (a git worktree), built and started
from zero; a person, a group, a chat with an answer and a file go in; then this
checkout is built and started over the same volumes. It must come up at its
version, keep every one of those, answer in the old chat, and survive `up`
again and `down` then `up`. Secrets are never printed.
"""
from __future__ import annotations

import argparse
import http.cookiejar
import json
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
GREEN, RED, DIM, OFF = "\033[32m", "\033[31m", "\033[90m", "\033[0m"
results: list[tuple[str, bool]] = []


def check(name: str, ok: bool, detail: str = "") -> bool:
    print(f"  {GREEN + 'PASS' if ok else RED + 'FAIL'}{OFF}  {name}" + (f"  {DIM}({detail[:300]}){OFF}" if detail else ""), flush=True)
    results.append((name, ok))
    return ok


def env_value(key: str, default: str = "") -> str:
    for line in (ROOT / ".env").read_text().splitlines():
        if line.startswith(key + "="):
            return line.split("=", 1)[1].strip()
    return default


def sh(args: list[str], cwd: Path, timeout: int = 3600, quiet: bool = True) -> int:
    print(f"  {DIM}$ {' '.join(a for a in args)}{OFF}", flush=True)
    p = subprocess.run(args, cwd=cwd, timeout=timeout, capture_output=quiet, text=True)
    if p.returncode != 0 and quiet:
        print((p.stdout or "")[-1500:] + (p.stderr or "")[-1500:])
    return p.returncode


class Client:
    def __init__(self, domain: str, ca: Path):
        self.app = f"https://{domain}"
        self.tls = ssl.create_default_context(cafile=str(ca)) if ca.exists() else ssl._create_unverified_context()  # noqa: S323 (a fresh test CA)
        self.jar = http.cookiejar.CookieJar()
        self.open = urllib.request.build_opener(urllib.request.HTTPCookieProcessor(self.jar), urllib.request.HTTPSHandler(context=self.tls))

    def call(self, path: str, body: object | None = None, method: str | None = None, raw: bytes | None = None, ctype: str | None = None, timeout: int = 300):
        data = raw if raw is not None else (json.dumps(body).encode() if body is not None else None)
        req = urllib.request.Request(self.app + path, data=data, method=method or ("POST" if data is not None else "GET"))
        req.add_header("X-Requested-With", "fetch")
        req.add_header("Content-Type", ctype or "application/json")
        try:
            with self.open.open(req, timeout=timeout) as r:
                text = r.read().decode("utf-8", "replace")
                status = r.status
        except urllib.error.HTTPError as e:
            text, status = e.read().decode("utf-8", "replace"), e.code
        except (urllib.error.URLError, TimeoutError, ConnectionError) as e:
            return 0, str(e)
        try:
            return status, json.loads(text)
        except json.JSONDecodeError:
            return status, text

    def upload(self, name: str, content: bytes):
        boundary = "----upgradetest"
        body = (f"--{boundary}\r\nContent-Disposition: form-data; name=\"file\"; filename=\"{name}\"\r\nContent-Type: text/plain\r\n\r\n").encode() + content + f"\r\n--{boundary}--\r\n".encode()
        return self.call("/api/chat/attachments", raw=body, ctype=f"multipart/form-data; boundary={boundary}")


def wait_up(c: Client, minutes: int = 15) -> dict | None:
    end = time.time() + minutes * 60
    while time.time() < end:
        status, info = c.call("/api/info", timeout=10)
        if status == 200 and isinstance(info, dict):
            return info
        time.sleep(5)
    return None


def wait_model(c: Client, minutes: int = 15) -> str | None:
    end = time.time() + minutes * 60
    while time.time() < end:
        status, cfg = c.call("/api/chat/config", timeout=20)
        loaded = [m["name"] for m in (cfg.get("models", []) if isinstance(cfg, dict) else []) if m.get("loaded")]
        if status == 200 and loaded:
            return loaded[0]
        time.sleep(10)
    return None


def ask(c: Client, chat: str, text: str, attachments: list[str] | None = None) -> str:
    """Sends and reads the answer stream to its end: the answer's words."""
    status, body = c.call(f"/api/chat/conversations/{chat}/messages", {"content": text, "attachments": attachments or []}, timeout=600)
    words = ""
    for line in (body if isinstance(body, str) else "").split("\n"):
        if line.startswith("data: "):
            e = json.loads(line[6:])
            if e.get("type") == "content":
                words += e.get("text", "")
    return words if status == 200 else f"HTTP {status}"


def compose(compose_file: Path, env_file: Path, project: str, *args: str) -> list[str]:
    return ["docker", "compose", "-f", str(compose_file), "--env-file", str(env_file), "-p", project, *args]


def main() -> int:
    ap = argparse.ArgumentParser()
    g = ap.add_mutually_exclusive_group(required=True)
    g.add_argument("--from", dest="old", help="the release to upgrade from (a git tag)")
    g.add_argument("--zero", action="store_true", help="this checkout from zero")
    ap.add_argument("--stop-live", action="store_true")
    ap.add_argument("--keep", action="store_true")
    ap.add_argument("--profiles", default=env_value("COMPOSE_PROFILES"))
    args = ap.parse_args()

    live_project = env_value("COMPOSE_PROJECT_NAME", "llmservice")
    project = "llm-upgrade-test" if args.old else "llm-zero-test"
    domain = env_value("LLM_DOMAIN", "llm.localhost") or "llm.localhost"
    version = (REPO / "VERSION").read_text().strip()
    running = subprocess.run(["docker", "compose", "-p", live_project, "ps", "-q"], cwd=ROOT, capture_output=True, text=True).stdout.split()
    if running and not args.stop_live:
        print(f"The live stack ({live_project}) is up, and container names are fixed: pass --stop-live (its volumes are kept).")
        return 2

    work = Path(tempfile.mkdtemp(prefix=f"{project}-"))
    config = work / "config"
    old_tree = work / "from"
    try:
        if running:
            print(f"Taking the live stack down (volumes kept)")
            sh(["docker", "compose", "down"], ROOT)
        source = old_tree if args.old else REPO
        if args.old:
            sh(["git", "worktree", "add", "--detach", str(old_tree), args.old], REPO)
        # The clean config of the version that starts first; the stack writes its CA and its catalogue into it.
        subprocess.run(["git", "archive", args.old or "HEAD", "deploy/config"], cwd=REPO, stdout=open(work / "config.tar", "wb"), check=True)
        subprocess.run(["tar", "-xf", str(work / "config.tar"), "-C", str(work)], check=True)
        shutil.move(str(work / "deploy" / "config"), str(config))
        env_file = work / "test.env"
        # The models where the live stack has them (absolute): a fresh project must not download them again.
        models = Path(env_value("LLM_MODELS_DIR", "./models"))
        models = models if models.is_absolute() else (ROOT / models).resolve()
        lines = [l for l in (ROOT / ".env").read_text().splitlines()
                 if not l.startswith(("COMPOSE_PROJECT_NAME=", "LLM_CONFIG_DIR=", "COMPOSE_PROFILES=", "LLM_MODELS_DIR=", "EMBED_MODEL_DIR="))]
        env_file.write_text("\n".join(lines + [f"COMPOSE_PROJECT_NAME={project}", f"LLM_CONFIG_DIR={config}", f"COMPOSE_PROFILES={args.profiles}",
                                               f"LLM_MODELS_DIR={models}", f"EMBED_MODEL_DIR={env_value('EMBED_MODEL_DIR') or models}"]) + "\n")
        ca = config / "traefik" / "certs" / "ca.crt"

        first = source / "deploy" / "docker-compose.yml"
        print(f"{'Upgrade from ' + args.old if args.old else 'From zero'}: building and starting {project}")
        check("the first version builds", sh(compose(first, env_file, project, "build"), source / "deploy") == 0)
        check("and starts from zero", sh(compose(first, env_file, project, "up", "-d"), source / "deploy") == 0)
        c = Client(domain, ca)
        info = wait_up(c)
        check("it answers", info is not None, json.dumps(info))
        status, _ = c.call("/api/auth/login", {"userName": "admin", "password": env_value("ADMIN_PASSWORD")})
        check("the admin signs in", status == 200, str(status))
        model = wait_model(c)
        check("a model is serving", model is not None, str(model))

        name = f"upgrade{int(time.time())}"
        status, made = c.call("/api/admin/people", {"userName": name, "email": f"{name}@example.test", "displayName": "Upgrade Test"})
        person_password = made.get("password") if isinstance(made, dict) else None
        check("a person is made", status in (200, 201) and bool(person_password), str(status))
        status, group = c.call("/api/admin/groups", {"name": f"Upgrade group {name}"})
        check("a group is made", status in (200, 201), str(status))
        status, chat = c.call("/api/chat/conversations", {})
        chat_id = chat.get("id") if isinstance(chat, dict) else None
        status, file = c.upload("upgrade-facts.txt", b"The upgrade code word is MAPLE-17.\n")
        file_id = file.get("id") if isinstance(file, dict) else None
        said = ask(c, chat_id, "What is the code word in the attached file? Reply with it only.", [file_id] if file_id else [])
        check("a chat answers with its file", "MAPLE-17" in said.upper(), said[-120:])

        if args.old:
            print(f"Upgrading to this checkout ({version}) over the same volumes")
            new = REPO / "deploy" / "docker-compose.yml"
            check("this version builds", sh(compose(new, env_file, project, "build"), REPO / "deploy") == 0)
            check("and starts over the old volumes", sh(compose(new, env_file, project, "up", "-d", "--remove-orphans"), REPO / "deploy") == 0)
            c = Client(domain, ca)
            end = time.time() + 900
            info = None
            while time.time() < end:
                info = wait_up(c, 1)
                if info and info.get("version") == version:
                    break
                time.sleep(5)
            check(f"it comes up at {version}", bool(info) and info.get("version") == version, json.dumps(info))
            status, _ = c.call("/api/auth/login", {"userName": "admin", "password": env_value("ADMIN_PASSWORD")})
            check("the admin still signs in", status == 200, str(status))
            check("a model is serving again", wait_model(c) is not None)

            def verify(when: str):
                status, people = c.call("/api/admin/people")
                check(f"{when}: the person is there", status == 200 and any(p.get("userName") == name for p in people.get("people", [])))
                status, groups = c.call("/api/admin/groups")
                check(f"{when}: the group is there", status == 200 and f"Upgrade group {name}" in json.dumps(groups))
                status, conv = c.call(f"/api/chat/conversations/{chat_id}")
                msgs = conv.get("messages", []) if isinstance(conv, dict) else []
                check(f"{when}: the chat and its answer are there", status == 200 and any("MAPLE-17" in (m.get("content") or "").upper() for m in msgs), f"{len(msgs)} messages")
                status, text = c.call(f"/api/chat/attachments/{file_id}/content")
                check(f"{when}: its file is there", status == 200 and "MAPLE-17" in str(text))
                person = Client(domain, ca)
                status, _ = person.call("/api/auth/login", {"userName": name, "password": person_password})
                check(f"{when}: the person signs in with their password", status == 200, str(status))

            verify("after the upgrade")
            said = ask(c, chat_id, "And what was the code word again? Reply with it only.")
            check("the old chat goes on, with what it said before", "MAPLE-17" in said.upper(), said[-120:])
            for path in ("/api/projects", "/api/notifications", "/api/admin/config"):
                status, _ = c.call(path)
                check(f"new in this version: GET {path}", status == 200, str(status))
            logs = subprocess.run(["docker", "logs", "app"], capture_output=True, text=True).stdout + subprocess.run(["docker", "logs", "app"], capture_output=True, text=True).stderr
            bad = [l for l in logs.splitlines() if ("fail:" in l and "Microsoft.EntityFrameworkCore" in l) or "Unhandled exception" in l]
            check("no migration or startup error in the app's log", not bad, bad[0][:200] if bad else "")

            print("Idempotent: up again, then down and up")
            check("up again changes nothing", sh(compose(new, env_file, project, "up", "-d"), REPO / "deploy") == 0 and wait_up(c, 5) is not None)
            check("down (volumes kept)", sh(compose(new, env_file, project, "down"), REPO / "deploy") == 0)
            check("up again", sh(compose(new, env_file, project, "up", "-d"), REPO / "deploy") == 0)
            c = Client(domain, ca)
            check("it answers", wait_up(c) is not None)
            status, _ = c.call("/api/auth/login", {"userName": "admin", "password": env_value("ADMIN_PASSWORD")})
            verify("after down and up")
        else:
            check(f"it is {version}", bool(info) and info.get("version") == version, json.dumps(info))
            for path in ("/api/projects", "/api/notifications", "/api/admin/config", "/api/admin/overview"):
                status, _ = c.call(path)
                check(f"GET {path}", status == 200, str(status))
    finally:
        if not args.keep:
            compose_file = (REPO if args.zero or not old_tree.exists() else REPO) / "deploy" / "docker-compose.yml"
            env_file = work / "test.env"
            if env_file.exists():
                print(f"Removing {project} and its volumes")
                sh(compose(compose_file, env_file, project, "down", "-v", "--remove-orphans"), REPO / "deploy")
            if old_tree.exists():
                sh(["git", "worktree", "remove", "--force", str(old_tree)], REPO)
            shutil.rmtree(work, ignore_errors=True)
        if running:
            print("Building this checkout's images and bringing the live stack back")
            sh(["docker", "compose", "build"], ROOT)
            sh(["docker", "compose", "up", "-d"], ROOT)

    failed = [n for n, ok in results if not ok]
    print(f"\n{len(results) - len(failed)}/{len(results)} checks passed")
    return 1 if failed else 0


if __name__ == "__main__":
    sys.exit(main())
