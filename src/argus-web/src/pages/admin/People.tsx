import { useState, type FormEvent } from "react";
import { api, money, relTime, type User } from "../../api";
import { useAuth } from "../../auth";
import Secret from "../../components/Secret";
import { useAsync } from "../../components/useAsync";

interface UsersResponse { users: User[]; usage_error: string | null }

function budgetValue(raw: string): number | null {
  const t = raw.trim();
  return t === "" ? null : Number(t);
}

function EditRow({ user, onClose, onSaved }: { user: User; onClose: () => void; onSaved: (msg: string) => void }) {
  const [displayName, setDisplayName] = useState(user.display_name);
  const [role, setRole] = useState(user.role);
  const [gitlab, setGitlab] = useState(user.gitlab_username ?? "");
  const [budget, setBudget] = useState(user.max_budget == null ? "" : String(user.max_budget));
  const [error, setError] = useState<string | null>(null);
  async function save(e: FormEvent) {
    e.preventDefault();
    try {
      await api.patch(`/api/admin/users/${user.id}`, {
        display_name: displayName, role, gitlab_username: gitlab.trim() || null, max_budget: budgetValue(budget),
      });
      onSaved(`Saved ${user.username}.`);
    } catch (err) {
      setError(err instanceof Error ? err.message : String(err));
    }
  }
  return (
    <tr className="editing">
      <td colSpan={7}>
        <form className="grid-form" onSubmit={save} aria-label={`Edit ${user.username}`}>
          {error && <div className="msg bad span">{error}</div>}
          <label>Display name<input value={displayName} onChange={(e) => setDisplayName(e.target.value)} /></label>
          <label>Role<select value={role} onChange={(e) => setRole(e.target.value as User["role"])}><option value="user">user</option><option value="admin">admin</option></select></label>
          <label>GitLab username<input value={gitlab} onChange={(e) => setGitlab(e.target.value)} placeholder="only if the email does not match" /></label>
          <label>Budget (USD / period)<input value={budget} onChange={(e) => setBudget(e.target.value)} inputMode="decimal" placeholder="empty = default" aria-label="Budget" /></label>
          <div className="row span"><button className="btn primary" type="submit">Save</button><button className="btn ghost" type="button" onClick={onClose}>Cancel</button></div>
        </form>
      </td>
    </tr>
  );
}

