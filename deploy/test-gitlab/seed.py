#!/usr/bin/env python3
"""Seed the throwaway GitLab with the fixtures Argus verification needs.

Creates:
  * three PRIVATE projects containing real C/C++ with cross-repo #includes
  * two developers, each a Reporter on exactly one project
  * a personal access token for each developer, plus an admin token

The point is to make the access-control claim falsifiable. Developer alpha is a
member of eal-core only; developer beta of etl-decoder only. Neither is a member
of driver-shim. If alpha can see beta's code through the MCP tools, the whole
design is wrong, and this is the fixture that proves it either way.

Usage (after `docker compose up -d` and GitLab is healthy):

    python deploy/test-gitlab/seed.py

Writes the resulting tokens to deploy/test-gitlab/seeded.json (gitignored).
"""
from __future__ import annotations

import json
import pathlib
import subprocess
import sys
import time

import httpx

GITLAB = "http://localhost:8929"
CONTAINER = "argus-test-gitlab"
OUT = pathlib.Path(__file__).parent / "seeded.json"

# Cross-repo includes are the whole point of the corpus: eal-core defines a
# symbol, the other two include its header and call it. That is what makes
# find_references and the include graph meaningful rather than trivial.
PROJECTS = {
    "eal-core": {
        "include/eal/decoder.h": (
            "#pragma once\n"
            "namespace eal {\n"
            "struct DecoderConfig { int max_frames; };\n"
            "int DecodeFrame(const char* buf, int len);\n"
            "namespace detail { int ScratchBuffer(int n); }\n"
            "}\n"
        ),
        "src/decoder.c": (
            '#include "eal/decoder.h"\n'
            "static int HelperOnly(int x) { return x + 1; }\n"
            "int DecodeFrame(const char* buf, int len) { return HelperOnly(len); }\n"
        ),
    },
    "etl-decoder": {
        "src/pipeline.c": (
            '#include "eal/decoder.h"\n'
            '#include <stdio.h>\n'
            "int RunPipeline(const char* b, int n) {\n"
            "    return DecodeFrame(b, n);\n"
            "}\n"
        ),
        "src/notes.md": "RunPipeline calls DecodeFrame from eal-core.\n",
    },
    "driver-shim": {
        "src/shim.c": (
            '#include "eal/decoder.h"\n'
            "int ShimEntry(const char* b, int n) { return DecodeFrame(b, n); }\n"
        ),
    },
}

# username -> project it may read
MEMBERSHIPS = {"dev_alpha": "eal-core", "dev_beta": "etl-decoder"}
REPORTER = 20


def _rails(ruby: str) -> str:
    """Run ruby inside the GitLab container via rails runner."""
    proc = subprocess.run(
        ["docker", "exec", CONTAINER, "gitlab-rails", "runner", ruby],
        capture_output=True, text=True, timeout=600,
    )
    if proc.returncode != 0:
        raise RuntimeError(f"rails runner failed:\n{proc.stderr[:2000]}")
    return proc.stdout.strip()


def wait_for_api(timeout_s: int = 1800) -> None:
    """GitLab takes minutes to become usable; poll until the API answers."""
    deadline = time.time() + timeout_s
    while time.time() < deadline:
        try:
            r = httpx.get(f"{GITLAB}/-/readiness", timeout=10)
            if r.status_code == 200:
                print("gitlab: ready")
                return
        except Exception:
            pass
        print("gitlab: waiting...", flush=True)
        time.sleep(15)
    raise TimeoutError("GitLab did not become ready in time")


def admin_token() -> str:
    """Mint an admin PAT directly, avoiding the web login flow."""
    out = _rails(
        "u = User.find_by_username('root');"
        "t = u.personal_access_tokens.create!("
        "  name: 'argus-seed', scopes: ['api','read_api','read_repository'],"
        "  expires_at: 365.days.from_now);"
        "t.set_token('argus-admin-token-0001');"
        "t.save!;"
        "puts t.token"
    )
    return out.splitlines()[-1].strip()


def main() -> int:
    wait_for_api()
    admin = admin_token()
    print(f"admin token: {admin[:12]}...")
    c = httpx.Client(base_url=f"{GITLAB}/api/v4",
                     headers={"PRIVATE-TOKEN": admin}, timeout=60)

    # --- projects, PRIVATE on purpose -------------------------------------
    project_ids: dict[str, int] = {}
    for name, files in PROJECTS.items():
        r = c.post("/projects", json={
            "name": name, "path": name,
            "visibility": "private",          # the whole point
            "initialize_with_readme": True,
        })
        r.raise_for_status()
        pid = r.json()["id"]
        project_ids[name] = pid
        print(f"created private project {name} (id={pid})")

        for path, content in files.items():
            c.post(f"/projects/{pid}/repository/files/{path.replace('/', '%2F')}",
                   json={"branch": "main", "content": content,
                         "commit_message": f"add {path}"}).raise_for_status()
        print(f"  seeded {len(files)} files")

    # --- developers, each scoped to exactly one project -------------------
    users: dict[str, dict] = {}
    for username, project in MEMBERSHIPS.items():
        r = c.post("/users", json={
            "email": f"{username}@argus.test", "username": username,
            "name": username, "password": "argus-test-pw-2026",
            "skip_confirmation": True,
        })
        r.raise_for_status()
        uid = r.json()["id"]

        c.post(f"/projects/{project_ids[project]}/members",
               json={"user_id": uid, "access_level": REPORTER}).raise_for_status()

        # Impersonation tokens are the supported way to mint a token *as*
        # another user without knowing their password.
        r = c.post(f"/users/{uid}/impersonation_tokens", json={
            "name": f"{username}-argus", "scopes": ["api", "read_api", "read_repository"],
            "expires_at": "2027-01-01",
        })
        r.raise_for_status()
        users[username] = {"id": uid, "token": r.json()["token"], "member_of": project}
        print(f"created {username} (id={uid}) -> Reporter on {project}")

    OUT.write_text(json.dumps({
        "gitlab_url": GITLAB,
        "admin_token": admin,
        "projects": project_ids,
        "users": users,
    }, indent=2), encoding="utf-8")
    print(f"\nwrote {OUT}")
    print("\nNOTE: no developer is a member of driver-shim. If either developer")
    print("can reach it through Argus, the access-control design has failed.")
    return 0


if __name__ == "__main__":
    sys.exit(main())
