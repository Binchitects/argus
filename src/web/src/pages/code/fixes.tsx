import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { ArrowLeft, ExternalLink, GitPullRequestCreate, KeyRound, Square, Trash2, Wrench } from 'lucide-react'
import { useState } from 'react'
import { Link, useNavigate, useParams } from 'react-router'
import { PageHeader } from '@/components/app/page-header'
import { PageSkeleton, QueryError } from '@/components/app/query-state'
import { Segmented } from '@/components/app/segmented'
import { Alert } from '@/components/ui/alert'
import { Badge } from '@/components/ui/badge'
import { Button } from '@/components/ui/button'
import { Card, CardContent, CardDescription, CardHeader, CardTitle } from '@/components/ui/card'
import { useConfirm } from '@/components/ui/confirm'
import { EmptyState } from '@/components/ui/empty-state'
import { Field } from '@/components/ui/field'
import { Input, Textarea } from '@/components/ui/input'
import { Spinner } from '@/components/ui/spinner'
import { toast } from '@/components/ui/toaster'
import { api, errorMessage } from '@/lib/api'
import { cn } from '@/lib/utils'
import { Markdown } from '@/pages/chat/markdown'

type FixStatus = 'working' | 'ready' | 'failed' | 'opened' | 'stopped'

interface FixBrief {
  id: string
  project: string
  branch: string
  title: string
  source: 'finding' | 'security' | 'issue' | 'pipeline' | 'ask'
  status: FixStatus
  step: string | null
  mergeRequestUrl: string | null
  error: string | null
  createdAt: string
}

interface Fix extends FixBrief {
  baseSha: string | null
  ask: string
  summary: string | null
  diff: string
  model: string | null
  log: { tool: string; about: string; result: string }[]
  tests: { command: string; passed: boolean | null; output: string | null } | null
  checks: Checks | null
  files: { path: string; deleted: boolean }[]
}

interface ScannerHit {
  path: string
  line: number
  rule: string
  cwe: string
  title: string
}

/** A security fix's change, checked before its merge request: the scanners before and after it, and the review asked again. */
interface Checks {
  note?: string
  scanners?: { error?: string; tools?: string; still?: ScannerHit[]; new?: ScannerHit[] }
  review?: { error?: string; verdict?: 'fixed' | 'still' | 'gone'; line?: number; why?: string }
}

const statusLabels: Record<FixStatus, string> = { working: 'At work', ready: 'Ready', failed: 'Failed', opened: 'Merge request open', stopped: 'Stopped' }
const statusTone: Record<FixStatus, 'secondary' | 'outline' | 'destructive' | 'success'> = { working: 'outline', ready: 'secondary', failed: 'destructive', opened: 'success', stopped: 'outline' }
const toolLabels: Record<string, string> = { list_files: 'Looked at the files', read_file: 'Read', search: 'Searched for', replace: 'Changed', write_file: 'Wrote', delete_file: 'Deleted', run_tests: 'Ran', finish: 'Finished' }

const tokenQuery = { queryKey: ['fixes', 'token'] as const, queryFn: ({ signal }: { signal: AbortSignal }) => api<{ linked: boolean; account: string | null; gitlab: string | null }>('/api/fixes/token', { signal }) }
const fixesQuery = { queryKey: ['fixes'] as const, queryFn: ({ signal }: { signal: AbortSignal }) => api<FixBrief[]>('/api/fixes', { signal }) }
const fixQuery = (id: string) => ({ queryKey: ['fixes', id] as const, queryFn: ({ signal }: { signal: AbortSignal }) => api<Fix>(`/api/fixes/${id}`, { signal }) })

