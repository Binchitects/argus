"""arenactl, the repository's one command: Code Arena's packages with a fake .NET SDK in a copy of the tree (nothing here
needs .NET, Node, a container engine or the network), the features it builds for, doctor and the list of scripts."""
from __future__ import annotations

import importlib.machinery
import importlib.util
import io
import os
import shutil
import stat
import subprocess
import sys
import tarfile
import tempfile
import unittest
import zipfile
from pathlib import Path

REPO = Path(__file__).resolve().parents[2]

# dotnet as arenactl uses it: an SDK 10 listed, and publish, which writes the program where -o says (with a .pdb beside it):
# the smallest one tools/code-arena-check.py reads as published (FAKE_PROGRAM=framework: one that needs .NET installed).
FAKE_DOTNET = r"""#!/usr/bin/env python3
import importlib.util, os, pathlib, sys
args = sys.argv[1:]
spec = importlib.util.spec_from_file_location("check", "tools/code-arena-check.py")
check = importlib.util.module_from_spec(spec)
spec.loader.exec_module(check)
if args == ["--list-sdks"]:
    print("10.0.100 [/usr/share/dotnet/sdk]")
    sys.exit(0)
if args[:1] == ["restore"]:
    sys.exit(0)
if args[:1] == ["publish"]:
    out = pathlib.Path(args[args.index("-o") + 1])
    rid = args[args.index("-r") + 1]
    out.mkdir(parents=True, exist_ok=True)
    exe = out / ("code-arena.exe" if rid.startswith("win-") else "code-arena")
    exe.write_bytes(check.sample(rid.startswith("win-"), self_contained=os.environ.get("FAKE_PROGRAM") != "framework"))
    (out / "code-arena.pdb").write_bytes(b"symbols")
    sys.exit(0)
if args[:1] == ["build"] and args[1].endswith("tools/ide-launcher/ide-launcher.csproj"):
    out = pathlib.Path(args[args.index("-o") + 1])
    out.mkdir(parents=True, exist_ok=True)
    (out / "ide-launcher.dll").write_text("writes code-arena-ide.exe")
    sys.exit(0)
if args[:1] and args[0].endswith("/ide-launcher.dll"):
    # The IDE's launcher for Windows (FAKE_LAUNCHER=console: one Windows would open a console for).
    pathlib.Path(args[1]).write_bytes(check.sample_launcher(subsystem=3 if os.environ.get("FAKE_LAUNCHER") == "console" else 2))
    sys.exit(0)
sys.exit(1)
"""


def load_check():
    spec = importlib.util.spec_from_file_location("code_arena_check", REPO / "tools" / "code-arena-check.py")
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    return module


def load_arenactl():
    loader = importlib.machinery.SourceFileLoader("arenactl", str(REPO / "arenactl"))
    spec = importlib.util.spec_from_loader("arenactl", loader)
    module = importlib.util.module_from_spec(spec)
    loader.exec_module(module)
    return module


