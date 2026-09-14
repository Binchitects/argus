#!/usr/bin/env python3
"""
Everything a person does with the stack, done for real, as two people.

acceptance.py proves the stack is wired: routes answer, discovery documents
exist, datasources are healthy. It does not prove the things people actually
do work -- that someone an admin creates in the panel can sign in, that their
key reaches the model, that a budget really stops them, that a rotated key
really dies, that a non-admin cannot reach the admin console or the metrics,
that Grafana and Open WebUI sign them in with the right role, that a chat in
Open WebUI is billed to the person who typed it.

This does each of those through the public HTTPS endpoints with real logins,
as the admin and as a brand-new person it creates, and removes that person at
the end. Standard library only.

    python3 scripts/functional-test.py            # reads .env for domain and admin password
    python3 scripts/functional-test.py --keep     # leave the test person in place

Exit code is the number of failed checks.
"""

import argparse
import base64
import http.cookiejar
import json
import os
import re
import secrets
import ssl
import subprocess
import sys
import time
import urllib.error
import urllib.parse
import urllib.request
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent


def env(key, default=""):
    for line in (ROOT / ".env").read_text(encoding="utf-8").splitlines():
        if line.startswith(key + "="):
            return line.split("=", 1)[1].strip()
    return default


DOM = env("LLM_DOMAIN", "llm.localhost")
MODEL = env("MODEL_NAME")
PORT = env("TRAEFIK_HTTPS_PORT", "443")
SUFFIX = "" if PORT == "443" else f":{PORT}"
CTX = ssl.create_default_context(cafile=str(ROOT / "config/traefik/certs/tls.crt"))
RESULTS = []
WEBUI = {}  # the test person's Open WebUI id and an admin token, for cleanup


def u(host, path="/"):
    return f"https://{host}.{DOM}{SUFFIX}{path}"


def rec(area, name, ok, detail=""):
    RESULTS.append((area, name, ok, detail))
    mark = "\033[32mPASS\033[0m" if ok else "\033[31mFAIL\033[0m"
    print(f"  {mark}  {name}" + (f"  ({detail})" if detail else ""), flush=True)
    return ok


class NoRedirect(urllib.request.HTTPRedirectHandler):
    def redirect_request(self, *a, **k):
        return None


class Browser:
    """A cookie jar plus a TLS context: enough to be a person in a browser."""

    def __init__(self):
        self.jar = http.cookiejar.CookieJar()
        https = urllib.request.HTTPSHandler(context=CTX)
        cookies = urllib.request.HTTPCookieProcessor(self.jar)
        self.follow = urllib.request.build_opener(https, cookies)
        self.nofollow = urllib.request.build_opener(https, cookies, NoRedirect)

    def req(self, url, data=None, headers=None, method=None, follow=True, form=False, timeout=120):
        body = None
        hdrs = dict(headers or {})
        if data is not None:
            if form:
                body = urllib.parse.urlencode(data).encode()
                hdrs.setdefault("Content-Type", "application/x-www-form-urlencoded")
            else:
                body = json.dumps(data).encode()
                hdrs.setdefault("Content-Type", "application/json")
        r = urllib.request.Request(url, data=body, headers=hdrs, method=method)
        opener = self.follow if follow else self.nofollow
        try:
            with opener.open(r, timeout=timeout) as resp:
                return resp.status, resp.read().decode("utf-8", "replace"), resp.headers, resp.url
        except urllib.error.HTTPError as e:
            return e.code, e.read().decode("utf-8", "replace"), e.headers, url

    def cookie(self, name):
        return next((c.value for c in self.jar if c.name == name), None)

    def login(self, username, password):
        code, body, _, _ = self.req(u("auth", "/api/firstfactor"),
                                    {"username": username, "password": password, "keepMeLoggedIn": False})
        return code == 200 and '"status":"OK"' in body.replace(" ", "")