export default function People() {
  const { me } = useAuth();
  const data = useAsync(() => api.get<UsersResponse>("/api/admin/users"), []);
  const [q, setQ] = useState("");
  const [editing, setEditing] = useState<number | null>(null);
  const [secret, setSecret] = useState<{ label: string; value: string } | null>(null);
  const [flash, setFlash] = useState<{ ok: boolean; text: string } | null>(null);
  const [form, setForm] = useState({ username: "", email: "", display_name: "", role: "user", gitlab_username: "", max_budget: "" });

  async function run(action: () => Promise<void>) {
    try {
      await action();
    } catch (err) {
      setFlash({ ok: false, text: err instanceof Error ? err.message : String(err) });
    }
    await data.reload();
  }

  async function create(e: FormEvent) {
    e.preventDefault();
    await run(async () => {
      const r = await api.post<{ user: User; password?: string; warning?: string }>("/api/admin/users", {
        ...form, gitlab_username: form.gitlab_username.trim() || null, max_budget: budgetValue(form.max_budget),
      });
      if (r.password) setSecret({ label: `Password for ${r.user.username}`, value: r.password });
      setFlash(r.warning ? { ok: false, text: r.warning } : { ok: true, text: `Created ${r.user.username}. Give them the password below.` });
      setForm({ username: "", email: "", display_name: "", role: "user", gitlab_username: "", max_budget: "" });
    });
  }

  const users = (data.value?.users ?? []).filter((u) => !q || `${u.username} ${u.email} ${u.display_name}`.toLowerCase().includes(q.toLowerCase()));

  return (
    <div className="page">
      <h1>People</h1>
      <p className="lede">Who can sign in, what they may do, and how much of the model they have used.</p>
      {flash && <div className={`msg ${flash.ok ? "ok" : "bad"}`} role="status">{flash.text}</div>}
      {secret && <Secret label={secret.label} value={secret.value} onDone={() => setSecret(null)} />}
      {data.value?.usage_error && <div className="msg bad">Spend is unavailable: {data.value.usage_error}</div>}
      <div className="card">
        <div className="row between">
          <h2>Everyone ({data.value?.users.length ?? 0})</h2>
          <input className="search" placeholder="Filter" value={q} onChange={(e) => setQ(e.target.value)} aria-label="Filter people" />
        </div>
        <table data-testid="people-table">
          <thead><tr><th>Person</th><th>Role</th><th>Status</th><th>Spend / budget</th><th>Last sign-in</th><th>GitLab</th><th /></tr></thead>
          <tbody>
            {users.map((u) => [
              <tr key={u.id}>
                <td><strong>{u.display_name || u.username}</strong><div className="dim small">{u.username} · {u.email}</div></td>
                <td>{u.role === "admin" ? <span className="badge">admin</span> : "user"}</td>
                <td>{u.disabled ? <span className="bad">disabled</span> : "active"}</td>
                <td>{money(u.spend ?? null)} / {u.max_budget == null ? "default" : money(u.max_budget)}</td>
                <td>{relTime(u.last_login_at)}</td>
                <td>{u.gitlab_username ?? <span className="dim">by email</span>}</td>
                <td className="right nowrap">
                  <button className="btn small" onClick={() => setEditing(editing === u.id ? null : u.id)}>Edit</button>{" "}
                  <button className="btn small" onClick={() => run(async () => {
                    if (!window.confirm(`Reset ${u.username}'s password? Their sessions end.`)) return;
                    const r = await api.post<{ password: string }>(`/api/admin/users/${u.id}/reset-password`);
                    setSecret({ label: `New password for ${u.username}`, value: r.password });
                  })}>Reset password</button>{" "}
                  {u.id !== me!.user.id && (
                    <>
                      <button className="btn small" onClick={() => run(async () => {
                        await api.patch(`/api/admin/users/${u.id}`, { disabled: !u.disabled });
                        setFlash({ ok: true, text: `${u.username} ${u.disabled ? "enabled" : "disabled"}.` });
                      })}>{u.disabled ? "Enable" : "Disable"}</button>{" "}
                      <button className="btn small danger" onClick={() => run(async () => {
                        if (!window.confirm(`Delete ${u.username}? Their conversations and keys go too.`)) return;
                        const r = await api.del<{ warning?: string }>(`/api/admin/users/${u.id}`);
                        setFlash(r.warning ? { ok: false, text: r.warning } : { ok: true, text: `Deleted ${u.username}.` });
                      })}>Delete</button>
                    </>
                  )}
                </td>
              </tr>,
              editing === u.id ? (
                <EditRow key={`e${u.id}`} user={u} onClose={() => setEditing(null)} onSaved={(text) => { setEditing(null); setFlash({ ok: true, text }); void data.reload(); }} />
              ) : null,
            ])}
          </tbody>
        </table>
      </div>
      <form className="card grid-form" onSubmit={create} aria-label="Add a person">
        <h2 className="span">Add a person</h2>
        <label>Username<input value={form.username} onChange={(e) => setForm({ ...form, username: e.target.value })} required /></label>
        <label>Email<input type="email" value={form.email} onChange={(e) => setForm({ ...form, email: e.target.value })} required /></label>
        <label>Display name<input value={form.display_name} onChange={(e) => setForm({ ...form, display_name: e.target.value })} /></label>
        <label>Role<select value={form.role} onChange={(e) => setForm({ ...form, role: e.target.value })}><option value="user">user</option><option value="admin">admin</option></select></label>
        <label>GitLab username<input value={form.gitlab_username} onChange={(e) => setForm({ ...form, gitlab_username: e.target.value })} placeholder="only if the email does not match" /></label>
        <label>Budget (USD / period)<input value={form.max_budget} onChange={(e) => setForm({ ...form, max_budget: e.target.value })} inputMode="decimal" placeholder="empty = default" /></label>
        <p className="dim span">A password is generated and shown once. The code index matches the email to a GitLab account to decide which repositories this person can see.</p>
        <div className="span"><button className="btn primary" type="submit">Add person</button></div>
      </form>
    </div>
  );
}
