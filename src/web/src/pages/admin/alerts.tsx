import { keepPreviousData, useQuery } from '@tanstack/react-query'
import { AlertTriangle, BellOff, CheckCircle2, CircleDot, Clock, XCircle } from 'lucide-react'
import { useState, type ReactNode } from 'react'
import { CodeBlock } from '@/components/app/code-block'
import { PageHeader } from '@/components/app/page-header'
import { PageSkeleton, QueryError } from '@/components/app/query-state'
import { Segmented } from '@/components/app/segmented'
import { Stat, StatGrid } from '@/components/app/stat'
import { Alert } from '@/components/ui/alert'
import { Badge } from '@/components/ui/badge'
import { Card, CardContent, CardDescription, CardHeader, CardTitle } from '@/components/ui/card'
import { EmptyState } from '@/components/ui/empty-state'
import { Skeleton } from '@/components/ui/skeleton'
import { api } from '@/lib/api'
import { ago, duration, when } from '@/lib/format'
import { resolve } from '@/lib/time'
import { cn } from '@/lib/utils'

interface ActiveAlert {
  name: string
  severity: string | null
  state: 'active' | 'suppressed' | 'unprocessed'
  startsAt: string
  summary: string | null
  description: string | null
  labels: Record<string, string>
  silencedBy: string[]
  inhibitedBy: string[]
}

interface Rule {
  group: string
  name: string
  severity: string | null
  state: 'inactive' | 'pending' | 'firing'
  health: string
  lastError: string | null
  query: string
  for: number
  summary: string | null
  description: string | null
  active: number
  activeAt: string | null
}

interface Episode {
  name: string
  severity: string | null
  labels: Record<string, string>
  start: string
  end: string | null
  summary: string | null
}

interface Now {
  firing: ActiveAlert[] | null
  rules: Rule[] | null
  errors: { alertmanager: string | null; prometheus: string | null }
}

/** Labels every alert has, already shown elsewhere on its row. */
const shownElsewhere = new Set(['alertname', 'severity'])

const historyRanges = [
  { value: 'now-24h', label: '24 hours' },
  { value: 'now-7d', label: '7 days' },
  { value: 'now-30d', label: '30 days' },
]

/**
 * What fires now (from Alertmanager), what fired before (Prometheus keeps
 * every alert's state as the ALERTS series), and every rule that watches the
 * stack. Rules are changed in their files; silences stay in Alertmanager.
 */
