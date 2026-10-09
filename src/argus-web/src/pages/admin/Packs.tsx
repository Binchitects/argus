import { useState, type FormEvent } from "react";
import { api, megabytes, relTime, type PacksStatus } from "../../api";
import { useAsync } from "../../components/useAsync";
import { useTable, type Key } from "../../components/useTable";

type Library = NonNullable<PacksStatus["library"]>[number];
type Installed = PacksStatus["packs"][number];
const NONE: never[] = [];

const LIBRARY: Record<string, Key<Library>> = {
  name: (p) => `${p.name} ${p.version}`,
  file: (p) => p.file,
  model: (p) => `${p.model}/${p.dim}`,
  size: (p) => p.size_bytes,
  state: (p) => (p.loaded ? "Loaded" : "Not loaded"),
};

const INSTALLED: Record<string, Key<Installed>> = {
  name: (p) => p.name,
  version: (p) => p.version,
  model: (p) => `${p.model}/${p.dim}`,
  size: (p) => p.size_bytes,
  license: (p) => p.license,
};

export default function Packs() {
  const data = useAsync(() => api.get<PacksStatus>("/admin/packs"), [], (v) => (v?.job.state === "running" ? 1500 : null));
  const [source, setSource] = useState("");
  const [sha, setSha] = useState("");
  const [flash, setFlash] = useState<{ ok: boolean; text: string } | null>(null);
  const d = data.value;
  const running = d?.job.state === "running";
  const library = useTable<Library>(d?.library ?? NONE, LIBRARY, { by: "name", desc: false });
  const installed = useTable<Installed>(d?.packs ?? NONE, INSTALLED, { by: "name", desc: false });

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
      {d?.library_dir && (
        <div className="card">
          <div className="row between"><h2>Pack library</h2>{library.search("Filter the library")}</div>
          <p className="dim small">Built packs in <code>{d.library_dir}</code>. Loading links one in: instant, nothing copied; unloading leaves it in the library.</p>
          <table data-testid="library-table">
            <thead><tr>{library.header("name", "Pack")}{library.header("file", "File")}{library.header("model", "Embedding")}{library.header("size", "Size", "right")}{library.header("state", "State")}<th /></tr></thead>
            <tbody>
              {library.rows.map((p) => (
                <tr key={p.file}>
                  <td><strong>{p.name}</strong> <span className="dim">{p.version}</span>{!p.compatible && <div className="warn small">{p.incompatible_reason}</div>}</td>
                  <td className="dim"><code>{p.file}</code></td>
                  <td className="dim">{p.model}/{p.dim}</td>
                  <td className="right">{megabytes(p.size_bytes)}</td>
                  <td>{p.loaded ? <strong>Loaded</strong> : <span className="dim">Not loaded</span>}</td>
                  <td className="right">
                    {p.loaded ? (
                      <button className="btn small" disabled={running} onClick={() => void act("/admin/packs/remove", { name: p.name }, `Unloaded ${p.name}.`)}>Unload</button>
                    ) : (
                      <button className="btn small primary" disabled={running || !p.compatible} onClick={() => void act("/admin/packs/load", { file: p.file }, `Loaded ${p.name}.`)}>Load</button>
                    )}
                  </td>
                </tr>
              ))}
              {(d.library ?? []).length === 0 && <tr><td colSpan={6} className="dim">The library is empty: build packs into it with tools/build-packs.sh.</td></tr>}
            </tbody>
          </table>
        </div>
      )}
      <div className="card">
        {running ? (
          <div className="msg">{(d!.job.action ?? "working").replace(/^./, (c) => c.toUpperCase())} <b>{d!.job.target}</b> — started {relTime(d!.job.started)}.</div>
        ) : d?.job.finished ? (
          <div className={`msg ${d.job.returncode === 0 ? "ok" : "bad"}`} data-testid="pack-finished">Last pack operation finished {relTime(d.job.finished)} — {d.job.returncode === 0 ? "finished cleanly" : "failed; the log below names why"}</div>
        ) : null}
        <div className="row between"><h2>Installed packs</h2>{installed.search("Filter the installed packs")}</div>
        <table data-testid="packs-table">
          <thead><tr>{installed.header("name", "Pack")}{installed.header("version", "Version")}{installed.header("model", "Embedding")}{installed.header("size", "Size", "right")}{installed.header("license", "Licence")}<th /></tr></thead>
          <tbody>
            {installed.rows.map((p) => (
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
