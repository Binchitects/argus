#!/usr/bin/env python3
"""
Everything a person does with the stack, done for real, as two people.

acceptance.py proves the stack is wired: routes answer, discovery documents
exist, datasources are healthy. It does not prove the things people actually
do work -- that someone an admin creates in the app can sign in, that their
key reaches the model, that a budget really stops them, that a rotated key
really dies, that a non-admin cannot reach the admin area or the metrics,
that Open WebUI signs them in with the right role, that the dashboards, logs
and alerts answer the admin and nobody else, that a chat in Open WebUI is
billed to the person who typed it.

This does each of those through the public HTTPS endpoints with real logins,
as the admin and as a brand-new person it creates, and removes that person at
the end. Standard library only.

    python3 scripts/functional-test.py            # reads .env for domain and admin password
    python3 scripts/functional-test.py --keep     # leave the test person in place

Exit code is the number of failed checks.
"""

import argparse
from datetime import datetime, timedelta, timezone
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
# The picture model, when the image profile is on (Admin -> Tools, image generation).
IMAGE = env("IMAGEGEN_MODEL_NAME") if "image" in env("COMPOSE_PROFILES").split(",") else ""
LOCAL = {}  # models added in Admin -> Models: name -> who may use it
PORT = env("TRAEFIK_HTTPS_PORT", "443")
SUFFIX = "" if PORT == "443" else f":{PORT}"
CTX = ssl.create_default_context(cafile=str(ROOT / "config/traefik/certs/tls.crt"))
RESULTS = []
WEBUI = {}  # the test person's Open WebUI id and an admin token, for cleanup


def u(host, path="/"):
    """host "" is the app itself, at the bare domain."""
    return f"https://{host + '.' if host else ''}{DOM}{SUFFIX}{path}"


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
        code, body = self.app("POST", "/api/auth/login", {"userName": username, "password": password})
        return code == 200 and body.get("status") == "ok"

    def app(self, method, path, data=None):
        """A call to the app's API as this browser: (status, parsed JSON or {})."""
        code, body, _, _ = self.req(u("", path), data if data is not None or method == "GET" else {},
                                    method=method, headers={"X-Requested-With": "functional-test", "Accept": "application/json"})
        try:
            return code, json.loads(body) if body else {}
        except json.JSONDecodeError:
            return code, {}


def api_key_call(key, path="/v1/models", payload=None, timeout=300):
    b = Browser()
    return b.req(u("gateway", path), payload, headers={"Authorization": f"Bearer {key}"}, timeout=timeout)[:2]


def listed_by(key):
    code, body = api_key_call(key)
    return [m.get("id") for m in json.loads(body).get("data", [])] if code == 200 else []


def wait_listed(key, model, want, seconds=45):
    """Whether the key's model list (kept by the app's key sync) comes to include, or not, the model."""
    deadline = time.time() + seconds
    while time.time() < deadline:
        if (model in listed_by(key)) == want:
            return True
        time.sleep(1.5)
    return False


def chat(key, text="Reply with the single word: pong", max_tokens=400):
    return api_key_call(key, "/v1/chat/completions",
                        {"model": MODEL, "messages": [{"role": "user", "content": text}],
                         "max_tokens": max_tokens})


def app_chat(browser, text="Reply with the single word: pong"):
    """One question in the app's chat, as the signed-in person: ("answered" | the error sentence, conversation id)."""
    code, conv = browser.app("POST", "/api/chat/conversations", {"thinking": "off", "useArgus": False})
    if code != 201:
        return f"HTTP {code}", None
    code, body, _, _ = browser.req(u("", f"/api/chat/conversations/{conv['id']}/messages"), {"content": text},
                                   headers={"X-Requested-With": "functional-test"}, timeout=300)
    events = [json.loads(l[6:]) for l in body.split("\n") if l.startswith("data: ")] if code == 200 else []
    last = next((e for e in reversed(events) if e.get("type") in ("done", "error")), None)
    if last is None:
        return f"HTTP {code}", conv["id"]
    return ("answered" if last["type"] == "done" else last["message"]), conv["id"]


