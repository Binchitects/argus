"""
Code Studio's workspaces: each a clone of a project worked on through Code Arena's own
engine (code-arena web --serve), one engine per workspace, run as the workspace's own
user and limited; Arena drives this over HTTP with a key of its own, and reaches each
workspace's engine at its port with that workspace's key.

    GET    /health                     {"ok", "running", "slots"}
    GET    /v1/workspaces              [{"id", "port", "since"}] of those running
    PUT    /v1/workspaces/<id>         {"repo", "branch", "git_user", "git_token", "arena_url",
                                        "api_key", "gateway_url", "mcp_url", "argus_url", "model", "ca",
                                        "git_name", "git_email"}
                                       -> {"port", "key", "cloned"}: cloned once (the branch
                                       checked out), its engine started (again)
    POST   /v1/workspaces/<id>/stop    its engine stopped; its files stay
    DELETE /v1/workspaces/<id>         stopped, and its files gone
    GET    /v1/workspaces/<id>/ports   [{"port", "address", "pid", "process"}]: what its programs
                                       listen on (a dev server), not its engine
    CONNECT <id>:<port>                a forwarded port: the connection joined, both ways, to
                                       what the workspace listens on there (on its loopback when
                                       it listens on all); Arena's own route for its owner
                                       (Code Studio's Ports) is the only way in

Each workspace has a slot (its number, kept in its folder): its user is FIRST_UID + slot
(named ws<slot> in /etc/passwd and /etc/group, its home the workspace's, so whoami, a
prompt, Python's getpass, Node.js's os.userInfo() and ssh know it), its engine's port
FIRST_PORT + slot. Its folder (WORKSPACES/<id>, 0700, its user's) holds
the clone (repo) and the engine's home (home: its settings, sessions, and the person's
code-host token, 0600 in the engine's config folder, which git reads through an askpass
helper and never writes into the clone's config). Every process of a stopped workspace's
user is killed.

The agent's commands run in Code Arena's sandbox, bubblewrap: the clone and the build
caches writable, the engine's config and data folders (its sessions, the token) hidden.
It needs user namespaces, which compose's security_opt for this service allows (its own
seccomp profile, no AppArmor confinement, an unmasked /proc); /health says whether it works.

"ca" is the company CA the stack trusts (a GitLab on the company's own CA): written into
the home with the system's CAs as one bundle, which git, curl, Python, pip, .NET and
Node.js in the workspace (its clone, its engine, its terminals) are pointed at.

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

PASSWD = os.environ.get("WORKSPACES_PASSWD", "/etc/passwd")
# The container's sockets (every workspace shares its network).
PROC_NET = "/proc/net"
GROUP = os.environ.get("WORKSPACES_GROUP", "/etc/group")
# The system's CAs, which a bundle with the company CA keeps.
SYSTEM_CAS = os.environ.get("WORKSPACES_SYSTEM_CAS", "/etc/ssl/certs/ca-certificates.crt")
CA_MOST = 64 * 1024
# A user of no workspace: the sandbox is tried as it.
NOBODY = 65534
# Whether the agent's commands get a sandbox (_sandbox), found at start.
SANDBOX: dict = {"sandbox": "off", "why": "not checked yet"}

ID = re.compile(r"^[0-9a-f]{32}$")
BRANCH = re.compile(r"^[A-Za-z0-9._/-]{1,200}$")
# The code host as the app knows it (GitLab's address: https, or http inside a company's network).
REPO = re.compile(r"^https?://[A-Za-z0-9.-]+(:[0-9]{1,5})?/[A-Za-z0-9._/-]+?(\.git)?$")
ARENA_URL = re.compile(r"^https?://[A-Za-z0-9.-]+(:[0-9]{1,5})?(/.*)?$")

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


def _name(uid: int) -> str:
    return f"ws{uid - FIRST_UID}"


def _rewrite(path: str, uid: int, line: str) -> None:
    """The file with this uid's line as given (added, or put in place of the one there), written whole at once."""
    with open(path) as f:
        lines = f.read().splitlines()
    kept = [x for x in lines if not (len(x.split(":")) > 2 and x.split(":")[2] == str(uid))]
    if kept + [line] == lines:
        return
    tmp = path + ".ws"
    with open(tmp, "w") as f:
        f.write("\n".join(kept + [line]) + "\n")
    os.chmod(tmp, 0o644)
    os.replace(tmp, path)


