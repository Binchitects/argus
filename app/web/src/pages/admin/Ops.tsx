import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { useState } from 'react'
import { Link } from 'react-router'
import { api } from '../../api'
import { ErrorText } from '../../components/ErrorText'
import { ago, duration, formatValue } from '../../format'
import { Tile } from '../Usage'
import { host, indexExit } from './ops-shared'

interface Probe {
  name: string
  purpose: string
  ok: boolean
  detail: string
}

interface IndexSummary {
  repos?: number
  stale?: number
  errored?: number
  never_run?: number
  files?: number
  symbols?: number
  stale_names?: string[]
  returncode?: number | null
  finished?: number | null
  state?: string
  error?: string
}

interface Overview {
  people: number
  admins: number
  spend: number
  overCredit: string[]
  warning: string | null
  services: Probe[]
  index: { configured: boolean; summary: IndexSummary | null; error: string | null }
  model: string | null
}

export function Overview() {
  const o = useQuery({ queryKey: ['admin', 'overview'], queryFn: () => api<Overview>('/api/admin/overview'), refetchInterval: 30_000 })
  if (o.isPending) return <p aria-busy="true">Loading…</p>
  if (o.error) return <ErrorText error={o.error} />
  const d = o.data
  const up = d.services.filter((s) => s.ok).length
  const idx = d.index.summary
  return (
    <div className="stack">
      <div className="tiles">
        <Tile label="Services up" value={`${up}/${d.services.length}`} />
        <Tile label="People" value={String(d.people)} hint={`${d.admins} admin${d.admins === 1 ? '' : 's'}`} />
        <Tile label="Spend" value={formatValue(d.spend, 'currencyUSD')} hint="every key and chat" />
        <Tile label="Over credit" value={String(d.overCredit.length)} hint={d.overCredit.length ? 'ask before they notice' : 'nobody'} />
        {d.index.configured && (
          <Tile
            label="Code index"
            value={d.index.error ? 'unreachable' : idx?.repos ? `${idx.repos - (idx.stale ?? 0)}/${idx.repos}` : 'empty'}
            hint={d.index.error ? 'Argus did not answer' : idx?.stale ? `${idx.stale} out of date` : idx?.repos ? 'repositories current' : 'nothing indexed yet'}
          />
        )}
      </div>
      {d.warning && <p className="warn-text">{d.warning}</p>}
      {d.overCredit.length > 0 && (
        <div className="notice warn" role="status">
          <strong>{d.overCredit.length} at or past their credit:</strong> {d.overCredit.slice(0, 8).join(', ')}
          {d.overCredit.length > 8 ? ` and ${d.overCredit.length - 8} more` : ''}. <Link to="/admin/people">People</Link>
        </div>
      )}
      <IndexAlert configured={d.index.configured} idx={idx} error={d.index.error} />
      <section className="card">
        <h2>Services</h2>
        <Services services={d.services} />
      </section>
    </div>
  )
}

/** Why the index needs attention, with the cause when the last run's exit code gives one. */
function IndexAlert({ configured, idx, error }: { configured: boolean; idx: IndexSummary | null; error: string | null }) {
  if (!configured) return null
  if (error)
    return (
      <div className="notice warn" role="status">
        <strong>The code index is unreachable.</strong> Every indexed answer in chat is failing right now. {error}
      </div>
    )
  if (!idx?.repos)
    return (
      <div className="notice warn" role="status">
        <strong>No repository is indexed.</strong> Usually an expired GitLab token. See <Link to="/admin/indexing">Indexing</Link>.
      </div>
    )
  if (!idx.stale) return null
  const cause = idx.returncode !== null && idx.returncode !== undefined && idx.returncode !== 0 ? indexExit[String(idx.returncode)] : null
  return (
    <div className="notice warn" role="status">
      <strong>{idx.stale} repositor{idx.stale === 1 ? 'y has' : 'ies have'} a stale index.</strong>{' '}
      {idx.never_run ? `${idx.never_run} have never been indexed. ` : ''}
      {cause ? `The last pass ${cause}. ` : ''}Answers about them come from old data. {(idx.stale_names ?? []).slice(0, 5).join(', ')}{' '}
      <Link to="/admin/indexing">Index now</Link>
    </div>
  )
}