class CodeArenaPackageTests(unittest.TestCase):
    def setUp(self):
        self.dir = Path(tempfile.mkdtemp(prefix="arenactl-test-"))
        self.root = self.dir / "repo"
        (self.root / "src" / "CodeArena" / "web").mkdir(parents=True)
        shutil.copy(REPO / "arenactl", self.root / "arenactl")
        (self.root / "tools").mkdir()
        shutil.copy(REPO / "tools" / "code-arena-check.py", self.root / "tools" / "code-arena-check.py")
        (self.root / "VERSION").write_text("9.9.9\n")
        (self.root / "LICENSE.md").write_text("AGPL\n")
        (self.root / "LICENSING.md").write_text("terms\n")
        (self.root / "src" / "CodeArena" / "CodeArena.csproj").write_text("<Project/>\n")
        (self.root / "src" / "CodeArena" / "web" / "code-arena.html").write_text("<html></html>\n")
        (self.root / "deploy").mkdir()
        self.bin = self.dir / "bin"
        self.bin.mkdir()
        (self.bin / "dotnet").write_text(FAKE_DOTNET)
        (self.bin / "dotnet").chmod(0o755)
        # Only these: no node, docker or podman, so the page built already is used and nothing runs in a container.
        for tool in ("python3", "git", "sh", "bash"):
            if shutil.which(tool):
                (self.bin / tool).symlink_to(shutil.which(tool))

    def tearDown(self):
        shutil.rmtree(self.dir, ignore_errors=True)

    def run_it(self, *args: str, env: dict[str, str] | None = None) -> subprocess.CompletedProcess:
        env = {"PATH": str(self.bin), "HOME": str(self.dir), "SOURCE_DATE_EPOCH": "1790000000", **(env or {})}
        return subprocess.run([sys.executable, str(self.root / "arenactl"), *args], cwd=self.root, env=env, capture_output=True, text=True, timeout=120)

    def test_A_launcher_that_would_open_a_console_stops_the_packages(self):
        r = self.run_it("code-arena", "win-x64", env={"FAKE_LAUNCHER": "console"})
        self.assertNotEqual(r.returncode, 0)
        self.assertIn("code-arena-ide.exe: not a program of the GUI subsystem", r.stdout + r.stderr)
        self.assertFalse((self.root / "dist" / "code-arena-9.9.9-win-x64.zip").exists())

    def test_Each_system_gets_its_package_with_the_program_alone_a_readme_and_the_licences(self):
        r = self.run_it("code-arena", "linux-x64", "win-x64", "win-arm64")
        self.assertEqual(r.returncode, 0, r.stdout + r.stderr)
        self.assertIn("Code Arena's page: built already", r.stdout)
        dist = self.root / "dist"
        with tarfile.open(dist / "code-arena-9.9.9-linux-x64.tar.gz") as t:
            names = t.getnames()
            self.assertEqual(sorted(names), ["code-arena-9.9.9-linux-x64", "code-arena-9.9.9-linux-x64/LICENSE.md", "code-arena-9.9.9-linux-x64/LICENSING.md",
                                             "code-arena-9.9.9-linux-x64/README.txt", "code-arena-9.9.9-linux-x64/code-arena"])
            exe = t.getmember("code-arena-9.9.9-linux-x64/code-arena")
            self.assertEqual((exe.mode & 0o777, exe.uid, exe.gid, exe.mtime), (0o755, 0, 0, 1790000000))
            self.assertEqual(t.extractfile(exe).read(), load_check().sample(False))
            readme = t.extractfile("code-arena-9.9.9-linux-x64/README.txt").read().decode()
        self.assertIn("Code Arena 9.9.9 for Linux (x64)", readme)
        self.assertIn("code-arena login --url https://DOMAIN", readme)
        with zipfile.ZipFile(dist / "code-arena-9.9.9-win-x64.zip") as z:
            self.assertIn("code-arena-9.9.9-win-x64/code-arena.exe", z.namelist())
            # The IDE's launcher beside it, a GUI program, and the readme says which one is for what.
            self.assertEqual(load_check().pe_subsystem(z.read("code-arena-9.9.9-win-x64/code-arena-ide.exe")), 2)
            self.assertIn(b"use code-arena-ide.exe", z.read("code-arena-9.9.9-win-x64/README.txt"))
            # Notepad's line ends.
            self.assertIn(b"\r\n", z.read("code-arena-9.9.9-win-x64/README.txt"))
        with zipfile.ZipFile(dist / "code-arena-9.9.9-win-arm64.zip") as z:
            self.assertIn("code-arena-9.9.9-win-arm64/code-arena.exe", z.namelist())
            self.assertIn(b"Windows (ARM64)", z.read("code-arena-9.9.9-win-arm64/README.txt"))
        # Only the program is kept from the publish.
        self.assertEqual(sorted(p.name for p in (dist / "code-arena" / "linux-x64").iterdir()), ["code-arena"])
        sums = (dist / "SHA256SUMS").read_text().splitlines()
        self.assertEqual([line.split()[1] for line in sums], ["code-arena-9.9.9-linux-x64.tar.gz", "code-arena-9.9.9-win-arm64.zip", "code-arena-9.9.9-win-x64.zip"])

    def test_A_program_that_needs_dotnet_installed_is_not_packaged(self):
        r = self.run_it("code-arena", "linux-x64", env={"FAKE_PROGRAM": "framework"})
        self.assertEqual(r.returncode, 1, r.stdout + r.stderr)
        self.assertIn("linux-x64: not self-contained: it asks for a .NET runtime installed on the machine", r.stdout)
        self.assertFalse((self.root / "dist" / "code-arena-9.9.9-linux-x64.tar.gz").exists())

    def test_The_same_files_give_the_same_package(self):
        self.assertEqual(self.run_it("code-arena", "linux-x64").returncode, 0)
        first = (self.root / "dist" / "code-arena-9.9.9-linux-x64.tar.gz").read_bytes()
        self.assertEqual(self.run_it("code-arena", "linux-x64").returncode, 0)
        self.assertEqual(first, (self.root / "dist" / "code-arena-9.9.9-linux-x64.tar.gz").read_bytes())

    def test_Offline_needs_the_packs_and_says_which_are_missing(self):
        (self.bin / "dotnet").write_text(FAKE_DOTNET.replace('if args[:1] == ["publish"]:',
                                                             'if args[:1] == ["msbuild"]:\n    print("10.0.13")\n    sys.exit(0)\nif args[:1] == ["publish"]:'))
        r = self.run_it("code-arena", "linux-x64", "--offline")
        self.assertEqual(r.returncode, 3, r.stdout + r.stderr)
        self.assertIn("microsoft.netcore.app.runtime.linux-x64.10.0.13.nupkg", r.stderr)
        self.assertIn("without --offline nuget.org is used", r.stderr)

    def test_An_unknown_system_is_refused(self):
        r = self.run_it("code-arena", "plan9-x64")
        self.assertEqual(r.returncode, 2)
        self.assertIn("no system plan9-x64", r.stderr)


