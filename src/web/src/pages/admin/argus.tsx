import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { Database, FileCode2, Hash, Package, Play, RefreshCw, Search, Trash2, TriangleAlert } from 'lucide-react'
import { useState } from 'react'
import { CodeBlock } from '@/components/app/code-block'
import { PageHeader } from '@/components/app/page-header'
import { PageSkeleton, QueryError } from '@/components/app/query-state'
import { Stat, StatGrid } from '@/components/app/stat'
import { Alert } from '@/components/ui/alert'
import { Badge } from '@/components/ui/badge'
import { Button } from '@/components/ui/button'
import { Card, CardContent, CardDescription, CardHeader, CardTitle } from '@/components/ui/card'
import { Checkbox } from '@/components/ui/checkbox'
import { useConfirm } from '@/components/ui/confirm'
import { DataTable, SortHeader, type ColumnDef } from '@/components/ui/data-table'
import { EmptyState } from '@/components/ui/empty-state'
import { Field } from '@/components/ui/field'
import { Input } from '@/components/ui/input'
import { Label } from '@/components/ui/label'
import { Select, SelectContent, SelectItem, SelectTrigger, SelectValue } from '@/components/ui/select'
import { toast } from '@/components/ui/toaster'
import { api, errorMessage } from '@/lib/api'
import { agoSeconds, duration, formatValue } from '@/lib/format'
import { indexExit, type IndexSummary } from './ops-api'

function NotConfigured() {
  return (
    <EmptyState icon={Database} title="Argus is not set up in this deployment">
      Add <code className="font-mono">argus</code> to the parts that run (Settings → Deployment) and set <code className="font-mono">ARGUS_ADMIN_TOKEN</code> in <code className="font-mono">deploy/.env</code>.
    </EmptyState>
  )
}

// ------------------------------------------------------------------ indexing --

interface IndexStatus {
  configured?: boolean
  job: { state: string; branches: string[]; started: number | null; finished: number | null; returncode: number | null; tail: string[]; trigger: string | null; allow_partial?: boolean; repos_error?: string }
  repos: { repo: string; branch: string; default_branch: string; last_run_at: number | null; timed_out: boolean; symbols_failed: number | null }[]
  index: IndexSummary
  interval: number
  webhook: boolean
  pending: string[]
}

type RepoRow = IndexStatus['repos'][number]

const repoColumns: ColumnDef<RepoRow>[] = [
  { accessorKey: 'repo', header: ({ column }) => <SortHeader column={column} title="Repository" />, cell: ({ getValue }) => <span className="font-medium">{getValue<string>()}</span> },
  {
    accessorKey: 'branch',
    header: 'Branch',
    cell: ({ row: { original: r } }) => (
      <span className="inline-flex items-center gap-1.5">
        <code className="font-mono text-xs">{r.branch}</code>
        {r.branch === r.default_branch && <Badge variant="secondary">default</Badge>}
      </span>
    ),
  },
  { id: 'last', accessorFn: (r) => r.last_run_at ?? 0, header: ({ column }) => <SortHeader column={column} title="Last run" />, cell: ({ row: { original: r } }) => <span className="text-muted-foreground">{agoSeconds(r.last_run_at)}</span> },
  {
    id: 'state',
    header: 'State',
    accessorFn: (r) => (r.timed_out ? 'timed out' : r.symbols_failed ? 'failed' : 'ok'),
    cell: ({ row: { original: r } }) =>
      r.timed_out ? <Badge variant="destructive">Timed out</Badge> : r.symbols_failed ? <Badge variant="warning">{r.symbols_failed} failed</Badge> : <Badge variant="success">OK</Badge>,
  },
]

