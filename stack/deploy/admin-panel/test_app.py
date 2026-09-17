"""Rendering checks for the admin panel.

Plain asserts rather than pytest. This image holds the LiteLLM master key and
writes Authelia's user database; pulling a test framework into it to check a
handful of strings is not a trade worth making, and a script that exits
non-zero gates the build just as well.

What these exist for, concretely: the Indexing card's log block rendered the
LIST OF REPOSITORIES instead of the run log, because the closure that built it
referenced a name that a later line rebound to the repos-table slice. Python
resolves that name when the closure runs, so the card looked plausible and was
wrong. Nothing else in the deployment could have caught it.
"""
from __future__ import annotations

import html as _html

import app as panel

FAILURES: list[str] = []
CHECKS: list[str] = []


def check(condition: bool, message: str) -> None:
    CHECKS.append(message)
    if not condition:
        FAILURES.append(message)


def status(*, state="idle", tail=None, repos=None, returncode=0,
           finished=1_700_000_000.0, allow_partial=False, trigger=None):
    return {
        "job": {"state": state, "branches": [], "started": 1_699_999_000.0,
                "finished": finished, "returncode": returncode,
                "tail": tail or [], "allow_partial": allow_partial,
                "trigger": trigger},
        "repos": repos or [],
    }


REPOS = [
    {"repo": "group/alpha", "branch": "main", "default_branch": "main",
     "last_run_at": 1_700_000_000, "timed_out": False, "symbols_failed": 0},
    {"repo": "group/beta", "branch": "main", "default_branch": "main",
     "last_run_at": 1_700_000_000, "timed_out": True, "symbols_failed": 3},
]
TAIL = ['{"ts": "2026-01-01T00:00:00Z", "event": "index_start", "repos": 2}',
        "group/alpha: up to date",
        '{"ts": "2026-01-01T00:00:01Z", "event": "index_end", "returncode": 1}']

panel.ARGUS_ADMIN_TOKEN = "test-token"


def card(payload, *, is_admin=True):
    panel._argus = lambda path, body=None: payload
    return panel.indexing_card(is_admin=is_admin)


# --- the bug this file exists for ------------------------------------------

html = card(status(tail=TAIL, repos=REPOS, returncode=1))
check("group/alpha: up to date" in html, "the run log is not in the card")
check("index_end" in html, "the structured lines are not in the log block")
# Unescape first. The first version of this check compared against raw quotes
# and passed against the bug: the card HTML-escapes what it renders, so the
# giveaway was present as `{&#x27;repo&#x27;` and the assertion looked for
# `{'repo'`. `default_branch` is the clean discriminator -- it is a key of the
# repository rows and appears in no log line, escaped or not.
flat = _html.unescape(html)
check("{'repo': 'group/alpha'" not in flat,
      "the log block rendered the repository LIST instead of the log")
check("default_branch" not in flat,
      "the log block rendered the repository LIST instead of the log")
check("group/alpha" in html, "the per-repo table lost its rows")

# --- the log survives the run ----------------------------------------------

check("Log from the last run" in html,
      "the log is dropped once the run finishes, which is when it is needed")
check("index_end" in html, "the log is empty after the run")

running = card(status(state="running", tail=TAIL, repos=REPOS))
check("Run log" in running, "a running pass should label the log as live")
check("refresh" in running, "a running pass should keep refreshing the page")
check("disabled" in running, "the button must be disabled while a pass runs")

# --- exit codes are explained, not just printed ----------------------------

check("exit 1" in html and "unhealthy" in html,
      "exit 1 is shown as a bare number instead of naming what it means")
check("completed" in card(status(returncode=0)),
      "exit 0 should read as completed")
check("ctags" in card(status(returncode=4)),
      "exit 4 should name the preflight cause")

# --- the partial-enumeration opt-in ----------------------------------------

refused = card(status(returncode=3))
check("Index what the token can see" in refused,
      "there is no way to opt into a partial index from the panel")
check("admin rights" in refused,
      "an exit-3 refusal should say what to do about it")
check("checked" in card(status(allow_partial=True)),
      "the checkbox does not reflect the mode the last run used")
