import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { CheckCircle2, CircleDashed, CircleHelp, Download, FileCheck2, RefreshCw, TriangleAlert, XCircle, type LucideIcon } from 'lucide-react'
import { useState } from 'react'
import { Link, useLocation, useNavigate, useOutletContext, useSearchParams } from 'react-router'
import { PageHeader } from '@/components/app/page-header'
import { QueryError } from '@/components/app/query-state'
import { Badge } from '@/components/ui/badge'
import { Button } from '@/components/ui/button'
import { Card, CardContent, CardHeader, CardTitle } from '@/components/ui/card'
import { Dialog, DialogContent, DialogDescription, DialogFooter, DialogHeader, DialogTitle } from '@/components/ui/dialog'
import { Field } from '@/components/ui/field'
import { Input, Textarea } from '@/components/ui/input'
import { Select, SelectContent, SelectItem, SelectTrigger, SelectValue } from '@/components/ui/select'
import { Skeleton } from '@/components/ui/skeleton'
import { Tabs, TabsContent, TabsList, TabsTrigger } from '@/components/ui/tabs'
import { toast } from '@/components/ui/toaster'
import { api, errorMessage, type Me } from '@/lib/api'
import { ago, when } from '@/lib/format'
import { cn } from '@/lib/utils'
import { attestedNow, complianceQuery, FRAMEWORK_GROUPS, FRAMEWORKS, manualLink, readinessQuery, type FrameworkReadiness, reviewsQuery, reviewState, tally, type Attestation, type CheckResult, type Control, type Framework, type State } from './compliance-api'
import { DataRequestsSection } from './data-requests'
import { IncidentsSection } from './incidents'
import { ManagementSection } from './management'
import { AiSystemsSection } from './ai-systems'
import { AuditsSection } from './audits'
import { PoliciesSection } from './policies'
import { RisksSection } from './risks'
import { SoaSection } from './soa'
import { SuppliersSection } from './suppliers'

/** How ready the company is for a framework, what is missing and where, and its auditor's package for a period. */
function ReadinessPanel({ r, label }: { r: FrameworkReadiness; label: string }) {
  const [from, setFrom] = useState('')
  const [to, setTo] = useState('')
  const query = new URLSearchParams({ ...(from ? { from } : {}), ...(to ? { to } : {}) }).toString()
  return (
    <section aria-labelledby="readiness" className="grid gap-3 rounded-xl border bg-card p-4">
      <div className="flex flex-wrap items-baseline justify-between gap-2">
        <h2 id="readiness" className="text-lg font-semibold">
          Ready for {label}: {r.score} of 100
        </h2>
        <span className="text-sm text-muted-foreground">
          {r.holding} of {r.controls} of Arena's controls holding, {r.attested} attested
        </span>
      </div>
      {r.gaps.length === 0 ? (
        <p className="text-sm text-success-ink">Nothing missing that Arena can see.</p>
      ) : (
        <ul className="grid gap-1.5 text-sm">
          {r.gaps.map((g) => (
            <li key={g.what} className="grid gap-x-3 sm:grid-cols-[1fr_auto]">
              <span>{g.what}</span>
              <span className="text-xs text-muted-foreground">{g.where}</span>
            </li>
          ))}
        </ul>
      )}
      <div className="flex flex-wrap items-end gap-2">
        <Field label="From" className="w-40">
          <Input type="date" value={from} onChange={(e) => setFrom(e.target.value)} />
        </Field>
        <Field label="To" className="w-40">
          <Input type="date" value={to} onChange={(e) => setTo(e.target.value)} />
        </Field>
        <Button asChild>
          <a href={`/api/admin/compliance/readiness/${r.framework}/package${query ? `?${query}` : ''}`} download>
            <Download /> Download for the auditor
          </a>
        </Button>
        <span className="pb-2 text-xs text-muted-foreground">The checks' history over the period (a year if not said), and what its audit reads.</span>
      </div>
    </section>
  )
}

