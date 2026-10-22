"""
Code Studio's workspaces: each a clone of a project worked on through Code Arena's own
engine (code-arena web --serve), one engine per workspace, run as the workspace's own
user and limited; Arena drives this over HTTP with a key of its own, and reaches each
workspace's engine at its port with that workspace's key.

    GET    /health                     {"ok", "running", "slots"}
    GET    /v1/workspaces              [{"id", "port", "since"}] of those running
    PUT    /v1/workspaces/<id>         {"repo", "branch", "git_user", "git_token", "arena_url",
                                        "api_key", "gateway_url", "mcp_url", "argus_url", "model"}
                                       -> {"port", "key", "cloned"}: cloned once (the branch
                                       checked out), its engine started (again)
    POST   /v1/workspaces/<id>/stop    its engine stopped; its files stay
    DELETE /v1/workspaces/<id>         stopped, and its files gone

Each workspace has a slot (its number, kept in its folder): its user is FIRST_UID + slot,
its engine's port FIRST_PORT + slot. Its folder (WORKSPACES/<id>, 0700, its user's) holds
the clone (repo) and the engine's home (home: its settings, sessions, and the person's
code-host token, 0600, which git reads through an askpass helper and never writes into
the clone's config). Every process of a stopped workspace's user is killed.

The container itself has no Docker socket and no capabilities but those to switch users;
its network reaches the code host, the package registries and Arena's gateway, not the
stack's databases (its compose network). The limits per engine (processes, open files,
file size) are rlimits; memory and CPU are the container's.
"""
from __future__ import annotations

import hmac
import json
import os
import re
import resource
import secrets
import shutil
import signal
import socket
import subprocess
import threading
import time
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer

WORKSPACES = os.environ.get("WORKSPACES_DIR", "/workspaces")
KEY = os.environ.get("WORKSPACES_KEY", "")
CODE_ARENA = os.environ.get("CODE_ARENA", "/opt/code-arena/code-arena")
FIRST_UID = int(os.environ.get("WORKSPACES_FIRST_UID", "30000"))
FIRST_PORT = int(os.environ.get("WORKSPACES_FIRST_PORT", "4100"))
SLOTS = int(os.environ.get("WORKSPACES_SLOTS", "64"))
PROCESSES = int(os.environ.get("WORKSPACES_PROCESSES", "512"))
OPEN_FILES = int(os.environ.get("WORKSPACES_OPEN_FILES", "8192"))
FILE_MB = int(os.environ.get("WORKSPACES_FILE_MB", "1024"))
CLONE_SECONDS = int(os.environ.get("WORKSPACES_CLONE_SECONDS", "600"))
START_SECONDS = int(os.environ.get("WORKSPACES_START_SECONDS", "30"))

ID = re.compile(r"^[0-9a-f]{32}$")
BRANCH = re.compile(r"^[A-Za-z0-9._/-]{1,200}$")
REPO = re.compile(r"^https://[A-Za-z0-9.-]+(:[0-9]{1,5})?/[A-Za-z0-9._/-]+?(\.git)?$")

_lock = threading.Lock()
# id -> {"process": Popen, "port", "key", "since"}
_running: dict[str, dict] = {}


class Refused(Exception):
    def __init__(self, status: int, message: str):
        super().__init__(message)
        self.status = status


def _folder(wid: str) -> str:
    return os.path.join(WORKSPACES, wid)


def _read(path: str) -> int:
    with open(path) as f:
        return int(f.read().strip())


def _slot(wid: str) -> int:
    """The workspace's slot: kept in its folder, else the first no other workspace holds."""
    path = os.path.join(_folder(wid), "slot")
    if os.path.isfile(path):
        return _read(path)
    taken = set()
    for other in os.listdir(WORKSPACES):
        p = os.path.join(WORKSPACES, other, "slot")
        if os.path.isfile(p):
            taken.add(_read(p))
    free = next((s for s in range(SLOTS) if s not in taken), None)
    if free is None:
        raise Refused(503, f"All {SLOTS} workspaces are taken: delete one first.")
    os.makedirs(_folder(wid), mode=0o700, exist_ok=True)
    with open(path, "w") as f:
        f.write(str(free))
    return free


def _demote(uid: int):
    """In the child, before it runs: its own session, the workspace's user, its limits, files private to it."""
    def run():
        os.setsid()
        os.setgroups([])
        os.setgid(uid)
        os.setuid(uid)
        os.umask(0o077)
        resource.setrlimit(resource.RLIMIT_NPROC, (PROCESSES, PROCESSES))
        resource.setrlimit(resource.RLIMIT_NOFILE, (OPEN_FILES, OPEN_FILES))
        resource.setrlimit(resource.RLIMIT_FSIZE, (FILE_MB * 1024 * 1024, FILE_MB * 1024 * 1024))
        resource.setrlimit(resource.RLIMIT_CORE, (0, 0))
    return run