check("checked" not in card(status(allow_partial=False)),
      "partial enumeration must not look enabled by default")

# --- errors and gating ------------------------------------------------------

check("Could not read per-repo" not in card(status()),
      "the card warns about a per-repo read failure that did not happen")
with_error = {"job": {"state": "idle", "repos_error": "NameError: nope"},
              "repos": []}
check("NameError" in card(with_error),
      "a swallowed status-route error is hidden from the operator")
check(card(status(), is_admin=False) == "",
      "the indexing card must not render for a non-admin")

panel.ARGUS_ADMIN_TOKEN = ""
check(card(status()) == "",
      "the card must not render when Argus's admin surface is unconfigured")

panel.ARGUS_ADMIN_TOKEN = "test-token"


# --- the console shell ------------------------------------------------------
#
# The panel was one long page. These pin the parts that make it a console: a
# sidebar that says where you are, a nav that does not offer a person pages they
# cannot open, and redirects that return you to the page you acted on.

from starlette.requests import Request  # noqa: E402


def _request(path="/", cookies=None):
    headers = []
    if cookies:
        headers.append((b"cookie", "; ".join(f"{k}={v}" for k, v in cookies.items()).encode()))
    return Request({"type": "http", "method": "GET", "path": path, "headers": headers,
                    "query_string": b"", "scheme": "https", "server": ("x", 443)})


def test_the_shell_renders_a_sidebar_with_the_active_section():
    html = panel.page(_request(), "People", "<p>body</p>", "admin", True,
                      active="people").body.decode()
    check('class="shell"' in html, "no shell wrapper")
    check('class="item on" href="/people"' in html, "the active section is not marked")
    check(html.count('class="item') >= 6, "the sidebar is missing sections")
    check("<p>body</p>" in html, "the page body was dropped")


def test_an_ordinary_person_is_not_offered_admin_pages():
    """Listing sections someone cannot open reads as a permissions problem
    rather than a design decision, and the 403 is the first thing they see."""
    html = panel.page(_request(), "Your account", "", "dev", False,
                      active="profile").body.decode()
    check('href="/people"' not in html, "a non-admin was offered the people list")
    check('href="/settings"' not in html, "a non-admin was offered settings")
    check('href="/profile"' in html, "a non-admin lost their own page")
    check("admin" not in html.split("<main>")[0].replace("Administration", ""),
          "a non-admin page claims to be an admin")


def test_the_theme_comes_from_a_cookie_and_rejects_junk():
    check(panel._theme(_request()) == "system", "the default is not the OS preference")
    check(panel._theme(_request(cookies={"theme": "light"})) == "light", "cookie ignored")
    check(panel._theme(_request(cookies={"theme": "<script>"})) == "system",
          "an unknown theme was accepted")


def test_the_theme_button_offers_the_next_theme():
    html = panel.page(_request(cookies={"theme": "dark"}), "x", "", "a", True).body.decode()
    check('value="light"' in html, "the cycle from dark should offer light")
    html = panel.page(_request(cookies={"theme": "light"}), "x", "", "a", True).body.decode()
    check('value="system"' in html, "the cycle from light should offer system")


def test_actions_return_to_the_page_they_were_taken_on():
    check(panel._where("people") == "/people", "people does not map to its page")
    check(panel._where("nonsense") == "/", "an unknown destination should fall back")
    check(panel._dest({"to": "person", "username": "dev a"}) == "/people/dev%20a",
          "a per-person action should return to that person, escaped")
    check(panel._dest({"to": "person"}) == "/people",
          "a per-person action with no name should fall back to the list")


def test_the_csv_export_cannot_be_used_as_a_formula():
    """A username is attacker-controlled and a spreadsheet executes a leading
    = or + -- the classic CSV injection."""
    check(panel._csv_cell("=cmd|'/c calc'!A1").startswith('"\'='),
          "a formula was exported unescaped")
    check(panel._csv_cell('he said "hi"') == '"he said ""hi"""', "quotes not doubled")
    check(panel._csv_cell(None) == '""', "None should be an empty field")
    check(panel._csv_cell(12.5) == '"12.5"', "numbers should be quoted too")


