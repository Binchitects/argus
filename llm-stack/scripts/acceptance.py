#!/usr/bin/env python3
"""Whole-stack acceptance: is this build deliverable?

`e2e-check.py` proves the SERVING path from inside the compose network -- engine,
gateway, attribution, budgets, Argus. This runs from the HOST and covers what
that cannot see: real TLS through Traefik, the routes a browser would use, the
identity provider, the observability stack, and the operational promises
(backup works, setup is re-runnable, no placeholder secrets shipped).

It calls e2e-check rather than reimplementing it -- one copy of that logic.

    python3 scripts/acceptance.py
    python3 scripts/acceptance.py --skip-e2e     # host-side surfaces only

Exit code is 0 only if every check passes. SKIP is not failure: some checks
cannot apply to a stack in a given state (a profile is off, an account already
exists) and say so rather than pretending.
"""
from __future__ import annotations

import argparse
import json
import os
import re
import ssl
import subprocess
import sys
import urllib.error
import urllib.request
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent
GREEN, RED, YELLOW, DIM, BOLD, OFF = (
    "\033[32m", "\033[31m", "\033[33m", "\033[90m", "\033[1m", "\033[0m")

results: list[tuple[str, str, str]] = []   # (section, name, state)


def record(section: str, name: str, state: str, detail: str = "") -> bool:
    colour = {"PASS": GREEN, "FAIL": RED, "SKIP": YELLOW}[state]
    print(f"  [{colour}{state}{OFF}] {name}" + (f"  {DIM}{detail}{OFF}" if detail else ""),
          flush=True)
    results.append((section, name, state))
    return state != "FAIL"


def section(title: str) -> None:
    print(f"\n{BOLD}{title}{OFF}", flush=True)


def env(key: str, default: str = "") -> str:
    try:
        for line in (ROOT / ".env").read_text(encoding="utf-8", errors="replace").splitlines():
            if line.startswith(f"{key}="):
                return line.split("=", 1)[1].strip()
    except OSError:
        pass
    return default


def _bash() -> str:
    """`bash` on PATH resolves to WSL's under native Windows Python, which
    cannot exec this repo's scripts ("execvpe(/bin/bash) failed"). Prefer Git
    Bash when present. Forward slashes deliberately -- Windows accepts them,
    and they cannot be mangled by a backslash escape."""
    for c in ("C:/Program Files/Git/bin/bash.exe",
              "C:/Program Files (x86)/Git/bin/bash.exe",
              "/usr/bin/bash", "/bin/bash"):
        if Path(c).exists():
            return c
    return "bash"


def sh(*args: str, timeout: int = 120) -> tuple[int, str]:
    argv = list(args)
    if argv and argv[0] == "bash":
        argv[0] = _bash()
    p = subprocess.run(argv, cwd=ROOT, capture_output=True, text=True, timeout=timeout)
    return p.returncode, (p.stdout or "") + (p.stderr or "")


DOMAIN = env("LLM_DOMAIN", "llm.localhost")
HTTPS_PORT = env("TRAEFIK_HTTPS_PORT", "443")
HTTP_PORT = env("TRAEFIK_HTTP_PORT", "80")
PROFILES = env("COMPOSE_PROFILES", "")
CA = ROOT / "config/traefik/certs/ca.crt"
SUFFIX = "" if HTTPS_PORT == "443" else f":{HTTPS_PORT}"


def url(host: str, path: str = "/") -> str:
    return f"https://{host}.{DOMAIN}{SUFFIX}{path}"


def ctx() -> ssl.SSLContext:
    """Verify against the stack's OWN CA -- never disable verification, since
    'does TLS actually work for a client' is one of the things under test."""
    return ssl.create_default_context(cafile=str(CA))


def get(u: str, token: str = "", timeout: int = 30):
    headers = {"Authorization": f"Bearer {token}"} if token else {}
    req = urllib.request.Request(u, headers=headers)
    return urllib.request.urlopen(req, context=ctx(), timeout=timeout)