export function IndexingPage() {
  const queryClient = useQueryClient()
  const st = useQuery({
    queryKey: ['admin', 'argus', 'status'],
    queryFn: ({ signal }) => api<IndexStatus>('/api/admin/argus/status', { signal }),
    refetchInterval: (q) => (q.state.data?.job?.state === 'running' ? 3000 : 30_000),
  })
  const [branches, setBranches] = useState('')
  const [partial, setPartial] = useState(false)
  const start = useMutation({
    mutationFn: () => api('/api/admin/argus/index', { body: { branches: branches.split(/[\s,]+/).filter(Boolean), allowPartial: partial } }),
    onSuccess: () => {
      toast.success('Indexing started')
      void queryClient.invalidateQueries({ queryKey: ['admin', 'argus', 'status'] })
    },
  })
  if (st.isPending) return <PageSkeleton />
  if (st.error) return <QueryError error={st.error} retry={() => st.refetch()} />
  if (st.data.configured === false)
    return (
      <>
        <PageHeader title="Indexing" />
        <NotConfigured />
      </>
    )
  const { job, index: idx, repos } = st.data
  const running = job.state === 'running'
  const trigger = { schedule: 'the schedule', webhook: 'a GitLab push', manual: 'an admin' }[job.trigger ?? ''] ?? job.trigger
  return (
    <>
      <PageHeader title="Indexing" description="Argus's index of your GitLab: what it holds, how current it is, and runs on demand." />
      <div className="grid gap-6">
        <StatGrid className="xl:grid-cols-4">
          <Stat icon={Database} label="Repositories" value={idx.repos ?? 0} hint={idx.stale ? `${idx.stale} out of date` : 'all current'} tone={idx.stale ? 'warning' : undefined} />
          <Stat icon={TriangleAlert} label="Failing" value={idx.errored ?? 0} tone={idx.errored ? 'destructive' : undefined} />
          <Stat icon={FileCode2} label="Files" value={formatValue(idx.files ?? 0)} />
          <Stat icon={Hash} label="Symbols" value={formatValue(idx.symbols ?? 0)} />
        </StatGrid>
        <Card>
          <CardHeader>
            <CardTitle>Index now</CardTitle>
            <CardDescription>
              {st.data.interval ? `Argus reindexes by itself every ${duration(st.data.interval)}.` : 'Argus does not reindex by itself (no schedule set).'} {st.data.webhook ? 'GitLab pushes also start a run.' : ''}
            </CardDescription>
          </CardHeader>
          <CardContent className="grid gap-4">
            <form
              className="grid gap-4"
              onSubmit={(e) => {
                e.preventDefault()
                start.mutate()
              }}
            >
              <Field label="Extra branches" hint="Besides each repository's default branch. Space- or comma-separated; globs work: develop release/*">
                <Input value={branches} onChange={(e) => setBranches(e.target.value)} placeholder="develop release/*" className="font-mono" />
              </Field>
              <Label className="font-normal">
                <Checkbox checked={partial} onCheckedChange={(v) => setPartial(!!v)} /> Go on when the token cannot list every repository
              </Label>
              <div>
                <Button type="submit" loading={running || start.isPending} disabled={running}>
                  <Play /> {running ? 'Running…' : 'Index now'}
                </Button>
              </div>
            </form>
            {start.error && <Alert variant="destructive">{errorMessage(start.error)}</Alert>}
            <output className="block text-sm text-muted-foreground">
              {running
                ? `Running since ${agoSeconds(job.started)}, started by ${trigger}, for ${job.branches.join(', ') || 'default branches'}.`
                : job.finished
                  ? `Last run finished ${agoSeconds(job.finished)}: exit ${job.returncode}, ${indexExit[String(job.returncode)] ?? 'an unrecognised exit code'}.`
                  : 'No run since Argus started.'}
            </output>
            {st.data.pending.length > 0 && <p className="text-sm text-muted-foreground">Waiting: {st.data.pending.join(', ')}</p>}
            {job.repos_error && <Alert variant="warning">{job.repos_error}</Alert>}
            {job.tail.length > 0 && <CodeBlock code={job.tail.join('\n')} label="run log" log />}
          </CardContent>
        </Card>
        <DataTable columns={repoColumns} data={repos} noun="repositories" getRowId={(r) => `${r.repo}@${r.branch}`} initialSorting={[{ id: 'repo', desc: false }]} />
      </div>
    </>
  )
}

// --------------------------------------------------------------------- packs --

interface Packs {
  configured?: boolean
  packs: { name: string; version: string; model: string; dim: number; size_bytes: number; license: string | null; compatible: boolean; incompatible_reason: string | null }[]
  job: { state: string; action: string | null; target: string | null; returncode: number | null; tail: string[]; finished: number | null }
  index_url: string | null
  error?: string
}

