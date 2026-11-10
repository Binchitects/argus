"""Every image that builds Code Arena copies in the files its project links from elsewhere (Llm.Core's shared ones):
a file linked in CodeArena.csproj and missing from an image's COPY lines fails that image's build."""
from __future__ import annotations

import re
import unittest
from pathlib import Path

REPO = Path(__file__).resolve().parents[2]
PROJECT = REPO / "src" / "CodeArena" / "CodeArena.csproj"
IMAGES = [REPO / "deploy" / "services" / "workspaces" / "Dockerfile", REPO / "src" / "Llm.Api" / "Dockerfile"]


def linked() -> list[str]:
    """The files the project compiles from outside its folder, as paths from the repository's root."""
    found = re.findall(r'<Compile Include="\.\./([^"]+)"', PROJECT.read_text())
    return [f"src/{path}" for path in found]


def copied(dockerfile: Path) -> set[str]:
    """What the stage that builds Code Arena (the one copying src/CodeArena/) brings in with its COPY lines: each file
    named, and each folder copied whole (with a trailing /). Another stage's copies are not there when it compiles."""
    stages: list[set[str]] = [set()]
    for line in dockerfile.read_text().splitlines():
        if line.startswith("FROM "):
            stages.append(set())
        elif line.startswith("COPY ") and "--from=" not in line:
            stages[-1].update(line.split()[1:-1])
    return next((s for s in stages if "src/CodeArena/" in s), set())


class CodeArenaImagesTests(unittest.TestCase):
    def test_Each_image_that_builds_Code_Arena_copies_the_files_it_links(self):
        files = linked()
        self.assertTrue(files, "CodeArena.csproj links no file from elsewhere: the check reads it wrong")
        for image in IMAGES:
            text = image.read_text()
            self.assertIn("COPY src/CodeArena/ src/CodeArena/", text, f"{image} no longer builds Code Arena: take it out of IMAGES")
            sources = copied(image)
            missing = [f for f in files if f not in sources and not any(f.startswith(s) for s in sources if s.endswith("/"))]
            self.assertEqual([], missing, f"{image.relative_to(REPO)} does not copy what Code Arena links")


class CodeIntelImageTests(unittest.TestCase):
    def test_CodeIntel_s_image_copies_the_files_its_project_links_from_Code_Arena(self):
        """code-intel links Code Arena's scanners and code graph (its symbol graph): its build stage must copy each one."""
        project = (REPO / "src" / "code-intel" / "CodeIntel.csproj").read_text()
        files = [f"src/{path}" for path in re.findall(r'<Compile Include="\.\./([^"]+)"', project)]
        self.assertTrue(files, "CodeIntel.csproj links nothing from elsewhere: the check reads it wrong")
        stages: list[set[str]] = [set()]
        for line in (REPO / "src" / "code-intel" / "Dockerfile").read_text().splitlines():
            if line.startswith("FROM "):
                stages.append(set())
            elif line.startswith("COPY ") and "--from=" not in line:
                stages[-1].update(line.split()[1:-1])
        build = next(st for st in stages if "src/CodeIntel/" in st)
        self.assertEqual([], [f for f in files if f not in build], "src/CodeIntel/Dockerfile does not copy what code-intel links")


class BuildContextTests(unittest.TestCase):
    def test_What_the_images_copy_from_the_repository_is_in_the_build_context(self):
        """A COPY of a path .dockerignore leaves out fails the build ("not found"): every source a Dockerfile built from the
        repository's root copies is let in. Patterns are read as Docker reads them: in order, the last match decides."""
        import fnmatch
        rules = [l.strip() for l in (REPO / ".dockerignore").read_text().splitlines() if l.strip() and not l.startswith("#")]

        def ignored(path: str) -> bool:
            out = False
            parts = path.strip("/").split("/")
            for rule in rules:
                keep = rule.startswith("!")
                pattern = rule[1:] if keep else rule
                pattern = pattern.strip("/")
                # A pattern matches the path or any folder it is in.
                if any(fnmatch.fnmatchcase("/".join(parts[: i + 1]), pattern) for i in range(len(parts))):
                    out = not keep
            return out

        missing = []
        for image in IMAGES:
            for line in image.read_text().splitlines():
                if line.startswith("COPY ") and "--from=" not in line:
                    for source in line.split()[1:-1]:
                        if not source.startswith("--") and ignored(source):
                            missing.append(f"{image.relative_to(REPO)}: {source}")
        self.assertEqual([], missing, ".dockerignore leaves these out of the build context")


if __name__ == "__main__":
    unittest.main()
