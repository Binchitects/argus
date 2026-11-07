"""deploy/services/workspaces/supervisor.py: what a workspace is asked with, its slot, who may drive it, and its engine
started and stopped, with git, the engine and the users' processes stubbed (no root here)."""
from __future__ import annotations

import importlib.util
import io
import json
import os
import subprocess
import tempfile
import unittest
from pathlib import Path
from unittest import mock

REPO = Path(__file__).resolve().parents[2]
spec = importlib.util.spec_from_file_location("workspaces_supervisor", REPO / "deploy/services/workspaces/supervisor.py")
sup = importlib.util.module_from_spec(spec)
spec.loader.exec_module(sup)

WID = "0123456789abcdef0123456789abcdef"
GOOD = {"repo": "https://gitlab.example.test/team/shop.git", "branch": "main", "git_user": "dana", "git_token": "glpat-SECRET-123",
        "arena_url": "https://llm.example.test", "api_key": "sk-0123456789abcdef"}


class Base(unittest.TestCase):
    def setUp(self):
        self.dir = tempfile.TemporaryDirectory()
        self.passwd, self.group = Path(self.dir.name, "passwd"), Path(self.dir.name, "group")
        self.passwd.write_text("root:x:0:0:root:/root:/bin/bash\n")
        self.group.write_text("root:x:0:\n")
        patches = [mock.patch.object(sup, "WORKSPACES", self.dir.name), mock.patch.object(sup, "_own"), mock.patch.object(sup, "_kill_user"),
                   mock.patch.dict(sup._running, {}, clear=True), mock.patch.object(sup, "PASSWD", str(self.passwd)), mock.patch.object(sup, "GROUP", str(self.group))]
        for p in patches:
            p.start()
            self.addCleanup(p.stop)
        self.addCleanup(self.dir.cleanup)


class Asked(Base):
    def test_a_workspace_needs_a_project_a_branch_name_arenas_address_and_a_key(self):
        sup._check(GOOD)
        # The app over the stack's own network, and a company's GitLab on http.
        sup._check({**GOOD, "arena_url": "http://app:8080", "repo": "http://gitlab.corp:8929/team/shop.git"})
        for bad, why in [({"repo": "git@host:team/shop.git"}, "repo"), ({"repo": "https://host/x; rm -rf /"}, "repo"),
                         ({"branch": "--upload-pack=x y"}, "branch"), ({"arena_url": "ftp://llm"}, "arena_url"), ({"arena_url": "app:8080"}, "arena_url"), ({"api_key": "short"}, "api_key")]:
            with self.assertRaises(sup.Refused) as refused:
                sup._check({**GOOD, **bad})
            self.assertEqual(refused.exception.status, 400)
            self.assertIn(why, str(refused.exception))

    def test_each_workspace_keeps_its_slot_and_a_new_one_takes_the_first_free(self):
        a, b = "a" * 32, "b" * 32
        self.assertEqual(sup._slot(a), 0)
        self.assertEqual(sup._slot(b), 1)
        self.assertEqual(sup._slot(a), 0)
        with mock.patch.object(sup, "SLOTS", 2), self.assertRaises(sup.Refused) as full:
            sup._slot("c" * 32)
        self.assertEqual(full.exception.status, 503)


