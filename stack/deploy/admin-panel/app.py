"""Self-service usage page, and an admin console behind the same login.

Who sees what is decided by Authelia, not by this app. Traefik runs every
request through Authelia's forward-auth endpoint first, and Authelia answers
with Remote-User / Remote-Email / Remote-Groups. Someone in the `admins` group
gets the console; everyone else gets their own usage and a password form.

WHY THE HEADERS CAN BE TRUSTED, AND THE ONE CASE WHERE THEY CANNOT
------------------------------------------------------------------
Traefik OVERWRITES Remote-* from Authelia's response, so a browser cannot forge
them: whatever a client sends is replaced before this app sees it. That holds
only for traffic arriving through Traefik.

Another container on llm-net could reach this one directly and set the headers
itself. That is why REQUIRE_FORWARDED defaults on: a request with no
X-Forwarded-Host did not come through the proxy and is refused. It is a second
lock on an internal-only door, not a substitute for the first.

WHAT IT TOUCHES
---------------
  LiteLLM   users, virtual keys and spend, over its admin API with the master
            key. The master key never leaves this container.
  users.yml Authelia's file backend, for creating users and setting passwords.
            Hashes are argon2id with the same parameters Authelia itself uses
            (m=65536,t=3,p=4) so entries written here are indistinguishable
            from ones written by the auth-init service.

RELOADING -- THE PART THAT BITES
--------------------------------
Authelia is configured with `watch: true`, and on a normal Linux host that is
enough: it re-reads users.yml when the file changes. On Docker Desktop it is
NOT enough. The file lives on a Windows bind mount, inotify events do not cross
that boundary, and Authelia never learns the file moved. Measured here: after
creating a user, the file contained it and Authelia still answered
`error="user not found"` on sign-in; after `docker compose restart authelia`
the same attempt reached "Unsuccessful 1FA" instead -- the user was known and
only the password was wrong.

Neither os.replace() nor an in-place rewrite makes the watch fire, so this is
not something the write strategy can fix. Rather than pretend, every action
that writes users.yml says so and gives the command.
"""
from __future__ import annotations

import html
import json
import os
import re
import secrets
import sys
import threading
import time
import urllib.error
import urllib.parse
import urllib.request
from datetime import datetime, timezone

import yaml
from argon2 import PasswordHasher
from argon2.low_level import Type
from starlette.applications import Starlette
from starlette.requests import Request
from starlette.responses import HTMLResponse, JSONResponse, RedirectResponse, Response
from starlette.routing import Route

LITELLM = os.environ.get("LITELLM_URL", "http://litellm:4000").rstrip("/")
MASTER = os.environ.get("LITELLM_MASTER_KEY", "")
USERS_FILE = os.environ.get("AUTHELIA_USERS_FILE", "/authelia/users.yml")
#: Who is who, without password hashes: username -> email and display name.
#: Argus reads this to map a chat user's email to their sign-in username, and
#: must not be able to read users.yml itself. Kept in step by save_users and
#: by a background sync, so a hand edit of users.yml reaches it too.
DIRECTORY_FILE = os.environ.get(
    "AUTHELIA_DIRECTORY_FILE",
    os.path.join(os.path.dirname(USERS_FILE), "directory", "users.yml"))
ADMIN_GROUP = os.environ.get("ADMIN_GROUP", "admins")
GRAFANA_URL = os.environ.get("GRAFANA_URL", "")
#: Probed by the Monitoring page. Internal name on purpose: this is the
#: container talking to a container, not a browser round-tripping the proxy.
PROMETHEUS_URL = os.environ.get("PROMETHEUS_URL", "http://prometheus:9090").rstrip("/")
#: Where the health probes go, as opposed to where the links go. The public
#: hostnames resolve to the host's loopback, which is not Traefik from inside
#: this container -- so probing them reported Grafana as down while it was
#: serving fine.
GRAFANA_PROBE_URL = os.environ.get("GRAFANA_PROBE_URL", "http://grafana:3000").rstrip("/")
AUTHELIA_PROBE_URL = os.environ.get("AUTHELIA_PROBE_URL", "http://authelia:9091").rstrip("/")
#: Argus's operator control surface. Both must be set for the indexing card to
#: appear: without the token the endpoint does not exist on Argus's side, so
#: rendering a button that cannot work would only mislead.
ARGUS_URL = os.environ.get("ARGUS_URL", "http://argus:7700").rstrip("/")
ARGUS_ADMIN_TOKEN = os.environ.get("ARGUS_ADMIN_TOKEN", "")
# Signing out is Authelia's job, not this app's: the session cookie is
# Authelia's and clearing it anywhere else would leave the portal still
# logged in, so the next visit would walk straight back in.
AUTHELIA_URL = os.environ.get("AUTHELIA_URL", "")
DOMAIN = os.environ.get("LLM_DOMAIN", "llm.localhost")
DEFAULT_BUDGET = float(os.environ.get("LITELLM_DEFAULT_USER_BUDGET", "50") or 50)
REQUIRE_FORWARDED = os.environ.get("REQUIRE_FORWARDED", "1") == "1"
# Set to "0" on a host where Authelia's file watch is known to work (a normal
# Linux box) to drop the reload note from success messages.
WARN_RELOAD = os.environ.get("WARN_RELOAD", "1") == "1"
RELOAD_NOTE = ("  --  Authelia must re-read users.yml before this can sign in: "
               "run  docker compose restart authelia")

# Authelia 4.39's own argon2id defaults. Verified against the hashes already in
# users.yml ($argon2id$v=19$m=65536,t=3,p=4) -- a mismatch here would write
# entries that Authelia rejects at login, which is a failure you only discover
# when someone cannot get in.
_hasher = PasswordHasher(time_cost=3, memory_cost=65536, parallelism=4,
                         hash_len=32, salt_len=16, type=Type.ID)

_EMAIL = re.compile(r"^[^@\s]+@[^@\s]+\.[^@\s]+$")
_USERNAME = re.compile(r"^[a-z0-9._-]{2,64}$")


# ----------------------------------------------------------------- identity --
class Caller:
    def __init__(self, user: str, email: str, groups: list[str]):
        self.user, self.email, self.groups = user, email, groups
        self.is_admin = ADMIN_GROUP in groups

    @property
    def label(self) -> str:
        return self.email or self.user


def caller(request: Request) -> Caller | None:
    if REQUIRE_FORWARDED and not request.headers.get("x-forwarded-host"):
        return None
    user = (request.headers.get("remote-user") or "").strip()
    if not user:
        return None
    email = (request.headers.get("remote-email") or "").strip().lower()
    raw = request.headers.get("remote-groups") or ""
    groups = [g.strip() for g in raw.split(",") if g.strip()]
    return Caller(user, email, groups)


# ------------------------------------------------------------------ litellm --
class GatewayUnreachable(urllib.error.HTTPError):
    """A transport failure wearing the shape every call site already handles.

    Each LiteLLM call below branches on `HTTPError.code`. A gateway that is
    merely SLOW or REFUSED raises `URLError` or `TimeoutError` instead --
    different types, caught nowhere, so a stopped LiteLLM turned every page
    into a 500 traceback instead of a page saying the gateway is down.

    503 is the honest code for "the upstream is not there", and borrowing the
    HTTPError shape means no caller has to learn a second one.
    """

    def __init__(self, url: str, reason: object) -> None:
        super().__init__(url, 503, f"gateway unreachable: {reason}", {}, None)  # type: ignore[arg-type]


def api(path: str, payload: dict | None = None, method: str | None = None) -> dict:
    req = urllib.request.Request(
        LITELLM + path,
        data=json.dumps(payload).encode() if payload is not None else None,
        headers={"Authorization": f"Bearer {MASTER}", "Content-Type": "application/json"},
        method=method or ("POST" if payload is not None else "GET"),
    )
    try:
        with urllib.request.urlopen(req, timeout=30) as resp:
            body = resp.read()
    except urllib.error.HTTPError:
        raise                     # a real answer, with a code worth reporting
    except (TimeoutError, OSError) as exc:   # URLError is an OSError
        raise GatewayUnreachable(LITELLM + path, exc) from exc
    return json.loads(body) if body else {}


def spend_by_user() -> tuple[dict[str, dict], str | None]:
    """Everyone LiteLLM knows about, with spend and ceiling.

    Returns the rows AND why they might be incomplete. An empty dict on its
    own is ambiguous -- either nobody has spent anything or the gateway is
    down -- and showing 0.00 for every person in the second case is a lie the
    reader has no way to detect.
    """
    out: dict[str, dict] = {}
    page = 1
    while True:
        try:
            data = api(f"/user/list?page={page}&page_size=100")
        except urllib.error.HTTPError as exc:
            return out, (f"Could not read usage from the gateway (HTTP {exc.code}). "
                         "The figures below are incomplete.")
        rows = data.get("users") if isinstance(data, dict) else data
        if not rows:
            break
        for u in rows:
            uid = (u.get("user_id") or "").strip()
            if not uid:
                continue
            out[uid.lower()] = {
                "user_id": uid,
                "email": u.get("user_email") or uid,
                "spend": float(u.get("spend") or 0.0),
                "budget": u.get("max_budget"),
                "role": u.get("user_role") or "",
            }
        if len(rows) < 100:
            break
        page += 1
    return out, None


def keys_for(user_id: str) -> list[dict]:
    try:
        data = api(f"/key/list?user_id={urllib.parse.quote(user_id)}&return_full_object=true")
    except urllib.error.HTTPError:
        return []
    rows = data.get("keys") if isinstance(data, dict) else data
    out = []
    for k in rows or []:
        if isinstance(k, str):
            out.append({"token": k, "alias": "", "spend": 0.0})
        else:
            out.append({"token": k.get("token") or k.get("key_name") or "",
                        "alias": k.get("key_alias") or "",
                        "spend": float(k.get("spend") or 0.0)})
    return out


# ------------------------------------------------------------- authelia file --
def load_users() -> dict:
    try:
        with open(USERS_FILE, encoding="utf-8") as fh:
            doc = yaml.safe_load(fh) or {}
    except FileNotFoundError:
        return {}
    return doc.get("users") or {}


def _replace_file(path: str, data: bytes, mode: int, owner: os.stat_result) -> None:
    """Write `data` to a temp file and move it over `path`.

    The temp file has `mode` from its first byte, so a password hash is never
    readable by anyone else, not even for a moment. When the writer is root (a
    `docker exec` into this container is), the file is handed to `owner`: a
    root-owned users.yml locks the panel out of its next write, and root's
    umask left it world-readable.
    """
    tmp = path + ".tmp"
    fd = os.open(tmp, os.O_WRONLY | os.O_CREAT | os.O_TRUNC, mode)
    with os.fdopen(fd, "wb") as fh:
        fh.write(data)
    os.chmod(tmp, mode)  # the umask filters O_CREAT's mode, and a stale tmp kept its own
    if os.geteuid() == 0:
        os.chown(tmp, owner.st_uid, owner.st_gid)
    os.replace(tmp, path)