# --- the delete guard -------------------------------------------------------
#
# This one shipped broken and deleted the administrator account off a live
# stack, so it is pinned from both sides: it must refuse, and it must not
# refuse everything.


class _Who:
    def __init__(self, user, email):
        self.user, self.email, self.groups = user, email, ["admins"]
        self.is_admin = True

    @property
    def label(self):
        return self.email or self.user


ADMIN = _Who("admin", "admin@llm.localhost")
USERS = {
    "admin": {"groups": ["admins"], "email": "admin@llm.localhost"},
    "ali": {"groups": ["users"], "email": "ali@llm.local"},
}


def test_deleting_yourself_is_refused_by_every_identity():
    """`who.label` is the EMAIL, not the username. The first version compared
    the username against it, so the guard never fired and the admin account was
    deleted on a live stack."""
    check(panel._delete_refusal(ADMIN, "admin", "admin@llm.localhost", USERS) is not None,
          "deleting yourself by username was allowed")
    check(panel._delete_refusal(ADMIN, "someone-else", "admin@llm.localhost", USERS)
          is not None, "deleting yourself by email was allowed")
    check(panel._delete_refusal(ADMIN, "admin", "different@x.test", USERS) is not None,
          "deleting yourself with a mismatched email was allowed")


def test_deleting_the_last_administrator_is_refused():
    only = {"admin": {"groups": ["admins"], "email": "a@b.c"}}
    check(panel._delete_refusal(_Who("other", "o@x.test"), "admin", "a@b.c", only)
          is not None, "the only administrator could be deleted")


def test_an_ordinary_person_can_still_be_deleted():
    """A guard that refuses everything is not a guard, it is an outage."""
    check(panel._delete_refusal(ADMIN, "ali", "ali@llm.local", USERS) is None,
          "an ordinary account could not be deleted")
    two = {"admin": {"groups": ["admins"]}, "second": {"groups": ["admins"]}}
    check(panel._delete_refusal(ADMIN, "second", "s@x.test", two) is None,
          "an administrator could not be deleted while another remains")


# --- the Overview's index numbers -------------------------------------------
#
# The index is the one component whose failure is invisible from every other
# page: the machine is healthy, the engine answers, and the answers are just
# out of date. So the Overview has to say so, and it has to say which
# repository -- "3 stale" is a number nobody can act on.

def idx(**kw):
    base = {"configured": True, "ok": True, "repos": 4, "stale": 0,
            "errored": 0, "never_run": 0, "stale_names": []}
    base.update(kw)
    return base


check(panel.index_tile({"configured": False}) == "",
      "a deployment with no index should not get an index tile")
check("unreachable" in panel.index_tile({"configured": True, "ok": False,
                                         "error": "URLError: refused"}),
      "an unreachable Argus is not reported on the Overview")
check("empty" in panel.index_tile(idx(repos=0)),
      "an index with no repositories is not reported as empty")

current = panel.index_tile(idx(repos=4, stale=0))
check("4/4" in current and "repositories current" in current,
      "a fully current index should read 4/4")
behind = panel.index_tile(idx(repos=4, stale=1))
check("3/4" in behind and "1 out of date" in behind,
      "a stale repository is not counted against the index total")
# Errored is its own state: the pass RUNS on schedule, so a freshness-only
# reading is perfect while the answers come from a failed pass.
check("3/4" in panel.index_tile(idx(repos=4, stale=0, errored=1))
      and "failing to index" in panel.index_tile(idx(repos=4, stale=0, errored=1)),
      "repositories failing to index are indistinguishable from healthy ones")

check(panel.index_alert(idx()) == "",
      "a healthy index should not raise an alert banner")
check(panel.index_alert({"configured": False}) == "",
      "an unconfigured index should not raise an alert banner")
check("unreachable" in panel.index_alert({"configured": True, "ok": False,
                                          "error": "URLError: refused"}),
      "an unreachable index raises no banner")
check("No repository is indexed" in panel.index_alert(idx(repos=0, stale=0)),
      "an empty index raises no banner")

banner = panel.index_alert(idx(stale=2, never_run=1,
                               stale_names=["group/alpha@main", "group/beta@main"]))