def _user(uid: int, home: str) -> None:
    """The workspace's user as the system knows it: its name, its group and its home (tools that ask getpwuid, not $HOME)."""
    _rewrite(PASSWD, uid, f"{_name(uid)}:x:{uid}:{uid}:Code Studio workspace:{home}:/bin/bash")
    _rewrite(GROUP, uid, f"{_name(uid)}:x:{uid}:")


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
# the engine's own config folder (never written into the clone's config). The agent's sandbox
# hides that folder, so a command the model runs cannot read it.
case "$1" in
  Username*) printf '%s\\n' "$GIT_USER" ;;
  *) cat "$HOME/.config/code-arena/git-token" ;;
esac
"""

# The engine's config folder in a workspace's home ($XDG_CONFIG_HOME/code-arena), hidden from the agent's commands.
CONFIG = os.path.join(".config", "code-arena")


def _env(home: str, spec: dict, key: str | None) -> dict:
    env = {
        "HOME": home, "PATH": "/usr/local/sbin:/usr/local/bin:/usr/sbin:/usr/bin:/sbin:/bin:/opt/code-arena",
        "LANG": "C.UTF-8", "TERM": "xterm-256color", "DOTNET_CLI_TELEMETRY_OPTOUT": "1",
        "GIT_ASKPASS": os.path.join(home, ".git-askpass"), "GIT_TERMINAL_PROMPT": "0", "GIT_USER": spec.get("git_user") or "oauth2",
    }
    bundle, company = os.path.join(home, ".ca-bundle.pem"), os.path.join(home, ".company-ca.pem")
    if os.path.isfile(company):
        # git and curl, Python (ssl, requests, pip), .NET (OpenSSL) take the bundle; Node.js adds the company CA to its own.
        for name in ("GIT_SSL_CAINFO", "CURL_CA_BUNDLE", "SSL_CERT_FILE", "REQUESTS_CA_BUNDLE", "PIP_CERT"):
            env[name] = bundle
        env["NODE_EXTRA_CA_CERTS"] = company
    for name, value in (("ARENA_URL", "arena_url"), ("ARENA_API_KEY", "api_key"), ("ARENA_GATEWAY_URL", "gateway_url"),
                        ("ARENA_MCP_URL", "mcp_url"), ("ARENA_ARGUS_URL", "argus_url"), ("ARENA_MODEL", "model")):
        if spec.get(value):
            env[name] = str(spec[value])
    if key:
        env["CODE_ARENA_TOKEN"] = key
    return env


def _check(spec: dict) -> None:
    if not isinstance(spec.get("repo"), str) or not REPO.match(spec["repo"]):
        raise Refused(400, "repo: the project's address (https://host/group/project.git).")
    if spec.get("branch") is not None and (not isinstance(spec["branch"], str) or not BRANCH.match(spec["branch"])):
        raise Refused(400, "branch: a branch's name.")
    # The app over the stack's own network (http://app:8080), or its public https address.
    if not isinstance(spec.get("arena_url"), str) or not ARENA_URL.match(spec["arena_url"]):
        raise Refused(400, "arena_url: Arena's address (http or https).")
    if not isinstance(spec.get("api_key"), str) or len(spec["api_key"]) < 16:
        raise Refused(400, "api_key: the person's key for the workspace's agent.")
    for name in ("git_name", "git_email"):
        value = spec.get(name)
        if value is not None and (not isinstance(value, str) or len(value) > 200 or any(c in value for c in "\r\n\0")):
            raise Refused(400, f"{name}: the person's name and email for their commits, one line each.")
    ca = spec.get("ca")
    if ca is not None and (not isinstance(ca, str) or len(ca) > CA_MOST or "PRIVATE KEY" in ca
                           or (ca.strip() and "-----BEGIN CERTIFICATE-----" not in ca)):
        raise Refused(400, "ca: the company CA's certificates (PEM), no key.")


def _trust(home: str, ca: str | None) -> None:
    """The company CA in the home, with the system's CAs as one bundle; none given: both gone (a CA taken away)."""
    bundle, company = os.path.join(home, ".ca-bundle.pem"), os.path.join(home, ".company-ca.pem")
    if not ca or not ca.strip():
        for path in (bundle, company):
            if os.path.exists(path):
                os.remove(path)
        return
    system = ""
    if os.path.isfile(SYSTEM_CAS):
        with open(SYSTEM_CAS) as f:
            system = f.read().rstrip() + "\n"
    for path, text in ((company, ca.strip() + "\n"), (bundle, system + ca.strip() + "\n")):
        with open(path, "w") as f:
            f.write(text)
        os.chmod(path, 0o644)


