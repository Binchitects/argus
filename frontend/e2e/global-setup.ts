import { execFile, execFileSync, spawn, type ChildProcess } from "node:child_process";
import { promisify } from "node:util";
import { createHash } from "node:crypto";
import { existsSync, mkdirSync, readFileSync, rmSync, writeFileSync } from "node:fs";
import { dirname, join, resolve } from "node:path";
import { fileURLToPath } from "node:url";
import { fakeEmbeddings, fakeGateway, fakeGitLab } from "./fakes";

const here = dirname(fileURLToPath(import.meta.url));
const repo = resolve(here, "../..");
const work = join(here, ".work");

export const MASTER = "sk-master-e2e";
export const MODEL = "qwen3.8-flash-next";
export const ADMIN = { username: "admin", email: "admin@example.test", password: "admin-password-e2e" };
export const WEBHOOK = "hook-secret-e2e";

function git(cwd: string, ...args: string[]) {
  execFileSync("git", args, {
    cwd,
    stdio: "pipe",
    env: { ...process.env, GIT_AUTHOR_NAME: "e2e", GIT_AUTHOR_EMAIL: "e2e@example.test", GIT_COMMITTER_NAME: "e2e", GIT_COMMITTER_EMAIL: "e2e@example.test" },
  });
}

function write(path: string, text: string) {
  mkdirSync(dirname(path), { recursive: true });
  writeFileSync(path, text);
}

/** A working tree committed and published as a bare repository GitLab's fake serves over dumb HTTP. */
function publish(name: string, files: Record<string, string>) {
  const tree = join(work, "trees", name);
  for (const [path, text] of Object.entries(files)) write(join(tree, path), text);
  git(tree, "init", "-q", "-b", "main");
  git(tree, "add", "-A");
  git(tree, "commit", "-q", "-m", "initial");
  const bare = join(work, "repos", `${name}.git`);
  mkdirSync(dirname(bare), { recursive: true });
  git(work, "clone", "-q", "--bare", tree, bare);
  git(bare, "update-server-info");
  return tree;
}

async function waitFor(url: string, proc: ChildProcess, log: string, seconds = 120) {
  const deadline = Date.now() + seconds * 1000;
  while (Date.now() < deadline) {
    if (proc.exitCode !== null) throw new Error(`backend exited ${proc.exitCode}:\n${readFileSync(log, "utf8")}`);
    try {
      if ((await fetch(url)).ok) return;
    } catch {
      /* not yet */
    }
    await new Promise((r) => setTimeout(r, 250));
  }
  throw new Error(`${url} did not come up:\n${readFileSync(log, "utf8")}`);
}

