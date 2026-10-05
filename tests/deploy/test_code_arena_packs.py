"""tools/publish-code-arena.sh --offline and the app image's CODE_ARENA=auto: the runtime packs checked
first against the runtime version the SDK publishes with, with a fake dotnet. Nothing here needs .NET, a container engine or
the network."""
from __future__ import annotations

import json
import os
import re
import shutil
import stat
import subprocess
import tempfile
import unittest
from pathlib import Path

REPO = Path(__file__).resolve().parents[2]
RIDS = ["linux-x64", "linux-arm64", "win-x64", "osx-x64", "osx-arm64"]

# dotnet as far as the script uses it: the runtime version the SDK publishes with (FAKE_RUNTIME), the
# runtimes installed (that one and FAKE_OTHER_RUNTIMES), and publish, which writes the program where -o
# says, or fails like a compile error (FAKE_PUBLISH_FAILS). Each call is logged as a JSON line.
FAKE_DOTNET = r"""#!/usr/bin/env python3
import json, os, pathlib, sys
args = sys.argv[1:]
with open(os.environ["FAKE_LOG"], "a") as log:
    log.write(json.dumps(args) + "\n")
if args == ["msbuild", "src/CodeArena/CodeArena.csproj", "-getProperty:BundledNETCoreAppPackageVersion"]:
    print(os.environ["FAKE_RUNTIME"])
    sys.exit(0)
if args[:1] == ["--list-runtimes"]:
    for v in [os.environ["FAKE_RUNTIME"], *os.environ.get("FAKE_OTHER_RUNTIMES", "").split()]:
        print(f"Microsoft.AspNetCore.App {v} [/usr/share/dotnet/shared/Microsoft.AspNetCore.App]")
        print(f"Microsoft.NETCore.App {v} [/usr/share/dotnet/shared/Microsoft.NETCore.App]")
    sys.exit(0)
if args[:1] == ["publish"]:
    if os.environ.get("FAKE_PUBLISH_FAILS"):
        print("error CS1002: ; expected", file=sys.stderr)
        sys.exit(1)
    out = pathlib.Path(args[args.index("-o") + 1])
    rid = args[args.index("-r") + 1]
    out.mkdir(parents=True, exist_ok=True)
    (out / ("code-arena.exe" if rid.startswith("win") else "code-arena")).write_text(f"program for {rid}\n")
    (out / "code-arena.pdb").write_text("symbols\n")
    sys.exit(0)
sys.exit(f"fake dotnet: no {args}")
"""


def dockerfile_auto_step() -> str:
    """The code-arena stage's RUN that builds or skips Code Arena, as the image's shell runs it."""
    lines = (REPO / "src" / "Llm.Api" / "Dockerfile").read_text().splitlines()
    start = next(i for i, line in enumerate(lines) if line.startswith("RUN mkdir -p dist/code-arena"))
    step = []
    for line in lines[start:]:
        step.append(line)
        if not line.rstrip().endswith("\\"):
            break
    return "\n".join(step)[len("RUN "):]


