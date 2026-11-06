#!/usr/bin/env python3
"""A published Code Arena carries everything it needs: checked in the file itself, after tools/publish-code-arena.sh.

  tools/code-arena-check.py dist/code-arena/*/code-arena*            each file; 1 when one falls short
  tools/code-arena-check.py --no-web FILE...                         built without the IDE's page on purpose

For each program: a .NET single-file bundle, self-contained (the runtime inside, so nothing is installed first), with no
native library to unpack at start (that would need a writable temporary folder, which locked-down machines forbid), the
IDE's page embedded (code-arena.html and browser-check.js), and on Windows a console program, so that a terminal waits for
it and it talks there (started on its own, it hides its console itself: src/CodeArena/DesktopStart.cs).
"""
from __future__ import annotations

import json
import struct
import sys
import zlib
from pathlib import Path

# The SHA-256 of ".net core bundle": the host keeps the bundle's header offset in the 8 bytes before it.
SIGNATURE = bytes.fromhex("8b1202b96a612038727b930214d7a03213f5b9e6efae3318ee3b2dce24b36aae")
KINDS = {0: "unknown", 1: "assembly", 2: "native", 3: "deps", 4: "runtimeconfig", 5: "symbols"}
PAGE = (b"web/code-arena.html", b"web/browser-check.js")


def _string(data: bytes, at: int) -> tuple[str, int]:
    """A string as .NET's BinaryWriter writes it: its length in 7-bit groups, then UTF-8."""
    size = shift = 0
    while True:
        b = data[at]
        at += 1
        size |= (b & 0x7F) << shift
        shift += 7
        if b < 0x80:
            return data[at:at + size].decode(), at + size


def entries(data: bytes) -> list[dict]:
    """The bundle's files (path, kind, bytes as stored, compressed or not); ValueError when it is no bundle."""
    found = data.find(SIGNATURE)
    if found < 8:
        raise ValueError("not a .NET single-file bundle")
    at = struct.unpack_from("<q", data, found - 8)[0]
    if not 0 < at < len(data):
        raise ValueError("not a .NET single-file bundle (no header)")
    major, _minor, count = struct.unpack_from("<IIi", data, at)
    if major < 6:
        raise ValueError(f"bundle version {major}: older than .NET 6's")
    _, at = _string(data, at + 12)
    at += 8 * 4 + 8  # where deps.json and runtimeconfig.json are, and the flags
    files = []
    for _ in range(count):
        offset, size, compressed = struct.unpack_from("<qqq", data, at)
        kind = data[at + 24]
        path, at = _string(data, at + 25)
        stored = data[offset:offset + (compressed or size)]
        files.append({"path": path, "kind": KINDS.get(kind, str(kind)), "data": zlib.decompress(stored, -15) if compressed else stored})
    return files


def pe_subsystem(data: bytes) -> int | None:
    """A Windows program's subsystem (2: a window, 3: the console); None for a file that is no PE."""
    if data[:2] != b"MZ":
        return None
    pe = struct.unpack_from("<I", data, 0x3C)[0]
    return struct.unpack_from("<H", data, pe + 24 + 68)[0] if data[pe:pe + 4] == b"PE\0\0" else None


def check(path: Path, web: bool = True) -> list[str]:
    """What this program lacks; empty when it carries everything."""
    data = path.read_bytes()
    try:
        files = entries(data)
    except (ValueError, struct.error, IndexError, zlib.error) as e:
        return [f"{e}: published without PublishSingleFile"]
    problems = []
    config = next((f for f in files if f["kind"] == "runtimeconfig"), None)
    options = json.loads(config["data"]).get("runtimeOptions", {}) if config else {}
    if "includedFrameworks" not in options or "framework" in options or "frameworks" in options:
        problems.append("not self-contained: it asks for a .NET runtime installed on the machine")
    natives = [f["path"] for f in files if f["kind"] == "native"]
    if natives:
        problems.append(f"native libraries unpacked at start (a writable temporary folder needed): {', '.join(natives)}")
    if web:
        program = b"".join(f["data"] for f in files if f["kind"] == "assembly" and f["path"] == "code-arena.dll")
        missing = [p.decode() for p in PAGE if p not in program]
        if missing:
            problems.append(f"the IDE's page is not embedded ({', '.join(missing)}): built with --no-web, or without Node.js")
    if path.suffix == ".exe" and pe_subsystem(data) != 3:
        problems.append("not a console program: a terminal would not wait for it, nor see what it prints")
    return problems


def sample(windows: bool, web: bool = True, self_contained: bool = True, natives: tuple[str, ...] = ()) -> bytes:
    """The smallest program these checks read as published: for the tests' fake dotnet."""
    def text(s: str) -> bytes:
        raw, size, out = s.encode(), len(s.encode()), bytearray()
        while True:
            out.append((size & 0x7F) | (0x80 if size > 0x7F else 0))
            size >>= 7
            if not size:
                return bytes(out) + raw
    options = {"tfm": "net10.0"} | ({"includedFrameworks": [{"name": "Microsoft.NETCore.App", "version": "10.0.0"}]} if self_contained
                                    else {"framework": {"name": "Microsoft.NETCore.App", "version": "10.0.0"}})
    files = [("code-arena.dll", 1, b"managed " + (b" ".join(PAGE) if web else b"")),
             ("code-arena.runtimeconfig.json", 4, json.dumps({"runtimeOptions": options}).encode()),
             *((n, 2, b"native") for n in natives)]
    host = bytearray((b"MZ" if windows else b"\x7fELF") + bytes(0x40 - (2 if windows else 4)))
    if windows:
        struct.pack_into("<I", host, 0x3C, 0x40)
        host += b"PE\0\0" + bytes(20 + 68) + struct.pack("<H", 3) + bytes(16)
    marker = len(host)
    host += bytes(8) + SIGNATURE
    blobs = bytearray()
    placed = []
    for path, kind, data in files:
        placed.append((len(host) + len(blobs), len(data), kind, path))
        blobs += data
    header_at = len(host) + len(blobs)
    header = struct.pack("<IIi", 6, 0, len(files)) + text("sample") + bytes(8 * 4 + 8)
    for offset, size, kind, path in placed:
        header += struct.pack("<qqq", offset, size, 0) + bytes([kind]) + text(path)
    struct.pack_into("<q", host, marker, header_at)
    return bytes(host + blobs + header)


def main(argv: list[str]) -> int:
    web = "--no-web" not in argv
    paths = [Path(a) for a in argv if a != "--no-web"]
    if not paths:
        print(__doc__.strip(), file=sys.stderr)
        return 2
    failed = 0
    for path in paths:
        problems = check(path, web)
        print(f"{path}: {'carries everything it needs' if not problems else '; '.join(problems)}")
        failed |= bool(problems)
    return 1 if failed else 0


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
