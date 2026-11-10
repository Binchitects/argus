#!/usr/bin/env python3
"""
What people keep in an installation, written before an upgrade and looked for after it (tools/offline-e2e/run.sh).

    python3 -I data.py seed RECORD     as the admin: a person, a chat (answered when .env names a MODEL), a file
                                       attached to it, a key of the admin's own, and a setting changed; what was
                                       written goes in RECORD (JSON, 0600: it holds that key)
    python3 -I data.py check RECORD    each of them is there as it was written: exit 1 when one is not

It reads DOMAIN, HTTPS_PORT, ADMIN_PASSWORD and MODEL from the installation's .env (ARENA_DEPLOY, default
/srv/arena/deploy) and reaches the stack through HTTPS by its names (installer.sh --hosts wrote them). It prints no
secret. Standard library only.
"""

import hashlib
import http.cookiejar
import json
import os
import secrets
import ssl
import sys
import time
import urllib.error
import urllib.request
from pathlib import Path

DEPLOY = Path(os.environ.get("ARENA_DEPLOY", "/srv/arena/deploy"))
SETTING = "Branding:SignInHeadline"


def env(key, default=""):
    for line in (DEPLOY / ".env").read_text(encoding="utf-8").splitlines():
        if line.startswith(key + "="):
            return line.split("=", 1)[1].strip().strip('"')
    return default


DOMAIN = env("DOMAIN", "llm.localhost")
PORT = env("HTTPS_PORT", "443")
BASE = f"https://{DOMAIN}" + ("" if PORT == "443" else f":{PORT}")
# The stack's own CA, or Traefik's certificate: nothing here checks it (the test host's names point at itself).
CTX = ssl._create_unverified_context()


class Session:
    def __init__(self):
        self.opener = urllib.request.build_opener(urllib.request.HTTPSHandler(context=CTX),
                                                  urllib.request.HTTPCookieProcessor(http.cookiejar.CookieJar()))

    def call(self, method, path, body=None, raw=None, ctype=None, timeout=60):
        data = raw if raw is not None else (json.dumps(body).encode() if body is not None else None)
        hdrs = {"X-Requested-With": "e2e-data", "Accept": "application/json"}
        if data is not None:
            hdrs["Content-Type"] = ctype or "application/json"
        r = urllib.request.Request(BASE + path, data=data, headers=hdrs, method=method)
        try:
            with self.opener.open(r, timeout=timeout) as resp:
                return resp.status, resp.read()
        except urllib.error.HTTPError as e:
            return e.code, e.read()

    def json(self, method, path, body=None, **kw):
        code, data = self.call(method, path, body, **kw)
        try:
            return code, json.loads(data) if data else {}
        except json.JSONDecodeError:
            return code, {}


def signed_in():
    s = Session()
    # The app may still be warming up after a start: a minute for the sign-in to answer.
    for _ in range(30):
        code, body = s.json("POST", "/api/auth/login", {"userName": "admin", "password": env("ADMIN_PASSWORD")})
        if code == 200 and body.get("status") == "ok":
            return s
        if code not in (502, 503, 504):
            break
        time.sleep(2)
    sys.exit(f"data: the admin cannot sign in with .env's ADMIN_PASSWORD (HTTP {code})")


def api_answers(key):
    """The gateway lists the models for this key (the key sync may take a few seconds after it is made)."""
    url = f"https://gateway.{DOMAIN}" + ("" if PORT == "443" else f":{PORT}") + "/v1/models"
    opener = urllib.request.build_opener(urllib.request.HTTPSHandler(context=CTX))
    for _ in range(30):
        try:
            with opener.open(urllib.request.Request(url, headers={"Authorization": f"Bearer {key}"}), timeout=30) as resp:
                if resp.status == 200:
                    return True
        except (urllib.error.URLError, OSError):
            pass
        time.sleep(2)
    return False


def must(ok, what):
    if not ok:
        print(f"data: FAIL {what}", flush=True)
        sys.exit(1)


def setting_value(s):
    code, view = s.json("GET", "/api/admin/config")
    must(code == 200, f"the settings did not answer (HTTP {code})")
    for g in view.get("groups", []):
        for row in g.get("settings", []):
            if row.get("key") == SETTING:
                return row.get("value")
    return None