def save_users(users: dict) -> None:
    """Rewrite users.yml, keeping a backup.

    Written to a temp file and moved into place so Authelia never observes a
    half-written database -- it watches this file and reloads on change. Both
    files stay mode 600 and keep the owner the original had.
    """
    st = os.stat(USERS_FILE)
    with open(USERS_FILE, "rb") as fh:
        raw = fh.read()
    doc = yaml.safe_load(raw) or {}
    doc["users"] = users
    _replace_file(USERS_FILE + ".bak", raw, 0o600, st)
    _replace_file(USERS_FILE, yaml.safe_dump(
        doc, default_flow_style=False, sort_keys=False, allow_unicode=True).encode("utf-8"),
        0o600, st)
    save_directory(users)


def save_directory(users: dict) -> bool:
    """Publish username -> email for Argus, without hashes. True if it changed.

    Disabled accounts are left out: they cannot sign in to chat. A failure is
    reported and swallowed, since the account change it follows has already
    been saved and must not be reported as failed.
    """
    people = {name: {"email": str(u.get("email") or ""),
                     "displayname": str(u.get("displayname") or "")}
              for name, u in users.items()
              if isinstance(u, dict) and not u.get("disabled")}
    data = ("# GENERATED by the admin panel from users.yml. No passwords. Do not edit.\n"
            + yaml.safe_dump({"users": people}, default_flow_style=False,
                             sort_keys=True, allow_unicode=True)).encode("utf-8")
    try:
        with open(DIRECTORY_FILE, "rb") as fh:
            if fh.read() == data:
                return False
    except OSError:
        pass
    try:
        _replace_file(DIRECTORY_FILE, data, 0o644, os.stat(os.path.dirname(DIRECTORY_FILE)))
    except OSError as exc:
        print(f"admin-panel: cannot write {DIRECTORY_FILE}: {exc}; Argus falls back to "
              "GitLab public emails to identify chat users", file=sys.stderr)
        return False
    return True


def _sync_directory_forever(interval: float = 30.0) -> None:
    """Follow users.yml, including edits made by hand."""
    seen = None
    while True:
        try:
            mtime = os.stat(USERS_FILE).st_mtime_ns
            if mtime != seen:
                save_directory(load_users())
                seen = mtime
        except (OSError, yaml.YAMLError) as exc:
            print(f"admin-panel: directory sync: {exc}", file=sys.stderr)
        time.sleep(interval)


def _start_directory_sync() -> None:
    threading.Thread(target=_sync_directory_forever, name="directory-sync", daemon=True).start()


def set_password(username: str, password: str) -> None:
    users = load_users()
    if username not in users:
        raise KeyError(username)
    users[username]["password"] = _hasher.hash(password)
    save_users(users)


# ------------------------------------------------------------------- render --
def _h(s) -> str:
    return html.escape(str(s if s is not None else ""))


def _money(v) -> str:
    return "—" if v is None else f"${float(v):,.2f}"


def _tile(label: str, value: str, sub: str = "") -> str:
    """One headline number. The figures an operator opens this page for were
    scattered through table cells; a tile row answers them before any scroll."""
    tail = f'<div class="sub">{_h(sub)}</div>' if sub else ""
    return (f'<div class="tile"><div class="k">{_h(label)}</div>'
            f'<div class="v">{value}</div>{tail}</div>')