export function Services({ services }: { services: Probe[] }) {
  return (
    <ul className="plain services">
      {services.map((s) => (
        <li key={s.name}>
          <span className={`dot ${s.ok ? 'ok' : 'bad'}`} aria-hidden="true" />
          <strong>{s.name}</strong> <span className="muted">{s.purpose}</span>{' '}
          <span className={s.ok ? 'muted' : 'warn-text'}>
            {s.ok ? 'up' : 'down'} · {s.detail}
          </span>
        </li>
      ))}
    </ul>
  )
}

export function Monitoring() {
  const s = useQuery({ queryKey: ['admin', 'services'], queryFn: () => api<Probe[]>('/api/admin/services'), refetchInterval: 15_000 })
  const links: [string, string, string][] = [
    ['Grafana', host('grafana'), 'the dashboards not yet in the app: engine, GPU, host, logs, Argus'],
    ['Prometheus', host('metrics'), 'raw metrics and alert rules'],
    ['Alertmanager', host('alerts'), 'firing alerts'],
    ['Argus MCP', `${host('argus')}/mcp`, 'the code index, for agents'],
  ]
  return (
    <div className="stack">
      <section className="card">
        <h2>Services</h2>
        <ErrorText error={s.error} />
        {s.data && <Services services={s.data} />}
      </section>
      <section className="card">
        <h2>Elsewhere</h2>
        <ul className="plain">
          {links.map(([name, url, what]) => (
            <li key={name}>
              <a href={url} target="_blank" rel="noopener noreferrer">
                {name} ↗
              </a>{' '}
              <span className="muted">{what}</span>
            </li>
          ))}
        </ul>
        <p className="muted">They use the same sign-in as this app, so an open tab is usually signed in already.</p>
      </section>
    </div>
  )
}

interface Sample {
  file: string
  title: string
  hardware: string | null
  download: string | null
  measured: string | null
  status: string | null
  model: string
  block: string
}

interface ModelInfo {
  running: { name: string | null; file: string | null; context: string | null; maxOutput: string | null; mtpDraftMax: string | null; gpuPowerLimitW: string | null; cpuPowerLimitW: string | null; thinkingPresets: string | null }
  prices: { input: string | null; cachedInput: string | null; output: string | null }
  samples: Sample[]
}