def api_key_call(key, path="/v1/models", payload=None, timeout=300):
    b = Browser()
    return b.req(u("gateway", path), payload, headers={"Authorization": f"Bearer {key}"}, timeout=timeout)[:2]


def chat(key, text="Reply with the single word: pong", max_tokens=400):
    return api_key_call(key, "/v1/chat/completions",
                        {"model": MODEL, "messages": [{"role": "user", "content": text}],
                         "max_tokens": max_tokens})


def litellm(path, payload=None, method=None):
    b = Browser()
    code, body, _, _ = b.req(u("gateway", path), payload, method=method,
                             headers={"Authorization": "Bearer " + env("LITELLM_MASTER_KEY")})
    try:
        return code, json.loads(body)
    except json.JSONDecodeError:
        return code, body


def panel_secret(browser, resp_url):
    """Follow the panel's show-once redirect and return the revealed text."""
    code, body, _, _ = browser.req(resp_url)
    m = re.search(r'class="msg[^"]*"[^>]*>(.*?)</div>', body, re.S)
    return re.sub(r"<[^>]+>", " ", m.group(1)) if m else body


def post_panel(browser, path, fields):
    """POST a form without following, and return (status, Location)."""
    code, body, headers, _ = browser.req(u("admin", path), fields, form=True, follow=False)
    return code, headers.get("Location", "") if headers else ""


def sso(browser, start_url, done_host):
    """Walk an OIDC login from the app's start URL to its callback, as a browser would."""
    url = start_url
    for _ in range(12):
        code, body, headers, _ = browser.req(url, follow=False)
        loc = headers.get("Location") if headers else None
        if code in (301, 302, 303, 307, 308) and loc:
            url = urllib.parse.urljoin(url, loc)
            continue
        return code, url, body
    return 0, url, "too many redirects"