def litellm(path, payload=None, method=None):
    b = Browser()
    code, body, _, _ = b.req(u("gateway", path), payload, method=method,
                             headers={"Authorization": "Bearer " + env("LITELLM_MASTER_KEY")})
    try:
        return code, json.loads(body)
    except json.JSONDecodeError:
        return code, body


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

    admin_pw = env("ADMIN_PASSWORD") or env("AUTHELIA_ADMIN_PASSWORD")
    who = f"ft{secrets.token_hex(3)}"
    email = f"{who}@example.test"
    print(f"domain {DOM}, test person {who} <{email}>\n")

    # ------------------------------------------------------------------ admin
    print("1. Admin signs in to the app and manages people")
    admin = Browser()
    rec("admin", "admin signs in at the app", admin.login("admin", admin_pw))
    code, me = admin.app("GET", "/api/auth/me")
    rec("admin", "the app knows them as an admin", code == 200 and me.get("isAdmin") is True, f"HTTP {code}")
    code, people = admin.app("GET", "/api/admin/people")
    rec("admin", "admin lists people", code == 200 and any(p.get("userName") == "admin" for p in people.get("people", [])), f"HTTP {code}")
    code, model = admin.app("GET", "/api/admin/model")
    samples = model.get("samples", []) if code == 200 else []
    shipped = len(list((ROOT / "env-samples").glob("*.env")))
    rec("admin", "the Model page lists every env-sample with its MODEL block",
        len(samples) == shipped and all(x.get("block", "").startswith("# >>> MODEL") for x in samples),
        f"{len(samples)} of {shipped} samples")
    rec("admin", "the Model page names the running model", model.get("running", {}).get("name") == MODEL, MODEL)
    code, models = admin.app("GET", "/api/admin/models")
    rows = models.get("models", []) if code == 200 else []
    LOCAL.update({m["name"]: (m.get("access") or {}).get("audience") for m in rows if m.get("source") == "local"})
    env_row = next((m for m in rows if m.get("source") == "env"), {})
    rec("admin", "the Models page has the .env model, loaded",
        env_row.get("name") == MODEL and env_row.get("status") in ("loaded", None), f"HTTP {code} {env_row.get('status')}")
    # A stack that has just started gets two minutes: on a from-zero start Loki
    # is busy for a while taking (and refusing) every container's old log lines.
    for attempt in range(25):
        code, overview = admin.app("GET", "/api/admin/overview")
        down = [x["name"] for x in overview.get("services", []) if not x.get("ok")]
        if code == 200 and not down:
            break
        time.sleep(5)
    rec("admin", "the Overview reaches the gateway, Prometheus, Alertmanager and Loki", code == 200 and not down,
        f"down: {down}" if down else (f"after {attempt * 5} s" if attempt else ""))
    code = admin.req(u("admin", "/model"), follow=False)[0]
    loc = admin.req(u("admin", "/model"), follow=False)[2].get("Location", "")
    rec("admin", "the old admin panel address sends bookmarks to the app", code == 302 and loc.endswith("/admin/model"), f"HTTP {code} {loc}")

    code, made = admin.app("POST", "/api/admin/people", {"userName": who, "email": email, "budget": 5})
    pw, key, pid = made.get("password") or "", made.get("apiKey") or "", made.get("id")
    rec("admin", "create person returns a password and an API key", bool(pw and key), f"HTTP {code} {made.get('warning') or ''}")
    code, again = admin.app("GET", f"/api/admin/people/{pid}")
    rec("admin", "the one-time secrets cannot be shown twice", code == 200 and key not in json.dumps(again) and pw not in json.dumps(again))
    if not (pw and key):
        return finish(admin, pid, email, args.keep)

    # ------------------------------------------------------------- new person
    print("\n2. The new person signs in and is NOT an admin")
    person = Browser()
    rec("person", "the new person signs in with the shown password", person.login(who, pw))
    code, own = person.app("GET", "/api/account/keys")
    rec("person", "the app shows them their own key and credit", code == 200 and len(own.get("keys", [])) == 1 and own.get("budget") == 5,
        f"HTTP {code}")
    code, me = person.app("GET", "/api/auth/me")
    rec("person", "the app knows them as a member", me.get("isAdmin") is False, f"HTTP {code}")
    code = person.app("GET", "/api/admin/people")[0]
    rec("person", "the people list is refused", code == 403, f"HTTP {code}")
    for path in ("/api/admin/overview", "/api/admin/model", "/api/admin/settings", "/api/admin/argus/status",
                 "/api/dashboards/usage-by-user"):
        code = person.app("GET", path)[0]
        rec("person", f"GET {path} is refused", code == 403, f"HTTP {code}")
    for method, path, body in (("POST", "/api/admin/people", {"userName": "x" + who, "email": "x" + email}),
                               ("POST", f"/api/admin/people/{pid}/key", {}),
                               ("PUT", f"/api/admin/people/{pid}/budget", {"budget": 1000}),
                               ("PATCH", f"/api/admin/people/{pid}", {"admin": True}),
                               ("POST", "/api/admin/argus/index", {"branches": []})):
        code = person.app(method, path, body)[0]
        rec("person", f"{method} {path.replace(str(pid), '<self>')} is refused", code == 403, f"HTTP {code}")
    for host in ("metrics", "alerts"):
        code = person.req(u(host, "/-/healthy"), follow=False)[0]
        rec("person", f"{host} is refused to a non-admin", code in (401, 403), f"HTTP {code}")
        code = admin.req(u(host, "/-/healthy"), follow=False)[0]
        rec("admin", f"{host} is open to the admin", code == 200, f"HTTP {code}")

    # ----------------------------------------------------------------- API key
    print("\n3. The person's API key")
    listed = listed_by(key)
    # The .env model, the picture model, and the models added in Admin -> Models
    # that everyone may use -- by their real names, nothing else.
    expected = [MODEL] + ([IMAGE] if IMAGE else []) + [n for n, a in LOCAL.items() if a == "Everyone"]
    rec("key", "the gateway lists the models the person may use, by their real names", sorted(listed) == sorted(expected), f"{listed}")
    code, body = chat(key)
    content = ""
    if code == 200:
        msg = json.loads(body)["choices"][0]["message"]
        content = (msg.get("content") or "") + (msg.get("reasoning_content") or "")
    rec("key", "key gets a completion from the model", code == 200 and bool(content.strip()), f"HTTP {code}")
    code, _ = api_key_call("sk-not-a-real-key")
    rec("key", "an invalid key is refused", code in (400, 401, 403), f"HTTP {code}")
    # Fair use: a key has at most Chat:ApiRequestsPerKey requests at once (2 by
    # default); a third at the same time is refused, and nothing else is.
    from concurrent.futures import ThreadPoolExecutor
    with ThreadPoolExecutor(3) as pool:
        codes = sorted(c for c, _ in pool.map(lambda _: chat(key, "Count from 1 to 60, one number per line.", max_tokens=200), range(3)))
    rec("key", "a key's third request at once is refused (429), the first two answered", codes == [200, 200, 429], f"{codes}")
    # Access per model (Admin -> Models): given to admins only, a model leaves
    # the person's key (the app keeps each key's model list at the gateway) and
    # is refused; given back, it returns.
    if IMAGE:
        def rule(audience):
            return admin.app("PUT", f"/api/admin/models/{urllib.parse.quote(IMAGE, safe='')}/access", {"audience": audience, "groups": []})[0]
        try:
            code = rule("Admins")
            rec("key", "a model given to admins only leaves the person's key", code == 204 and wait_listed(key, IMAGE, False), f"HTTP {code}")
            code, _ = api_key_call(key, "/v1/images/generations", {"model": IMAGE, "prompt": "a red square", "size": "256x256", "n": 1})
            rec("key", "and a call to it is refused", code in (401, 403), f"HTTP {code}")
        finally:
            code = rule("Everyone")
        rec("key", "given back to everyone, it is listed again", code == 204 and wait_listed(key, IMAGE, True), f"HTTP {code}")
    code, _ = Browser().req(u("gateway", "/v1/models"))[:2]
    rec("key", "no key is refused", code in (400, 401, 403), f"HTTP {code}")

    # ------------------------------------------------------------ SSO + roles
    print("\n4. Single sign-on and roles")
    # The dashboards, logs and alerts are in the app (Grafana is gone): the admin's, nobody else's.
    code, boards = admin.app("GET", "/api/dashboards/")
    rec("obs", "the app lists every dashboard", code == 200 and len(boards) >= 10, f"HTTP {code}, {len(boards) if code == 200 else 0} dashboards")
    now = time.time()
    span = {"from": datetime.fromtimestamp(now - 3600, timezone.utc).isoformat(), "to": datetime.fromtimestamp(now, timezone.utc).isoformat()}
    code, up = admin.app("POST", "/api/dashboards/stack-health/panels/0/query", span)
    series = [x for t in (up.get("results") or []) for x in (t.get("series") or [])] if code == 200 else []
    rec("obs", "a Prometheus panel answers with data (targets up)", bool(series) and all(not t.get("error") for t in up["results"]), f"HTTP {code}, {len(series)} series")
    code, logs = admin.app("GET", "/api/admin/logs/?limit=5")
    rec("obs", "the Logs page reads Loki", code == 200 and len(logs.get("lines", [])) > 0, f"HTTP {code} {logs.get('error', '')}")
    code, alerts = admin.app("GET", "/api/admin/alerts/")
    errors = alerts.get("errors") or {}
    rec("obs", "the Alerts page reads Alertmanager and every rule", code == 200 and len(alerts.get("rules") or []) > 0 and not any(errors.values()),
        f"HTTP {code}, {len(alerts.get('rules') or [])} rules, {errors}")
    refused = [p for p in ("/api/dashboards/", "/api/admin/logs/", "/api/admin/alerts/") if person.app("GET", p)[0] != 403]
    rec("obs", "a person cannot read the dashboards, logs or alerts", not refused, f"not refused: {refused}")

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
        # The served model, plus one per-chat thinking preset per THINKING_PRESETS entry
        # (services/seed-presets.py) -- and nothing else: no aliases, no Arena Model.
        presets = [lvl.split(":")[0].strip().lower() for lvl in (env("THINKING_PRESETS") or "").split(",") if lvl.strip()] \
            if "llamacpp" in env("COMPOSE_PROFILES") else []
        expected = [MODEL] + [f"{MODEL}-think-{lvl}".replace("/", "-").lower() for lvl in presets] + ([IMAGE] if IMAGE else []) + list(LOCAL)
        rec("sso", "Open WebUI lists the stack's models and the thinking presets, nothing else", sorted(models) == sorted(expected), ", ".join(models))
        model = MODEL
        code, body, _, _ = person.req(u("chat", "/api/chat/completions"),
                                      {"model": model, "stream": False, "max_tokens": 400,
                                       "messages": [{"role": "user", "content": "Reply with the single word: pong"}]},
                                      headers=hdr, timeout=300)
        webui_ok = code == 200 and bool(json.loads(body)["choices"][0]["message"].get("content", "").strip()) \
            if code == 200 else False
        rec("sso", "a chat in Open WebUI gets an answer", webui_ok, f"HTTP {code}")

    result, conv_id = app_chat(person)
    rec("chat", "a chat in the app gets an answer", result == "answered", result)
    code, conv = person.app("GET", f"/api/chat/conversations/{conv_id}") if conv_id else (0, {})
    answer = next((m for m in reversed(conv.get("messages", [])) if m.get("role") == "assistant"), {})
    rec("chat", "the app saves the chat with its answer and token counts",
        bool(answer.get("content")) and (answer.get("completionTokens") or 0) > 0, f"HTTP {code}")

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
    # Every logged request must be priced exactly as .env says: cache-miss input,
    # cache-hit input and output at their own per-1M-token prices.
    price = {k: float(env(k) or d) / 1e6 for k, d in (("PRICE_INPUT_PER_MTOK", "0.20"),
             ("PRICE_CACHED_INPUT_PER_MTOK", "0.02"), ("PRICE_OUTPUT_PER_MTOK", "0.80"))}
    wrong = []
    for r in rows:
        usage = ((r.get("metadata") or {}).get("usage_object") or {})
        cached = int(((usage.get("prompt_tokens_details") or {}).get("cached_tokens")) or 0)
        prompt, out = int(r.get("prompt_tokens") or 0), int(r.get("completion_tokens") or 0)
        expected = (prompt - cached) * price["PRICE_INPUT_PER_MTOK"] + cached * price["PRICE_CACHED_INPUT_PER_MTOK"] \
            + out * price["PRICE_OUTPUT_PER_MTOK"]
        if prompt and abs(float(r.get("spend") or 0) - expected) > 1e-9:
            wrong.append((prompt, cached, out, r.get("spend"), expected))
    rec("usage", "each request is priced from .env: cache miss, cache hit, output", bool(rows) and not wrong,
        f"{len(rows)} checked" if not wrong else f"mismatch {wrong[:1]}")
    start = (datetime.now(timezone.utc) - timedelta(hours=1)).isoformat()
    end = (datetime.now(timezone.utc) + timedelta(minutes=5)).isoformat()
    code, mine = person.app("GET", "/api/usage/me?" + urllib.parse.urlencode({"from": start, "to": end}))
    t = mine.get("totals", {}) if code == 200 else {}
    mine_cost = float(t.get("cost") or 0)
    logged_cost = sum(float(r.get("spend") or 0) for r in rows)
    rec("usage", "the person's own usage page shows their requests and cost", code == 200 and (t.get("requests") or 0) >= len(rows) > 0
        and mine_cost >= logged_cost - 1e-9, f"{t.get('requests')} requests, ${mine_cost:.6f} (logged ${logged_cost:.6f})")
    rows_chat = []
    for _ in range(24):
        code, logs = litellm("/spend/logs?summarize=false")
        rows_chat = [r for r in (logs if isinstance(logs, list) else [])
                     if r.get("end_user") == email and (r.get("metadata") or {}).get("user_api_key_alias") == "chat"]
        if rows_chat:
            break
        time.sleep(5)
    rec("usage", "the app's chat is billed to the person, under its own key (not the master key)", bool(rows_chat),
        f"{len(rows_chat)} rows with key alias 'chat' and end_user={email}")
    if webui_ok:
        code, logs = litellm("/spend/logs?summarize=false")
        chat_rows = [r for r in (logs if isinstance(logs, list) else []) if r.get("end_user") == email]
        rec("usage", "the Open WebUI chat is billed to the person, not the shared key", bool(chat_rows),
            f"{len(chat_rows)} rows with end_user={email}")

    # ----------------------------------------------------------------- budget
    print("\n6. Credit limits bind")
    admin.app("PUT", f"/api/admin/people/{pid}/budget", {"budget": 0})
    code_key = None
    for _ in range(30):
        code_key, body = chat(key, max_tokens=64)
        if code_key != 200:
            break
        time.sleep(4)
    rec("budget", "at credit 0 the person's key is refused", code_key in (400, 401, 403, 429),
        f"HTTP {code_key}: {body[:90] if code_key != 200 else 'still answering'}")
    # The chat path's limit is cached by the gateway for about a minute.
    chat_result = "not tried"
    for _ in range(25):
        chat_result, _ = app_chat(person)
        if chat_result != "answered":
            break
        time.sleep(6)
    rec("budget", "at credit 0 the app's chat is refused, and says why", "credit" in chat_result, chat_result[:90])
    admin.app("PUT", f"/api/admin/people/{pid}/budget", {"budget": 5})
    code_back = None
    for _ in range(30):
        code_back, _ = chat(key, max_tokens=64)
        if code_back == 200:
            break
        time.sleep(4)
    rec("budget", "raising the credit again restores access", code_back == 200, f"HTTP {code_back}")
    for _ in range(25):
        chat_result, _ = app_chat(person)
        if chat_result == "answered":
            break
        time.sleep(6)
    rec("budget", "raising the credit again restores the app's chat", chat_result == "answered", chat_result[:90])

    # --------------------------------------------------------------- rotation
    print("\n7. Key rotation and password resets")
    code, rotated = admin.app("POST", f"/api/admin/people/{pid}/key")
    new_key = rotated.get("apiKey") or ""
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

    code, reset = admin.app("POST", f"/api/admin/people/{pid}/password")
    pw2 = reset.get("password") or ""
    rec("reset", "reset returns a new password", bool(pw2), f"HTTP {code}")
    rec("reset", "the old password no longer signs in", not Browser().login(who, pw))
    rec("reset", "the new password signs in", Browser().login(who, pw2))

    person = Browser()
    person.login(who, pw2)
    pw3 = "functional " + " ".join(secrets.token_hex(3) for _ in range(4))
    code, _ = person.app("POST", "/api/account/password", {"current": pw2, "next": pw3})
    rec("reset", "a person can change their own password", code == 204 and Browser().login(who, pw3), f"HTTP {code}")
    code, answer = person.app("POST", "/api/account/password", {"current": "wrong-password", "next": "another " + pw3})
    rec("reset", "changing it needs the current password", answer.get("status") == "incorrect", f"HTTP {code}")

    return finish(admin, pid, email, args.keep)


