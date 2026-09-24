#!/usr/bin/env python3
"""Run every Grafana panel query through Grafana's /api/ds/query and judge the data.

Catches what a healthy-looking Grafana hides: queries that error (bad PromQL,
LogQL or SQL), panels that are empty, NaN or null series, and percentages out
of range. It also produces the baseline the app's native dashboards must match
(docs/enterprise/PLAN.md, phase 5).

    python3 scripts/audit-dashboards.py              # last 30 minutes
    python3 scripts/audit-dashboards.py now-6h       # another window
    python3 scripts/audit-dashboards.py --json out.json

It runs in a throwaway container of the stack's own Python image, on llm-net
next to Grafana, so nothing is published; the Grafana admin credentials come
from .env.
Exit status: the number of queries that ERRORED. Empty panels are reported but
do not fail: many are empty by design until the matching traffic exists.
"""
import os
import subprocess
import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent

INNER = r'''
import base64, glob, json, math, os, sys, urllib.request, urllib.error
G = "http://grafana:3000"
AUTH = "Basic " + base64.b64encode(f"{os.environ['GU']}:{os.environ['GP']}".encode()).decode()
WINDOW = os.environ.get("WINDOW", "now-30m")

def post(path, body):
    req = urllib.request.Request(G + path, data=json.dumps(body).encode(),
                                 headers={"Content-Type": "application/json", "Authorization": AUTH})
    try:
        with urllib.request.urlopen(req, timeout=60) as r:
            return json.load(r)
    except urllib.error.HTTPError as e:
        return {"_http": e.code, "_body": e.read().decode()[:300]}

def walk(ps):
    for p in ps:
        yield p
        yield from walk(p.get("panels", []))

out = []
for f in sorted(glob.glob("/dash/*.json")):
    d = json.load(open(f))
    varvals = {}
    for v in d.get("templating", {}).get("list", []):
        cur = (v.get("current") or {}).get("value")
        # What "All" sends: the variable's own allValue (Loki rejects .*), else .*
        varvals[v["name"]] = (v.get("allValue") or ".*") if (v.get("includeAll") or v.get("type") == "query") \
            else (cur if isinstance(cur, str) else "")
    for p in walk(d.get("panels", [])):
        unit = (p.get("fieldConfig", {}).get("defaults", {}) or {}).get("unit", "")
        pds = p.get("datasource") or {}
        for t in p.get("targets") or []:
            if t.get("hide"):
                continue
            ds = t.get("datasource") or pds
            uid = ds.get("uid") if isinstance(ds, dict) else ds
            q = dict(t)
            q["datasource"] = {"uid": uid}
            for key in ("expr", "rawSql"):
                if isinstance(q.get(key), str):
                    for k, v in varvals.items():
                        q[key] = q[key].replace("${%s:regex}" % k, v).replace("${%s}" % k, v).replace("$" + k, v)
            q.setdefault("intervalMs", 15000)
            q.setdefault("maxDataPoints", 400)
            if uid == "prometheus":
                q["range"] = True
                q["instant"] = False
            res = post("/api/ds/query", {"queries": [q], "from": WINDOW, "to": "now"})
            rec = {"dash": d["title"], "panel": p.get("title"), "ref": t.get("refId"), "unit": unit,
                   "ds": uid, "q": (q.get("expr") or q.get("rawSql") or "")[:220]}
            if "_http" in res:
                rec["verdict"] = f"ERROR HTTP {res['_http']}: {res['_body'][:200]}"
                out.append(rec)
                continue
            r = list(res.get("results", {}).values())[0]
            if r.get("error"):
                rec["verdict"] = "ERROR " + r["error"][:200]
                out.append(rec)
                continue
            pts = nans = 0
            vals = []
            for fr in r.get("frames", []):
                fields = fr.get("schema", {}).get("fields", [])
                data = fr.get("data", {}).get("values", [])
                for i, fl in enumerate(fields):
                    if fl.get("type") != "number":
                        continue
                    for v in (data[i] if i < len(data) else []):
                        if v is None or (isinstance(v, float) and (math.isnan(v) or math.isinf(v))):
                            nans += 1
                        else:
                            pts += 1
                            vals.append(v)
            probs = []
            if pts == 0:
                probs.append("NO DATA")
            if nans:
                probs.append(f"{nans} null/NaN")
            if vals and unit == "percentunit" and max(vals) > 1.0001:
                probs.append(f"percentunit max {max(vals):.3g}")
            if vals and unit == "percent" and max(vals) > 100.01:
                probs.append(f"percent max {max(vals):.3g}")
            rec["verdict"] = "; ".join(probs) or "ok"
            rec["min"] = min(vals) if vals else None
            rec["max"] = max(vals) if vals else None
            out.append(rec)
json.dump(out, sys.stdout, default=str)
'''


def env(key):
    for line in (ROOT / ".env").read_text(encoding="utf-8").splitlines():
        if line.startswith(key + "="):
            return line.split("=", 1)[1].strip()
    return ""


def main():
    import json
    args = sys.argv[1:]
    dump = None
    if "--json" in args:
        i = args.index("--json")
        dump = args[i + 1]
        del args[i:i + 2]
    window = args[0] if args else "now-30m"
    dash = ROOT / "config" / "grafana" / "dashboards"
    r = subprocess.run(
        ["docker", "run", "--rm", "-i", "--network", "llm-net", "--entrypoint", "python",
         "-e", f"GU={env('GRAFANA_ADMIN_USER') or 'admin'}", "-e", f"GP={env('GRAFANA_ADMIN_PASSWORD')}",
         "-e", f"WINDOW={window}", "-v", f"{dash}:/dash:ro", "llmservice-identity-proxy:latest", "-"],
        input=INNER, capture_output=True, text=True)
    if r.returncode != 0:
        sys.exit(f"audit failed to run: {r.stderr.strip()[-400:]}")
    rows = json.loads(r.stdout)
    if dump:
        Path(dump).write_text(json.dumps(rows, indent=1, default=str), encoding="utf-8")
    errors = [x for x in rows if x["verdict"].startswith("ERROR")]
    empty = [x for x in rows if x["verdict"] != "ok" and not x["verdict"].startswith("ERROR")]
    print(f"{len(rows)} panel queries over {window}: {len(rows) - len(errors) - len(empty)} ok, "
          f"{len(empty)} empty or partial, {len(errors)} errors")
    for x in errors:
        print(f"  ERROR  [{x['dash']}] {x['panel']} ({x['ref']}): {x['verdict'][6:]}")
    for x in empty:
        print(f"  note   [{x['dash']}] {x['panel']} ({x['ref']}): {x['verdict']}")
    return len(errors)


if __name__ == "__main__":
    sys.exit(main())
