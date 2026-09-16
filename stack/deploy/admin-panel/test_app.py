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


def check(condition: bool, message: str) -> None:
    if not condition:
        FAILURES.append(message)


def status(*, state="idle", tail=None, repos=None, returncode=0,
           finished=1_700_000_000.0, allow_partial=False):
    return {
        "job": {"state": state, "branches": [], "started": 1_699_999_000.0,
                "finished": finished, "returncode": returncode,
                "tail": tail or [], "allow_partial": allow_partial},
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

if FAILURES:
    for line in FAILURES:
        print(f"FAIL: {line}")
    raise SystemExit(1)
print(f"admin panel: {len(FAILURES)} failures, all rendering checks passed")


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
