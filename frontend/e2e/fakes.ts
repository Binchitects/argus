// Stand-ins for the services around the backend, on real sockets, so the
// browser test exercises the real HTTP paths: GitLab (REST and git's dumb
// HTTP protocol, so the indexer's clone path runs for real), the LiteLLM
// gateway (people, keys, budgets, a scripted streaming model with tool calls)
// and llama.cpp's embeddings endpoint.

import { createHash } from "node:crypto";
import { createServer, type IncomingMessage, type Server, type ServerResponse } from "node:http";
import { readFileSync, statSync } from "node:fs";
import { resolve, sep } from "node:path";
import type { AddressInfo } from "node:net";

function listen(server: Server): Promise<string> {
  return new Promise((ok) => server.listen(0, "127.0.0.1", () => ok(`http://127.0.0.1:${(server.address() as AddressInfo).port}`)));
}

async function body(req: IncomingMessage): Promise<any> {
  const chunks: Buffer[] = [];
  for await (const c of req) chunks.push(c as Buffer);
  const text = Buffer.concat(chunks).toString("utf8");
  return text ? JSON.parse(text) : {};
}

function json(res: ServerResponse, status: number, value: unknown) {
  const data = Buffer.from(JSON.stringify(value));
  res.writeHead(status, { "Content-Type": "application/json", "Content-Length": data.length });
  res.end(data);
}

// --- GitLab ---------------------------------------------------------------------------

export interface GitLabUser { id: number; username: string; token: string; email: string; publicEmail?: string; isAdmin?: boolean; name?: string }
export interface GitLabProject { id: number; path: string; defaultBranch: string; members: Record<number, number> }

export async function fakeGitLab(reposDir: string, users: GitLabUser[], projects: GitLabProject[]) {
  const byToken = new Map(users.map((u) => [u.token, u]));
  const byId = new Map(users.map((u) => [u.id, u]));
  const root = resolve(reposDir);
  const server = createServer(async (req, res) => {
    const url = new URL(req.url ?? "/", "http://x");
    if (!url.pathname.startsWith("/api/v4/")) {
      const target = resolve(root, "." + decodeURIComponent(url.pathname));
      try {
        if (!target.startsWith(root + sep) || !statSync(target).isFile()) throw new Error();
        const data = readFileSync(target);
        res.writeHead(200, { "Content-Type": "application/octet-stream", "Content-Length": data.length });
        return res.end(data);
      } catch {
        res.writeHead(404);
        return res.end("not found");
      }
    }
    const token = (req.headers["private-token"] as string) || (req.headers.authorization ?? "").replace(/^Bearer /i, "");
    const me = byToken.get(token);
    if (!me) return json(res, 401, { message: "401 Unauthorized" });
    const path = url.pathname.slice("/api/v4".length);
    const page = Number(url.searchParams.get("page") ?? 1);
    const perPage = Number(url.searchParams.get("per_page") ?? 20);
    const paged = <T,>(items: T[]) => items.slice((page - 1) * perPage, page * perPage);
    const project = (p: GitLabProject) => ({
      id: p.id, path_with_namespace: p.path, default_branch: p.defaultBranch,
      http_url_to_repo: `https://gitlab.example.invalid/${p.path}.git`,
    });
    if (path === "/user") return json(res, 200, { id: me.id, username: me.username, is_admin: !!me.isAdmin, email: me.email, state: "active", name: me.name ?? me.username });
    if (path === "/projects") {
      let items: GitLabProject[] = [];
      if (url.searchParams.get("membership") === "true") {
        const level = Math.max(Number(url.searchParams.get("min_access_level") ?? 10), 10);
        items = projects.filter((p) => (p.members[me.id] ?? 0) >= level);
      } else if (me.isAdmin) items = projects;
      return json(res, 200, paged(items.map(project)));
    }
    const members = /^\/projects\/(\d+)\/members\/all$/.exec(path);
    if (members) {
      const p = projects.find((x) => x.id === Number(members[1]));
      if (!p) return json(res, 404, { message: "404 Project Not Found" });
      return json(res, 200, paged(Object.entries(p.members).map(([uid, level]) => {
        const u = byId.get(Number(uid))!;
        return { id: u.id, username: u.username, name: u.name ?? u.username, access_level: level, state: "active" };
      })));
    }
    if (path === "/users") {
      const search = url.searchParams.get("search")?.toLowerCase();
      const username = url.searchParams.get("username")?.toLowerCase();
      const hits = users.filter((u) =>
        search ? search === (u.publicEmail ?? "").toLowerCase() || (me.isAdmin && search === u.email.toLowerCase())
          : username ? u.username.toLowerCase() === username : false);
      return json(res, 200, hits.map((u) => ({ id: u.id, username: u.username, name: u.name ?? u.username, state: "active", public_email: u.publicEmail ?? "", ...(me.isAdmin ? { email: u.email } : {}) })));
    }
    return json(res, 404, { message: "404 Not Found" });
  });
  return { url: await listen(server), close: () => server.close() };
}

