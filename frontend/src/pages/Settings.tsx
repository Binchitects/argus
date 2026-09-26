import { useState, type FormEvent } from "react";
import { api, money, relTime } from "../api";
import { useAuth } from "../auth";
import Secret from "../components/Secret";
import { useAsync } from "../components/useAsync";

interface ApiKey {
  id: number;
  name: string;
  prefix: string;
  created_at: number;
  last_used_at: number | null;
}
interface ModelKey {
  token: string;
  alias: string;
  masked: string | null;
  spend: number | null;
}

function Password() {
  const [current, setCurrent] = useState("");
  const [next, setNext] = useState("");
  const [msg, setMsg] = useState<{ ok: boolean; text: string } | null>(null);
  async function submit(e: FormEvent) {
    e.preventDefault();
    try {
      await api.post("/api/me/password", { current, new: next });
      setMsg({ ok: true, text: "Password changed. Every other session has been signed out." });
      setCurrent("");
      setNext("");
    } catch (err) {
      setMsg({ ok: false, text: err instanceof Error ? err.message : String(err) });
    }
  }
  return (
    <form className="card" onSubmit={submit} aria-label="Change password">
      <h2>Password</h2>
      {msg && <div className={`msg ${msg.ok ? "ok" : "bad"}`}>{msg.text}</div>}
      <div className="row">
        <input type="password" placeholder="Current password" value={current} onChange={(e) => setCurrent(e.target.value)} autoComplete="current-password" aria-label="Current password" required />
        <input type="password" placeholder="New password (12+ characters)" value={next} onChange={(e) => setNext(e.target.value)} autoComplete="new-password" minLength={12} aria-label="New password" required />
        <button className="btn" type="submit">Change password</button>
      </div>
    </form>
  );
}

function CodeKeys({ mcpUrl }: { mcpUrl: string }) {
  const keys = useAsync(() => api.get<ApiKey[]>("/api/me/keys"), []);
  const [name, setName] = useState("");
  const [shown, setShown] = useState<string | null>(null);
  const [error, setError] = useState<string | null>(null);
  async function create(e: FormEvent) {
    e.preventDefault();
    try {
      const r = await api.post<{ key: string }>("/api/me/keys", { name });
      setShown(r.key);
      setName("");
      await keys.reload();
    } catch (err) {
      setError(err instanceof Error ? err.message : String(err));
    }
  }
  return (
    <div className="card" data-testid="code-keys">
      <h2>Code index keys</h2>
      <p className="dim">
        For editors and agents that speak MCP (Claude Code, Continue, Qwen Code…). Point them at <code>{mcpUrl}</code> with{" "}
        <code>Authorization: Bearer &lt;key&gt;</code>. The key sees exactly the repositories your GitLab account can read.
      </p>
      {shown && <Secret label="Your new code index key" value={shown} onDone={() => setShown(null)} />}
      {error && <div className="msg bad">{error}</div>}
      <table>
        <thead>
          <tr><th>Name</th><th>Key</th><th>Created</th><th>Last used</th><th /></tr>
        </thead>
        <tbody>
          {(keys.value ?? []).map((k) => (
            <tr key={k.id}>
              <td>{k.name}</td>
              <td><code>{k.prefix}…</code></td>
              <td>{relTime(k.created_at)}</td>
              <td>{relTime(k.last_used_at)}</td>
              <td className="right">
                <button className="btn small danger" onClick={async () => {
                  if (!window.confirm(`Revoke “${k.name}”? Anything using it stops working.`)) return;
                  await api.del(`/api/me/keys/${k.id}`);
                  await keys.reload();
                }}>Revoke</button>
              </td>
            </tr>
          ))}
          {keys.value?.length === 0 && <tr><td colSpan={5} className="dim">No keys yet.</td></tr>}
        </tbody>
      </table>
      <form className="row" onSubmit={create}>
        <input placeholder="Name, e.g. laptop" value={name} onChange={(e) => setName(e.target.value)} maxLength={60} required aria-label="Code key name" />
        <button className="btn" type="submit">Create code key</button>
      </form>
    </div>
  );
}