class Cloned(Base):
    def test_a_clone_that_fails_says_why_without_the_token(self):
        failed = subprocess.CompletedProcess([], 128, "", "fatal: Authentication failed for 'https://oauth2:glpat-SECRET-123@gitlab.example.test/team/shop.git'")
        with mock.patch.object(sup.subprocess, "run", return_value=failed) as run, self.assertRaises(sup.Refused) as refused:
            sup._clone(WID, 30000, GOOD)
        self.assertEqual(refused.exception.status, 502)
        self.assertNotIn("glpat-SECRET-123", str(refused.exception))
        self.assertIn("Authentication failed", str(refused.exception))
        # The token is read through the askpass helper from the workspace's home, never on git's command line.
        cmd = run.call_args.args[0]
        self.assertNotIn("glpat-SECRET-123", " ".join(cmd))
        self.assertEqual(cmd[-3:], ["--", GOOD["repo"], os.path.join(self.dir.name, WID, "repo")])
        home = Path(self.dir.name, WID, "home")
        # In the engine's own config folder, which the agent's sandbox hides (as it hides the engine's sessions): the
        # commands the model runs cannot read the person's token. Before: $HOME/.git-token, readable by them.
        token = home / ".config" / "code-arena" / "git-token"
        self.assertEqual(token.read_text(), "glpat-SECRET-123")
        self.assertEqual(oct(token.stat().st_mode & 0o777), "0o600")
        self.assertFalse((home / ".git-token").exists())
        self.assertIn(".config/code-arena/git-token", sup.ASKPASS)
        self.assertEqual(run.call_args.kwargs["env"]["GIT_ASKPASS"], str(home / ".git-askpass"))

    def test_a_token_left_where_the_sandbox_reads_is_taken_away(self):
        home = Path(self.dir.name, WID, "home")
        home.mkdir(parents=True)
        (home / ".git-token").write_text("glpat-OLD")
        with mock.patch.object(sup.subprocess, "run", return_value=subprocess.CompletedProcess([], 0, "", "")):
            sup._clone(WID, 30000, GOOD)
        self.assertFalse((home / ".git-token").exists())


class Committed(Base):
    def test_the_workspace_commits_as_its_person_unless_they_set_another_name(self):
        # Before: no identity in the workspace, so every commit (the IDE's, the agent's after a turn) failed with
        # "Author identity unknown".
        home = Path(self.dir.name, WID, "home")
        spec = {**GOOD, "git_name": "Dana Smith", "git_email": "dana@example.test"}
        done = subprocess.CompletedProcess([], 0, "", "")
        with mock.patch.object(sup.subprocess, "run", return_value=done):
            sup._clone(WID, 30000, spec)
        config = home / ".gitconfig"
        got = lambda key: subprocess.run(["git", "config", "--file", str(config), "--get", key], capture_output=True, text=True).stdout.strip()
        self.assertEqual((got("user.name"), got("user.email")), ("Dana Smith", "dana@example.test"))
        # Their own, set in a terminal: kept.
        subprocess.run(["git", "config", "--file", str(config), "user.email", "dana@home.test"], check=True)
        with mock.patch.object(sup.subprocess, "run", return_value=done):
            sup._clone(WID, 30000, spec)
        self.assertEqual(got("user.email"), "dana@home.test")

    def test_a_name_or_email_that_would_write_more_config_is_refused(self):
        for bad in ({"git_name": "Dana\n[core]\n\tsshCommand = evil"}, {"git_email": "x" * 300}, {"git_email": 7}):
            with self.assertRaises(sup.Refused) as refused:
                sup._check({**GOOD, **bad})
            self.assertEqual(refused.exception.status, 400)


class Sandboxed(Base):
    def test_health_says_whether_the_agents_commands_have_a_sandbox(self):
        # bubblewrap made a sandbox as a user of no workspace: on; it could not (the container's security options): why.
        works = subprocess.CompletedProcess([], 0, "", "")
        with mock.patch.object(sup.subprocess, "run", return_value=works) as run, mock.patch.object(sup.shutil, "which", return_value="/usr/bin/bwrap"):
            self.assertEqual(sup._sandbox(), {"sandbox": "bubblewrap"})
        cmd = run.call_args.args[0]
        self.assertEqual(cmd[0], "/usr/bin/bwrap")
        # As a command of the agent's is sandboxed: its own /proc, its own processes.
        self.assertIn("--proc", cmd)
        self.assertIn("--unshare-pid", cmd)
        failed = subprocess.CompletedProcess([], 1, "", "bwrap: Can't mount proc on /newroot/proc: Operation not permitted\n")
        with mock.patch.object(sup.subprocess, "run", return_value=failed), mock.patch.object(sup.shutil, "which", return_value="/usr/bin/bwrap"):
            off = sup._sandbox()
        self.assertEqual(off["sandbox"], "off")
        self.assertIn("Can't mount proc", off["why"])
        with mock.patch.object(sup.shutil, "which", return_value=None):
            self.assertIn("not installed", sup._sandbox()["why"])


