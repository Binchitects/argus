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
</style></head><body>
<header>
 <span class="brand"><span class="mark"></span>LLM Service</span>{badge}
 <div class="who">{who}</div>{logout}
</header>
<main>{msg}{body}</main></body></html>"""


def page(title: str, body: str, who: str, admin: bool, msg: str = "") -> HTMLResponse:
    # Applied to every page, not just the ones showing a credential: a one-time
    # secret is rendered through this same function, and a header that is only
    # sometimes present is a header someone will eventually forget.
    #
    #   no-store      keeps a shown-once password out of the disk cache and out
    #                 of the back button.
    #   no-referrer   the page links out to Grafana; without this the URL of the
    #                 page that displayed the secret travels in the Referer.
    logout = (f'<a class="out" href="{_h(AUTHELIA_URL)}/logout'
              f'?rd={urllib.parse.quote(f"https://admin.{DOMAIN}/")}">Sign out</a>'
              if AUTHELIA_URL else "")
    return HTMLResponse(
        PAGE.format(title=_h(title), body=body, who=_h(who), msg=msg,
                    logout=logout,
                    badge='<span class="badge">admin</span>' if admin else ""),
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
            f'<li>Run <code class="key">docker compose up -d</code> in the llm-stack directory. '
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
async def index(request: Request) -> Response:
    who = caller(request)
    if who is None:
        return HTMLResponse("<h1>403</h1><p>Sign in through the portal.</p>",
                            status_code=403)
    return admin_view(request, who) if who.is_admin else self_view(request, who)


def _degraded(note: str | None) -> str:
    return f'<div class="msg bad">{_h(note)}</div>' if note else ""


def self_view(request: Request, who: Caller) -> Response:
    users, note = spend_by_user()
    me = users.get(who.label.lower()) or {"spend": 0.0, "budget": None,
                                          "email": who.label}
    body = (_degraded(note)
            + f'<div class="card"><h2>Your usage</h2>{_usage_row(me)}</div>'
            + monitoring_card()
            + '<div class="card"><h2>Change password</h2>'
              '<form class="row" method="post" action="/password">'
              '<input type="password" name="current" placeholder="Current password" required>'
              '<input type="password" name="new" placeholder="New password (min 12)" '
              'minlength="12" required>'
              '<button type="submit">Update password</button></form>'
              '<p class="dim" style="margin:10px 0 0">Minimum 12 characters.</p></div>')
    return page("Your usage", body, who.label, False, _flash(request))


def admin_view(request: Request, who: Caller) -> Response:
    litellm_users, note = spend_by_user()
    authelia_users = load_users()

    rows = []
    for username, entry in sorted(authelia_users.items()):
        email = (entry.get("email") or username).lower()
        u = litellm_users.get(email, {"spend": 0.0, "budget": None})
        groups = entry.get("groups") or []
        is_admin = ADMIN_GROUP in groups
        keys = keys_for(u.get("user_id") or email)
        keycell = (f'<code class="key">{_h(keys[0]["token"][:14])}…</code>'
                   if keys else '<span class="dim">no key</span>')
        rows.append(
            f'<tr><td><strong>{_h(username)}</strong>'
            f'{" <span class=badge>admin</span>" if is_admin else ""}'
            f'<div class="dim">{_h(email)}</div></td>'
            f'<td>{_usage_row(u)}</td>'
            f'<td>{keycell}</td>'
            f'<td><form class="row" method="post" action="/admin/budget">'
            f'<input type="hidden" name="email" value="{_h(email)}">'
            f'<input type="number" name="budget" step="1" min="0" style="width:90px" '
            f'value="{_h(u.get("budget") or "")}" placeholder="none">'
            f'<button class="ghost" type="submit">Set</button></form></td>'
            f'<td><form method="post" action="/admin/rotate" style="display:inline">'
            f'<input type="hidden" name="email" value="{_h(email)}">'
            f'<button class="ghost" type="submit">New key</button></form> '
            f'<form method="post" action="/admin/reset" style="display:inline">'
            f'<input type="hidden" name="username" value="{_h(username)}">'
            f'<button class="ghost" type="submit">Reset password</button></form></td></tr>')

    spend_total = sum(float((u or {}).get("spend") or 0)
                      for u in litellm_users.values())
    tiles = ('<div class="tiles">'
             + _tile("People", str(len(rows)), "accounts in Authelia")
             + _tile("Spend", _money(spend_total), "across every key and chat")
             + _tile("Model", _h(os.environ.get("MODEL_NAME", "")),
                     _h(os.environ.get("MODEL_CONTEXT", "") + " token window"))
             + "</div>")

    body = ('<h1 class="page">Administration</h1>'
            '<p class="lede">Accounts, credit and API keys for this deployment.</p>'
            + tiles
            + _degraded(note)
            + f'<div class="card"><h2>People ({len(rows)})</h2>'
            f'<table><tr><th>User</th><th>Usage / credit</th><th>API key</th>'
            f'<th>Credit</th><th>Actions</th></tr>{"".join(rows)}</table></div>'
            + model_card()
            + indexing_card(is_admin=True)
            + monitoring_card()
            + '<div class="card"><h2>Add a person</h2>'
              '<form class="row" method="post" action="/admin/create">'
              '<input name="username" placeholder="username" required>'
              '<input name="email" type="email" placeholder="email" required>'
              '<input name="displayname" placeholder="Display name">'
              f'<input name="budget" type="number" step="1" min="0" style="width:110px" '
              f'value="{int(DEFAULT_BUDGET)}" placeholder="credit">'
              '<label class="dim"><input type="checkbox" name="admin" value="1"> admin</label>'
              '<button type="submit">Create</button></form>'
              '<p class="dim" style="margin:10px 0 0">A password and an API key are '
              'generated and shown once.</p></div>'
              '<div class="card"><h2>Your password</h2>'
              '<form class="row" method="post" action="/password">'
              '<input type="password" name="current" placeholder="Current password" required>'
              '<input type="password" name="new" placeholder="New password (min 12)" '
              'minlength="12" required>'
              '<button type="submit">Update password</button></form></div>')
    return page("Admin", body, who.label, True, _flash(request))


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


def _back(msg: str = "", err: str = "") -> RedirectResponse:
    q = urllib.parse.urlencode({"ok": msg} if msg else {"err": err})
    return RedirectResponse(f"/?{q}", status_code=303)


def _back_secret(text: str) -> RedirectResponse:
    """Redirect carrying a TOKEN, never the secret itself."""
    return RedirectResponse(f"/?shown={_stash(text)}", status_code=303)


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
        return _back(err="Username must be 2-64 chars of a-z 0-9 . _ -")
    if not _EMAIL.match(email):
        return _back(err="That does not look like an email address.")
    users = load_users()
    if username in users:
        return _back(err=f"{username} already exists.")

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
                            f"Password: {password}")
    return _back_secret(f"{username} -- password: {password} -- API key: {key}"
                        + (RELOAD_NOTE if WARN_RELOAD else ""))


async def admin_rotate(request: Request) -> Response:
    who = _require_admin(request)
    if who is None:
        return JSONResponse({"error": "forbidden"}, status_code=403)
    form = await request.form()
    email = str(form.get("email") or "").strip().lower()
    if not email:
        return _back(err="No user given.")
    # Delete first, then mint: the reverse order leaves a window in which the
    # old key still works alongside the new one, which is the opposite of what
    # "revoke" is asked for here.
    existing = [k["token"] for k in keys_for(email) if k["token"]]
    if existing:
        try:
            api("/key/delete", {"keys": existing})
        except urllib.error.HTTPError as exc:
            return _back(err=f"Could not revoke the old key: HTTP {exc.code}")
    try:
        key = api("/key/generate",
                  {"user_id": email, "key_alias": f"panel-{email}"}).get("key", "")
    except urllib.error.HTTPError as exc:
        return _back(err=f"Revoked {len(existing)} key(s) but minting failed: "
                         f"HTTP {exc.code}")
    return _back_secret(f"Revoked {len(existing)} key(s) for {email}. "
                        f"New API key: {key}")


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
        return _back(err=f"No Authelia account named {username}.")
    return _back_secret(f"New password for {username}: {password}"
                        + (RELOAD_NOTE if WARN_RELOAD else ""))


async def admin_index(request: Request) -> Response:
    """Start an index pass across every repo, optionally at extra branches."""
    who = _require_admin(request)
    if who is None:
        return JSONResponse({"error": "forbidden"}, status_code=403)
    if not (ARGUS_URL and ARGUS_ADMIN_TOKEN):
        return _back(err="Indexing is not configured: ARGUS_ADMIN_TOKEN is unset.")
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
            return _back(err="An index run is already in progress.")
        return _back(err=f"Argus refused the request: HTTP {exc.code}")
    except Exception as exc:                                   # noqa: BLE001
        return _back(err=f"Could not reach Argus: {repr(exc)[:120]}")
    label = ", ".join(branches) if branches else "default branches"
    if allow_partial:
        label += "; partial enumeration allowed"
    return _back(msg=f"Indexing started across all repos ({label}).")


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
        return _back(err="Credit must be a number.")
    try:
        api("/user/update", {"user_id": email, **quota})
        api("/end_user/update", {"user_id": email, **quota})
    except urllib.error.HTTPError as exc:
        return _back(err=f"LiteLLM refused: HTTP {exc.code}")
    return _back(f"Credit for {email} set to {_money(quota['max_budget'])}.")


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


app = Starlette(routes=[
    Route("/healthz", healthz, methods=["GET"]),
    Route("/", index, methods=["GET"]),
    Route("/password", change_password, methods=["POST"]),
    Route("/admin/create", admin_create, methods=["POST"]),
    Route("/admin/rotate", admin_rotate, methods=["POST"]),
    Route("/admin/reset", admin_reset, methods=["POST"]),
    Route("/admin/budget", admin_budget, methods=["POST"]),
    Route("/admin/index", admin_index, methods=["POST"]),
], on_startup=[_start_directory_sync])