/** What is running, and the steps to switch. It shows the steps rather than doing them: that would need the Docker socket. */
export function Model() {
  const m = useQuery({ queryKey: ['admin', 'model'], queryFn: () => api<ModelInfo>('/api/admin/model') })
  if (m.isPending) return <p aria-busy="true">Loading…</p>
  if (m.error) return <ErrorText error={m.error} />
  const r = m.data.running
  const mtp = r.mtpDraftMax && r.mtpDraftMax !== '0' ? `on (${r.mtpDraftMax} draft tokens)` : 'off'
  return (
    <div className="stack">
      <section className="card">
        <h2>Running</h2>
        <dl className="facts">
          <dt>Model</dt>
          <dd>
            <strong>{r.name ?? 'not set'}</strong> <span className="muted">{r.file}</span>
          </dd>
          <dt>Context</dt>
          <dd>{r.context ? `${formatValue(Number(r.context))} tokens` : '?'}</dd>
          <dt>Longest reply</dt>
          <dd>{r.maxOutput ? `${formatValue(Number(r.maxOutput))} tokens` : '?'}</dd>
          <dt>Multi-token prediction</dt>
          <dd>{mtp}</dd>
          <dt>Thinking presets</dt>
          <dd>{r.thinkingPresets || 'none'}</dd>
          <dt>Power limits</dt>
          <dd>
            GPU {r.gpuPowerLimitW ? `${r.gpuPowerLimitW} W` : 'default'}, CPU {r.cpuPowerLimitW ? `${r.cpuPowerLimitW} W` : 'firmware'}
          </dd>
          <dt>Prices per 1M tokens</dt>
          <dd>
            input ${m.data.prices.input ?? '?'} · cached input ${m.data.prices.cachedInput ?? '?'} · output ${m.data.prices.output ?? '?'}
          </dd>
        </dl>
        <p className="muted">Prices and everything else here come from <code>stack/.env</code>; change them there and run <code>docker compose up -d</code>.</p>
      </section>
      <section className="card">
        <h2>Switch the deployment to</h2>
        {m.data.samples.length === 0 && <p className="muted">No env samples mounted.</p>}
        {m.data.samples.map((s) => (
          <details key={s.file} className="sample">
            <summary>
              <strong>{s.title}</strong> {s.model === r.name && <span className="badge">running</span>} <span className="muted">{s.hardware}</span>
            </summary>
            <p className="muted">
              {s.download}
              {s.measured && (
                <>
                  <br />
                  {s.measured}
                </>
              )}
              {s.status && (
                <>
                  <br />
                  {s.status}
                </>
              )}
            </p>
            <ol>
              <li>
                In <code>stack/.env</code>, replace everything from <code># &gt;&gt;&gt; MODEL</code> to <code># &lt;&lt;&lt; MODEL</code> with the block below. Keep your own{' '}
                <code>LLAMACPP_MODEL_DIR</code>.
              </li>
              <li>
                Run <code>docker compose up -d</code> in <code>stack/</code>. A model not on disk yet downloads first: <code>docker logs -f model-init</code>.
              </li>
              <li>
                The model list then shows <strong>{s.model}</strong> and nothing else.
              </li>
            </ol>
            <pre className="code-block">{s.block}</pre>
            <p className="muted">
              Whole file: <code>env-samples/{s.file}</code>
            </p>
          </details>
        ))}
      </section>
    </div>
  )
}

interface SettingsGroup {
  title: string
  rows: { name: string; value: string | null; purpose: string }[]
}

export function Settings() {
  const s = useQuery({ queryKey: ['admin', 'settings'], queryFn: () => api<SettingsGroup[]>('/api/admin/settings') })
  return (
    <div className="stack">
      <ErrorText error={s.error} />
      {s.data?.map((g) => (
        <section key={g.title} className="card">
          <h2>{g.title}</h2>
          <dl className="facts">
            {g.rows.map((r) => (
              <FactRow key={r.name} r={r} />
            ))}
          </dl>
        </section>
      ))}
      <p className="muted">
        Read-only. These come from <code>stack/.env</code> and apply at container start. Secrets are never shown, not even masked: a masked value still leaks its length into every screenshot.
      </p>
    </div>
  )
}

function FactRow({ r }: { r: { name: string; value: string | null; purpose: string } }) {
  return (
    <>
      <dt>
        <code>{r.name}</code>
        <div className="muted">{r.purpose}</div>
      </dt>
      <dd>{r.value ?? '—'}</dd>
    </>
  )
}

// ------------------------------------------------------------------ Argus --

interface IndexStatus {
  configured?: boolean
  job: { state: string; branches: string[]; started: number | null; finished: number | null; returncode: number | null; tail: string[]; trigger: string | null; allow_partial?: boolean; repos_error?: string }
  repos: { repo: string; branch: string; default_branch: string; last_run_at: number | null; timed_out: boolean; symbols_failed: number | null }[]
  index: IndexSummary
  interval: number
  webhook: boolean
  pending: string[]
}

function NotConfigured() {
  return (
    <p className="muted">
      Argus is not set up in this deployment. Add <code>argus</code> to <code>COMPOSE_PROFILES</code> and set <code>ARGUS_ADMIN_TOKEN</code> in <code>stack/.env</code>.
    </p>
  )
}

