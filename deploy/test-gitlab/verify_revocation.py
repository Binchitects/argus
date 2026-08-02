import asyncio, json, pathlib, subprocess, sys, time, os, httpx
ROOT = pathlib.Path("E:/Projects/CodeAssistant"); sys.path.insert(0, str(ROOT))
HERE = ROOT/"deploy"/"test-gitlab"
s = json.loads((HERE/"seeded.json").read_text())
GL, admin = s["gitlab_url"], s["admin_token"]
alpha = s["users"]["dev_alpha"]; pid = s["projects"]["eal-core"]
HOST, PORT = "127.0.0.1", 7763
c = httpx.Client(base_url=f"{GL}/api/v4", headers={"PRIVATE-TOKEN": admin}, timeout=30)

p = subprocess.Popen([sys.executable,"-m","argus.cli","serve","--config",str(HERE/"work"/"config.yaml"),
  "--host",HOST,"--port",str(PORT),"--allowed-host",f"{HOST}:{PORT}","--allowed-host",HOST],
  cwd=ROOT, stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)
for _ in range(60):
    time.sleep(1)
    try:
        if httpx.get(f"http://{HOST}:{PORT}/healthz",timeout=2).status_code==200: break
    except Exception: pass

async def n_refs():
    from mcp import ClientSession
    from mcp.client.streamable_http import streamablehttp_client
    async with streamablehttp_client(f"http://{HOST}:{PORT}/mcp",
            headers={"Authorization": f"Bearer {alpha['token']}"}) as (r,w,_):
        async with ClientSession(r,w) as sess:
            await sess.initialize()
            res = await sess.call_tool("find_references", {"name":"DecodeFrame"})
            sc = getattr(res,"structuredContent",None) or {}
            return len(sc.get("result") or [])

try:
    before = asyncio.run(n_refs())
    print(f"1. baseline: dev_alpha sees {before} references")

    c.delete(f"/projects/{pid}/members/{alpha['id']}").raise_for_status()
    print("2. removed dev_alpha's membership in GitLab")

    cached = asyncio.run(n_refs())
    print(f"3. immediately after revoke (cache still warm): {cached} references"
          f"  <- expected NON-zero; ACL is cached for TTL=600s by design")

    r = subprocess.run([sys.executable,"-m","argus.cli","flush-acl","--config",
        str(HERE/"work"/"config.yaml"),"--user","dev_alpha"],
        cwd=ROOT, capture_output=True, text=True)
    print(f"4. flush-acl: {r.stdout.strip() or r.stderr.strip()}")

    after = asyncio.run(n_refs())
    print(f"5. after flush-acl: {after} references  <- expected 0")

    ok = before > 0 and cached > 0 and after == 0
    print(f"\n{'PASS' if ok else 'FAIL'}: revocation takes effect, and flush-acl is a working escape hatch")
finally:
    c.post(f"/projects/{pid}/members", json={"user_id": alpha["id"], "access_level": 20})
    print("   (membership restored for re-runs)")
    p.terminate()
