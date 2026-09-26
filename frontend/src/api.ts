// The one place the app talks to the backend. Every change carries
// X-Argus-Request, which the backend requires of a browser session so that a
// cross-site form cannot act on someone's behalf.

export class ApiError extends Error {
  constructor(message: string, readonly status: number) {
    super(message);
  }
}

async function request<T>(method: string, path: string, body?: unknown): Promise<T> {
  const headers: Record<string, string> = {};
  if (method !== "GET") headers["X-Argus-Request"] = "1";
  if (body !== undefined) headers["Content-Type"] = "application/json";
  const resp = await fetch(path, {
    method,
    headers,
    body: body === undefined ? undefined : JSON.stringify(body),
    credentials: "same-origin",
  });
  const text = await resp.text();
  let data: unknown = null;
  try {
    data = text ? JSON.parse(text) : null;
  } catch {
    data = null;
  }
  if (!resp.ok) {
    const message = (data as { error?: string } | null)?.error ?? `${method} ${path} failed (HTTP ${resp.status})`;
    throw new ApiError(message, resp.status);
  }
  return data as T;
}

export const api = {
  get: <T>(path: string) => request<T>("GET", path),
  post: <T>(path: string, body?: unknown) => request<T>("POST", path, body ?? {}),
  patch: <T>(path: string, body: unknown) => request<T>("PATCH", path, body),
  del: <T>(path: string) => request<T>("DELETE", path),
};

// --- types ------------------------------------------------------------------------

export interface User {
  id: number;
  username: string;
  email: string;
  display_name: string;
  role: "admin" | "user";
  gitlab_username: string | null;
  disabled: boolean;
  created_at: number;
  last_login_at: number | null;
  spend?: number;
  max_budget?: number | null;
}

export interface Me {
  user: User;
  endpoints: { mcp: string; gateway: string | null };
  usage?: { spend: number; max_budget: number | null };
  usage_error?: string;
}

export interface Conversation {
  id: string;
  title: string;
  model: string | null;
  created_at: number;
  updated_at: number;
}

export interface ToolCall {
  id: string;
  type: "function";
  function: { name: string; arguments: string };
}

export interface Message {
  id: number;
  role: "user" | "assistant" | "tool";
  content: string;
  reasoning?: string;
  tool_calls?: ToolCall[];
  tool_call_id?: string;
  name?: string;
  is_error?: boolean;
  created_at: number;
}

export type ChatEvent =
  | { type: "start"; conversation_id: string; model: string }
  | { type: "reasoning"; text: string }
  | { type: "content"; text: string }
  | { type: "tool_call"; id: string; name: string; arguments: string }
  | { type: "tool_result"; id: string; name: string; content: string; is_error: boolean }
  | { type: "error"; message: string }
  | { type: "done"; finish_reason: string | null; usage: Record<string, number> | null };

/** Send one message and yield the server's events as they arrive. Abort with the signal to stop generating. */
export async function* sendMessage(
  conversationId: string,
  content: string,
  model: string | null,
  tools: boolean,
  signal: AbortSignal,
): AsyncGenerator<ChatEvent> {
  const resp = await fetch(`/api/conversations/${encodeURIComponent(conversationId)}/messages`, {
    method: "POST",
    headers: { "Content-Type": "application/json", "X-Argus-Request": "1" },
    body: JSON.stringify({ content, model, tools }),
    credentials: "same-origin",
    signal,
  });
  if (!resp.ok || !resp.body) {
    let message = `The message could not be sent (HTTP ${resp.status}).`;
    try {
      message = ((await resp.json()) as { error?: string }).error ?? message;
    } catch {
      /* not JSON */
    }
    yield { type: "error", message };
    return;
  }
  const reader = resp.body.pipeThrough(new TextDecoderStream()).getReader();
  let buffer = "";
  for (;;) {
    const { value, done } = await reader.read();
    if (done) break;
    buffer += value;
    let cut: number;
    while ((cut = buffer.indexOf("\n\n")) >= 0) {
      const frame = buffer.slice(0, cut);
      buffer = buffer.slice(cut + 2);
      for (const line of frame.split("\n")) {
        if (line.startsWith("data: ")) yield JSON.parse(line.slice(6)) as ChatEvent;
      }
    }
  }
}

// --- the code index's operator surface ---------------------------------------------

export interface IndexJob {
  state: "idle" | "running";
  branches: string[];
  started: number | null;
  finished: number | null;
  returncode: number | null;
  tail: string[];
  trigger: string | null;
  allow_partial?: boolean;
  repos_error?: string;
}

export interface IndexStatus {
  job: IndexJob;
  repos: {
    repo: string;
    branch: string;
    default_branch: string;
    last_run_at: number | null;
    timed_out: boolean;
    symbols_failed: number | null;
  }[];
  index: {
    repos?: number;
    stale?: number;
    errored?: number;
    never_run?: number;
    files?: number;
    symbols?: number;
    stale_names?: string[];
    stale_after?: number;
    error?: string;
  };
  interval: number;
  webhook: boolean;
  pending: string[];
}

export interface Pack {
  name: string;
  version: string;
  model: string;
  dim: number;
  size_bytes: number;
  license: string;
  commit: string;
  compatible: boolean;
  incompatible_reason: string | null;
}

export interface PacksStatus {
  packs: Pack[];
  job: { state: string; action: string | null; target: string | null; started: number | null; finished: number | null; returncode: number | null; tail: string[] };
  index_url: string;
  packs_dir?: string;
  error?: string;
}

export interface ExploreResult {
  repos: { path_with_namespace: string; symbols: number; files: number; [k: string]: unknown }[];
  symbols: { rows: { name: string; kind: string; scope: string | null; signature: string | null; is_public: number; path: string; line: number; path_with_namespace: string }[]; capped: boolean };
  files: { rows: { path: string; lang: string | null; symbols: number; path_with_namespace: string }[]; capped: boolean };
  error?: string;
}

export interface Overview {
  services: { name: string; ok: boolean; detail: string; ms: number }[];
  users: { total: number; admins: number; disabled: number };
  spend?: number;
  spend_error?: string;
}

// --- formatting ------------------------------------------------------------------

export function relTime(ts: number | null | undefined): string {
  if (!ts) return "never";
  const s = Math.max(0, Date.now() / 1000 - ts);
  if (s < 60) return `${Math.round(s)}s ago`;
  if (s < 3600) return `${Math.round(s / 60)}m ago`;
  if (s < 86400) return `${Math.round(s / 3600)}h ago`;
  return `${Math.round(s / 86400)}d ago`;
}

export function money(v: number | null | undefined): string {
  return v === null || v === undefined ? "—" : `$${v.toFixed(2)}`;
}

export function megabytes(n: number): string {
  return n < 1024 ** 3 ? `${(n / 1048576).toFixed(1)} MB` : `${(n / 1024 ** 3).toFixed(2)} GB`;
}
