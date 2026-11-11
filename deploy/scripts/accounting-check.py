#!/usr/bin/env python3
"""
Accounting's numbers against the ledger they come from.

Admin → Settings → Costs → Accounting reads gateway.requests. This asks the page's API for a window, as the page does
(signed in as the admin; the password comes from .env and is never printed), reads the same window straight from
Postgres with its own SQL, and checks:

  totals     requests, tokens (input, cached, output) and spend equal to the ledger's (refusals left out)
  breakdowns people, models, sources, kinds and the time series each add up to the totals
  shares     each breakdown's shares add up to 1 (when anything was used)
  rows       no row with negative tokens or cost, cached never above input, a finished request with no tokens
  tags       the By tag rows match the ledger joined to the chats' and projects' tags

    python3 scripts/accounting-check.py              # the last 24 hours
    python3 scripts/accounting-check.py 7d           # another window (h or d)
    python3 scripts/accounting-check.py --json out.json

Exit status: the number of checks that failed. Standard library only; needs docker (docker exec postgres psql).
"""
import http.cookiejar
import json
import ssl
import subprocess
import sys
import urllib.error
import urllib.parse
import urllib.request
from datetime import datetime, timedelta, timezone
from decimal import Decimal
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent


def env(key, default=""):
    for line in (ROOT / ".env").read_text(encoding="utf-8").splitlines():
        if line.startswith(key + "="):
            return line.split("=", 1)[1].strip()
    return default


DOM = env("DOMAIN", "llm.localhost")
PORT = env("TRAEFIK_HTTPS_PORT", "443")
APP = f"https://{DOM}" + ("" if PORT == "443" else f":{PORT}")
CTX = ssl.create_default_context() if env("ACME_EMAIL") else ssl._create_unverified_context()
JAR = http.cookiejar.CookieJar()
OPENER = urllib.request.build_opener(urllib.request.HTTPCookieProcessor(JAR), urllib.request.HTTPSHandler(context=CTX))


def call(path, body=None):
    req = urllib.request.Request(APP + path, data=None if body is None else json.dumps(body).encode(), method="GET" if body is None else "POST",
                                 headers={"Content-Type": "application/json", "X-Requested-With": "accounting-check"})
    try:
        with OPENER.open(req, timeout=120) as r:
            return r.status, json.loads(r.read().decode() or "null")
    except urllib.error.HTTPError as e:
        return e.code, e.read().decode(errors="replace")[:300]


def psql(sql):
    out = subprocess.run(["docker", "exec", "postgres", "psql", "-U", "arena", "-d", "llmapp", "-At", "-F", "\t", "-c", sql], capture_output=True, text=True, timeout=120)
    if out.returncode != 0:
        sys.exit(f"psql: {out.stderr.strip()[:300]}")
    return [line.split("\t") for line in out.stdout.splitlines() if line.strip()]


def window(arg):
    n, unit = int(arg[:-1]), arg[-1]
    return timedelta(hours=n) if unit == "h" else timedelta(days=n)