class Started(Base):
    def test_a_workspace_is_cloned_once_its_engine_started_with_its_own_key_and_the_persons_access_and_stopped(self):
        cloned = subprocess.CompletedProcess([], 0, "", "")

        def clone(cmd, **kw):
            os.makedirs(os.path.join(cmd[-1], ".git"))
            return cloned

        engine = mock.Mock()
        engine.poll.return_value = None
        engine.pid = 4321
        with mock.patch.object(sup.subprocess, "run", side_effect=clone) as run, mock.patch.object(sup.subprocess, "Popen", return_value=engine) as popen, \
                mock.patch.object(sup, "_listening", return_value=True):
            first = sup.start(WID, GOOD)
            self.assertEqual((first["port"], first["cloned"]), (sup.FIRST_PORT, True))
            self.assertGreaterEqual(len(first["key"]), 32)
            cmd, kw = popen.call_args.args[0], popen.call_args.kwargs
            self.assertEqual(cmd[1:], ["web", "--serve", "--port", str(sup.FIRST_PORT)])
            self.assertEqual(kw["cwd"], os.path.join(self.dir.name, WID, "repo"))
            self.assertEqual((kw["env"]["CODE_ARENA_TOKEN"], kw["env"]["ARENA_API_KEY"], kw["env"]["ARENA_URL"]), (first["key"], GOOD["api_key"], GOOD["arena_url"]))
            self.assertNotIn("glpat-SECRET-123", json.dumps(kw["env"]))
            # Asked again while it runs: the same engine.
            self.assertEqual(sup.start(WID, GOOD), {"port": first["port"], "key": first["key"], "cloned": False})
            self.assertEqual(run.call_count, 1)
            self.assertEqual([w["id"] for w in sup.running()], [WID])

            with mock.patch.object(sup.os, "killpg") as killpg:
                sup.stop(WID)
            killpg.assert_called_once()
            sup._kill_user.assert_called_with(30000)
            self.assertEqual(sup.running(), [])
            # Started again: not cloned again, a new key.
            again = sup.start(WID, GOOD)
            self.assertEqual(again["cloned"], False)
            self.assertNotEqual(again["key"], first["key"])

    def test_an_engine_that_does_not_listen_is_killed_and_what_it_said_is_told(self):
        engine = mock.Mock()

        def said(cmd, **kw):
            kw["stdout"].write(b"\xe2\x9c\x97 Port 4100 is taken:\n  choose another\n")
            return engine

        with mock.patch.object(sup, "_clone", return_value=False), mock.patch.object(sup.subprocess, "Popen", side_effect=said), \
                mock.patch.object(sup, "_listening", return_value=False), self.assertRaises(sup.Refused) as refused:
            sup.start(WID, GOOD)
        engine.kill.assert_called_once()
        self.assertEqual(refused.exception.status, 502)
        self.assertEqual(str(refused.exception), "The workspace's engine did not start: \u2717 Port 4100 is taken: choose another")