/** A section's old anchor (#incidents in a notification's link): its tab. */
const HASH_TABS: Record<string, string> = { incidents: 'incidents', 'data-requests': 'requests', risks: 'risks', soa: 'soa', policies: 'policies', audits: 'audits', suppliers: 'suppliers', ai: 'ai', management: 'management', 'access-reviews': 'reviews' }

const AREAS: Record<string, string> = {
  IAM: 'People and access',
  LOG: 'Records',
  MON: 'Monitoring',
  DAT: 'Data',
  NET: 'Network and encryption',
  OPS: 'Operations',
  GOV: 'Kept from breaking',
}

const STATES: Record<State, { label: string; icon: LucideIcon; tone: string; badge: 'success' | 'warning' | 'destructive' | 'secondary' }> = {
  ok: { label: 'Met', icon: CheckCircle2, tone: 'text-success', badge: 'success' },
  warn: { label: 'In part', icon: TriangleAlert, tone: 'text-warning', badge: 'warning' },
  fail: { label: 'Not met', icon: XCircle, tone: 'text-destructive', badge: 'destructive' },
  unknown: { label: 'Unknown', icon: CircleHelp, tone: 'text-muted-foreground', badge: 'secondary' },
}

function StateBadge({ state }: { state: State | null }) {
  if (!state) {
    return (
      <Badge variant="outline" title="Watched from outside, by deploy/scripts/compliance-check.py, or proved by tests alone">
        <CircleDashed className="size-3" aria-hidden="true" /> No live check here
      </Badge>
    )
  }
  const s = STATES[state]
  return (
    <Badge variant={s.badge}>
      <s.icon className="size-3" aria-hidden="true" /> {s.label}
    </Badge>
  )
}

function AttestDialog({ control, onClose }: { control: Control; onClose: () => void }) {
  const queryClient = useQueryClient()
  const [note, setNote] = useState('')
  const attest = useMutation({
    mutationFn: () => api<Attestation>(`/api/admin/compliance/controls/${control.id}/attest`, { method: 'POST', body: { note } }),
    onSuccess: () => {
      toast.success(`${control.id} attested`)
      void queryClient.invalidateQueries({ queryKey: complianceQuery.queryKey })
      onClose()
    },
    onError: (e) => toast.error(errorMessage(e)),
  })
  return (
    <Dialog open onOpenChange={(o) => !o && onClose()}>
      <DialogContent>
        <DialogHeader>
          <DialogTitle>
            Attest {control.id}: {control.title}
          </DialogTitle>
          <DialogDescription>{control.operator}</DialogDescription>
        </DialogHeader>
        <form
          className="grid gap-4"
          onSubmit={(e) => {
            e.preventDefault()
            attest.mutate()
          }}
        >
          <Field label="What was done, and where its proof is" hint="Kept with your name and the date, and in the audit log. It counts for a year.">
            <Textarea value={note} onChange={(e) => setNote(e.target.value)} rows={4} maxLength={2000} placeholder="Access policy v3, approved by the CISO on 2026-10-01, in the ISMS folder" />
          </Field>
          <DialogFooter>
            <Button type="button" variant="outline" onClick={onClose}>
              Cancel
            </Button>
            <Button type="submit" loading={attest.isPending} disabled={note.trim().length < 3}>
              Attest
            </Button>
          </DialogFooter>
        </form>
      </DialogContent>
    </Dialog>
  )
}

