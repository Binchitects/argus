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
            from ones written by scripts/gen-auth.sh.

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
import time
import shutil
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


def save_users(users: dict) -> None:
    """Rewrite users.yml, keeping a backup.

    Written to a temp file and moved into place so Authelia never observes a
    half-written database -- it watches this file and reloads on change.
    """
    with open(USERS_FILE, encoding="utf-8") as fh:
        doc = yaml.safe_load(fh) or {}
    doc["users"] = users
    shutil.copy2(USERS_FILE, USERS_FILE + ".bak")
    tmp = USERS_FILE + ".tmp"
    with open(tmp, "w", encoding="utf-8") as fh:
        yaml.safe_dump(doc, fh, default_flow_style=False, sort_keys=False,
                       allow_unicode=True)
    os.replace(tmp, USERS_FILE)


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


PAGE = """<!doctype html><html><head><meta charset="utf-8">
<meta name="viewport" content="width=device-width,initial-scale=1">
<title>{title}</title><style>
:root{{--bg:#0f1115;--card:#171a21;--line:#262b36;--fg:#e6e9ef;--dim:#9aa4b2;
--acc:#4f8cff;--warn:#ffb020;--ok:#35c26b;--bad:#ff5c5c}}
*{{box-sizing:border-box}}
body{{margin:0;background:var(--bg);color:var(--fg);
font:14px/1.5 ui-sans-serif,system-ui,-apple-system,Segoe UI,Roboto,sans-serif}}
header{{display:flex;align-items:center;gap:16px;padding:14px 22px;
background:var(--card);border-bottom:1px solid var(--line)}}
header h1{{font-size:15px;margin:0;font-weight:600}}
header .who{{margin-left:auto;color:var(--dim);font-size:13px}}
header a.out{{margin-left:14px;text-decoration:none;color:var(--dim);
border:1px solid var(--line);border-radius:7px;padding:5px 11px;font-size:12px;
font-weight:600}}
header a.out:hover{{border-color:var(--bad);color:var(--bad)}}
.badge{{background:var(--acc);color:#fff;border-radius:999px;padding:1px 9px;
font-size:11px;font-weight:600;margin-left:8px}}
main{{max-width:1080px;margin:0 auto;padding:22px}}
.card{{background:var(--card);border:1px solid var(--line);border-radius:10px;
padding:18px;margin-bottom:18px}}
.card h2{{font-size:13px;margin:0 0 14px;color:var(--dim);font-weight:600;
text-transform:uppercase;letter-spacing:.06em}}
table{{width:100%;border-collapse:collapse}}
th,td{{text-align:left;padding:9px 10px;border-bottom:1px solid var(--line);
font-size:13px;vertical-align:middle}}
th{{color:var(--dim);font-weight:600;font-size:11px;text-transform:uppercase;
letter-spacing:.05em}}
tr:last-child td{{border-bottom:0}}
input,select{{background:#0e1116;border:1px solid var(--line);color:var(--fg);
border-radius:7px;padding:8px 10px;font:inherit;min-width:0}}
button{{background:var(--acc);color:#fff;border:0;border-radius:7px;
padding:8px 13px;font:inherit;font-weight:600;cursor:pointer}}
button.ghost{{background:transparent;border:1px solid var(--line);color:var(--fg)}}
button.danger{{background:var(--bad)}}
a.btn{{display:inline-block;text-decoration:none;background:transparent;
border:1px solid var(--line);color:var(--fg);border-radius:7px;padding:8px 13px;
font-weight:600}}
a.btn:hover{{border-color:var(--acc);color:var(--acc)}}
form.row{{display:flex;gap:9px;flex-wrap:wrap;align-items:center}}
.msg{{padding:11px 14px;border-radius:8px;margin-bottom:16px;font-size:13px}}
.msg.ok{{background:rgba(53,194,107,.12);border:1px solid var(--ok);color:var(--ok)}}
.msg.bad{{background:rgba(255,92,92,.12);border:1px solid var(--bad);color:var(--bad)}}
code.key{{font-family:ui-monospace,SFMono-Regular,Menlo,monospace;font-size:12px;
background:#0e1116;border:1px solid var(--line);border-radius:6px;padding:3px 7px;
display:inline-block;word-break:break-all}}
.bar{{height:5px;background:#0e1116;border-radius:3px;overflow:hidden;margin-top:5px}}
.bar i{{display:block;height:100%;background:var(--ok)}}
.bar i.warn{{background:var(--warn)}} .bar i.bad{{background:var(--bad)}}
.dim{{color:var(--dim);font-size:12px}}
</style></head><body>
<header><h1>LLM Service</h1>{badge}
<div class="who">{who}</div>{logout}</header>
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

    if running:
        started = _rel_time(job.get("started"))
        tail = "\n".join(job.get("tail") or [])[-4000:]
        status = (f'<div class="msg">Indexing <b>{_h(", ".join(job.get("branches") or []) or "default branches")}</b>'
                  f' — started {_h(started)}. This page refreshes every 5s.</div>'
                  f'<pre style="max-height:220px;overflow:auto;background:#111;color:#ddd;'
                  f'padding:10px;border-radius:6px;font-size:12px">{_h(tail) or "starting…"}</pre>')
    elif job.get("finished"):
        rc = job.get("returncode")
        cls = "msg" if rc == 0 else "msg bad"
        status = (f'<div class="{cls}">Last run finished {_h(_rel_time(job.get("finished")))} '
                  f'— exit {_h(str(rc))}</div>')
    else:
        status = '<p class="dim" style="margin:0 0 12px">No run has been started from here yet.</p>'

    # Per-repo freshness. Argus records one row PER REF, so a project indexed at
    # two branches legitimately appears twice; the branch column is what tells
    # them apart.
    body = ""
    if repos:
        shown = repos[:40]
        trs = "".join(
            '<tr><td>{}</td><td>{}</td><td>{}</td><td>{}</td></tr>'.format(
                _h(r.get("repo") or "?"),
                _h(r.get("branch") or "?") + (" <span class=dim>(default)</span>"
                                              if r.get("branch") == r.get("default_branch") else ""),
                _h(_rel_time(r.get("last_run_at"))),
                ('<span class="bad">timed out</span>' if r.get("timed_out")
                 else (f'{_h(str(r.get("symbols_failed")))} failed'
                       if r.get("symbols_failed") else "ok")))
            for r in shown)
        more = (f'<p class="dim">showing {len(shown)} of {len(repos)} refs</p>'
                if len(repos) > len(shown) else "")
        body = (f'<table><thead><tr><th>Repo</th><th>Branch</th><th>Last indexed</th>'
                f'<th>Result</th></tr></thead><tbody>{trs}</tbody></table>{more}')

    disabled = " disabled" if running else ""
    refresh = ('<meta http-equiv="refresh" content="5">' if running else "")
    return (f'{refresh}<div class="card"><h2>Indexing</h2>'
            f'{status}'
            f'<form method="post" action="/admin/index" style="margin:12px 0">'
            f'<input name="branches" placeholder="branch or glob, e.g. develop or release/*" '
            f'style="min-width:280px"{disabled}> '
            f'<button class="btn" type="submit"{disabled}>Index all repos</button>'
            f'<p class="dim" style="margin:6px 0 0">Space-separated for several. Each '
            f'project&#39;s default branch is always included; repos without the named '
            f'branch are indexed at their default rather than failing.</p></form>'
            f'{body}</div>')


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

    body = (_degraded(note)
            + f'<div class="card"><h2>People ({len(rows)})</h2>'
            f'<table><tr><th>User</th><th>Usage / credit</th><th>API key</th>'
            f'<th>Credit</th><th>Actions</th></tr>{"".join(rows)}</table></div>'
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
    try:
        _argus("/admin/index", {"branches": branches})
    except urllib.error.HTTPError as exc:
        if exc.code == 409:
            return _back(err="An index run is already in progress.")
        return _back(err=f"Argus refused the request: HTTP {exc.code}")
    except Exception as exc:                                   # noqa: BLE001
        return _back(err=f"Could not reach Argus: {repr(exc)[:120]}")
    label = ", ".join(branches) if branches else "default branches"
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
])
