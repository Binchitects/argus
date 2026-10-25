#!/usr/bin/env python3
"""The compliance checks against the running stack (docs/compliance/README.md).

From outside, what a browser sees: TLS 1.2 or later and nothing older, the
security headers, the session cookie's flags, and the guard against
cross-site requests. Then the app's own checks (GET /api/admin/compliance),
signed in as the first admin with the password from .env, which is never
printed.

    python3 scripts/compliance-check.py            # exits 1 when a check fails
    python3 scripts/compliance-check.py --strict   # warnings and unknowns fail too
    python3 scripts/compliance-check.py --json     # the results as JSON

Each check is ok, warn (in part, or a choice to make knowingly), fail or
unknown (what it reads did not answer). The ids are those controls.yaml
names as live:ID and app:ID.
"""
import argparse
import http.cookiejar
import json
import socket
import ssl
import sys
import urllib.error
import urllib.request
import warnings
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent

# What this script checks from outside; the guard test reads this list.
LIVE_CHECKS = ("tls", "headers", "cookies", "csrf")

OK, WARN, FAIL, UNKNOWN = "ok", "warn", "fail", "unknown"


def env(key, default=""):
    path = ROOT / ".env"
    if not path.exists():
        return default
    for line in path.read_text(encoding="utf-8").splitlines():
        if line.startswith(key + "="):
            return line.split("=", 1)[1].strip()
    return default


class Stack:
    def __init__(self, domain, port):
        self.domain = domain
        self.port = int(port)
        self.base = f"https://{domain}" + ("" if self.port == 443 else f":{self.port}")
        # The stack's own CA is not in this machine's store unless it was added: what is checked here is
        # how the server speaks, not who signed its certificate (the app's certificate check says that).
        self.ctx = ssl.create_default_context() if env("ACME_EMAIL") else ssl._create_unverified_context()
        self.jar = http.cookiejar.CookieJar()
        self.open = urllib.request.build_opener(urllib.request.HTTPSHandler(context=self.ctx),
                                                urllib.request.HTTPCookieProcessor(self.jar))

    def req(self, method, path, body=None, headers=None):
        data = json.dumps(body).encode() if body is not None else None
        hdrs = {"Accept": "application/json", **({"Content-Type": "application/json"} if data else {}), **(headers or {})}
        r = urllib.request.Request(self.base + path, data=data, headers=hdrs, method=method)
        try:
            with self.open.open(r, timeout=60) as resp:
                return resp.status, resp.read().decode("utf-8", "replace"), resp.headers
        except urllib.error.HTTPError as e:
            return e.code, e.read().decode("utf-8", "replace"), e.headers


def handshake(stack, version):
    """The protocol and cipher the server agrees to at exactly this TLS version, or the reason it refused."""
    ctx = ssl.SSLContext(ssl.PROTOCOL_TLS_CLIENT)
    ctx.check_hostname = False
    ctx.verify_mode = ssl.CERT_NONE
    # Let this side offer the old versions at all, to see whether the server takes them.
    ctx.set_ciphers("DEFAULT:@SECLEVEL=0")
    with warnings.catch_warnings():
        warnings.simplefilter("ignore", DeprecationWarning)
        ctx.minimum_version = ctx.maximum_version = version
    try:
        with socket.create_connection((stack.domain, stack.port), timeout=10) as raw, \
                ctx.wrap_socket(raw, server_hostname=stack.domain) as tls:
            return True, f"{tls.version()} {tls.cipher()[0]}"
    except ssl.SSLError as e:
        return False, e.reason or str(e)


def check_tls(stack):
    with warnings.catch_warnings():
        warnings.simplefilter("ignore", DeprecationWarning)
        old = [("TLS 1.0", ssl.TLSVersion.TLSv1), ("TLS 1.1", ssl.TLSVersion.TLSv1_1)]
    taken, unknown = [], []
    for name, version in old:
        ok, said = handshake(stack, version)
        if ok:
            taken.append(name)
        elif "NO_PROTOCOLS_AVAILABLE" in said or "NO_CIPHERS_AVAILABLE" in said:
            unknown.append(name)
    good, said = handshake(stack, ssl.TLSVersion.TLSv1_2)
    best = ssl.SSLContext(ssl.PROTOCOL_TLS_CLIENT)
    best.check_hostname = False
    best.verify_mode = ssl.CERT_NONE
    with socket.create_connection((stack.domain, stack.port), timeout=10) as raw, best.wrap_socket(raw, server_hostname=stack.domain) as tls:
        newest = f"{tls.version()} {tls.cipher()[0]}"
    if taken:
        return FAIL, f"The server still takes {' and '.join(taken)}; it agrees to {newest}."
    if not good:
        return FAIL, f"TLS 1.2 is refused ({said}); it agrees to {newest}."
    if unknown:
        return UNKNOWN, f"This machine cannot offer {' and '.join(unknown)} to try them; the server agrees to {newest} and TLS 1.2 ({said})."
    return OK, f"TLS 1.0 and 1.1 refused; TLS 1.2 ({said}) and {newest}."


REQUIRED = {
    "Content-Security-Policy": lambda v: "frame-ancestors 'none'" in v and "script-src 'self'" in v,
    "X-Content-Type-Options": lambda v: v.lower() == "nosniff",
    "X-Frame-Options": lambda v: v.upper() == "DENY",
    "Referrer-Policy": lambda v: v.lower() in ("no-referrer", "same-origin", "strict-origin", "strict-origin-when-cross-origin"),
    "Permissions-Policy": lambda v: "camera=()" in v,
}