check("2 repository(ies) have a stale" in banner, "the stale count is missing")
check("group/alpha@main" in banner and "group/beta@main" in banner,
      "the banner does not name the repositories, so nobody can act on it")
check("1 of them have never been indexed" in banner,
      "a repository that was never indexed at all reads the same as a late one")
check("/indexing" in banner, "the banner offers no way to fix it")

# A stale index has several causes with different fixes -- no schedule, GitLab
# unreachable, a token that can no longer enumerate, a pass that keeps timing
# out. The exit code is what tells them apart, and it is already on the Indexing
# page; repeating it here is the difference between an alarm the reader can act
# on and one they have to go investigating.
unreachable = panel.index_alert(idx(stale=3, returncode=3,
                                    stale_names=["g/a@main"]))
check("could not reach GitLab" in unreachable,
      "a stale index caused by an unreachable GitLab does not say so")
check("advises on nothing" not in unreachable, "sanity")

timed_out = panel.index_alert(idx(stale=3, returncode=1,
                                  stale_names=["g/a@main"]))
check("could not reach GitLab" not in timed_out,
      "a failing repository is reported as an unreachable GitLab")
check("unhealthy" in timed_out, "a failing repository is not described")

# A clean last run means the cause is elsewhere -- most often that nothing is
# scheduled -- so the banner must not invent one.
check("The last pass ended" not in panel.index_alert(
        idx(stale=3, returncode=0, stale_names=["g/a@main"])),
      "a successful last pass is reported as the cause of staleness")
check("The last pass ended" not in panel.index_alert(
        idx(stale=3, stale_names=["g/a@main"])),
      "a never-run index is reported with an exit-code cause it does not have")

# --- the indexing cadence ---------------------------------------------------
#
# The stack shipped for months with nothing ever running `argus index` on a
# timer: the index advanced only when an operator pressed the button, and the
# console never said so. These pin the line that says it, in both states.

check(panel._duration(900) == "15 minutes", f"900s read as {panel._duration(900)!r}")
check(panel._duration(3600) == "1 hour", f"3600s read as {panel._duration(3600)!r}")
check(panel._duration(60) == "1 minute", f"60s read as {panel._duration(60)!r}")
check(panel._duration(45) == "45 seconds", f"45s read as {panel._duration(45)!r}")
check(panel._duration(7200) == "2 hours", f"7200s read as {panel._duration(7200)!r}")


def with_interval(seconds, **kw):
    payload = status(**kw)
    payload["interval"] = seconds
    return card(payload)


auto = with_interval(900)
check("Reindexes itself every" in auto and "15 minutes" in auto,
      "an enabled schedule is not stated on the Indexing page")
check("not required" in auto,
      "the page does not say the button is optional once a schedule exists")

manual = with_interval(0)
check("Automatic reindexing is" in manual and "off" in manual,
      "a stack that never reindexes does not say so -- this is the bug")
check("ARGUS_INDEX_INTERVAL" in manual,
      "the page names the problem but not the setting that fixes it")

# A run the schedule started must not read as somebody else having pressed the
# button: "why is this running?" is the first question an operator asks.
check("the schedule" in with_interval(900, state="running", trigger="schedule"),
      "a scheduled run is indistinguishable from a manual one")
check("this console" in with_interval(900, state="running", trigger="manual"),
      "a manual run is indistinguishable from a scheduled one")

# --- the push webhook is visible where the cadence is ------------------------
#
# "Reindexes every 15 minutes" reads very differently depending on whether a
# push also arrives immediately, and the console cannot see whether Argus has a
# webhook secret -- it is Argus's setting, not the panel's. So the server tells
# it, rather than the panel guessing from its own environment.


def with_hook(*, webhook, interval=900, pending=None, **kw):
    payload = status(**kw)
    payload["interval"] = interval
    payload["webhook"] = webhook
    payload["pending"] = pending or []
    return card(payload)


check("reports a push" in with_hook(webhook=True),
      "the page does not say pushes are indexed immediately")
check("ARGUS_WEBHOOK_TOKEN" in with_hook(webhook=False),
      "the page does not name the setting that enables push indexing")

