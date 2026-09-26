import { useState, type FormEvent } from "react";
import { api, relTime, type IndexStatus } from "../../api";
import { useAsync } from "../../components/useAsync";

const EXIT: Record<number, string> = {
  0: "completed",
  1: "ran, but at least one repository is unhealthy — the log names it",
  3: "could not reach GitLab, or the token cannot enumerate every repository",
  4: "preflight failed — ctags is missing or not Universal Ctags, or the include graph could not be rebuilt",
  [-1]: "the run could not be started at all",
};

function duration(s: number): string {
  if (s % 3600 === 0) return `${s / 3600} h`;
  if (s % 60 === 0) return `${s / 60} min`;
  return `${s} s`;
}

export default function Indexing() {
  const status = useAsync(() => api.get<IndexStatus>("/admin/index/status"), [], (v) => (v?.job.state === "running" ? 2000 : null));
  const [branches, setBranches] = useState("");
  const [partial, setPartial] = useState(false);
  const [flash, setFlash] = useState<{ ok: boolean; text: string } | null>(null);
  const s = status.value;
  const job = s?.job;
  const running = job?.state === "running";

  async function start(e: FormEvent) {
    e.preventDefault();
    const list = [...new Set(branches.split(/\s+/).filter(Boolean))];
    try {
      await api.post("/admin/index", { branches: list, allow_partial: partial });
      setFlash({ ok: true, text: `Indexing started across all repositories (${list.length ? list.join(", ") : "default branches"}).` });
    } catch (err) {
      setFlash({ ok: false, text: err instanceof Error && err.message.includes("in progress") ? "An index run is already in progress." : String(err instanceof Error ? err.message : err) });
    }
    await status.reload();
  }

  const log = (job?.tail ?? []).join("\n");
  return (
    <div className="page">
      <h1>Indexing</h1>
      <p className="lede">The code index: what it covers and what it is doing.</p>
      {status.error && <div className="msg bad">Argus did not answer: {status.error}</div>}
      {flash && <div className={`msg ${flash.ok ? "ok" : "bad"}`} role="status">{flash.text}</div>}
      {s && (
        <div className="card">
          <h2>Schedule</h2>
          {s.interval > 0 ? (
            <p className="dim">Reindexes itself every <b>{duration(s.interval)}</b>.{s.webhook ? " It also indexes a repository as soon as GitLab reports a push." : " Set ARGUS_WEBHOOK_TOKEN and a GitLab push webhook to index changes immediately."}</p>
          ) : (
            <p className="dim"><b>Automatic reindexing is off.</b> The index only advances when someone starts a pass here. Set ARGUS_INDEX_INTERVAL to change this.</p>
          )}
          {s.pending.length > 0 && <div className="msg">Queued from GitLab pushes: <b>{s.pending.join(", ")}</b></div>}
          {running ? (
            <div className="msg" data-testid="index-running">
              Indexing <b>{job!.branches.length ? job!.branches.join(", ") : "default branches"}</b>, started by {job!.trigger === "webhook" ? "a GitLab push" : job!.trigger === "schedule" ? "the schedule" : "this console"} {relTime(job!.started)}.
            </div>
          ) : job?.finished ? (
            <div className={`msg ${job.returncode === 0 ? "ok" : "bad"}`} data-testid="index-finished">
              Last run finished {relTime(job.finished)} — exit {job.returncode}: {EXIT[job.returncode ?? -1] ?? `unrecognised exit code ${job.returncode}`}
            </div>
          ) : (
            <p className="dim">No run has been started since the server last restarted.</p>
          )}
          {log && (
            <>
              <div className="dim small">{running ? "Run log" : "Log from the last run"}</div>
              <pre className="log">{log}</pre>
            </>
          )}
          <form className="row" onSubmit={start}>
            <input value={branches} onChange={(e) => setBranches(e.target.value)} placeholder="extra branches or globs, e.g. develop release/*" disabled={running} aria-label="Branches" style={{ minWidth: 300 }} />
            <label className="inline check"><input type="checkbox" checked={partial} onChange={(e) => setPartial(e.target.checked)} disabled={running} /> Index what the token can see, even if that is not everything</label>
            <button className="btn primary" type="submit" disabled={running}>Index all repositories</button>
          </form>
          <p className="dim small">Each project's default branch is always included. Leave partial indexing off unless a partial index is the intent: without it, Argus refuses to build an index whose gaps nothing downstream could detect.</p>
        </div>
      )}
      {s && s.repos.length > 0 && (
        <div className="card">
          <h2>Repositories ({s.index.repos ?? s.repos.length} refs, {s.index.symbols?.toLocaleString() ?? "?"} symbols)</h2>
          <table data-testid="repo-table">
            <thead><tr><th>Repository</th><th>Branch</th><th>Last indexed</th><th>Result</th></tr></thead>
            <tbody>
              {s.repos.map((r) => (
                <tr key={`${r.repo}@${r.branch}`}>
                  <td>{r.repo}</td>
                  <td>{r.branch}{r.branch === r.default_branch && <span className="dim"> (default)</span>}</td>
                  <td>{relTime(r.last_run_at)}</td>
                  <td>{r.timed_out ? <span className="bad">timed out</span> : r.symbols_failed ? `${r.symbols_failed} failed` : "ok"}</td>
                </tr>
              ))}
            </tbody>
          </table>
        </div>
      )}
    </div>
  );
}