export function AlertsPage() {
  const now = useQuery({ queryKey: ['admin', 'alerts'], queryFn: ({ signal }) => api<Now>('/api/admin/alerts/', { signal }), refetchInterval: 30_000 })
  const [range, setRange] = useState('now-7d')
  const history = useQuery({
    queryKey: ['admin', 'alerts', 'history', range],
    queryFn: ({ signal }) => {
      const to = new Date()
      return api<{ episodes: Episode[]; from: string; to: string }>(`/api/admin/alerts/history?from=${resolve(range, to).toISOString()}&to=${to.toISOString()}`, { signal })
    },
    placeholderData: keepPreviousData,
    refetchInterval: 60_000,
  })

  if (now.isPending) return <PageSkeleton />
  if (now.error) return <QueryError error={now.error} retry={() => now.refetch()} />
  const { firing, rules, errors } = now.data
  const active = (firing ?? []).filter((a) => a.state !== 'suppressed')
  const pending = (rules ?? []).filter((r) => r.state === 'pending')
  const broken = (rules ?? []).filter((r) => r.health !== 'ok')

  return (
    <>
      <PageHeader title="Alerts" description="What fires now, what fired before, and every rule that watches the stack. Checked every 30 seconds." />
      <div className="grid gap-6">
        <StatGrid>
          <Stat label="Firing now" value={firing ? active.length : '—'} icon={XCircle} tone={active.length ? 'destructive' : undefined} hint={firing && firing.length > active.length ? `${firing.length - active.length} silenced` : undefined} />
          <Stat label="Pending" value={rules ? pending.length : '—'} icon={Clock} tone={pending.length ? 'warning' : undefined} hint="Condition met, waiting out its time" />
          <Stat label="Rules" value={rules ? rules.length : '—'} icon={CheckCircle2} tone={broken.length ? 'warning' : undefined} hint={broken.length ? `${broken.length} cannot be evaluated` : 'All evaluate'} />
        </StatGrid>

        <Card>
          <CardHeader>
            <CardTitle>Firing now</CardTitle>
            <CardDescription>From Alertmanager, which also sends the notifications.</CardDescription>
          </CardHeader>
          <CardContent>
            {errors.alertmanager ? (
              <Alert variant="warning" title="Alertmanager did not answer">
                {errors.alertmanager}
              </Alert>
            ) : !firing?.length ? (
              <EmptyState icon={CheckCircle2} title="Nothing is firing" className="py-8">
                Every rule's condition is clear.
              </EmptyState>
            ) : (
              <ul className="grid gap-3">
                {firing.map((a) => (
                  <li key={`${a.name}-${JSON.stringify(a.labels)}`} className={cn('rounded-lg border border-l-4 p-4', edge(a.severity), a.state === 'suppressed' && 'opacity-70')}>
                    <div className="flex flex-wrap items-center gap-2">
                      <Severity value={a.severity} />
                      <span className="font-semibold">{a.name}</span>
                      {a.state === 'suppressed' && (
                        <Badge variant="secondary">
                          <BellOff aria-hidden="true" /> {a.silencedBy.length ? 'Silenced' : 'Inhibited'}
                        </Badge>
                      )}
                      <span className="ml-auto text-sm text-muted-foreground">
                        since <time dateTime={a.startsAt} title={when(a.startsAt)}>{ago(a.startsAt)}</time>
                      </span>
                    </div>
                    {a.summary && <p className="mt-2">{a.summary}</p>}
                    {a.description && <p className="mt-1 text-sm text-muted-foreground">{a.description}</p>}
                    <Labels labels={a.labels} />
                  </li>
                ))}
              </ul>
            )}
          </CardContent>
        </Card>

        <Card>
          <CardHeader className="flex flex-row flex-wrap items-start justify-between gap-3">
            <div className="grid gap-1.5">
              <CardTitle>History</CardTitle>
              <CardDescription>Every time an alert fired: to the half minute over a day or a week, to a few minutes over a month.</CardDescription>
            </div>
            <Segmented label="History range" value={range} onChange={setRange} options={historyRanges} />
          </CardHeader>
          <CardContent>
            {history.error ? (
              <QueryError error={history.error} retry={() => history.refetch()} />
            ) : history.isPending ? (
              <Skeleton className="h-40" />
            ) : history.data.episodes.length === 0 ? (
              <EmptyState icon={CheckCircle2} title="Nothing fired" className="py-8">
                No alert fired in the last {historyRanges.find((r) => r.value === range)?.label}.
              </EmptyState>
            ) : (
              <History episodes={history.data.episodes} from={history.data.from} to={history.data.to} />
            )}
          </CardContent>
        </Card>

        <Card>
          <CardHeader>
            <CardTitle>Rules</CardTitle>
            <CardDescription>
              Prometheus checks each one every 30 seconds; it fires once its condition has held for its time. They are set in <code className="text-xs">deploy/config/prometheus/rules</code>.
            </CardDescription>
          </CardHeader>
          <CardContent>
            {errors.prometheus ? (
              <Alert variant="warning" title="Prometheus did not answer">
                {errors.prometheus}
              </Alert>
            ) : (
              <Rules rules={rules ?? []} />
            )}
          </CardContent>
        </Card>
      </div>
    </>
  )
}

function edge(severity: string | null) {
  return severity === 'critical' ? 'border-l-destructive' : severity === 'warning' ? 'border-l-warning' : 'border-l-border'
}

function Severity({ value }: { value: string | null }) {
  if (value === 'critical')
    return (
      <Badge variant="destructive">
        <XCircle aria-hidden="true" /> Critical
      </Badge>
    )
  if (value === 'warning')
    return (
      <Badge variant="warning">
        <AlertTriangle aria-hidden="true" /> Warning
      </Badge>
    )
  return <Badge variant="secondary">{value ?? 'No severity'}</Badge>
}

function Labels({ labels }: { labels: Record<string, string> }) {
  const rest = Object.entries(labels).filter(([k]) => !shownElsewhere.has(k))
  if (!rest.length) return null
  return (
    <ul className="mt-2 flex flex-wrap gap-1.5" aria-label="Labels">
      {rest.map(([k, v]) => (
        <li key={k} className="rounded-md bg-muted px-1.5 py-0.5 font-mono text-xs">
          <span className="text-muted-foreground">{k}=</span>
          {v}
        </li>
      ))}
    </ul>
  )
}