def _own(path: str, uid: int) -> None:
    for root, dirs, files in os.walk(path):
        os.chown(root, uid, uid)
        for name in dirs + files:
            p = os.path.join(root, name)
            if not os.path.islink(p):
                os.chown(p, uid, uid)


ASKPASS = """#!/bin/sh
# git asks for the code host's user name, then its password: the person's token, read from
# this home (never written into the clone's config).
case "$1" in
  Username*) printf '%s\\n' "$GIT_USER" ;;
  *) cat "$HOME/.git-token" ;;
esac
"""


def _env(home: str, spec: dict, key: str | None) -> dict:
    env = {
        "HOME": home, "PATH": "/usr/local/sbin:/usr/local/bin:/usr/sbin:/usr/bin:/sbin:/bin:/opt/code-arena",
        "LANG": "C.UTF-8", "TERM": "xterm-256color", "DOTNET_CLI_TELEMETRY_OPTOUT": "1",
        "GIT_ASKPASS": os.path.join(home, ".git-askpass"), "GIT_TERMINAL_PROMPT": "0", "GIT_USER": spec.get("git_user") or "oauth2",
    }
    for name, value in (("ARENA_URL", "arena_url"), ("ARENA_API_KEY", "api_key"), ("ARENA_GATEWAY_URL", "gateway_url"),
                        ("ARENA_MCP_URL", "mcp_url"), ("ARENA_ARGUS_URL", "argus_url"), ("ARENA_MODEL", "model")):
        if spec.get(value):
            env[name] = str(spec[value])
    if key:
        env["CODE_ARENA_TOKEN"] = key
    return env


def _check(spec: dict) -> None:
    if not isinstance(spec.get("repo"), str) or not REPO.match(spec["repo"]):
        raise Refused(400, "repo: the project's https address (https://host/group/project.git).")
    if spec.get("branch") is not None and (not isinstance(spec["branch"], str) or not BRANCH.match(spec["branch"])):
        raise Refused(400, "branch: a branch's name.")
    if not isinstance(spec.get("arena_url"), str) or not spec["arena_url"].startswith("https://"):
        raise Refused(400, "arena_url: Arena's https address.")
    if not isinstance(spec.get("api_key"), str) or len(spec["api_key"]) < 16:
        raise Refused(400, "api_key: the person's key for the workspace's agent.")


def _clone(wid: str, uid: int, spec: dict) -> bool:
    """The project cloned into the workspace once (its branch), as its user, with the person's token; false when it was there."""
    folder, home, repo = _folder(wid), os.path.join(_folder(wid), "home"), os.path.join(_folder(wid), "repo")
    os.makedirs(home, mode=0o700, exist_ok=True)
    with open(os.path.join(home, ".git-askpass"), "w") as f:
        f.write(ASKPASS)
    os.chmod(os.path.join(home, ".git-askpass"), 0o700)
    token = os.path.join(home, ".git-token")
    with open(token, "w") as f:
        f.write(str(spec.get("git_token") or ""))
    os.chmod(token, 0o600)
    os.chmod(folder, 0o700)
    _own(folder, uid)
    if os.path.isdir(os.path.join(repo, ".git")):
        return False
    cmd = ["git", "clone", "--quiet"] + (["--branch", spec["branch"]] if spec.get("branch") else []) + ["--", spec["repo"], repo]
    done = subprocess.run(cmd, env=_env(home, spec, None), cwd=folder, preexec_fn=_demote(uid), capture_output=True, text=True, timeout=CLONE_SECONDS)
    if done.returncode != 0:
        shutil.rmtree(repo, ignore_errors=True)
        # What git said, its token out of it.
        said = (done.stderr or done.stdout).replace(str(spec.get("git_token") or "\0"), "***").strip()[-600:]
        raise Refused(502, f"The project could not be cloned: {said or 'git failed'}")
    return True


def _listening(port: int, seconds: float) -> bool:
    end = time.monotonic() + seconds
    while time.monotonic() < end:
        with socket.socket() as s:
            s.settimeout(0.5)
            if s.connect_ex(("127.0.0.1", port)) == 0:
                return True
        time.sleep(0.2)
    return False


def _kill_user(uid: int) -> None:
    """Every process of the workspace's user (its terminals, servers, builds), wherever they went."""
    for pid in os.listdir("/proc"):
        if pid.isdigit():
            try:
                if os.stat(f"/proc/{pid}").st_uid == uid:
                    os.kill(int(pid), signal.SIGKILL)
            except (FileNotFoundError, ProcessLookupError, PermissionError):
                pass