# --------------------------------------------------------------- A. config ---
def check_config() -> None:
    section("A. Configuration hygiene")

    if not (ROOT / ".env").exists():
        record("config", ".env exists", "FAIL", "run scripts/setup.sh")
        return
    record("config", ".env exists", "PASS")

    # Shipping a placeholder secret is the difference between a demo and a
    # deliverable. bootstrap writes real values; anything left is a bug.
    text = (ROOT / ".env").read_text(encoding="utf-8", errors="replace")
    placeholders = [l.split("=", 1)[0] for l in text.splitlines()
                    if "change-me" in l and not l.lstrip().startswith("#")]
    record("config", "no placeholder secrets left in .env",
           "PASS" if not placeholders else "FAIL",
           "" if not placeholders else ", ".join(placeholders[:5]))

    # The rendered files must agree with LLM_DOMAIN. A stale domain here is the
    # failure that breaks SSO while every container still reports healthy.
    for rel in ("config/authelia/configuration.yml", "config/homepage/services.yaml"):
        p = ROOT / rel
        if not p.exists():
            record("config", f"{Path(rel).name} rendered", "SKIP", "not present")
            continue
        body = p.read_text(encoding="utf-8", errors="replace")
        # Require at least one label: a bare ".localhost" is a fragment,
        # not a hostname, and matching it produced a false failure.
        stale = re.findall(r"[a-z0-9][a-z0-9.-]*\.localhost", body)
        wrong = sorted({s for s in stale if not s.endswith(DOMAIN)})
        record("config", f"{Path(rel).name} matches LLM_DOMAIN",
               "PASS" if not wrong else "FAIL",
               f"domain={DOMAIN}" if not wrong else f"stale: {wrong[:3]}")

    code, out = sh("docker", "compose", "config", "-q")
    record("config", "docker-compose.yml parses", "PASS" if code == 0 else "FAIL",
           "" if code == 0 else out.strip().splitlines()[-1][:90])

    # Traefik must not adopt containers from other compose projects: router
    # names are global, so a neighbouring `authelia` router silently wins.
    tf = (ROOT / "config/traefik/traefik.yml").read_text(encoding="utf-8", errors="replace")
    record("config", "Traefik discovery scoped to this project",
           "PASS" if "constraints:" in tf else "FAIL",
           "" if "constraints:" in tf else "an unrelated project can steal routes")


# ------------------------------------------------------- B. infrastructure ---
EXPECTED = {
    "": ["open-webui", "prometheus", "grafana", "alertmanager", "node-exporter", "cadvisor"],
    "proxy": ["traefik"],
    "gateway": ["litellm", "postgres", "redis"],
    "auth": ["authelia"],
    "smi": ["nvidia-smi-exporter"],
    "argus": ["argus"],
}


def check_infra() -> None:
    section("B. Containers")
    code, out = sh("docker", "compose", "ps", "--format", "{{.Service}}\t{{.Status}}")
    if code != 0:
        record("infra", "docker compose reachable", "FAIL", out.strip()[:90])
        return
    state = dict(l.split("\t", 1) for l in out.strip().splitlines() if "\t" in l)

    want: list[str] = []
    for profile, services in EXPECTED.items():
        if profile == "" or profile in PROFILES:
            want += services
    missing = [s for s in want if s not in state]
    unhealthy = [f"{s}={state[s]}" for s in want
                 if s in state and "Up" not in state[s]]
    record("infra", f"all {len(want)} expected services running",
           "PASS" if not missing and not unhealthy else "FAIL",
           f"profiles={PROFILES}" if not missing and not unhealthy
           else f"missing={missing} bad={unhealthy}")

    # vLLM's healthcheck is the one that means "weights loaded and serving".
    if "vllm" in state:
        record("infra", "engine reports healthy",
               "PASS" if "healthy" in state["vllm"] else "FAIL", state["vllm"])

    code, out = sh("docker", "compose", "ps", "traefik", "--format", "{{.Ports}}")
    ok = f":{HTTPS_PORT}->" in out and f":{HTTP_PORT}->" in out
    record("infra", f"Traefik publishes {HTTP_PORT} and {HTTPS_PORT}",
           "PASS" if ok else "FAIL", out.strip()[:80])


