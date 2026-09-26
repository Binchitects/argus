#!/usr/bin/env python3
"""Run every dashboard panel's queries in the app and judge the data.

Catches what a healthy-looking page hides: queries that error (bad PromQL,
LogQL or SQL), panels that are empty, null or NaN values, and percentages out
of range. Each panel is asked as its page asks it: through the app's
/api/dashboards API, signed in as the admin (the password comes from .env and
is never printed).

    python3 scripts/audit-dashboards.py              # metrics and logs over 30 minutes
    python3 scripts/audit-dashboards.py 6h           # another window (usage panels: 30 days)
    python3 scripts/audit-dashboards.py --json out.json

Exit status: the number of queries that ERRORED. Empty panels are reported but
do not fail: many are empty by design until the matching traffic exists.
"""
import http.cookiejar
import json
import math
import ssl
import sys
import time
import urllib.error
import urllib.request
from datetime import datetime, timezone
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent
SQL_UID = "litellm-db"


def env(key, default=""):
    for line in (ROOT / ".env").read_text(encoding="utf-8").splitlines():
        if line.startswith(key + "="):
            return line.split("=", 1)[1].strip()
    return default


DOM = env("LLM_DOMAIN", "llm.localhost")
PORT = env("TRAEFIK_HTTPS_PORT", "443")
BASE = f"https://{DOM}" + ("" if PORT == "443" else f":{PORT}")
CTX = ssl.create_default_context(cafile=str(ROOT / "config/traefik/certs/tls.crt"))
OPENER = urllib.request.build_opener(urllib.request.HTTPSHandler(context=CTX),
                                     urllib.request.HTTPCookieProcessor(http.cookiejar.CookieJar()))


def call(path, body=None):
    req = urllib.request.Request(BASE + path, data=json.dumps(body).encode() if body is not None else None,
                                 headers={"Content-Type": "application/json", "Accept": "application/json",
                                          "X-Requested-With": "audit"})
    try:
        with OPENER.open(req, timeout=120) as r:
            return r.status, json.load(r)
    except urllib.error.HTTPError as e:
        try:
            return e.code, json.load(e)
        except ValueError:
            return e.code, {"error": f"HTTP {e.code}"}


def seconds(text):
    return int(text[:-1]) * {"m": 60, "h": 3600, "d": 86400}[text[-1]]


def iso(t):
    return datetime.fromtimestamp(t, timezone.utc).isoformat()


def values(result):
    """Every number a target returned, and how many were null or NaN; log lines are counted apart."""
    nums, bad = [], 0
    for s in result.get("series") or []:
        for p in s["points"]:
            v = p[1] if len(p) > 1 else None
            if v is None or (isinstance(v, float) and (math.isnan(v) or math.isinf(v))):
                bad += 1
            else:
                nums.append(v)
    table = result.get("table")
    if table:
        numeric = [i for i, c in enumerate(table["columns"]) if c["type"] == "number"]
        for row in table["rows"]:
            for i in numeric:
                if row[i] is None:
                    bad += 1
                else:
                    nums.append(row[i])
    return nums, bad, len(result.get("logs") or [])


def main():
    args = sys.argv[1:]
    dump = None
    if "--json" in args:
        i = args.index("--json")
        dump = args[i + 1]
        del args[i:i + 2]
    window = args[0].removeprefix("now-") if args else "30m"
    code, body = call("/api/auth/login", {"userName": "admin", "password": env("ADMIN_PASSWORD") or env("AUTHELIA_ADMIN_PASSWORD")})
    if code != 200 or body.get("status") != "ok":
        sys.exit(f"cannot sign in to the app: HTTP {code} {body.get('error', '')}")

    now = time.time()
    rows = []
    _, dashboards = call("/api/dashboards/")
    for listed in dashboards:
        _, d = call(f"/api/dashboards/{listed['uid']}")
        for p in d["panels"]:
            if p["type"] in ("row", "text") or not p.get("supported"):
                continue
            # Usage panels read the gateway's database: a month, as their page opens.
            span = 30 * 86400 if SQL_UID in p.get("datasources", []) else seconds(window)
            code, r = call(f"/api/dashboards/{d['uid']}/panels/{p['key']}/query",
                           {"from": iso(now - span), "to": iso(now), "maxDataPoints": 400})
            unit = ((p.get("fieldConfig") or {}).get("defaults") or {}).get("unit", "")
            base = {"dash": d["title"], "panel": p.get("title"), "unit": unit}
            if code != 200:
                rows.append({**base, "ref": "-", "verdict": f"ERROR HTTP {code}: {r.get('error', '')[:200]}"})
                continue
            for t in r["results"]:
                rec = {**base, "ref": t["refId"]}
                if t.get("error"):
                    rows.append({**rec, "verdict": "ERROR " + t["error"][:200]})
                    continue
                nums, bad, lines = values(t)
                problems = []
                if not nums and not lines:
                    problems.append("NO DATA")
                if bad:
                    problems.append(f"{bad} null/NaN")
                if nums and unit == "percentunit" and max(nums) > 1.0001:
                    problems.append(f"percentunit max {max(nums):.3g}")
                if nums and unit == "percent" and max(nums) > 100.01:
                    problems.append(f"percent max {max(nums):.3g}")
                rows.append({**rec, "verdict": "; ".join(problems) or "ok",
                             "min": min(nums) if nums else None, "max": max(nums) if nums else None, "lines": lines})

    if dump:
        Path(dump).write_text(json.dumps(rows, indent=1, default=str), encoding="utf-8")
    errors = [x for x in rows if x["verdict"].startswith("ERROR")]
    empty = [x for x in rows if x["verdict"] != "ok" and not x["verdict"].startswith("ERROR")]
    print(f"{len(rows)} panel queries (metrics and logs over {window}, usage over 30d): "
          f"{len(rows) - len(errors) - len(empty)} ok, {len(empty)} empty or partial, {len(errors)} errors")
    for x in errors:
        print(f"  ERROR  [{x['dash']}] {x['panel']} ({x['ref']}): {x['verdict'][6:]}")
    for x in empty:
        print(f"  note   [{x['dash']}] {x['panel']} ({x['ref']}): {x['verdict']}")
    return len(errors)


if __name__ == "__main__":
    sys.exit(main())
