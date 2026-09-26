import { useState, type FormEvent } from "react";
import { useSearchParams } from "react-router";
import { api, type ExploreResult } from "../../api";
import { useAsync } from "../../components/useAsync";

export default function Explore() {
  const [params, setParams] = useSearchParams();
  const q = params.get("q") ?? "";
  const repo = params.get("repo") ?? "";
  const [draft, setDraft] = useState(q);
  const [draftRepo, setDraftRepo] = useState(repo);
  const data = useAsync(
    () => api.get<ExploreResult>(`/admin/explore?q=${encodeURIComponent(q)}&repo=${encodeURIComponent(repo)}&limit=50`),
    [q, repo],
  );
  const d = data.value;

  function submit(e: FormEvent) {
    e.preventDefault();
    const next: Record<string, string> = {};
    if (draft.trim()) next.q = draft.trim();
    if (draftRepo) next.repo = draftRepo;
    setParams(next);
  }

  return (
    <div className="page">
      <h1>Explore</h1>
      <p className="lede">What the index actually holds, across every repository — so "the tool found nothing" can be told apart from "it was never indexed".</p>
      <form className="row" onSubmit={submit}>
        <input value={draft} onChange={(e) => setDraft(e.target.value)} placeholder="symbol or path fragment" aria-label="Search the index" style={{ minWidth: 280 }} />
        <select value={draftRepo} onChange={(e) => setDraftRepo(e.target.value)} aria-label="Repository">
          <option value="">every repository</option>
          {(d?.repos ?? []).map((r) => (
            <option key={r.path_with_namespace} value={r.path_with_namespace}>{r.path_with_namespace} ({r.symbols} symbols)</option>
          ))}
        </select>
        <button className="btn primary" type="submit">Search</button>
      </form>
      {data.error && <div className="msg bad">Argus did not answer: {data.error}</div>}
      {d?.error && <div className="msg bad"><strong>The index could not be read.</strong> {d.error}</div>}
      {!q && !repo && d && (
        <div className="card">
          <h2>Repositories</h2>
          <table>
            <thead><tr><th>Repository</th><th>Files</th><th>Symbols</th></tr></thead>
            <tbody>{d.repos.map((r) => <tr key={r.path_with_namespace}><td>{r.path_with_namespace}</td><td>{r.files}</td><td>{r.symbols}</td></tr>)}</tbody>
          </table>
        </div>
      )}
      {(q || repo) && d && (
        <>
          <div className="card">
            <h2>Symbols</h2>
            <table data-testid="symbols-table">
              <thead><tr><th>Name</th><th>Kind</th><th>Where</th></tr></thead>
              <tbody>
                {d.symbols.rows.map((s, i) => (
                  <tr key={i}>
                    <td><strong>{s.name}</strong> {s.is_public ? <span className="badge">public</span> : <span className="dim">private</span>}<div className="dim small mono">{s.signature}</div></td>
                    <td>{s.kind}<div className="dim small">{s.scope}</div></td>
                    <td>{s.path_with_namespace}<div className="dim small mono">{s.path}:{s.line}</div></td>
                  </tr>
                ))}
                {d.symbols.rows.length === 0 && <tr><td colSpan={3} className="dim">No symbol matches that.</td></tr>}
              </tbody>
            </table>
            {d.symbols.capped && <p className="dim">More symbols match than are shown — narrow the search.</p>}
          </div>
          <div className="card">
            <h2>Files</h2>
            <p className="dim small">A file with 0 symbols is one the extractor did not recognise.</p>
            <table>
              <thead><tr><th>Path</th><th>Language</th><th>Symbols</th><th>Repository</th></tr></thead>
              <tbody>
                {d.files.rows.map((f, i) => (
                  <tr key={i}><td className="mono">{f.path}</td><td>{f.lang ?? "—"}</td><td className={f.symbols ? "" : "bad"}>{f.symbols}</td><td>{f.path_with_namespace}</td></tr>
                ))}
                {d.files.rows.length === 0 && <tr><td colSpan={4} className="dim">No file matches that.</td></tr>}
              </tbody>
            </table>
            {d.files.capped && <p className="dim">More files match than are shown — narrow the search.</p>}
          </div>
        </>
      )}
    </div>
  );
}