// --- embeddings (llama.cpp's OpenAI endpoint) ----------------------------------------------

const DIM = 768;

/** A deterministic vector in which overlapping words point the same way. */
export function embedText(text: string): number[] {
  const vec: number[] = Array.from({ length: DIM }, () => 0);
  const lower = text.toLowerCase();
  const words = lower.match(/[a-z0-9]+/g) ?? [];
  const grams = [...words];
  for (let i = 0; i < Math.max(lower.length - 3, 0); i += 3) grams.push(lower.slice(i, i + 4));
  for (const token of grams) {
    const d = createHash("sha256").update(token).digest();
    for (let k = 0; k < 3; k++) {
      const index = d.readUInt16LE(k * 4) % DIM;
      const sign = d[k * 4 + 2] & 1 ? 1 : -1;
      vec[index] += (sign * (1 + d[k * 4 + 3] / 255)) / (1 + k);
    }
  }
  if (!vec.some((x) => x !== 0)) vec[0] = 1;
  return vec;
}

export async function fakeEmbeddings() {
  const server = createServer(async (req, res) => {
    if (req.url === "/health") return json(res, 200, { status: "ok" });
    if (req.url === "/v1/embeddings" && req.method === "POST") {
      const b = await body(req);
      const input: string[] = typeof b.input === "string" ? [b.input] : b.input ?? [];
      return json(res, 200, { object: "list", model: b.model, data: input.map((t, index) => ({ object: "embedding", index, embedding: embedText(t) })) });
    }
    json(res, 404, { error: "not found" });
  });
  return { url: await listen(server), close: () => server.close() };
}

// --- LiteLLM ----------------------------------------------------------------------------

interface GwUser { user_id: string; max_budget: number | null; spend: number }

