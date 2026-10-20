import { useQuery, useQueryClient } from '@tanstack/react-query'
import { ListChecks, MessageSquare, Plus } from 'lucide-react'
import { useState } from 'react'
import { Link, useParams } from 'react-router'
import { PageHeader } from '@/components/app/page-header'
import { QueryError } from '@/components/app/query-state'
import { Segmented } from '@/components/app/segmented'
import { Alert } from '@/components/ui/alert'
import { Badge } from '@/components/ui/badge'
import { Button } from '@/components/ui/button'
import { EmptyState } from '@/components/ui/empty-state'
import { Input } from '@/components/ui/input'
import { Skeleton } from '@/components/ui/skeleton'
import { toast } from '@/components/ui/toaster'
import { api, errorMessage } from '@/lib/api'

export type StepStatus = 'todo' | 'doing' | 'blocked' | 'done'

export interface PlanStep {
  number: number
  title: string
  notes: string | null
  owner: string | null
  status: StepStatus
  start: string | null
  due: string | null
  dependsOn: number[]
}

export interface Plan {
  id: string
  title: string
  goal: string | null
  conversationId: string | null
  updatedAt: string
  steps: PlanStep[]
  warnings: string[]
  done: number
}

const columns: { status: StepStatus; label: string }[] = [
  { status: 'todo', label: 'To do' },
  { status: 'doing', label: 'Doing' },
  { status: 'blocked', label: 'Blocked' },
  { status: 'done', label: 'Done' },
]

const planQuery = (id: string) => ({
  queryKey: ['plans', id] as const,
  queryFn: ({ signal }: { signal: AbortSignal }) => api<Plan>(`/api/plans/${id}`, { signal }),
  refetchInterval: 15_000,
})

/** The person's plans and their rooms', newest change first. */
export function PlansPage() {
  const plans = useQuery({
    queryKey: ['plans'] as const,
    queryFn: ({ signal }) => api<{ id: string; title: string; conversationId: string | null; updatedAt: string; steps: number; done: number }[]>('/api/plans', { signal }),
  })
  return (
    <>
      <PageHeader title="Plans" description="Plans the chat made and keeps up to date (its Plans tool): steps with owners, dates and what each waits for. A room's plans are its people's." />
      {plans.error ? (
        <QueryError error={plans.error} retry={() => plans.refetch()} />
      ) : plans.isPending ? (
        <Skeleton className="h-32" />
      ) : plans.data.length === 0 ? (
        <EmptyState icon={ListChecks} title="No plans yet">
          Ask a chat with the Plans tool on to plan something: a launch, a migration, a trip.
        </EmptyState>
      ) : (
        <ul className="grid gap-2" aria-label="Plans">
          {plans.data.map((p) => (
            <li key={p.id}>
              <Link to={`/plans/${p.id}`} className="flex items-center gap-3 rounded-lg border px-4 py-3 text-sm outline-none hover:bg-accent focus-visible:ring-[3px] focus-visible:ring-ring">
                <ListChecks className="size-4 shrink-0 text-muted-foreground" aria-hidden="true" />
                <span className="min-w-0 flex-1 truncate font-medium">{p.title}</span>
                <span className="text-xs text-muted-foreground tabular-nums">
                  {p.done} of {p.steps} done
                </span>
              </Link>
            </li>
          ))}
        </ul>
      )}
    </>
  )
}

const day = 24 * 60 * 60 * 1000
const toTime = (d: string) => new Date(`${d}T00:00:00Z`).getTime()

/** Bars from start to due on a shared scale; a step with one date is a mark. */
function Timeline({ plan }: { plan: Plan }) {
  const dated = plan.steps.filter((s) => s.start || s.due)
  if (dated.length === 0) return <p className="text-sm text-muted-foreground">No step has dates yet.</p>
  const times = dated.flatMap((s) => [s.start, s.due].filter((d): d is string => !!d).map(toTime))
  const from = Math.min(...times)
  const to = Math.max(...times) + day
  const at = (t: number) => ((t - from) / (to - from)) * 100
  return (
    <div className="grid gap-1.5">
      <div className="flex justify-between text-xs text-muted-foreground">
        <span>{new Date(from).toISOString().slice(0, 10)}</span>
        <span>{new Date(to - day).toISOString().slice(0, 10)}</span>
      </div>
      <ul className="grid gap-1.5" aria-label="Timeline">
      {plan.steps.map((s) => {
        const start = s.start ? toTime(s.start) : s.due ? toTime(s.due) : null
        const end = (s.due ? toTime(s.due) : start ?? 0) + day
        return (
          <li key={s.number} className="grid grid-cols-[minmax(8rem,14rem)_1fr] items-center gap-3 text-sm">
            <span className="truncate" title={s.title}>
              {s.number}. {s.title}
            </span>
            <div className="relative h-6 rounded bg-muted/50">
              {start !== null && (
                <div
                  className={`absolute top-1 h-4 rounded ${s.status === 'done' ? 'bg-success/60' : s.status === 'blocked' ? 'bg-destructive/60' : s.status === 'doing' ? 'bg-primary' : 'bg-primary/40'}`}
                  style={{ left: `${at(start)}%`, width: `${Math.max(1, at(end) - at(start))}%` }}
                  title={`${s.start ?? ''} → ${s.due ?? ''}${s.owner ? ` · ${s.owner}` : ''}`}
                />
              )}
            </div>
          </li>
        )
      })}
      </ul>
    </div>
  )
}