def finish(admin, pid, email, keep):
    if not keep:
        print("\ncleanup")
        # Through the app: the person, their gateway user and their keys go together.
        app_code = admin.app("DELETE", f"/api/admin/people/{pid}")[0] if pid else None
        # Belt and braces for a run that stopped before the person existed in the app.
        codes = [litellm("/user/delete", {"user_ids": [email]})[0], litellm("/end_user/delete", {"user_ids": [email]})[0]]
        # And the Open WebUI account its SSO sign-in created. Every run used to
        # leave one behind, which piled up as fake people in the chat admin.
        webui = None
        if WEBUI.get("person_id") and WEBUI.get("admin_token"):
            webui = Browser().req(u("chat", f"/api/v1/users/{WEBUI['person_id']}"), method="DELETE",
                                  headers={"Authorization": f"Bearer {WEBUI['admin_token']}"})[0]
        print(f"  removed {email}: app {app_code}, litellm {codes}, open-webui {webui}")
    failed = [r for r in RESULTS if not r[2]]
    print(f"\n{len(RESULTS) - len(failed)}/{len(RESULTS)} checks passed")
    for area, name, _, detail in failed:
        print(f"  FAILED [{area}] {name} {detail}")
    return len(failed)


if __name__ == "__main__":
    sys.exit(main())
