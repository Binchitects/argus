#!/usr/bin/env python3
"""
Give every person in team.yml a LiteLLM identity, an API key, and a quota.

`sync-users.py` reconciles Authelia (who may sign in). This reconciles the
gateway (what they may spend). Both read the SAME team.yml, and both key off
the email address, which is the only identifier Authelia, Open WebUI and
LiteLLM all agree on:

    Authelia   issues email over OIDC
    Open WebUI forwards it as X-OpenWebUI-User-Email
    LiteLLM    maps that header to internal_user  (config/litellm/config.yaml)
    this file  mints each API key with user_id = the same email

That last line is what makes one person's chat usage and API usage add up to
one number. Mint a key by hand without a user_id and its spend lands nowhere
near the person's chat spend, and the totals quietly stop meaning anything.

    python3 sync-llm-users.py <team.yml>            # plan only
    python3 sync-llm-users.py <team.yml> --apply    # create/update
    python3 sync-llm-users.py <team.yml> --rotate alice@example.com --apply

Needs LITELLM_MASTER_KEY and LITELLM_URL (default http://localhost:4000).

Keys are shown ONCE, at creation. LiteLLM stores them hashed and this script
never writes them to disk -- capture them when they are printed, or rotate.
"""
from __future__ import annotations

import argparse
import os
import sys
import urllib.error
import urllib.request

try:
    import json

    import yaml
except ImportError:  # pragma: no cover - dependency guidance, not logic
    sys.exit("needs PyYAML: pip install pyyaml")

GREEN, YELLOW, RED, DIM, OFF = (
    "\033[32m", "\033[33m", "\033[31m", "\033[90m", "\033[0m")

#: Quota keys a person may carry in team.yml, and the LiteLLM field each maps
#: to. Absent means "leave at the config-wide default" rather than "unlimited".
QUOTAS = {
    "max_budget": "max_budget",
    "budget_duration": "budget_duration",
    "tpm_limit": "tpm_limit",
    "rpm_limit": "rpm_limit",
    "max_parallel_requests": "max_parallel_requests",
}


def api(path: str, payload: dict | None, master: str, base: str,
        method: str = "POST") -> dict:
    """Call the gateway. Raises with the server's own message on failure."""
    url = f"{base.rstrip('/')}{path}"
    data = json.dumps(payload).encode() if payload is not None else None
    req = urllib.request.Request(url, data=data, method=method, headers={
        "Authorization": f"Bearer {master}",
        "Content-Type": "application/json",
    })
    try:
        with urllib.request.urlopen(req, timeout=30) as resp:
            return json.loads(resp.read() or b"{}")
    except urllib.error.HTTPError as exc:
        body = exc.read().decode("utf-8", "replace")[:400]
        raise SystemExit(
            f"{RED}{method} {path} failed: HTTP {exc.code}{OFF}\n  {body}\n"
            f"  {DIM}(is LITELLM_MASTER_KEY right, and is the gateway up?){OFF}"
        ) from None
    except urllib.error.URLError as exc:
        raise SystemExit(
            f"{RED}cannot reach the gateway at {base}{OFF}\n"
            f"  {exc.reason}\n"
            f"  {DIM}start it with ./scripts/up.sh, or set LITELLM_URL{OFF}"
        ) from None


#: LiteLLM rejects anything above 100 with a 422, so this is a hard ceiling
#: rather than a tuning choice -- a single larger request is not an option.
PAGE_SIZE = 100


def existing_users(master: str, base: str) -> dict[str, dict]:
    """Everyone the gateway already knows, by user_id.

    Paginated because the roster can exceed one page, and a truncated list
    reads as "these people do not exist yet" -- which would create duplicate
    users and hand out second keys to people who already had one.
    """
    found: dict[str, dict] = {}
    for page in range(1, 51):
        got = api(f"/user/list?page={page}&page_size={PAGE_SIZE}", None,
                  master, base, method="GET")
        rows = got.get("users", got if isinstance(got, list) else [])
        if not rows:
            break
        for user in rows:
            if user.get("user_id"):
                found[str(user["user_id"])] = user
        if len(rows) < PAGE_SIZE:
            break
    return found


