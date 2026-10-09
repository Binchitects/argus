import { useState, type FormEvent } from "react";
import { useSearchParams } from "react-router";
import { api, type ExploreResult } from "../../api";
import { useAsync } from "../../components/useAsync";
import { useTable, type Key } from "../../components/useTable";

type RepoRow = ExploreResult["repos"][number];
type SymbolRow = ExploreResult["symbols"]["rows"][number];
type FileRow = ExploreResult["files"]["rows"][number];
const NONE: never[] = [];

const REPOS: Record<string, Key<RepoRow>> = { repo: (r) => r.path_with_namespace, files: (r) => r.files, symbols: (r) => r.symbols };
const SYMBOLS: Record<string, Key<SymbolRow>> = {
  name: (s) => `${s.name} ${s.signature ?? ""}`,
  kind: (s) => `${s.kind} ${s.scope ?? ""}`,
  where: (s) => `${s.path_with_namespace} ${s.path}:${s.line}`,
};
const FILES: Record<string, Key<FileRow>> = { path: (f) => f.path, lang: (f) => f.lang, symbols: (f) => f.symbols, repo: (f) => f.path_with_namespace };

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
  const repos = useTable<RepoRow>(d?.repos ?? NONE, REPOS, { by: "repo", desc: false });
  const symbols = useTable<SymbolRow>(d?.symbols.rows ?? NONE, SYMBOLS);
  const files = useTable<FileRow>(d?.files.rows ?? NONE, FILES);

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
          <div className="row between"><h2>Repositories</h2>{repos.search("Filter the repositories")}</div>
          <table>
            <thead><tr>{repos.header("repo", "Repository")}{repos.header("files", "Files")}{repos.header("symbols", "Symbols")}</tr></thead>
            <tbody>{repos.rows.map((r) => <tr key={r.path_with_namespace}><td>{r.path_with_namespace}</td><td>{r.files}</td><td>{r.symbols}</td></tr>)}</tbody>
          </table>
        </div>
      )}
      {(q || repo) && d && (
        <>
          <div className="card">
            <div className="row between"><h2>Symbols</h2>{symbols.search("Filter the symbols")}</div>
            <table data-testid="symbols-table">
              <thead><tr>{symbols.header("name", "Name")}{symbols.header("kind", "Kind")}{symbols.header("where", "Where")}</tr></thead>
              <tbody>
                {symbols.rows.map((s, i) => (
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
            <div className="row between"><h2>Files</h2>{files.search("Filter the files")}</div>
            <p className="dim small">A file with 0 symbols is one the extractor did not recognise.</p>
            <table>
              <thead><tr>{files.header("path", "Path")}{files.header("lang", "Language")}{files.header("symbols", "Symbols")}{files.header("repo", "Repository")}</tr></thead>
              <tbody>
                {files.rows.map((f, i) => (
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
