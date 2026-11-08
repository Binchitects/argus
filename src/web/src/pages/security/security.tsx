import { useQuery, useQueryClient } from '@tanstack/react-query'
import { Download, FolderGit2, Layers, ScanSearch, ShieldAlert, ShieldCheck, Trash2 } from 'lucide-react'
import { useState } from 'react'
import { Link, useNavigate, useParams } from 'react-router'
import { PageHeader } from '@/components/app/page-header'
import { QueryError } from '@/components/app/query-state'
import { Segmented } from '@/components/app/segmented'
import { Stat, StatGrid } from '@/components/app/stat'
import { Alert } from '@/components/ui/alert'
import { Badge } from '@/components/ui/badge'
import { Button } from '@/components/ui/button'
import { Card, CardContent, CardDescription, CardHeader, CardTitle } from '@/components/ui/card'
import { Checkbox } from '@/components/ui/checkbox'
import { useConfirm } from '@/components/ui/confirm'
import { EmptyState } from '@/components/ui/empty-state'
import { Field } from '@/components/ui/field'
import { Input, Textarea } from '@/components/ui/input'
import { Select, SelectContent, SelectItem, SelectTrigger, SelectValue } from '@/components/ui/select'
import { Skeleton } from '@/components/ui/skeleton'
import { toast } from '@/components/ui/toaster'
import { api, errorMessage, mayUse, meQuery } from '@/lib/api'
import { Markdown } from '@/pages/chat/markdown'
import { securityBatchesQuery, securityBatchQuery, securityScansQuery, type BatchBrief, type ScanBrief, type SecurityFinding } from '@/pages/code/api'
import { ScanState } from '@/pages/code/security'
import { repositoriesOf, severities, severityVariant } from '@/pages/code/severity'

type Severity = SecurityFinding['severity']

/** The open findings by severity, as badges, the worst first. */
function Open({ open }: { open: Partial<Record<Severity, number>> }) {
  const any = severities.some((v) => open[v])
  if (!any) return <span className="text-xs text-muted-foreground">Nothing open</span>
  return (
    <span className="flex flex-wrap gap-1">
      {severities.map((v) => (open[v] ? <Badge key={v} variant={severityVariant[v]}>{`${open[v]} ${v}`}</Badge> : null))}
    </span>
  )
}

function BatchState({ b }: { b: BatchBrief }) {
  if (b.status === 'failed') return <Badge variant="destructive">Failed</Badge>
  if (b.status !== 'done') return <Badge variant="secondary">{b.step ?? 'Waiting'}…</Badge>
  return <Badge variant="success">Done</Badge>
}

/**
 * Security review: the section of its own. What is open across the latest review of each repository; a review of one
 * repository or of several at once (each reviewed, then looked at across for what they share); and every review.
 */