PAGE = """<!doctype html><html lang="en"><head><meta charset="utf-8">
<meta name="viewport" content="width=device-width,initial-scale=1">
<meta name="color-scheme" content="dark light">
<title>{title}</title><style>
/* Design tokens. One palette, one spacing scale and one type scale, so a new
   card cannot invent its own. Raises to a light theme when the OS asks for
   one: an admin console gets opened on whatever machine is to hand. */
:root{{
 --bg:#0b0d12;--surface:#141821;--surface-2:#1a1f2a;--line:#242b38;--line-soft:#1d232e;
 --fg:#e8ecf3;--fg-muted:#98a2b3;--fg-faint:#6b7686;
 --accent:#5b8cff;--accent-soft:rgba(91,140,255,.14);
 --ok:#3ecf8e;--ok-soft:rgba(62,207,142,.13);
 --warn:#f5a524;--warn-soft:rgba(245,165,36,.13);
 --bad:#f2555a;--bad-soft:rgba(242,85,90,.13);
 --r-sm:6px;--r:10px;--r-lg:14px;
 --s1:4px;--s2:8px;--s3:12px;--s4:16px;--s5:24px;--s6:32px;
 --shadow:0 1px 2px rgba(0,0,0,.4),0 8px 24px -12px rgba(0,0,0,.6);
 --mono:ui-monospace,SFMono-Regular,"SF Mono",Menlo,Consolas,monospace;
}}
@media (prefers-color-scheme:light){{
 :root{{--bg:#f6f7f9;--surface:#fff;--surface-2:#f2f4f7;--line:#e3e6ec;--line-soft:#eef0f4;
 --fg:#171a20;--fg-muted:#5b6472;--fg-faint:#8a93a1;--shadow:0 1px 2px rgba(16,24,40,.06),0 8px 24px -14px rgba(16,24,40,.18)}}
}}
*{{box-sizing:border-box}}
html{{-webkit-text-size-adjust:100%}}
body{{margin:0;background:var(--bg);color:var(--fg);
 font:14px/1.55 ui-sans-serif,system-ui,-apple-system,"Segoe UI",Roboto,sans-serif;
 -webkit-font-smoothing:antialiased}}
:focus-visible{{outline:2px solid var(--accent);outline-offset:2px;border-radius:var(--r-sm)}}

header{{position:sticky;top:0;z-index:10;display:flex;align-items:center;gap:var(--s3);
 padding:var(--s3) var(--s5);background:color-mix(in srgb,var(--surface) 88%,transparent);
 backdrop-filter:blur(10px);border-bottom:1px solid var(--line)}}
.brand{{display:flex;align-items:center;gap:10px;font-weight:650;letter-spacing:-.01em}}
.brand .mark{{width:22px;height:22px;border-radius:6px;flex:0 0 auto;
 background:linear-gradient(135deg,var(--accent),color-mix(in srgb,var(--accent) 45%,var(--ok)))}}
header .who{{margin-left:auto;color:var(--fg-muted);font-size:13px}}
header a.out{{text-decoration:none;color:var(--fg-muted);border:1px solid var(--line);
 border-radius:var(--r-sm);padding:6px 12px;font-size:12px;font-weight:600;
 transition:border-color .15s,color .15s}}
header a.out:hover{{border-color:var(--bad);color:var(--bad)}}
.badge{{background:var(--accent-soft);color:var(--accent);border-radius:999px;
 padding:2px 10px;font-size:11px;font-weight:700;letter-spacing:.02em}}

main{{max-width:1100px;margin:0 auto;padding:var(--s5) var(--s5) var(--s6)}}
h1.page{{font-size:20px;font-weight:650;letter-spacing:-.02em;margin:0 0 var(--s1)}}
p.lede{{color:var(--fg-muted);margin:0 0 var(--s5);font-size:13px}}

/* A tile row for the numbers that answer "is it healthy and who is using it",
   which are otherwise scattered as table cells. */
.tiles{{display:grid;gap:var(--s3);grid-template-columns:repeat(auto-fit,minmax(180px,1fr));
 margin-bottom:var(--s5)}}
.tile{{background:var(--surface);border:1px solid var(--line);border-radius:var(--r);
 padding:var(--s4);box-shadow:var(--shadow)}}
.tile .k{{color:var(--fg-faint);font-size:11px;font-weight:650;text-transform:uppercase;
 letter-spacing:.07em;margin-bottom:var(--s2)}}
.tile .v{{font-size:22px;font-weight:650;letter-spacing:-.02em;line-height:1.15}}
.tile .sub{{color:var(--fg-muted);font-size:12px;margin-top:var(--s1)}}

.card{{background:var(--surface);border:1px solid var(--line);border-radius:var(--r);
 padding:var(--s5);margin-bottom:var(--s4);box-shadow:var(--shadow)}}
.card h2{{font-size:13px;margin:0 0 var(--s4);font-weight:650;
 letter-spacing:-.005em;display:flex;align-items:center;gap:var(--s2)}}
.card h2::before{{content:"";width:3px;height:14px;border-radius:2px;background:var(--accent)}}
.card h2 .n{{color:var(--fg-faint);font-weight:600}}

table{{width:100%;border-collapse:separate;border-spacing:0}}
th,td{{text-align:left;padding:10px 12px;border-bottom:1px solid var(--line-soft);
 font-size:13px;vertical-align:middle}}
th{{color:var(--fg-faint);font-weight:650;font-size:11px;text-transform:uppercase;
 letter-spacing:.06em;background:var(--surface-2);
 position:sticky;top:0}}
th:first-child{{border-top-left-radius:var(--r-sm)}} th:last-child{{border-top-right-radius:var(--r-sm)}}
tbody tr:hover td{{background:var(--surface-2)}}
tr:last-child td{{border-bottom:0}}
td.num,th.num{{text-align:right;font-variant-numeric:tabular-nums}}
td.mono{{font-family:var(--mono);font-size:12px}}

input,select{{background:var(--bg);border:1px solid var(--line);color:var(--fg);
 border-radius:var(--r-sm);padding:9px 11px;font:inherit;min-width:0;
 transition:border-color .15s,box-shadow .15s}}
input:hover,select:hover{{border-color:var(--fg-faint)}}
input:focus,select:focus{{border-color:var(--accent);
 box-shadow:0 0 0 3px var(--accent-soft);outline:none}}
label{{display:block;color:var(--fg-muted);font-size:12px;font-weight:600;margin-bottom:5px}}
.field{{display:flex;flex-direction:column;gap:2px}}

button{{background:var(--accent);color:#fff;border:1px solid transparent;
 border-radius:var(--r-sm);padding:9px 14px;font:inherit;font-weight:600;
 cursor:pointer;transition:filter .15s,background .15s,border-color .15s}}
button:hover{{filter:brightness(1.08)}}
button:active{{filter:brightness(.94)}}
button.ghost{{background:transparent;border-color:var(--line);color:var(--fg)}}
button.ghost:hover{{border-color:var(--accent);color:var(--accent);filter:none}}
button.danger{{background:transparent;border-color:var(--line);color:var(--bad)}}
button.danger:hover{{border-color:var(--bad);background:var(--bad-soft);filter:none}}
a.btn{{display:inline-block;text-decoration:none;background:transparent;
 border:1px solid var(--line);color:var(--fg);border-radius:var(--r-sm);
 padding:9px 14px;font-weight:600;transition:border-color .15s,color .15s}}
a.btn:hover{{border-color:var(--accent);color:var(--accent)}}
form.row{{display:flex;gap:var(--s3);flex-wrap:wrap;align-items:flex-end}}

.msg{{padding:12px 15px;border-radius:var(--r);margin-bottom:var(--s4);font-size:13px;
 border:1px solid transparent;display:flex;gap:10px;align-items:flex-start}}
.msg::before{{font-weight:700;line-height:1.4}}
.msg.ok{{background:var(--ok-soft);border-color:color-mix(in srgb,var(--ok) 45%,transparent);color:var(--ok)}}
.msg.ok::before{{content:"✓"}}
.msg.bad{{background:var(--bad-soft);border-color:color-mix(in srgb,var(--bad) 45%,transparent);color:var(--bad)}}
.msg.bad::before{{content:"!"}}

code.key{{font-family:var(--mono);font-size:12px;background:var(--bg);
 border:1px solid var(--line);border-radius:var(--r-sm);padding:4px 8px;
 display:inline-block;word-break:break-all;color:var(--fg)}}
.pill{{display:inline-block;border-radius:999px;padding:2px 9px;font-size:11px;font-weight:650}}
.pill.ok{{background:var(--ok-soft);color:var(--ok)}}
.pill.warn{{background:var(--warn-soft);color:var(--warn)}}
.pill.bad{{background:var(--bad-soft);color:var(--bad)}}
.bar{{height:6px;background:var(--surface-2);border-radius:999px;overflow:hidden;margin-top:6px}}
.bar i{{display:block;height:100%;background:var(--ok);border-radius:999px;
 transition:width .3s ease}}
.bar i.warn{{background:var(--warn)}} .bar i.bad{{background:var(--bad)}}
.dim{{color:var(--fg-muted);font-size:12px}}
.empty{{color:var(--fg-faint);font-size:13px;padding:var(--s4) 0;text-align:center}}

@media (max-width:640px){{
 main{{padding:var(--s4) var(--s3) var(--s5)}}
 header{{padding:var(--s3) var(--s4);gap:var(--s2)}}
 header .who{{display:none}}
 .card{{padding:var(--s4)}}
 th,td{{padding:8px 9px}}
}}
@media (prefers-reduced-motion:reduce){{*{{transition:none!important}}}}

/* ---------------------------------------------------------------- shell ----
   The console was a single scrolling page. A sidebar is what makes it a set of
   places rather than one list, and it is the difference between "a page with
   tables on it" and something an operator can move around in. */
.shell{{display:grid;grid-template-columns:238px 1fr;min-height:100vh}}
nav.side{{position:sticky;top:0;height:100vh;overflow:auto;display:flex;flex-direction:column;
 gap:2px;padding:var(--s4) var(--s3);background:var(--surface);border-right:1px solid var(--line)}}
nav.side .brand{{padding:0 var(--s2) var(--s4)}}
nav.side .group{{margin:var(--s4) 0 var(--s1);padding:0 10px;font-size:10.5px;font-weight:700;
 letter-spacing:.07em;text-transform:uppercase;color:var(--fg-faint)}}
nav.side a.item{{display:flex;align-items:center;gap:10px;padding:8px 10px;border-radius:var(--r-sm);
 color:var(--fg-muted);text-decoration:none;font-weight:550;font-size:13px;
 transition:background .12s,color .12s}}
nav.side a.item:hover{{background:var(--surface-2);color:var(--fg)}}
nav.side a.item.on{{background:var(--accent-soft);color:var(--accent)}}
nav.side a.item .ic{{width:16px;height:16px;flex:0 0 auto;opacity:.85}}
nav.side a.item .n{{margin-left:auto;font-size:11px;color:var(--fg-faint);font-variant-numeric:tabular-nums}}
nav.side .foot{{margin-top:auto;padding-top:var(--s3);border-top:1px solid var(--line-soft)}}
nav.side .me{{display:flex;align-items:center;gap:9px;padding:8px 10px;font-size:12px;
 color:var(--fg-muted);min-width:0}}
nav.side .me .who{{overflow:hidden;text-overflow:ellipsis;white-space:nowrap}}
.avatar{{width:24px;height:24px;border-radius:50%;background:var(--accent-soft);color:var(--accent);
 display:grid;place-items:center;font-weight:700;font-size:11px;flex:0 0 auto;text-transform:uppercase}}
.content{{min-width:0;display:flex;flex-direction:column}}
.topbar{{position:sticky;top:0;z-index:9;display:flex;align-items:center;gap:var(--s3);
 padding:var(--s3) var(--s5);background:color-mix(in srgb,var(--bg) 88%,transparent);
 backdrop-filter:blur(10px);border-bottom:1px solid var(--line)}}
.crumbs{{font-size:12px;color:var(--fg-muted)}} .crumbs b{{color:var(--fg);font-weight:600}}
.topbar .spacer{{margin-left:auto}}
.chip{{border:1px solid var(--line);background:transparent;color:var(--fg-muted);border-radius:var(--r-sm);
 padding:5px 10px;font-size:12px;cursor:pointer;text-decoration:none;display:inline-flex;
 gap:6px;align-items:center;font-weight:550}}
.chip:hover{{color:var(--fg);border-color:var(--fg-faint)}}
.toolbar{{display:flex;gap:var(--s2);align-items:center;flex-wrap:wrap;margin:0 0 var(--s3)}}
.toolbar form{{display:flex;gap:var(--s2);align-items:center;margin:0}}
.toolbar .spacer{{margin-left:auto}}
.tablewrap{{overflow-x:auto}}
.btn.primary{{background:var(--accent);border-color:var(--accent);color:#08101f}}
.btn.danger{{color:var(--bad);border-color:color-mix(in srgb,var(--bad) 45%,transparent);
 background:transparent}}
.btn.danger:hover{{background:var(--bad-soft)}}
.svc{{display:flex;align-items:center;gap:10px;padding:9px 0;border-bottom:1px solid var(--line-soft);
 font-size:13px}}
.svc:last-child{{border-bottom:0}}
.dot{{width:8px;height:8px;border-radius:50%;flex:0 0 auto;background:var(--fg-faint)}}
.dot.ok{{background:var(--ok);box-shadow:0 0 0 3px var(--ok-soft)}}
.dot.bad{{background:var(--bad);box-shadow:0 0 0 3px var(--bad-soft)}}
.svc .name{{font-weight:550}}
.svc .ms{{margin-left:auto;color:var(--fg-muted);font-size:12px;
 font-variant-numeric:tabular-nums;white-space:nowrap}}
.pager{{display:flex;gap:var(--s2);align-items:center;justify-content:flex-end;
 margin-top:var(--s3);font-size:12px;color:var(--fg-muted)}}
.pager a{{color:var(--fg-muted);text-decoration:none;border:1px solid var(--line);
 border-radius:var(--r-sm);padding:5px 10px}}
.pager a:hover{{color:var(--fg);border-color:var(--fg-faint)}}
.pager a.off{{opacity:.4;pointer-events:none}}
.kv{{display:grid;grid-template-columns:minmax(170px,250px) 1fr;gap:0}}
.kv dt{{padding:9px 0;border-bottom:1px solid var(--line-soft);color:var(--fg-muted);font-size:13px}}
.kv dd{{margin:0;padding:9px 0;border-bottom:1px solid var(--line-soft);
 font-family:var(--mono);font-size:12.5px;overflow-wrap:anywhere}}
.sectionnav{{display:flex;gap:var(--s2);flex-wrap:wrap;margin-bottom:var(--s4)}}
.sectionnav a{{text-decoration:none;color:var(--fg-muted);font-size:12px;font-weight:600;
 border:1px solid var(--line);border-radius:999px;padding:5px 12px}}
.sectionnav a.on{{background:var(--accent-soft);color:var(--accent);border-color:transparent}}
@media (max-width:900px){{
 .shell{{grid-template-columns:1fr}}
 nav.side{{position:static;height:auto;flex-direction:row;align-items:center;overflow-x:auto;
  border-right:0;border-bottom:1px solid var(--line);padding:var(--s2) var(--s3);gap:var(--s1)}}
 nav.side .brand,nav.side .group,nav.side .foot{{display:none}}
 nav.side a.item{{white-space:nowrap;padding:6px 10px}}
 topbar,.topbar{{padding:var(--s2) var(--s4)}}
 main{{padding:var(--s4) var(--s4) var(--s6)}}
}}
</style></head><body>
<div class="shell">
<nav class="side">
 <span class="brand"><span class="mark"></span>LLM Service</span>
 {nav}
 <div class="foot">{face}</div>
</nav>
<div class="content">
 <div class="topbar"><div class="crumbs">{crumbs}</div><div class="spacer"></div>{theme}</div>
 <main>{msg}{body}</main>
</div></div></body></html>"""


#: The sections of the console. `icon` is an SVG path, `admin` hides it from
#: ordinary people, and `count` is filled in per request where a number is
#: useful at a glance -- the sidebar is where "how many people" belongs, not a
#: tile you have to scroll to.
NAV_ITEMS = (
    ("overview",   "/",           "Overview",   "M3 3h7v7H3zM14 3h7v4h-7zM14 11h7v10h-7zM3 14h7v7H3z", False),
    ("people",     "/people",     "People",     "M16 21v-2a4 4 0 0 0-4-4H6a4 4 0 0 0-4 4v2M9 3a4 4 0 1 1 0 8 4 4 0 0 1 0-8M22 21v-2a4 4 0 0 0-3-3.9", True),
    ("model",      "/model",      "Model",      "M12 2 2 7l10 5 10-5-10-5zM2 17l10 5 10-5M2 12l10 5 10-5", False),
    ("indexing",   "/indexing",   "Indexing",   "M21 12a9 9 0 1 1-6.2-8.6M22 4v6h-6", False),
    ("monitoring", "/monitoring", "Monitoring", "M3 3v18h18M19 9l-5 5-4-4-3 3", False),
    ("settings",   "/settings",   "Settings",   "M12 15a3 3 0 1 0 0-6 3 3 0 0 0 0 6zM19.4 15a1.65 1.65 0 0 0 .33 1.82l.06.06a2 2 0 1 1-2.83 2.83l-.06-.06a1.65 1.65 0 0 0-2.82 1.18V21a2 2 0 1 1-4 0v-.09A1.65 1.65 0 0 0 7.26 19.4l-.06.06a2 2 0 1 1-2.83-2.83l.06-.06A1.65 1.65 0 0 0 3.09 14H3a2 2 0 1 1 0-4h.09A1.65 1.65 0 0 0 4.6 8.74l-.06-.06a2 2 0 1 1 2.83-2.83l.06.06A1.65 1.65 0 0 0 10 4.6V3a2 2 0 1 1 4 0v.09a1.65 1.65 0 0 0 2.74 1.18l.06-.06a2 2 0 1 1 2.83 2.83l-.06.06A1.65 1.65 0 0 0 19.4 10V10a2 2 0 1 1 0 4h-.09a1.65 1.65 0 0 0-1.51 1z", True),
)

THEMES = ("system", "dark", "light")


def _theme(request: Request) -> str:
    """Which theme to render, from a cookie. Falls back to the OS preference.

    A cookie rather than a database or a query string: this is one person's
    view of one page, and it should survive a navigation without becoming
    part of a URL that someone pastes into a ticket.
    """
    value = (request.cookies.get("theme") or "system").lower()
    return value if value in THEMES else "system"