def main():
    args = [a for a in sys.argv[1:] if not a.startswith("--")]
    out_json = sys.argv[sys.argv.index("--json") + 1] if "--json" in sys.argv else None
    if out_json in args:
        args.remove(out_json)
    length = window(args[0]) if args else timedelta(hours=24)
    to = datetime.now(timezone.utc).replace(microsecond=0) + timedelta(minutes=1)
    frm = to - length
    code, _ = call("/api/auth/login", {"userName": "admin", "password": env("ADMIN_PASSWORD")})
    if code != 200:
        sys.exit(f"the admin could not sign in: {code}")
    q = urllib.parse.urlencode({"from": frm.isoformat(), "to": to.isoformat()})
    code, rep = call(f"/api/admin/accounting?{q}")
    if code != 200:
        sys.exit(f"/api/admin/accounting: {code} {rep}")

    where = f"r.started_at >= '{frm.isoformat()}' and r.started_at < '{to.isoformat()}'"
    t = psql(f"""select count(*), coalesce(sum(r.prompt_tokens),0) + coalesce(sum(r.completion_tokens),0), coalesce(sum(r.cost),0)::text,
                        coalesce(sum(greatest(r.prompt_tokens - r.cached_tokens, 0)),0), coalesce(sum(r.cached_tokens),0), coalesce(sum(r.completion_tokens),0),
                        count(distinct lower(r.person))
                 from gateway.requests r where {where} and r.status <> 'refused'""")[0]
    ledger = {"requests": int(t[0]), "tokens": int(t[1]), "charged": Decimal(t[2]), "input": int(t[3]), "cached": int(t[4]), "output": int(t[5]), "people": int(t[6])}
    refused = int(psql(f"select count(*) from gateway.requests r where {where} and r.status = 'refused'")[0][0])
    bad = psql(f"""select
                     count(*) filter (where r.prompt_tokens < 0 or r.completion_tokens < 0 or r.cached_tokens < 0),
                     count(*) filter (where r.cost < 0),
                     count(*) filter (where r.cached_tokens > r.prompt_tokens),
                     count(*) filter (where r.status = 'done' and r.call in ('chat', 'completion', 'responses', 'messages') and coalesce(r.prompt_tokens,0) + coalesce(r.completion_tokens,0) = 0)
                   from gateway.requests r where {where}""")[0]
    used = rep["used"]
    checks = []

    def check(name, ok, detail=""):
        checks.append({"check": name, "ok": bool(ok), "detail": detail})

    def money(v):
        return Decimal(str(v))

    check("totals: requests", used["requests"] == ledger["requests"], f"page {used['requests']}, ledger {ledger['requests']} ({refused} refusals left out)")
    check("totals: tokens", used["tokens"] == ledger["tokens"], f"page {used['tokens']}, ledger {ledger['tokens']}")
    for k in ("input", "cached", "output"):
        check(f"totals: {k} tokens", used[k] == ledger[k], f"page {used[k]}, ledger {ledger[k]}")
    check("totals: spend", abs(money(used["charged"]) - ledger["charged"]) <= Decimal("0.000001"), f"page {used['charged']}, ledger {ledger['charged']}")
    check("totals: people", used["people"] == ledger["people"], f"page {used['people']}, ledger {ledger['people']}")

    for part in ("people", "models", "sources", "kinds"):
        rows = rep.get(part) or []
        r_sum = sum(r["requests"] for r in rows)
        t_sum = sum(r["tokens"] for r in rows)
        c_sum = sum(money(r["charged"]) for r in rows)
        check(f"{part}: add up to the totals", r_sum == used["requests"] and t_sum == used["tokens"] and abs(c_sum - money(used["charged"])) <= Decimal("0.000001"),
              f"{len(rows)} rows: {r_sum} requests, {t_sum} tokens, {c_sum} spend")
        if used["requests"] > 0 and rows:
            share = sum(r["share"] for r in rows)
            check(f"{part}: shares add up to 1", abs(share - 1) < 0.001, f"{share:.4f}")
    series = rep.get("series") or []
    s_req = sum(b["requests"] for b in series)
    s_tok = sum(b["tokens"] for b in series)
    check("series: adds up to the totals", s_req == used["requests"] and s_tok == used["tokens"], f"{len(series)} buckets: {s_req} requests, {s_tok} tokens")

    by_model = {r[0]: (int(r[1]), int(r[2])) for r in psql(
        f"select coalesce(r.model, ''), count(*), coalesce(sum(r.prompt_tokens),0) + coalesce(sum(r.completion_tokens),0) from gateway.requests r where {where} and r.status <> 'refused' group by 1")}
    page_model = {}
    for r in rep.get("models") or []:
        a, b = page_model.get(r["name"], (0, 0))
        page_model[r["name"]] = (a + r["requests"], b + r["tokens"])
    check("models: each as the ledger's", by_model == page_model or {k: v for k, v in by_model.items() if k} == {k: v for k, v in page_model.items() if k},
          "; ".join(f"{k or '(none)'} page {page_model.get(k)} ledger {v}" for k, v in sorted(by_model.items())))

    tags = {r[0]: (int(r[1]), int(r[2])) for r in psql(f"""
        select t.tag, count(*), coalesce(sum(r.prompt_tokens),0) + coalesce(sum(r.completion_tokens),0)
        from gateway.requests r
        left join conversations c on c."Id" = r.conversation_id
        left join work_projects p on p."Id" = r.project_id
        cross join lateral (select distinct x from unnest(coalesce(c."Tags", '{{}}'::text[]) || coalesce(p."Tags", '{{}}'::text[])) x) t(tag)
        where {where} and r.status <> 'refused' group by 1""")} if rep.get("tags") is not None else None
    if tags is not None:
        page_tags = {}
        for r in rep.get("tags") or []:
            a, b = page_tags.get(r["name"], (0, 0))
            page_tags[r["name"]] = (a + r["requests"], b + r["tokens"])
        check("tags: each as the ledger's", tags == page_tags, f"page {page_tags}, ledger {tags}")

    check("rows: no negative tokens", int(bad[0]) == 0, bad[0])
    check("rows: no negative cost", int(bad[1]) == 0, bad[1])
    check("rows: cached never above input", int(bad[2]) == 0, bad[2])
    check("rows: every finished answer has tokens", int(bad[3]) == 0, bad[3])

    print(f"Accounting {frm:%Y-%m-%d %H:%M} to {to:%Y-%m-%d %H:%M} UTC: {used['requests']} requests, {used['tokens']} tokens, spend {used['charged']}")
    for c in checks:
        print(f"  {'ok  ' if c['ok'] else 'FAIL'}  {c['check']:<42} {c['detail']}")
    failed = sum(1 for c in checks if not c["ok"])
    print(f"{len(checks) - failed} of {len(checks)} checks pass")
    if out_json:
        Path(out_json).write_text(json.dumps({"from": frm.isoformat(), "to": to.isoformat(), "checks": checks, "used": used}, indent=2, default=str), encoding="utf-8")
    sys.exit(failed)


if __name__ == "__main__":
    main()