def main():
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("--keep", action="store_true", help="do not delete the test person")
    args = ap.parse_args()

    admin_pw = env("AUTHELIA_ADMIN_PASSWORD")
    who = f"ft{secrets.token_hex(3)}"
    email = f"{who}@example.test"
    print(f"domain {DOM}, test person {who} <{email}>\n")

    # ------------------------------------------------------------------ admin
    print("1. Admin signs in and uses the panel")
    admin = Browser()
    rec("admin", "admin signs in at the portal", admin.login("admin", admin_pw))
    code, body, _, _ = admin.req(u("admin"))
    rec("admin", "panel console renders for an admin", code == 200 and "Add a person" in body, f"HTTP {code}")
    rec("admin", "admin sees the indexing card", "<h2>Indexing</h2>" in body)
    samples = len(re.findall(r"<details", body))
    shipped = len(list((ROOT / "env-samples").glob("*.env")))
    rec("admin", "admin sees the Model card with every env-sample", "<h2>Model</h2>" in body and samples == shipped,
        f"{samples} of {shipped} samples")
    rec("admin", "the Model card names the running model", f"<strong>{MODEL}</strong>" in body, MODEL)

    code, loc = post_panel(admin, "/admin/create", {"username": who, "email": email, "budget": "5"})
    shown = panel_secret(admin, urllib.parse.urljoin(u("admin"), loc)) if loc else ""
    pw = (re.search(r"password:\s*(\S+)", shown) or [None, ""])[1]
    key = (re.search(r"API key:\s*(sk-\S+)", shown) or [None, ""])[1]
    rec("admin", "create person returns a password and an API key", bool(pw and key), f"HTTP {code}")
    code, again = admin.req(urllib.parse.urljoin(u("admin"), loc))[:2] if loc else (0, "")
    rec("admin", "the one-time secret cannot be shown twice", key not in again)
    if not (pw and key):
        return finish(who, email, args.keep)

    # ------------------------------------------------------------- new person
    print("\n2. The new person signs in and is NOT an admin")
    person = Browser()
    ok = False
    for _ in range(20):                      # Authelia reloads users.yml via its file watcher
        if person.login(who, pw):
            ok = True
            break
        time.sleep(1)
    rec("person", "panel-created person can sign in with the shown password", ok)
    code, body, _, _ = person.req(u("admin"))
    rec("person", "panel shows them their own usage", code == 200 and "Your usage" in body, f"HTTP {code}")
    rec("person", "no admin console, no indexing card, no Model card",
        "Add a person" not in body and "<h2>Indexing</h2>" not in body and "<h2>Model</h2>" not in body)
    rec("person", "no other people listed", "admin@" not in body)
    for path, fields in (("/admin/create", {"username": "x" + who, "email": "x" + email}),
                         ("/admin/rotate", {"email": email}), ("/admin/reset", {"username": "admin"}),
                         ("/admin/budget", {"email": email, "budget": "1000"}),
                         ("/admin/index", {"branches": "main"})):
        code, _ = post_panel(person, path, fields)
        rec("person", f"POST {path} is refused", code == 403, f"HTTP {code}")
    for host in ("metrics", "alerts"):
        code = person.req(u(host, "/-/healthy"), follow=False)[0]
        rec("person", f"{host} is refused to a non-admin", code in (401, 403), f"HTTP {code}")
        code = admin.req(u(host, "/-/healthy"), follow=False)[0]
        rec("admin", f"{host} is open to the admin", code == 200, f"HTTP {code}")

    # ----------------------------------------------------------------- API key
    print("\n3. The person's API key")
    code, body = api_key_call(key)
    listed = [m.get("id") for m in json.loads(body).get("data", [])] if code == 200 else []
    rec("key", "the gateway lists exactly one model, by its real name", listed == [MODEL], f"{listed}")
    code, body = chat(key)
    content = ""
    if code == 200:
        msg = json.loads(body)["choices"][0]["message"]
        content = (msg.get("content") or "") + (msg.get("reasoning_content") or "")
    rec("key", "key gets a completion from the model", code == 200 and bool(content.strip()), f"HTTP {code}")
    code, _ = api_key_call("sk-not-a-real-key")
    rec("key", "an invalid key is refused", code in (400, 401, 403), f"HTTP {code}")
    code, _ = Browser().req(u("gateway", "/v1/models"))[:2]
    rec("key", "no key is refused", code in (400, 401, 403), f"HTTP {code}")

    # ------------------------------------------------------------ SSO + roles
    print("\n4. Single sign-on and roles")
    for label, b, want in (("admin", admin, "Admin"), ("person", person, "Viewer")):
        code, final, body = sso(b, u("grafana", "/login/generic_oauth"), "grafana")
        code2, me, _, _ = b.req(u("grafana", "/api/user"))
        role = ""
        if code2 == 200:
            c3, orgs, _, _ = b.req(u("grafana", "/api/user/orgs"))
            role = (json.loads(orgs)[0].get("role") if c3 == 200 and orgs.startswith("[") else "")
        rec("sso", f"Grafana signs in the {label} as {want}", role == want, f"role={role or 'none'}, HTTP {code2}")
    code, body, _, _ = admin.req(u("grafana", "/api/search?type=dash-db"))
    n = len(json.loads(body)) if code == 200 else 0
    rec("sso", "Grafana shows the provisioned dashboards", n >= 8, f"{n} dashboards")

    code, final, body = sso(person, u("chat", "/oauth/oidc/login"), "chat")
    token = person.cookie("token")
    rec("sso", "Open WebUI signs the person in", bool(token), f"landed on {urllib.parse.urlparse(final).path}")
    if token:
        code, body, _, _ = person.req(u("chat", "/api/v1/auths/"), headers={"Authorization": f"Bearer {token}"})
        me = json.loads(body) if code == 200 else {}
        WEBUI["person_id"] = me.get("id")
        rec("sso", "Open WebUI makes the person a user, not an admin", me.get("role") == "user", f"role={me.get('role')}")
    sso(admin, u("chat", "/oauth/oidc/login"), "chat")
    WEBUI["admin_token"] = admin.cookie("token")
    code, body, _, _ = admin.req(u("chat", "/api/v1/auths/"),
                                 headers={"Authorization": f"Bearer {WEBUI['admin_token']}"})
    role = json.loads(body).get("role") if code == 200 else None
    rec("sso", "Open WebUI makes a member of admins an admin", role == "admin", f"role={role}, HTTP {code}")
    # SSO only. An email that fails validation means an open signup answers 400
    # and creates nothing, so this probe cannot leave an account behind.
    anon = Browser()
    code = anon.req(u("chat", "/api/v1/auths/signin"), {"email": email, "password": "not-the-password"})[0]
    rec("sso", "Open WebUI refuses password sign-in (SSO only)", code == 403, f"HTTP {code}")
    code = anon.req(u("chat", "/api/v1/auths/signup"),
                    {"name": "probe", "email": "not-an-email", "password": secrets.token_urlsafe(16)})[0]
    rec("sso", "Open WebUI refuses self sign-up", code == 403, f"HTTP {code}")
    webui_ok = False
    if token:
        hdr = {"Authorization": f"Bearer {token}"}
        code, body, _, _ = person.req(u("chat", "/api/models"), headers=hdr)
        models = [m.get("id") for m in json.loads(body).get("data", [])] if code == 200 else []
        rec("sso", "Open WebUI lists the stack's models", bool(models), ", ".join(models[:4]))
        rec("sso", "Open WebUI lists only the real model (no aliases, no Arena Model)", models == [MODEL], ", ".join(models))
        model = MODEL
        code, body, _, _ = person.req(u("chat", "/api/chat/completions"),
                                      {"model": model, "stream": False, "max_tokens": 400,
                                       "messages": [{"role": "user", "content": "Reply with the single word: pong"}]},
                                      headers=hdr, timeout=300)
        webui_ok = code == 200 and bool(json.loads(body)["choices"][0]["message"].get("content", "").strip()) \
            if code == 200 else False
        rec("sso", "a chat in Open WebUI gets an answer", webui_ok, f"HTTP {code}")

    # ------------------------------------------------------------ attribution
    print("\n5. Usage is attributed to the person")
    seen = {}
    for _ in range(24):                      # LiteLLM writes spend logs in batches
        code, logs = litellm(f"/spend/logs?user_id={urllib.parse.quote(email)}")
        rows = logs if isinstance(logs, list) else []
        seen = {"key": any(r.get("api_key") and r.get("user") in ("", None, email) for r in rows),
                "chat": any((r.get("end_user") == email) or (r.get("metadata") or {}).get("user_api_key_alias", "").startswith("open-webui")
                            for r in rows)}
        if rows and (seen["chat"] or not webui_ok):
            break
        time.sleep(5)
    rec("usage", "API-key requests are logged under the person", bool(rows), f"{len(rows)} spend rows")
    if webui_ok:
        code, logs = litellm("/spend/logs?summarize=false")
        chat_rows = [r for r in (logs if isinstance(logs, list) else []) if r.get("end_user") == email]
        rec("usage", "the Open WebUI chat is billed to the person, not the shared key", bool(chat_rows),
            f"{len(chat_rows)} rows with end_user={email}")

    # ----------------------------------------------------------------- budget
    print("\n6. Credit limits bind")
    post_panel(admin, "/admin/budget", {"email": email, "budget": "0"})
    code_key = None
    for _ in range(30):
        code_key, body = chat(key, max_tokens=64)
        if code_key != 200:
            break
        time.sleep(4)
    rec("budget", "at credit 0 the person's key is refused", code_key in (400, 401, 403, 429),
        f"HTTP {code_key}: {body[:90] if code_key != 200 else 'still answering'}")
    post_panel(admin, "/admin/budget", {"email": email, "budget": "5"})
    code_back = None
    for _ in range(30):
        code_back, _ = chat(key, max_tokens=64)
        if code_back == 200:
            break
        time.sleep(4)
    rec("budget", "raising the credit again restores access", code_back == 200, f"HTTP {code_back}")

    # --------------------------------------------------------------- rotation
    print("\n7. Key rotation and password resets")
    code, loc = post_panel(admin, "/admin/rotate", {"email": email})
    shown = panel_secret(admin, urllib.parse.urljoin(u("admin"), loc)) if loc else ""
    new_key = (re.search(r"New API key:\s*(sk-\S+)", shown) or [None, ""])[1]
    rec("rotate", "rotate returns a new key", bool(new_key), f"HTTP {code}")
    old_code = None
    for _ in range(20):
        old_code, _ = api_key_call(key)
        if old_code != 200:
            break
        time.sleep(3)
    rec("rotate", "the old key stops working", old_code in (400, 401, 403), f"HTTP {old_code}")
    code, _ = api_key_call(new_key)
    rec("rotate", "the new key works", code == 200, f"HTTP {code}")

    code, loc = post_panel(admin, "/admin/reset", {"username": who})
    shown = panel_secret(admin, urllib.parse.urljoin(u("admin"), loc)) if loc else ""
    pw2 = (re.search(r"New password for \S+:\s*(\S+)", shown) or [None, ""])[1]
    rec("reset", "reset returns a new password", bool(pw2), f"HTTP {code}")
    time.sleep(3)
    rec("reset", "the old password no longer signs in", not Browser().login(who, pw))
    ok = any(Browser().login(who, pw2) or time.sleep(1) for _ in range(15))
    rec("reset", "the new password signs in", ok)

    person = Browser()
    person.login(who, pw2)
    pw3 = "Functional-" + secrets.token_urlsafe(12)
    code, loc = post_panel(person, "/password", {"current": pw2, "new": pw3})
    ok = any(Browser().login(who, pw3) or time.sleep(1) for _ in range(15))
    rec("reset", "a person can change their own password", ok, f"HTTP {code}")
    code, loc = post_panel(person, "/password", {"current": "wrong-password", "new": "Another-" + pw3})
    rec("reset", "changing it needs the current password", "incorrect" in urllib.parse.unquote(loc))

    return finish(who, email, args.keep)


