#!/usr/bin/env python3
"""
What restore-test.sh and rollback-test.sh check, through the app's API. Python's
standard library only; secrets come from the environment and are never printed.

    recovery-check.py facts BACKUP/postgres.sql.gz        what a backup holds: people, chats, settings, the schema (JSON)
    recovery-check.py wait --url URL [--version V]        until the app answers (at that version)
    recovery-check.py restored --url URL --facts FILE     the app shows what the backup holds
    recovery-check.py seed --url URL --state FILE --label L --setting KEY
    recovery-check.py verify --url URL --state FILE [--absent FILE]

--connect IP sends URL's host to IP (as curl --resolve): a throwaway stack on
127.0.0.1 answers for the deployment's own domain. The person who signs in is
--user (admin), with the password in RECOVERY_PASSWORD.
"""
from __future__ import annotations

import argparse
import gzip
import http.cookiejar
import json
import os
import re
import socket
import ssl
import sys
import time
import urllib.error
import urllib.parse
import urllib.request

GREEN, RED, DIM, OFF = ("\033[32m", "\033[31m", "\033[90m", "\033[0m") if sys.stdout.isatty() else ("", "", "", "")
results: list[tuple[str, bool]] = []


def check(name: str, ok: bool, detail: str = "") -> bool:
    print(f"  {GREEN + 'PASS' if ok else RED + 'FAIL'}{OFF}  {name}" + (f"  {DIM}({detail[:300]}){OFF}" if detail else ""), flush=True)
    results.append((name, ok))
    return ok


# ---------------------------------------------------------------- the backup's facts

CONNECT = re.compile(r"""^\\connect\s+(?:-reuse-previous=on\s+)?(?:"dbname='((?:[^']|'')*)'"|"((?:[^"]|"")*)"|(\S+))""")
COPY = re.compile(r"^COPY (\S+) \((.*)\) FROM stdin;$")
ESCAPE = re.compile(r"\\(?:([0-7]{1,3})|x([0-9a-fA-F]{1,2})|(.))")
SIMPLE = {"b": "\b", "f": "\f", "n": "\n", "r": "\r", "t": "\t", "v": "\v"}
# The app's tables this reads whole; chat_messages is only counted per chat.
WANTED = {"AspNetUsers", "conversations", "settings", "__EFMigrationsHistory"}


def ident(name: str) -> str:
    """public."AspNetUsers" -> AspNetUsers; "Id" -> Id."""
    name = name.split(".", 1)[1] if name.startswith("public.") else name
    return name[1:-1].replace('""', '"') if name.startswith('"') and name.endswith('"') else name


def unescape(field: str) -> str | None:
    """A field of COPY's text format: \\N is null; backslash escapes as Postgres writes them."""
    if field == r"\N":
        return None
    if "\\" not in field:
        return field

    def one(m: re.Match) -> str:
        if m.group(1):
            return chr(int(m.group(1), 8))
        if m.group(2):
            return chr(int(m.group(2), 16))
        return SIMPLE.get(m.group(3), m.group(3))

    return ESCAPE.sub(one, field)


def parse_dump(lines) -> dict:
    """Per database: the wanted tables' rows (as dicts) and chat_messages counted per chat."""
    dbs: dict[str, dict] = {}
    db = ""
    table = None
    cols: list[str] = []
    for raw in lines:
        line = raw.rstrip("\n")
        if table is None:
            m = CONNECT.match(line)
            if m:
                db = (m.group(1) or "").replace("''", "'") or (m.group(2) or "").replace('""', '"') or m.group(3)
                continue
            m = COPY.match(line)
            if m:
                table = ident(m.group(1))
                cols = [ident(c.strip()) for c in m.group(2).split(",")]
                dbs.setdefault(db, {"tables": {}, "messages": {}})
                if table in WANTED:
                    dbs[db]["tables"].setdefault(table, [])
            continue
        if line == "\\.":
            table = None
            continue
        if table in WANTED:
            dbs[db]["tables"][table].append(dict(zip(cols, (unescape(v) for v in line.split("\t")))))
        elif table == "chat_messages" and "ConversationId" in cols:
            chat = unescape(line.split("\t")[cols.index("ConversationId")])
            dbs[db]["messages"][chat] = dbs[db]["messages"].get(chat, 0) + 1
    return dbs