export function Indexing() {
  const queryClient = useQueryClient()
  const st = useQuery({
    queryKey: ['admin', 'argus', 'status'],
    queryFn: () => api<IndexStatus>('/api/admin/argus/status'),
    refetchInterval: (q) => (q.state.data?.job?.state === 'running' ? 3000 : 30_000),
  })
  const [branches, setBranches] = useState('')
  const [partial, setPartial] = useState(false)
  const start = useMutation({
    mutationFn: () => api('/api/admin/argus/index', { body: { branches: branches.split(/[\s,]+/).filter(Boolean), allowPartial: partial } }),
    onSuccess: () => queryClient.invalidateQueries({ queryKey: ['admin', 'argus', 'status'] }),
  })
  if (st.isPending) return <p aria-busy="true">Loading…</p>
  if (st.error) return <ErrorText error={st.error} />
  if (st.data.configured === false) return <NotConfigured />
  const { job, index: idx, repos } = st.data
  const running = job.state === 'running'
  const trigger = { schedule: 'the schedule', webhook: 'a GitLab push', manual: 'an admin' }[job.trigger ?? ''] ?? job.trigger
  return (
    <div className="stack">
      <div className="tiles">
        <Tile label="Repositories" value={String(idx.repos ?? 0)} hint={idx.stale ? `${idx.stale} out of date` : 'all current'} />
        <Tile label="Failing" value={String(idx.errored ?? 0)} />
        <Tile label="Files" value={formatValue(idx.files ?? 0)} />
        <Tile label="Symbols" value={formatValue(idx.symbols ?? 0)} />
      </div>
      <section className="card">
        <h2>Index now</h2>
        <p className="muted">
          {st.data.interval ? `Argus reindexes by itself every ${duration(st.data.interval)}.` : 'Argus does not reindex by itself (no schedule set).'}{' '}
          {st.data.webhook ? 'GitLab pushes also trigger a run.' : ''}
        </p>
        <form
          className="inline-form"
          onSubmit={(e) => {
            e.preventDefault()
            start.mutate()
          }}
        >
          <label>
            Branches <span className="muted">(besides each default branch; globs allowed)</span>
            <input value={branches} onChange={(e) => setBranches(e.target.value)} placeholder="develop release/*" />
          </label>
          <label className="check">
            <input type="checkbox" checked={partial} onChange={(e) => setPartial(e.target.checked)} /> Allow a partial list of repositories
          </label>
          <button className="button" disabled={running || start.isPending}>
            {running ? 'Running…' : 'Index now'}
          </button>
        </form>
        <ErrorText error={start.error} />
        <p role="status" className="muted">
          {running
            ? `Running since ${ago(job.started)}, started by ${trigger}, for ${job.branches.join(', ') || 'default branches'}.`
            : job.finished
              ? `Last run finished ${ago(job.finished)}: exit ${job.returncode}, ${indexExit[String(job.returncode)] ?? 'unrecognised exit code'}.`
              : 'No run since Argus started.'}
        </p>
        {st.data.pending.length > 0 && <p className="muted">Waiting: {st.data.pending.join(', ')}</p>}
        {job.tail.length > 0 && <pre className="code-block log">{job.tail.join('\n')}</pre>}
        {job.repos_error && <p className="warn-text">{job.repos_error}</p>}
      </section>
      <section className="card">
        <h2>Repositories</h2>
        <div className="table-wrap">
          <table>
            <thead>
              <tr>
                <th>Repository</th>
                <th>Branch</th>
                <th>Last run</th>
                <th>State</th>
              </tr>
            </thead>
            <tbody>
              {repos.map((r) => (
                <tr key={`${r.repo}@${r.branch}`}>
                  <td>{r.repo}</td>
                  <td>
                    {r.branch} {r.branch === r.default_branch && <span className="muted">(default)</span>}
                  </td>
                  <td>{ago(r.last_run_at)}</td>
                  <td>{r.timed_out ? <span className="warn-text">timed out</span> : r.symbols_failed ? `${r.symbols_failed} failed` : 'ok'}</td>
                </tr>
              ))}
            </tbody>
          </table>
        </div>
      </section>
    </div>
  )
}

interface Packs {
  configured?: boolean
  packs: { name: string; version: string; model: string; dim: number; size_bytes: number; license: string | null; compatible: boolean; incompatible_reason: string | null }[]
  job: { state: string; action: string | null; target: string | null; returncode: number | null; tail: string[]; finished: number | null }
  index_url: string | null
  error?: string
}

