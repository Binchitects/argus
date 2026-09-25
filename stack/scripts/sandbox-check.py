#!/usr/bin/env python3
"""Escape and limit tests for the Python sandbox (profile sandbox), against the
running container: jobs go through the sandbox-jobs volume exactly as the app
sends them, as the app's user.

    python3 scripts/sandbox-check.py

What must hold: code runs and its files come back; it has no network; it
cannot read the jobs, other runs, or anything of the runner's, nor write the
image; time, memory, file size and process limits bind; nothing it starts
outlives the run; and a stop request stops it. Exit status: failures.
"""

import json
import os
import subprocess
import sys
import textwrap
import time
import uuid
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent


def env(key, default=""):
    for line in (ROOT / ".env").read_text(encoding="utf-8").splitlines():
        if line.startswith(key + "="):
            return line.split("=", 1)[1].strip()
    return default


PROJECT = env("COMPOSE_PROJECT_NAME", "llmservice")
UID, GID = env("LLM_UID", "1000"), env("LLM_GID", "1000")
VOLUME = f"{PROJECT}_sandbox-jobs"
RESULTS = []
LAST = {}  # the last run's result: a run that failed to happen fails its checks

# One helper container per check plays the app: it writes the job, waits, prints the result.
CLIENT = textwrap.dedent(r"""
    import json, os, sys, time
    job = json.loads(sys.stdin.read())
    jid, files = job.pop("id"), job.pop("inputs", {})
    draft = f"/sandbox/in/.tmp-{jid}"
    os.makedirs(draft + "/files")
    for name, text in files.items():
        open(f"{draft}/files/{name}", "w").write(text)
    job["files"] = list(files)
    json.dump(job, open(draft + "/job.json", "w"))
    os.rename(draft, f"/sandbox/in/{jid}")
    if job.pop("cancel_after", None):
        time.sleep(2)
        open(f"/sandbox/cancel/{jid}", "w").close()
    out = f"/sandbox/out/{jid}"
    for _ in range(3000):
        if os.path.isdir(out):
            break
        time.sleep(0.1)
    else:
        print(json.dumps({"error": "no result"})); sys.exit()
    result = json.load(open(out + "/result.json"))
    result["made"] = {n: open(f"{out}/files/{n}", "rb").read()[:64].hex() for n in sorted(os.listdir(out + "/files"))}
    import shutil; shutil.rmtree(out)
    print(json.dumps(result))
""")


def run(code, timeout=20, inputs=None, cancel_after=False):
    job = {"id": uuid.uuid4().hex, "code": textwrap.dedent(code), "timeout": timeout, "inputs": inputs or {}, "cancel_after": cancel_after}
    # The sandbox's own image, with python itself as the entrypoint: the client, not the runner.
    p = subprocess.run(["docker", "run", "--rm", "-i", "--network", "none", "--user", f"{UID}:{GID}", "-v", f"{VOLUME}:/sandbox",
                        "--entrypoint", "python", "llmservice-sandbox:latest", "-c", CLIENT], input=json.dumps(job), capture_output=True, text=True,
                       timeout=600, check=False)
    try:
        result = json.loads(p.stdout.strip().splitlines()[-1])
    except (IndexError, json.JSONDecodeError):
        result = {"error": f"client failed: {p.stderr.strip()[-300:]}"}
    LAST.clear()
    LAST.update(result)
    return result


def rec(name, ok, detail=""):
    ok = bool(ok) and not LAST.get("error")
    RESULTS.append((name, ok))
    print(f"  {'PASS' if ok else 'FAIL'}  {name}" + (f"  ({detail})" if detail else ""), flush=True)


def processes_of(uid):
    """Every process of the uid on the host, zombies too: what the kernel's process limit counts."""
    out = subprocess.run(["ps", "-eo", "uid=,pid=,stat=,comm="], capture_output=True, text=True, check=False).stdout
    return [line for line in out.splitlines() if line.split() and line.split()[0] == str(uid)]