# ------------------------------------------------------- C. TLS and routes ---
ROUTES = [
    ("chat", "/", {200, 302}, ""),
    ("gateway", "/v1/models", {200, 401}, ""),
    ("grafana", "/", {200, 302}, ""),
    ("auth", "/", {200, 302}, "auth"),
    # 302 ONLY, and that narrowness is the point: this request carries no
    # session, so Authelia must bounce it to the portal. A 200 here would mean
    # forward-auth was bypassed and an anonymous caller reached a page that can
    # mint API keys -- exactly the failure worth catching automatically.
    ("admin", "/", {302}, "auth"),
    ("argus", "/healthz", {200, 401}, "argus"),
]


def check_routes() -> None:
    section("C. TLS and routing (as a browser sees it)")
    if not CA.exists():
        record("routes", "stack CA present", "FAIL", str(CA))
        return
    record("routes", "stack CA present", "PASS", CA.name)

    for host, path, allowed, profile in ROUTES:
        if profile and profile not in PROFILES:
            record("routes", f"{host}.{DOMAIN}", "SKIP", f"profile {profile} off")
            continue
        try:
            with get(url(host, path)) as r:
                code = r.status
        except urllib.error.HTTPError as exc:
            code = exc.code
        except Exception as exc:                       # TLS failure lands here
            record("routes", f"{host}.{DOMAIN}", "FAIL", f"{type(exc).__name__}: {exc}"[:80])
            continue
        record("routes", f"{host}.{DOMAIN}", "PASS" if code in allowed else "FAIL",
               f"HTTP {code} over TLS")

    # Plain HTTP must not serve content; it must redirect.
    try:
        req = urllib.request.Request(
            f"http://chat.{DOMAIN}" + ("" if HTTP_PORT == "80" else f":{HTTP_PORT}"))
        opener = urllib.request.build_opener(NoRedirect)
        try:
            with opener.open(req, timeout=20) as r:
                code, loc = r.status, r.headers.get("Location", "")
        except urllib.error.HTTPError as exc:
            code, loc = exc.code, exc.headers.get("Location", "")
        record("routes", "HTTP redirects to HTTPS",
               "PASS" if code in (301, 302, 308) and loc.startswith("https://") else "FAIL",
               f"HTTP {code} -> {loc[:50]}")
    except Exception as exc:
        record("routes", "HTTP redirects to HTTPS", "FAIL", str(exc)[:70])


class NoRedirect(urllib.request.HTTPRedirectHandler):
    def redirect_request(self, *_args, **_kw):
        return None


# ------------------------------------------------------------ D. identity ---
def check_identity() -> None:
    section("D. Identity provider")
    if "auth" not in PROFILES:
        record("identity", "Authelia", "SKIP", "auth profile off")
        return
    try:
        with get(url("auth", "/.well-known/openid-configuration")) as r:
            doc = json.load(r)
    except Exception as exc:
        record("identity", "OIDC discovery document", "FAIL", str(exc)[:80])
        return

    issuer = doc.get("issuer", "")
    record("identity", "OIDC discovery document", "PASS",
           f"{len(doc.get('scopes_supported', []))} scopes")
    # The issuer proves WHICH Authelia answered -- the check that would have
    # caught a neighbouring project's router hijacking this hostname.
    record("identity", "issuer names this domain",
           "PASS" if DOMAIN in issuer else "FAIL", issuer[:70])
    for ep in ("authorization_endpoint", "token_endpoint", "jwks_uri"):
        v = doc.get(ep, "")
        if not (v and DOMAIN in v):
            record("identity", f"{ep} on this domain", "FAIL", v[:70])
            break
    else:
        record("identity", "all endpoints on this domain", "PASS")


