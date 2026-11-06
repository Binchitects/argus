"""tools/code-arena-check.py: a published Code Arena carries everything it needs, read in the file itself. Programs made
by its own sample() stand in for the published ones; nothing here needs .NET."""
from __future__ import annotations

import importlib.util
import re
import shutil
import struct
import subprocess
import sys
import tempfile
import unittest
from pathlib import Path

REPO = Path(__file__).resolve().parents[2]
TOOL = REPO / "tools" / "code-arena-check.py"
spec = importlib.util.spec_from_file_location("code_arena_check", TOOL)
check = importlib.util.module_from_spec(spec)
spec.loader.exec_module(check)


class CodeArenaCheckTests(unittest.TestCase):
    def setUp(self):
        self.dir = Path(tempfile.mkdtemp(prefix="code-arena-check-"))
        self.addCleanup(shutil.rmtree, self.dir, ignore_errors=True)

    def program(self, name: str, data: bytes) -> Path:
        path = self.dir / name
        path.write_bytes(data)
        return path

    def test_a_self_contained_program_with_its_page_carries_everything(self):
        self.assertEqual(check.check(self.program("code-arena", check.sample(windows=False))), [])
        self.assertEqual(check.check(self.program("code-arena.exe", check.sample(windows=True))), [])
        r = subprocess.run([sys.executable, str(TOOL), str(self.dir / "code-arena"), str(self.dir / "code-arena.exe")], capture_output=True, text=True)
        self.assertEqual(r.returncode, 0, r.stdout + r.stderr)
        self.assertIn("code-arena.exe: carries everything it needs", r.stdout)

    def test_a_program_that_needs_dotnet_installed_or_unpacks_libraries_at_start_falls_short(self):
        self.assertEqual(check.check(self.program("code-arena", check.sample(windows=False, self_contained=False))),
                         ["not self-contained: it asks for a .NET runtime installed on the machine"])
        self.assertEqual(check.check(self.program("code-arena", check.sample(windows=False, natives=("libSystem.Native.so",)))),
                         ["native libraries unpacked at start (a writable temporary folder needed): libSystem.Native.so"])
        self.assertEqual(check.check(self.program("code-arena", b"\x7fELF a program that is no bundle")),
                         ["not a .NET single-file bundle: published without PublishSingleFile"])

    def test_without_its_page_it_falls_short_unless_built_without_on_purpose(self):
        program = self.program("code-arena", check.sample(windows=False, web=False))
        self.assertEqual(check.check(program), ["the IDE's page is not embedded (web/code-arena.html, web/browser-check.js): built with --no-web, or without Node.js"])
        self.assertEqual(check.check(program, web=False), [])
        r = subprocess.run([sys.executable, str(TOOL), "--no-web", str(program)], capture_output=True, text=True)
        self.assertEqual(r.returncode, 0, r.stdout + r.stderr)

    def test_on_windows_it_is_a_console_program_so_a_terminal_waits_for_it(self):
        data = bytearray(check.sample(windows=True))
        pe = struct.unpack_from("<I", data, 0x3C)[0]
        struct.pack_into("<H", data, pe + 24 + 68, 2)  # the Windows GUI subsystem
        self.assertEqual(check.check(self.program("code-arena.exe", bytes(data))), ["not a console program: a terminal would not wait for it, nor see what it prints"])
        r = subprocess.run([sys.executable, str(TOOL), str(self.dir / "code-arena.exe")], capture_output=True, text=True)
        self.assertEqual(r.returncode, 1)


class OldestBrowserTests(unittest.TestCase):
    def test_the_page_is_built_for_the_chrome_and_edge_the_window_takes(self):
        """The build's targets (browsers.ts) and the window's MinBrowser (AppWindow.cs) name the same Chrome and Edge."""
        targets = (REPO / "src" / "web" / "src" / "lib" / "browsers.ts").read_text()
        window = (REPO / "src" / "CodeArena" / "AppWindow.cs").read_text()
        least = int(re.search(r"public const int MinBrowser = (\d+);", window).group(1))
        self.assertIn(f"'chrome{least}'", targets)
        self.assertIn(f"'edge{least}'", targets)
        self.assertIn(f"Chrome or Edge {least} or newer", (REPO / "src" / "web" / "public" / "browser-check.js").read_text())
        self.assertIn("target: codeArenaBrowsers", (REPO / "src" / "web" / "vite.code-arena.config.ts").read_text())


if __name__ == "__main__":
    unittest.main()
