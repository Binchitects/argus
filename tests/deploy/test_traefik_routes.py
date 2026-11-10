"""
deploy/config/traefik/routes.yml in the stack's own Traefik: the routes as Traefik renders them from the environment, every
request asked about at the app's /api/authz/address first (Settings → Network access), and IP_ALLOW kept by Traefik itself.
A fake app answers the forward auth by the path asked for and says what it saw, a fake web and code-intel answer the rest; a
client container on the same network makes the requests, so Traefik sees its address.
"""
from __future__ import annotations

import json
import re
import shutil
import subprocess
import tempfile
import time
import unittest
import uuid
from pathlib import Path

from support import REPO

ROUTES = REPO / "deploy" / "config" / "traefik" / "routes.yml"
COMPOSE = REPO / "deploy" / "docker-compose.yml"
PYTHON = "python:3.13-slim"

# The web, the app and code-intel: /api/authz/address refuses the paths under /refused (with a sentence, as the app does) and
# lets the rest in; anything else is answered with who answered it. Every forward auth asked is kept, to be read back.
FAKE = r'''
import http.server, json, threading
asked = []
class H(http.server.BaseHTTPRequestHandler):
    def log_message(self, *a): pass
    def reply(self, code, body):
        b = body.encode()
        self.send_response(code); self.send_header("Content-Length", str(len(b))); self.end_headers(); self.wfile.write(b)
    def do_GET(self):
        if self.path == "/api/authz/address":
            uri = self.headers.get("X-Forwarded-Uri", "")
            asked.append({"uri": uri, "for": self.headers.get("X-Forwarded-For", ""), "host": self.headers.get("X-Forwarded-Host", "")})
            return self.reply(403, "refused by the test") if uri.startswith("/refused") else self.reply(204, "")
        if self.path == "/asked":
            return self.reply(200, json.dumps(asked))
        self.reply(200, f"{self.server.server_port} {self.headers.get('Host')} {self.path}")
for port in (8080, 7700):
    s = http.server.ThreadingHTTPServer(("0.0.0.0", port), H)
    threading.Thread(target=s.serve_forever, daemon=True).start()
threading.Event().wait()
'''

# GET each (host, path) through Traefik, over TLS (its own certificate), and print the status and body of each.
CLIENT = r'''
import json, ssl, sys, urllib.request, urllib.error
ctx = ssl.create_default_context(); ctx.check_hostname = False; ctx.verify_mode = ssl.CERT_NONE
out = []
for host, path in json.loads(sys.argv[1]):
    req = urllib.request.Request("https://traefik" + path, headers={"Host": host})
    try:
        with urllib.request.urlopen(req, context=ctx, timeout=10) as r: out.append([r.status, r.read().decode()])
    except urllib.error.HTTPError as e: out.append([e.code, e.read().decode()])
    except Exception as e: out.append([0, str(e)])
print(json.dumps(out))
'''


def docker(*args: str, check: bool = True) -> subprocess.CompletedProcess[str]:
    return subprocess.run(["docker", *args], capture_output=True, text=True, check=check, timeout=120)


def has_image(ref: str) -> bool:
    return shutil.which("docker") is not None and subprocess.run(["docker", "image", "inspect", ref], capture_output=True).returncode == 0


TRAEFIK = re.search(r"image:\s*(traefik:\S+)", COMPOSE.read_text()).group(1)


