import { useState, type FormEvent } from "react";
import { api, megabytes, relTime, type PacksStatus } from "../../api";
import { useAsync } from "../../components/useAsync";

export default function Packs() {
  const data = useAsync(() => api.get<PacksStatus>("/admin/packs"), [], (v) => (v?.job.state === "running" ? 1500 : null));
  const [source, setSource] = useState("");
  const [sha, setSha] = useState("");
  const [flash, setFlash] = useState<{ ok: boolean; text: string } | null>(null);
  const d = data.value;
  const running = d?.job.state === "running";

  async function act(path: string, body: unknown, message: string) {
    try {
      await api.post(path, body);
      setFlash({ ok: true, text: message });
    } catch (err) {
      setFlash({ ok: false, text: `Argus refused: ${err instanceof Error ? err.message : String(err)}` });
    }
    await data.reload();
  }

  async function install(e: FormEvent) {
    e.preventDefault();
    await act("/admin/packs/install", { source: source.trim(), sha256: sha.trim() }, "Installing. This page shows progress.");
    setSource("");
    setSha("");
  }

  const total = (d?.packs ?? []).reduce((n, p) => n + p.size_bytes, 0);
  return (
    <div className="page">
      <h1>Knowledge packs</h1>
      <p className="lede">Public documentation corpora — prose, API symbols and embeddings in one file each. They are what the six docs tools answer from.</p>
      {data.error && <div className="msg bad">Argus did not answer: {data.error}</div>}
      {flash && <div className={`msg ${flash.ok ? "ok" : "bad"}`} role="status">{flash.text}</div>}
      <div className="tiles">
        <div className="tile" data-testid="tile-packs"><div className="tile-label">Packs installed</div><div className="tile-value">{d?.packs.length ?? "—"}</div><div className="dim">{d?.packs.length ? `${megabytes(total)} on disk` : "none yet"}</div></div>
      </div>
      <div className="card">
        {running ? (
          <div className="msg">{(d!.job.action ?? "working").replace(/^./, (c) => c.toUpperCase())} <b>{d!.job.target}</b> — started {relTime(d!.job.started)}.</div>
        ) : d?.job.finished ? (
          <div className={`msg ${d.job.returncode === 0 ? "ok" : "bad"}`} data-testid="pack-finished">Last pack operation finished {relTime(d.job.finished)} — {d.job.returncode === 0 ? "finished cleanly" : "failed; the log below names why"}</div>
        ) : null}
        <table data-testid="packs-table">
          <thead><tr><th>Pack</th><th>Version</th><th>Embedding</th><th className="right">Size</th><th>Licence</th><th /></tr></thead>
          <tbody>
            {(d?.packs ?? []).map((p) => (
              <tr key={p.name}>
                <td><strong>{p.name}</strong>{!p.compatible && <div className="warn small">{p.incompatible_reason ?? "incompatible embedding model"} — lookup and text search still work</div>}</td>
                <td>{p.version}</td>
                <td className="dim">{p.model}/{p.dim}</td>
                <td className="right">{megabytes(p.size_bytes)}</td>
                <td className="dim">{p.license}</td>
                <td className="right">
                  <button className="btn small danger" disabled={running} onClick={() => {
                    if (window.confirm(`Remove ${p.name}?`)) void act("/admin/packs/remove", { name: p.name }, `Removed ${p.name}.`);
                  }}>Remove</button>
                </td>
              </tr>
            ))}
            {d && d.packs.length === 0 && <tr><td colSpan={6} className="dim">No packs installed.</td></tr>}
          </tbody>
        </table>
        {d && d.job.tail.length > 0 && <pre className="log">{d.job.tail.join("\n")}</pre>}
        <form className="row" onSubmit={install}>
          <input value={source} onChange={(e) => setSource(e.target.value)} placeholder="https://…/win32.arguspack or a path on the server" required disabled={running} aria-label="Pack source" style={{ minWidth: 340 }} />
          <input value={sha} onChange={(e) => setSha(e.target.value)} placeholder="sha256 (recommended)" disabled={running} aria-label="SHA-256" style={{ minWidth: 240 }} />
          <button className="btn primary" type="submit" disabled={running}>Install pack</button>
        </form>
        <p className="dim small">A digest mismatch is refused and leaves nothing behind; without a digest the pack is installed on trust.</p>
        <div className="row">
          <button className="btn" disabled={running || !d?.index_url} onClick={() => void act("/admin/packs/update", {}, "Updating packs. This page shows progress.")}>Update all packs</button>
          <span className="dim small">{d?.index_url ? <>From <code>{d.index_url}</code>. Current packs are left alone.</> : "Set ARGUS_PACK_INDEX_URL to a published pack index to enable updates."}</span>
        </div>
      </div>
    </div>
  );
}
