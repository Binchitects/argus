"""The Python sandbox's runner: takes jobs the app leaves in /jobs, runs each in
isolation, and leaves the results beside them.

The container has no network at all (network_mode: none), a read-only root,
and only the capabilities needed to switch users. Each job:

  - runs as its own unprivileged user (one per slot: 20000 + slot), in a fresh
    directory on tmpfs that only that user can read;
  - gets the chat's files there, by name, and its code on stdin (never on the
    command line, which other users could read in /proc);
  - is limited in CPU time, memory, file size, open files and processes, and
    killed at its time limit, or when the app asks (the person pressed stop);
  - leaves nothing behind: every process of its user is killed afterwards and
    its directory wiped. Files it wrote (not the ones it was given) go back.

The protocol, all in /jobs (owned by the app's user, 0700):

  in/<id>/job.json    {"code": str, "timeout": seconds, "files": [names]}
  in/<id>/files/...   the chat's files
  cancel/<id>         stop that job
  out/<id>/result.json, out/<id>/files/...
  heartbeat           {"at": epoch seconds, "slots": n, "busy": n}; the app
                      treats the sandbox as down when it is stale

A job directory is written as in/.tmp-<id> and renamed, so it is whole when
seen; results the same way.
"""

import json
import os
import queue
import shutil
import signal
import subprocess
import sys
import threading
import time

JOBS = "/jobs"
IN, RUN, OUT, CANCEL = (os.path.join(JOBS, d) for d in ("in", "run", "out", "cancel"))
WORK = "/work"
PRIVATE = "/tmp/runner"

SLOTS = max(1, int(os.environ.get("SANDBOX_SLOTS", "2")))
OWNER_UID = int(os.environ.get("SANDBOX_OWNER_UID", "1000"))
OWNER_GID = int(os.environ.get("SANDBOX_OWNER_GID", str(OWNER_UID)))
MAX_TIMEOUT = int(os.environ.get("SANDBOX_MAX_TIMEOUT", "300"))
MEMORY_MB = int(os.environ.get("SANDBOX_JOB_MEMORY_MB", "2048"))
MAX_FILE_MB = 64
MAX_OUT_FILES, MAX_OUT_FILE_MB, MAX_OUT_TOTAL_MB = 20, 16, 32
MAX_STREAM = 64 * 1024
FIRST_UID = 20000

# Runs in the child, as the job's user: the limits first (hard ones too, so the
# code cannot raise them), then the code from stdin under the name main.py.
PRELUDE = r"""
import os, resource, sys
cpu, mem, fsize = (int(v) for v in sys.argv[1:4])
for what, value in ((resource.RLIMIT_CPU, cpu), (resource.RLIMIT_AS, mem), (resource.RLIMIT_FSIZE, fsize),
                    (resource.RLIMIT_NOFILE, 256), (resource.RLIMIT_NPROC, 64), (resource.RLIMIT_CORE, 0)):
    resource.setrlimit(what, (value, value))
try:
    with open("/proc/self/oom_score_adj", "w") as f:
        f.write("1000")  # when memory runs out, this dies, not the runner
except OSError:
    pass
del cpu, mem, fsize, what, value
import warnings
warnings.filterwarnings("ignore", message=".*non-interactive.*")
_before = set(os.listdir("."))
_code = sys.stdin.read()
sys.argv = ["main.py"]
_status = 0
try:
    exec(compile(_code, "main.py", "exec"), {"__name__": "__main__", "__file__": "main.py", "__builtins__": __builtins__})
except SystemExit as e:
    _status = e.code if isinstance(e.code, int) else (0 if e.code is None else 1)
    if not isinstance(e.code, (int, type(None))):
        print(e.code, file=sys.stderr)
except BaseException:
    import traceback
    _t, _v, _tb = sys.exc_info()
    traceback.print_exception(_t, _v, _tb.tb_next)
    _status = 1
# Charts drawn and not saved are saved, so plt.show() works as people expect.
_plt = sys.modules.get("matplotlib.pyplot")
if _plt is not None and _plt.get_fignums():
    _made = [f for f in set(os.listdir(".")) - _before if f.lower().endswith((".png", ".jpg", ".jpeg", ".svg", ".pdf"))]
    if not _made:
        for _i, _n in enumerate(_plt.get_fignums(), 1):
            _plt.figure(_n).savefig(f"figure-{_i}.png", dpi=120, bbox_inches="tight")
sys.stdout.flush()
sys.stderr.flush()
os._exit(_status)
"""


def log(*parts):
    print(time.strftime("%H:%M:%S"), *parts, flush=True)


def owned(path, mode):
    os.chmod(path, mode)
    os.chown(path, OWNER_UID, OWNER_GID)


