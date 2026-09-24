#!/usr/bin/env python3
"""Every dashboard panel the app draws itself must give the same data as Grafana.

For each SQL panel of the dashboards the app serves, the same time range and
the same interval go to Grafana (/api/ds/query) and to the app
(/api/dashboards/<uid>/panels/<key>/query); the answers are normalised (rows
sorted, Grafana's wide frames' null gaps dropped, times as epoch ms) and
compared value for value.

    python3 scripts/compare-dashboards.py            # last 30 days
    python3 scripts/compare-dashboards.py 7d

Signs in to the app as `admin` (ADMIN_PASSWORD, or AUTHELIA_ADMIN_PASSWORD on
an install from before the app) and to Grafana as its local admin.
Exit status: the number of panels that differ or failed.
"""
import base64
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
DASHBOARDS = ["usage-by-user", "llm-overview"]
SQL_UID = "litellm-db"


def env(key, default=""):
    for line in (ROOT / ".env").read_text(encoding="utf-8").splitlines():
        if line.startswith(key + "="):
            return line.split("=", 1)[1].strip()
    return default


DOM = env("LLM_DOMAIN", "llm.localhost")
PORT = env("TRAEFIK_HTTPS_PORT", "443")
SUFFIX = "" if PORT == "443" else f":{PORT}"
CTX = ssl.create_default_context(cafile=str(ROOT / "config/traefik/certs/tls.crt"))


def round_interval(ms):
    """Grafana's rangeutil.roundInterval (and the app's SqlMacros.RoundInterval)."""
    for below, rounded in [(15, 10), (35, 20), (75, 50), (150, 100), (350, 200), (750, 500), (1500, 1000),
                           (3500, 2000), (7500, 5000), (12500, 10000), (17500, 15000), (25000, 20000),
                           (45000, 30000), (90000, 60000), (210000, 120000), (450000, 300000),
                           (750000, 600000), (1050000, 900000), (1500000, 1200000), (2700000, 1800000),
                           (5400000, 3600000), (9000000, 7200000), (16200000, 10800000),
                           (32400000, 21600000), (86400000, 43200000), (604800000, 86400000),
                           (1814400000, 604800000), (3628800000, 2592000000)]:
        if ms < below:
            return rounded
    return 31536000000


class Client:
    def __init__(self):
        self.opener = urllib.request.build_opener(urllib.request.HTTPSHandler(context=CTX),
                                                  urllib.request.HTTPCookieProcessor(http.cookiejar.CookieJar()))

    def call(self, url, body=None, headers=None):
        h = {"Content-Type": "application/json", "Accept": "application/json", **(headers or {})}
        req = urllib.request.Request(url, data=json.dumps(body).encode() if body is not None else None, headers=h)
        try:
            with self.opener.open(req, timeout=120) as r:
                return r.status, json.load(r)
        except urllib.error.HTTPError as e:
            return e.code, {"error": e.read().decode()[:300]}


def walk(panels):
    for p in panels or []:
        yield p
        yield from walk(p.get("panels"))


def num(v):
    if isinstance(v, bool) or v is None:
        return v
    if isinstance(v, (int, float)):
        return None if (isinstance(v, float) and math.isnan(v)) else round(float(v), 9)
    return v


def iso_ms(v):
    return int(datetime.fromisoformat(v.replace("Z", "+00:00")).replace(tzinfo=timezone.utc).timestamp() * 1000)


def grafana_norm(result, fmt):
    frames = result.get("frames") or []
    if fmt == "time_series":
        out = {}
        for fr in frames:
            fields = fr["schema"]["fields"]
            values = fr["data"]["values"]
            ti = next(i for i, f in enumerate(fields) if f.get("type") == "time")
            for i, f in enumerate(fields):
                if i == ti or f.get("type") != "number":
                    continue
                # A NULL metric: Grafana leaves the field unnamed, the app names it "".
                name = (f.get("labels") or {}).get("metric") or (f.get("config") or {}).get("displayNameFromDS") or f.get("name") or ""
                pts = out.setdefault(name, set())
                for t, v in zip(values[ti], values[i]):
                    if v is not None:
                        pts.add((int(t), num(v)))
        return {k: sorted(v) for k, v in out.items() if v}
    rows = []
    for fr in frames:
        fields = fr["schema"]["fields"]
        values = fr["data"]["values"]
        for r in range(len(values[0]) if values else 0):
            rows.append(tuple(num(values[c][r]) for c in range(len(fields))))
    return sorted(rows, key=repr)