export function SecurityPage() {
  const navigate = useNavigate()
  const queryClient = useQueryClient()
  const me = useQuery(meQuery)
  const mayStart = mayUse(me.data, 'review.run')
  const scans = useQuery(securityScansQuery)
  const batches = useQuery(securityBatchesQuery)
  const [many, setMany] = useState<'one' | 'several'>('one')
  const [project, setProject] = useState('')
  const [list, setList] = useState('')
  const [ref, setRef] = useState('')
  const [title, setTitle] = useState('')
  const [cross, setCross] = useState(true)
  const [busy, setBusy] = useState(false)
  const [words, setWords] = useState('')
  const repositories = repositoriesOf(list)

  const start = async () => {
    setBusy(true)
    try {
      if (many === 'one') {
        const made = await api<{ id: string }>('/api/code/security', { body: { project: project.trim(), ...(ref.trim() && { ref: ref.trim() }) } })
        await queryClient.invalidateQueries({ queryKey: ['code', 'security'] })
        navigate(`/security/scans/${made.id}`)
      } else {
        const made = await api<{ id: string }>('/api/code/security/batches', {
          body: { projects: repositories, cross, ...(ref.trim() && { ref: ref.trim() }), ...(title.trim() && { title: title.trim() }) },
        })
        await queryClient.invalidateQueries({ queryKey: ['code', 'security'] })
        navigate(`/security/batches/${made.id}`)
      }
    } catch (e) {
      toast.error(errorMessage(e))
    } finally {
      setBusy(false)
    }
  }

  // Each repository's latest review that ended: what is open now.
  const latest = new Map<string, { scan: ScanBrief; open: Partial<Record<Severity, number>> }>()
  for (const x of scans.data ?? []) {
    const key = `${x.scan.project}@${x.scan.ref ?? ''}`
    if (x.scan.status === 'done' && !latest.has(key)) latest.set(key, x)
  }
  const openNow = (v: Severity) => [...latest.values()].reduce((n, x) => n + (x.open[v] ?? 0), 0)
  const running = (scans.data ?? []).filter((x) => x.scan.status === 'running' || x.scan.status === 'queued').length
  const q = words.trim().toLowerCase()
  const shownScans = (scans.data ?? []).filter((x) => !q || x.scan.project.toLowerCase().includes(q))

  return (
    <>
      <PageHeader
        title="Security review"
        description="Where data from outside reaches a query, a command, a path, a fetch, a deserialiser or HTML, traced and judged by the model, with the security scanners, the packages' known vulnerabilities and the secrets in the history. One repository or several at once, looked at across for what they share. Read with your own access; nothing is written to the code host."
      />
      <div className="grid gap-6">
        {/* Nothing reviewed yet: the form comes first, not a row of noughts. */}
        {(latest.size > 0 || running > 0) && (
        <section aria-label="What is open">
          <StatGrid className="xl:grid-cols-6">
            {severities.map((v) => (
              <Stat key={v} icon={ShieldAlert} label={`Open ${v}`} value={String(openNow(v))} tone={v === 'critical' || v === 'high' ? (openNow(v) > 0 ? 'destructive' : undefined) : undefined} />
            ))}
            <Stat icon={FolderGit2} label="Repositories" value={String(latest.size)} hint="Each one's latest review" />
            <Stat icon={ScanSearch} label="Running" value={String(running)} hint={`${batches.data?.length ?? 0} reviews of several`} />
          </StatGrid>
        </section>
        )}

        {mayStart && (
          <Card>
            <CardHeader>
              <CardTitle>Review security</CardTitle>
              <CardDescription>
                A finding is kept only when it cites the line as the file has it and names a weakness the security pack knows. Several repositories are reviewed one after the other, then looked
                at across: the weaknesses and vulnerable packages they share, what one&apos;s weakness means for the others, and what to fix first.
              </CardDescription>
            </CardHeader>
            <CardContent className="grid gap-4">
              <Segmented
                label="How many repositories"
                value={many}
                onChange={setMany}
                options={[
                  { value: 'one', label: 'One repository' },
                  { value: 'several', label: 'Several repositories' },
                ]}
              />
              <form
                className="grid gap-3"
                onSubmit={(e) => {
                  e.preventDefault()
                  void start()
                }}
              >
                {many === 'one' ? (
                  <Field label="Repository">
                    <Input value={project} onChange={(e) => setProject(e.target.value)} placeholder="group/project" />
                  </Field>
                ) : (
                  <>
                    <Field label="Repositories" hint={`One a line, 2 to 20. ${repositories.length} named.`}>
                      <Textarea rows={4} value={list} onChange={(e) => setList(e.target.value)} placeholder={'payments/api\npayments/web\nshared/auth'} />
                    </Field>
                    <Field label="What it is for" hint="Optional: a name for the review, such as a product or a release.">
                      <Input value={title} maxLength={200} onChange={(e) => setTitle(e.target.value)} placeholder="Payments, before 2.0" />
                    </Field>
                    <div className="flex items-center gap-2 text-sm">
                      <Checkbox id="security-cross" checked={cross} onCheckedChange={(v) => setCross(v === true)} />
                      <label htmlFor="security-cross">Look across them: what they share, and the paths from one to another</label>
                    </div>
                  </>
                )}
                <div className="flex flex-wrap items-end gap-2">
                  <Field label="Branch, tag or commit" className="w-56" hint={many === 'several' ? 'The same in each, or each one’s default branch.' : undefined}>
                    <Input value={ref} onChange={(e) => setRef(e.target.value)} placeholder="the default branch" />
                  </Field>
                  <Button type="submit" loading={busy} disabled={many === 'one' ? !project.trim() : repositories.length < 2 || repositories.length > 20}>
                    <ShieldCheck /> {many === 'one' ? 'Review security' : `Review ${repositories.length} repositories`}
                  </Button>
                </div>
              </form>
            </CardContent>
          </Card>
        )}

        <section aria-labelledby="batches-title" className="grid gap-3">
          <h2 id="batches-title" className="text-lg font-semibold">
            Several repositories
          </h2>
          {batches.error ? (
            <QueryError error={batches.error} retry={() => batches.refetch()} />
          ) : !batches.data ? (
            <Skeleton className="h-16" />
          ) : batches.data.length === 0 ? (
            <p className="text-sm text-muted-foreground">No review of several repositories yet.</p>
          ) : (
            <ul className="grid gap-2" aria-label="Reviews of several repositories">
              {batches.data.map(({ batch: b, repositories: names, done, open }) => (
                <li key={b.id}>
                  <Link to={`/security/batches/${b.id}`} className="flex flex-wrap items-center gap-3 rounded-lg border px-4 py-3 text-sm outline-none hover:bg-accent focus-visible:ring-[3px] focus-visible:ring-ring">
                    <Layers className="size-4 shrink-0 text-muted-foreground" aria-hidden="true" />
                    <span className="min-w-0 flex-1">
                      <span className="block truncate font-medium">{b.title ?? `${names.length} repositories`}</span>
                      <span className="block truncate text-xs text-muted-foreground">
                        {names.join(', ')}
                        {b.ref && ` @ ${b.ref}`} · {new Date(b.createdAt).toLocaleString()} · {done} of {names.length} reviewed
                      </span>
                    </span>
                    <Open open={open} />
                    <BatchState b={b} />
                  </Link>
                </li>
              ))}
            </ul>
          )}
        </section>

        <section aria-labelledby="scans-title" className="grid gap-3">
          <div className="flex flex-wrap items-center justify-between gap-2">
            <h2 id="scans-title" className="text-lg font-semibold">
              Repositories
            </h2>
            <Input value={words} onChange={(e) => setWords(e.target.value)} placeholder="Find a repository" aria-label="Find a repository" className="h-9 max-w-xs" />
          </div>
          {scans.error ? (
            <QueryError error={scans.error} retry={() => scans.refetch()} />
          ) : !scans.data ? (
            <Skeleton className="h-16" />
          ) : shownScans.length === 0 ? (
            <EmptyState icon={ShieldCheck} title={q ? 'No review of such a repository' : 'No security review yet'}>
              {mayStart ? 'Name a repository above and review its security.' : 'Reviews shared with you show here.'}
            </EmptyState>
          ) : (
            <ul className="grid gap-2" aria-label="Security reviews">
              {shownScans.map(({ scan: s, open }) => (
                <li key={s.id}>
                  <Link to={`/security/scans/${s.id}`} className="flex flex-wrap items-center gap-3 rounded-lg border px-4 py-3 text-sm outline-none hover:bg-accent focus-visible:ring-[3px] focus-visible:ring-ring">
                    <span className="min-w-0 flex-1">
                      <span className="block truncate font-medium">
                        {s.project}
                        {s.ref && <span className="text-muted-foreground"> @ {s.ref}</span>}
                      </span>
                      <span className="block text-xs text-muted-foreground">{new Date(s.createdAt).toLocaleString()}</span>
                    </span>
                    {s.status === 'done' && <Open open={open} />}
                    <ScanState s={s} />
                  </Link>
                </li>
              ))}
            </ul>
          )}
        </section>
      </div>
    </>
  )
}