export function PacksPage() {
  const queryClient = useQueryClient()
  const confirm = useConfirm()
  const p = useQuery({
    queryKey: ['admin', 'argus', 'packs'],
    queryFn: ({ signal }) => api<Packs>('/api/admin/argus/packs', { signal }),
    refetchInterval: (q) => (q.state.data?.job?.state === 'running' ? 3000 : false),
  })
  const act = useMutation({
    mutationFn: ({ action, body }: { action: string; body: object }) => api(`/api/admin/argus/packs/${action}`, { body }),
    onSuccess: () => void queryClient.invalidateQueries({ queryKey: ['admin', 'argus', 'packs'] }),
    onError: (e) => toast.error(errorMessage(e)),
  })
  const [source, setSource] = useState('')
  const [sha, setSha] = useState('')
  if (p.isPending) return <PageSkeleton />
  if (p.error) return <QueryError error={p.error} retry={() => p.refetch()} />
  if (p.data.configured === false)
    return (
      <>
        <PageHeader title="Knowledge packs" />
        <NotConfigured />
      </>
    )
  const running = p.data.job.state === 'running'
  const shaBad = sha !== '' && !/^[0-9a-fA-F]{64}$/.test(sha)
  return (
    <>
      <PageHeader
        title="Knowledge packs"
        description="Prebuilt indexes of documentation (SDKs, standards) that Argus searches next to your code."
        actions={
          p.data.index_url && (
            <Button variant="outline" disabled={running} onClick={() => act.mutate({ action: 'update', body: {} })}>
              <RefreshCw /> Update all
            </Button>
          )
        }
      />
      <div className="grid gap-6">
        {p.data.error && <Alert variant="warning">{p.data.error}</Alert>}
        {p.data.packs.length === 0 ? (
          <EmptyState icon={Package} title="No pack installed">
            Install one below from its URL.
          </EmptyState>
        ) : (
          <div className="grid gap-3 md:grid-cols-2">
            {p.data.packs.map((k) => (
              <Card key={k.name}>
                <CardHeader>
                  <CardTitle className="flex flex-wrap items-center gap-2">
                    {k.name} <Badge variant="secondary">{k.version}</Badge>
                    {!k.compatible && <Badge variant="destructive">Incompatible</Badge>}
                  </CardTitle>
                  <CardDescription>
                    {k.model}, {k.dim} dimensions · {formatValue(k.size_bytes, 'bytes')} · {k.license ?? 'no licence given'}
                  </CardDescription>
                </CardHeader>
                <CardContent className="grid gap-3">
                  {!k.compatible && k.incompatible_reason && <Alert variant="warning">{k.incompatible_reason}</Alert>}
                  <div className="flex gap-2">
                    {p.data.index_url && (
                      <Button size="sm" variant="outline" disabled={running} onClick={() => act.mutate({ action: 'update', body: { name: k.name } })}>
                        <RefreshCw /> Update
                      </Button>
                    )}
                    <Button
                      size="sm"
                      variant="outline"
                      disabled={running}
                      onClick={async () => {
                        if (await confirm({ title: `Remove ${k.name}?`, description: 'Argus stops searching it. You can install it again.', confirm: 'Remove', destructive: true }))
                          act.mutate({ action: 'remove', body: { name: k.name } })
                      }}
                    >
                      <Trash2 /> Remove
                    </Button>
                  </div>
                </CardContent>
              </Card>
            ))}
          </div>
        )}
        <Card>
          <CardHeader>
            <CardTitle>Install</CardTitle>
            <CardDescription>From a URL or a path Argus can read. Give the SHA-256 so a changed download is refused.</CardDescription>
          </CardHeader>
          <CardContent className="grid gap-4">
            <form
              className="grid gap-4"
              onSubmit={(e) => {
                e.preventDefault()
                if (!shaBad) act.mutate({ action: 'install', body: { source, sha256: sha || null } })
              }}
            >
              <Field label="Pack URL or path">
                <Input value={source} onChange={(e) => setSource(e.target.value)} required />
              </Field>
              <Field label="SHA-256" hint="Recommended." error={shaBad ? '64 hexadecimal characters.' : undefined}>
                <Input value={sha} onChange={(e) => setSha(e.target.value.trim())} className="font-mono text-xs" />
              </Field>
              <div>
                <Button type="submit" disabled={running || !source} loading={act.isPending}>
                  <Package /> Install
                </Button>
              </div>
            </form>
            <output className="block text-sm text-muted-foreground">
              {running ? `${p.data.job.action} of ${p.data.job.target ?? 'packs'} running…` : p.data.job.finished ? `Last ${p.data.job.action}: exit ${p.data.job.returncode}, ${agoSeconds(p.data.job.finished)}.` : ''}
            </output>
            {p.data.job.tail.length > 0 && <CodeBlock code={p.data.job.tail.join('\n')} label="pack log" log />}
          </CardContent>
        </Card>
      </div>
    </>
  )
}

// ------------------------------------------------------------------- explore --

interface Explore {
  configured?: boolean
  repos: { path_with_namespace: string; branch: string; files: number; symbols: number; public_symbols: number }[]
  symbols: { rows: { name: string; kind: string; path: string; line: number; path_with_namespace: string; branch: string; signature?: string }[]; capped: boolean }
  files: { rows: { path: string; lang: string; size: number; path_with_namespace: string; branch: string; symbols: number }[]; capped: boolean }
  error?: string
}

const ALL = '__all__'