/** The person's own GitLab token for Fix it and Code Studio: read, cloned, pushed and opened as them. */
export function TokenCard() {
  const queryClient = useQueryClient()
  const token = useQuery(tokenQuery)
  const [value, setValue] = useState('')
  const link = useMutation({
    mutationFn: () => api('/api/fixes/token', { method: 'PUT', body: { token: value.trim() } }),
    onSuccess: () => {
      setValue('')
      void queryClient.invalidateQueries({ queryKey: ['fixes', 'token'] })
    },
    onError: (e) => toast.error(errorMessage(e)),
  })
  const unlink = useMutation({ mutationFn: () => api('/api/fixes/token', { method: 'DELETE' }), onSuccess: () => void queryClient.invalidateQueries({ queryKey: ['fixes', 'token'] }) })
  if (!token.data) return null
  return (
    <Card>
      <CardHeader>
        <CardTitle className="flex items-center gap-2 text-base">
          <KeyRound className="size-4 text-muted-foreground" aria-hidden="true" /> Your GitLab token
        </CardTitle>
        <CardDescription>
          Fix it and Code Studio read, clone, push and open merge requests as you, with your own token (scope api), never Argus's. It is kept encrypted and never shown again.
        </CardDescription>
      </CardHeader>
      <CardContent>
        {token.data.linked ? (
          <div className="flex flex-wrap items-center gap-2 text-sm">
            <Badge variant="success">Linked</Badge> as <span className="font-medium">{token.data.account}</span>
            <Button size="sm" variant="ghost" onClick={() => unlink.mutate()} loading={unlink.isPending}>
              Unlink
            </Button>
          </div>
        ) : (
          <form
            className="flex flex-wrap items-end gap-2"
            onSubmit={(e) => {
              e.preventDefault()
              link.mutate()
            }}
          >
            <Field label="Personal access token" className="min-w-64 flex-1" hint={token.data.gitlab ? `Make one at ${token.data.gitlab}/-/user_settings/personal_access_tokens with the api scope.` : undefined}>
              <Input type="password" autoComplete="off" value={value} onChange={(e) => setValue(e.target.value)} />
            </Field>
            <Button type="submit" loading={link.isPending} disabled={value.trim().length < 8}>
              Link it
            </Button>
          </form>
        )}
      </CardContent>
    </Card>
  )
}

/** Code → Fix it: one's fixes, the newest first, and a new one: words, an issue, or a failed pipeline. */
export function FixesPage() {
  const fixes = useQuery({ ...fixesQuery, refetchInterval: (q) => (q.state.data?.some((f) => f.status === 'working') ? 3000 : false) })
  const token = useQuery(tokenQuery)
  const navigate = useNavigate()
  const [from, setFrom] = useState<'ask' | 'issue' | 'pipeline'>('ask')
  const [project, setProject] = useState('')
  const [branch, setBranch] = useState('')
  const [ask, setAsk] = useState('')
  const [number, setNumber] = useState('')
  const start = useMutation({
    mutationFn: () =>
      api<FixBrief>('/api/fixes', {
        body: {
          project: project.trim(), branch: branch.trim() || undefined, ask: ask.trim() || undefined,
          issue: from === 'issue' ? Number(number) : undefined, pipeline: from === 'pipeline' ? Number(number) : undefined,
        },
      }),
    onSuccess: (f) => navigate(`/code/fixes/${f.id}`),
    onError: (e) => toast.error(errorMessage(e)),
  })
  if (fixes.isPending) return <PageSkeleton />
  if (fixes.error) return <QueryError error={fixes.error} retry={() => fixes.refetch()} />
  return (
    <>
      <PageHeader
        title="Fix it"
        description="An agent takes the code of a branch, makes the change (for a review finding, an issue, a failed pipeline, or your words), runs the tests it can, and shows you the diff. When you say so, it opens the merge request as you."
        actions={
          <Button variant="outline" asChild>
            <Link to="/code">
              <ArrowLeft /> Code
            </Link>
          </Button>
        }
      />
      <div className="grid gap-4">
        <TokenCard />
        {token.data?.linked && (
          <Card>
            <CardHeader>
              <CardTitle className="text-base">A new fix</CardTitle>
              <CardDescription>A review's finding has its own Fix it button.</CardDescription>
            </CardHeader>
            <CardContent>
              <form
                className="grid gap-3"
                onSubmit={(e) => {
                  e.preventDefault()
                  start.mutate()
                }}
              >
                <Segmented label="What to fix" value={from} onChange={setFrom} options={[{ value: 'ask', label: 'In words' }, { value: 'issue', label: 'An issue' }, { value: 'pipeline', label: 'A failed pipeline' }]} />
                <div className="grid gap-3 sm:grid-cols-2">
                  <Field label="Project">
                    <Input value={project} onChange={(e) => setProject(e.target.value)} placeholder="group/project" />
                  </Field>
                  {from === 'pipeline' ? (
                    <Field label="Pipeline">
                      <Input inputMode="numeric" value={number} onChange={(e) => setNumber(e.target.value.replace(/\D/g, ''))} placeholder="1234" />
                    </Field>
                  ) : (
                    <Field label="Branch (optional)" hint="Its default branch when empty.">
                      <Input value={branch} onChange={(e) => setBranch(e.target.value)} placeholder="main" />
                    </Field>
                  )}
                </div>
                {from === 'issue' && (
                  <Field label="Issue">
                    <Input inputMode="numeric" value={number} onChange={(e) => setNumber(e.target.value.replace(/\D/g, ''))} placeholder="42" />
                  </Field>
                )}
                <Field label={from === 'ask' ? 'What to fix' : 'Anything to add (optional)'}>
                  <Textarea rows={3} value={ask} onChange={(e) => setAsk(e.target.value)} placeholder={from === 'ask' ? 'The date parser drops the time zone: keep it, and add a test.' : ''} />
                </Field>
                <Button type="submit" className="w-fit" loading={start.isPending} disabled={!project.trim() || (from === 'ask' ? !ask.trim() : !number)}>
                  <Wrench /> Fix it
                </Button>
              </form>
            </CardContent>
          </Card>
        )}
        {fixes.data.length === 0 ? (
          <EmptyState icon={Wrench} title="No fixes yet">
            Start one above, or press Fix it on a review's finding.
          </EmptyState>
        ) : (
          <ul className="grid gap-1.5" aria-label="Your fixes">
            {fixes.data.map((f) => (
              <li key={f.id}>
                <Link to={`/code/fixes/${f.id}`} className="flex items-center gap-3 rounded-lg border bg-card px-3 py-2 text-sm hover:bg-accent/40">
                  <Badge variant={statusTone[f.status]}>{statusLabels[f.status]}</Badge>
                  <span className="min-w-0 flex-1 truncate font-medium">{f.title}</span>
                  <span className="hidden text-xs text-muted-foreground sm:inline">
                    {f.project} · {f.branch}
                  </span>
                </Link>
              </li>
            ))}
          </ul>
        )}
      </div>
    </>
  )
}