def finish(who, email, keep):
    if not keep:
        print("\ncleanup")
        codes = []
        code, keys = litellm(f"/key/list?user_id={urllib.parse.quote(email)}&return_full_object=true")
        toks = [k.get("token") for k in (keys.get("keys", []) if isinstance(keys, dict) else []) if isinstance(k, dict)]
        if toks:
            codes.append(litellm("/key/delete", {"keys": toks})[0])
        codes.append(litellm("/user/delete", {"user_ids": [email]})[0])
        codes.append(litellm("/end_user/delete", {"user_ids": [email]})[0])
        # The panel has no delete action; remove the Authelia entry the same way
        # the panel writes it, through its own container.
        r = subprocess.run(["docker", "exec", "admin-panel", "python", "-c",
                            "import app; u=app.load_users(); u.pop(%r, None); app.save_users(u)" % who],
                           capture_output=True, text=True)
        # And the Open WebUI account its SSO sign-in created. Every run used to
        # leave one behind, which piled up as fake people in the chat admin.
        webui = None
        if WEBUI.get("person_id") and WEBUI.get("admin_token"):
            webui = Browser().req(u("chat", f"/api/v1/users/{WEBUI['person_id']}"), method="DELETE",
                                  headers={"Authorization": f"Bearer {WEBUI['admin_token']}"})[0]
        print(f"  removed {who}: litellm {codes}, authelia exit {r.returncode} {r.stderr.strip()[:120]}, "
              f"open-webui {webui}")
    failed = [r for r in RESULTS if not r[2]]
    print(f"\n{len(RESULTS) - len(failed)}/{len(RESULTS)} checks passed")
    for area, name, _, detail in failed:
        print(f"  FAILED [{area}] {name} {detail}")
    return len(failed)


if __name__ == "__main__":
    sys.exit(main())