class FeatureTests(unittest.TestCase):
    def setUp(self):
        self.a = load_arenactl()

    def test_The_defaults_with_and_without_and_what_they_require(self):
        rows = {f["id"]: f for f in self.a.features()}
        defaults = self.a.choose_features(None, None)
        self.assertEqual(defaults, sorted(i for i, f in rows.items() if f["default"]))
        # A feature brings those it requires.
        needing = next(f for f in rows.values() if f["requires"] and not f["default"])
        on = self.a.choose_features(needing["id"], None)
        for r in needing["requires"]:
            self.assertIn(r, on)
        self.assertEqual(sorted(rows), self.a.choose_features(None, None, all_=True))

    def test_CPU_only_leaves_out_what_needs_a_GPU_and_what_requires_it(self):
        rows = {f["id"]: f for f in self.a.features()}
        gpu = {i for i, f in rows.items() if "gpu" in f["needs"]}
        self.assertTrue(gpu)
        on = set(self.a.choose_features(None, None, cpu_only=True))
        self.assertFalse(gpu & on)
        self.assertTrue(all(set(rows[i]["requires"]) <= on for i in on))
        # Unless asked for by name.
        self.assertIn("pictures", self.a.choose_features("pictures", None, cpu_only=True))

    def test_An_unknown_feature_is_refused(self):
        with self.assertRaises(self.a.Stop):
            self.a.choose_features("no-such-feature", None)


class CommandLineTests(unittest.TestCase):
    def run_it(self, *args: str) -> subprocess.CompletedProcess:
        return subprocess.run([sys.executable, str(REPO / "arenactl"), *args], cwd=REPO, capture_output=True, text=True, timeout=300)

    def test_Doctor_says_each_thing_a_task_needs_with_what_to_do(self):
        r = self.run_it("doctor", "--for", "code-arena")
        self.assertIn(r.returncode, (0, 1))
        self.assertIn("Python 3.8+", r.stdout)
        self.assertIn(".NET SDK 10", r.stdout)
        self.assertIn("Node.js 20+ and npm", r.stdout)

    def test_Scripts_lists_every_script_with_what_it_is_for(self):
        r = self.run_it("scripts")
        self.assertEqual(r.returncode, 0, r.stderr)
        for name in ("installer.sh", "make-installer.sh", "backup.sh", "make-cert.sh", "sign-code-arena.sh", "publish-code-arena.sh"):
            self.assertIn(name, r.stdout)
        listed = [line.split()[0] for line in r.stdout.splitlines() if line.startswith("  ") and line.split()]
        described = [line for line in r.stdout.splitlines() if line.startswith("  ") and len(line.split()) > 1]
        self.assertEqual(len(listed), len(described), "a script without a line saying what it is for")

    def test_Every_check_names_a_script_that_is_there(self):
        a = load_arenactl()
        for name, (script, _) in a.CHECKS.items():
            self.assertTrue((REPO / "deploy" / "scripts" / script).is_file() or (REPO / "tools" / script).is_file(), name)
        for name, (script, _) in a.OPS.items():
            self.assertTrue((REPO / "deploy" / "scripts" / script).is_file(), name)

class Engine:
    name = "docker"


class BuildTests(unittest.TestCase):
    """build_images: the release's images built at once, and, when that fails, each again alone."""

    def build_with(self, fails):
        arenactl = load_arenactl()
        calls = []

        class FakeCompose:
            def __init__(self, engine, profiles, project="arena"):
                pass

            def config(self):
                return {"services": {s: {"build": {"context": ".."}} for s in ("app", "web", "studio")} | {"postgres": {"image": "postgres:17"}}}

            def run(self, *args):
                calls.append(args)
                if fails(args):
                    raise arenactl.Stop("docker failed (exit 1)", 1)

            def close(self):
                pass

        arenactl.Compose = FakeCompose
        arenactl.say = lambda *a, **k: None
        arenactl.step = lambda *a, **k: None
        return arenactl, calls

    def test_images_that_time_out_together_are_built_again_one_at_a_time(self):
        # PyPI times out while three images read it at once; each alone builds.
        arenactl, calls = self.build_with(lambda args: args[0] == "build" and len(args) > 2)
        arenactl.build_images(Engine(), ["core"], pull=False)
        self.assertEqual(calls, [("build", "app", "studio", "web"), ("build", "app"), ("build", "studio"), ("build", "web")])

    def test_an_image_that_fails_alone_fails_the_build(self):
        arenactl, calls = self.build_with(lambda args: args[0] == "build" and "studio" in args)
        with self.assertRaises(arenactl.Stop):
            arenactl.build_images(Engine(), ["core"], pull=False)
        self.assertEqual(calls[-1], ("build", "studio"))
        # One image asked for: it is not built twice.
        arenactl, calls = self.build_with(lambda args: True)
        with self.assertRaises(arenactl.Stop):
            arenactl.build_images(Engine(), ["core"], pull=False, services=["studio"])
        self.assertEqual(calls, [("build", "studio")])


if __name__ == "__main__":
    unittest.main()