def _nav(active: str, admin: bool, counts: dict[str, str]) -> str:
    def item(key, href, label, icon):
        on = " on" if key == active else ""
        n = counts.get(key)
        badge = f'<span class="n">{_h(n)}</span>' if n else ""
        return (f'<a class="item{on}" href="{href}">'
                f'<svg class="ic" viewBox="0 0 24 24" fill="none" stroke="currentColor" '
                f'stroke-width="1.8" stroke-linecap="round" stroke-linejoin="round">'
                f'<path d="{icon}"/></svg>{_h(label)}{badge}</a>')
    if not admin:
        # One place to go. Listing sections they cannot open reads as a
        # permissions problem rather than as a design decision, and the 403 is
        # the first thing a new person would see.
        return ('<div class="group">Your account</div>'
                + item("profile", "/profile", "Your account",
                       "M20 21v-2a4 4 0 0 0-4-4H8a4 4 0 0 0-4 4v2M12 3a4 4 0 1 1 0 8 4 4 0 0 1 0-8"))
    primary = "".join(item(*i[:4]) for i in NAV_ITEMS[:5])
    extra = ('<div class="group">Configuration</div>'
             + "".join(item(*i[:4]) for i in NAV_ITEMS[5:]))
    return ('<div class="group">Operations</div>' + primary + extra)


def page(request: Request, title: str, body: str, who: str, admin: bool,
         msg: str = "", active: str = "overview", crumbs: str = "",
         counts: dict[str, str] | None = None) -> HTMLResponse:
    """Render the shell around a page body.

    Headers, and why each is here:

      no-store      keeps a shown-once password out of the disk cache and out
                    of the back button.
      no-referrer   the console links out to Grafana; without this the URL of
                    the page that displayed a secret travels in the Referer.
    """
    theme = _theme(request)
    logout = (f'<a class="chip" href="{_h(AUTHELIA_URL)}/logout'
              f'?rd={urllib.parse.quote(f"https://admin.{DOMAIN}/")}">Sign out</a>'
              if AUTHELIA_URL else "")
    cycle = THEMES[(THEMES.index(theme) + 1) % len(THEMES)]
    glyph = {"system": "auto", "dark": "dark", "light": "light"}[theme]
    return HTMLResponse(
        PAGE.format(
            title=_h(title), body=body, msg=msg,
            nav=_nav(active, admin, counts or {}),
            crumbs=crumbs or f"<b>{_h(title)}</b>",
            theme=(f'<form method="post" action="/theme" style="margin:0">'
                   f'<input type="hidden" name="theme" value="{cycle}">'
                   f'<input type="hidden" name="to" value="{_h(active)}">'
                   f'<button class="chip" type="submit" title="Theme">'
                   f'<svg width="13" height="13" viewBox="0 0 24 24" fill="none" '
                   f'stroke="currentColor" stroke-width="1.8" stroke-linecap="round">'
                   f'<circle cx="12" cy="12" r="4"/><path d="M12 2v2M12 20v2M4.9 4.9l1.4 1.4'
                   f'M17.7 17.7l1.4 1.4M2 12h2M20 12h2M4.9 19.1l1.4-1.4M17.7 6.3l1.4-1.4"/>'
                   f'</svg>{glyph}</button></form>'),
            face=(f'<div class="me"><span class="avatar">{_h((who or "?")[:2])}</span>'
                  f'<div class="who">{_h(who)}'
                  f'{" <span class=\"badge\">admin</span>" if admin else ""}</div></div>'
                  f'{logout}') if who else logout,
        ),
        headers={"Cache-Control": "no-store, no-cache, must-revalidate",
                 "Pragma": "no-cache",
                 "Referrer-Policy": "no-referrer",
                 "X-Content-Type-Options": "nosniff",
                 "X-Frame-Options": "DENY"})


def _flash(request: Request) -> str:
    token = request.query_params.get("shown")
    if token:
        text = _pop(token)
        if text:
            return ('<div class="msg ok"><strong>Shown once -- copy it now.</strong>'
                    f'<br><code class="key">{_h(text)}</code></div>')
        return ('<div class="msg bad">That one-time value has already been shown, '
                'or it expired. Issue a new one.</div>')
    ok, bad = request.query_params.get("ok"), request.query_params.get("err")
    if ok:
        return f'<div class="msg ok">{_h(ok)}</div>'
    if bad:
        return f'<div class="msg bad">{_h(bad)}</div>'
    return ""


def _usage_row(row: dict) -> str:
    spend, budget = row["spend"], row["budget"]
    pct = 0 if not budget else min(100, 100 * spend / float(budget))
    cls = "" if pct < 75 else ("warn" if pct < 100 else "bad")
    left = "unlimited" if not budget else _money(max(0.0, float(budget) - spend))
    return (f'<div>{_money(spend)} of {_money(budget)}</div>'
            f'<div class="bar"><i class="{cls}" style="width:{pct:.0f}%"></i></div>'
            f'<div class="dim">{left} remaining</div>')


def _argus(path: str, payload: dict | None = None) -> dict:
    """Call Argus's admin surface. Short timeout: this is a button, not a batch."""
    data = json.dumps(payload).encode() if payload is not None else None
    req = urllib.request.Request(ARGUS_URL + path, data=data,
                                 method="POST" if data is not None else "GET")
    req.add_header("Content-Type", "application/json")
    req.add_header("X-Argus-Admin-Token", ARGUS_ADMIN_TOKEN)
    with urllib.request.urlopen(req, timeout=10) as r:
        body = r.read()
    return json.loads(body) if body else {}


def _rel_time(ts) -> str:
    if not ts:
        return "never"
    delta = time.time() - float(ts)
    if delta < 90:
        return f"{int(delta)}s ago"
    if delta < 5400:
        return f"{int(delta // 60)}m ago"
    if delta < 172800:
        return f"{int(delta // 3600)}h ago"
    return f"{int(delta // 86400)}d ago"


def indexing_card(is_admin: bool = False) -> str:
    """Trigger an index pass across every repo at a chosen branch, and show progress.

    The branch box takes globs and is additive: each project's DEFAULT branch is
    always indexed as well, so entering `develop` means "default plus develop
    wherever develop exists" rather than "develop only". Repos without that
    branch are indexed at their default rather than failing the run, which is
    what makes one branch name usable across an estate that does not share it.
    """
    # ADMINS ONLY, checked here and not merely at the call site. This card
    # lists every repository in the estate along with its indexing state, which
    # is more than a non-admin is entitled to see even though the POST handler
    # would refuse them. Rendering it in self_view was exactly that mistake.
    if not is_admin:
        return ""
    if not (ARGUS_URL and ARGUS_ADMIN_TOKEN):
        return ""
    try:
        st = _argus("/admin/index/status")
    except Exception as exc:                                   # noqa: BLE001
        return (f'<div class="card"><h2>Indexing</h2>'
                f'<div class="msg bad">Argus unreachable: {_h(repr(exc)[:120])}</div></div>')

    job = st.get("job") or {}
    repos = st.get("repos") or []
    running = job.get("state") == "running"

    # The log is kept on screen AFTER the run ends, not only while it runs.
    # It used to be dropped the moment the job went idle, so a failed pass
    # showed "exit 3" and nothing else -- the one piece of information that
    # says WHY sat in the tail the whole time and was then thrown away. That
    # is the difference between "indexing is broken" and "the container cannot
    # reach GitLab".
    tail_text = "\n".join(job.get("tail") or [])
    # Named for what it is, and NOT reused below: `shown` was the repos-table
    # slice a few lines down, and a closure over it meant the log block
    # rendered the list of repositories instead of the log. Python resolves
    # that name when the closure RUNS, by which point it had been rebound.
    log_text = tail_text[-6000:] if len(tail_text) > 6000 else tail_text

    def log_block(label: str = "") -> str:
        if not log_text:
            return ""
        return (f'{f"<p class=dim style=margin:8px_0_4px>{_h(label)}</p>" if label else ""}'
                f'<pre style="max-height:280px;overflow:auto;background:#111;color:#ddd;'
                f'padding:10px;border-radius:6px;font-size:12px;white-space:pre-wrap">'
                f'{_h(log_text)}</pre>')

    if running:
        mode = ("indexing only what the token can see (may be partial)"
                if job.get("allow_partial") else "full enumeration")
        status = (f'<div class="msg">Indexing '
                  f'<b>{_h(", ".join(job.get("branches") or []) or "default branches")}</b>'
                  f' — {_h(mode)}, started {_h(_rel_time(job.get("started")))}. '
                  f'This page refreshes every 5s.</div>')
    elif job.get("finished"):
        rc = job.get("returncode")
        ok = rc == 0
        cls = "msg" if ok else "msg bad"
        meaning = _INDEX_EXIT.get(rc, f"unrecognised exit code {rc}")
        status = (f'<div class="{cls}">Last run finished '
                  f'{_h(_rel_time(job.get("finished")))} — exit {_h(str(rc))}: '
                  f'{_h(meaning)}</div>')
    else:
        status = ('<p class="dim" style="margin:0 0 12px">No run has been started '
                  'from here since Argus last restarted.</p>')

    # Exit 3 is the one code with an action attached, and it is the one an
    # operator is most likely to hit on a fresh deployment: it means "use a
    # token that can see everything, or accept a partial index on purpose".
    hint = ""
    if not running and job.get("finished") and job.get("returncode") == 3:
        hint = ('<div class="msg bad" style="margin:8px 0">If the token is meant to '
                'see only part of the estate, tick <b>Index what the token can see</b> '
                'below and run again — otherwise use a token with admin rights, or add '
                'the service account to every project to index.</div>')
    if job.get("repos_error"):
        # The per-repo table below is empty when this is set, and "empty" is
        # indistinguishable from "nothing indexed yet" -- which is how a
        # NameError in the status route went unnoticed. Say it out loud.
        hint += (f'<div class="msg bad" style="margin:8px 0">Could not read per-repo '
                 f'state: {_h(job["repos_error"])}</div>')

    # Per-repo freshness. Argus records one row PER REF, so a project indexed at
    # two branches legitimately appears twice; the branch column is what tells
    # them apart.
    body = ""
    if repos:
        page_repos = repos[:40]
        trs = "".join(
            '<tr><td>{}</td><td>{}</td><td>{}</td><td>{}</td></tr>'.format(
                _h(r.get("repo") or "?"),
                _h(r.get("branch") or "?") + (" <span class=dim>(default)</span>"
                                              if r.get("branch") == r.get("default_branch") else ""),
                _h(_rel_time(r.get("last_run_at"))),
                ('<span class="bad">timed out</span>' if r.get("timed_out")
                 else (f'{_h(str(r.get("symbols_failed")))} failed'
                       if r.get("symbols_failed") else "ok")))
            for r in page_repos)
        more = (f'<p class="dim">showing {len(page_repos)} of {len(repos)} refs</p>'
                if len(repos) > len(page_repos) else "")
        body = (f'<table><thead><tr><th>Repo</th><th>Branch</th><th>Last indexed</th>'
                f'<th>Result</th></tr></thead><tbody>{trs}</tbody></table>{more}')

    disabled = " disabled" if running else ""
    refresh = ('<meta http-equiv="refresh" content="5">' if running else "")
    checked = " checked" if job.get("allow_partial") else ""
    return (f'{refresh}<div class="card"><h2>Indexing</h2>'
            f'{status}'
            f'{hint}'
            f'{log_block("Run log" if running else "Log from the last run")}'
            f'<form method="post" action="/admin/index" style="margin:12px 0">'
            f'<input name="branches" placeholder="branch or glob, e.g. develop or release/*" '
            f'style="min-width:280px"{disabled}> '
            f'<button class="btn" type="submit"{disabled}>Index all repos</button>'
            f'<p class="dim" style="margin:6px 0 0">Space-separated for several. Each '
            f'project&#39;s default branch is always included; repos without the named '
            f'branch are indexed at their default rather than failing.</p>'
            f'<label class="dim" style="display:block;margin-top:8px;cursor:pointer">'
            f'<input type="checkbox" name="allow_partial" value="1"{checked}{disabled}> '
            f'Index what the token can see, even if that is not the whole estate</label>'
            f'<p class="dim" style="margin:6px 0 0">Off by default, and it should stay off: '
            f'with a token that cannot see every repository, Argus refuses to run rather '
            f'than build an index whose gaps nothing downstream can detect. Tick this only '
            f'when a partial index is the intent.</p></form>'
            f'{body}</div>')