function AccessReviews({ mayStart }: { mayStart: boolean }) {
  const reviews = useQuery(reviewsQuery)
  const navigate = useNavigate()
  const queryClient = useQueryClient()
  const start = useMutation({
    mutationFn: () => api<{ id: string }>('/api/admin/compliance/reviews', { method: 'POST', body: {} }),
    onSuccess: ({ id }) => {
      void queryClient.invalidateQueries({ queryKey: reviewsQuery.queryKey })
      navigate(`/admin/compliance/reviews/${id}`)
    },
    onError: (e) => toast.error(errorMessage(e)),
  })
  const now = new Date()
  return (
    <section aria-labelledby="reviews" className="grid gap-3">
      <div className="flex flex-wrap items-center justify-between gap-2">
        <div>
          <h2 id="reviews" className="text-lg font-semibold">
            Access reviews
          </h2>
          <p className="text-sm text-muted-foreground">Who holds privileged access and which accounts went unused, each kept or revoked; a new one starts by itself every quarter (Settings → Compliance).</p>
        </div>
        {mayStart && (
          <Button variant="outline" onClick={() => start.mutate()} loading={start.isPending}>
            Start a review
          </Button>
        )}
      </div>
      {reviews.data?.length === 0 && <p className="text-sm text-muted-foreground">No review yet.</p>}
      {(reviews.data?.length ?? 0) > 0 && (
        <ul className="divide-y rounded-lg border bg-card">
          {reviews.data!.slice(0, 8).map((r) => {
            const state = reviewState(r, now)
            return (
              <li key={r.id} className="flex flex-wrap items-center gap-x-4 gap-y-1 p-3 text-sm">
                <Link to={`/admin/compliance/reviews/${r.id}`} className="min-w-48 flex-1 font-medium text-primary-ink underline-offset-2 hover:underline">
                  {r.title}
                </Link>
                <span className="text-muted-foreground">
                  {r.kept + r.revoked} of {r.items} decided, {r.revoked} revoked
                </span>
                <Badge variant={state === 'closed' ? 'success' : state === 'late' ? 'warning' : 'secondary'}>
                  {state === 'closed' ? `Closed ${new Date(r.closedAt!).toLocaleDateString()}` : state === 'late' ? 'Late' : `Due ${new Date(r.dueAt).toLocaleDateString()}`}
                </Badge>
              </li>
            )
          })}
        </ul>
      )}
    </section>
  )
}

function ControlCard({ c, framework, checks, mayAttest, onAttest, now }: { c: Control; framework: Framework | 'all'; checks: Map<string, CheckResult>; mayAttest: boolean; onAttest: () => void; now: Date }) {
  const shown = FRAMEWORKS.filter((f) => (framework === 'all' || f.key === framework) && (c.frameworks[f.key]?.length ?? 0) > 0)
  const overdue = c.attested !== null && !attestedNow(c, now)
  return (
    <Card id={c.id} className="scroll-mt-20">
      <CardHeader className="flex flex-row flex-wrap items-start justify-between gap-2 space-y-0">
        <CardTitle className="flex min-w-0 items-center gap-2 text-base">
          <code className="shrink-0 rounded bg-muted px-1.5 py-0.5 font-mono text-xs">{c.id}</code>
          <span className="min-w-0">{c.title}</span>
        </CardTitle>
        <StateBadge state={c.state} />
      </CardHeader>
      <CardContent className="grid gap-3 text-sm">
        <p className="text-muted-foreground">{c.meets}</p>
        <dl className="grid gap-2 sm:grid-cols-[9rem_1fr]">
          <dt className="font-medium">Serves</dt>
          <dd className="flex flex-wrap gap-x-3 gap-y-1">
            {shown.map((f) => (
              <span key={f.key}>
                <span className="text-muted-foreground">{f.label}</span> {c.frameworks[f.key]!.join(', ')}
              </span>
            ))}
          </dd>
          {c.checks.length > 0 && (
            <>
              <dt className="font-medium">Checked</dt>
              <dd className="flex flex-wrap gap-1.5">
                {c.checks.map((k) => {
                  const r = checks.get(k.replace(/^app:/, ''))
                  const s = r && k.startsWith('app:') ? STATES[r.state] : null
                  return (
                    <span key={k} className="inline-flex items-center gap-1 rounded-md border px-1.5 py-0.5 text-xs" title={r?.detail ?? 'From outside: deploy/scripts/compliance-check.py'}>
                      {s ? <s.icon className={cn('size-3', s.tone)} aria-label={s.label} /> : <CircleDashed className="size-3 text-muted-foreground" aria-label="From outside" />}
                      {r?.title ?? k.replace(/^live:/, 'outside: ')}
                    </span>
                  )
                })}
              </dd>
            </>
          )}
          <dt className="font-medium">Documented</dt>
          <dd className="flex flex-wrap gap-x-3 gap-y-1">
            {c.docs.map((d) => (
              <Link key={d} to={manualLink(d)} className="text-primary-ink underline-offset-2 hover:underline">
                {d.replace(/\.md/, '').replace('#', ' › ').replace(/-/g, ' ')}
              </Link>
            ))}
          </dd>
          <dt className="font-medium">Proved by</dt>
          <dd>
            <details>
              <summary className="cursor-pointer text-muted-foreground">
                {c.tests.length} {c.tests.length === 1 ? 'test' : 'tests'}, run in every release
              </summary>
              <ul className="mt-1 grid gap-0.5 font-mono text-xs break-all text-muted-foreground">
                {c.tests.map((t) => (
                  <li key={t}>{t}</li>
                ))}
              </ul>
            </details>
          </dd>
          <dt className="font-medium">The company's part</dt>
          <dd className="grid gap-1.5">
            <span>{c.operator}</span>
            <span className="flex flex-wrap items-center gap-2">
              {c.attested ? (
                <span className={cn('inline-flex items-center gap-1 text-xs', overdue ? 'text-warning-ink' : 'text-muted-foreground')}>
                  <FileCheck2 className="size-3.5" aria-hidden="true" />
                  {overdue ? 'Due again: attested' : 'Attested'} by {c.attested.actor}{' '}
                  <time dateTime={c.attested.at} title={when(c.attested.at)}>
                    {ago(c.attested.at)}
                  </time>
                  : {c.attested.note}
                </span>
              ) : (
                <span className="text-xs text-muted-foreground">Not attested yet.</span>
              )}
              {mayAttest && (
                <Button size="sm" variant="outline" onClick={onAttest}>
                  {c.attested ? 'Attest again' : 'Attest'}
                </Button>
              )}
            </span>
          </dd>
        </dl>
      </CardContent>
    </Card>
  )
}

