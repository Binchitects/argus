import { useEffect, useMemo, useState } from "react";
import { api, inTime, relTime, type BatchResult, type LogLine, type Repo, type RepoProgress, type RepoState, type ReposView } from "../../api";
import { useAsync } from "../../components/useAsync";

const STATES: Record<RepoState, { label: string; tone: string; order: number }> = {
  indexing: { label: "Indexing", tone: "accent", order: 0 },
  queued: { label: "Queued", tone: "", order: 1 },
  failed: { label: "Failed", tone: "bad", order: 2 },
  stale: { label: "Out of date", tone: "warn", order: 3 },
  never: { label: "Not indexed yet", tone: "", order: 4 },
  indexed: { label: "Indexed", tone: "ok", order: 5 },
  off: { label: "Left out", tone: "", order: 6 },
};

const DAYS = ["Monday", "Tuesday", "Wednesday", "Thursday", "Friday", "Saturday", "Sunday"];

/** A schedule as Argus stores it: "" (the one for all), pass, off, hours:N, daily:HH:MM, weekly:D:HH:MM. */
interface Schedule {
  kind: "default" | "pass" | "off" | "hours" | "daily" | "weekly";
  hours: number;
  time: string;
  day: number;
}

function scheduleOf(spec: string): Schedule {
  const [kind, ...rest] = spec.split(":");
  const at = (h?: string, m?: string) => `${String(Number(h ?? 0)).padStart(2, "0")}:${String(Number(m ?? 0)).padStart(2, "0")}`;
  if (kind === "hours") return { kind, hours: Number(rest[0]) || 6, time: "02:00", day: 1 };
  if (kind === "daily") return { kind, hours: 6, time: at(rest[0], rest[1]), day: 1 };
  if (kind === "weekly") return { kind, hours: 6, time: at(rest[1], rest[2]), day: Number(rest[0]) || 1 };
  return { kind: kind === "" ? "default" : kind === "off" ? "off" : "pass", hours: 6, time: "02:00", day: 1 };
}

function specOf(s: Schedule): string {
  if (s.kind === "default") return "";
  if (s.kind === "hours") return `hours:${s.hours}`;
  if (s.kind === "daily") return `daily:${s.time}`;
  if (s.kind === "weekly") return `weekly:${s.day}:${s.time}`;
  return s.kind;
}

function ScheduleFields({ value, onChange, allowDefault, label }: { value: Schedule; onChange: (s: Schedule) => void; allowDefault: boolean; label: string }) {
  return (
    <span className="row" style={{ margin: 0 }}>
      <select aria-label={label} value={value.kind} onChange={(e) => onChange({ ...value, kind: e.target.value as Schedule["kind"] })}>
        {allowDefault && <option value="default">Same as all</option>}
        <option value="pass">With each scheduled pass</option>
        <option value="hours">Every few hours</option>
        <option value="daily">Every day</option>
        <option value="weekly">Once a week</option>
        <option value="off">Off: only pushes and when asked</option>
      </select>
      {value.kind === "hours" && (
        <input type="number" min={1} max={168} aria-label="Every (hours)" value={value.hours} onChange={(e) => onChange({ ...value, hours: Number(e.target.value) })} style={{ width: 80 }} />
      )}
      {value.kind === "weekly" && (
        <select aria-label="On" value={value.day} onChange={(e) => onChange({ ...value, day: Number(e.target.value) })}>
          {DAYS.map((d, i) => <option key={d} value={i + 1}>{d}</option>)}
        </select>
      )}
      {(value.kind === "daily" || value.kind === "weekly") && (
        <input type="time" aria-label="At" value={value.time} onChange={(e) => onChange({ ...value, time: e.target.value })} />
      )}
    </span>
  );
}

function progressWords(p: RepoProgress): string {
  const on = p.branch ? ` on ${p.branch}` : "";
  switch (p.state) {
    case "queued": return "Waiting for its turn";
    case "fetching": return "Fetching from GitLab…";
    case "files": return p.total ? `Reading files${on}: ${p.done ?? 0} of ${p.total}` : `Reading files${on}…`;
    case "symbols": return `Reading symbols${on}…`;
    case "embedding": return p.total ? `Embedding for meaning search: ${p.done ?? 0} of ${p.total}` : "Embedding for meaning search…";
    default: return p.message ?? (p.state === "done" ? "Done" : "Failed");
  }
}

const working = (p: RepoProgress | null) => !!p && ["fetching", "files", "symbols", "embedding"].includes(p.state);
const noun = (n: number) => (n === 1 ? "1 repository" : `${n} repositories`);