/** A unified diff, its lines coloured: what goes, what comes, and each file's name. */
/** What checking a security fix's change found: whether the scanners and the review still find the weakness, and what is new. */
export function ChecksView({ checks }: { checks: Checks }) {
  const s = checks.scanners
  const r = checks.review
  return (
    <section aria-label="Checked after the change" className="grid gap-2 rounded-xl border p-3 text-sm">
      <h2 className="text-sm font-semibold">Checked after the change</h2>
      {checks.note && <p className="text-muted-foreground">{checks.note}</p>}
      {s && (
        <div className="flex flex-wrap items-center gap-2">
          <span className="font-medium">The scanners</span>
          {s.error ? (
            <span className="text-muted-foreground">could not check it: {s.error}</span>
          ) : (s.still?.length ?? 0) > 0 ? (
            <>
              <Badge variant="destructive">still find it</Badge>
              <span className="text-muted-foreground">
                {s.still!.map((h) => `line ${h.line} (${h.rule})`).join(', ')}
              </span>
            </>
          ) : (
            <Badge variant="success">no longer find it</Badge>
          )}
          {s.tools && <code className="text-xs text-muted-foreground">{s.tools}</code>}
        </div>
      )}
      {s?.new && s.new.length > 0 && (
        <div className="grid gap-1">
          <span className="font-medium">New with the change</span>
          <ul className="grid gap-1" aria-label="New scanner findings">
            {s.new.map((h) => (
              <li key={`${h.path}:${h.line}:${h.rule}`} className="flex flex-wrap items-center gap-2">
                <Badge variant="warning">{h.cwe || h.rule}</Badge>
                <span className="[overflow-wrap:anywhere]">
                  {h.path}:{h.line} {h.title}
                </span>
              </li>
            ))}
          </ul>
        </div>
      )}
      {r && (
        <div className="flex flex-wrap items-center gap-2">
          <span className="font-medium">The security review</span>
          {r.error ? (
            <span className="text-muted-foreground">could not read it again: {r.error}</span>
          ) : (
            <>
              <Badge variant={r.verdict === 'still' ? 'destructive' : 'success'}>
                {r.verdict === 'still' ? 'still reads it as vulnerable' : r.verdict === 'fixed' ? 'reads it as fixed' : 'finds the call gone'}
              </Badge>
              {r.line ? <span className="text-muted-foreground">line {r.line}</span> : null}
              {r.why && <span className="basis-full text-muted-foreground">{r.why}</span>}
            </>
          )}
        </div>
      )}
    </section>
  )
}

