"""Every script the stack ships or a person runs is at least valid: each Python file compiles and each shell
script parses. A rename once left `code-intel = ...` as a variable in clients-check.py, which no test ran."""
from __future__ import annotations

import subprocess
import unittest
from pathlib import Path

REPO = Path(__file__).resolve().parents[2]
ROOTS = [REPO / "deploy" / "scripts", REPO / "deploy" / "config", REPO / "tools"]
SKIP = {"node_modules", ".venv", "__pycache__"}


def files(suffix: str) -> list[Path]:
    found = []
    for root in ROOTS:
        for p in root.rglob(f"*{suffix}"):
            if not SKIP.intersection(p.parts) and p.is_file():
                found.append(p)
    return sorted(found)


class ScriptsCompile(unittest.TestCase):
    def test_every_python_script_compiles(self) -> None:
        bad = []
        for p in files(".py"):
            try:
                compile(p.read_text(encoding="utf-8"), str(p), "exec")
            except SyntaxError as e:
                bad.append(f"{p.relative_to(REPO)}:{e.lineno}: {e.msg}")
        self.assertEqual([], bad)

    def test_every_shell_script_parses(self) -> None:
        bad = []
        scripts = files(".sh") + [REPO / "deploy" / "scripts" / "arenactl"]
        for p in scripts:
            r = subprocess.run(["bash", "-n", str(p)], capture_output=True, text=True)
            if r.returncode != 0:
                bad.append(f"{p.relative_to(REPO)}: {r.stderr.strip()[:200]}")
        self.assertEqual([], bad)


if __name__ == "__main__":
    unittest.main()