def start(wid: str, spec: dict) -> dict:
    _check(spec)
    with _lock:
        if wid in _running and _running[wid]["process"].poll() is None:
            r = _running[wid]
            return {"port": r["port"], "key": r["key"], "cloned": False}
        slot = _slot(wid)
        uid, port = FIRST_UID + slot, FIRST_PORT + slot
        cloned = _clone(wid, uid, spec)
        key = secrets.token_urlsafe(32)
        home, repo = os.path.join(_folder(wid), "home"), os.path.join(_folder(wid), "repo")
        process = subprocess.Popen([CODE_ARENA, "web", "--serve", "--port", str(port)], cwd=repo, env=_env(home, spec, key),
                                   preexec_fn=_demote(uid), stdin=subprocess.DEVNULL, stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)
        if not _listening(port, START_SECONDS):
            process.kill()
            _kill_user(uid)
            raise Refused(502, "The workspace's engine did not start.")
        _running[wid] = {"process": process, "port": port, "key": key, "since": int(time.time()), "uid": uid}
        return {"port": port, "key": key, "cloned": cloned}


def stop(wid: str) -> None:
    with _lock:
        r = _running.pop(wid, None)
        slot_file = os.path.join(_folder(wid), "slot")
        uid = r["uid"] if r else (FIRST_UID + _read(slot_file) if os.path.isfile(slot_file) else None)
        if r:
            try:
                os.killpg(r["process"].pid, signal.SIGTERM)
                r["process"].wait(timeout=5)
            except (ProcessLookupError, subprocess.TimeoutExpired, PermissionError):
                pass
        if uid is not None:
            _kill_user(uid)


def delete(wid: str) -> None:
    stop(wid)
    shutil.rmtree(_folder(wid), ignore_errors=True)


def running() -> list[dict]:
    with _lock:
        for wid in [w for w, r in _running.items() if r["process"].poll() is not None]:
            _running.pop(wid)
        return [{"id": w, "port": r["port"], "since": r["since"]} for w, r in _running.items()]


class Handler(BaseHTTPRequestHandler):
    server_version = "workspaces"

    def log_message(self, *_):
        pass

    def _answer(self, status: int, body) -> None:
        data = json.dumps(body).encode()
        self.send_response(status)
        self.send_header("Content-Type", "application/json")
        self.send_header("Content-Length", str(len(data)))
        self.end_headers()
        self.wfile.write(data)

    def _allowed(self) -> bool:
        given = self.headers.get("Authorization", "")
        return bool(KEY) and hmac.compare_digest(given.encode(), f"Bearer {KEY}".encode())

    def _handle(self, method: str) -> None:
        if self.path == "/health" and method == "GET":
            return self._answer(200, {"ok": True, "running": len(running()), "slots": SLOTS})
        if not self._allowed():
            return self._answer(401, {"error": "Only Arena drives the workspaces."})
        parts = self.path.strip("/").split("/")
        try:
            if parts == ["v1", "workspaces"] and method == "GET":
                return self._answer(200, running())
            if len(parts) >= 3 and parts[:2] == ["v1", "workspaces"] and ID.match(parts[2]):
                wid = parts[2]
                if len(parts) == 3 and method == "PUT":
                    length = int(self.headers.get("Content-Length") or 0)
                    if length > 64 * 1024:
                        raise Refused(413, "Too large.")
                    spec = json.loads(self.rfile.read(length) or b"{}")
                    return self._answer(200, start(wid, spec))
                if len(parts) == 4 and parts[3] == "stop" and method == "POST":
                    stop(wid)
                    return self._answer(200, {"stopped": True})
                if len(parts) == 3 and method == "DELETE":
                    delete(wid)
                    return self._answer(200, {"deleted": True})
            self._answer(404, {"error": "No such route."})
        except Refused as e:
            self._answer(e.status, {"error": str(e)})
        except (json.JSONDecodeError, ValueError):
            self._answer(400, {"error": "Send JSON."})
        except subprocess.TimeoutExpired:
            self._answer(504, {"error": "The project took too long to clone."})

    def do_GET(self):
        self._handle("GET")

    def do_PUT(self):
        self._handle("PUT")

    def do_POST(self):
        self._handle("POST")

    def do_DELETE(self):
        self._handle("DELETE")


def main() -> None:
    if len(KEY) < 32:
        raise SystemExit("WORKSPACES_KEY: at least 32 characters, shared with Arena.")
    os.makedirs(WORKSPACES, mode=0o711, exist_ok=True)
    ThreadingHTTPServer(("0.0.0.0", int(os.environ.get("WORKSPACES_PORT", "4000"))), Handler).serve_forever()


if __name__ == "__main__":
    main()
