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
import pathlib
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


PROJECT = os.environ.get("COMPOSE_PROJECT_NAME", "arena")
UID, GID = env("LLM_UID", "1000"), env("LLM_GID", "1000")
VOLUME = f"{PROJECT}_sandbox"
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
    calls, answered = f"/sandbox/calls/{jid}", 0
    for _ in range(3000):
        if os.path.isdir(out):
            break
        # A program's calls of the chat's tools (run_code): answered as the app would, with what was asked.
        while os.path.exists(f"{calls}/{answered + 1}.json"):
            answered += 1
            asked = json.load(open(f"{calls}/{answered}.json"))
            json.dump({"ok": True, "result": json.dumps({"called": asked["name"], "with": asked["args"]})}, open(f"{calls}/.tmp-{answered}", "w"))
            os.rename(f"{calls}/.tmp-{answered}", f"{calls}/{answered}.out.json")
        time.sleep(0.05 if job.get("calls") else 0.1)
    else:
        print(json.dumps({"error": "no result"})); sys.exit()
    result = json.load(open(out + "/result.json"))
    result["made"] = {n: open(f"{out}/files/{n}", "rb").read()[:64].hex() for n in sorted(os.listdir(out + "/files"))}
    import shutil; shutil.rmtree(out)
    print(json.dumps(result))
