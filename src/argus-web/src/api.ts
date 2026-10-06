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
  put: <T>(path: string, body: unknown) => request<T>("PUT", path, body),
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
  /** The version, its licence and where its source is (the AGPL offers it to everyone who uses the app). */
  about: { version: string; license: string; source: string };
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

/** Where the run going now is with one repository. */
export interface RepoProgress {
  state: "queued" | "fetching" | "files" | "symbols" | "embedding" | "done" | "failed";
  branch?: string | null;
  done?: number | null;
  total?: number | null;
  outcome?: string | null;
  message?: string | null;
}

export type RepoState = "indexing" | "queued" | "failed" | "stale" | "indexed" | "never" | "off";

/** A repository GitLab lists, what is chosen for it, and how its index is. */
export interface Repo {
  gitlab_id: number;
  repo: string;
  name: string;
  group: string;
  default_branch: string;
  included: boolean;
  branches: string[];
  listed: boolean;
  language: string | null;
  schedule: string;
  schedule_words: string;
  schedule_kind: string;
  next_run_at: number | null;
  last_run_at: number | null;
  state: RepoState;
  problem: string | null;
  progress: RepoProgress | null;
  indexed: { branch: string; sha: string | null; message: string | null; files: number; symbols: number; error: string | null; stale: boolean }[];
}

export interface ReposView {
  new_repos: "include" | "exclude";
  schedule: { default: string; words: string; time_zone: string; zone_problem: string | null; pass_interval: number; next_pass_at: number | null };
  listed_at: number | null;
  running: boolean;
  pending: string[];
  repos: Repo[];
  found?: { new: number; moved: number; set_aside: number };
}

export interface BatchResult {
  action: string;
  results: { gitlab_id: number; repo: string | null; ok: boolean; message: string }[];
}

export interface LogLine {
  run: number;
  at: number;
  level: "info" | "warning" | "error";
  text: string;
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
  packs: (Pack & { source?: "library" | "installed" })[];
  /** The pack library's files (ARGUS_PACK_LIBRARY), and which are loaded. */
  library?: (Pack & { file: string; loaded: boolean })[];
  library_dir?: string | null;
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

/** A time to come: "in 5m", "in 3h", "in 2d"; "now" once it is due. */
export function inTime(ts: number | null | undefined): string {
  if (!ts) return "—";
  const s = ts - Date.now() / 1000;
  if (s < 60) return "now";
  if (s < 3600) return `in ${Math.round(s / 60)}m`;
  if (s < 86400) return `in ${Math.round(s / 3600)}h`;
  return `in ${Math.round(s / 86400)}d`;
}

export function money(v: number | null | undefined): string {
  return v === null || v === undefined ? "—" : `$${v.toFixed(2)}`;
}

export function megabytes(n: number): string {
  return n < 1024 ** 3 ? `${(n / 1048576).toFixed(1)} MB` : `${(n / 1024 ** 3).toFixed(2)} GB`;
}