def _identity(home: str, name: str | None, email: str | None) -> None:
    """The person's name and email for commits in the home's .gitconfig, unless it names an email already (theirs, set in a terminal)."""
    if not email:
        return
    path = os.path.join(home, ".gitconfig")
    text = ""
    if os.path.isfile(path):
        with open(path) as f:
            text = f.read()
    section = ""
    for line in text.splitlines():
        line = line.strip()
        if line.startswith("["):
            section = line.strip("[]").strip().lower()
        elif section == "user" and line.split("=", 1)[0].strip().lower() == "email":
            return

    def quoted(value: str) -> str:
        return '"' + value.replace("\\", "\\\\").replace('"', '\\"') + '"'
    with open(path, "a") as f:
        f.write(("\n" if text and not text.endswith("\n") else "") + "[user]\n" + (f"\tname = {quoted(name)}\n" if name else "") + f"\temail = {quoted(email)}\n")
    os.chmod(path, 0o644)


def _clone(wid: str, uid: int, spec: dict) -> bool:
    """The project cloned into the workspace once (its branch), as its user, with the person's token; false when it was there."""
    folder, home, repo = _folder(wid), os.path.join(_folder(wid), "home"), os.path.join(_folder(wid), "repo")
    os.makedirs(home, mode=0o700, exist_ok=True)
    with open(os.path.join(home, ".git-askpass"), "w") as f:
        f.write(ASKPASS)
    os.chmod(os.path.join(home, ".git-askpass"), 0o700)
    os.makedirs(os.path.join(home, CONFIG), mode=0o700, exist_ok=True)
    token = os.path.join(home, CONFIG, "git-token")
    with open(token, "w") as f:
        f.write(str(spec.get("git_token") or ""))
    os.chmod(token, 0o600)
    # Where an earlier supervisor kept it, which the agent's commands could read.
    if os.path.exists(os.path.join(home, ".git-token")):
        os.remove(os.path.join(home, ".git-token"))
    _trust(home, spec.get("ca"))
    # Commits (the IDE's, and the agent's after a turn) are the person's.
    _identity(home, spec.get("git_name"), spec.get("git_email"))
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


def _sandbox() -> dict:
    """
    Whether the agent's commands get a sandbox here: bubblewrap making one as Code Arena does (its own /proc and
    processes), as a user of no workspace. It needs the container's own seccomp profile and no AppArmor confinement or
    masked /proc (compose's security_opt); without them Code Arena runs the commands unsandboxed and says why.
    """
    bwrap = shutil.which("bwrap")
    if bwrap is None:
        return {"sandbox": "off", "why": "bubblewrap is not installed in the workspaces image."}
    try:
        done = subprocess.run([bwrap, "--ro-bind", "/", "/", "--dev", "/dev", "--proc", "/proc", "--unshare-pid", "--die-with-parent", "true"],
                              preexec_fn=_demote(NOBODY), capture_output=True, text=True, timeout=10)
    except (OSError, subprocess.TimeoutExpired) as e:
        return {"sandbox": "off", "why": f"bubblewrap did not run: {e}"}
    if done.returncode != 0:
        return {"sandbox": "off", "why": " ".join((done.stderr or done.stdout or "bubblewrap failed").split())[:300]}
    return {"sandbox": "bubblewrap"}


def _listening(port: int, seconds: float, process=None) -> bool:
    """Whether the engine listens within the time; false at once when it ended (it said why in its log)."""
    end = time.monotonic() + seconds
    while time.monotonic() < end:
        if process is not None and process.poll() is not None:
            return False
        with socket.socket() as s:
            s.settimeout(0.5)
            if s.connect_ex(("127.0.0.1", port)) == 0:
                return True
        time.sleep(0.2)
    return False