def roster(path: str) -> list[dict]:
    with open(path, encoding="utf-8") as fh:
        doc = yaml.safe_load(fh) or {}
    people = []
    for entry in doc.get("users") or []:
        email = (entry.get("email") or "").strip().lower()
        if not email:
            print(f"{YELLOW}  skipping {entry.get('username')!r}: no email, "
                  f"and email is the identity this stack keys on{OFF}")
            continue
        people.append({
            "email": email,
            "username": entry.get("username") or email,
            "quotas": {v: entry[k] for k, v in QUOTAS.items() if k in entry},
        })
    return people


def main() -> int:
    ap = argparse.ArgumentParser()
    ap.add_argument("team", help="path to config/authelia/team.yml")
    ap.add_argument("--apply", action="store_true",
                    help="make the changes (default: print the plan)")
    ap.add_argument("--rotate", metavar="EMAIL",
                    help="issue a fresh key for one person and revoke the old")
    ap.add_argument("--url", default=os.environ.get("LITELLM_URL",
                                                    "http://localhost:4000"))
    args = ap.parse_args()

    master = os.environ.get("LITELLM_MASTER_KEY", "")
    if not master:
        return int(bool(sys.stderr.write(
            f"{RED}LITELLM_MASTER_KEY is not set{OFF}\n"
            f"  It is in llm-stack/.env; export it, or run this through\n"
            f"  ./scripts/llm-users.sh which loads that file for you.\n")))

    people = roster(args.team)
    if not people:
        print("no users with an email in the roster")
        return 0

    known = existing_users(master, args.url)
    created = updated = 0

    for person in people:
        email, quotas = person["email"], person["quotas"]
        shown = f"{email} {DIM}({person['username']}){OFF}"

        if email not in known:
            if not args.apply:
                print(f"{GREEN}  CREATE{OFF} {shown} {DIM}{quotas or 'defaults'}{OFF}")
                created += 1
                continue
            api("/user/new", {"user_id": email, "user_email": email,
                              "user_role": "internal_user", **quotas},
                master, args.url)
            key = api("/key/generate",
                      {"user_id": email, "key_alias": f"{person['username']}-api",
                       **quotas}, master, args.url)
            print(f"{GREEN}  CREATED{OFF} {shown}")
            # Printed once and never stored: LiteLLM keeps only a hash, and
            # writing it to a file here would put every person's key on disk
            # in one place.
            print(f"    key: {key.get('key')}  {DIM}(shown once){OFF}")
            created += 1
            continue

        if quotas:
            if not args.apply:
                print(f"{YELLOW}  UPDATE{OFF} {shown} {DIM}{quotas}{OFF}")
            else:
                api("/user/update", {"user_id": email, **quotas},
                    master, args.url)
                print(f"{YELLOW}  UPDATED{OFF} {shown} {DIM}{quotas}{OFF}")
            updated += 1
        else:
            print(f"{DIM}  ok     {email}{OFF}")

    if args.rotate:
        email = args.rotate.strip().lower()
        if not args.apply:
            print(f"{YELLOW}  would rotate the key for {email}{OFF}")
        else:
            key = api("/key/generate",
                      {"user_id": email, "key_alias": f"{email}-api"},
                      master, args.url)
            print(f"{GREEN}  ROTATED{OFF} {email}")
            print(f"    key: {key.get('key')}  {DIM}(shown once){OFF}")
            print(f"    {DIM}revoke the previous key in the LiteLLM UI once "
                  f"the holder has switched over{OFF}")

    verb = "applied" if args.apply else "planned"
    print(f"\n{created} created, {updated} updated ({verb}).")
    if not args.apply:
        print(f"{DIM}re-run with --apply to make these changes{OFF}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