class Trusted(Base):
    CA = "-----BEGIN CERTIFICATE-----\nMIIBtest\n-----END CERTIFICATE-----\n"

    def test_the_company_ca_is_one_bundle_with_the_systems_and_every_tool_is_pointed_at_it(self):
        system = Path(self.dir.name, "system.pem")
        system.write_text("-----BEGIN CERTIFICATE-----\nSYSTEM\n-----END CERTIFICATE-----\n")
        home = Path(self.dir.name, "home")
        home.mkdir()
        # Before: nothing in the home, and git, curl, Python and .NET check GitLab's certificate against the system's CAs alone.
        self.assertNotIn("GIT_SSL_CAINFO", sup._env(str(home), GOOD, None))
        with mock.patch.object(sup, "SYSTEM_CAS", str(system)):
            sup._trust(str(home), self.CA)
        bundle = (home / ".ca-bundle.pem").read_text()
        self.assertIn("SYSTEM", bundle)
        self.assertIn("MIIBtest", bundle)
        env = sup._env(str(home), GOOD, "k" * 40)
        for name in ("GIT_SSL_CAINFO", "CURL_CA_BUNDLE", "SSL_CERT_FILE", "REQUESTS_CA_BUNDLE", "PIP_CERT"):
            self.assertEqual(env[name], str(home / ".ca-bundle.pem"))
        self.assertEqual(env["NODE_EXTRA_CA_CERTS"], str(home / ".company-ca.pem"))
        # Taken away in Settings: gone at the next start.
        sup._trust(str(home), "")
        self.assertFalse((home / ".ca-bundle.pem").exists())
        self.assertNotIn("SSL_CERT_FILE", sup._env(str(home), GOOD, None))

    def test_a_ca_with_a_key_or_no_certificate_is_refused(self):
        sup._check({**GOOD, "ca": self.CA})
        sup._check({**GOOD, "ca": ""})
        for bad in ("-----BEGIN PRIVATE KEY-----\nx\n-----END PRIVATE KEY-----", "not a certificate", 7):
            with self.assertRaises(sup.Refused) as refused:
                sup._check({**GOOD, "ca": bad})
            self.assertIn("ca", str(refused.exception))

    def test_the_clone_gets_the_ca(self):
        cloned = subprocess.CompletedProcess([], 0, "", "")
        with mock.patch.object(sup.subprocess, "run", return_value=cloned) as run:
            sup._clone(WID, 30000, {**GOOD, "ca": self.CA})
        self.assertEqual(run.call_args.kwargs["env"]["GIT_SSL_CAINFO"], os.path.join(self.dir.name, WID, "home", ".ca-bundle.pem"))


TCP = """  sl  local_address rem_address   st tx_queue rx_queue tr tm->when retrnsmt   uid  timeout inode
   0: 0100007F:1435 00000000:0000 0A 00000000:00000000 00:00000000 00000000 30000        0 111 1 0000000000000000 100 0 0 10 0
   1: 00000000:1004 00000000:0000 0A 00000000:00000000 00:00000000 00000000 30000        0 222 1 0000000000000000 100 0 0 10 0
   2: 00000000:1F90 00000000:0000 0A 00000000:00000000 00:00000000 00000000 30001        0 333 1 0000000000000000 100 0 0 10 0
   3: 0100007F:1435 0100007F:D431 01 00000000:00000000 00:00000000 00000000 30000        0 444 1 0000000000000000 100 0 0 10 0
"""
TCP6 = """  sl  local_address                         remote_address                        st tx_queue rx_queue tr tm->when retrnsmt   uid  timeout inode
   0: 00000000000000000000000001000000:0BB8 00000000000000000000000000000000:0000 0A 00000000:00000000 00:00000000 00000000 30000        0 555 1 0000000000000000 100 0 0 10 0
   1: 00000000000000000000000000000000:1F40 00000000000000000000000000000000:0000 0A 00000000:00000000 00:00000000 00000000 30000        0 666 1 0000000000000000 100 0 0 10 0
"""