/** A review of several repositories: each one's progress and what is open, the look across them, and every finding together. */
export function SecurityBatchPage() {
  const { id = '' } = useParams()
  const navigate = useNavigate()
  const queryClient = useQueryClient()
  const confirm = useConfirm()
  const batch = useQuery(securityBatchQuery(id))
  const [repo, setRepo] = useState('all')
  const [severity, setSeverity] = useState<'all' | Severity>('all')
  if (batch.error) return <QueryError error={batch.error} retry={() => batch.refetch()} />
  if (!batch.data) return <Skeleton className="h-64" />
  const d = batch.data
  const b = d.batch
  const ended = b.status === 'done' || b.status === 'failed'
  const shown = d.findings.filter((f) => (repo === 'all' || f.project === repo) && (severity === 'all' || f.severity === severity))
  const remove = async () => {
    if (!(await confirm({ title: 'Delete this review?', description: 'Each repository’s review in it goes too.', confirm: 'Delete', destructive: true }))) return
    try {
      await api(`/api/code/security/batches/${id}`, { method: 'DELETE' })
      await queryClient.invalidateQueries({ queryKey: ['code', 'security'] })
      navigate('/security')
    } catch (e) {
      toast.error(errorMessage(e))
    }
  }
  return (
    <>
      <PageHeader
        title={b.title ?? `Security: ${d.scans.length} repositories`}
        description={`Reviewed for ${d.by}${b.ref ? `, each at ${b.ref}` : ''}${b.model ? `; looked across with ${b.model}` : ''}. Read with ${d.mine ? 'your' : 'their'} own access; nothing is written to the code host.`}
        actions={
          <>
            {ended && d.findings.length > 0 && (
              <Button variant="outline" asChild>
                <a href={`/api/code/security/batches/${id}/findings.csv`} download>
                  <Download /> Findings (CSV)
                </a>
              </Button>
            )}
            {ended && (
              <Button variant="ghost" onClick={() => void remove()}>
                <Trash2 /> Delete
              </Button>
            )}
          </>
        }
      />
      <div className="grid gap-6">
        <div className="flex flex-wrap items-center gap-3">
          <BatchState b={b} />
          {b.error && <span className="text-sm text-destructive-ink">{b.error}</span>}
        </div>

        <Card>
          <CardHeader>
            <CardTitle>The repositories</CardTitle>
            <CardDescription>Reviewed one after the other; each opens to its own findings.</CardDescription>
          </CardHeader>
          <CardContent>
            <ul className="grid gap-2" aria-label="The repositories">
              {d.scans.map((s) => (
                <li key={s.id}>
                  <Link to={`/security/scans/${s.id}`} className="flex flex-wrap items-center gap-3 rounded-md border px-3 py-2 text-sm hover:bg-accent">
                    <FolderGit2 className="size-4 text-muted-foreground" aria-hidden="true" />
                    <span className="min-w-0 flex-1 truncate font-medium">
                      {s.project}
                      {s.commit && <code className="ms-2 text-xs font-normal text-muted-foreground">{s.commit.slice(0, 12)}</code>}
                    </span>
                    {s.status === 'done' && <Open open={s.open} />}
                    {s.status === 'failed' ? <Badge variant="destructive" title={s.error ?? undefined}>Failed</Badge> : s.status === 'done' ? <Badge variant="success">Done</Badge> : <Badge variant="secondary">{s.step ?? 'Waiting'}…</Badge>}
                  </Link>
                </li>
              ))}
            </ul>
          </CardContent>
        </Card>

        {ended && (
          <Card>
            <CardHeader>
              <CardTitle>Across the repositories</CardTitle>
              <CardDescription>What they share, from their findings, and the model&apos;s look at what one&apos;s weakness means for the others.</CardDescription>
            </CardHeader>
            <CardContent className="grid gap-6">
              {d.report ? <Markdown text={d.report} /> : <p className="text-sm text-muted-foreground">{b.cross ? 'No look across: fewer than two repositories were reviewed.' : 'Not asked to look across them.'}</p>}
              <div className="grid gap-4 lg:grid-cols-2">
                <div className="grid content-start gap-2">
                  <h3 className="text-sm font-semibold">Weaknesses in more than one</h3>
                  {d.weaknesses.length === 0 ? (
                    <p className="text-sm text-muted-foreground">None: each weakness is in one repository only.</p>
                  ) : (
                    <table className="w-full text-sm" aria-label="Weaknesses in more than one">
                      <thead>
                        <tr className="text-left text-xs text-muted-foreground">
                          <th className="py-1.5 pr-3 font-medium">Weakness</th>
                          <th className="py-1.5 pr-3 font-medium">Worst</th>
                          <th className="py-1.5 pr-3 text-right font-medium">Findings</th>
                          <th className="py-1.5 font-medium">In</th>
                        </tr>
                      </thead>
                      <tbody>
                        {d.weaknesses.map((w) => (
                          <tr key={w.cwe} className="border-t align-top">
                            <td className="py-1.5 pr-3">
                              <span className="font-medium">{w.cwe}</span> <span className="text-muted-foreground">{w.title.replace(`${w.cwe}: `, '')}</span>
                            </td>
                            <td className="py-1.5 pr-3">
                              <Badge variant={severityVariant[w.worst]}>{w.worst}</Badge>
                            </td>
                            <td className="py-1.5 pr-3 text-right tabular-nums">{w.findings}</td>
                            <td className="py-1.5 text-xs">{w.repositories.join(', ')}</td>
                          </tr>
                        ))}
                      </tbody>
                    </table>
                  )}
                </div>
                <div className="grid content-start gap-2">
                  <h3 className="text-sm font-semibold">Vulnerable packages in more than one</h3>
                  {d.packages.length === 0 ? (
                    <p className="text-sm text-muted-foreground">None shared.</p>
                  ) : (
                    <table className="w-full text-sm" aria-label="Vulnerable packages in more than one">
                      <thead>
                        <tr className="text-left text-xs text-muted-foreground">
                          <th className="py-1.5 pr-3 font-medium">Package</th>
                          <th className="py-1.5 pr-3 font-medium">Worst</th>
                          <th className="py-1.5 font-medium">In</th>
                        </tr>
                      </thead>
                      <tbody>
                        {d.packages.map((p) => (
                          <tr key={p.package} className="border-t align-top">
                            <td className="py-1.5 pr-3">
                              <span className="font-medium">{p.package}</span> <span className="text-xs text-muted-foreground">{p.advisories}</span>
                            </td>
                            <td className="py-1.5 pr-3">
                              <Badge variant={severityVariant[p.worst]}>{p.worst}</Badge>
                            </td>
                            <td className="py-1.5 text-xs">{p.repositories.join(', ')}</td>
                          </tr>
                        ))}
                      </tbody>
                    </table>
                  )}
                  {d.withSecrets.length > 0 && (
                    <Alert variant="destructive" className="mt-2">
                      Secrets in the code or history of {d.withSecrets.join(', ')}: revoke them, and check none is used in another repository.
                    </Alert>
                  )}
                </div>
              </div>
            </CardContent>
          </Card>
        )}

        {d.findings.length > 0 && (
          <Card>
            <CardHeader>
              <CardTitle>Every finding</CardTitle>
              <CardDescription>All the repositories&apos; findings together, the worst first; each opens in its repository&apos;s review, to fix, accept or mark wrong.</CardDescription>
            </CardHeader>
            <CardContent className="grid gap-3">
              <div className="flex flex-wrap gap-2">
                <Select value={repo} onValueChange={setRepo}>
                  <SelectTrigger className="h-9 w-56" aria-label="Repository">
                    <SelectValue />
                  </SelectTrigger>
                  <SelectContent>
                    <SelectItem value="all">Every repository</SelectItem>
                    {d.scans.map((s) => (
                      <SelectItem key={s.id} value={s.project}>
                        {s.project}
                      </SelectItem>
                    ))}
                  </SelectContent>
                </Select>
                <Select value={severity} onValueChange={(v) => setSeverity(v as 'all' | Severity)}>
                  <SelectTrigger className="h-9 w-40" aria-label="Severity">
                    <SelectValue />
                  </SelectTrigger>
                  <SelectContent>
                    <SelectItem value="all">Every severity</SelectItem>
                    {severities.map((v) => (
                      <SelectItem key={v} value={v}>
                        {v}
                      </SelectItem>
                    ))}
                  </SelectContent>
                </Select>
                <span className="self-center text-sm text-muted-foreground">{shown.length} shown</span>
              </div>
              <ul className="grid gap-1" aria-label="Every finding">
                {shown.map((f) => (
                  <li key={f.id}>
                    <Link to={`/security/scans/${f.scanId}`} className="flex flex-wrap items-center gap-2 rounded-md px-2 py-1.5 text-sm hover:bg-accent">
                      <Badge variant={severityVariant[f.severity]}>{f.severity}</Badge>
                      {f.exploited && <Badge variant="destructive">known exploited</Badge>}
                      {f.state !== 'open' && <Badge variant="secondary">{f.state}</Badge>}
                      <span className="min-w-0 flex-1 truncate">{f.title}</span>
                      <code className="text-xs text-muted-foreground">
                        {f.project} · {f.kind === 'dependency' ? f.source : `${f.path}${f.line > 0 ? `:${f.line}` : ''}`}
                      </code>
                    </Link>
                  </li>
                ))}
              </ul>
            </CardContent>
          </Card>
        )}
      </div>
    </>
  )
}