/** A repository's log: its last runs, newest first, a sentence a line. */
function LogDialog({ repo, onClose }: { repo: Repo; onClose: () => void }) {
  const log = useAsync(() => api.get<{ lines: LogLine[]; progress: RepoProgress | null }>(`/admin/repos/${repo.gitlab_id}/log?runs=5`), [repo.gitlab_id],
    (v) => (v?.progress && !["done", "failed"].includes(v.progress.state) ? 2000 : null));
  useEffect(() => {
    const onKey = (e: KeyboardEvent) => e.key === "Escape" && onClose();
    window.addEventListener("keydown", onKey);
    return () => window.removeEventListener("keydown", onKey);
  }, [onClose]);
  const runs = new Map<number, LogLine[]>();
  for (const l of log.value?.lines ?? []) runs.set(l.run, [...(runs.get(l.run) ?? []), l]);
  return (
    <div className="overlay" onClick={onClose}>
      <div className="card modal" role="dialog" aria-modal="true" aria-labelledby="log-title" onClick={(e) => e.stopPropagation()}>
        <div className="row between">
          <h2 id="log-title">Log of {repo.repo}</h2>
          <button className="btn small" onClick={onClose}>Close</button>
        </div>
        {log.error && <div className="msg bad">{log.error}</div>}
        {log.value?.progress && working(log.value.progress) && <div className="msg" role="status">{progressWords(log.value.progress)}</div>}
        {log.value && runs.size === 0 && <p className="dim">Nothing yet: the first run writes here.</p>}
        <div className="log-runs" data-testid="repo-log">
          {[...runs.entries()].sort(([a], [b]) => b - a).map(([run, lines]) => (
            <section key={run} aria-label={`Run of ${new Date(run).toLocaleString()}`}>
              <div className="dim small">{new Date(run).toLocaleString()}</div>
              <ol>
                {lines.map((l, i) => (
                  <li key={i} className={l.level === "info" ? "" : l.level === "warning" ? "warn" : "bad"}>
                    <span className="mono dim">{new Date(l.at * 1000).toLocaleTimeString()}</span>{" "}
                    {l.level !== "info" && <strong>{l.level === "warning" ? "Warning: " : "Error: "}</strong>}
                    {l.text}
                  </li>
                ))}
              </ol>
            </section>
          ))}
        </div>
      </div>
    </div>
  );
}