/** One plan: its board (a column per status, a step moved with a click) and its timeline. */
export function PlanPage() {
  const { id = '' } = useParams()
  const queryClient = useQueryClient()
  const plan = useQuery(planQuery(id))
  const [view, setView] = useState<'board' | 'timeline'>('board')
  const [adding, setAdding] = useState('')
  if (plan.error) return <QueryError error={plan.error} retry={() => plan.refetch()} />
  if (!plan.data) return <Skeleton className="h-64" />
  const p = plan.data
  const change = async (path: string, method: 'PATCH' | 'POST', body: object) => {
    try {
      const saved = await api<Plan>(`/api/plans/${id}${path}`, { method, body })
      queryClient.setQueryData(planQuery(id).queryKey, saved)
    } catch (e) {
      toast.error(errorMessage(e))
    }
  }
  return (
    <>
      <PageHeader
        title={p.title}
        description={p.goal ?? undefined}
        actions={
          p.conversationId ? (
            <Button variant="outline" asChild>
              <Link to={`/chat/${p.conversationId}`}>
                <MessageSquare /> Its chat
              </Link>
            </Button>
          ) : undefined
        }
      />
      {p.warnings.length > 0 && (
        <Alert className="mb-4">
          <ul className="list-disc pl-4 text-sm">
            {p.warnings.map((w) => (
              <li key={w}>{w}</li>
            ))}
          </ul>
        </Alert>
      )}
      <div className="mb-4 flex flex-wrap items-center gap-3">
        <Segmented
          label="View"
          value={view}
          onChange={setView}
          options={[
            { value: 'board', label: 'Board' },
            { value: 'timeline', label: 'Timeline' },
          ]}
        />
        <span className="text-sm text-muted-foreground">
          {p.done} of {p.steps.length} done
        </span>
      </div>
      {view === 'timeline' ? (
        <Timeline plan={p} />
      ) : (
        <div className="grid gap-4 md:grid-cols-4">
          {columns.map((c) => (
            <section key={c.status} aria-label={c.label} className="grid content-start gap-2 rounded-lg bg-muted/40 p-3">
              <h2 className="text-sm font-medium">
                {c.label} <span className="text-muted-foreground">({p.steps.filter((s) => s.status === c.status).length})</span>
              </h2>
              {p.steps
                .filter((s) => s.status === c.status)
                .map((s) => (
                  <article key={s.number} className="grid gap-1.5 rounded-md border bg-card p-3 text-sm shadow-xs">
                    <p className="font-medium">
                      {s.number}. {s.title}
                    </p>
                    <p className="flex flex-wrap gap-1.5 text-xs text-muted-foreground">
                      {s.owner && <Badge variant="outline">{s.owner}</Badge>}
                      {(s.start || s.due) && (
                        <span>
                          {s.start ?? '…'} → {s.due ?? '…'}
                        </span>
                      )}
                      {s.dependsOn.length > 0 && <span>after {s.dependsOn.join(', ')}</span>}
                    </p>
                    {s.notes && <p className="text-xs whitespace-pre-wrap text-muted-foreground">{s.notes}</p>}
                    <div className="flex flex-wrap gap-1">
                      {columns
                        .filter((o) => o.status !== s.status)
                        .map((o) => (
                          <Button key={o.status} size="sm" variant="ghost" className="h-6 px-2 text-xs" onClick={() => void change(`/steps/${s.number}`, 'PATCH', { status: o.status })}>
                            {o.label}
                          </Button>
                        ))}
                    </div>
                  </article>
                ))}
              {c.status === 'todo' && (
                <form
                  className="flex gap-1"
                  onSubmit={(e) => {
                    e.preventDefault()
                    if (!adding.trim()) return
                    void change('/steps', 'POST', { title: adding.trim() }).then(() => setAdding(''))
                  }}
                >
                  <Input value={adding} onChange={(e) => setAdding(e.target.value)} placeholder="A new step" aria-label="A new step" className="h-8" />
                  <Button type="submit" size="icon-sm" variant="ghost" aria-label="Add the step">
                    <Plus />
                  </Button>
                </form>
              )}
            </section>
          ))}
        </div>
      )}
    </>
  )
}