export function PacksPage() {
  const queryClient = useQueryClient()
  const p = useQuery({
    queryKey: ['admin', 'argus', 'packs'],
    queryFn: () => api<Packs>('/api/admin/argus/packs'),
    refetchInterval: (q) => (q.state.data?.job?.state === 'running' ? 3000 : false),
  })
  const act = useMutation({
    mutationFn: ({ action, body }: { action: string; body: object }) => api(`/api/admin/argus/packs/${action}`, { body }),
    onSuccess: () => queryClient.invalidateQueries({ queryKey: ['admin', 'argus', 'packs'] }),
  })
  const [source, setSource] = useState('')
  const [sha, setSha] = useState('')
  if (p.isPending) return <p aria-busy="true">Loading…</p>
  if (p.error) return <ErrorText error={p.error} />
  if (p.data.configured === false) return <NotConfigured />
  const running = p.data.job.state === 'running'
  return (
    <div className="stack">
      <section className="card">
        <h2>Installed knowledge packs</h2>
        {p.data.error && <p className="warn-text">{p.data.error}</p>}
        {p.data.packs.length === 0 ? (
          <p className="muted">None installed.</p>
        ) : (
          <div className="table-wrap">
            <table>
              <thead>
                <tr>
                  <th>Pack</th>
                  <th>Embeddings</th>
                  <th className="num">Size</th>
                  <th>License</th>
                  <th />
                </tr>
              </thead>
              <tbody>
                {p.data.packs.map((k) => (
                  <tr key={k.name}>
                    <td>
                      {k.name} <span className="muted">{k.version}</span>
                      {!k.compatible && <div className="warn-text">{k.incompatible_reason}</div>}
                    </td>
                    <td>
                      {k.model} <span className="muted">{k.dim}d</span>
                    </td>
                    <td className="num">{formatValue(k.size_bytes, 'bytes')}</td>
                    <td>{k.license ?? '—'}</td>
                    <td className="actions">
                      {p.data.index_url && (
                        <button className="button small secondary" disabled={running} onClick={() => act.mutate({ action: 'update', body: { name: k.name } })}>
                          Update
                        </button>
                      )}
                      <button
                        className="button small secondary"
                        disabled={running}
                        onClick={() => window.confirm(`Remove ${k.name}?`) && act.mutate({ action: 'remove', body: { name: k.name } })}
                      >
                        Remove
                      </button>
                    </td>
                  </tr>
                ))}
              </tbody>
            </table>
          </div>
        )}
      </section>
      <section className="card">
        <h2>Install</h2>
        <form
          className="grid-form"
          onSubmit={(e) => {
            e.preventDefault()
            act.mutate({ action: 'install', body: { source, sha256: sha || null } })
          }}
        >
          <label>
            Pack URL or path
            <input value={source} onChange={(e) => setSource(e.target.value)} required />
          </label>
          <label>
            SHA-256 <span className="muted">(recommended)</span>
            <input value={sha} onChange={(e) => setSha(e.target.value)} pattern="[0-9a-fA-F]{64}" />
          </label>
          <div className="row">
            <button className="button" disabled={running || act.isPending}>
              Install
            </button>
            {p.data.index_url && (
              <button type="button" className="button secondary" disabled={running} onClick={() => act.mutate({ action: 'update', body: {} })}>
                Update all
              </button>
            )}
          </div>
        </form>
        <ErrorText error={act.error} />
        <p role="status" className="muted">
          {running ? `${p.data.job.action} of ${p.data.job.target ?? 'packs'} running…` : p.data.job.finished ? `Last ${p.data.job.action}: exit ${p.data.job.returncode}, ${ago(p.data.job.finished)}.` : ''}
        </p>
        {p.data.job.tail.length > 0 && <pre className="code-block log">{p.data.job.tail.join('\n')}</pre>}
      </section>
    </div>
  )
}

