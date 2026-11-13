import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { CheckCircle2, Gauge, ListChecks, XCircle } from 'lucide-react'
import { Button } from '@/components/ui/button'
import { Dialog, DialogContent, DialogDescription, DialogHeader, DialogTitle } from '@/components/ui/dialog'
import { toast } from '@/components/ui/toaster'
import { api, errorMessage } from '@/lib/api'
import { cn } from '@/lib/utils'

interface BenchTestResult {
  id: string
  title: string
  skill: string
  passed: boolean
  why: string | null
  ms: number
  tokens: number
  answer: string
}

interface BenchLevel {
  atOnce: number
  tokensPerSecond: number
  perAnswer: number
  firstTokenP50: number
  firstTokenP95: number
  failed: number
  seconds: number
}

export interface BenchRun {
  id: string
  model: string
  kind: 'capability' | 'capacity'
  status: 'running' | 'done' | 'failed'
  error: string | null
  startedAt: string
  finishedAt: string | null
  results: { tests?: BenchTestResult[]; passed?: number; total?: number; levels?: BenchLevel[]; best?: { atOnce: number; tokensPerSecond: number } }
}

const runsKey = (model: string) => ['admin', 'models', model, 'bench'] as const

/**
 * Admin → Models → Test: a model's capability (a set of tests, each checked by a rule: arithmetic, an instruction, logic,
 * JSON, a tool call, a fact in a long text, Persian, code) and its capacity (answers at once, raised until more do not make
 * more), each run kept, the last of each shown.
 */
export function ModelBenchDialog({ model, open, onOpenChange }: { model: string; open: boolean; onOpenChange: (open: boolean) => void }) {
  const queryClient = useQueryClient()
  const runs = useQuery({
    queryKey: runsKey(model),
    queryFn: ({ signal }) => api<BenchRun[]>(`/api/admin/models/${encodeURIComponent(model)}/bench`, { signal }),
    enabled: open,
    refetchInterval: (q) => (q.state.data?.some((r) => r.status === 'running') ? 1500 : false),
  })
  const start = useMutation({
    mutationFn: (kind: BenchRun['kind']) => api<{ id: string }>(`/api/admin/models/${encodeURIComponent(model)}/bench`, { body: { kind } }),
    onSuccess: () => void queryClient.invalidateQueries({ queryKey: runsKey(model) }),
    onError: (e) => toast.error(errorMessage(e)),
  })
  const all = runs.data ?? []
  const running = all.some((r) => r.status === 'running')
  const last = (kind: BenchRun['kind']) => all.find((r) => r.kind === kind)
  return (
    <Dialog open={open} onOpenChange={onOpenChange}>
      <DialogContent className="grid-cols-[minmax(0,1fr)] sm:max-w-3xl">
        <DialogHeader>
          <DialogTitle>Test {model}</DialogTitle>
          <DialogDescription>
            What it can do (each test checked by a rule, no judge), and how much at once (answers at once raised until more do not make more). Through the gateway, as any request: it counts to you.
          </DialogDescription>
        </DialogHeader>
        <div className="flex flex-wrap gap-2">
          <Button size="sm" onClick={() => start.mutate('capability')} disabled={running || start.isPending}>
            <ListChecks /> Test its capability
          </Button>
          <Button size="sm" variant="outline" onClick={() => start.mutate('capacity')} disabled={running || start.isPending}>
            <Gauge /> Test its capacity
          </Button>
          {running && <span className="self-center text-sm text-muted-foreground">Testing…</span>}
        </div>
        <div className="grid max-h-[60vh] gap-4 overflow-y-auto">
          <Capability run={last('capability')} />
          <Capacity run={last('capacity')} />
          {all.length > 2 && (
            <section aria-label="Earlier tests" className="grid gap-1 text-xs text-muted-foreground">
              <h3 className="text-sm font-semibold text-foreground">Earlier</h3>
              {all.slice(0, 10).map((r) => (
                <span key={r.id}>
                  {new Date(r.startedAt).toLocaleString()} · {r.kind} · {r.status}
                  {r.kind === 'capability' && r.results.total ? ` · ${r.results.passed}/${r.results.total} passed` : ''}
                  {r.kind === 'capacity' && r.results.best ? ` · ${r.results.best.tokensPerSecond} tokens a second at ${r.results.best.atOnce} at once` : ''}
                </span>
              ))}
            </section>
          )}
        </div>
      </DialogContent>
    </Dialog>
  )
}

function Capability({ run }: { run: BenchRun | undefined }) {
  if (!run) return null
  const tests = run.results.tests ?? []
  return (
    <section aria-label="Capability" className="grid gap-2">
      <h3 className="text-sm font-semibold">
        Capability{run.results.total ? `: ${run.results.passed} of ${run.results.total} passed` : ''}
        {run.status === 'failed' && <span className="ms-2 font-normal text-destructive-ink">{run.error}</span>}
      </h3>
      <ul className="grid gap-1 text-sm">
        {tests.map((t) => (
          <li key={t.id} className="grid gap-0.5 rounded-md border px-2.5 py-1.5">
            <span className="flex items-center gap-2">
              {t.passed ? <CheckCircle2 className="size-4 text-success-ink" aria-label="passed" /> : <XCircle className="size-4 text-destructive-ink" aria-label="failed" />}
              <span className="flex-1 font-medium">{t.title}</span>
              <span className="text-xs tabular-nums text-muted-foreground">
                {(t.ms / 1000).toFixed(1)} s · {t.tokens} tokens
              </span>
            </span>
            {!t.passed && t.why && <span className="text-xs text-muted-foreground">{t.why}</span>}
            <span className="line-clamp-2 text-xs text-muted-foreground [overflow-wrap:anywhere]">{t.answer}</span>
          </li>
        ))}
      </ul>
    </section>
  )
}

function Capacity({ run }: { run: BenchRun | undefined }) {
  if (!run) return null
  const levels = run.results.levels ?? []
  const best = run.results.best
  return (
    <section aria-label="Capacity" className="grid gap-2">
      <h3 className="text-sm font-semibold">
        Capacity{best && best.atOnce > 0 ? `: ${best.tokensPerSecond} tokens a second, at ${best.atOnce} answers at once` : ''}
        {run.status === 'failed' && <span className="ms-2 font-normal text-destructive-ink">{run.error}</span>}
      </h3>
      <table className="w-full text-sm" aria-label="Answers at once">
        <thead>
          <tr className="text-left text-xs text-muted-foreground">
            <th className="py-1 pr-3 font-medium">At once</th>
            <th className="py-1 pr-3 text-right font-medium">Tokens a second, all</th>
            <th className="py-1 pr-3 text-right font-medium">Each answer's</th>
            <th className="py-1 pr-3 text-right font-medium">First token, p50 / p95</th>
            <th className="py-1 pr-3 text-right font-medium">Failed</th>
          </tr>
        </thead>
        <tbody>
          {levels.map((l) => (
            <tr key={l.atOnce} className={cn('border-t tabular-nums', best?.atOnce === l.atOnce && 'font-medium')}>
              <td className="py-1 pr-3">{l.atOnce}</td>
              <td className="py-1 pr-3 text-right">{l.tokensPerSecond}</td>
              <td className="py-1 pr-3 text-right">{l.perAnswer}</td>
              <td className="py-1 pr-3 text-right">
                {l.firstTokenP50} s / {l.firstTokenP95} s
              </td>
              <td className={cn('py-1 pr-3 text-right', l.failed > 0 && 'text-destructive-ink')}>{l.failed}</td>
            </tr>
          ))}
        </tbody>
      </table>
    </section>
  )
}
