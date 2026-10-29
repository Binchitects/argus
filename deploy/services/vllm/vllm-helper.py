#!/usr/bin/env python3
"""
The JSON and HTTP side of supervisor.sh (vLLM's and SGLang's images have python3; the standard library only). Nothing here starts or
stops a process: the supervisor does, by what this writes.

    vllm-helper.py plan MODELS_JSON WORKDIR
        The app's desired state, {"models": [{"name", "port", "gpus": [0, 1], "sleep": false, "args": ["/library/...", ...]}]},
        as one folder a model under WORKDIR/want: name, port, gpus ("0,1"), sleep (true/false), args (one a line) and hash
        (of all of them). A model whose entry is not sound (a name that is not one, a port out of range, a key on its command
        line) is left out and said on stderr. Prints the names wanted, one a line.

    vllm-helper.py health PORT        exit 0 when http://127.0.0.1:PORT/health answers 200
    vllm-helper.py post PORT PATH     POST {} to the server (vLLM's /sleep?level=1 and /wake_up, SGLang's
                                      /release_memory_occupation and /resume_memory_occupation); exit 0 on 200
    vllm-helper.py sleeping PORT      exit 0 when /is_sleeping says it is

    vllm-helper.py status FILE KEY=VALUE... [--log LOGFILE]
        Writes FILE atomically as JSON: each KEY=VALUE (numbers, true/false and null as JSON, gpus as a list), with the last
        lines of LOGFILE.
"""
from __future__ import annotations

import hashlib
import json
import os
import re
import sys
import urllib.error
import urllib.request

NAME = re.compile(r"^[A-Za-z0-9][A-Za-z0-9._:-]{0,99}$")
# Options that would put a secret on the command line (ps shows it to anyone in the container), or move what the supervisor owns.
FORBIDDEN = {"--api-key", "--host", "--port", "--served-model-name", "--ssl-keyfile", "--ssl-certfile"}
LOG_LINES = 20


def plan(models_json: str, workdir: str) -> int:
    try:
        with open(models_json) as f:
            wanted = json.load(f).get("models", [])
    except (OSError, json.JSONDecodeError, AttributeError) as e:
        print(f"vllm: cannot read {models_json}: {e}", file=sys.stderr)
        return 1
    root = os.path.join(workdir, "want")
    os.makedirs(root, exist_ok=True)
    names = []
    for m in wanted if isinstance(wanted, list) else []:
        name = str(m.get("name", ""))
        port = m.get("port")
        args = m.get("args") or []
        gpus = m.get("gpus") or []
        why = None
        if not NAME.match(name):
            why = "not a model name"
        elif not isinstance(port, int) or not 1024 <= port <= 65535:
            why = "its port is not 1024-65535"
        elif not isinstance(args, list) or not all(isinstance(a, str) and "\n" not in a for a in args):
            why = "its arguments are not a list of single-line strings"
        elif any(a.split("=", 1)[0] in FORBIDDEN for a in args):
            why = "its arguments set what the supervisor owns (the key goes by the environment, never the command line)"
        elif not isinstance(gpus, list) or not all(isinstance(g, int) and g >= 0 for g in gpus):
            why = "its GPUs are not a list of indices"
        elif name in names:
            why = "it is there twice"
        if why:
            print(f"vllm: {name or '?'} left out: {why}", file=sys.stderr)
            continue
        names.append(name)
        d = os.path.join(root, name)
        os.makedirs(d, exist_ok=True)
        fields = {"port": str(port), "gpus": ",".join(str(g) for g in gpus), "sleep": "true" if m.get("sleep") else "false",
                  "args": "".join(a + "\n" for a in args)}
        # The sleep switch is no reason to start it again: it is not in the hash.
        fields["hash"] = hashlib.sha256(json.dumps([port, gpus, args, m.get("env") or {}]).encode()).hexdigest()[:16]
        env = m.get("env") or {}
        fields["env"] = "".join(f"{k}={v}\n" for k, v in sorted(env.items())
                                if re.match(r"^[A-Z][A-Z0-9_]*$", str(k)) and "\n" not in str(v) and "KEY" not in str(k) and "TOKEN" not in str(k))
        for k, v in fields.items():
            with open(os.path.join(d, k + ".tmp"), "w") as f:
                f.write(v)
            os.replace(os.path.join(d, k + ".tmp"), os.path.join(d, k))
    for gone in os.listdir(root):
        if gone not in names:
            for f in os.listdir(os.path.join(root, gone)):
                os.remove(os.path.join(root, gone, f))
            os.rmdir(os.path.join(root, gone))
    for n in names:
        print(n)
    return 0


def request(port: str, path: str, method: str = "GET") -> tuple[int, bytes]:
    # An empty JSON object: SGLang's endpoints read one, vLLM's ignore it.
    req = urllib.request.Request(f"http://127.0.0.1:{port}{path}", method=method, data=b"{}" if method == "POST" else None)
    if method == "POST":
        req.add_header("Content-Type", "application/json")
    key = os.environ.get("VLLM_API_KEY")
    if key:
        req.add_header("Authorization", f"Bearer {key}")
    try:
        with urllib.request.urlopen(req, timeout=5) as r:
            return r.status, r.read()
    except urllib.error.HTTPError as e:
        with e:
            return e.code, e.read()
    except OSError:
        return 0, b""


def status(path: str, pairs: list[str]) -> int:
    log = None
    if "--log" in pairs:
        i = pairs.index("--log")
        log = pairs[i + 1] if i + 1 < len(pairs) else None
        pairs = pairs[:i] + pairs[i + 2:]
    out: dict = {}
    for p in pairs:
        k, _, v = p.partition("=")
        if v == "" and k not in ("name", "phase"):
            out[k] = None
        elif k == "gpus":
            out[k] = [int(g) for g in v.split(",") if g.strip().isdigit()]
        elif v in ("true", "false", "null") or re.fullmatch(r"-?\d+", v):
            out[k] = json.loads(v)
        else:
            out[k] = v
    lines: list[str] = []
    if log and os.path.exists(log):
        with open(log, errors="replace") as f:
            lines = [l.rstrip("\n") for l in f.readlines()[-LOG_LINES:]]
    out["log"] = lines
    with open(path + ".tmp", "w") as f:
        json.dump(out, f)
    os.replace(path + ".tmp", path)
    return 0


def main(argv: list[str]) -> int:
    if len(argv) >= 3 and argv[0] == "plan":
        return plan(argv[1], argv[2])
    if len(argv) == 2 and argv[0] == "health":
        return 0 if request(argv[1], "/health")[0] == 200 else 1
    if len(argv) == 3 and argv[0] == "post":
        return 0 if request(argv[1], argv[2], "POST")[0] == 200 else 1
    if len(argv) == 2 and argv[0] == "sleeping":
        code, body = request(argv[1], "/is_sleeping")
        try:
            return 0 if code == 200 and json.loads(body or b"{}").get("is_sleeping") else 1
        except json.JSONDecodeError:
            return 1
    if len(argv) >= 2 and argv[0] == "status":
        return status(argv[1], argv[2:])
    print(__doc__, file=sys.stderr)
    return 2


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
