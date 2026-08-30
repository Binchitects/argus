#!/usr/bin/env python3
"""
Reconcile Authelia's users.yml against the declarative list in team.yml.

Runs inside a python container (see gen-auth.sh --sync) so that PyYAML and
argon2-cffi are available without touching the host.

Rules:
    in team.yml only      -> create, with a random password printed once
    in both               -> keep the password hash, update groups/name/email
    in users.yml only     -> remove

Password hashing uses exactly the parameters in configuration.yml
(argon2id, t=3, m=65536, p=4, 32-byte hash, 16-byte salt) so Authelia accepts
the result.

    python3 sync-users.py <team.yml> <users.yml> [--apply]

Without --apply it prints the plan and changes nothing.
"""
import secrets
import string
import sys

import yaml
from argon2 import PasswordHasher
from argon2.low_level import Type

# Must match authentication_backend.file.password in configuration.yml.
HASHER = PasswordHasher(
    time_cost=3, memory_cost=65536, parallelism=4,
    hash_len=32, salt_len=16, type=Type.ID,
)

# Ambiguous characters removed: these get read aloud and typed by hand.
ALPHABET = "".join(c for c in (string.ascii_letters + string.digits) if c not in "O0Il1")

GREEN, YELLOW, RED, DIM, OFF = "\033[32m", "\033[33m", "\033[31m", "\033[90m", "\033[0m"


def new_password(n: int = 20) -> str:
    return "".join(secrets.choice(ALPHABET) for _ in range(n))


def load(path: str) -> dict:
    with open(path, encoding="utf-8") as fh:
        return yaml.safe_load(fh) or {}


def main() -> int:
    team_path, users_path = sys.argv[1], sys.argv[2]
    apply = "--apply" in sys.argv[3:]

    team = load(team_path).get("users") or []
    if not isinstance(team, list):
        print(f"{RED}team.yml: 'users' must be a list{OFF}")
        return 1

    desired = {}
    for entry in team:
        if not isinstance(entry, dict) or not entry.get("username"):
            print(f"{RED}team.yml: every entry needs a 'username'{OFF}")
            return 1
        name = str(entry["username"])
        desired[name] = {
            "displayname": entry.get("displayname", name),
            "email": entry.get("email", f"{name}@llm.localhost"),
            "groups": list(entry.get("groups") or ["users"]),
        }

    existing = (load(users_path).get("users") or {}) if users_path else {}

    # Refuse to leave nobody able to reach the admin-only endpoints.
    if not any("admins" in v["groups"] for v in desired.values()):
        print(f"{RED}refusing to sync: no user in team.yml is in the 'admins' group{OFF}")
        print(f"{RED}that would lock you out of Prometheus, Alertmanager and Traefik{OFF}")
        return 1

    to_create = [u for u in desired if u not in existing]
    to_remove = [u for u in existing if u not in desired]
    to_update, unchanged = [], []

    for name in desired:
        if name in to_create:
            continue
        cur = existing[name] or {}
        want = desired[name]
        diffs = []
        if sorted(cur.get("groups") or []) != sorted(want["groups"]):
            diffs.append(("groups", cur.get("groups"), want["groups"]))
        if (cur.get("displayname") or "") != want["displayname"]:
            diffs.append(("displayname", cur.get("displayname"), want["displayname"]))
        if (cur.get("email") or "") != want["email"]:
            diffs.append(("email", cur.get("email"), want["email"]))
        (to_update if diffs else unchanged).append((name, diffs))

    # ---- plan --------------------------------------------------------------
    print()
    if to_create:
        for u in to_create:
            print(f"  {GREEN}CREATE {OFF} {u:<14} groups={','.join(desired[u]['groups'])}")
    for u, diffs in to_update:
        for field, old, new in diffs:
            print(f"  {YELLOW}UPDATE {OFF} {u:<14} {field}: {old} -> {new}")
    for u in to_remove:
        print(f"  {RED}REMOVE {OFF} {u:<14} {DIM}(access revoked){OFF}")
    for u, _ in unchanged:
        print(f"  {DIM}ok      {u}{OFF}")
    if not (to_create or to_update or to_remove):
        print(f"  {DIM}nothing to do — users.yml already matches team.yml{OFF}")

    if not apply:
        print()
        print(f"  {DIM}dry run — re-run with --apply to write users.yml{OFF}")
        return 0

    # ---- apply -------------------------------------------------------------
    result, created_passwords = {}, {}
    for name, want in desired.items():
        if name in existing:
            # Preserve the password hash; only membership/metadata change.
            block = dict(existing[name] or {})
            block["displayname"] = want["displayname"]
            block["email"] = want["email"]
            block["groups"] = want["groups"]
            block.setdefault("disabled", False)
            result[name] = block
        else:
            pw = new_password()
            created_passwords[name] = pw
            result[name] = {
                "disabled": False,
                "displayname": want["displayname"],
                "password": HASHER.hash(pw),
                "email": want["email"],
                "groups": want["groups"],
            }

    header = (
        "# Authelia user database - GENERATED by scripts/gen-auth.sh --sync\n"
        "# Edit config/authelia/team.yml and re-run --sync; manual edits here are\n"
        "# overwritten. Passwords are argon2id hashes, never plaintext.\n"
    )
    with open(users_path, "w", encoding="utf-8", newline="\n") as fh:
        fh.write(header)
        yaml.safe_dump({"users": result}, fh, sort_keys=True, default_flow_style=False)

    print()
    print(f"  {GREEN}users.yml written{OFF} — {len(result)} user(s)")
    if created_passwords:
        print()
        print("  " + "=" * 58)
        print("  NEW PASSWORDS - shown once, not recoverable afterwards")
        print("  " + "=" * 58)
        for u, pw in created_passwords.items():
            print(f"    {u:<14} {pw}")
        print("  " + "=" * 58)
        print(f"  {DIM}Authelia reloads users.yml within a minute; no restart needed.{OFF}")
    return 0


if __name__ == "__main__":
    sys.exit(main())