class Ports(Base):
    """What a workspace's own programs listen on (a dev server), from the container's /proc/net, and reached through the relay."""

    def setUp(self):
        super().setUp()
        net = Path(self.dir.name, "net")
        net.mkdir()
        (net / "tcp").write_text(TCP)
        (net / "tcp6").write_text(TCP6)
        patch = mock.patch.object(sup, "PROC_NET", str(net))
        patch.start()
        self.addCleanup(patch.stop)
        engine = mock.Mock()
        engine.poll.return_value = None
        engine.pid = 10
        sup._running[WID] = {"process": engine, "port": 4100, "key": "k", "since": 1, "uid": 30000}

    def test_the_ports_its_user_listens_on_with_their_addresses_and_not_another_workspaces(self):
        self.assertEqual(sup._listeners(30000), {5173: "127.0.0.1", 4100: "0.0.0.0", 3000: "::1", 8000: "::"})
        self.assertEqual(sup._listeners(30001), {8080: "0.0.0.0"})

    def test_a_workspaces_ports_are_its_programs_not_its_engines_with_what_runs_them(self):
        found = {111: (42, "node /workspaces/x/repo/node_modules/.bin/vite"), 555: (43, "python3 -m http.server 3000 --bind ::1"), 666: (10, "dotnet code-arena.dll")}
        with mock.patch.object(sup, "_sockets", return_value=found) as sockets:
            ports = sup.ports(WID)
        sockets.assert_called_once_with(30000)
        # The engine's own port (4100) and what the engine itself listens on (8000) are not the person's to forward.
        self.assertEqual(ports, [
            {"port": 3000, "address": "::1", "pid": 43, "process": "python3 -m http.server 3000 --bind ::1"},
            {"port": 5173, "address": "127.0.0.1", "pid": 42, "process": "node /workspaces/x/repo/node_modules/.bin/vite"},
        ])
        with self.assertRaises(sup.Refused) as asleep:
            sup.ports("f" * 32)
        self.assertEqual(asleep.exception.status, 404)

    def test_the_relay_reaches_only_a_port_the_workspace_listens_on(self):
        self.assertEqual(sup._target(WID, 5173), ("127.0.0.1", 5173))
        # Any address: the loopback of its family.
        self.assertEqual(sup._target(WID, 3000), ("::1", 3000))
        with mock.patch.object(sup, "_sockets", return_value={}):
            self.assertEqual(sup._target(WID, 8000), ("::1", 8000))
        for port, status in ((4100, 403), (8080, 404), (9, 404)):
            with self.assertRaises(sup.Refused) as refused:
                sup._target(WID, port)
            self.assertEqual(refused.exception.status, status, port)

    def test_a_connect_with_arenas_key_is_joined_to_the_dev_server_end_to_end(self):
        import socket
        import threading
        server = socket.socket()
        server.bind(("127.0.0.1", 0))
        server.listen()
        port = server.getsockname()[1]

        def echo():
            conn, _ = server.accept()
            with conn:
                while data := conn.recv(1024):
                    conn.sendall(b"echo:" + data)
        threading.Thread(target=echo, daemon=True).start()
        relay = sup.ThreadingHTTPServer(("127.0.0.1", 0), sup.Handler)
        threading.Thread(target=relay.serve_forever, daemon=True).start()
        self.addCleanup(relay.server_close)
        self.addCleanup(relay.shutdown)
        self.addCleanup(server.close)

        def connect(key: str, target: str) -> tuple[socket.socket, bytes]:
            s = socket.create_connection(relay.server_address, timeout=5)
            s.sendall(f"CONNECT {target} HTTP/1.1\r\nHost: {target}\r\nAuthorization: Bearer {key}\r\n\r\n".encode())
            head = b""
            while b"\r\n\r\n" not in head:
                head += s.recv(1)
            return s, head

        with mock.patch.object(sup, "KEY", "k" * 40), mock.patch.object(sup, "_target", return_value=("127.0.0.1", port)) as target:
            refused, head = connect("wrong", f"{WID}:5173")
            refused.close()
            self.assertIn(b" 401 ", head.split(b"\r\n")[0])
            joined, head = connect("k" * 40, f"{WID}:5173")
            self.assertIn(b" 200 ", head.split(b"\r\n")[0])
            target.assert_called_with(WID, 5173)
            joined.sendall(b"GET / HTTP/1.1\r\n\r\n")
            self.assertEqual(joined.recv(1024), b"echo:GET / HTTP/1.1\r\n\r\n")
            joined.close()
            bad, head = connect("k" * 40, "not-a-workspace:80")
            bad.close()
            self.assertIn(b" 404 ", head.split(b"\r\n")[0])


