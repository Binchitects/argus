"""In-process stand-ins for GitLab and Ollama, shared by both implementations.

The conformance run drives the Python and the .NET Argus against the SAME
services, so any difference in what they store or answer is a difference in
Argus, never in its environment.

FakeGitLab answers the handful of REST endpoints Argus calls (/user, /projects,
/projects/:id/members/all, /users) and serves the bare repositories over git's
dumb HTTP protocol, so mirroring and fetching exercise the real clone path --
askpass, clone URL rewriting and all.

FakeOllama returns deterministic embeddings from a hashing vectoriser: words
that overlap produce vectors that point the same way, which keeps semantic
search meaningful without a model, and identical input always produces
bit-identical output for both implementations.
"""
from __future__ import annotations

import hashlib
import json
import math
import re
import threading
from dataclasses import dataclass, field
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
from pathlib import Path
from urllib.parse import parse_qs, urlsplit

DIM = 768


@dataclass
class User:
    id: int
    username: str
    token: str
    email: str = ""
    public_email: str = ""
    is_admin: bool = False
    state: str = "active"
    name: str = ""


@dataclass
class ProjectDef:
    id: int
    path_with_namespace: str
    default_branch: str
    #: user id -> access level (10 guest, 20 reporter, 30 dev, 40 maintainer, 50 owner)
    members: dict[int, int] = field(default_factory=dict)


class FakeGitLab:
    def __init__(self, repos_dir: Path, users: list[User], projects: list[ProjectDef],
                 advertised: str = "https://gitlab.example.invalid"):
        self.repos_dir = Path(repos_dir)
        self.users = {u.token: u for u in users}
        self.users_by_id = {u.id: u for u in users}
        self.projects = projects
        self.advertised = advertised
        self.requests: list[str] = []
        self.server = ThreadingHTTPServer(("127.0.0.1", 0), self._handler())
        self.port = self.server.server_address[1]
        self.url = f"http://127.0.0.1:{self.port}"
        threading.Thread(target=self.server.serve_forever, daemon=True).start()

    def close(self):
        self.server.shutdown()

    def _project_json(self, p: ProjectDef) -> dict:
        return {
            "id": p.id,
            "path_with_namespace": p.path_with_namespace,
            "default_branch": p.default_branch,
            # Deliberately NOT the address Argus is configured with, so the
            # clone-URL rewrite is part of what both implementations must do.
            "http_url_to_repo": f"{self.advertised}/{p.path_with_namespace}.git",
        }

    def _handler(self):
        gl = self

        class Handler(BaseHTTPRequestHandler):
            def log_message(self, *args):
                pass

            def _send(self, status, body, ctype="application/json"):
                data = body if isinstance(body, bytes) else json.dumps(body).encode()
                self.send_response(status)
                self.send_header("Content-Type", ctype)
                self.send_header("Content-Length", str(len(data)))
                self.end_headers()
                self.wfile.write(data)

            def _caller(self):
                token = self.headers.get("PRIVATE-TOKEN") or ""
                auth = self.headers.get("Authorization") or ""
                if not token and auth.lower().startswith("bearer "):
                    token = auth[7:]
                return gl.users.get(token)

            def do_GET(self):
                parts = urlsplit(self.path)
                q = {k: v[-1] for k, v in parse_qs(parts.query).items()}
                gl.requests.append(parts.path)
                if parts.path.startswith("/api/v4/"):
                    return self._api(parts.path[len("/api/v4"):], q)
                return self._git(parts.path)

            def _api(self, path, q):
                me = self._caller()
                if me is None:
                    return self._send(401, {"message": "401 Unauthorized"})
                page = int(q.get("page", "1"))
                per_page = int(q.get("per_page", "20"))

                def paged(items):
                    return items[(page - 1) * per_page: page * per_page]

                if path == "/user":
                    return self._send(200, {"id": me.id, "username": me.username, "is_admin": me.is_admin,
                                            "email": me.email, "state": me.state, "name": me.name})
                if path == "/projects":
                    if q.get("membership") == "true":
                        level = int(q.get("min_access_level", "10"))
                        items = [p for p in gl.projects if p.members.get(me.id, 0) >= max(level, 10)]
                    elif me.is_admin:
                        items = list(gl.projects)
                    else:
                        items = []
                    return self._send(200, paged([gl._project_json(p) for p in items]))
                m = re.fullmatch(r"/projects/(\d+)/members/all", path)
                if m:
                    project = next((p for p in gl.projects if p.id == int(m.group(1))), None)
                    if project is None:
                        return self._send(404, {"message": "404 Project Not Found"})
                    members = []
                    for uid, level in sorted(project.members.items()):
                        u = gl.users_by_id[uid]
                        members.append({"id": u.id, "username": u.username, "name": u.name,
                                        "access_level": level, "state": u.state})
                    return self._send(200, paged(members))
                if path == "/users":
                    if "search" in q:
                        needle = q["search"].lower()
                        hits = [u for u in gl.users.values()
                                if needle in (u.public_email.lower(), u.email.lower() if me.is_admin else "")]
                    elif "username" in q:
                        hits = [u for u in gl.users.values() if u.username.lower() == q["username"].lower()]
                    else:
                        hits = []
                    return self._send(200, [{"id": u.id, "username": u.username, "name": u.name, "state": u.state,
                                             "public_email": u.public_email,
                                             **({"email": u.email} if me.is_admin else {})} for u in hits])
                return self._send(404, {"message": "404 Not Found"})

            def _git(self, path):
                target = (gl.repos_dir / path.lstrip("/")).resolve()
                if not str(target).startswith(str(gl.repos_dir.resolve())) or not target.is_file():
                    return self._send(404, b"not found", "text/plain")
                return self._send(200, target.read_bytes(), "application/octet-stream")

        return Handler


def embed_text(text: str) -> list[float]:
    """A deterministic, similarity-preserving vector for `text` (not normalised)."""
    vec = [0.0] * DIM
    words = re.findall(r"[a-z0-9]+", text.lower())
    grams = words + [text.lower()[i:i + 4] for i in range(0, max(len(text) - 3, 0), 3)]
    for token in grams:
        digest = hashlib.sha256(token.encode("utf-8")).digest()
        for k in range(3):
            index = int.from_bytes(digest[k * 4:k * 4 + 2], "little") % DIM
            sign = 1.0 if digest[k * 4 + 2] & 1 else -1.0
            weight = 1.0 + (digest[k * 4 + 3] / 255.0)
            vec[index] += sign * weight / (1.0 + k)
    if not any(vec):
        vec[0] = 1.0
    return vec


class FakeOllama:
    def __init__(self):
        self.calls = 0
        self.server = ThreadingHTTPServer(("127.0.0.1", 0), self._handler())
        self.port = self.server.server_address[1]
        self.url = f"http://127.0.0.1:{self.port}"
        threading.Thread(target=self.server.serve_forever, daemon=True).start()

    def close(self):
        self.server.shutdown()

    def _handler(self):
        fake = self

        class Handler(BaseHTTPRequestHandler):
            def log_message(self, *args):
                pass

            def do_POST(self):
                length = int(self.headers.get("Content-Length") or 0)
                body = json.loads(self.rfile.read(length) or b"{}")
                texts = body.get("input") or []
                if isinstance(texts, str):
                    texts = [texts]
                fake.calls += 1
                data = json.dumps({"model": body.get("model"),
                                   "embeddings": [embed_text(t) for t in texts]}).encode()
                self.send_response(200)
                self.send_header("Content-Type", "application/json")
                self.send_header("Content-Length", str(len(data)))
                self.end_headers()
                self.wfile.write(data)

        return Handler