export default function Repositories() {
  const data = useAsync(() => api.get<ReposView>("/admin/repos"), [], (v) => (v?.running || v?.pending.length ? 2500 : 30_000));
  const [flash, setFlash] = useState<{ ok: boolean; text: string } | null>(null);
  const [outcome, setOutcome] = useState<BatchResult | null>(null);
  const [query, setQuery] = useState("");
  const [state, setState] = useState<"all" | RepoState | "unlisted">("all");
  const [group, setGroup] = useState("all");
  const [language, setLanguage] = useState("all");
  const [enabled, setEnabled] = useState<"all" | "on" | "off">("all");
  const [sort, setSort] = useState<{ by: "repo" | "state" | "next"; desc: boolean }>({ by: "repo", desc: false });
  const [selected, setSelected] = useState<Set<number>>(new Set());
  const [batchSchedule, setBatchSchedule] = useState<Schedule>(scheduleOf("daily:02:00"));
  const [batchBranches, setBatchBranches] = useState("");
  const [defaultSchedule, setDefaultSchedule] = useState<Schedule | null>(null);
  const [zone, setZone] = useState<string | null>(null);
  const [logOf, setLogOf] = useState<Repo | null>(null);
  const v = data.value;

  const groups = useMemo(() => [...new Set((v?.repos ?? []).map((r) => r.group).filter(Boolean))].sort(), [v]);
  const languages = useMemo(() => [...new Set((v?.repos ?? []).map((r) => r.language).filter((l): l is string => !!l))].sort(), [v]);
  const counts = useMemo(() => {
    const c: Record<string, number> = {};
    for (const r of v?.repos ?? []) {
      c[r.state] = (c[r.state] ?? 0) + 1;
      if (!r.listed) c.unlisted = (c.unlisted ?? 0) + 1;
    }
    return c;
  }, [v]);
  const shown = useMemo(() => {
    const q = query.trim().toLowerCase();
    const rows = (v?.repos ?? []).filter((r) =>
      (!q || r.repo.toLowerCase().includes(q)) &&
      (state === "all" || (state === "unlisted" ? !r.listed : r.state === state)) &&
      (group === "all" || r.group === group || r.group.startsWith(`${group}/`)) &&
      (language === "all" || r.language === language) &&
      (enabled === "all" || r.included === (enabled === "on")));
    const key = (r: Repo) => (sort.by === "state" ? STATES[r.state].order : sort.by === "next" ? (r.next_run_at ?? Number.MAX_SAFE_INTEGER) : 0);
    rows.sort((a, b) => (sort.by === "repo" ? a.repo.localeCompare(b.repo) : key(a) - key(b) || a.repo.localeCompare(b.repo)) * (sort.desc ? -1 : 1));
    return rows;
  }, [v, query, state, group, language, enabled, sort]);
  const chosen = (v?.repos ?? []).filter((r) => selected.has(r.gitlab_id));

  async function batch(action: string, extra: object, ask: string) {
    if (!window.confirm(ask)) return;
    try {
      setOutcome(await api.post<BatchResult>("/admin/repos/batch", { ids: chosen.map((r) => r.gitlab_id), action, ...extra }));
      setFlash(null);
    } catch (err) {
      setFlash({ ok: false, text: err instanceof Error ? err.message : String(err) });
    }
    await data.reload();
  }

  /** Do something, then say so: what it says itself, or `done`. */
  async function act(run: () => Promise<unknown>, done: string) {
    try {
      const said = await run();
      setFlash({ ok: true, text: typeof said === "string" ? said : done });
    } catch (err) {
      setFlash({ ok: false, text: err instanceof Error ? err.message : String(err) });
    }
    await data.reload();
  }

  const header = (by: "repo" | "state" | "next", label: string) => (
    <th aria-sort={sort.by === by ? (sort.desc ? "descending" : "ascending") : undefined}>
      <button className="sort" onClick={() => setSort({ by, desc: sort.by === by ? !sort.desc : false })}>
        {label} {sort.by === by ? (sort.desc ? "↓" : "↑") : ""}
      </button>
    </th>
  );

  return (
    <div className="page wide">
      <h1>Repositories</h1>
      <p className="lede">What GitLab lists, and which are indexed: find them by name, path or group, narrow them down, and change several at once.</p>
      {data.error && <div className="msg bad">Argus did not answer: {data.error}</div>}
      {flash && <div className={`msg ${flash.ok ? "ok" : "bad"}`} role="status">{flash.text}</div>}
      {outcome && (
        <div className="card" data-testid="batch-outcome">
          <div className="row between">
            <h2>{outcome.results.filter((r) => r.ok).length} of {noun(outcome.results.length)} done</h2>
            <button className="btn small" onClick={() => setOutcome(null)}>Dismiss</button>
          </div>
          <ul className="outcome">
            {[...outcome.results].sort((a, b) => Number(a.ok) - Number(b.ok)).map((r) => (
              <li key={r.gitlab_id} className={r.ok ? "" : "bad"}>
                <strong>{r.ok ? "✓" : "✗"} {r.repo ?? `#${r.gitlab_id}`}</strong> <span className="dim">{r.message}</span>
              </li>
            ))}
          </ul>
        </div>
      )}
      {v && (
        <div className="card">
          <div className="row">
            <span className="dim">Schedule for all:</span>
            {defaultSchedule ? (
              <>
                <ScheduleFields value={defaultSchedule} onChange={setDefaultSchedule} allowDefault={false} label="Schedule for all" />
                <input aria-label="Time zone" value={zone ?? v.schedule.time_zone} onChange={(e) => setZone(e.target.value)} placeholder="Europe/Berlin" style={{ width: 160 }} />
                <button className="btn small primary" onClick={() => void act(async () => {
                  await api.put("/admin/repos/settings", { schedule: specOf(defaultSchedule), schedule_tz: zone ?? v.schedule.time_zone });
                  setDefaultSchedule(null);
                }, "Schedule for all saved.")}>Save</button>
                <button className="btn small ghost" onClick={() => setDefaultSchedule(null)}>Cancel</button>
              </>
            ) : (
              <>
                <strong>{v.schedule.words}</strong>
                <span className="dim small">
                  {v.schedule.default === "pass" && (v.schedule.pass_interval > 0 ? `(a pass every ${Math.round(v.schedule.pass_interval / 60)} min)` : "(no passes: ARGUS_INDEX_INTERVAL is off)")} · times of day in {v.schedule.time_zone}
                </span>
                <button className="btn small" onClick={() => setDefaultSchedule(scheduleOf(v.schedule.default))}>Change</button>
              </>
            )}
            <span className="right row" style={{ margin: 0 }}>
              <label className="inline">New repositories
                <select aria-label="New repositories" value={v.new_repos} onChange={(e) => void act(() => api.put("/admin/repos/settings", { new_repos: e.target.value }), "Saved.")}>
                  <option value="include">Index them</option>
                  <option value="exclude">Leave them out</option>
                </select>
              </label>
              <button className="btn small" onClick={() => void act(async () => {
                const r = await api.post<ReposView>("/admin/repos/discover");
                const listed = `GitLab lists ${noun(r.repos.filter((x) => x.listed).length)}`;
                return r.found?.moved ? `${listed}; ${noun(r.found.moved)} found again under a new id, with their index.` : `${listed}; ${r.found?.new ?? 0} new.`;
              }, "Listed again from GitLab.")}>Refresh from GitLab</button>
            </span>
          </div>
          <div className="row chips" role="group" aria-label="Repositories by state">
            {(Object.keys(STATES) as RepoState[]).filter((s) => counts[s]).map((s) => (
              <button key={s} className={`btn small ${state === s ? "" : "ghost"}`} aria-pressed={state === s} onClick={() => setState(state === s ? "all" : s)}>
                {STATES[s].label} <span className="dim">{counts[s]}</span>
              </button>
            ))}
            {counts.unlisted ? (
              <button className={`btn small ${state === "unlisted" ? "" : "ghost"}`} aria-pressed={state === "unlisted"} onClick={() => setState(state === "unlisted" ? "all" : "unlisted")}>
                Not in GitLab <span className="dim">{counts.unlisted}</span>
              </button>
            ) : null}
          </div>
          <div className="row">
            <input type="search" className="search" aria-label="Find a repository" placeholder="Name, path or group" value={query} onChange={(e) => setQuery(e.target.value)} />
            <select aria-label="Status" value={state} onChange={(e) => setState(e.target.value as typeof state)}>
              <option value="all">Every state</option>
              {(Object.keys(STATES) as RepoState[]).map((s) => <option key={s} value={s}>{STATES[s].label}</option>)}
              <option value="unlisted">Not in GitLab</option>
            </select>
            <select aria-label="Group" value={group} onChange={(e) => setGroup(e.target.value)}>
              <option value="all">Every group</option>
              {groups.map((g) => <option key={g} value={g}>{g}</option>)}
            </select>
            <select aria-label="Language" value={language} onChange={(e) => setLanguage(e.target.value)}>
              <option value="all">Every language</option>
              {languages.map((l) => <option key={l} value={l}>{l}</option>)}
            </select>
            <select aria-label="Indexed or not" value={enabled} onChange={(e) => setEnabled(e.target.value as typeof enabled)}>
              <option value="all">Indexed or not</option>
              <option value="on">Indexed</option>
              <option value="off">Left out</option>
            </select>
          </div>
          {chosen.length > 0 && (
            <div className="bulk" role="region" aria-label="Bulk actions">
              <strong>{chosen.length} selected</strong>
              {chosen.length < shown.length && <button className="btn small ghost" onClick={() => setSelected(new Set(shown.map((r) => r.gitlab_id)))}>Select all {shown.length}</button>}
              <button className="btn small" onClick={() => void batch("reindex", {}, `Update ${noun(chosen.length)} now, from their latest commits?`)}>Update now</button>
              <button className="btn small" onClick={() => void batch("include", {}, `Index ${noun(chosen.length)}?`)}>Index them</button>
              <button className="btn small" onClick={() => void batch("exclude", {}, `Leave ${noun(chosen.length)} out of the index? Their files and symbols are removed.`)}>Leave them out</button>
              <span className="row" style={{ margin: 0 }}>
                <ScheduleFields value={batchSchedule} onChange={setBatchSchedule} allowDefault label="Schedule for the selected" />
                <button className="btn small" onClick={() => void batch("schedule", { schedule: specOf(batchSchedule) }, `Set the schedule of ${noun(chosen.length)}?`)}>Set schedule</button>
              </span>
              <span className="row" style={{ margin: 0 }}>
                <input aria-label="Branches to add" placeholder="release/*" value={batchBranches} onChange={(e) => setBatchBranches(e.target.value)} style={{ width: 130 }} />
                <button className="btn small" disabled={!batchBranches.trim()} onClick={() => void batch("add_branches", { branches: batchBranches.split(/[\s,]+/).filter(Boolean) }, `Add these branches to ${noun(chosen.length)}?`)}>Add branches</button>
              </span>
              <button className="btn small danger" onClick={() => void batch("remove", {}, `Remove ${noun(chosen.length)} from the index? One GitLab no longer lists is forgotten.`)}>Remove</button>
              <button className="btn small ghost right" onClick={() => setSelected(new Set())}>Clear</button>
            </div>
          )}
          <div className="scroll">
            <table data-testid="repos-table">
              <thead>
                <tr>
                  <th><input type="checkbox" aria-label="Select all shown" checked={shown.length > 0 && shown.every((r) => selected.has(r.gitlab_id))}
                    onChange={(e) => setSelected(e.target.checked ? new Set(shown.map((r) => r.gitlab_id)) : new Set())} /></th>
                  {header("repo", "Repository")}
                  <th>Indexed</th>
                  {header("state", "Status")}
                  {header("next", "Schedule")}
                  <th>Index</th>
                  <th />
                </tr>
              </thead>
              <tbody>
                {shown.map((r) => (
                  <tr key={r.gitlab_id}>
                    <td><input type="checkbox" aria-label={`Select ${r.repo}`} checked={selected.has(r.gitlab_id)} onChange={(e) => {
                      const next = new Set(selected);
                      if (e.target.checked) next.add(r.gitlab_id); else next.delete(r.gitlab_id);
                      setSelected(next);
                    }} /></td>
                    <td>
                      <strong>{r.name}</strong>
                      <div className="dim small">{r.group}{r.group && r.language ? " · " : ""}{r.language}</div>
                      <div className="small mono dim">{[r.default_branch, ...r.branches].join(" ")}</div>
                    </td>
                    <td>
                      <button className="btn small" aria-label={`${r.included ? "Leave out" : "Index"} ${r.repo}`} onClick={() => {
                        if (r.included && !window.confirm(`Leave ${r.repo} out of the index? Its files and symbols are removed.`)) return;
                        void act(() => api.patch(`/admin/repos/${r.gitlab_id}`, { included: !r.included }), `${r.repo} ${r.included ? "left out" : "chosen for the index"}.`);
                      }}>{r.included ? "On" : "Off"}</button>
                    </td>
                    <td data-testid="repo-status">
                      <span className={`badge ${STATES[r.state].tone}`}>{STATES[r.state].label}</span>
                      {!r.listed && <span className="badge warn" title="GitLab did not list it the last time it was asked">Not in GitLab</span>}
                      {r.progress && working(r.progress) && (
                        <div className="small">
                          {progressWords(r.progress)}
                          {r.progress.total ? <div className="meter"><span style={{ width: `${Math.round((100 * (r.progress.done ?? 0)) / r.progress.total)}%` }} /></div> : null}
                        </div>
                      )}
                      {r.progress && (r.progress.state === "done" || r.progress.state === "failed") && <div className={`small ${r.progress.state === "failed" ? "bad" : "dim"}`}>{r.progress.message}</div>}
                      {!r.progress && r.problem && <div className="small bad">{r.problem}</div>}
                    </td>
                    <td className="small">
                      {r.schedule_words}{!r.schedule && <span className="dim"> (for all)</span>}
                      <div className="dim">{r.next_run_at ? `next ${inTime(r.next_run_at)} · ` : ""}last {relTime(r.last_run_at)}</div>
                    </td>
                    <td className="small">
                      {r.indexed.length === 0 ? <span className="dim">{r.included ? "Not indexed yet" : "Not indexed"}</span> : r.indexed.map((b) => (
                        <div key={b.branch}><span className="mono">{b.branch}</span> <span className="mono dim">{(b.sha ?? "").slice(0, 8)}</span> <span className="dim">{b.files} files, {b.symbols} symbols</span></div>
                      ))}
                    </td>
                    <td className="right nowrap">
                      <button className="btn small" disabled={!r.included || !r.listed} aria-label={`Update ${r.repo}`}
                        onClick={() => void act(() => api.post("/admin/index", { repo: r.repo }), `Updating ${r.repo}.`)}>Update</button>{" "}
                      <button className="btn small ghost" aria-label={`Log of ${r.repo}`} onClick={() => setLogOf(r)}>Log</button>
                    </td>
                  </tr>
                ))}
                {v.repos.length > 0 && shown.length === 0 && <tr><td colSpan={7} className="dim">No repositories match these filters.</td></tr>}
                {v.repos.length === 0 && <tr><td colSpan={7} className="dim">No repositories yet: Refresh from GitLab lists them, and so does every index pass.</td></tr>}
              </tbody>
            </table>
          </div>
          <p className="dim small">{shown.length} of {noun(v.repos.length)}.</p>
        </div>
      )}
      {logOf && <LogDialog repo={logOf} onClose={() => setLogOf(null)} />}
    </div>
  );
}
