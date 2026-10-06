import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { Database, FileCode2, Hash, Play, TriangleAlert } from 'lucide-react'
import { useState } from 'react'
import { CodeBlock } from '@/components/app/code-block'
import { PageHeader } from '@/components/app/page-header'
import { PageSkeleton, QueryError } from '@/components/app/query-state'
import { Stat, StatGrid } from '@/components/app/stat'
import { Alert } from '@/components/ui/alert'
import { Button } from '@/components/ui/button'
import { Card, CardContent, CardDescription, CardHeader, CardTitle } from '@/components/ui/card'
import { Checkbox } from '@/components/ui/checkbox'
import { EmptyState } from '@/components/ui/empty-state'
import { Field } from '@/components/ui/field'
import { Input } from '@/components/ui/input'
import { Label } from '@/components/ui/label'
import { toast } from '@/components/ui/toaster'
import { api, errorMessage } from '@/lib/api'
import { agoSeconds, duration, formatValue } from '@/lib/format'
import { configQuery } from '../chat/api'
import { passPercent, type IndexProgress } from './argus-progress'
import { RepositoriesCard } from './argus-repos'
import { ScheduleCard } from './argus-schedule'
import { WebhookCard } from './argus-webhook'
import { indexExit, type IndexSummary } from './ops-api'

export function NotConfigured() {
  return (
    <EmptyState icon={Database} title="Argus is not set up in this deployment">
      Set <code className="font-mono">ARGUS_KEY</code> in <code className="font-mono">deploy/.env</code>, and do not leave <code className="font-mono">argus</code> out in <code className="font-mono">docker-compose.override.yml</code>.
    </EmptyState>
  )
}

// ------------------------------------------------------------------ indexing --

interface IndexStatus {
  configured?: boolean
  job: {
    state: string
    branches: string[]
    started: number | null
    finished: number | null
    returncode: number | null
    tail: string[]
    trigger: string | null
    /** The repositories of the run; null for a pass over every one. */
    repos?: string[] | null
    allow_partial?: boolean
    repos_error?: string
    progress?: IndexProgress | null
  }
  index: IndexSummary
  interval: number
  webhook: boolean
  pending: string[]
}

/** Where a running pass is, in words: the repository on now and its files, or what it does after the last one. */
function passWords(p: IndexProgress): string {
  if (p.stage === 'finishing') return p.what === 'embeddings' ? 'Every repository done: embedding new symbols for meaning search.' : 'Every repository done: linking includes across repositories.'
  if (!p.repo) return 'Asking GitLab for the repositories…'
  const files = p.total ? `, ${p.done ?? 0} of ${p.total} changed files` : ''
  return `Repository ${p.position} of ${p.repos}: ${p.repo}${p.branch ? ` (${p.branch})` : ''}${files}.`
}

export function IndexingPage() {
  const queryClient = useQueryClient()
  const gitlabUrl = useQuery(configQuery).data?.gitlabUrl ?? null
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
  const { job, index: idx } = st.data
  const running = job.state === 'running'
  const trigger =
    { schedule: 'the schedule', webhook: 'a GitLab push or merge', manual: 'an admin', 'repo-schedule': "the repositories' own schedules", queued: 'what waited for the run before' }[job.trigger ?? ''] ??
    job.trigger
  return (
    <>
      <PageHeader title="Indexing" description="Argus's index of your GitLab: what it holds, how current it is, and runs on demand." />
      <div className="grid grid-cols-[minmax(0,1fr)] gap-6">
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
              A pass over every chosen repository and branch, now. {st.data.webhook ? 'GitLab pushes and merges also update the repository they change.' : ''}
              {st.data.interval ? ` Argus's own timer also runs one every ${duration(st.data.interval)} (ARGUS_INDEX_INTERVAL).` : ''}
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
              <Field label="Extra branches, this run only" hint="Besides each repository's own (chosen under Repositories below). Space- or comma-separated; globs work: develop release/*">
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
            {running && job.progress && (
              <div className="grid gap-1.5">
                <progress
                  value={passPercent(job.progress)}
                  max={100}
                  aria-label="Index pass"
                  className="h-2 w-full appearance-none overflow-hidden rounded-full bg-muted [&::-moz-progress-bar]:bg-primary [&::-webkit-progress-bar]:bg-muted [&::-webkit-progress-value]:bg-primary"
                />
                <p className="flex flex-wrap justify-between gap-2 text-xs text-muted-foreground">
                  <span>{passWords(job.progress)}</span>
                  <span className="tabular-nums">{passPercent(job.progress)}%</span>
                </p>
              </div>
            )}
            <output className="block text-sm text-muted-foreground">
              {running
                ? `Running since ${agoSeconds(job.started)}, started by ${trigger}, for ${job.repos?.length ? (job.repos.length === 1 ? job.repos[0] : `${job.repos.length} repositories`) : job.branches.join(', ') || 'default branches'}.`
                : job.finished
                  ? `Last run finished ${agoSeconds(job.finished)}: exit ${job.returncode}, ${indexExit[String(job.returncode)] ?? 'an unrecognised exit code'}.`
                  : 'No run since Argus started.'}
            </output>
            {st.data.pending.length > 0 && <p className="text-sm text-muted-foreground">Waiting: {st.data.pending.join(', ')}</p>}
            {job.repos_error && <Alert variant="warning">{job.repos_error}</Alert>}
            {job.tail.length > 0 && <CodeBlock code={job.tail.join('\n')} label="run log" log />}
          </CardContent>
        </Card>
        <ScheduleCard />
        <WebhookCard />
        <RepositoriesCard gitlabUrl={gitlabUrl} />
      </div>
    </>
  )
}