export function DiffView({ diff }: { diff: string }) {
  if (!diff.trim()) return <p className="text-sm text-muted-foreground">No change yet.</p>
  return (
    <pre className="max-h-[32rem] overflow-auto rounded-lg border bg-muted/30 py-2 text-xs leading-5" aria-label="The diff">
      {diff.split('\n').map((line, i) => (
        <div
          key={i}
          className={cn(
            'px-3 whitespace-pre',
            line.startsWith('+++') || line.startsWith('---') ? 'bg-muted font-semibold' : line.startsWith('@@') ? 'text-info-ink' : line.startsWith('+') ? 'bg-success/15 text-success-ink' : line.startsWith('-') ? 'bg-destructive/10 text-destructive-ink' : '',
          )}
        >
          {line || ' '}
        </div>
      ))}
    </pre>
  )
}

/** /code/fixes/:id: what the agent did, step by step, the diff and the tests; another round, or the merge request. */
export function FixPage() {
  const { id = '' } = useParams()
  const navigate = useNavigate()
  const queryClient = useQueryClient()
  const confirm = useConfirm()
  const fix = useQuery({ ...fixQuery(id), refetchInterval: (q) => (q.state.data?.status === 'working' ? 2000 : false) })
  const [title, setTitle] = useState<string | null>(null)
  const [more, setMore] = useState('')
  const changed = () => void queryClient.invalidateQueries({ queryKey: ['fixes'] })
  const open = useMutation({
    mutationFn: () => api<{ url: string }>(`/api/fixes/${id}/merge-request`, { body: { title: title?.trim() || undefined } }),
    onSuccess: (r) => {
      toast.success('The merge request is open')
      changed()
      window.open(r.url, '_blank', 'noopener')
    },
    onError: (e) => toast.error(errorMessage(e)),
  })
  const again = useMutation({ mutationFn: () => api(`/api/fixes/${id}/more`, { body: { ask: more.trim() } }), onSuccess: () => (setMore(''), changed()), onError: (e) => toast.error(errorMessage(e)) })
  const stop = useMutation({ mutationFn: () => api(`/api/fixes/${id}/stop`, { body: {} }), onSuccess: changed })
  if (fix.isPending) return <PageSkeleton />
  if (fix.error) return <QueryError error={fix.error} retry={() => fix.refetch()} />
  const f = fix.data
  return (
    <>
      <PageHeader
        title={f.title}
        description={`${f.project} · from ${f.branch}${f.baseSha ? ` at ${f.baseSha.slice(0, 8)}` : ''}${f.model ? ` · ${f.model}` : ''}`}
        actions={
          <>
            <Button variant="outline" asChild>
              <Link to="/code/fixes">
                <ArrowLeft /> Fix it
              </Link>
            </Button>
            {f.status === 'working' && (
              <Button variant="outline" onClick={() => stop.mutate()} loading={stop.isPending}>
                <Square /> Stop
              </Button>
            )}
            <Button
              variant="ghost"
              size="icon"
              aria-label="Delete this fix"
              onClick={async () => {
                if (await confirm({ title: 'Delete this fix?', description: 'Its change goes; a merge request it opened stays in GitLab.', confirm: 'Delete', destructive: true })) {
                  await api(`/api/fixes/${id}`, { method: 'DELETE' }).catch((e: unknown) => toast.error(errorMessage(e)))
                  changed()
                  navigate('/code/fixes')
                }
              }}
            >
              <Trash2 />
            </Button>
          </>
        }
      />
      <div className="grid gap-4 lg:grid-cols-[1fr_22rem]">
        <div className="grid min-w-0 content-start gap-4">
          <div className="flex flex-wrap items-center gap-2">
            <Badge variant={statusTone[f.status]}>{statusLabels[f.status]}</Badge>
            {f.status === 'working' && f.step && (
              <span className="flex items-center gap-2 text-sm text-muted-foreground">
                <Spinner label={f.step} /> {f.step}…
              </span>
            )}
            {f.mergeRequestUrl && (
              <a href={f.mergeRequestUrl} target="_blank" rel="noopener noreferrer" className="flex items-center gap-1 text-sm text-primary hover:underline">
                <ExternalLink className="size-3.5" /> The merge request
              </a>
            )}
          </div>
          {f.error && <Alert variant={f.status === 'failed' ? 'destructive' : 'info'}>{f.error}</Alert>}
          {f.summary && (
            <section aria-label="What it did" className="rounded-lg border bg-card p-3 text-sm">
              <Markdown text={f.summary} />
            </section>
          )}
          <section aria-label="The change" className="grid gap-2">
            <h2 className="text-sm font-semibold">
              The change{f.files.length > 0 ? `: ${f.files.length} ${f.files.length === 1 ? 'file' : 'files'}` : ''}
            </h2>
            <DiffView diff={f.diff} />
          </section>
          {f.checks && <ChecksView checks={f.checks} />}
          {f.tests && (
            <section aria-label="The tests" className="grid gap-1.5">
              <h2 className="flex items-center gap-2 text-sm font-semibold">
                The tests <code className="text-xs font-normal text-muted-foreground">{f.tests.command}</code>
                {f.tests.passed !== null && <Badge variant={f.tests.passed ? 'success' : 'destructive'}>{f.tests.passed ? 'Passed' : 'Did not pass'}</Badge>}
              </h2>
              <pre className="max-h-64 overflow-auto rounded-lg bg-muted p-3 text-xs whitespace-pre-wrap">{f.tests.output}</pre>
            </section>
          )}
          {f.status === 'ready' && (
            <section aria-label="Open the merge request" className="grid gap-2 rounded-xl border p-3">
              <Field label="Its title">
                <Input value={title ?? `Fix: ${f.title}`} maxLength={200} onChange={(e) => setTitle(e.target.value)} />
              </Field>
              <Button className="w-fit" onClick={() => open.mutate()} loading={open.isPending}>
                <GitPullRequestCreate /> Open the merge request as you
              </Button>
            </section>
          )}
          {['ready', 'failed', 'stopped'].includes(f.status) && (
            <form
              className="grid gap-2 rounded-xl border p-3"
              onSubmit={(e) => {
                e.preventDefault()
                again.mutate()
              }}
            >
              <Field label="More to do on it">
                <Textarea rows={2} value={more} onChange={(e) => setMore(e.target.value)} placeholder="Add a test for the empty case, too." />
              </Field>
              <Button type="submit" variant="outline" className="w-fit" loading={again.isPending} disabled={!more.trim()}>
                <Wrench /> Another round
              </Button>
            </form>
          )}
        </div>
        <aside aria-label="What it did, step by step" className="grid content-start gap-2">
          <h2 className="text-sm font-semibold">Step by step</h2>
          <ol className="grid gap-1 text-sm">
            {f.log.map((s, i) => (
              <li key={i} className="rounded-md border px-2 py-1.5">
                <span className="font-medium">{toolLabels[s.tool] ?? s.tool}</span> {s.about && <code className="text-xs break-all">{s.about}</code>}
                {s.result && <span className="mt-0.5 line-clamp-2 block text-xs break-words text-muted-foreground">{s.result}</span>}
              </li>
            ))}
            {f.log.length === 0 && <li className="text-xs text-muted-foreground">Nothing yet.</li>}
          </ol>
          <details className="text-xs">
            <summary className="cursor-pointer text-muted-foreground">What it was asked</summary>
            <p className="mt-1 whitespace-pre-wrap">{f.ask}</p>
          </details>
        </aside>
      </div>
    </>
  )
}
