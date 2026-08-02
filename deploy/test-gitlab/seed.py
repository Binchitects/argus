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
    """GitLab takes minutes to become usable; poll until the API answers.

    Do NOT probe `/-/readiness` from the host. GitLab restricts its monitoring
    endpoints to an IP allowlist that is localhost-only by default, so through
    Docker's NAT the source is the bridge gateway and the endpoint returns
    **404** -- indistinguishable from "not up yet" if you are only checking for
    200. In-container it returns 200, which makes the container healthcheck go
    green while a host-side probe appears to hang forever.

    `/api/v4/version` is the right signal: it needs no allowlist and answers 401
    (not a connection error) as soon as Rails is serving.
    """
    deadline = time.time() + timeout_s
    while time.time() < deadline:
        try:
            r = httpx.get(f"{GITLAB}/api/v4/version", timeout=10)
            if r.status_code in (200, 401):
                print("gitlab: API is serving")
                return
        except Exception:
            pass
        print("gitlab: waiting...", flush=True)
        time.sleep(15)
    raise TimeoutError("GitLab did not become ready in time")


def admin_token() -> str:
    """Mint an admin PAT directly, avoiding the web login flow.

    Idempotent by destroying any prior 'argus-seed' token first. GitLab stores
    only the token *digest*, so a fixed token value cannot simply be re-read on
    a re-run -- a second create collides with
    `index_personal_access_tokens_on_token_digest` and aborts the whole seed.
    """
    out = _rails(
        "u = User.find_by_username('root');"
        "raise 'no root user -- run `gitlab-rake db:seed_fu` first' if u.nil?;"
        "PersonalAccessToken.where(name: 'argus-seed').destroy_all;"
        "t = u.personal_access_tokens.create!("
        "  name: 'argus-seed', scopes: ['api','read_api','read_repository'],"
        "  expires_at: 365.days.from_now);"
        "t.set_token('argus-admin-token-0001');"
        "t.save!;"
        "puts t.token"
    )
    return out.splitlines()[-1].strip()


def _ok(r: httpx.Response, what: str) -> httpx.Response:
    """raise_for_status, but show GitLab's actual complaint.

    A bare 400 from /api/v4/users is unactionable; the body says exactly which
    validation failed. It is usually the password-complexity rule -- the same
    one that silently defeats the root-admin seed in 003_admin.rb.
    """
    if r.is_error:
        raise RuntimeError(f"{what} failed: {r.status_code} {r.text[:500]}")
    return r


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
        if r.status_code == 400 and "already been taken" in r.text:
            # Re-run against an instance already seeded: reuse it rather than
            # forcing a full teardown just to retry a later step.
            existing = _ok(c.get("/projects", params={"search": name, "simple": True}),
                           "project lookup").json()
            pid = next(p["id"] for p in existing if p["path"] == name)
            project_ids[name] = pid
            print(f"reusing existing project {name} (id={pid})")
            continue
        _ok(r, f"create project {name}")
        pid = r.json()["id"]
        project_ids[name] = pid
        print(f"created private project {name} (id={pid})")

        for path, content in files.items():
            _ok(c.post(f"/projects/{pid}/repository/files/{path.replace('/', '%2F')}",
                       json={"branch": "main", "content": content,
                             "commit_message": f"add {path}"}),
                f"add {path}")
        print(f"  seeded {len(files)} files")

    # --- developers, each scoped to exactly one project -------------------
    users: dict[str, dict] = {}
    for username, project in MEMBERSHIPS.items():
        r = c.post("/users", json={
            "email": f"{username}@argus.test", "username": username,
            "name": username,
            # Must clear GitLab's complexity check -- a readable passphrase is
            # rejected with "Password must not contain commonly used
            # combinations of words and letters".
            "password": "Kt5wQ9rBz3Xm7Yv2Np8D",
            "skip_confirmation": True,
        })
        if r.status_code == 409 or (r.status_code == 400 and "has already been taken" in r.text):
            found = _ok(c.get("/users", params={"username": username}), "user lookup").json()
            uid = found[0]["id"]
            print(f"reusing existing user {username} (id={uid})")
        else:
            _ok(r, f"create user {username}")
            uid = r.json()["id"]

        m = c.post(f"/projects/{project_ids[project]}/members",
                   json={"user_id": uid, "access_level": REPORTER})
        if not (m.status_code == 409 or (m.status_code == 400 and "already exists" in m.text)):
            _ok(m, f"add {username} to {project}")

        # Impersonation tokens are the supported way to mint a token *as*
        # another user without knowing their password.
        r = _ok(c.post(f"/users/{uid}/impersonation_tokens", json={
            "name": f"{username}-argus", "scopes": ["api", "read_api", "read_repository"],
            "expires_at": "2027-01-01",
        }), f"mint token for {username}")
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