def prepare():
    os.makedirs(PRIVATE, mode=0o700, exist_ok=True)
    os.makedirs(WORK, exist_ok=True)
    os.chmod(WORK, 0o711)
    owned(JOBS, 0o700)
    for d in (IN, OUT, CANCEL):
        os.makedirs(d, exist_ok=True)
        owned(d, 0o700)
    os.makedirs(RUN, exist_ok=True)
    os.chmod(RUN, 0o700)
    # A job that was running when the sandbox stopped is not run again.
    for stale in os.listdir(RUN):
        shutil.rmtree(os.path.join(RUN, stale), ignore_errors=True)


def safe_name(name):
    name = os.path.basename(name.replace("\\", "/")).strip()
    return name if name and not name.startswith(".") else None


def kill_user(uid):
    """Every process of this user, whatever it did to escape its session."""
    subprocess.run([sys.executable, "-I", "-c", "import os, signal\ntry: os.kill(-1, signal.SIGKILL)\nexcept ProcessLookupError: pass"],
                   user=uid, group=uid, extra_groups=[], env={}, cwd="/", check=False, timeout=10)


def read_stream(path):
    size = os.path.getsize(path)
    with open(path, "rb") as f:
        head = f.read(MAX_STREAM)
    text = head.decode("utf-8", "replace")
    return text, size > MAX_STREAM


def run_job(slot, job_id):
    src = os.path.join(RUN, job_id)
    uid = FIRST_UID + slot
    work = os.path.join(WORK, str(slot))
    tmp_out = os.path.join(OUT, f".tmp-{job_id}")
    result = {"exit_code": None, "timed_out": False, "cancelled": False, "stdout": "", "stderr": "", "files": [], "skipped_files": []}
    started = time.monotonic()
    try:
        with open(os.path.join(src, "job.json"), encoding="utf-8") as f:
            job = json.load(f)
        timeout = max(1, min(int(job.get("timeout", 60)), MAX_TIMEOUT))
        shutil.rmtree(work, ignore_errors=True)
        os.mkdir(work, 0o700)
        os.makedirs(os.path.join(work, ".tmp"), 0o700)
        given = {}
        for name in job.get("files", []):
            safe = safe_name(name)
            if safe is None or not os.path.isfile(os.path.join(src, "files", name)):
                continue
            target = os.path.join(work, safe)
            shutil.copyfile(os.path.join(src, "files", name), target)
            given[safe] = os.path.getsize(target), os.path.getmtime(target)
        for root, dirs, files in os.walk(work):
            for p in [root] + [os.path.join(root, x) for x in dirs + files]:
                os.chown(p, uid, uid)

        env = {
            "PATH": "/usr/local/bin:/usr/bin:/bin", "HOME": work, "TMPDIR": os.path.join(work, ".tmp"), "LANG": "C.UTF-8",
            "MPLBACKEND": "Agg", "MPLCONFIGDIR": "/opt/mplconfig",
            "OPENBLAS_NUM_THREADS": "1", "OMP_NUM_THREADS": "1", "MKL_NUM_THREADS": "1", "NUMEXPR_NUM_THREADS": "1",
        }
        out_path, err_path = os.path.join(PRIVATE, f"{slot}.out"), os.path.join(PRIVATE, f"{slot}.err")
        # The streams are files in the runner's own directory, handed to the child open.
        with open(out_path, "wb") as out, open(err_path, "wb") as err:
            proc = subprocess.Popen(
                [sys.executable, "-I", "-c", PRELUDE, str(timeout + 1), str(MEMORY_MB * 1024 * 1024), str(MAX_FILE_MB * 1024 * 1024)],
                cwd=work, env=env, stdin=subprocess.PIPE, stdout=out, stderr=err,
                user=uid, group=uid, extra_groups=[], start_new_session=True, close_fds=True)
            try:
                proc.stdin.write(job.get("code", "").encode("utf-8"))
                proc.stdin.close()
            except BrokenPipeError:
                pass
            deadline = started + timeout
            while proc.poll() is None:
                if os.path.exists(os.path.join(CANCEL, job_id)):
                    result["cancelled"] = True
                    break
                if time.monotonic() > deadline:
                    result["timed_out"] = True
                    break
                time.sleep(0.05)
            if proc.poll() is None:
                os.killpg(proc.pid, signal.SIGKILL)
            code = proc.wait()
        kill_user(uid)
        result["exit_code"] = code
        if code == -signal.SIGKILL and not result["timed_out"] and not result["cancelled"]:
            result["killed"] = "The code was killed: most likely it ran out of memory."
        elif code == -signal.SIGXCPU:
            result["timed_out"] = True
        elif code == -signal.SIGXFSZ:
            result["killed"] = f"The code wrote a file larger than {MAX_FILE_MB} MB."
        result["stdout"], result["stdout_cut"] = read_stream(out_path)
        result["stderr"], result["stderr_cut"] = read_stream(err_path)

        # What it wrote: new or changed files, not hidden ones, within limits.
        os.makedirs(os.path.join(tmp_out, "files"))
        total = 0
        for root, dirs, files in os.walk(work):
            dirs[:] = sorted(d for d in dirs if not d.startswith("."))
            for name in sorted(files):
                if name.startswith("."):
                    continue
                path = os.path.join(root, name)
                if os.path.islink(path) or not os.path.isfile(path):
                    continue
                rel = os.path.relpath(path, work)
                size = os.path.getsize(path)
                if rel in given and given[rel] == (size, os.path.getmtime(path)):
                    continue
                shown = rel.replace(os.sep, "_")
                if len(result["files"]) >= MAX_OUT_FILES or size > MAX_OUT_FILE_MB * 1024 * 1024 or total + size > MAX_OUT_TOTAL_MB * 1024 * 1024:
                    result["skipped_files"].append(shown)
                    continue
                shutil.copyfile(path, os.path.join(tmp_out, "files", shown))
                result["files"].append(shown)
                total += size
    except Exception as ex:  # the job failed to run, not the code in it
        result["error"] = f"The sandbox could not run this: {type(ex).__name__}: {ex}"
        log("job", job_id, "failed:", repr(ex))
    finally:
        try:
            kill_user(uid)
        except Exception as ex:
            log("could not clear user", uid, repr(ex))
        shutil.rmtree(work, ignore_errors=True)
        shutil.rmtree(src, ignore_errors=True)
        cancel = os.path.join(CANCEL, job_id)
        if os.path.exists(cancel):
            os.remove(cancel)
    result["duration_ms"] = int((time.monotonic() - started) * 1000)
    os.makedirs(os.path.join(tmp_out, "files"), exist_ok=True)
    with open(os.path.join(tmp_out, "result.json"), "w", encoding="utf-8") as f:
        json.dump(result, f)
    for root, dirs, files in os.walk(tmp_out):
        for p in [root] + [os.path.join(root, x) for x in dirs + files]:
            os.chown(p, OWNER_UID, OWNER_GID)
    os.rename(tmp_out, os.path.join(OUT, job_id))
    log("job", job_id, "slot", slot, "exit", result["exit_code"], "timed out" if result["timed_out"] else "", f"{result['duration_ms']} ms")