def facts(lines) -> dict:
    dbs = parse_dump(lines)
    name = next((n for n, d in dbs.items() if "AspNetUsers" in d["tables"]), None)
    if name is None:
        raise SystemExit("the dump has no app database (no AspNetUsers table)")
    t, messages = dbs[name]["tables"], dbs[name]["messages"]
    chats: dict[str, list] = {}
    for c in t.get("conversations", []):
        chats.setdefault(c["UserId"], []).append({
            "id": c["Id"], "updatedAt": c.get("UpdatedAt") or "", "archived": c.get("ArchivedAt") is not None,
            "messages": messages.get(c["Id"], 0),
        })
    migrations = sorted(m["MigrationId"] for m in t.get("__EFMigrationsHistory", []))
    return {
        "database": name,
        "migration": migrations[-1] if migrations else None,
        "people": [{"id": u["Id"], "userName": u["UserName"]} for u in t.get("AspNetUsers", [])],
        "chats": chats,
        "settings": {s["key"][len("config:"):]: s["value"] for s in t.get("settings", []) if (s.get("key") or "").startswith("config:")},
    }


# ---------------------------------------------------------------------------- the API

class Client:
    def __init__(self, url: str, connect: str | None = None):
        self.base = url.rstrip("/")
        host = urllib.parse.urlsplit(self.base).hostname or ""
        if connect:
            pin(host, connect)
        # Traefik's own certificate on a throwaway stack.
        self.open = urllib.request.build_opener(
            urllib.request.HTTPCookieProcessor(http.cookiejar.CookieJar()),
            urllib.request.HTTPSHandler(context=ssl._create_unverified_context()))

    def call(self, path: str, body: object | None = None, method: str | None = None, timeout: int = 60):
        data = json.dumps(body).encode() if body is not None else None
        req = urllib.request.Request(self.base + path, data=data, method=method or ("POST" if data is not None else "GET"))
        req.add_header("X-Requested-With", "fetch")
        req.add_header("Content-Type", "application/json")
        try:
            with self.open.open(req, timeout=timeout) as r:
                text, status = r.read().decode("utf-8", "replace"), r.status
        except urllib.error.HTTPError as e:
            with e:
                text, status = e.read().decode("utf-8", "replace"), e.code
        except (urllib.error.URLError, TimeoutError, ConnectionError, OSError) as e:
            return 0, str(e)
        try:
            return status, json.loads(text) if text else None
        except json.JSONDecodeError:
            return status, text

    def sign_in(self, user: str) -> bool:
        password = os.environ.get("RECOVERY_PASSWORD", "")
        if not password:
            return check(f"{user} signs in", False, "no password: set RECOVERY_PASSWORD")
        status, body = self.call("/api/auth/login", {"userName": user, "password": password})
        state = body.get("status") if isinstance(body, dict) else None
        if status == 200 and state == "2fa":
            return check(f"{user} signs in", False, "two-step sign-in is on for them: pass --user of someone without it")
        return check(f"{user} signs in", status == 200 and state == "ok", f"HTTP {status}")


_pins: dict[str, str] = {}
_getaddrinfo = socket.getaddrinfo


def pin(host: str, address: str) -> None:
    _pins[host] = address

    def resolve(h, *args, **kwargs):
        return _getaddrinfo(_pins.get(h, h), *args, **kwargs)

    socket.getaddrinfo = resolve


def wait_up(c: Client, minutes: float, version: str | None = None) -> dict | None:
    end = time.time() + minutes * 60
    while time.time() < end:
        status, info = c.call("/api/info", timeout=10)
        if status == 200 and isinstance(info, dict) and (version is None or info.get("version") == version):
            return info
        time.sleep(3)
    return None


def people_of(c: Client, minutes: float = 3) -> tuple[list[dict], str | None]:
    """The people list, waiting a little for the gateway (spend and credit) to answer."""
    end = time.time() + minutes * 60
    people, warning = [], "no answer"
    while True:
        status, body = c.call("/api/admin/people")
        if status == 200 and isinstance(body, dict):
            people, warning = body.get("people", []), body.get("warning")
            if not warning:
                break
        if time.time() > end:
            break
        time.sleep(5)
    return people, warning