/** Each firing on the range's timeline, newest first; the words carry the times, the bar shows where they fall. */
function History({ episodes, from, to }: { episodes: Episode[]; from: string; to: string }) {
  const start = new Date(from).getTime()
  const span = new Date(to).getTime() - start
  return (
    <ul className="divide-y">
      {episodes.map((e) => {
        const s = new Date(e.start).getTime()
        const end = e.end ? new Date(e.end).getTime() : new Date(to).getTime()
        const left = Math.max(0, ((s - start) / span) * 100)
        const width = Math.max(0.6, ((end - Math.max(s, start)) / span) * 100)
        return (
          <li key={`${e.name}-${e.start}-${JSON.stringify(e.labels)}`} className="grid gap-2 py-3 sm:grid-cols-[minmax(0,1fr)_14rem] sm:items-center sm:gap-4">
            <div className="min-w-0">
              <div className="flex flex-wrap items-center gap-2">
                <Severity value={e.severity} />
                <span className="font-medium">{e.name}</span>
                {!e.end && (
                  <Badge variant="destructive">
                    <CircleDot aria-hidden="true" /> Still firing
                  </Badge>
                )}
              </div>
              {e.summary && <p className="mt-1 text-sm">{e.summary}</p>}
              <p className="mt-1 text-sm text-muted-foreground">
                <time dateTime={e.start}>{when(e.start)}</time>
                {e.end ? (
                  <>
                    {' '}
                    to <time dateTime={e.end}>{when(e.end)}</time>, {duration(Math.round((end - s) / 1000))}
                  </>
                ) : (
                  `, ${duration(Math.round((end - s) / 1000))} so far`
                )}
              </p>
              <Labels labels={e.labels} />
            </div>
            <div className="relative h-2 rounded-full bg-muted" aria-hidden="true">
              <div className={cn('absolute inset-y-0 rounded-full', e.severity === 'critical' ? 'bg-destructive' : 'bg-warning')} style={{ left: `${left}%`, width: `${Math.min(width, 100 - left)}%` }} />
            </div>
          </li>
        )
      })}
    </ul>
  )
}

const ruleStates: Record<Rule['state'], { label: string; icon: typeof CheckCircle2; variant: 'success' | 'warning' | 'destructive' }> = {
  inactive: { label: 'Clear', icon: CheckCircle2, variant: 'success' },
  pending: { label: 'Pending', icon: Clock, variant: 'warning' },
  firing: { label: 'Firing', icon: XCircle, variant: 'destructive' },
}

/** The rules by group, as their files list them; a rule opens to its description and query. */
function Rules({ rules }: { rules: Rule[] }) {
  const [show, setShow] = useState<'all' | 'active'>('all')
  const shown = show === 'all' ? rules : rules.filter((r) => r.state !== 'inactive' || r.health !== 'ok')
  const groups = [...new Set(shown.map((r) => r.group))]
  return (
    <div className="grid min-w-0 gap-4">
      <Segmented
        label="Rules shown"
        value={show}
        onChange={setShow}
        options={[
          { value: 'all', label: `All ${rules.length}` },
          { value: 'active', label: 'Pending, firing or failing' },
        ]}
      />
      {groups.length === 0 && <p className="text-sm text-muted-foreground">No rule is pending, firing or failing.</p>}
      {groups.map((g) => (
        <section key={g} aria-labelledby={`rules-${g}`} className="grid min-w-0 gap-1">
          <h3 id={`rules-${g}`} className="text-sm font-semibold text-muted-foreground">
            {g}
          </h3>
          <ul className="min-w-0 divide-y rounded-lg border">
            {shown
              .filter((r) => r.group === g)
              .map((r) => (
                <li key={r.name}>
                  <RuleRow rule={r} />
                </li>
              ))}
          </ul>
        </section>
      ))}
    </div>
  )
}

function RuleRow({ rule: r }: { rule: Rule }) {
  const state = ruleStates[r.state] ?? ruleStates.inactive
  return (
    <details className="group">
      <summary className="flex cursor-pointer flex-wrap items-center gap-x-3 gap-y-1 px-3 py-2.5 outline-none hover:bg-accent/50 focus-visible:bg-accent [&::-webkit-details-marker]:hidden">
        <Badge variant={state.variant} className="w-20 justify-center">
          <state.icon aria-hidden="true" /> {state.label}
        </Badge>
        <span className="font-medium">{r.name}</span>
        <Severity value={r.severity} />
        {r.health !== 'ok' && <Badge variant="destructive">Cannot be evaluated</Badge>}
        <span className="ml-auto text-xs text-muted-foreground">{r.for ? `after ${duration(r.for)}` : 'at once'}</span>
      </summary>
      <div className="grid gap-2 border-t bg-muted/30 px-3 py-3 text-sm">
        {r.summary && <Field label="Says">{r.summary}</Field>}
        {r.description && <Field label="Why">{r.description}</Field>}
        {r.activeAt && <Field label="Condition met">{`${ago(r.activeAt)} (${when(r.activeAt)})${r.active > 1 ? `, ${r.active} series` : ''}`}</Field>}
        {r.lastError && <Field label="Error">{r.lastError}</Field>}
        <CodeBlock code={r.query} label="PromQL" />
      </div>
    </details>
  )
}

function Field({ label, children }: { label: string; children: ReactNode }) {
  return (
    <p>
      <span className="text-muted-foreground">{label}: </span>
      {children}
    </p>
  )
}