function ModelKeys({ gatewayUrl }: { gatewayUrl: string | null }) {
  const keys = useAsync(() => api.get<ModelKey[]>("/api/me/model-keys"), []);
  const [name, setName] = useState("");
  const [shown, setShown] = useState<string | null>(null);
  const [error, setError] = useState<string | null>(null);
  async function create(e: FormEvent) {
    e.preventDefault();
    try {
      const r = await api.post<{ key: string }>("/api/me/model-keys", { name });
      setShown(r.key);
      setName("");
      await keys.reload();
    } catch (err) {
      setError(err instanceof Error ? err.message : String(err));
    }
  }
  return (
    <div className="card" data-testid="model-keys">
      <h2>Model API keys</h2>
      <p className="dim">
        OpenAI-compatible keys for scripts and editors{gatewayUrl ? <> — base URL <code>{gatewayUrl}</code></> : null}. Usage counts
        against your budget, the same as chatting here.
      </p>
      {keys.error && <div className="msg bad">{keys.error}</div>}
      {shown && <Secret label="Your new model API key" value={shown} onDone={() => setShown(null)} />}
      {error && <div className="msg bad">{error}</div>}
      <table>
        <thead>
          <tr><th>Name</th><th>Key</th><th>Spend</th><th /></tr>
        </thead>
        <tbody>
          {(keys.value ?? []).map((k) => (
            <tr key={k.token}>
              <td>{k.alias || "—"}</td>
              <td><code>{k.masked ?? "sk-…"}</code></td>
              <td>{money(k.spend)}</td>
              <td className="right">
                <button className="btn small danger" onClick={async () => {
                  if (!window.confirm(`Revoke “${k.alias}”?`)) return;
                  await api.del(`/api/me/model-keys/${encodeURIComponent(k.token)}`);
                  await keys.reload();
                }}>Revoke</button>
              </td>
            </tr>
          ))}
          {keys.value?.length === 0 && <tr><td colSpan={4} className="dim">No keys yet.</td></tr>}
        </tbody>
      </table>
      <form className="row" onSubmit={create}>
        <input placeholder="Name, e.g. editor" value={name} onChange={(e) => setName(e.target.value)} maxLength={60} required aria-label="Model key name" />
        <button className="btn" type="submit" disabled={!!keys.error}>Create model key</button>
      </form>
    </div>
  );
}

export default function Settings() {
  const { me } = useAuth();
  const user = me!.user;
  const usage = me!.usage;
  const pct = usage && usage.max_budget ? Math.min(100, (usage.spend / usage.max_budget) * 100) : null;
  return (
    <div className="page">
      <h1>Settings &amp; keys</h1>
      <div className="tiles">
        <div className="tile"><div className="tile-label">Signed in as</div><div className="tile-value small">{user.username}</div><div className="dim">{user.email}</div></div>
        <div className="tile"><div className="tile-label">Role</div><div className="tile-value small">{user.role}</div><div className="dim">{user.gitlab_username ? `GitLab: ${user.gitlab_username}` : "GitLab matched by email"}</div></div>
        <div className="tile">
          <div className="tile-label">Usage this period</div>
          <div className="tile-value small">{usage ? money(usage.spend) : "—"}</div>
          <div className="dim">{usage ? (usage.max_budget ? `of ${money(usage.max_budget)}` : "no limit") : me!.usage_error ?? "gateway not configured"}</div>
          {pct !== null && <div className="meter" aria-label="Budget used"><span style={{ width: `${pct}%` }} /></div>}
        </div>
      </div>
      <Password />
      <CodeKeys mcpUrl={me!.endpoints.mcp} />
      <ModelKeys gatewayUrl={me!.endpoints.gateway} />
    </div>
  );
}