/** Admin → Compliance: each control with what meets it, its checks now, the company's part attested, and the evidence in one download. */
export function CompliancePage() {
  const me = useOutletContext<Me>()
  const mayAttest = !!me.isAdmin || !!me.permissions?.includes('compliance.manage')
  const view = useQuery(complianceQuery)
  const [framework, setFramework] = useState<Framework | 'all'>('all')
  const [attesting, setAttesting] = useState<Control | null>(null)
  const readiness = useQuery(readinessQuery)
  const ready = new Map((readiness.data ?? []).map((r) => [r.framework, r]))
  const now = new Date()
  const data = view.data
  const checks = new Map((data?.checks ?? []).map((c) => [c.id, c]))
  const controls = (data?.controls ?? []).filter((c) => framework === 'all' || (c.frameworks[framework]?.length ?? 0) > 0)
  // The tab: ?tab=, or a section's old #anchor (a notification's link), else the frameworks.
  const [params, setParams] = useSearchParams()
  const { hash } = useLocation()
  const tab = params.get('tab') ?? HASH_TABS[hash.slice(1)] ?? 'frameworks'
  const areas = [...new Set(controls.map((c) => c.id.split('-')[0]!))]

  return (
    <>
      <PageHeader
        title="Compliance"
        description="Fourteen frameworks, from ISO 27001 and SOC 2 to the EU AI Act: what Arena does for each control, whether it holds now, and the company's part, attested."
        actions={
          <>
            <Button variant="outline" onClick={() => view.refetch()} loading={view.isFetching && !view.isPending}>
              <RefreshCw /> Check again
            </Button>
            <Button asChild>
              <a href="/api/admin/compliance/evidence" download>
                <Download /> Download evidence
              </a>
            </Button>
          </>
        }
      />
      <Tabs value={tab} onValueChange={(v) => setParams(v === 'frameworks' ? {} : { tab: v }, { replace: true })}>
        <TabsList aria-label="Compliance" className="mb-2">
          <TabsTrigger value="frameworks">Frameworks and controls</TabsTrigger>
          <TabsTrigger value="risks">Risks</TabsTrigger>
          <TabsTrigger value="soa">Statement of Applicability</TabsTrigger>
          <TabsTrigger value="policies">Policies</TabsTrigger>
          <TabsTrigger value="audits">Audits</TabsTrigger>
          <TabsTrigger value="suppliers">Suppliers</TabsTrigger>
          <TabsTrigger value="ai">AI systems</TabsTrigger>
          <TabsTrigger value="management">Management</TabsTrigger>
          <TabsTrigger value="reviews">Access reviews</TabsTrigger>
          <TabsTrigger value="requests">Data requests</TabsTrigger>
          <TabsTrigger value="incidents">Incidents</TabsTrigger>
        </TabsList>
        <TabsContent value="frameworks">
      {view.error ? (
        <QueryError error={view.error} retry={() => view.refetch()} />
      ) : !data ? (
        <div className="grid gap-3 md:grid-cols-4">
          {FRAMEWORKS.slice(0, 8).map((f) => (
            <Skeleton key={f.key} className="h-28" />
          ))}
        </div>
      ) : (
        <div className="grid grid-cols-1 gap-8">
          <section aria-label="Frameworks" className="grid gap-5">
            {FRAMEWORK_GROUPS.filter((g) => FRAMEWORKS.some((f) => f.group === g && f.key in data.frameworks)).map((group) => (
              <div key={group} className="grid gap-2">
                <h2 className="text-sm font-medium text-muted-foreground">{group}</h2>
                <div className="grid gap-3 sm:grid-cols-2 xl:grid-cols-4">
            {FRAMEWORKS.filter((f) => f.group === group && f.key in data.frameworks).map((f) => {
              const t = tally(data.controls, f.key, now)
              const cov = data.coverage[f.key]
              const chosen = framework === f.key
              const r = ready.get(f.key)
              return (
                <button
                  key={f.key}
                  type="button"
                  onClick={() => setFramework(chosen ? 'all' : f.key)}
                  aria-pressed={chosen}
                  className={cn('rounded-xl border bg-card p-4 text-left shadow-xs transition-colors hover:border-primary/50', chosen && 'border-primary ring-1 ring-primary')}
                >
                  <div className="text-xs font-medium text-muted-foreground">{data.frameworks[f.key]}</div>
                  <div className="mt-1 text-lg font-semibold">{f.label}</div>
                  <div className="mt-2 text-sm text-muted-foreground">
                    {t.controls} controls{cov ? `, serving ${cov.served} of its ${cov.clauses} clauses` : `, ${t.clauses} clauses`}
                  </div>
                  <div className="mt-1 flex flex-wrap gap-x-3 text-xs">
                    <span className="text-success-ink">{t.ok} met</span>
                    <span className="text-warning-ink">{t.warn} in part</span>
                    <span className="text-destructive-ink">{t.fail} not met</span>
                    <span className="text-muted-foreground">
                      {t.attested} of {t.controls} attested
                    </span>
                  </div>
                  {r && (
                    <div className="mt-3 grid gap-1">
                      <div className="flex justify-between text-xs">
                        <span className="text-muted-foreground">Ready</span>
                        <span className="font-medium tabular-nums">{r.score} of 100</span>
                      </div>
                      <div className="h-1.5 overflow-hidden rounded-full bg-muted" role="presentation">
                        <div className={cn('h-full rounded-full', r.score >= 80 ? 'bg-success' : r.score >= 50 ? 'bg-warning' : 'bg-destructive')} style={{ width: `${r.score}%` }} />
                      </div>
                    </div>
                  )}
                </button>
              )
            })}
                </div>
              </div>
            ))}
          </section>

          {framework !== 'all' && ready.get(framework) && <ReadinessPanel r={ready.get(framework)!} label={FRAMEWORKS.find((f) => f.key === framework)?.label ?? framework} />}

          <section aria-labelledby="checks" className="grid gap-3">
            <div className="flex flex-wrap items-baseline justify-between gap-2">
              <h2 id="checks" className="text-lg font-semibold">
                Checks now
              </h2>
              <span className="text-xs text-muted-foreground">
                Checked {ago(data.checkedAt)}. From outside, <code className="font-mono">deploy/scripts/compliance-check.py</code> checks TLS, the headers, the session cookie and the cross-site guard.
              </span>
            </div>
            <ul className="grid gap-2 md:grid-cols-2">
              {data.checks.map((c) => {
                const s = STATES[c.state]
                return (
                  <li key={c.id} className="flex gap-3 rounded-lg border bg-card p-3 text-sm">
                    <s.icon className={cn('mt-0.5 size-4 shrink-0', s.tone)} aria-label={s.label} />
                    <div className="min-w-0">
                      <div className="font-medium">{c.title}</div>
                      <div className="text-muted-foreground">{c.detail}</div>
                    </div>
                  </li>
                )
              })}
            </ul>
          </section>

          {(data.retention?.length ?? 0) > 0 && (
            <section aria-labelledby="retention" className="grid gap-3">
              <div>
                <h2 id="retention" className="text-lg font-semibold">
                  How long things are kept
                </h2>
                <p className="text-sm text-muted-foreground">Each kind of data and record, how long it stays, and what removes it (Settings → Data retention).</p>
              </div>
              <dl className="divide-y rounded-lg border bg-card text-sm">
                {data.retention!.map((r) => (
                  <div key={r.what} className="grid gap-1 p-3 sm:grid-cols-[16rem_1fr]">
                    <dt className="font-medium">{r.what}</dt>
                    <dd className="text-muted-foreground">{r.rule}</dd>
                  </div>
                ))}
              </dl>
            </section>
          )}

          <section aria-labelledby="controls" className="grid grid-cols-1 gap-4">
            <div className="flex flex-wrap items-center justify-between gap-2">
              <h2 id="controls" className="text-lg font-semibold">
                Controls
              </h2>
              <Select value={framework} onValueChange={(v) => setFramework(v as Framework | 'all')}>
                <SelectTrigger className="w-56" aria-label="Framework">
                  <SelectValue />
                </SelectTrigger>
                <SelectContent>
                  <SelectItem value="all">Every framework</SelectItem>
                  {FRAMEWORKS.filter((f) => f.key in data.frameworks).map((f) => (
                    <SelectItem key={f.key} value={f.key}>
                      {f.label}
                    </SelectItem>
                  ))}
                </SelectContent>
              </Select>
            </div>
            {areas.map((area) => (
              <div key={area} className="grid gap-3">
                <h3 className="text-sm font-medium text-muted-foreground">{AREAS[area] ?? area}</h3>
                {controls
                  .filter((c) => c.id.startsWith(`${area}-`))
                  .map((c) => (
                    <ControlCard key={c.id} c={c} framework={framework} checks={checks} mayAttest={mayAttest} onAttest={() => setAttesting(c)} now={now} />
                  ))}
              </div>
            ))}
          </section>
        </div>
      )}
        </TabsContent>
        <TabsContent value="risks">
          <RisksSection mayManage={mayAttest} controls={(data?.controls ?? []).map((c) => c.id)} />
        </TabsContent>
        <TabsContent value="soa">
          <SoaSection mayManage={mayAttest} controls={data?.controls ?? []} />
        </TabsContent>
        <TabsContent value="policies">
          <PoliciesSection mayManage={mayAttest} />
        </TabsContent>
        <TabsContent value="audits">
          <AuditsSection mayManage={mayAttest} />
        </TabsContent>
        <TabsContent value="suppliers">
          <SuppliersSection mayManage={mayAttest} />
        </TabsContent>
        <TabsContent value="ai">
          <AiSystemsSection mayManage={mayAttest} />
        </TabsContent>
        <TabsContent value="management">
          <ManagementSection mayManage={mayAttest} controls={data?.controls ?? []} checks={data?.checks ?? []} />
        </TabsContent>
        <TabsContent value="reviews">
          <AccessReviews mayStart={mayAttest} />
        </TabsContent>
        <TabsContent value="requests">
          <DataRequestsSection mayAnswer={!!me.isAdmin || !!me.permissions?.includes('privacy.manage')} />
        </TabsContent>
        <TabsContent value="incidents">
          <IncidentsSection mayManage={mayAttest} />
        </TabsContent>
      </Tabs>
      {attesting && <AttestDialog control={attesting} onClose={() => setAttesting(null)} />}
    </>
  )
}