export default async function globalSetup() {
  rmSync(work, { recursive: true, force: true });
  mkdirSync(work, { recursive: true });

  publish("acme/decoder", {
    "include/decoder.h": "#pragma once\n/** Decode one frame of the stream into the caller's buffer. */\nint DecodeFrame(const unsigned char *in, int len, unsigned char *out);\n",
    "src/decode.c": '#include "decoder.h"\n\n/** Decode one frame of the stream into the caller\'s buffer. */\nint DecodeFrame(const unsigned char *in, int len, unsigned char *out) {\n  for (int i = 0; i < len; i++) out[i] = in[i];\n  return len;\n}\n\nstatic int checksum(const unsigned char *p, int n) { int s = 0; while (n--) s += *p++; return s; }\n',
    "tools/cache.py": 'def expire_keys(store, now):\n    """Remove every key whose time to live has elapsed."""\n    for key in list(store):\n        if store[key] < now:\n            del store[key]\n',
    "README.md": "# decoder\n\nA tiny frame decoder.\n",
  });
  publish("acme/secret", {
    "vault.c": "/** Unlock the vault with a PIN. */\nint vault_unlock(int pin) { return pin == 1234; }\n",
  });

  const gitlab = await fakeGitLab(join(work, "repos"), [
    { id: 1, username: "svc", token: "svc-token", email: "svc@example.test", isAdmin: true },
    { id: 2, username: "alice", token: "alice-gitlab-token", email: "alice@example.test", publicEmail: "alice@example.test", name: "Alice" },
  ], [
    { id: 100, path: "acme/decoder", defaultBranch: "main", members: { 1: 50, 2: 30 } },
    { id: 101, path: "acme/secret", defaultBranch: "main", members: { 1: 50 } },
  ]);
  const embeddings = await fakeEmbeddings();
  const gateway = await fakeGateway(MASTER, MODEL);

  const bin = process.env.ARGUS_BIN ?? join(repo, "dotnet/src/Argus/bin/Release/net10.0/argus");
  if (!existsSync(bin)) throw new Error(`no backend at ${bin}: run 'dotnet build dotnet/Argus.sln -c Release' first`);
  const webRoot = process.env.ARGUS_WEB_ROOT ?? join(here, "../dist");
  if (!existsSync(join(webRoot, "index.html"))) throw new Error(`no frontend build at ${webRoot}: run 'npm run build' first`);

  const env: NodeJS.ProcessEnv = {
    ...process.env,
    ARGUS_GATEWAY_URL: gateway.url,
    LITELLM_MASTER_KEY: MASTER,
    ARGUS_PUBLIC_GATEWAY_URL: "https://gateway.example.test/v1",
    ARGUS_EMBED_URL: embeddings.url,
    ARGUS_LLAMACPP_HEALTH_URL: `${embeddings.url}/health`,
    ARGUS_EMBED_PER_PASS: "100000",
    ARGUS_INDEX_INTERVAL: "0",
    ARGUS_WEBHOOK_TOKEN: WEBHOOK,
    ARGUS_ADMIN_USERNAME: ADMIN.username,
    ARGUS_ADMIN_EMAIL: ADMIN.email,
    ARGUS_ADMIN_PASSWORD: ADMIN.password,
    ARGUS_WEB_ROOT: webRoot,
    ARGUS_AUDIT_LOG: "0",
    GIT_TERMINAL_PROMPT: "0",
  };

  // A knowledge pack to install through the UI, built by the real builder.
  // Asynchronously: the fake embeddings server lives in this process's event loop.
  const pack = join(work, "python.arguspack");
  await promisify(execFile)(bin, ["pack", "build", "--source", "python", "--work-dir", join(repo, "dotnet/tests/fixtures/packs/python"),
    "--out", pack, "--version", "1.0", "--commit", "e2e"], { env });

  const config = join(work, "config.yaml");
  writeFileSync(config, `gitlab:\n  url: ${gitlab.url}\n  token: svc-token\nindex:\n  data_dir: ${join(work, "data")}\n  db_path: ${join(work, "data", "index.db")}\npacks:\n  dir: ${join(work, "packs")}\n`);
  const port = Number(process.env.E2E_PORT);
  const log = join(work, "backend.log");
  writeFileSync(log, "");
  const backend = spawn(bin, ["serve", "--config", config, "--port", String(port)], { env, stdio: ["ignore", "pipe", "pipe"] });
  backend.stdout!.on("data", (d) => writeFileSync(log, d, { flag: "a" }));
  backend.stderr!.on("data", (d) => writeFileSync(log, d, { flag: "a" }));
  const base = `http://127.0.0.1:${port}`;
  await waitFor(`${base}/healthz`, backend, log);

  process.env.E2E_BASE_URL = base;
  process.env.E2E_PACK = pack;
  process.env.E2E_PACK_SHA256 = createHash("sha256").update(readFileSync(pack)).digest("hex");
  process.env.E2E_GATEWAY_URL = gateway.url;
  process.env.E2E_WORK = work;

  return async () => {
    backend.kill("SIGTERM");
    gitlab.close();
    embeddings.close();
    gateway.close();
  };
}