# A queued run must be visible, or an operator watching one pass finish and
# another start has no way to know why.
queued = with_hook(webhook=True, pending=["g/a", "g/b"])
check("g/a" in queued and "g/b" in queued, "queued repositories are not shown")
check("Queued from GitLab pushes" in queued, "the queue is not explained")

# A webhook-started run says so: "something changed in GitLab" and "somebody
# pressed the button" are different answers to "why is this running?".
webhook_run = with_hook(webhook=True, state="running", trigger="webhook")
check("a GitLab push" in webhook_run, "a webhook-started run is not identified")

# --- the index explorer -------------------------------------------------------
#
# The page that answers "why did the agent not find it?" -- a question with four
# possible answers (not in the code, named differently, private, never indexed)
# that all look identical from the chat window. Every one of them needs the page
# to say which one it is.


def explore(payload, query=""):
    panel._argus = lambda path, body=None: payload
    from starlette.requests import Request as _R
    scope = {"type": "http", "method": "GET", "path": "/explore",
             "query_string": query.encode(), "headers": []}
    return panel.explore_view(_R(scope), _Who("admin", "a@b.c")).body.decode()


EMPTY_EXPLORE = {"repos": [], "symbols": {"rows": [], "capped": False},
                 "files": {"rows": [], "capped": False}}

blank = explore(EMPTY_EXPLORE)
check("What this is for" in blank,
      "the empty page does not explain what it is for")
check("Nothing is indexed yet" in blank,
      "an index with no repositories does not say so")

with_repo = explore({
    "repos": [{"path_with_namespace": "g/alpha", "branch": "main",
               "default_branch": "main", "files": 8, "symbols": 70,
               "public_symbols": 68, "last_run_at": 1_700_000_000}],
    "symbols": {"rows": [], "capped": False}, "files": {"rows": [], "capped": False}})
check("g/alpha" in with_repo and "70" in with_repo,
      "the repository list does not show what the index holds")

# A file indexed with ZERO symbols is the case worth surfacing: it is the
# difference between "the agent cannot find it" and "it is not in the index".
zero = explore({
    "repos": [],
    "symbols": {"rows": [], "capped": False},
    "files": {"rows": [{"path": "notes.unknown", "lang": "", "symbols": 0,
                        "path_with_namespace": "g/alpha"}], "capped": False}},
    query="q=notes")
check("notes.unknown" in zero, "a matching file is not listed")
check("the extractor did not recognise" in zero,
      "a file with no symbols is not explained")

# Truncation must be stated. A list that silently stops at the limit reads as
# "that is all there is", which is how an operator concludes a symbol is absent.
capped = explore({
    "repos": [],
    "symbols": {"rows": [{"name": "DecodeFrame", "kind": "function",
                          "is_public": 1, "path": "src/decoder.c", "line": 3,
                          "path_with_namespace": "g/alpha"}], "capped": True},
    "files": {"rows": [], "capped": True}}, query="q=Decode")
check("DecodeFrame" in capped, "a symbol result is not shown")
check("More symbols match than are shown" in capped, "symbol truncation is silent")
check("More files match than are shown" in capped, "file truncation is silent")

# An error in the payload must be RENDERED, not rendered as an empty page. This
# is not hypothetical: the route answered 200 with "cannot convert dictionary
# update sequence" next to empty lists, and the console drew "Nothing is indexed
# yet" while the index held seventy symbols.
broken = explore({"error": "TypeError: cannot convert dictionary update sequence",
                  "repos": [], "symbols": {"rows": [], "capped": False},
                  "files": {"rows": [], "capped": False}})
check("could not read the" in broken and "TypeError" in broken,
      "an unreadable index is drawn as an empty index")
check("Nothing is indexed yet" not in broken,
      "the empty-index message is shown for a read failure")

# --- the summary has to be the LAST thing in this file -----------------------
#
# It used to sit two thirds of the way down, just after the Indexing-card
# checks. Everything below it -- the console shell, the theme, the redirects,
# the CSV escaping and both delete guards -- appended to FAILURES that nobody
# ever read, so the build passed no matter what those checks said. A guard that
# cannot fail is worse than no guard, because it is believed.