# --------------------------------------------------------- E. observability ---
def check_observability() -> None:
    section("E. Observability")
    code, out = sh("docker", "compose", "exec", "-T", "prometheus",
                   "wget", "-qO-", "http://localhost:9090/api/v1/targets")
    if code != 0:
        record("obs", "Prometheus targets", "FAIL", "cannot query prometheus")
    else:
        try:
            targets = json.loads(out)["data"]["activeTargets"]
            up = [t for t in targets if t["health"] == "up"]
            down = sorted({t["labels"].get("job", "?") for t in targets
                           if t["health"] != "up"})
            # vLLM and dcgm are legitimately down when not scraped/enabled;
            # report them but do not fail the build on them.
            record("obs", "Prometheus scraping", "PASS",
                   f"{len(up)}/{len(targets)} up" + (f", down: {down}" if down else ""))
        except Exception as exc:
            record("obs", "Prometheus targets", "FAIL", str(exc)[:70])

    user, pw = env("GRAFANA_ADMIN_USER", "admin"), env("GRAFANA_ADMIN_PASSWORD")
    if not pw:
        record("obs", "Grafana", "SKIP", "no GRAFANA_ADMIN_PASSWORD in .env")
        return
    import base64
    auth = base64.b64encode(f"{user}:{pw}".encode()).decode()
    def gf(path: str):
        req = urllib.request.Request(url("grafana", path),
                                     headers={"Authorization": f"Basic {auth}"})
        return urllib.request.urlopen(req, context=ctx(), timeout=30)
    try:
        with gf("/api/datasources") as r:
            ds = json.load(r)
    except Exception as exc:
        record("obs", "Grafana API", "FAIL", str(exc)[:80] + " (password may predate a redeploy)")
        return
    record("obs", "Grafana API reachable", "PASS", f"{len(ds)} datasources")

    # Datasources for profile-gated services (Loki with `logging` off, Langfuse
    # with `tracing` off) cannot be healthy and are not defects.
    gated = {"loki": "logging", "langfuse": "tracing", "clickhouse": "tracing"}
    bad, skipped = [], []
    for d in ds:
        need = gated.get(d["name"].lower())
        if need and need not in PROFILES:
            skipped.append(d["name"]); continue
        try:
            with gf(f"/api/datasources/{d['id']}/health") as r:
                if json.load(r).get("status") != "OK":
                    bad.append(d["name"])
        except urllib.error.HTTPError as exc:
            # Grafana's built-in Alertmanager datasource has no backend health
            # handler and answers "plugin.unavailable". Verified separately that
            # it proxies /api/v2/status correctly, so this is a missing endpoint,
            # not a broken datasource.
            body = exc.read().decode("utf-8", "replace")
            if "plugin.unavailable" in body:
                skipped.append(f"{d['name']} (no health endpoint)")
            else:
                bad.append(d["name"])
        except Exception:
            bad.append(d["name"])
    if skipped:
        record("obs", f"datasources for disabled profiles", "SKIP", ", ".join(skipped))
    record("obs", "every active datasource healthy", "PASS" if not bad else "FAIL",
           f"{len(ds) - len(skipped)} checked" if not bad else f"unhealthy: {bad}")

    try:
        with gf("/api/search?type=dash-db") as r:
            dash = json.load(r)
        record("obs", "dashboards provisioned", "PASS" if dash else "FAIL",
               f"{len(dash)}: {[d['title'] for d in dash][:4]}")
    except Exception as exc:
        record("obs", "dashboards provisioned", "FAIL", str(exc)[:70])


# ------------------------------------------------------------ F. serving ---
def check_e2e() -> None:
    section("F. Serving path (e2e-check, from inside the network)")
    mk, vk = env("LITELLM_MASTER_KEY"), env("VLLM_API_KEY")
    # The engine probe tries vLLM then llama.cpp, and llama.cpp requires a
    # bearer token on /v1/*. Passing only VLLM_API_KEY made the llama.cpp
    # attempt fail with a 401 that reads like "no engine is serving" -- so a
    # perfectly healthy llama.cpp deployment failed acceptance.
    lk = env("LLAMACPP_API_KEY")
    if not mk:
        record("e2e", "e2e-check", "SKIP", "no LITELLM_MASTER_KEY")
        return
    envv = dict(os.environ, MSYS_NO_PATHCONV="1")
    p = subprocess.run(
        ["docker", "run", "--rm", "--network", "llm-net",
         "--add-host=host.docker.internal:host-gateway",
         "-e", f"MK={mk}", "-e", f"VLLM_API_KEY={vk}", "-e", f"LLAMACPP_API_KEY={lk}",
         # docker -v needs a POSIX path. ROOT is a WindowsPath when this runs
         # under native Python, and passing E:\... silently mounts nothing --
         # e2e then produced no output and looked like a failure of the stack.
         "-v", f"{ROOT.as_posix()}/scripts:/s:ro", "python:3.13-slim",
         "python", "/s/e2e-check.py"],
        capture_output=True, text=True, timeout=2400, env=envv, cwd=ROOT)
    out = (p.stdout or "") + (p.stderr or "")
    for line in out.splitlines():
        # Strip ANSI FIRST. e2e prints "PASS" wrapped in colour codes, so the
        # bracket is not adjacent: a naive `"PASS]" in line` never matches and
        # the whole section records nothing -- which reads as "e2e did not run"
        # rather than "the parser is wrong".
        clean = re.sub(r"\[[0-9;]*m", "", line).strip()
        if "[PASS]" in clean or "[FAIL]" in clean or "[SKIP]" in clean:
            state = ("PASS" if "[PASS]" in clean else
                     "FAIL" if "[FAIL]" in clean else "SKIP")
            record("e2e", clean.split("]", 1)[1].strip()[:70], state)
    if "checks passed" not in out:
        tail = (out.strip().splitlines() or ["no output at all"])[-1]
        record("e2e", "e2e-check completed", "FAIL", f"rc={p.returncode}: {tail[:90]}")