#: What `argus index` can return, in the words of the person reading it. A bare
#: "exit 3" tells an operator nothing and suggests Argus is broken; the number
#: is only meaningful next to the reason, and the reason is what they act on.
_INDEX_EXIT = {
    0: "completed",
    1: "ran, but at least one repository is unhealthy — the log names it",
    3: "could not reach GitLab, or the token cannot enumerate every repository",
    4: "preflight failed — ctags is missing or not Universal Ctags, or the "
       "include graph could not be rebuilt",
    -1: "Argus could not start the run at all",
}


ENV_SAMPLES_DIR = os.environ.get("ENV_SAMPLES_DIR", "/env-samples")
_BLOCK = re.compile(r"^# >>> MODEL.*?^# <<< MODEL[^\n]*$", re.M | re.S)


def _sample(path: str) -> dict | None:
    """Read one env-samples file: its '# SAMPLE:' header lines and MODEL block."""
    try:
        text = open(path, encoding="utf-8").read()
    except OSError:
        return None
    block = _BLOCK.search(text)
    if not block:
        return None
    meta = {m.group(1).strip().lower(): m.group(2).strip()
            for m in re.finditer(r"^# (TITLE|HARDWARE|DOWNLOAD|MEASURED|STATUS): (.*)$", text, re.M)}
    name = re.search(r"^MODEL_NAME=(.*)$", block.group(0), re.M)
    return {"file": os.path.basename(path), "block": block.group(0), "meta": meta,
            "model": name.group(1).strip() if name else "?"}


def model_card() -> str:
    """What is running, and how to switch the whole deployment to a sample.

    Deliberately SHOWS the steps instead of performing them. Switching means
    recreating the engine container, which needs the Docker socket -- and a
    socket in a web app is root on the host for anyone who can reach it. The
    operator chose instructions over that trade.
    """
    running = os.environ.get("MODEL_NAME", "")
    ctx = os.environ.get("MODEL_CONTEXT", "")
    mtp_n = os.environ.get("LLAMACPP_MTP_DRAFT_MAX", "0") or "0"
    mtp = (f"{mtp_n} draft token(s)" if mtp_n != "0" else "")
    gpu_w = os.environ.get("GPU_POWER_LIMIT_W", "")
    cpu_w = os.environ.get("CPU_POWER_LIMIT_W", "")
    now = (f'<p style="margin:0 0 6px"><strong>{_h(running or "not set")}</strong>'
           f'<span class="dim"> &nbsp;{_h(os.environ.get("LLAMACPP_MODEL_FILE", ""))}</span></p>'
           f'<p class="dim" style="margin:0 0 14px">context {_h(ctx or "?")} tokens'
           f' &middot; MTP {"on (" + _h(mtp) + ")" if mtp else "off"}'
           f' &middot; power limit GPU {_h(gpu_w + " W" if gpu_w else "default")},'
           f' CPU {_h(cpu_w + " W" if cpu_w else "firmware")}</p>')
    try:
        files = sorted(f for f in os.listdir(ENV_SAMPLES_DIR) if f.endswith(".env"))
    except OSError:
        files = []
    items = []
    for f in files:
        smp = _sample(os.path.join(ENV_SAMPLES_DIR, f))
        if not smp:
            continue
        m = smp["meta"]
        current = " <span class=badge>running</span>" if smp["model"] == running else ""
        items.append(
            f'<details style="border-top:1px solid var(--line);padding:10px 0">'
            f'<summary style="cursor:pointer"><strong>{_h(m.get("title", smp["file"]))}</strong>{current}'
            f'<span class="dim"> &nbsp;{_h(m.get("hardware", ""))}</span></summary>'
            f'<div class="dim" style="margin:8px 0">{_h(m.get("download", ""))}'
            f'{"<br>" + _h(m.get("measured")) if m.get("measured") else ""}'
            f'{"<br>" + _h(m.get("status")) if m.get("status") else ""}</div>'
            f'<ol class="dim" style="margin:6px 0 8px 18px;padding:0">'
            f'<li>In <code class="key">.env</code>, replace everything from '
            f'<code class="key"># &gt;&gt;&gt; MODEL</code> to <code class="key"># &lt;&lt;&lt; MODEL</code> '
            f'with the block below. Keep your own <code class="key">LLAMACPP_MODEL_DIR</code> path.</li>'
            f'<li>Run <code class="key">docker compose up -d</code> in the stack directory. '
            f'A model not on disk yet is downloaded first: <code class="key">docker logs -f model-init</code>.</li>'
            f'<li>The model list then shows <strong>{_h(smp["model"])}</strong> and nothing else.</li></ol>'
            f'<pre style="white-space:pre-wrap;background:#0e1116;border:1px solid var(--line);'
            f'border-radius:7px;padding:10px;font-size:12px;overflow-x:auto">{_h(smp["block"])}</pre>'
            f'<p class="dim" style="margin:0">Whole file: <code class="key">env-samples/{_h(smp["file"])}</code></p>'
            f'</details>')
    samples = "".join(items) or '<p class="dim">No env-samples mounted.</p>'
    return (f'<div class="card"><h2>Model</h2>{now}'
            f'<p class="dim" style="margin:0 0 4px">Switch the whole deployment to one of these:</p>'
            f'{samples}</div>')


def monitoring_card() -> str:
    if not GRAFANA_URL:
        return ""
    return (f'<div class="card"><h2>Monitoring</h2>'
            f'<p class="dim" style="margin:0 0 12px">Live usage per person, GPU '
            f'and engine health.</p>'
            f'<a class="btn" href="{_h(GRAFANA_URL)}/d/usage-by-user" '
            f'target="_blank" rel="noopener noreferrer">Usage by person</a> '
            f'<a class="btn" href="{_h(GRAFANA_URL)}/d/llm-overview" '
            f'target="_blank" rel="noopener noreferrer">LLM overview</a> '
            f'<a class="btn" href="{_h(GRAFANA_URL)}/d/resources" '
            f'target="_blank" rel="noopener noreferrer">Resources</a> '
            f'<a class="btn" href="{_h(GRAFANA_URL)}" target="_blank" '
            f'rel="noopener noreferrer">All dashboards</a></div>')


# -------------------------------------------------------------------- views --
# ------------------------------------------------------------- data shaping --
#: How many people fit on one page. The table used to render every account in
#: one <table>, which is fine for five and unusable for two hundred.
PAGE_SIZE = 25


def _probe(url: str, timeout: float = 2.5) -> tuple[bool, str]:
    """Is something answering there, and how fast. Never raises.

    TLS is not verified: the stack serves a self-signed certificate and this is
    a liveness check, not a trust decision. The decision it replaces --
    "everything looks healthy but one tab is empty" -- is worth far more than
    the reassurance of a verified handshake against our own container.
    """
    import ssl

    ctx = ssl.create_default_context()
    ctx.check_hostname = False
    ctx.verify_mode = ssl.CERT_NONE
    started = time.time()
    try:
        with urllib.request.urlopen(url, timeout=timeout, context=ctx) as r:
            ms = (time.time() - started) * 1000
            return 200 <= r.status < 400, f"{r.status} · {ms:.0f} ms"
    except urllib.error.HTTPError as exc:
        # An HTTP error is still an answer. Authelia replying 401 to a bare GET
        # means it is up, which is the question being asked.
        ms = (time.time() - started) * 1000
        return exc.code < 500, f"{exc.code} · {ms:.0f} ms"
    except Exception as exc:  # noqa: BLE001 - a probe must never break the page
        return False, type(exc).__name__


def _services() -> list[tuple[str, bool, str]]:
    """The five things this console can see, and what each one is for.

    No Docker socket here on purpose: this container holds the LiteLLM master
    key and writes Authelia's account file, and mounting the socket would give
    it the host as well. Reachability over HTTP is what it can honestly claim.
    """
    return [
        ("LiteLLM gateway", *_probe(LITELLM.rstrip("/") + "/health/liveliness")),
        ("Argus index", *_probe(ARGUS_URL.rstrip("/") + "/healthz")),
        ("Prometheus", *_probe(PROMETHEUS_URL + "/-/healthy")),
        ("Grafana", *_probe(GRAFANA_PROBE_URL + "/api/health")),
        ("Authelia", *_probe(AUTHELIA_PROBE_URL + "/api/health")),
    ]


def _people(with_keys: bool = False) -> list[dict]:
    """Every account, joined across both systems.

    Authelia owns who exists and what they may do; LiteLLM owns what they have
    spent and what keys they hold. The two are keyed by email, because that is
    the one field both agree on -- a username can differ between them, which is
    the whole reason Argus resolves people by email.
    """
    litellm_users, note = spend_by_user()
    rows = []
    for username, entry in sorted(load_users().items()):
        email = (entry.get("email") or username).lower()
        u = litellm_users.get(email, {"spend": 0.0, "budget": None})
        groups = entry.get("groups") or []
        row = {
            "username": username,
            "email": email,
            "display": entry.get("displayname") or username,
            "groups": groups,
            "admin": ADMIN_GROUP in groups,
            "spend": float(u.get("spend") or 0.0),
            "budget": u.get("budget"),
            "user_id": u.get("user_id") or email,
        }
        if with_keys:
            row["keys"] = keys_for(row["user_id"])
        rows.append(row)
    return rows, note


def _svc_list(services) -> str:
    out = []
    for name, ok, detail in services:
        out.append(f'<div class="svc"><span class="dot {"ok" if ok else "bad"}"></span>'
                   f'<span class="name">{_h(name)}</span>'
                   f'<span class="ms">{_h(detail)}</span></div>')
    return "".join(out)


def _pager(base: str, page: int, pages: int, q: str = "") -> str:
    if pages <= 1:
        return ""
    extra = f"&q={urllib.parse.quote(q)}" if q else ""

    def link(n, label, off=False):
        cls = ' class="off"' if off else ""
        return f'<a{cls} href="{base}?p={n}{extra}">{label}</a>'

    window = [n for n in (page - 1, page, page + 1) if 1 <= n <= pages]
    middle = "".join(link(n, str(n)) if n != page else f"<span>{n}</span>" for n in window)
    return (f'<div class="pager">Page {page} of {pages}{link(page - 1, "‹", page <= 1)}'
            f'{middle}{link(page + 1, "›", page >= pages)}</div>')