def saved_settings(c: Client) -> tuple[int, dict[str, dict]]:
    status, body = c.call("/api/admin/config")
    rows = [s for g in (body.get("groups", []) if isinstance(body, dict) else []) for s in g.get("settings", [])]
    return status, {s["key"].lower(): s for s in rows}


# ---------------------------------------------------------------------------- commands

def cmd_facts(a) -> int:
    opener = gzip.open if a.dump.endswith(".gz") else open
    with opener(a.dump, "rt", encoding="utf-8", errors="replace") as f:
        print(json.dumps(facts(f), indent=1))
    return 0


def cmd_wait(a) -> int:
    c = Client(a.url, a.connect)
    info = wait_up(c, a.minutes, a.version)
    what = f"the app answers at {a.version}" if a.version else "the app answers"
    check(what, info is not None, json.dumps(info) if info else f"nothing within {a.minutes} minutes")
    return 0 if info is not None else 1


def cmd_restored(a) -> int:
    with open(a.facts) as f:
        fx = json.load(f)
    c = Client(a.url, a.connect)
    if not c.sign_in(a.user):
        return 1
    people, warning = people_of(c)
    listed = {p.get("userName") for p in people}
    names = [p["userName"] for p in fx["people"]]
    lost = [n for n in names if n not in listed]
    check(f"every person in the backup is there ({len(names)})", not lost and len(people) == len(names),
          f"missing: {', '.join(lost[:10])}" if lost else f"{len(people)} listed")
    check("spend and credit read from the restored gateway", not warning, warning or "")

    me = next((p for p in fx["people"] if p["userName"].lower() == a.user.lower()), None)
    mine = [x for x in fx["chats"].get(me["id"], []) if not x["archived"]] if me else []
    status, body = c.call("/api/chat/conversations")
    shown = [x.get("id") for x in body] if status == 200 and isinstance(body, list) else []
    expected = {x["id"] for x in mine}
    # The list shows the newest 300.
    check(f"{a.user}'s chats in the backup are there ({len(mine)})",
          status == 200 and set(shown) <= expected and len(shown) == min(300, len(expected)),
          f"HTTP {status}, {len(shown)} listed, {len(set(shown) - expected)} not in the backup")
    newest = max(mine, key=lambda x: x["updatedAt"], default=None)
    if newest:
        status, conv = c.call(f"/api/chat/conversations/{newest['id']}")
        msgs = conv.get("messages", []) if isinstance(conv, dict) else []
        check("the newest chat opens with its messages", status == 200 and (len(msgs) > 0 or newest["messages"] == 0),
              f"HTTP {status}, {len(msgs)} shown, {newest['messages']} in the backup")

    status, view = saved_settings(c)
    known = {k: v for k, v in fx["settings"].items() if k.lower() in view}
    unsaved = [k for k in known if view[k.lower()].get("source") != "saved"]
    changed = [k for k, v in known.items() if view[k.lower()].get("value") is not None and v is not None and view[k.lower()]["value"] != v]
    check(f"every setting saved in the backup reads back saved, with its value ({len(known)})",
          status == 200 and not unsaved and not changed,
          f"HTTP {status}" + (f"; not saved: {', '.join(unsaved[:5])}" if unsaved else "") + (f"; other value: {', '.join(changed[:5])}" if changed else ""))
    return summary()


def cmd_seed(a) -> int:
    c = Client(a.url, a.connect)
    if not c.sign_in(a.user):
        return 1
    stamp = f"{a.label}{int(time.time())}"
    state: dict = {"people": [], "groups": [], "chats": [], "settings": {}}
    status, made = c.call("/api/admin/people", {"userName": stamp, "email": f"{stamp}@example.test", "displayName": f"{a.label} test"})
    if check(f"a person goes in ({stamp})", status in (200, 201), f"HTTP {status}"):
        state["people"].append(stamp)
    group = f"{a.label} group {stamp}"
    status, _ = c.call("/api/admin/groups", {"name": group})
    if check("a group goes in", status in (200, 201), f"HTTP {status}"):
        state["groups"].append(group)
    status, chat = c.call("/api/chat/conversations", {"thinking": "off"})
    chat_id = chat.get("id") if isinstance(chat, dict) else None
    title = f"{a.label} chat {stamp}"
    renamed, _ = c.call(f"/api/chat/conversations/{chat_id}", {"title": title}, method="PATCH") if chat_id else (0, None)
    if check("a chat goes in, with a title", status in (200, 201) and renamed in (200, 204), f"HTTP {status}, {renamed}"):
        state["chats"].append({"id": chat_id, "title": title})
    value = f"{a.label} {stamp}"
    status, _ = c.call("/api/admin/config", {"changes": [{"key": a.setting, "value": value}]}, method="PUT")
    if check(f"a setting is saved ({a.setting})", status == 200, f"HTTP {status}"):
        state["settings"][a.setting] = value
    with open(a.state, "w") as f:
        json.dump(state, f, indent=1)
    return summary()


