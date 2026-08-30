"""Normalise line endings for files that must be LF to work on Linux.

Shell scripts with CRLF fail on Linux with "$'\r': command not found" because
the carriage return becomes part of the command. The same applies to anything
executed or parsed inside a container.
"""
import pathlib, glob

CRLF = b"\r\n"
LF = b"\n"

patterns = [
    "scripts/*.sh", "scripts/*.py",
    "config/**/*.yml", "config/**/*.yaml", "config/**/*.json",
    "config/**/*.sql", "config/**/*.htpasswd",
    "docker-compose.yml", "Makefile", ".env", ".env.example",
    ".gitattributes", ".gitignore",
]

changed, checked = [], 0
for pat in patterns:
    for f in glob.glob(pat, recursive=True):
        p = pathlib.Path(f)
        if not p.is_file():
            continue
        checked += 1
        raw = p.read_bytes()
        if CRLF in raw:
            p.write_bytes(raw.replace(CRLF, LF))
            changed.append(f)

print("  files checked: %d" % checked)
print("  converted to LF: %d" % len(changed))
for c in sorted(changed):
    print("    " + c)