def _tail(path: str, size: int = 600) -> str:
    """The end of a log, in one line (what an engine said before it stopped)."""
    try:
        with open(path, "rb") as f:
            f.seek(max(0, os.path.getsize(path) - size))
            return " ".join(f.read().decode("utf-8", "replace").split())
    except OSError:
        return ""


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
        _user(uid, os.path.join(_folder(wid), "home"))
        cloned = _clone(wid, uid, spec)
        key = secrets.token_urlsafe(32)
        home, repo = os.path.join(_folder(wid), "home"), os.path.join(_folder(wid), "repo")
        # What the engine says (its start, its errors) is kept beside the workspace, anew at each start.
        os.makedirs(_folder(wid), exist_ok=True)
        log = os.path.join(_folder(wid), "engine.log")
        with open(log, "wb") as out:
            process = subprocess.Popen([CODE_ARENA, "web", "--serve", "--port", str(port)], cwd=repo, env=_env(home, spec, key),
                                       preexec_fn=_demote(uid), stdin=subprocess.DEVNULL, stdout=out, stderr=subprocess.STDOUT)
        if not _listening(port, START_SECONDS, process):
            process.kill()
            _kill_user(uid)
            said = _tail(log)
            raise Refused(502, "The workspace's engine did not start" + (f": {said}" if said else "."))
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


def _address(hexed: str) -> str:
    """An address as /proc/net/tcp and tcp6 write it (each 32-bit word in the host's order, little-endian here)."""
    raw = b"".join(bytes.fromhex(hexed[i:i + 8])[::-1] for i in range(0, len(hexed), 8))
    return socket.inet_ntop(socket.AF_INET if len(raw) == 4 else socket.AF_INET6, raw)


def _listening_sockets() -> list[tuple[int, int, str, int]]:
    """(uid, port, address, inode) of every TCP socket listening in the container's network."""
    found = []
    for name in ("tcp", "tcp6"):
        try:
            with open(os.path.join(PROC_NET, name)) as f:
                lines = f.read().splitlines()[1:]
        except OSError:
            continue
        for line in lines:
            fields = line.split()
            # The state 0A is LISTEN.
            if len(fields) > 9 and fields[3] == "0A":
                local, port = fields[1].rsplit(":", 1)
                found.append((int(fields[7]), int(port, 16), _address(local), int(fields[9])))
    return found


def _listeners(uid: int) -> dict[int, str]:
    """The ports a workspace's user listens on, each with its address (0.0.0.0, ::, a loopback)."""
    return {port: address for owner, port, address, _ in _listening_sockets() if owner == uid}


SOCKETS = r"""
import json, os
found = {}
for pid in filter(str.isdigit, os.listdir("/proc")):
    try:
        with open(f"/proc/{pid}/cmdline", "rb") as f:
            cmd = f.read().replace(b"\0", b" ").decode("utf-8", "replace").strip()[:300]
        for fd in os.listdir(f"/proc/{pid}/fd"):
            link = os.readlink(f"/proc/{pid}/fd/{fd}")
            if link.startswith("socket:["):
                found[link[8:-1]] = [int(pid), cmd]
    except OSError:
        pass
print(json.dumps(found))
"""


def _sockets(uid: int) -> dict[int, tuple[int, str]]:
    """Which process holds each socket of a workspace's user (inode -> pid, command line): read as that user, who alone may."""
    try:
        done = subprocess.run(["python3", "-I", "-c", SOCKETS], preexec_fn=_demote(uid), capture_output=True, text=True, timeout=5)
        return {int(inode): (pid, cmd) for inode, (pid, cmd) in json.loads(done.stdout or "{}").items()}
    except (OSError, ValueError, subprocess.TimeoutExpired):
        return {}


def _running_one(wid: str) -> dict:
    with _lock:
        r = _running.get(wid)
        if r is None or r["process"].poll() is not None:
            raise Refused(404, "The workspace is not running.")
        return r


def ports(wid: str) -> list[dict]:
    """
    What a running workspace's programs listen on (its dev servers), as VS Code's Ports view finds them: each port with its
    address and the process holding it; not the engine's own port, nor what the engine itself listens on.
    """
    r = _running_one(wid)
    held = _sockets(r["uid"])
    found = {}
    for owner, port, address, inode in _listening_sockets():
        if owner != r["uid"] or port == r["port"] or port in found:
            continue
        pid, cmd = held.get(inode, (None, None))
        if pid == r["process"].pid:
            continue
        found[port] = {"port": port, "address": address, "pid": pid, "process": cmd}
    return sorted(found.values(), key=lambda p: p["port"])