class Named(Base):
    def test_each_workspace_user_has_a_name_a_group_and_its_home_in_the_system(self):
        # Before: uid 30000 was in neither file: "I have no name!", whoami and getpass failed in its terminals.
        sup._user(30000, "/workspaces/a/home")
        sup._user(30001, "/workspaces/b/home")
        self.assertIn("ws0:x:30000:30000:Code Studio workspace:/workspaces/a/home:/bin/bash", self.passwd.read_text().splitlines())
        self.assertIn("ws1:x:30001:", self.group.read_text().splitlines())
        # The slot taken by another workspace: its home moves, the line is not repeated, the others stay.
        sup._user(30000, "/workspaces/c/home")
        lines = self.passwd.read_text().splitlines()
        self.assertEqual([x for x in lines if ":30000:" in x], ["ws0:x:30000:30000:Code Studio workspace:/workspaces/c/home:/bin/bash"])
        self.assertEqual(lines[0], "root:x:0:0:root:/root:/bin/bash")
        self.assertEqual(sum(":30001:" in x for x in lines), 1)


class Waited(Base):
    def test_an_engine_that_ends_is_not_waited_for(self):
        # Before: an engine that stopped at once (Arena not reached) was waited for the whole 30 s before it was said.
        ended = mock.Mock()
        ended.poll.return_value = 1
        started = sup.time.monotonic()
        self.assertFalse(sup._listening(1, 30, ended))
        self.assertLess(sup.time.monotonic() - started, 1)


class Driven(Base):
    def _ask(self, method, path, body=None, key="k" * 40):
        handler = sup.Handler.__new__(sup.Handler)
        data = json.dumps(body).encode() if body is not None else b""
        handler.path = path
        handler.headers = {"Authorization": f"Bearer {key}", "Content-Length": str(len(data))}
        handler.rfile = io.BytesIO(data)
        handler.wfile = io.BytesIO()
        answered = {}

        def answer(status, payload):
            answered["status"], answered["body"] = status, payload
        handler._answer = answer
        with mock.patch.object(sup, "KEY", "k" * 40):
            handler._handle(method)
        return answered["status"], answered["body"]

    def test_only_arenas_key_drives_it_and_health_is_open(self):
        self.assertEqual(self._ask("GET", "/health", key="x")[0], 200)
        self.assertEqual(self._ask("GET", "/v1/workspaces", key="wrong")[0], 401)
        self.assertEqual(self._ask("GET", "/v1/workspaces"), (200, []))
        self.assertEqual(self._ask("PUT", "/v1/workspaces/not-an-id", GOOD)[0], 404)
        self.assertEqual(self._ask("PUT", f"/v1/workspaces/{WID}", {**GOOD, "repo": "ftp://x"})[0], 400)
        with mock.patch.object(sup, "delete") as delete:
            self.assertEqual(self._ask("DELETE", f"/v1/workspaces/{WID}"), (200, {"deleted": True}))
        delete.assert_called_once_with(WID)

    def test_a_start_sent_in_chunks_is_read_whole(self):
        # Arena's HttpClient sends its JSON chunked (no Content-Length). Before: read as no body, every start refused
        # with "repo: the project's address", so no workspace ever started on a real service.
        data = json.dumps(GOOD).encode()
        chunked = b"".join(b"%x\r\n%s\r\n" % (len(data[i:i + 50]), data[i:i + 50]) for i in range(0, len(data), 50)) + b"0\r\n\r\n"
        handler = sup.Handler.__new__(sup.Handler)
        handler.path = f"/v1/workspaces/{WID}"
        handler.headers = {"Authorization": "Bearer " + "k" * 40, "Transfer-Encoding": "chunked"}
        handler.rfile = io.BytesIO(chunked)
        handler.wfile = io.BytesIO()
        answered = {}
        handler._answer = lambda status, payload: answered.update(status=status, body=payload)
        with mock.patch.object(sup, "KEY", "k" * 40), mock.patch.object(sup, "start", return_value={"port": 4100, "key": "x", "cloned": True}) as start:
            handler._handle("PUT")
        self.assertEqual(answered["status"], 200, answered)
        start.assert_called_once_with(WID, GOOD)
        # Too large in chunks too.
        big = b"%x\r\n%s\r\n0\r\n\r\n" % (70 * 1024, b"x" * 70 * 1024)
        handler.rfile = io.BytesIO(big)
        with mock.patch.object(sup, "KEY", "k" * 40):
            handler._handle("PUT")
        self.assertEqual(answered["status"], 413)


if __name__ == "__main__":
    unittest.main()