# --- the Packs card ---------------------------------------------------------


def packs(payload, *, is_admin=True):
    panel._argus = lambda path, body=None: payload
    return panel.packs_card(is_admin=is_admin)


PACKS = [
    {"name": "win32", "version": "1.1", "model": "nomic-embed-text", "dim": "768",
     "size_bytes": 726 * 1024 * 1024, "license": "CC-BY-4.0", "commit": "abc",
     "compatible": True, "incompatible_reason": ""},
    {"name": "sqlite", "version": "1.0", "model": "nomic-embed-text", "dim": "768",
     "size_bytes": 18 * 1024 * 1024, "license": "public-domain", "commit": "def",
     "compatible": True, "incompatible_reason": ""},
]
PACK_JOB = {"state": "idle", "action": None, "target": None, "started": None,
            "finished": 1_700_000_000.0, "returncode": 0, "tail": ["installed win32 1.1"]}

html = packs({"packs": PACKS, "job": PACK_JOB, "index_url": "", "packs_dir": "/p"})
check("win32" in html and "1.1" in html, "the installed pack and its version are not listed")
check("sqlite" in html and "public-domain" in html, "a pack row lost its licence")
check("726.0 MB" in html, "the pack size is not shown")
check("2" in html, "the count of installed packs is missing")

# The log survives the job, for the same reason the indexing log does: "exit 1"
# with the reason thrown away is the state that gets reported as broken.
check("installed win32 1.1" in html, "the pack job log is not shown")

# An incompatible pack still serves lookup and lexical search, so hiding it
# would remove a working tool and say nothing about why.
bad = packs({"packs": [dict(PACKS[0], compatible=False,
                            incompatible_reason="built with mxbai-embed-large")],
             "job": PACK_JOB, "index_url": "", "packs_dir": "/p"})
check("mxbai-embed-large" in bad, "an incompatible pack is listed without saying why")
check("lookup and text search still work" in bad,
      "an incompatible pack does not say what still works")

# Update must be offered only when there is an index to update FROM, and must
# name the variable to set when there is not.
no_index = packs({"packs": PACKS, "job": PACK_JOB, "index_url": "", "packs_dir": "/p"})
check("ARGUS_PACK_INDEX_URL" in no_index,
      "no index configured, and the card does not name the variable that fixes it")
with_index = packs({"packs": PACKS, "job": PACK_JOB,
                    "index_url": "https://example.invalid/index.json",
                    "packs_dir": "/p"})
check("Update all packs" in with_index, "the update button is missing when an index exists")
check("example.invalid" in with_index, "the update form does not say which index it uses")

# A running job disables the buttons: two installs into one directory race on
# the final rename, and Argus refuses the second anyway.
running = packs({"packs": PACKS, "index_url": "",
                 "job": dict(PACK_JOB, state="running", action="install",
                             target="https://x/win32.arguspack", finished=None),
                 "packs_dir": "/p"})
check("disabled" in running, "the forms stay enabled while a pack job is running")

# A failed job has to say so, with its return code.
failed = packs({"packs": [], "index_url": "",
                "job": dict(PACK_JOB, returncode=1, tail=["failed: checksum mismatch"]),
                "packs_dir": "/p"})
check("checksum mismatch" in failed, "a failed pack job hides its reason")

# Argus unreachable is a state of its own, not an empty pack list.
panel._argus = lambda path, body=None: (_ for _ in ()).throw(RuntimeError("boom"))
unreachable = panel.packs_card(is_admin=True)
check("did not answer" in unreachable,
      "an unreachable Argus renders as an empty registry rather than an error")

# Non-admins get nothing at all -- the card names every pack in the estate.
check(packs({"packs": PACKS, "job": PACK_JOB, "index_url": "", "packs_dir": "/p"},
            is_admin=False) == "", "a non-admin is shown the packs card")

if FAILURES:
    for line in FAILURES:
        print(f"FAIL: {line}")
    raise SystemExit(1)
print(f"admin panel: {len(CHECKS)} rendering checks passed")