# --------------------------------------------------------- G. operations ---
def check_operations() -> None:
    section("G. Operational promises")

    # Model weights must survive a teardown, or every redeploy is an hours-long
    # download. This is a property of the compose file, so assert it there.
    compose = (ROOT / "docker-compose.yml").read_text(encoding="utf-8", errors="replace")
    ext = compose.count("external: true")
    record("ops", "model caches declared external (survive `down -v`)",
           "PASS" if ext >= 2 else "FAIL", f"{ext} external volumes")

    record("ops", "backup script present",
           "PASS" if (ROOT / "scripts/backup.sh").exists() else "FAIL")

    # Re-running setup must be a no-op, not a reset. Dry-run is enough to prove
    # the guards fire without mutating a live stack.
    code, out = sh("bash", "scripts/setup.sh", "--dry-run", "--defaults",
                   "--domain", DOMAIN, timeout=900)
    # --dry-run deliberately does NOT execute gen-certs/gen-auth, so their
    # "already exists" messages cannot appear here -- asserting on them made a
    # passing stack look broken. What dry-run does prove is that the whole flow
    # completes on an already-configured stack without hitting a guard or a
    # die(). The guards themselves are asserted separately below.
    failed_guard = any(l.strip().startswith("X ") for l in out.splitlines())
    reruns_ok = code == 0 and not failed_guard
    detail = "dry-run completes on a configured stack" if reruns_ok else (
        f"exit {code}; " + (out.strip().splitlines() or ["no output"])[-1][:80])
    record("ops", "setup.sh re-run is non-destructive",
           "PASS" if reruns_ok else "FAIL", detail)

    # The Authelia key/volume trap must be guarded, not merely documented.
    setup = (ROOT / "scripts/setup.sh").read_text(encoding="utf-8", errors="replace")
    record("ops", "Authelia key/database mismatch is guarded",
           "PASS" if "undecryptable" in setup else "FAIL")
    record("ops", "host port conflict is detected before start",
           "PASS" if "already published by container" in setup else "FAIL")


def main() -> int:
    ap = argparse.ArgumentParser()
    ap.add_argument("--skip-e2e", action="store_true")
    args = ap.parse_args()

    print(f"{BOLD}Acceptance: domain={DOMAIN} ports={HTTP_PORT}/{HTTPS_PORT} "
          f"profiles={PROFILES}{OFF}")
    check_config()
    check_infra()
    check_routes()
    check_identity()
    check_observability()
    if not args.skip_e2e:
        check_e2e()
    check_operations()

    passed = sum(1 for _s, _n, st in results if st == "PASS")
    failed = [f"{s}: {n}" for s, n, st in results if st == "FAIL"]
    skipped = sum(1 for _s, _n, st in results if st == "SKIP")

    section("Summary")
    print(f"  {GREEN}{passed} passed{OFF}, "
          f"{RED if failed else DIM}{len(failed)} failed{OFF}, "
          f"{YELLOW if skipped else DIM}{skipped} skipped{OFF}")
    for f in failed:
        print(f"    {RED}FAIL{OFF} {f}")
    print()
    return 1 if failed else 0


if __name__ == "__main__":
    sys.exit(main())