def cmd_verify(a) -> int:
    with open(a.state) as f:
        want = json.load(f)
    gone = {"people": [], "groups": [], "chats": [], "settings": {}}
    if a.absent:
        with open(a.absent) as f:
            gone = json.load(f)
    c = Client(a.url, a.connect)
    if not c.sign_in(a.user):
        return 1
    status, body = c.call("/api/admin/people")
    names = {p.get("userName") for p in (body.get("people", []) if isinstance(body, dict) else [])}
    status_g, groups = c.call("/api/admin/groups")
    group_text = json.dumps(groups)
    status_c, chats = c.call("/api/chat/conversations")
    chat_ids = {x.get("id") for x in chats} if isinstance(chats, list) else set()
    _, view = saved_settings(c)
    when = a.when
    for p in want["people"]:
        check(f"{when}: the person {p} is there", p in names, f"HTTP {status}")
    for g in want["groups"]:
        check(f"{when}: the group is there", status_g == 200 and json.dumps(g)[1:-1] in group_text, f"HTTP {status_g}")
    for ch in want["chats"]:
        s, conv = c.call(f"/api/chat/conversations/{ch['id']}")
        check(f"{when}: the chat is there, with its title", ch["id"] in chat_ids and s == 200 and isinstance(conv, dict) and conv.get("title") == ch["title"], f"HTTP {s}")
    for k, v in want["settings"].items():
        row = view.get(k.lower(), {})
        check(f"{when}: the setting {k} is as saved", row.get("source") == "saved" and row.get("value") == v, str(row.get("value"))[:80])
    for p in gone["people"]:
        check(f"{when}: the person {p} from after it is not", status == 200 and p not in names)
    for g in gone["groups"]:
        check(f"{when}: the group from after it is not", status_g == 200 and json.dumps(g)[1:-1] not in group_text)
    for ch in gone["chats"]:
        check(f"{when}: the chat from after it is not", status_c == 200 and ch["id"] not in chat_ids)
    for k, v in gone["settings"].items():
        check(f"{when}: the setting {k} saved after it is not", view.get(k.lower(), {}).get("value") != v)
    return summary()


def summary() -> int:
    failed = [n for n, ok in results if not ok]
    return 1 if failed else 0


def main(argv: list[str] | None = None) -> int:
    ap = argparse.ArgumentParser(description="What restore-test.sh and rollback-test.sh check.")
    sub = ap.add_subparsers(dest="cmd", required=True)
    f = sub.add_parser("facts")
    f.add_argument("dump")
    for name in ("wait", "restored", "seed", "verify"):
        p = sub.add_parser(name)
        p.add_argument("--url", required=True)
        p.add_argument("--connect")
        p.add_argument("--user", default="admin")
        if name == "wait":
            p.add_argument("--version")
            p.add_argument("--minutes", type=float, default=10)
        if name == "restored":
            p.add_argument("--facts", required=True)
        if name in ("seed", "verify"):
            p.add_argument("--state", required=True)
        if name == "seed":
            p.add_argument("--label", required=True)
            p.add_argument("--setting", required=True)
        if name == "verify":
            p.add_argument("--absent")
            p.add_argument("--when", default="now")
    a = ap.parse_args(argv)
    return {"facts": cmd_facts, "wait": cmd_wait, "restored": cmd_restored, "seed": cmd_seed, "verify": cmd_verify}[a.cmd](a)


if __name__ == "__main__":
    sys.exit(main())
