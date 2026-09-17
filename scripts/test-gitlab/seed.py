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

    python scripts/test-gitlab/seed.py

Writes the resulting tokens to scripts/test-gitlab/seeded.json (gitignored).
"""
from __future__ import annotations

import json
import pathlib
import subprocess
import sys
import time

import httpx

GITLAB = "http://localhost:8929"

#: A second ref for ONE project, so branch-agnostic behaviour is verifiable.
#:
#: Trunk and this branch share a symbol name with DIFFERENT documentation, and
#: this branch alone defines a symbol trunk has never heard of. That makes every
#: branch claim falsifiable: an unqualified question must answer from trunk and
#: must not find the branch-only symbol; naming the branch must find it; and an
#: unindexed branch must say so rather than returning an empty list that reads
#: as "no such symbol".
RELEASE_BRANCH = "v2"

#: The branch-only source. `DecodeFrameV2` exists nowhere else, and the doc on
#: the shared `DecodeFrame` says which branch it came from -- so a result can be
#: traced back to the ref that answered.
RELEASE_DECODER_C = (
    '#include "eal/decoder.h"\n'
    "\n"
    "static int HelperOnly(int x) { return x + 1; }\n"
    "\n"
    "/**\n"
    " * V2 ONLY: decode one frame using the hardware path.\n"
    " *\n"
    " * Added on the release branch and absent from trunk, which is what makes\n"
    " * a branch-agnostic query falsifiable.\n"
    " */\n"
    "int DecodeFrameV2(const char* buf, int len) { return HelperOnly(len); }\n"
    "\n"
    "/**\n"
    " * Decode one frame from a caller-owned buffer.\n"
    " *\n"
    " * ON THE V2 BRANCH this description says so, which is how a result can be\n"
    " * traced back to the branch that answered.\n"
    " */\n"
    "int DecodeFrame(const char* buf, int len) { return HelperOnly(len); }\n"
)
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
            "\n"
            "static int HelperOnly(int x) { return x + 1; }\n"
            "\n"
            "/**\n"
            " * Decode one frame from a caller-owned buffer.\n"
            " *\n"
            " * Reads exactly len bytes and never advances the caller's pointer, so\n"
            " * the same buffer can be handed to the next stage unchanged.\n"
            " */\n"
            "int DecodeFrame(const char* buf, int len) { return HelperOnly(len); }\n"
            "\n"
            "/*\n"
            " * The two functions below exist to be told apart by their DOCUMENTATION\n"
            " * and nothing else, and they are a transcription of a real failure.\n"
            " *\n"
            " * On a real corpus, asked \"what expires keys past their TTL\", semantic\n"
            " * search returned expire_slave_keys -- which contains the words \"expire\"\n"
            " * and \"keys\" and does something else entirely. The routine that actually\n"
            " * reclaims expired keys has neither word in its name. With only a name,\n"
            " * a signature and a path to embed, the index could not tell them apart,\n"
            " * because the one thing that does is the sentence above each one.\n"
            " *\n"
            " * Which is why they are here: if the doc comment ever stops being\n"
            " * extracted or stops being embedded, this fixture goes back to returning\n"
            " * the wrong function and the suite says so, instead of quietly reverting\n"
            " * to matching vocabulary.\n"
            " *\n"
            " * The second one's doc deliberately describes REPLICATION and never says\n"
            " * what it does not do. The first version read \"it is NOT the routine that\n"
            " * reclaims expired keys\", and that negation pulled it to within 0.0002\n"
            " * of the right answer -- an embedding of a sentence lands near the thing\n"
            " * the sentence is about, negation included. Worth knowing generally:\n"
            " * documenting what a function does not do is not neutral, it is\n"
            " * evidence for the opposite.\n"
            " */\n"
            "\n"
            "/**\n"
            " * Reclaim keys whose time to live has elapsed.\n"
            " *\n"
            " * Walks the expiration index in bounded steps so a large keyspace does\n"
            " * not stall the caller, and frees each key it finds. The name says\n"
            " * nothing about any of that, deliberately.\n"
            " */\n"
            "int active_expire_cycle(int budget) { return budget; }\n"
            "\n"
            "/**\n"
            " * Forward a decision to a follower.\n"
            " *\n"
            " * Serialises the command and appends it to the replication stream so\n"
            " * the replica applies it in order; the decision itself was made by\n"
            " * the caller, before this was called.\n"
            " */\n"
            "int expire_slave_keys(int budget) { return budget; }\n"
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

    # --- a second ref, on one project -------------------------------------
    #
    # Re-run safe: an existing branch is reused and its file rewritten, so this
    # works both on a fresh instance and on one that was seeded before this
    # existed. That matters because the project loop above deliberately SKIPS
    # file creation for a project it is reusing, which is exactly how a new
    # fixture file silently fails to appear on a re-seed.
    releasable = project_ids.get("eal-core")
    if releasable:
        r = c.post(f"/projects/{releasable}/repository/branches",
                   json={"branch": RELEASE_BRANCH, "ref": "main"})
        if r.status_code in (200, 201):
            print(f"created branch {RELEASE_BRANCH} on eal-core")
        elif r.status_code == 400 and "already exists" in r.text:
            print(f"reusing existing branch {RELEASE_BRANCH} on eal-core")
        else:
            _ok(r, f"create branch {RELEASE_BRANCH}")
        _ok(c.put(
            f"/projects/{releasable}/repository/files/src%2Fdecoder.c",
            json={"branch": RELEASE_BRANCH, "content": RELEASE_DECODER_C,
                  "commit_message": "release: hardware decode path"}),
            f"write the {RELEASE_BRANCH} decoder")

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