def _usage_cell(row: dict, who: str = "") -> str:
    return _usage_row({"spend": row["spend"], "budget": row["budget"]})


# ------------------------------------------------------------------- views --
def overview_view(request: Request, who: Caller) -> Response:
    people, note = _people()
    services = _services()
    up = sum(1 for _, ok, _ in services if ok)
    spend_total = sum(p["spend"] for p in people)
    admins = sum(1 for p in people if p["admin"])
    over = [p for p in people if p["budget"] and p["spend"] >= float(p["budget"])]

    tiles = ('<div class="tiles">'
             + _tile("Services up", f"{up}/{len(services)}",
                     "reachable from this container")
             + _tile("People", str(len(people)), f"{admins} admin")
             + _tile("Spend", _money(spend_total), "across every key and chat")
             + _tile("Over credit", str(len(over)),
                     "ask before they notice" if over else "nobody")
             + "</div>")

    attention = ""
    if over:
        names = ", ".join(_h(p["username"]) for p in over[:5])
        more = f" and {len(over) - 5} more" if len(over) > 5 else ""
        attention = (f'<div class="msg bad"><strong>{len(over)} account(s) at or past '
                     f'their credit.</strong><br>{names}{more}</div>')
    elif note:
        attention = _degraded(note)

    body = ('<h1 class="page">Overview</h1>'
            '<p class="lede">Everything this console can see, at a glance.</p>'
            + tiles + attention
            + f'<div class="card"><h2>Services</h2>{_svc_list(services)}</div>'
            + model_card()
            + '<div class="card"><h2>Jump to</h2><div class="sectionnav">'
              '<a href="/people">People</a><a href="/indexing">Indexing</a>'
              '<a href="/monitoring">Monitoring</a>'
              '<a href="' + _h(GRAFANA_URL) + '" target="_blank" rel="noopener noreferrer">Grafana ↗</a>'
              '</div></div>')
    return page(request, "Overview", body, who.label, True, _flash(request),
                active="overview", crumbs="<b>Overview</b>",
                counts={"people": str(len(people))})