@unittest.skipUnless(has_image(TRAEFIK) and has_image(PYTHON), f"needs docker with {TRAEFIK} and {PYTHON} already pulled")
class TraefikRoutesTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.tag = uuid.uuid4().hex[:8]
        cls.net = f"routes-test-{cls.tag}"
        cls.dir = Path(tempfile.mkdtemp(prefix="routes-test-"))
        cls.dir.chmod(0o755)
        (cls.dir / "dynamic").mkdir()
        shutil.copy(ROUTES, cls.dir / "dynamic" / "routes.yml")
        (cls.dir / "fake.py").write_text(FAKE)
        (cls.dir / "client.py").write_text(CLIENT)
        for f in cls.dir.rglob("*"):
            f.chmod(0o755 if f.is_dir() else 0o644)
        docker("network", "create", cls.net)
        cls.fake = f"routes-app-{cls.tag}"
        cls.start_fake()
        # The web and code-intel apart from the app, so the app being down is told from theirs.
        docker("run", "-d", "--name", f"routes-web-{cls.tag}", "--network", cls.net, "--network-alias", "web", "--network-alias",
               "code-intel", "-v", f"{cls.dir}:/t:ro", PYTHON, "python", "-u", "/t/fake.py")

    @classmethod
    def start_fake(cls):
        docker("run", "-d", "--name", cls.fake, "--network", cls.net, "--network-alias", "app", "-v", f"{cls.dir}:/t:ro",
               PYTHON, "python", "-u", "/t/fake.py")

    @classmethod
    def tearDownClass(cls):
        for name in (cls.fake, f"routes-web-{cls.tag}", f"routes-traefik-{cls.tag}", f"routes-client-{cls.tag}"):
            docker("rm", "-f", name, check=False)
        docker("network", "rm", cls.net, check=False)
        shutil.rmtree(cls.dir, True)

    def start_traefik(self, allow: str = ""):
        name = f"routes-traefik-{self.tag}"
        docker("rm", "-f", name, check=False)
        docker("run", "-d", "--name", name, "--network", self.net, "--network-alias", "traefik", "-e", "DOMAIN=arena.test",
               "-e", f"IP_ALLOW={allow}", "-v", f"{self.dir / 'dynamic'}:/etc/traefik/dynamic:ro", TRAEFIK,
               "--providers.file.directory=/etc/traefik/dynamic", "--entrypoints.websecure.address=:443",
               "--entrypoints.websecure.http.tls=true", "--ping=true", "--log.level=ERROR")
        self.addCleanup(docker, "rm", "-f", name, check=False)
        # Up, with its routes read: the web answers through it.
        deadline = time.time() + 30
        while time.time() < deadline:
            if self.get([("arena.test", "/healthz")])[0][0] in (200, 403):
                break
            time.sleep(0.5)
        logs = docker("logs", name, check=False)
        self.assertNotIn("error", (logs.stdout + logs.stderr).lower(), "Traefik could not read routes.yml")

    def get(self, requests: list[tuple[str, str]]) -> list[list]:
        run = docker("run", "--rm", "--name", f"routes-client-{self.tag}", "--network", self.net, "-v", f"{self.dir}:/t:ro",
                     PYTHON, "python", "/t/client.py", json.dumps(requests), check=False)
        self.assertEqual(run.returncode, 0, run.stderr)
        return json.loads(run.stdout)

    def asked(self) -> list[dict]:
        run = docker("exec", self.fake, "python", "-c",
                     "import urllib.request; print(urllib.request.urlopen('http://localhost:8080/asked').read().decode())")
        return json.loads(run.stdout)

    def test_every_host_asks_the_app_about_the_caller_first_and_a_refusal_is_what_the_caller_gets(self):
        self.start_traefik()
        got = self.get([("arena.test", "/"), ("arena.test", "/api/me"), ("gateway.arena.test", "/v1/models"),
                        ("code-intel.arena.test", "/mcp"), ("arena.test", "/refused/page"), ("gateway.arena.test", "/refused/v1"),
                        ("code-intel.arena.test", "/refused/mcp")])
        self.assertEqual([g[0] for g in got[:4]], [200, 200, 200, 200], got)
        self.assertEqual(got[0][1], "8080 arena.test /")
        self.assertEqual(got[3][1], "7700 code-intel.arena.test /mcp")
        self.assertEqual(got[4:], [[403, "refused by the test"]] * 3)
        asked = self.asked()
        self.assertEqual({a["uri"] for a in asked} >= {"/", "/api/me", "/v1/models", "/mcp", "/refused/page"}, True, asked)
        # The caller's own address, as Traefik saw it: what the app decides by.
        self.assertTrue(all(re.fullmatch(r"\d+\.\d+\.\d+\.\d+", a["for"]) for a in asked), asked)

    def test_a_forwarded_ports_host_goes_to_the_app_and_no_other_name_does(self):
        self.start_traefik()
        port = "ws-0123456789abcdef0123456789abcdef-5173.arena.test"
        got = self.get([(port, "/@vite/client"), ("ws-nothex-5173.arena.test", "/"), ("ws-0123456789abcdef0123456789abcdef-5173.arena.test.evil", "/"),
                        (f"x.{port}", "/"), (port, "/refused/x")])
        # The app, with the host as the browser asked it (it finds the workspace and port there); asked about the caller first.
        self.assertEqual(got[0], [200, f"8080 {port} /@vite/client"])
        self.assertEqual([g[0] for g in got[1:4]], [404, 404, 404], got)
        self.assertEqual(got[4], [403, "refused by the test"])

    def test_ip_allow_is_kept_by_traefik_itself_before_the_app_is_asked(self):
        self.start_traefik(allow="10.255.255.0/24")
        before = len(self.asked())
        got = self.get([("arena.test", "/"), ("gateway.arena.test", "/v1/models")])
        self.assertEqual([g[0] for g in got], [403, 403], got)
        self.assertEqual(len(self.asked()), before, "the app was asked about an address IP_ALLOW refuses")

        # The client's network, among others and with spaces as people write them: it comes in.
        subnet = json.loads(docker("network", "inspect", self.net).stdout)[0]["IPAM"]["Config"][0]["Subnet"]
        self.start_traefik(allow=f"10.255.255.0/24, {subnet} ,192.0.2.1")
        self.assertEqual([g[0] for g in self.get([("arena.test", "/"), ("arena.test", "/refused/x")])], [200, 403])

    def test_while_the_app_is_down_nothing_comes_in_not_even_the_web_or_code_intel(self):
        self.start_traefik()
        docker("stop", "-t", "1", self.fake)
        try:
            got = self.get([("arena.test", "/"), ("code-intel.arena.test", "/mcp")])
            # Refused, never let through: the web and code-intel are up, the app that would say is not.
            self.assertTrue(all(g[0] >= 500 for g in got), got)
        finally:
            docker("rm", "-f", self.fake, check=False)
            self.start_fake()


if __name__ == "__main__":
    unittest.main()
