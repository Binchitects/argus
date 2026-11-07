"""
The code host and Arena's model API for a real Code Studio workspace (RealWorkspaceTests): GitLab's API as the app asks
it (the person, a project, a branch), the project's git over smart HTTP (git http-backend) with the person's token as
the password, and the OpenAI API the workspace's agent asks (a model that runs one command, then answers). What it was
asked is kept: GET /e2e/asked.
"""
import base64
import json
import os
import subprocess
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer

ROOT = "/srv/git"
USER, TOKEN = "dana", "glpat-e2e-token"
COMMAND = os.environ.get("E2E_COMMAND", "true")
asked = []


class H(BaseHTTPRequestHandler):
    protocol_version = "HTTP/1.1"

    def log_message(self, *_):
        pass

    def send(self, status, body, kind="application/json", headers=()):
        data = body if isinstance(body, bytes) else json.dumps(body).encode()
        self.send_response(status)
        self.send_header("Content-Type", kind)
        self.send_header("Content-Length", str(len(data)))
        for k, v in headers:
            self.send_header(k, v)
        self.end_headers()
        self.wfile.write(data)

    def body(self):
        return self.data

    def read(self):
        """The request's body, read whole first (an answer to a request not read would leave it for the next on the connection)."""
        if self.headers.get("Transfer-Encoding", "").lower() == "chunked":
            data = b""
            while size := int(self.rfile.readline().split(b";")[0].strip() or b"0", 16):
                data += self.rfile.read(size)
                self.rfile.readline()
            self.rfile.readline()
            return data
        n = int(self.headers.get("Content-Length") or 0)
        return self.rfile.read(n) if n else b""

    def do_GET(self):
        self.data = b""
        self.route("GET")

    def do_POST(self):
        self.data = self.read()
        self.route("POST")

    def do_PUT(self):
        # Arena's own API the agent keeps its chat with: not this stand-in's.
        self.read()
        self.send(404, {"error": "no"})

    do_PATCH = do_DELETE = do_PUT

    def route(self, method):
        path = self.path.split("?")[0]
        if path == "/e2e/asked":
            return self.send(200, asked)
        if path.startswith("/api/v4/"):
            return self.gitlab(path[len("/api/v4"):])
        if path.startswith("/v1/") or path == "/mcp":
            return self.model(method, path)
        if ".git/" in path or path.endswith(".git"):
            return self.git(method)
        self.send(404, {"error": "no"})

    def gitlab(self, path):
        if self.headers.get("PRIVATE-TOKEN") != TOKEN:
            return self.send(401, {"message": "401 Unauthorized"})
        asked.append({"gitlab": path})
        if path == "/user":
            return self.send(200, {"username": USER, "name": "Dana"})
        if path == "/projects/group%2Fapp":
            return self.send(200, {"id": 1, "path_with_namespace": "group/app", "name": "app", "default_branch": "main"})
        if path == "/projects/group%2Fapp/repository/branches/main":
            return self.send(200, {"name": "main"})
        self.send(404, {"message": "404 Not Found"})

    def model(self, method, path):
        if path == "/v1/models":
            return self.send(200, {"object": "list", "data": [{"id": "e2e-model", "object": "model"}]})
        if path == "/v1/chat/completions" and method == "POST":
            req = json.loads(self.body() or b"{}")
            tools = [m for m in req.get("messages", []) if m.get("role") == "tool"]
            if tools:
                asked.append({"tool_result": tools[-1].get("content")})
                message = {"role": "assistant", "content": "Done."}
            else:
                message = {"role": "assistant", "content": None, "tool_calls": [{"id": "call_1", "type": "function",
                           "function": {"name": "run_shell", "arguments": json.dumps({"command": COMMAND})}}]}
            if req.get("stream"):
                delta = dict(message)
                if "tool_calls" in delta:
                    delta["tool_calls"] = [dict(delta["tool_calls"][0], index=0)]
                chunks = [{"choices": [{"index": 0, "delta": delta, "finish_reason": None}]},
                          {"choices": [{"index": 0, "delta": {}, "finish_reason": "tool_calls" if tools == [] else "stop"}],
                           "usage": {"prompt_tokens": 10, "completion_tokens": 5, "total_tokens": 15}}]
                data = b"".join(f"data: {json.dumps(dict(c, id='x', object='chat.completion.chunk', model='e2e-model'))}\n\n".encode() for c in chunks) + b"data: [DONE]\n\n"
                return self.send(200, data, "text/event-stream")
            return self.send(200, {"id": "x", "object": "chat.completion", "model": "e2e-model",
                                   "choices": [{"index": 0, "message": message, "finish_reason": "tool_calls" if not tools else "stop"}],
                                   "usage": {"prompt_tokens": 10, "completion_tokens": 5, "total_tokens": 15}})
        self.send(404, {"error": "no"})

    def git(self, method):
        auth = self.headers.get("Authorization", "")
        if auth != "Basic " + base64.b64encode(f"{USER}:{TOKEN}".encode()).decode():
            return self.send(401, b"", "text/plain", [("WWW-Authenticate", 'Basic realm="git"')])
        path, _, query = self.path.partition("?")
        env = {"GIT_PROJECT_ROOT": ROOT, "GIT_HTTP_EXPORT_ALL": "1", "PATH_INFO": path, "QUERY_STRING": query, "REQUEST_METHOD": method,
               "CONTENT_TYPE": self.headers.get("Content-Type", ""), "REMOTE_USER": USER, "REMOTE_ADDR": self.client_address[0],
               "PATH": os.environ["PATH"], "HOME": "/tmp"}
        if self.headers.get("Content-Encoding"):
            env["HTTP_CONTENT_ENCODING"] = self.headers["Content-Encoding"]
        body = self.body()
        env["CONTENT_LENGTH"] = str(len(body))
        asked.append({"git": method + " " + path + ("?" + query if query else "")})
        out = subprocess.run(["git", "http-backend"], input=body, env=env, capture_output=True).stdout
        head, _, rest = out.partition(b"\r\n\r\n")
        status, headers = 200, []
        for line in head.decode().split("\r\n"):
            k, _, v = line.partition(":")
            if k.lower() == "status":
                status = int(v.strip().split()[0])
            elif k:
                headers.append((k, v.strip()))
        kind = next((v for k, v in headers if k.lower() == "content-type"), "application/octet-stream")
        self.send(status, rest, kind, [(k, v) for k, v in headers if k.lower() not in ("content-type", "content-length")])


def main():
    os.makedirs(ROOT, exist_ok=True)
    bare = os.path.join(ROOT, "group", "app.git")
    if not os.path.isdir(bare):
        seed = "/tmp/seed"
        subprocess.run(["git", "init", "-q", "-b", "main", seed], check=True)
        with open(os.path.join(seed, "README.md"), "w") as f:
            f.write("# app\n")
        env = dict(os.environ, GIT_AUTHOR_NAME="Seed", GIT_AUTHOR_EMAIL="seed@example.test", GIT_COMMITTER_NAME="Seed", GIT_COMMITTER_EMAIL="seed@example.test")
        subprocess.run(["git", "-C", seed, "add", "."], check=True)
        subprocess.run(["git", "-C", seed, "commit", "-qm", "First"], check=True, env=env)
        subprocess.run(["git", "clone", "-q", "--bare", seed, bare], check=True)
        subprocess.run(["git", "-C", bare, "config", "http.receivepack", "true"], check=True)
    ThreadingHTTPServer(("0.0.0.0", 8000), H).serve_forever()


if __name__ == "__main__":
    main()