export async function fakeGateway(masterKey: string, model: string) {
  const users = new Map<string, GwUser>();
  const keys = new Map<string, { user: string; alias: string }>();
  const requests: { auth: string; body: any }[] = [];
  const auth = (req: IncomingMessage) => (req.headers.authorization ?? "").replace(/^Bearer /i, "");

  const server = createServer(async (req, res) => {
    const url = new URL(req.url ?? "/", "http://x");
    const p = url.pathname;
    if (p === "/health/liveliness") return json(res, 200, "I'm alive!");
    // For the test to inspect, never part of LiteLLM.
    if (p === "/_e2e/state")
      return json(res, 200, { users: [...users.values()], keys: [...keys].map(([k, v]) => ({ key: k, ...v })), requests: requests.map((r) => ({ auth: r.auth, tools: (r.body.tools ?? []).length, model: r.body.model })) });
    const key = auth(req);
    const isMaster = key === masterKey;
    if (p.startsWith("/user/") || p.startsWith("/key/")) {
      if (!isMaster) return json(res, 401, { error: { message: "Authentication Error, master key required" } });
      if (p === "/user/new") {
        const b = await body(req);
        if (users.has(b.user_id)) return json(res, 400, { error: { message: `User ${b.user_id} already exists` } });
        users.set(b.user_id, { user_id: b.user_id, max_budget: b.max_budget ?? null, spend: 0 });
        return json(res, 200, { user_id: b.user_id });
      }
      if (p === "/user/update") {
        const b = await body(req);
        const u = users.get(b.user_id) ?? { user_id: b.user_id, max_budget: null, spend: 0 };
        u.max_budget = b.max_budget ?? null;
        users.set(b.user_id, u);
        return json(res, 200, { ok: true });
      }
      if (p === "/user/delete") {
        const b = await body(req);
        for (const id of b.user_ids) {
          users.delete(id);
          for (const [k, v] of keys) if (v.user === id) keys.delete(k);
        }
        return json(res, 200, { ok: true });
      }
      if (p === "/user/info") {
        const id = url.searchParams.get("user_id")!;
        const mine = [...keys].filter(([, v]) => v.user === id).map(([k, v]) => ({ token: `tok-${k}`, key_alias: v.alias, key_name: `sk-...${k.slice(-4)}`, spend: 0 }));
        return json(res, 200, { user_info: users.get(id) ?? null, keys: mine });
      }
      if (p === "/user/list") return json(res, 200, { users: [...users.values()] });
      if (p === "/key/generate") {
        const b = await body(req);
        const k = `sk-${createHash("sha256").update(String(Math.random())).digest("hex").slice(0, 32)}`;
        keys.set(k, { user: b.user_id, alias: b.key_alias ?? "" });
        return json(res, 200, { key: k, token: `tok-${k}` });
      }
      if (p === "/key/delete") {
        const b = await body(req);
        for (const t of b.keys) keys.delete(String(t).replace(/^tok-/, ""));
        return json(res, 200, { ok: true });
      }
    }
    const owner = isMaster ? null : keys.get(key);
    if (!isMaster && !owner) return json(res, 401, { error: { message: "Authentication Error, invalid key" } });
    if (p === "/v1/models") return json(res, 200, { object: "list", data: [{ id: model, object: "model" }] });
    if (p === "/v1/chat/completions") {
      const b = await body(req);
      requests.push({ auth: key, body: b });
      const u = owner ? users.get(owner.user) : null;
      if (u && u.max_budget !== null && u.spend >= u.max_budget)
        return json(res, 400, { error: { message: `Budget has been exceeded! Current cost: ${u.spend}, Max budget: ${u.max_budget}` } });
      if (u) u.spend += 0.01;
      return chat(b, res);
    }
    json(res, 404, { error: { message: "not found" } });
  });

  async function chat(b: any, res: ServerResponse) {
    res.writeHead(200, { "Content-Type": "text/event-stream", "Cache-Control": "no-cache" });
    const send = (delta: object, finish: string | null = null, extra: object = {}) =>
      res.write(`data: ${JSON.stringify({ id: "c1", object: "chat.completion.chunk", model: b.model, choices: [{ index: 0, delta, finish_reason: finish }], ...extra })}\n\n`);
    const sleep = (ms: number) => new Promise((r) => setTimeout(r, ms));
    const messages: any[] = b.messages;
    const last = messages[messages.length - 1];
    const lastUser = [...messages].reverse().find((m) => m.role === "user")?.content ?? "";
    const find = /^find (\S+)/.exec(lastUser);
    if (last.role === "user" && find && b.tools) {
      send({ reasoning_content: "The question names a symbol, " });
      await sleep(30);
      send({ reasoning_content: "so I will look it up in the index." });
      send({ tool_calls: [{ index: 0, id: "call_find", type: "function", function: { name: "find_symbol", arguments: "" } }] });
      send({ tool_calls: [{ index: 0, function: { arguments: JSON.stringify({ name: find[1] }) } }] }, "tool_calls");
    } else if (last.role === "tool") {
      const text: string = last.content;
      let answer: string;
      try {
        const rows = JSON.parse(text);
        answer = rows.length
          ? `**${rows[0].name}** is defined in \`${rows[0].path}\` of **${rows[0].path_with_namespace}** — ${rows[0].doc || "no doc comment"}.`
          : "The index has no symbol by that name.";
      } catch {
        answer = `The index refused: ${text}`;
      }
      for (const part of answer.match(/.{1,12}/gs) ?? []) {
        send({ content: part });
        await sleep(5);
      }
      send({}, "stop", { usage: { prompt_tokens: 100, completion_tokens: 20, total_tokens: 120 } });
    } else if (lastUser.startsWith("slow")) {
      for (let i = 0; i < 400 && !res.destroyed; i++) {
        send({ content: `word${i} ` });
        await sleep(25);
      }
      send({}, "stop");
    } else {
      const answer = `**Hello!** You said: _${lastUser}_\n\n| column | value |\n|---|---|\n| model | ${b.model} |\n\n\`\`\`c\nint main(void) { return 0; }\n\`\`\``;
      for (const part of answer.match(/.{1,16}/gs) ?? []) {
        send({ content: part });
        await sleep(3);
      }
      send({}, "stop", { usage: { prompt_tokens: 10, completion_tokens: 30, total_tokens: 40 } });
    }
    res.end("data: [DONE]\n\n");
  }

  return { url: await listen(server), close: () => server.close(), users, keys, requests };
}