def main():
    if subprocess.run(["docker", "inspect", "sandbox"], capture_output=True, check=False).returncode != 0:
        print("sandbox-check: the sandbox is not running (profile sandbox)")
        return 1
    print("Sandbox checks")

    r = run("""
        import pandas as pd, matplotlib.pyplot as plt
        df = pd.read_csv("sales.csv")
        print(df["amount"].sum())
        df.plot(x="month", y="amount"); plt.show()
        df.describe().to_csv("summary.csv")
    """, inputs={"sales.csv": "month,amount\n1,10\n2,32\n3,0.5\n"})
    rec("code runs with its files and pandas", r.get("exit_code") == 0 and r.get("stdout", "").strip() == "42.5", r.get("stderr", "")[-200:])
    rec("a chart shown is kept as a picture, a file written comes back", set(r.get("made", {})) == {"figure-1.png", "summary.csv"}
        and r["made"]["figure-1.png"].startswith("89504e47"), str(list(r.get("made", {}))))
    rec("the files it was given do not come back", "sales.csv" not in r.get("made", {}))

    r = run("""
        import socket
        for host in [("1.1.1.1", 53), ("app", 8080), ("172.17.0.1", 80)]:
            try:
                socket.create_connection(host, timeout=3); print("CONNECTED", host)
            except OSError as e:
                print("no", type(e).__name__)
        print(sorted(n for _, n in socket.if_nameindex()))
    """)
    rec("no network: nothing can be reached, only loopback exists", "CONNECTED" not in r.get("stdout", "") and "['lo']" in r.get("stdout", ""), r.get("stdout", "").strip().replace("\n", " "))

    r = run("""
        import os
        print("uid", os.getuid(), os.getgid(), os.getgroups())
        for path in ["/jobs", "/jobs/in", "/work/1", "/tmp/runner", "/proc/1/environ", "/etc/shadow"]:
            try:
                os.listdir(path) if os.path.isdir(path) else open(path, "rb").read(1)
                print("READ", path)
            except OSError as e:
                print("no", path, type(e).__name__)
        for path in ["/usr/local/lib/python3.13/os.py", "/runner.py", "/new-file"]:
            try:
                open(path, "a").write("x"); print("WROTE", path)
            except OSError as e:
                print("no", path, type(e).__name__)
    """)
    out = r.get("stdout", "")
    rec("runs as an unprivileged user of its own, in no group", "uid 20000 20000 []" in out or "uid 20001 20001 []" in out, out.splitlines()[0] if out else r.get("error", ""))
    rec("cannot read the jobs, other runs, the runner or secrets", "READ" not in out, " ".join(l for l in out.splitlines() if l.startswith("READ")))
    rec("cannot change the image or the runner", "WROTE" not in out, " ".join(l for l in out.splitlines() if l.startswith("WROTE")))

    r = run("while True: pass", timeout=3)
    rec("a run past its time is stopped", r.get("timed_out") is True and r.get("duration_ms", 99999) < 8000, f"{r.get('duration_ms')} ms")

    r = run("import time\nwhile True: time.sleep(1)", timeout=3)
    rec("a run that sleeps past its time is stopped too", r.get("timed_out") is True and r.get("duration_ms", 99999) < 8000, f"{r.get('duration_ms')} ms")

    r = run("x = bytearray(4 * 1024 ** 3)\nprint('ALLOCATED')")
    rec("memory is limited", "ALLOCATED" not in r.get("stdout", "") and ("MemoryError" in r.get("stderr", "") or r.get("killed")), (r.get("stderr", "") or r.get("killed") or "")[-120:])

    r = run("open('big.bin', 'wb').write(b'0' * (100 * 1024 ** 2))\nprint('WROTE')")
    rec("a file is limited in size", "WROTE" not in r.get("stdout", ""), (r.get("killed") or r.get("stderr", ""))[-120:])

    r = run("""
        import os
        n = 0
        try:
            while True:
                if os.fork() == 0:
                    import time; time.sleep(30); os._exit(0)
                n += 1
        except OSError as e:
            print("stopped at", n, type(e).__name__)
    """, timeout=10)
    rec("processes are limited (a fork bomb stops itself)", "stopped at" in r.get("stdout", ""), r.get("stdout", "").strip())
    time.sleep(1)
    left = processes_of(20000) + processes_of(20001)
    rec("and none of its processes is left, not even as a zombie", not left, f"{len(left)} left")

    r = run("""
        import subprocess
        subprocess.Popen(["sleep", "600"], start_new_session=True)
        subprocess.Popen(["python", "-c", "import os, time\\nif os.fork(): os._exit(0)\\nos.setsid(); time.sleep(600)"])
        print("left behind")
    """)
    time.sleep(1)
    left = processes_of(20000) + processes_of(20001)
    rec("nothing it starts outlives the run", r.get("exit_code") == 0 and not left, f"{len(left)} left")

    r = run("print('x' * 10_000_000)")
    rec("huge output is cut, not fatal", r.get("stdout_cut") is True and len(r.get("stdout", "")) <= 70_000)

    r = run("import time\nfor i in range(100): print(i, flush=True); time.sleep(1)", timeout=60, cancel_after=True)
    rec("a stop request stops the run", r.get("cancelled") is True and r.get("duration_ms", 99999) < 10000, f"{r.get('duration_ms')} ms")

    r = run("raise ValueError('bad input')")
    rec("an error comes back as its traceback, from main.py", r.get("exit_code") == 1 and 'File "main.py", line 1' in r.get("stderr", "") and "ValueError: bad input" in r.get("stderr", ""))

    failed = [n for n, ok in RESULTS if not ok]
    print(f"\n{len(RESULTS) - len(failed)}/{len(RESULTS)} checks passed")
    return len(failed)


if __name__ == "__main__":
    sys.exit(main())