/** What the index holds: for "a tool found nothing; is it absent, named differently, or never indexed?". */
export function ExplorePage() {
  const [q, setQ] = useState('')
  const [submitted, setSubmitted] = useState('')
  const [repo, setRepo] = useState('')
  const e = useQuery({
    queryKey: ['admin', 'argus', 'explore', submitted, repo],
    queryFn: ({ signal }) => api<Explore>(`/api/admin/argus/explore?${new URLSearchParams({ q: submitted, repo, limit: '100' })}`, { signal }),
  })
  if (e.data?.configured === false)
    return (
      <>
        <PageHeader title="Explore the index" />
        <NotConfigured />
      </>
    )
  const repoNames = [...new Set(e.data?.repos.map((r) => r.path_with_namespace) ?? [])]
  return (
    <>
      <PageHeader title="Explore the index" description="Search symbols and paths across everything indexed: is something absent, named differently, or never indexed?" />
      <div className="grid gap-6">
        <form
          className="flex flex-wrap items-end gap-3"
          onSubmit={(ev) => {
            ev.preventDefault()
            setSubmitted(q.trim())
          }}
        >
          <Field label="Symbol or path contains" className="min-w-56 flex-1">
            <Input type="search" value={q} onChange={(ev) => setQ(ev.target.value)} placeholder="DecodeFrame" />
          </Field>
          <Field label="Repository" className="w-64">
            <Select value={repo || ALL} onValueChange={(v) => setRepo(v === ALL ? '' : v)}>
              <SelectTrigger>
                <SelectValue />
              </SelectTrigger>
              <SelectContent>
                <SelectItem value={ALL}>All repositories</SelectItem>
                {repoNames.map((r) => (
                  <SelectItem key={r} value={r}>
                    {r}
                  </SelectItem>
                ))}
              </SelectContent>
            </Select>
          </Field>
          <Button type="submit">
            <Search /> Search
          </Button>
        </form>
        {e.error && <QueryError error={e.error} retry={() => e.refetch()} />}
        {e.data?.error && <Alert variant="warning">{e.data.error}</Alert>}
        {e.isPending && <PageSkeleton />}
        {e.data && submitted && (
          <div className="grid gap-6 xl:grid-cols-2">
            <Card>
              <CardHeader>
                <CardTitle>Symbols</CardTitle>
                {e.data.symbols.capped && <CardDescription>More matches exist; narrow the search.</CardDescription>}
              </CardHeader>
              <CardContent>
                {e.data.symbols.rows.length === 0 ? (
                  <p className="text-sm text-muted-foreground">No symbol matches “{submitted}”.</p>
                ) : (
                  <ul className="grid divide-y rounded-lg border">
                    {e.data.symbols.rows.map((s, i) => (
                      <li key={i} className="grid gap-0.5 px-3 py-2 text-sm">
                        <span className="flex flex-wrap items-center gap-2">
                          <code className="font-mono font-medium">{s.name}</code> <Badge variant="secondary">{s.kind}</Badge>
                        </span>
                        <span className="truncate text-xs text-muted-foreground">
                          {s.path_with_namespace}@{s.branch} · {s.path}:{s.line}
                        </span>
                      </li>
                    ))}
                  </ul>
                )}
              </CardContent>
            </Card>
            <Card>
              <CardHeader>
                <CardTitle>Files</CardTitle>
              </CardHeader>
              <CardContent>
                {e.data.files.rows.length === 0 ? (
                  <p className="text-sm text-muted-foreground">No path matches “{submitted}”.</p>
                ) : (
                  <ul className="grid divide-y rounded-lg border">
                    {e.data.files.rows.map((f, i) => (
                      <li key={i} className="grid gap-0.5 px-3 py-2 text-sm">
                        <code className="truncate font-mono">{f.path}</code>
                        <span className="text-xs text-muted-foreground">
                          {f.path_with_namespace}@{f.branch} · {f.lang} · {f.symbols} symbols
                        </span>
                      </li>
                    ))}
                  </ul>
                )}
              </CardContent>
            </Card>
          </div>
        )}
        {e.data && !submitted && (
          <DataTable
            columns={[
              { accessorKey: 'path_with_namespace', header: ({ column }) => <SortHeader column={column} title="Repository" />, cell: ({ getValue }) => <span className="font-medium">{getValue<string>()}</span> },
              { accessorKey: 'branch', header: 'Branch', cell: ({ getValue }) => <code className="font-mono text-xs">{getValue<string>()}</code> },
              { accessorKey: 'files', header: ({ column }) => <SortHeader column={column} title="Files" />, cell: ({ getValue }) => formatValue(getValue<number>()) },
              { accessorKey: 'symbols', header: ({ column }) => <SortHeader column={column} title="Symbols" />, cell: ({ getValue }) => formatValue(getValue<number>()) },
              { accessorKey: 'public_symbols', header: 'Public', cell: ({ getValue }) => formatValue(getValue<number>()) },
            ] satisfies ColumnDef<Explore['repos'][number]>[]}
            data={e.data.repos}
            noun="indexed repositories"
            getRowId={(r) => `${r.path_with_namespace}@${r.branch}`}
          />
        )}
      </div>
    </>
  )
}