def _target(wid: str, port: int) -> tuple[str, int]:
    """Where the relay joins a forwarded port: one the workspace's user listens on, on its loopback when it listens on all."""
    r = _running_one(wid)
    if port == r["port"]:
        raise Refused(403, "That is the workspace's engine, reached through Arena's IDE only.")
    listening = _listeners(r["uid"])
    if port not in listening:
        raise Refused(404, f"Nothing in the workspace listens on port {port}.")
    address = listening[port]
    return ("127.0.0.1" if address == "0.0.0.0" else "::1" if address == "::" else address), port


def _splice(a: socket.socket, b: socket.socket) -> None:
    """Both ways until both ends are done: each end's close is passed to the other."""
    for s in (a, b):
        s.settimeout(None)
        s.setsockopt(socket.SOL_SOCKET, socket.SO_KEEPALIVE, 1)

    def pump(src: socket.socket, dst: socket.socket) -> None:
        try:
            while data := src.recv(65536):
                dst.sendall(data)
        except OSError:
            pass
        finally:
            try:
                dst.shutdown(socket.SHUT_WR)
            except OSError:
                pass

    back = threading.Thread(target=pump, args=(b, a), daemon=True)
    back.start()
    pump(a, b)
    back.join()


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

    def _body(self, most: int) -> bytes:
        """The request's body, with its length or in chunks (Arena's HttpClient sends JSON chunked); past most: refused."""
        if self.headers.get("Transfer-Encoding", "").lower() != "chunked":
            length = int(self.headers.get("Content-Length") or 0)
            if length > most:
                raise Refused(413, "Too large.")
            return self.rfile.read(length)
        body = b""
        while True:
            size = int(self.rfile.readline(64).split(b";")[0].strip() or b"0", 16)
            if size == 0:
                self.rfile.readline(64)
                return body
            if len(body) + size > most:
                raise Refused(413, "Too large.")
            body += self.rfile.read(size)
            self.rfile.readline(64)

    def _handle(self, method: str) -> None:
        if self.path == "/health" and method == "GET":
            return self._answer(200, {"ok": True, "running": len(running()), "slots": SLOTS, **SANDBOX})
        if not self._allowed():
            return self._answer(401, {"error": "Only Arena drives the workspaces."})
        parts = self.path.strip("/").split("/")
        try:
            if parts == ["v1", "workspaces"] and method == "GET":
                return self._answer(200, running())
            if len(parts) >= 3 and parts[:2] == ["v1", "workspaces"] and ID.match(parts[2]):
                wid = parts[2]
                if len(parts) == 3 and method == "PUT":
                    spec = json.loads(self._body(64 * 1024) or b"{}")
                    return self._answer(200, start(wid, spec))
                if len(parts) == 4 and parts[3] == "stop" and method == "POST":
                    stop(wid)
                    return self._answer(200, {"stopped": True})
                if len(parts) == 4 and parts[3] == "ports" and method == "GET":
                    return self._answer(200, ports(wid))
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

    def do_CONNECT(self):
        """
        A forwarded port (CONNECT <id>:<port>, with Arena's key): joined to what the workspace listens on there, both
        ways, for as long as either end keeps it (an HTTP connection, a dev server's WebSocket).
        """
        self.close_connection = True
        if not self._allowed():
            return self._answer(401, {"error": "Only Arena drives the workspaces."})
        wid, _, port = self.path.partition(":")
        try:
            if not ID.match(wid) or not port.isdigit() or not 0 < int(port) < 65536:
                raise Refused(404, "CONNECT <workspace>:<port>.")
            upstream = socket.create_connection(_target(wid, int(port)), timeout=5)
        except Refused as e:
            return self._answer(e.status, {"error": str(e)})
        except OSError as e:
            return self._answer(502, {"error": f"Port {port} did not answer: {e.strerror or e}"})
        with upstream:
            self.send_response(200, "Connection established")
            self.end_headers()
            self.wfile.flush()
            _splice(self.connection, upstream)


def main() -> None:
    if len(KEY) < 32:
        raise SystemExit("WORKSPACES_KEY: at least 32 characters, shared with Arena.")
    os.makedirs(WORKSPACES, mode=0o711, exist_ok=True)
    SANDBOX.clear()
    SANDBOX.update(_sandbox())
    print("The agent's commands: " + ("sandboxed by bubblewrap" if SANDBOX["sandbox"] == "bubblewrap" else f"not sandboxed ({SANDBOX['why']})"), flush=True)
    ThreadingHTTPServer(("0.0.0.0", int(os.environ.get("WORKSPACES_PORT", "4000"))), Handler).serve_forever()


if __name__ == "__main__":
    main()