class CodeArenaPacksTests(unittest.TestCase):
    def setUp(self):
        self.dir = Path(tempfile.mkdtemp(prefix="code-arena-packs-"))
        self.addCleanup(shutil.rmtree, self.dir, ignore_errors=True)
        self.repo = self.dir / "repo"
        (self.repo / "tools" / "offline-nuget").mkdir(parents=True)
        shutil.copy2(REPO / "tools" / "publish-code-arena.sh", self.repo / "tools" / "publish-code-arena.sh")
        # The page, built already (as the image's own stage builds it): no Node needed.
        (self.repo / "src" / "CodeArena" / "web").mkdir(parents=True)
        (self.repo / "src" / "CodeArena" / "web" / "code-arena.html").write_text("<!doctype html>\n")
        self.bin = self.dir / "bin"
        self.bin.mkdir()
        dotnet = self.bin / "dotnet"
        dotnet.write_text(FAKE_DOTNET)
        dotnet.chmod(dotnet.stat().st_mode | stat.S_IXUSR)
        self.log = self.dir / "dotnet.log"
        self.log.touch()

    def packs(self, version: str, rids: list[str] = RIDS, name=lambda pack, rid, v: f"microsoft.netcore.app.{pack}.{rid}.{v}.nupkg") -> None:
        for rid in rids:
            for pack in ("runtime", "host"):
                (self.repo / "tools" / "offline-nuget" / name(pack, rid, version)).write_text("a signed zip")

    def env(self, runtime: str, **extra: str) -> dict[str, str]:
        env = {k: v for k, v in os.environ.items() if not k.startswith("FAKE_")}
        env.update({"PATH": f"{self.bin}:{env.get('PATH', '/usr/bin:/bin')}", "FAKE_LOG": str(self.log), "FAKE_RUNTIME": runtime})
        env.update(extra)
        return env

    def publish(self, runtime: str, *args: str, **extra: str) -> subprocess.CompletedProcess:
        return subprocess.run(["bash", "tools/publish-code-arena.sh", *args], cwd=self.repo, env=self.env(runtime, **extra),
                              capture_output=True, text=True, timeout=60)

    def image_step(self, runtime: str, mode: str = "auto", **extra: str) -> subprocess.CompletedProcess:
        """The Dockerfile's step, in /bin/sh (dash in the SDK image) with CODE_ARENA as the build argument sets it."""
        return subprocess.run(["sh", "-c", dockerfile_auto_step()], cwd=self.repo, env=self.env(runtime, CODE_ARENA=mode, **extra),
                              capture_output=True, text=True, timeout=60)

    def publishes(self) -> list[list[str]]:
        return [c for c in (json.loads(line) for line in self.log.read_text().splitlines()) if c[:1] == ["publish"]]

    def built(self) -> list[str]:
        out = self.repo / "dist" / "code-arena"
        return sorted(str(p.relative_to(out)) for p in out.rglob("code-arena*")) if out.exists() else []

    def test_packs_for_an_older_runtime_than_the_sdks_build_nothing_and_name_the_version_to_fetch(self):
        self.packs("10.0.12")
        r = self.publish("10.0.13", "--offline")
        self.assertEqual(r.returncode, 3, r.stderr)
        self.assertIn("lacks 10 of the 10 .NET packs this SDK publishes with (runtime 10.0.13)", r.stderr)
        self.assertIn("microsoft.netcore.app.runtime.linux-x64.10.0.13.nupkg", r.stderr)
        self.assertIn("microsoft.netcore.app.host.osx-arm64.10.0.13.nupkg", r.stderr)
        self.assertIn("Fetch them at version 10.0.13", r.stderr)
        self.assertEqual(self.publishes(), [])
        self.assertEqual(self.built(), [])

    def test_an_empty_folder_is_the_same_case(self):
        r = self.publish("10.0.12", "--offline", "linux-x64")
        self.assertEqual(r.returncode, 3, r.stderr)
        self.assertIn("lacks 2 of the 2 .NET packs", r.stderr)
        self.assertEqual(self.publishes(), [])

    def test_packs_at_the_sdks_runtime_build_every_system_from_that_folder_only(self):
        self.packs("10.0.13")
        self.packs("10.0.12")  # older ones beside them are left alone
        r = self.publish("10.0.13", "--offline")
        self.assertEqual(r.returncode, 0, r.stderr)
        calls = self.publishes()
        self.assertEqual([c[c.index("-r") + 1] for c in calls], RIDS)
        for c in calls:
            self.assertIn("--source", c)
            self.assertEqual(c[c.index("--source") + 1], "tools/offline-nuget")
        self.assertEqual(self.built(), sorted(f"{rid}/code-arena{'.exe' if rid.startswith('win') else ''}" for rid in RIDS))
        sums = (self.repo / "dist" / "code-arena" / "SHA256SUMS").read_text()
        self.assertEqual(len(sums.splitlines()), 5)

    def test_only_the_systems_asked_for_need_packs_and_any_case_of_the_names_does(self):
        self.packs("10.0.13", ["linux-x64"], name=lambda pack, rid, v: f"Microsoft.NETCore.App.{pack.title()}.{rid}.{v}.nupkg")
        r = self.publish("10.0.13", "--offline", "linux-x64")
        self.assertEqual(r.returncode, 0, r.stderr)
        self.assertEqual(self.built(), ["linux-x64/code-arena"])
        r = self.publish("10.0.13", "--offline", "linux-x64", "win-x64")
        self.assertEqual(r.returncode, 3, r.stderr)
        self.assertIn("lacks 2 of the 4", r.stderr)
        self.assertNotIn("linux-x64", r.stderr)

    def test_a_newer_dotnet_installed_beside_the_sdk_does_not_change_the_packs_it_needs(self):
        # A .NET 11 preview's runtime next to SDK 10.0's: the SDK still publishes with 10.0.12.
        self.packs("10.0.12")
        r = self.publish("10.0.12", "--offline", FAKE_OTHER_RUNTIMES="11.0.0-rc.2.26473.103 10.0.13")
        self.assertEqual(r.returncode, 0, r.stderr)
        self.assertEqual(len(self.built()), 5)

    def test_an_sdk_that_does_not_say_its_runtime_fails_rather_than_skipping(self):
        self.packs("10.0.12")
        r = self.publish("", "--offline")
        self.assertEqual(r.returncode, 1, r.stderr)
        self.assertIn("The .NET SDK did not say which runtime it publishes with", r.stderr)
        self.assertEqual(self.publishes(), [])
        r = self.image_step("")
        self.assertNotEqual(r.returncode, 0)
        self.assertNotIn("not built", r.stdout)

    def test_the_image_skips_code_arena_when_the_packs_do_not_match_its_sdk_and_builds_it_when_they_do(self):
        self.packs("10.0.12")
        r = self.image_step("10.0.13")
        self.assertEqual(r.returncode, 0, r.stderr)
        self.assertIn("Code Arena: not built, tools/offline-nuget lacks the packs above", r.stdout)
        self.assertIn("runtime 10.0.13", r.stderr)
        self.assertEqual(self.built(), [])
        self.assertTrue((self.repo / "dist" / "code-arena").is_dir())

        r = self.image_step("10.0.12")
        self.assertEqual(r.returncode, 0, r.stderr)
        self.assertEqual(len(self.built()), 5)

    def test_the_image_fails_on_a_real_build_error_and_skips_with_none(self):
        self.packs("10.0.12")
        r = self.image_step("10.0.12", FAKE_PUBLISH_FAILS="1")
        self.assertNotEqual(r.returncode, 0)
        self.assertNotIn("not built", r.stdout)

        calls = len(self.publishes())
        r = self.image_step("10.0.12", mode="none")
        self.assertEqual(r.returncode, 0, r.stderr)
        self.assertIn("Code Arena: not built (CODE_ARENA=none)", r.stdout)
        self.assertEqual(len(self.publishes()), calls)
        self.assertEqual(self.built(), [])

    def test_the_step_is_plain_sh(self):
        # dash in the SDK image: no bash-only syntax in the step itself.
        self.assertIsNone(re.search(r"\[\[|\$\(\(|<<<|function ", dockerfile_auto_step()))


if __name__ == "__main__":
    unittest.main()