def check_headers(stack):
    wrong = []
    hsts = None
    for path in ("/", "/api/info"):
        code, _, headers = stack.req("GET", path)
        if code >= 500:
            return UNKNOWN, f"{path} answered {code}."
        for name, good in REQUIRED.items():
            value = headers.get(name)
            if value is None or not good(value):
                wrong.append(f"{path} {name}: {value or 'missing'}")
        hsts = hsts or headers.get("Strict-Transport-Security")
    if wrong:
        return FAIL, "; ".join(wrong)
    said = f"{', '.join(REQUIRED)} set on the pages and the API."
    if not hsts or "max-age=0" in hsts.replace(" ", ""):
        return WARN, said + " No HSTS (Strict-Transport-Security): with the stack's own CA it is left off so a browser can still be told to trust it."
    return OK, said + f" HSTS: {hsts}."


def sign_in(stack):
    """Signs the first admin in; the Set-Cookie of the session (None when that failed) and why not."""
    password = env("ADMIN_PASSWORD")
    if not password:
        return None, "ADMIN_PASSWORD is not in .env."
    code, body, headers = stack.req("POST", "/api/auth/login", {"userName": env("ADMIN_USERNAME", "admin"), "password": password},
                                    {"X-Requested-With": "compliance-check"})
    status = json.loads(body).get("status") if code == 200 and body else None
    if status == "2fa":
        return None, "The admin signs in with two factors: run it from a session that can (the app's checks are in Admin → Compliance)."
    if status != "ok":
        return None, f"The admin could not sign in (HTTP {code})."
    return next((c for c in headers.get_all("Set-Cookie") or [] if c.startswith("llm_session=")), ""), None


def check_cookies(cookie, problem):
    if cookie is None:
        return UNKNOWN, problem
    flags = [p.strip().lower() for p in cookie.split(";")[1:]]
    missing = [f for f in ("secure", "httponly") if f not in flags]
    same = next((f.split("=", 1)[1] for f in flags if f.startswith("samesite=")), None)
    if missing or same not in ("lax", "strict"):
        return FAIL, f"llm_session lacks {', '.join(missing) or 'SameSite=Lax or Strict'}."
    return OK, f"llm_session is Secure, HttpOnly and SameSite={same.capitalize()}."


def check_csrf(stack):
    code, body, _ = stack.req("POST", "/api/auth/logout", {})
    if code == 400 and "csrf" in body:
        return OK, "A state change without X-Requested-With is refused (HTTP 400)."
    return FAIL, f"A state change without X-Requested-With answered HTTP {code}."


def app_checks(stack, cookie, problem):
    if cookie is None:
        return [("app:checks", UNKNOWN, problem)]
    code, body, _ = stack.req("GET", "/api/admin/compliance")
    if code != 200:
        return [("app:checks", UNKNOWN, f"GET /api/admin/compliance answered HTTP {code}.")]
    return [(f"app:{c['id']}", c["state"], c["detail"]) for c in json.loads(body)["checks"]]


def main():
    p = argparse.ArgumentParser(description=__doc__.split("\n\n")[0])
    p.add_argument("--strict", action="store_true", help="warnings and unknowns fail too")
    p.add_argument("--json", action="store_true", help="print the results as JSON")
    p.add_argument("--domain", default=env("DOMAIN", "llm.localhost"))
    p.add_argument("--port", default=env("TRAEFIK_HTTPS_PORT", "443"))
    a = p.parse_args()

    stack = Stack(a.domain, a.port)
    results = []

    def run(id_, fn, *args):
        try:
            state, detail = fn(*args)
        except (OSError, ssl.SSLError) as e:
            state, detail = UNKNOWN, f"{stack.base} did not answer: {e}"
        results.append((f"live:{id_}", state, detail))

    run("tls", check_tls, stack)
    run("headers", check_headers, stack)
    run("csrf", check_csrf, stack)
    try:
        cookie, problem = sign_in(stack)
    except OSError as e:
        cookie, problem = None, f"{stack.base} did not answer: {e}"
    run("cookies", check_cookies, cookie, problem)
    try:
        results += app_checks(stack, cookie, problem)
    except OSError as e:
        results.append(("app:checks", UNKNOWN, f"{stack.base} did not answer: {e}"))
    if cookie is not None:
        stack.req("POST", "/api/auth/logout", {}, {"X-Requested-With": "compliance-check"})

    failing = {FAIL} | ({WARN, UNKNOWN} if a.strict else set())
    if a.json:
        print(json.dumps([{"id": i, "state": s, "detail": d} for i, s, d in results], indent=2))
    else:
        colour = {OK: "\033[32m", WARN: "\033[33m", FAIL: "\033[31m", UNKNOWN: "\033[90m"}
        print(f"Compliance checks of {stack.base}")
        for i, s, d in results:
            print(f"  {colour[s]}{s.upper():7}\033[0m {i:20} {d}")
        bad = [i for i, s, _ in results if s in failing]
        print(f"\n{len(results) - len(bad)} of {len(results)} pass" + (f"; failing: {', '.join(bad)}" if bad else "."))
    sys.exit(1 if any(s in failing for _, s, _ in results) else 0)


if __name__ == "__main__":
    main()