""")


def run(code, timeout=20, inputs=None, cancel_after=False, calls=False):
    job = {"id": uuid.uuid4().hex, "code": textwrap.dedent(code), "timeout": timeout, "inputs": inputs or {}, "cancel_after": cancel_after}
    if calls:
        job["calls"] = True
    # The sandbox's own image, with python itself as the entrypoint: the client, not the runner.
    p = subprocess.run(["docker", "run", "--rm", "-i", "--network", "none", "--user", f"{UID}:{GID}", "-v", f"{VOLUME}:/sandbox",
                        "--entrypoint", "python", "arena-sandbox:latest", "-c", CLIENT], input=json.dumps(job), capture_output=True, text=True,
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

    # The libraries the tool promises the model, used for real under the job's limits
    # (its own user, a memory ceiling, a read-only image): a JIT, native code and data files.
    r = run("""
        import numpy as np, cv2, skimage.filters, polars as pl, plotly.express as px, xgboost, lightgbm, geopandas as gpd
        from numba import njit
        from PIL import Image
        import pillow_heif
        from shapely.geometry import Point
        @njit
        def total(n):
            t = 0
            for i in range(n):
                t += i
            return t
        img = np.zeros((64, 64), np.uint8); img[16:48, 16:48] = 255
        edges = int(cv2.Canny(img, 50, 150).sum() > 0) + int(skimage.filters.sobel(img).max() > 0)
        x, y = np.random.rand(60, 3), np.arange(60) % 2
        xgboost.XGBClassifier(n_estimators=3).fit(x, y); lightgbm.LGBMClassifier(n_estimators=3, verbose=-1).fit(x, y)
        g = gpd.GeoDataFrame(geometry=[Point(0, 0), Point(10, 10)], crs="EPSG:4326").to_crs("EPSG:3857")
        px.bar(pl.DataFrame({"k": ["a", "b"], "v": [1, 2]}).to_pandas(), x="k", y="v").write_html("chart.html")
        Image.fromarray(img).save("square.png")
        print(total(10), edges, round(g.geometry.x.iloc[1]), pillow_heif.__version__ > "")
    """, timeout=90)
    rec("image, model, map and chart libraries work under the limits", r.get("exit_code") == 0 and r.get("stdout", "").split()[:3] == ["45", "2", "1113195"],
        (r.get("stdout", "") + r.get("stderr", ""))[-300:])
    rec("an interactive chart comes back as a page", {"chart.html", "square.png"} <= set(r.get("made", {})), str(list(r.get("made", {}))))

    # OCR in the languages promised (a line drawn in each, read back), and LibreOffice
    # as a job runs it (its pipe and profile beside the job), with the CJK fonts.
    r = run("""
        import subprocess, pytesseract
        from PIL import Image, ImageDraw, ImageFont
        from docx import Document
        cjk, arabic = "/usr/share/fonts/opentype/noto/NotoSansCJK-Regular.ttc", "/usr/share/fonts/truetype/noto/NotoNaskhArabic-Regular.ttf"
        for lang, text, font in [("ara", "سلام عليكم", arabic), ("chi_sim", "你好世界", cjk), ("chi_tra", "你好世界今天天氣很好", cjk)]:
            img = Image.new("L", (900, 160), 255)
            d = ImageDraw.Draw(img)
            if lang == "ara":
                d.text((860, 40), text, font=ImageFont.truetype(font, 64), fill=0, anchor="ra", direction="rtl", language="ar")
            else:
                d.text((40, 40), text, font=ImageFont.truetype(font, 64), fill=0)
            # One line of text (page segmentation 7); spaces as tesseract places them do not count.
            read = pytesseract.image_to_string(img, lang=lang, config="--psm 7").strip()
            print(lang, read.replace(" ", "") == text.replace(" ", ""), repr(read))
        print("fas" in pytesseract.get_languages())
        doc = Document(); doc.add_paragraph("中文 文档"); doc.save("zh.docx")
        r = subprocess.run(["libreoffice", "--headless", "--convert-to", "pdf", "zh.docx"], capture_output=True, text=True, timeout=100)
        print("pdf", r.returncode, "CJK" in subprocess.run(["pdffonts", "zh.pdf"], capture_output=True, text=True).stdout)
    """, timeout=120)
    out = r.get("stdout", "")
    rec("OCR reads Arabic, Chinese simplified and traditional, and has Persian",
        [l.split()[:2] for l in out.splitlines()[:3]] == [["ara", "True"], ["chi_sim", "True"], ["chi_tra", "True"]] and out.splitlines()[3:4] == ["True"], (out + r.get("stderr", ""))[-300:])
    rec("LibreOffice converts in a job, with CJK fonts", "pdf 0 True" in out, (out + r.get("stderr", ""))[-300:])

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

    tools = json.dumps([{"name": "find_symbol", "group": "argus", "description": "Find a symbol.", "parameters": {"type": "object", "properties": {"name": {"type": "string"}}, "required": ["name"]}}])
    r = run("""
        import arena
        print(arena.tools.search("symbol"))
        print(arena.argus.find_symbol(name="DecodeFrame"))
        """, inputs={".arena-tools.json": tools}, calls=True)
    rec("a program calls the chat's tools through the runner, one line each way, and no other way out",
        "arena.argus.find_symbol" in r.get("stdout", "") and "'called': 'find_symbol', 'with': {'name': 'DecodeFrame'}" in r.get("stdout", "") and r.get("calls") == 1,
        (r.get("stdout", "") + r.get("stderr", ""))[-200:].strip())

    # The science lab's mathematics (src/Llm.Api/Lab/lab.py), as the app runs it: its call in a file beside it.
    lab = (pathlib.Path(__file__).resolve().parents[2] / "src" / "Llm.Api" / "Lab" / "lab.py").read_text()

    def lab_call(op, **args):
        out = run(lab, timeout=60, inputs={"lab-call.json": json.dumps({"op": op, "args": args})})
        try:
            return json.loads(out.get("stdout", "").strip().splitlines()[-1])
        except (IndexError, json.JSONDecodeError):
            return {"ok": False, "error": (out.get("stderr") or out.get("error") or "")[-200:]}

    proved = lab_call("math_check", left="sin(x)**2 + cos(x)**2", right="1")
    refuted = lab_call("math_check", left="(x + 1)**2", right="x**2 + 1")
    solved = lab_call("math_solve", equations=["x + y = 3", "x - y = 1"])
    units = lab_call("units_convert", quantity="72 km/h", to="m/s")
    refused = lab_call("math_compute", expression="__import__('os').system('id')")
    rec("the science lab proves, refutes, solves and converts with SymPy and Pint, and reads nothing but mathematics",
        proved.get("result", {}).get("verdict") == "proved" and refuted.get("result", {}).get("verdict") == "refuted"
        and solved.get("result", {}).get("solutions", [{}])[0].get("solution") == {"x": "2", "y": "1"}
        and str(units.get("result", {}).get("result", "")).startswith("20 ") and refused.get("ok") is False,
        json.dumps([proved, refuted, solved, units, refused])[-300:])

    molecule = lab_call("chem_molecule", smiles="CC(=O)Oc1ccccc1C(=O)O")
    balanced = lab_call("chem_balance", equation="MnO4^- + Fe^2+ + H^+ -> Mn^2+ + Fe^3+ + H2O")
    mass = lab_call("chem_formula", formula="CuSO4·5H2O")
    protein = lab_call("bio_sequence", sequence="ATGGCCATTGTAATGGGCCGCTGA", operation="translate")
    rec("the lab's chemistry reads molecules (and draws them), balances a redox reaction checked atom by atom and charge, and biology translates",
        molecule.get("result", {}).get("formula") == "C9H8O4" and "<svg" in molecule.get("show", {}).get("figures", [{}])[0].get("svg", "")
        and balanced.get("result", {}).get("coefficients") == [1, 5, 8, 1, 5, 4] and balanced.get("result", {}).get("checked") is True
        and mass.get("result", {}).get("molar_mass", "").startswith("249.6") and protein.get("result", {}).get("result") == "MAIVMGR*",
        json.dumps([molecule.get("result"), balanced, mass.get("result"), protein])[-300:])

    # Quantum, on simulators: a Bell pair run, drawn and checked, and a wrong claim refuted; the surface code below its
    # threshold; a qubit decaying; VQE on H2's qubit Hamiltonian; H2's energies (FCI equal to the qubit Hamiltonian's).
    bell = "qreg q[2]; creg c[2]; h q[0]; cx q[0],q[1]; measure q -> c;"
    ran = lab_call("quantum_run", qasm=bell, shots=2000, basis=["rz", "sx", "x", "cx"])
    equal = lab_call("quantum_check", first=bell, state=["1/sqrt(2)", "0", "0", "1/sqrt(2)"])
    wrong = lab_call("quantum_check", first="qreg q[1]; h q[0];", second="qreg q[1]; x q[0];")
    rec("the lab's quantum circuits run on Aer (counts, the exact state, compiled), are drawn, and a claim is checked and a wrong one refuted",
        set(ran.get("result", {}).get("counts", {})) == {"00", "11"} and ran.get("result", {}).get("statevector", "").startswith("0.7071|00⟩")
        and "compiled" in ran.get("result", {}) and "<svg" in ran.get("show", {}).get("figures", [{}])[0].get("svg", "") and "┤ H ├" in ran.get("show", {}).get("diagram", "")
        and equal.get("result", {}).get("checked") is True and wrong.get("result", {}).get("checked") is False,
        json.dumps([ran.get("result"), equal.get("result"), wrong])[-300:])
    code = lab_call("qec_sample", distances=[3, 5], errors=[0.001], shots=20000)
    decay = lab_call("qubit_dynamics", hamiltonian="0*Z", initial="1", t1=1.0, time=5.0)
    vqe = lab_call("quantum_variational", hamiltonian="-1.0524*II + 0.3979*ZI - 0.3979*IZ - 0.0113*ZZ + 0.1809*XX", steps=150)
    h2 = lab_call("quantum_chemistry", atoms="H 0 0 0; H 0 0 0.735", basis="sto-3g")
    rows = code.get("result", {}).get("results", [])
    rec("error correction is sampled and decoded (d = 5 below d = 3 at p = 0.001), a qubit decays by T1, VQE reaches the exact energy, H2's FCI equals its qubit Hamiltonian's",
        len(rows) == 2 and rows[1]["logical"] < rows[0]["logical"]
        and abs(decay.get("result", {}).get("bloch", {}).get("qubit 0", [0, 0, 0])[2] - (1 - 2 * 2.718281828 ** -5)) < 0.01
        and abs(vqe.get("result", {}).get("error", 1)) < 1e-3
        and abs(h2.get("result", {}).get("fci", 0) - -1.1373) < 1e-3 and h2.get("result", {}).get("qubit_hamiltonian", {}).get("matches_fci") is True,
        json.dumps([rows, decay.get("result", {}).get("bloch"), vqe.get("result", {}).get("error"), h2])[-300:])

    r = run("raise ValueError('bad input')")
    rec("an error comes back as its traceback, from main.py", r.get("exit_code") == 1 and 'File "main.py", line 1' in r.get("stderr", "") and "ValueError: bad input" in r.get("stderr", ""))

    failed = [n for n, ok in RESULTS if not ok]
    print(f"\n{len(RESULTS) - len(failed)}/{len(RESULTS)} checks passed")
    return len(failed)


if __name__ == "__main__":
    sys.exit(main())