def app_norm(result):
    if result.get("series") is not None:
        out = {}
        for s in result["series"]:
            pts = {(int(p[0]), num(p[1])) for p in s["points"] if p[1] is not None}
            if pts:
                out[s["name"]] = sorted(pts)
        return out
    t = result.get("table") or {"columns": [], "rows": []}
    rows = []
    for r in t["rows"]:
        rows.append(tuple(iso_ms(v) if c["type"] == "time" and isinstance(v, str) else num(v)
                          for c, v in zip(t["columns"], r)))
    return sorted(rows, key=repr)


def main():
    days = int((sys.argv[1] if len(sys.argv) > 1 else "30d").rstrip("d"))
    to_ms = int(time.time() * 1000) // 60000 * 60000
    from_ms = to_ms - days * 86400000
    interval = round_interval((to_ms - from_ms) / 400)

    app = Client()
    password = env("ADMIN_PASSWORD") or env("AUTHELIA_ADMIN_PASSWORD")
    code, body = app.call(f"https://{DOM}{SUFFIX}/api/auth/login", {"userName": "admin", "password": password},
                          {"X-Requested-With": "compare"})
    if code != 200 or body.get("status") != "ok":
        sys.exit(f"cannot sign in to the app: HTTP {code} {body}")
    graf = Client()
    gauth = {"Authorization": "Basic " + base64.b64encode(
        f"{env('GRAFANA_ADMIN_USER', 'admin')}:{env('GRAFANA_ADMIN_PASSWORD')}".encode()).decode()}

    frm = datetime.fromtimestamp(from_ms / 1000, timezone.utc).isoformat()
    to = datetime.fromtimestamp(to_ms / 1000, timezone.utc).isoformat()
    print(f"comparing over the last {days} days, interval {interval // 1000} s\n")
    bad = checked = 0
    for uid in DASHBOARDS:
        d = json.loads((ROOT / "config/grafana/dashboards" / f"{uid}.json").read_text(encoding="utf-8"))
        for key, p in enumerate(walk(d.get("panels"))):
            targets = [t for t in p.get("targets") or [] if not t.get("hide")]
            ds = [(t.get("datasource") or p.get("datasource") or {}).get("uid") for t in targets]
            if not targets or any(x != SQL_UID for x in ds) or any(not t.get("rawSql") for t in targets):
                continue
            checked += 1
            code, mine = app.call(f"https://{DOM}{SUFFIX}/api/dashboards/{uid}/panels/{key}/query",
                                  {"from": frm, "to": to, "intervalMs": interval}, {"X-Requested-With": "compare"})
            queries = [{**t, "datasource": {"uid": SQL_UID}, "intervalMs": interval, "maxDataPoints": 400} for t in targets]
            gcode, theirs = graf.call(f"https://grafana.{DOM}{SUFFIX}/api/ds/query",
                                      {"queries": queries, "from": str(from_ms), "to": str(to_ms)}, gauth)
            label = f"[{d['title']}] {p.get('title')}"
            if code != 200 or gcode != 200:
                print(f"  FAIL  {label}: app HTTP {code}, Grafana HTTP {gcode}")
                bad += 1
                continue
            diffs = []
            for r in mine["results"]:
                g = theirs["results"].get(r["refId"], {})
                if r.get("error") or g.get("error"):
                    diffs.append(f"{r['refId']}: app error {r.get('error')!r}, Grafana error {g.get('error')!r}")
                    continue
                a, b = app_norm(r), grafana_norm(g, r["format"])
                if a != b:
                    diffs.append(f"{r['refId']}: app {str(a)[:160]} != Grafana {str(b)[:160]}")
            if diffs:
                bad += 1
                print(f"  FAIL  {label}")
                for x in diffs:
                    print(f"        {x}")
            else:
                print(f"  same  {label}")
    print(f"\n{checked - bad}/{checked} panels give the same data in the app and in Grafana")
    return bad


if __name__ == "__main__":
    sys.exit(main())
