#!/usr/bin/env python3
"""
A stand-in for docker and podman on PATH, for the deploy script tests: it logs
every call (one JSON list a line, to FAKE_LOG) and answers the few the scripts
make with small fake files, so they run with no container engine at all.

    FAKE_IMAGES       what `compose config --images` prints (lines)
    FAKE_SERVICES     what `compose config --services` prints; unset: it fails
    FAKE_MISSING      image references `image inspect` does not find
    FAKE_VOLUMES      volumes `volume inspect` finds
    FAKE_VOLUME_LIST  what `volume ls -q` prints (no filter)
    FAKE_ENGINE       a folder standing for the engine volume (`cat /engine/X`)
"""
from __future__ import annotations

import hashlib
import io
import json
import os
import sys
import tarfile


def lines(name: str) -> list[str]:
    return [x for x in os.environ.get(name, "").splitlines() if x.strip()]


def mount(args: list[str], target: str) -> str | None:
    """The host side of `-v HOST:TARGET[:ro]`."""
    for i, a in enumerate(args):
        if a == "-v" and i + 1 < len(args):
            parts = args[i + 1].split(":")
            if len(parts) >= 2 and parts[1] == target:
                return parts[0]
    return None


def main() -> int:
    args = sys.argv[1:]
    with open(os.environ["FAKE_LOG"], "a") as log:
        log.write(json.dumps([os.path.basename(sys.argv[0])] + args) + "\n")
    if not args:
        return 0
    if args[0] == "compose":
        if "config" in args:
            if "--images" in args:
                print("\n".join(lines("FAKE_IMAGES")))
                return 0
            if "--services" in args:
                if "FAKE_SERVICES" not in os.environ:
                    return 1
                print("\n".join(lines("FAKE_SERVICES")))
                return 0
            return 0
        return 0
    if args[:2] == ["image", "inspect"]:
        ref = args[-1]
        if ref in lines("FAKE_MISSING"):
            print(f"Error: No such image: {ref}", file=sys.stderr)
            return 1
        print("sha256:" + hashlib.sha256(ref.encode()).hexdigest())
        return 0
    if args[0] == "save":
        out = args[args.index("-o") + 1]
        with open(out, "w") as f:
            f.write(f"fake image {args[-1]}\n")
        return 0
    if args[0] == "load":
        print(f"Loaded image from {args[args.index('-i') + 1]}")
        return 0
    if args[:2] == ["volume", "inspect"]:
        return 0 if args[-1] in lines("FAKE_VOLUMES") else 1
    if args[:2] == ["volume", "create"]:
        print(args[-1])
        return 0
    if args[:2] == ["volume", "ls"]:
        if not any(a.startswith("label=") for a in args):
            print("\n".join(lines("FAKE_VOLUME_LIST")))
        return 0
    if args[0] == "run":
        if "cat" in args:
            name = args[args.index("cat") + 1]
            path = os.path.join(os.environ.get("FAKE_ENGINE", "/nonexistent"), os.path.basename(name))
            if not os.path.isfile(path):
                return 1
            sys.stdout.write(open(path).read())
            return 0
        joined = " ".join(args)
        if "tar -czf /out/audio.tar.gz" in joined:
            out = mount(args, "/out")
            with tarfile.open(os.path.join(out, "audio.tar.gz"), "w:gz") as tar:
                data = b"a speech model\n"
                info = tarfile.TarInfo("models--speaches-ai--Kokoro/blob")
                info.size = len(data)
                tar.addfile(info, io.BytesIO(data))
            return 0
        if "/bundle/audio.tar.gz" in joined:
            return 0 if os.path.isfile(os.path.join(mount(args, "/bundle") or "", "audio.tar.gz")) else 1
        return 0
    return 0


if __name__ == "__main__":
    sys.exit(main())