def seed(record):
    s = signed_in()
    tok = secrets.token_hex(4)
    code, person = s.json("POST", "/api/admin/people", {"userName": f"e2e-keeper-{tok}", "email": f"keeper-{tok}@example.com",
                                                       "credits": {"chat": 5, "api": 5}})
    must(code in (200, 201) and person.get("id"), f"a person was not made (HTTP {code})")
    code, conv = s.json("POST", "/api/chat/conversations", {"thinking": "off", "useCodeIntel": False})
    must(code in (200, 201) and conv.get("id"), f"a chat was not made (HTTP {code})")
    content = f"kept across the upgrade: {tok}\n".encode() * 64
    boundary = "e2e" + secrets.token_hex(8)
    form = (f"--{boundary}\r\nContent-Disposition: form-data; name=\"file\"; filename=\"kept-{tok}.txt\"\r\n"
            f"Content-Type: text/plain\r\n\r\n").encode() + content + f"\r\n--{boundary}--\r\n".encode()
    code, att = s.json("POST", "/api/chat/attachments", raw=form, ctype=f"multipart/form-data; boundary={boundary}")
    must(code == 200 and att.get("id"), f"a file was not attached (HTTP {code})")
    messages = 0
    if env("MODEL"):
        # Answered by the model (the stream ends with the answer): the chat holds the question and its answer.
        code, body = s.call("POST", f"/api/chat/conversations/{conv['id']}/messages",
                            {"content": "Reply with the single word: kept", "attachments": [att["id"]]}, timeout=600)
        events = [json.loads(l[6:]) for l in body.decode("utf-8", "replace").split("\n") if l.startswith("data: ")]
        last = next((e for e in reversed(events) if e.get("type") in ("done", "error")), {})
        must(code == 200 and last.get("type") == "done", f"the chat was not answered (HTTP {code}: {last.get('message', 'no end')})")
        code, got = s.json("GET", f"/api/chat/conversations/{conv['id']}")
        messages = len(got.get("messages") or [])
        must(messages >= 2, f"the chat holds {messages} message(s), not the question and its answer")
    # Named once answered: the first question names a chat that has no name of its own.
    title = f"e2e kept chat {tok}"
    code, _ = s.json("PATCH", f"/api/chat/conversations/{conv['id']}", {"title": title})
    must(code in (200, 204), f"the chat was not named (HTTP {code})")
    # A key of the admin's own, by its name: it must still open the API afterwards.
    code, own = s.json("POST", "/api/account/keys/own", {"name": f"e2e kept key {tok}"})
    must(code == 200 and own.get("apiKey"), f"a key of the admin's own was not made (HTTP {code})")
    must(api_answers(own["apiKey"]), "the admin's own key does not open the API")
    value = f"Kept across upgrades {tok}"
    code, _ = s.json("PUT", "/api/admin/config", {"changes": [{"key": SETTING, "value": value}]})
    must(code == 200, f"the setting was not saved (HTTP {code})")
    must(setting_value(s) == value, "the setting does not read back as saved")
    Path(record).write_text(json.dumps({
        "person": person["id"], "userName": f"e2e-keeper-{tok}", "chat": conv["id"], "title": title, "messages": messages,
        "file": att["id"], "sha256": hashlib.sha256(content).hexdigest(), "setting": value,
        "key": own["apiKey"], "keyName": f"e2e kept key {tok}",
    }), encoding="utf-8")
    Path(record).chmod(0o600)
    print(f"data: written: the person e2e-keeper-{tok}, the chat \"{title}\" ({messages} messages), a file of {len(content)} bytes, "
          f"a key of the admin's own, {SETTING}", flush=True)


def check(record):
    r = json.loads(Path(record).read_text(encoding="utf-8"))
    s = signed_in()
    code, person = s.json("GET", f"/api/admin/people/{r['person']}")
    must(code == 200 and (person.get("person") or {}).get("userName") == r["userName"], f"the person {r['userName']} is gone (HTTP {code})")
    code, conv = s.json("GET", f"/api/chat/conversations/{r['chat']}")
    must(code == 200 and conv.get("title") == r["title"], f"the chat \"{r['title']}\" is gone (HTTP {code})")
    messages = len(conv.get("messages") or [])
    must(messages >= r["messages"], f"the chat holds {messages} message(s), not {r['messages']}")
    code, data = s.call("GET", f"/api/chat/attachments/{r['file']}/content")
    must(code == 200 and hashlib.sha256(data).hexdigest() == r["sha256"], f"the file is gone or changed (HTTP {code})")
    got = setting_value(s)
    must(got == r["setting"], f"{SETTING} is \"{got}\", not as it was saved")
    code, keys = s.json("GET", "/api/account/keys/own")
    must(code == 200 and any(k.get("name") == r["keyName"] for k in keys.get("keys", [])), f"the key \"{r['keyName']}\" is not listed (HTTP {code})")
    must(api_answers(r["key"]), f"the key \"{r['keyName']}\" no longer opens the API")
    print(f"data: kept: the person {r['userName']}, the chat \"{r['title']}\" ({messages} messages), the file, the key "
          f"(it opens the API), {SETTING}", flush=True)


if __name__ == "__main__":
    if len(sys.argv) != 3 or sys.argv[1] not in ("seed", "check"):
        sys.exit("usage: data.py seed|check RECORD")
    (seed if sys.argv[1] == "seed" else check)(sys.argv[2])