interface Explore {
  configured?: boolean
  repos: { path_with_namespace: string; branch: string; files: number; symbols: number; public_symbols: number }[]
  symbols: { rows: { name: string; kind: string; path: string; line: number; path_with_namespace: string; branch: string; signature?: string }[]; capped: boolean }
  files: { rows: { path: string; lang: string; size: number; path_with_namespace: string; branch: string; symbols: number }[]; capped: boolean }
  error?: string
}

/** What the index holds: for "a tool found nothing; is it absent, named differently, or never indexed?". */
export function ExplorePage() {
  const [q, setQ] = useState('')
  const [submitted, setSubmitted] = useState('')
  const [repo, setRepo] = useState('')
  const e = useQuery({
    queryKey: ['admin', 'argus', 'explore', submitted, repo],
    queryFn: () => api<Explore>(`/api/admin/argus/explore?${new URLSearchParams({ q: submitted, repo, limit: '100' })}`),
  })
  if (e.data?.configured === false) return <NotConfigured />
  return (
    <div className="stack">
      <form
        className="inline-form"
        onSubmit={(ev) => {
          ev.preventDefault()
          setSubmitted(q)
        }}
      >
        <label>
          Symbol or path contains
          <input type="search" value={q} onChange={(ev) => setQ(ev.target.value)} />
        </label>
        <label>
          Repository
          <select value={repo} onChange={(ev) => setRepo(ev.target.value)}>
            <option value="">all</option>
            {[...new Set(e.data?.repos.map((r) => r.path_with_namespace) ?? [])].map((r) => (
              <option key={r}>{r}</option>
            ))}
          </select>
        </label>
        <button className="button">Search</button>
      </form>
      <ErrorText error={e.error} />
      {e.data?.error && <p className="warn-text">{e.data.error}</p>}
      {e.data && submitted && (
        <>
          <section className="card">
            <h2>Symbols</h2>
            {e.data.symbols.rows.length === 0 ? (
              <p className="muted">No symbol matches.</p>
            ) : (
              <div className="table-wrap">
                <table>
                  <thead>
                    <tr>
                      <th>Name</th>
                      <th>Kind</th>
                      <th>Where</th>
                    </tr>
                  </thead>
                  <tbody>
                    {e.data.symbols.rows.map((s, i) => (
                      <tr key={i}>
                        <td>
                          <code>{s.name}</code>
                        </td>
                        <td>{s.kind}</td>
                        <td>
                          {s.path_with_namespace}@{s.branch} <span className="muted">
                            {s.path}:{s.line}
                          </span>
                        </td>
                      </tr>
                    ))}
                  </tbody>
                </table>
              </div>
            )}
            {e.data.symbols.capped && <p className="muted">More matches exist; narrow the search.</p>}
          </section>
          <section className="card">
            <h2>Files</h2>
            {e.data.files.rows.length === 0 ? (
              <p className="muted">No path matches.</p>
            ) : (
              <ul className="plain">
                {e.data.files.rows.map((f, i) => (
                  <li key={i}>
                    <code>{f.path}</code> <span className="muted">
                      {f.path_with_namespace}@{f.branch} · {f.lang} · {f.symbols} symbols
                    </span>
                  </li>
                ))}
              </ul>
            )}
          </section>
        </>
      )}
      {e.data && !submitted && (
        <section className="card">
          <h2>Indexed repositories</h2>
          <div className="table-wrap">
            <table>
              <thead>
                <tr>
                  <th>Repository</th>
                  <th>Branch</th>
                  <th className="num">Files</th>
                  <th className="num">Symbols</th>
                  <th className="num">Public</th>
                </tr>
              </thead>
              <tbody>
                {e.data.repos.map((r) => (
                  <tr key={`${r.path_with_namespace}@${r.branch}`}>
                    <td>{r.path_with_namespace}</td>
                    <td>{r.branch}</td>
                    <td className="num">{formatValue(r.files)}</td>
                    <td className="num">{formatValue(r.symbols)}</td>
                    <td className="num">{formatValue(r.public_symbols)}</td>
                  </tr>
                ))}
              </tbody>
            </table>
          </div>
        </section>
      )}
    </div>
  )
}