def people_view(request: Request, who: Caller) -> Response:
    q = (request.query_params.get("q") or "").strip().lower()
    try:
        page_no = max(1, int(request.query_params.get("p") or 1))
    except ValueError:
        page_no = 1

    people, note = _people()
    if q:
        people = [p for p in people
                  if q in p["username"].lower() or q in p["email"].lower()
                  or q in p["display"].lower()]

    pages = max(1, (len(people) + PAGE_SIZE - 1) // PAGE_SIZE)
    page_no = min(page_no, pages)
    window = people[(page_no - 1) * PAGE_SIZE: page_no * PAGE_SIZE]

    rows = []
    for p in window:
        rows.append(
            f'<tr><td><a class="plain" href="/people/{urllib.parse.quote(p["username"])}">'
            f'<strong>{_h(p["username"])}</strong></a>'
            f'{" <span class=badge>admin</span>" if p["admin"] else ""}'
            f'<div class="dim">{_h(p["email"])}</div></td>'
            f'<td>{_usage_cell(p)}</td>'
            f'<td><form class="row" method="post" action="/admin/budget">'
            f'<input type="hidden" name="email" value="{_h(p["email"])}">'
            f'<input type="hidden" name="to" value="people">'
            f'<input type="number" name="budget" step="1" min="0" style="width:88px" '
            f'value="{_h(p["budget"] or "")}" placeholder="none">'
            f'<button class="ghost" type="submit">Set</button></form></td>'
            f'<td><form method="post" action="/admin/rotate" style="display:inline">'
            f'<input type="hidden" name="email" value="{_h(p["email"])}">'
            f'<input type="hidden" name="to" value="people">'
            f'<button class="ghost" type="submit">New key</button></form> '
            f'<a class="chip" href="/people/{urllib.parse.quote(p["username"])}">Open</a></td></tr>')
    if not rows:
        rows = ['<tr><td colspan="4"><div class="empty">'
                + (f'No account matches “{_h(q)}”.' if q else "No accounts yet.")
                + '</div></td></tr>']

    toolbar = (
        '<div class="toolbar">'
        f'<form method="get" action="/people">'
        f'<input name="q" value="{_h(q)}" placeholder="Search name, email or username" '
        f'style="min-width:240px"><button class="ghost" type="submit">Search</button>'
        + ('<a class="chip" href="/people">Clear</a>' if q else "") + '</form>'
        '<div class="spacer"></div>'
        '<a class="chip" href="/export/people.csv">Export CSV</a>'
        '</div>')

    body = ('<h1 class="page">People</h1>'
            f'<p class="lede">{len(people)} account(s)'
            + (f' matching “{_h(q)}”' if q else "")
            + ' · credit, keys and access.</p>'
            + _degraded(note) + toolbar
            + '<div class="card"><div class="tablewrap"><table>'
              '<tr><th>Person</th><th>Usage / credit</th><th>Credit</th><th></th></tr>'
            + "".join(rows) + '</table></div>'
            + _pager("/people", page_no, pages, q) + '</div>')
    return page(request, "People", body, who.label, True, _flash(request),
                active="people", crumbs="<b>People</b>",
                counts={"people": str(len(people))})


def person_view(request: Request, who: Caller, username: str) -> Response:
    people, note = _people(with_keys=True)
    me = next((p for p in people if p["username"] == username), None)
    if me is None:
        return page(request, "Not found",
                    f'<h1 class="page">No such person</h1>'
                    f'<div class="msg bad">There is no account named {_h(username)}.</div>'
                    f'<p><a class="chip" href="/people">Back to people</a></p>',
                    who.label, True, "", active="people",
                    crumbs='<a href="/people">People</a> / <b>Not found</b>')

    keys = me.get("keys") or []
    key_rows = "".join(
        f'<tr><td><code class="key">{_h(k["token"][:18])}…</code></td>'
        f'<td>{_h(k["alias"] or "—")}</td><td>{_money(k["spend"])}</td></tr>'
        for k in keys) or ('<tr><td colspan="3"><div class="empty">'
                           'No API key issued yet.</div></td></tr>')

    confirm_delete = ("return confirm('Delete ' + this.dataset.u + '? "
                      "Their Authelia account and every API key stop working immediately. "
                      "This cannot be undone.')")
    body = (
        f'<h1 class="page">{_h(me["display"])}</h1>'
        f'<p class="lede">{_h(me["username"])} · {_h(me["email"])}'
        + (" · <span class=\"badge\">admin</span>" if me["admin"] else "") + '</p>'
        + _degraded(note)
        + '<div class="tiles">'
        + _tile("Spent", _money(me["spend"]), "lifetime, through the gateway")
        + _tile("Credit", _money(me["budget"]) if me["budget"] else "unlimited",
                "set below")
        + _tile("Keys", str(len(keys)), "active API keys")
        + '</div>'
        + f'<div class="card"><h2>Credit</h2>'
          f'<form class="row" method="post" action="/admin/budget">'
          f'<input type="hidden" name="email" value="{_h(me["email"])}">'
          f'<input type="hidden" name="to" value="person">'
          f'<input type="hidden" name="username" value="{_h(me["username"])}">'
          f'<input type="number" name="budget" step="1" min="0" style="width:120px" '
          f'value="{_h(me["budget"] or "")}" placeholder="empty = unlimited">'
          f'<button type="submit">Set credit</button></form>'
          f'<p class="dim" style="margin:10px 0 0">Empty means no limit. '
          f'A person at or past their credit is refused by the gateway, not by Argus.</p></div>'
        + f'<div class="card"><h2>API keys ({len(keys)})</h2>'
          f'<div class="tablewrap"><table><tr><th>Key</th><th>Alias</th><th>Spend</th></tr>'
          f'{key_rows}</table></div>'
          f'<form method="post" action="/admin/rotate" style="margin-top:12px">'
          f'<input type="hidden" name="email" value="{_h(me["email"])}">'
          f'<input type="hidden" name="to" value="person">'
          f'<input type="hidden" name="username" value="{_h(me["username"])}">'
          f'<button class="ghost" type="submit">Issue a new key</button></form>'
          f'<p class="dim" style="margin:10px 0 0">The new key is shown once. '
          f'Existing keys keep working until you delete them.</p></div>'
        + f'<div class="card"><h2>Account</h2>'
          f'<form class="row" method="post" action="/admin/reset">'
          f'<input type="hidden" name="username" value="{_h(me["username"])}">'
          f'<input type="hidden" name="to" value="person">'
          f'<button class="ghost" type="submit">Reset password</button></form>'
          f'<form method="post" action="/admin/delete" style="margin-top:12px" '
          f'data-u="{_h(me["username"])}" onsubmit="{confirm_delete}">'
          f'<input type="hidden" name="username" value="{_h(me["username"])}">'
          f'<button class="btn danger" type="submit">Delete this person</button></form>'
          f'<p class="dim" style="margin:10px 0 0">Deleting removes the Authelia '
          f'account and every API key it holds. It cannot be undone.</p></div>')
    return page(request, me["display"], body, who.label, True, _flash(request),
                active="people",
                crumbs=f'<a href="/people">People</a> / <b>{_h(me["username"])}</b>')


def model_view(request: Request, who: Caller) -> Response:
    body = ('<h1 class="page">Model</h1>'
            '<p class="lede">What is serving, and what it was configured with.</p>'
            + model_card())
    return page(request, "Model", body, who.label, True, _flash(request),
                active="model", crumbs="<b>Model</b>")


def indexing_view(request: Request, who: Caller) -> Response:
    body = ('<h1 class="page">Indexing</h1>'
            '<p class="lede">The Argus code index: what it covers and what it is doing.</p>'
            + indexing_card(is_admin=True))
    return page(request, "Indexing", body, who.label, True, _flash(request),
                active="indexing", crumbs="<b>Indexing</b>")


def monitoring_view(request: Request, who: Caller) -> Response:
    links = [
        ("Grafana", GRAFANA_URL, "dashboards: usage, GPU, logs, Argus, indexing"),
        ("Prometheus", PROMETHEUS_URL, "raw metrics and alert rules"),
        ("Argus MCP", f"https://argus.{DOMAIN}/mcp", "the code index, for agents"),
        ("Authelia", AUTHELIA_URL, "the identity provider behind every login"),
    ]
    rows = "".join(
        f'<div class="svc"><span class="name">{_h(n)}</span>'
        f'<span class="ms dim">{_h(d)}</span>'
        f'<a class="chip" href="{_h(u)}" target="_blank" rel="noopener noreferrer">Open ↗</a></div>'
        for n, u, d in links)
    body = ('<h1 class="page">Monitoring</h1>'
            '<p class="lede">Where the numbers live. This console links out rather '
            'than rebuilding charts badly.</p>'
            + f'<div class="card"><h2>Services</h2>{_svc_list(_services())}</div>'
            + f'<div class="card"><h2>Dashboards</h2>{rows}</div>'
            + '<div class="card"><h2>Sign in</h2>'
              '<p class="dim" style="margin:0">Grafana and the Argus MCP endpoint both '
              'sit behind the same single sign-on as this console, so an open tab is '
              'usually already signed in.</p></div>')
    return page(request, "Monitoring", body, who.label, True, _flash(request),
                active="monitoring", crumbs="<b>Monitoring</b>")


#: Read-only view of the effective configuration. Deliberately an allow-list:
#: an accidental `os.environ` dump is how a console leaks the LiteLLM master
#: key into a screenshot, and this container holds several secrets.
SETTINGS_VIEW = (
    ("Deployment", [
        ("LLM_DOMAIN", "the domain every hostname hangs off"),
        ("COMPOSE_PROFILES", "which parts of the stack are running"),
        ("ADMIN_GROUP", "the Authelia group that sees this console"),
    ]),
    ("Model", [
        ("MODEL_NAME", "what the gateway serves"),
        ("MODEL_CONTEXT", "token window"),
        ("MODEL_MAX_OUTPUT", "maximum reply length"),
        ("MODEL_REASONING_EFFORT", "engine default thinking level"),
        ("MODEL_ENABLE_THINKING", "whether it thinks at all"),
        ("THINKING_PRESETS", "per-chat presets offered in Open WebUI"),
    ]),
    ("Argus", [
        ("ARGUS_URL", "where the code index is reached"),
        ("ARGUS_GITLAB_URL", "the GitLab it indexes"),
        ("ARGUS_GITLAB_AUTH", "token or password"),
        ("ARGUS_GITLAB_USERNAME", "who it signs in as, in password mode"),
    ]),
)


def settings_view(request: Request, who: Caller) -> Response:
    blocks = []
    for title, keys in SETTINGS_VIEW:
        rows = "".join(
            f'<dt>{_h(k)}<div class="dim">{_h(why)}</div></dt>'
            f'<dd>{_h(os.environ.get(k) or "—")}</dd>'
            for k, why in keys)
        blocks.append(f'<div class="card"><h2>{_h(title)}</h2><dl class="kv">{rows}</dl></div>')
    body = ('<h1 class="page">Settings</h1>'
            '<p class="lede">The effective configuration. Read-only — these come from '
            '<code>.env</code> and are applied at container start.</p>'
            + "".join(blocks)
            + '<div class="card"><h2>Changing these</h2>'
              '<p class="dim" style="margin:0">Edit <code>stack/.env</code> and run '
              '<code>docker compose up -d</code>. Secrets are deliberately not shown '
              'here — not even masked, because a masked value still leaks its length '
              'and the first characters into every screenshot.</p></div>')
    return page(request, "Settings", body, who.label, True, _flash(request),
                active="settings", crumbs="<b>Settings</b>")


def profile_view(request: Request, who: Caller) -> Response:
    """The signed-in person's own page, admin or not."""
    people, note = _people(with_keys=True)
    me = next((p for p in people if p["username"] == who.label), None)
    if me is None:
        me = {"display": who.label, "username": who.label, "email": who.email or who.label,
              "admin": who.is_admin, "spend": 0.0, "budget": None, "keys": [], "groups": []}
    keys = me.get("keys") or []
    key_rows = "".join(
        f'<tr><td><code class="key">{_h(k["token"][:18])}…</code></td>'
        f'<td>{_h(k["alias"] or "—")}</td><td>{_money(k["spend"])}</td></tr>'
        for k in keys) or ('<tr><td colspan="3"><div class="empty">'
                           'No API key yet — ask an administrator for one.</div></td></tr>')
    body = (
        f'<h1 class="page">{_h(me["display"])}</h1>'
        f'<p class="lede">{_h(me["email"])}</p>' + _degraded(note)
        + '<div class="tiles">'
        + _tile("Spent", _money(me["spend"]), "lifetime")
        + _tile("Credit", _money(me["budget"]) if me["budget"] else "unlimited",
                "set by an administrator")
        + _tile("Keys", str(len(keys)), "yours to use")
        + '</div>'
        + f'<div class="card"><h2>Your API keys</h2>'
          f'<div class="tablewrap"><table><tr><th>Key</th><th>Alias</th><th>Spend</th></tr>'
          f'{key_rows}</table></div></div>'
        + '<div class="card"><h2>Change password</h2>'
          '<form class="row" method="post" action="/password">'
          '<input type="password" name="current" placeholder="Current password" required>'
          '<input type="password" name="new" placeholder="New password (min 12)" '
          'minlength="12" required>'
          '<button type="submit">Update</button></form>'
          '<p class="dim" style="margin:10px 0 0">Minimum 12 characters. This changes '
          'the password you sign in to the portal with.</p></div>')
    return page(request, "Your account", body, who.label, who.is_admin,
                _flash(request), active="profile", crumbs="<b>Your account</b>")


async def index(request: Request) -> Response:
    who = caller(request)
    if who is None:
        return HTMLResponse("<h1>403</h1><p>Sign in through the portal.</p>",
                            status_code=403)
    return overview_view(request, who) if who.is_admin else profile_view(request, who)


def _degraded(note: str | None) -> str:
    return f'<div class="msg bad">{_h(note)}</div>' if note else ""


# ------------------------------------------------------------------ actions --
# Generated credentials are held here for ONE retrieval instead of being put in
# the redirect URL. A password in a query string is written to browser history,
# to Traefik's access log, and -- worst of the three -- into the Referer header
# sent to Grafana the moment someone clicks a monitoring button. None of those
# are places a freshly minted API key should exist.
#
# In-process and deliberately not persisted: a restart losing an unread secret
# is the correct outcome for something labelled "shown once".
_ONCE: dict[str, tuple[float, str]] = {}
_ONCE_TTL = 300.0


def _stash(text: str) -> str:
    now = time.time()
    for k, (exp, _) in list(_ONCE.items()):
        if exp < now:
            _ONCE.pop(k, None)
    token = secrets.token_urlsafe(16)
    _ONCE[token] = (now + _ONCE_TTL, text)
    return token


def _pop(token: str) -> str:
    entry = _ONCE.pop(token, None)
    if not entry:
        return ""
    exp, text = entry
    return text if exp >= time.time() else ""


#: Where a POST sends you back to. The console has real pages now, so an action
#: taken on /people must return to /people -- landing on the overview after
#: every button is the kind of thing that makes a console feel unfinished.
_RETURN = {"overview": "/", "people": "/people", "model": "/model",
           "indexing": "/indexing", "monitoring": "/monitoring",
           "settings": "/settings"}


def _where(name: str) -> str:
    return _RETURN.get((name or "").strip(), "/")


def _dest(form) -> str:
    """Where to send the browser after a POST.

    "person" is special: the action was taken on one account's page, so it goes
    back to that page rather than to the list. Landing on /people after
    resetting one password loses the context the operator was working in.
    """
    to = str(form.get("to") or "")
    if to == "person":
        name = str(form.get("username") or "").strip()
        return f"/people/{urllib.parse.quote(name)}" if name else "/people"
    return _where(to)


def _back(msg: str = "", err: str = "", to: str = "") -> RedirectResponse:
    q = urllib.parse.urlencode({"ok": msg} if msg else {"err": err})
    sep = "&" if "?" in to else "?"
    return RedirectResponse(f"{to or '/'}{sep}{q}" if (msg or err) else (to or "/"),
                            status_code=303)


def _back_secret(text: str, to: str = "") -> RedirectResponse:
    """Redirect carrying a TOKEN, never the secret itself."""
    base = to or "/"
    sep = "&" if "?" in base else "?"
    return RedirectResponse(f"{base}{sep}shown={_stash(text)}", status_code=303)


async def change_password(request: Request) -> Response:
    who = caller(request)
    if who is None:
        return JSONResponse({"error": "forbidden"}, status_code=403)
    form = await request.form()
    current, new = str(form.get("current") or ""), str(form.get("new") or "")
    if len(new) < 12:
        return _back(err="Password must be at least 12 characters.")
    users = load_users()
    entry = users.get(who.user)
    if entry is None:
        return _back(err=f"No Authelia account named {who.user}.")
    # Verify the CURRENT password even though Authelia already authenticated the
    # session. A live session is not proof the person at the keyboard knows the
    # password -- an unlocked laptop would otherwise be enough to lock the owner
    # out of their own account.
    try:
        _hasher.verify(entry.get("password") or "", current)
    except Exception:            # noqa: BLE001 - any verify failure is the same answer
        return _back(err="Current password is incorrect.")
    set_password(who.user, new)
    return _back("Password updated."
                 + (RELOAD_NOTE if WARN_RELOAD else ""))


def _require_admin(request: Request) -> Caller | None:
    who = caller(request)
    return who if (who and who.is_admin) else None


async def admin_create(request: Request) -> Response:
    who = _require_admin(request)
    if who is None:
        return JSONResponse({"error": "forbidden"}, status_code=403)
    form = await request.form()
    username = str(form.get("username") or "").strip().lower()
    email = str(form.get("email") or "").strip().lower()
    if not _USERNAME.match(username):
        return _back(err="Username must be 2-64 chars of a-z 0-9 . _ -",
                     to=_where(form.get("to")) or "/people")
    if not _EMAIL.match(email):
        return _back(err="That does not look like an email address.",
                     to=_where(form.get("to")) or "/people")
    users = load_users()
    if username in users:
        return _back(err=f"{username} already exists.",
                     to=_where(form.get("to")) or "/people")

    password = secrets.token_urlsafe(15)
    groups = [ADMIN_GROUP] if form.get("admin") else ["users"]
    users[username] = {
        "disabled": False,
        "displayname": str(form.get("displayname") or username),
        "password": _hasher.hash(password),
        "email": email,
        "groups": groups,
    }
    save_users(users)

    budget = form.get("budget")
    quota = {"max_budget": float(budget)} if budget else {}
    key = ""
    try:
        api("/user/new", {"user_id": email, "user_email": email,
                          "user_role": "internal_user", **quota})
    except urllib.error.HTTPError:
        pass                      # already present is fine; update below
    try:
        if quota:
            api("/user/update", {"user_id": email, **quota})
            # The end-user record is what makes a ceiling BIND on the chat path,
            # where everyone shares one gateway key. Without it the budget is
            # tracked and not enforced -- see deploy/identity-proxy/app.py.
            api("/end_user/new", {"user_id": email, **quota})
        key = api("/key/generate",
                  {"user_id": email, "key_alias": f"panel-{username}"}).get("key", "")
    except urllib.error.HTTPError as exc:
        return _back_secret(f"Created {username} in Authelia, but LiteLLM refused "
                            f"(HTTP {exc.code}) so there is no API key yet. "
                            f"Password: {password}",
                            to=f"/people/{urllib.parse.quote(username)}")
    return _back_secret(f"{username} -- password: {password} -- API key: {key}"
                        + (RELOAD_NOTE if WARN_RELOAD else ""),
                        to=f"/people/{urllib.parse.quote(username)}")


async def admin_rotate(request: Request) -> Response:
    who = _require_admin(request)
    if who is None:
        return JSONResponse({"error": "forbidden"}, status_code=403)
    form = await request.form()
    email = str(form.get("email") or "").strip().lower()
    if not email:
        return _back(err="No user given.", to=_dest(form))
    # Delete first, then mint: the reverse order leaves a window in which the
    # old key still works alongside the new one, which is the opposite of what
    # "revoke" is asked for here.
    existing = [k["token"] for k in keys_for(email) if k["token"]]
    if existing:
        try:
            api("/key/delete", {"keys": existing})
        except urllib.error.HTTPError as exc:
            return _back(err=f"Could not revoke the old key: HTTP {exc.code}",
                         to=_dest(form))
    try:
        key = api("/key/generate",
                  {"user_id": email, "key_alias": f"panel-{email}"}).get("key", "")
    except urllib.error.HTTPError as exc:
        return _back(err=f"Revoked {len(existing)} key(s) but minting failed: "
                         f"HTTP {exc.code}")
    return _back_secret(f"Revoked {len(existing)} key(s) for {email}. "
                        f"New API key: {key}", to=_dest(form))


async def admin_reset(request: Request) -> Response:
    who = _require_admin(request)
    if who is None:
        return JSONResponse({"error": "forbidden"}, status_code=403)
    form = await request.form()
    username = str(form.get("username") or "").strip().lower()
    password = secrets.token_urlsafe(15)
    try:
        set_password(username, password)
    except KeyError:
        return _back(err=f"No Authelia account named {username}.",
                     to=_where(form.get("to")) or "/people")
    return _back_secret(f"New password for {username}: {password}"
                        + (RELOAD_NOTE if WARN_RELOAD else ""))


async def admin_index(request: Request) -> Response:
    """Start an index pass across every repo, optionally at extra branches."""
    who = _require_admin(request)
    if who is None:
        return JSONResponse({"error": "forbidden"}, status_code=403)
    if not (ARGUS_URL and ARGUS_ADMIN_TOKEN):
        return _back(err="Indexing is not configured: ARGUS_ADMIN_TOKEN is unset.",
                     to="/indexing")
    form = await request.form()
    raw = str(form.get("branches") or "").strip()
    # Space-separated, deduplicated, order preserved. Empty means "whatever the
    # config already says", which is each project's default branch.
    branches = list(dict.fromkeys(b for b in raw.split() if b))
    allow_partial = bool(form.get("allow_partial"))
    try:
        _argus("/admin/index", {"branches": branches,
                                "allow_partial": allow_partial})
    except urllib.error.HTTPError as exc:
        if exc.code == 409:
            return _back(err="An index run is already in progress.", to="/indexing")
        return _back(err=f"Argus refused the request: HTTP {exc.code}", to="/indexing")
    except Exception as exc:                                   # noqa: BLE001
        return _back(err=f"Could not reach Argus: {repr(exc)[:120]}", to="/indexing")
    label = ", ".join(branches) if branches else "default branches"
    if allow_partial:
        label += "; partial enumeration allowed"
    return _back(msg=f"Indexing started across all repos ({label}).", to="/indexing")


async def admin_budget(request: Request) -> Response:
    who = _require_admin(request)
    if who is None:
        return JSONResponse({"error": "forbidden"}, status_code=403)
    form = await request.form()
    email = str(form.get("email") or "").strip().lower()
    raw = str(form.get("budget") or "").strip()
    try:
        quota = {"max_budget": float(raw)} if raw else {"max_budget": None}
    except ValueError:
        return _back(err="Credit must be a number.", to=_dest(form))
    try:
        api("/user/update", {"user_id": email, **quota})
        api("/end_user/update", {"user_id": email, **quota})
    except urllib.error.HTTPError as exc:
        return _back(err=f"LiteLLM refused: HTTP {exc.code}", to=_dest(form))
    return _back(f"Credit for {email} set to {_money(quota['max_budget'])}.",
                 to=_dest(form))


async def healthz(_request: Request) -> Response:
    checks = {"litellm": False, "users_file": os.path.exists(USERS_FILE)}
    try:
        api("/health/liveliness")
        checks["litellm"] = True
    except Exception:            # noqa: BLE001
        pass
    ok = all(checks.values())
    return JSONResponse({"status": "ok" if ok else "degraded", **checks,
                         "time": datetime.now(timezone.utc).isoformat()},
                        status_code=200 if ok else 503)



# ------------------------------------------------------------------- theme --
async def theme(request: Request) -> Response:
    """Switch the console's theme, then return to the page you were on.

    A POST rather than a link so it is not prefetched by the browser or followed
    by a crawler, and so the choice is not part of a URL someone pastes.
    """
    form = await request.form()
    choice = str(form.get("theme") or "system").lower()
    if choice not in THEMES:
        choice = "system"
    response = RedirectResponse(_where(str(form.get("to") or "")), status_code=303)
    # Not HttpOnly: this cookie is a display preference, not a credential, and
    # nothing here reads it from JavaScript -- but marking it HttpOnly would
    # imply it protects something.
    response.set_cookie("theme", choice, max_age=60 * 60 * 24 * 365,
                        samesite="lax", path="/")
    return response


# ------------------------------------------------------------------ export --
def _csv_cell(value: object) -> str:
    """Quote a CSV field the way Excel expects.

    The filename and the values are attacker-controlled in the sense that a
    username can contain anything; a leading = or + makes a spreadsheet treat
    the cell as a formula, so those are prefixed. Spend is numbers only, which
    is why it is safe to write unquoted.
    """
    text = "" if value is None else str(value)
    if text[:1] in ("=", "+", "-", "@"):
        text = "'" + text
    return '"' + text.replace('"', '""') + '"'


async def export_people(request: Request) -> Response:
    who = _require_admin(request)
    if who is None:
        return JSONResponse({"error": "forbidden"}, status_code=403)
    people, _ = _people()
    lines = ["username,display_name,email,admin,spend,budget,credit_left"]
    for p in people:
        left = ("" if not p["budget"]
                else f'{max(0.0, float(p["budget"]) - p["spend"]):.4f}')
        lines.append(",".join(_csv_cell(v) for v in (
            p["username"], p["display"], p["email"], "yes" if p["admin"] else "no",
            f'{p["spend"]:.4f}', p["budget"] if p["budget"] else "", left)))
    stamp = time.strftime("%Y-%m-%d")
    return Response("\n".join(lines) + "\n", media_type="text/csv",
                    headers={"Content-Disposition":
                             f'attachment; filename="people-{stamp}.csv"'})


# ------------------------------------------------------------------ delete --
def _delete_refusal(who: Caller, username: str, email: str,
                    users: dict) -> str | None:
    """Why this account must not be deleted, or None if it may be.

    Two refusals, and the first one is here because it did not work:

    Deleting YOURSELF is matched on every identity rather than on one of them.
    The first version compared the username to `who.label`, which is the EMAIL
    -- so the two never matched, the guard did nothing, and the administrator
    account was deleted from a live stack while I was checking that the guard
    worked. A comparison between two different fields always fails open, and
    this one failed open on the account that administers everything.

    Deleting the LAST administrator is the same failure one step away: one
    click leaves a deployment nobody can administer, and every remaining
    account is refused the page it would take to undo.
    """
    mine = {x for x in ((who.user or "").lower(), (who.email or "").lower(),
                        (who.label or "").lower()) if x}
    if username.lower() in mine or (email or "").lower() in mine:
        return "Refusing to delete the account you are signed in as."
    admins = [n for n, e in users.items()
              if ADMIN_GROUP in ((e or {}).get("groups") or [])]
    if username in admins and len(admins) <= 1:
        return (f"{username} is the only administrator. "
                f"Make someone else an admin first.")
    return None


async def admin_delete(request: Request) -> Response:
    """Remove an account, its password and its API keys.

    There was no way to do this at all, which meant an offboarded person kept a
    working key until someone edited users.yml by hand.
    """
    who = _require_admin(request)
    if who is None:
        return JSONResponse({"error": "forbidden"}, status_code=403)
    form = await request.form()
    username = str(form.get("username") or "").strip().lower()
    target = "/people"

    users = load_users()
    entry = users.get(username)
    if entry is None:
        return _back(err=f"No account named {username}.", to=target)
    email = (entry.get("email") or username).lower()

    refusal = _delete_refusal(who, username, email, users)
    if refusal:
        return _back(err=refusal, to=target)

    keys_deleted = 0
    try:
        for key in keys_for(email):
            token = key.get("token") or ""
            if not token:
                continue
            try:
                api("/key/delete", {"keys": [token]})
                keys_deleted += 1
            except urllib.error.HTTPError:
                pass
    except Exception:                                        # noqa: BLE001
        pass

    del users[username]
    save_users(users)
    return _back(f"Deleted {username}: account removed, {keys_deleted} key(s) revoked.",
                 to=target)


# ------------------------------------------------------------------- pages --
async def people_page(request: Request) -> Response:
    who = _require_admin(request)
    return people_view(request, who) if who else _forbidden()


async def person_page(request: Request) -> Response:
    who = _require_admin(request)
    if who is None:
        return _forbidden()
    return person_view(request, who, request.path_params.get("username", ""))


async def model_page(request: Request) -> Response:
    who = _require_admin(request)
    return model_view(request, who) if who else _forbidden()


async def indexing_page(request: Request) -> Response:
    who = _require_admin(request)
    return indexing_view(request, who) if who else _forbidden()


async def monitoring_page(request: Request) -> Response:
    who = _require_admin(request)
    return monitoring_view(request, who) if who else _forbidden()


async def settings_page(request: Request) -> Response:
    who = _require_admin(request)
    return settings_view(request, who) if who else _forbidden()


async def profile_page(request: Request) -> Response:
    who = caller(request)
    if who is None:
        return _forbidden()
    return profile_view(request, who)


def _forbidden() -> Response:
    return HTMLResponse(
        '<!doctype html><meta charset="utf-8"><title>Forbidden</title>'
        '<body style="font:15px system-ui;background:#0b0d12;color:#e8ecf3;'
        'display:grid;place-items:center;height:100vh;margin:0">'
        '<div style="text-align:center"><h1 style="font-weight:650">403</h1>'
        '<p style="color:#98a2b3">This page is for administrators. '
        'Sign in through the portal.</p>'
        '<p><a style="color:#5b8cff" href="/">Your account</a></p></div>',
        status_code=403)


app = Starlette(routes=[
    Route("/healthz", healthz, methods=["GET"]),
    Route("/", index, methods=["GET"]),
    Route("/profile", profile_page, methods=["GET"]),
    Route("/people", people_page, methods=["GET"]),
    Route("/people/{username}", person_page, methods=["GET"]),
    Route("/model", model_page, methods=["GET"]),
    Route("/indexing", indexing_page, methods=["GET"]),
    Route("/monitoring", monitoring_page, methods=["GET"]),
    Route("/settings", settings_page, methods=["GET"]),
    Route("/export/people.csv", export_people, methods=["GET"]),
    Route("/theme", theme, methods=["POST"]),
    Route("/password", change_password, methods=["POST"]),
    Route("/admin/create", admin_create, methods=["POST"]),
    Route("/admin/rotate", admin_rotate, methods=["POST"]),
    Route("/admin/reset", admin_reset, methods=["POST"]),
    Route("/admin/budget", admin_budget, methods=["POST"]),
    Route("/admin/delete", admin_delete, methods=["POST"]),
    Route("/admin/index", admin_index, methods=["POST"]),
], on_startup=[_start_directory_sync])