busy = 0
busy_lock = threading.Lock()


def worker(slot, jobs, free):
    global busy
    while True:
        job_id = jobs.get()
        try:
            run_job(slot, job_id)
        finally:
            with busy_lock:
                busy -= 1
            free.put((slot, jobs))


def heartbeat():
    while True:
        try:
            tmp = os.path.join(JOBS, ".heartbeat")
            with open(tmp, "w", encoding="utf-8") as f:
                json.dump({"at": time.time(), "slots": SLOTS, "busy": busy}, f)
            os.chown(tmp, OWNER_UID, OWNER_GID)
            os.replace(tmp, os.path.join(JOBS, "heartbeat"))
            # Leftovers of an app that went away: results never collected, jobs half written.
            now = time.time()
            for d in (IN, OUT, CANCEL):
                for name in os.listdir(d):
                    path = os.path.join(d, name)
                    if now - os.path.getmtime(path) > 900:
                        shutil.rmtree(path, ignore_errors=True) if os.path.isdir(path) else os.remove(path)
        except Exception as ex:
            log("heartbeat:", repr(ex))
        time.sleep(2)


def main():
    global busy
    prepare()
    slots = queue.Queue()
    for slot in range(SLOTS):
        jobs = queue.Queue()
        threading.Thread(target=worker, args=(slot, jobs, slots), daemon=True).start()
        slots.put((slot, jobs))
    threading.Thread(target=heartbeat, daemon=True).start()
    log(f"sandbox: {SLOTS} slots, {MEMORY_MB} MB and at most {MAX_TIMEOUT} s a job, no network")
    free = {}
    while True:
        # A slot whose job ended is free again.
        while not slots.empty():
            slot, jobs = slots.get()
            free[slot] = jobs
        waiting = sorted(n for n in os.listdir(IN) if not n.startswith("."))
        for job_id in waiting:
            if not free:
                break
            try:
                os.rename(os.path.join(IN, job_id), os.path.join(RUN, job_id))
            except OSError:
                continue
            slot = min(free)
            jobs = free.pop(slot)
            with busy_lock:
                busy += 1
            jobs.put(job_id)
        time.sleep(0.1)


if __name__ == "__main__":
    signal.signal(signal.SIGTERM, lambda *_: sys.exit(0))
    main()
