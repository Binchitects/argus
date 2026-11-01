import { useQuery } from '@tanstack/react-query'
import { RefreshCw, RotateCcw, RotateCw } from 'lucide-react'
import { useOutletContext } from 'react-router'
import { QueryError } from '@/components/app/query-state'
import { Badge } from '@/components/ui/badge'
import { Button } from '@/components/ui/button'
import { Card, CardContent, CardDescription, CardHeader, CardTitle } from '@/components/ui/card'
import { api, type Me } from '@/lib/api'
import { ago } from '@/lib/format'
import { engineLabel, type EngineInstance } from './engine-api'
import { actionLabel, mayAsk, passing, plural, pollEvery, useRuntimeAsk, type Runtime } from './runtimes-api'

const stateLabel: Record<Runtime['state'], string> = {
  up: 'Up', idle: 'Idle', loading: 'Loading', down: 'Down', reloading: 'Reloading', restarting: 'Restarting', resetting: 'Resetting',
}

const stateVariant = (s: Runtime['state']) => (s === 'up' ? 'success' : s === 'down' ? 'destructive' : s === 'idle' ? 'secondary' : 'warning')

/** Admin → Models' engine summary: Reload and Restart for each engine the app runs, for those who may. */
export function EngineActions({ instances }: { instances: EngineInstance[] }) {
  const me = useOutletContext<Me | undefined>()
  const { asking, run } = useRuntimeAsk()
  const engines = instances.filter((i) => i.managed && i.on !== false)
  const actions = (['reload', 'restart'] as const).filter((a) => mayAsk(me, a))
  if (!engines.length || !actions.length) return null
  return (
    <div className="mt-3 grid gap-2">
      {engines.map((i) => {
        const label = engineLabel(i.kind) + (i.name === i.kind ? '' : ` (${i.name})`)
        return (
          <div key={i.name} className="flex flex-wrap items-center gap-2 text-sm">
            <span className="font-medium">{label}</span>
            {actions.map((a) => (
              <Button
                key={a}
                size="sm"
                variant="outline"
                loading={asking?.id === i.name && asking.action === a}
                onClick={() => run({ id: i.name, kind: 'engine', label }, a)}
                aria-label={`${actionLabel[a]} ${label}`}
              >
                {a === 'reload' ? <RefreshCw /> : <RotateCw />} {actionLabel[a]}
              </Button>
            ))}
          </div>
        )
      })}
    </div>
  )
}

/** Admin → Monitoring: each engine, picture or video server and sandbox, its state, and Reload, Restart or Reset. */
export function RuntimesCard() {
  const me = useOutletContext<Me | undefined>()
  const list = useQuery({
    queryKey: ['admin', 'runtimes'],
    queryFn: ({ signal }) => api<Runtime[]>('/api/admin/runtimes', { signal }),
    refetchInterval: (q) => pollEvery(q.state.data),
  })
  const { asking, run } = useRuntimeAsk()
  if (list.error) return <QueryError error={list.error} retry={() => list.refetch()} />
  if (!list.data?.length) return null
  return (
    <Card>
      <CardHeader>
        <CardTitle>Engines and sandboxes</CardTitle>
        <CardDescription>
          Reload an engine to load its models again, restart one that hangs, reset a sandbox whose jobs hang. Each asks first and is in the audit log.
        </CardDescription>
      </CardHeader>
      <CardContent>
        <ul className="grid divide-y rounded-lg border">
          {list.data.map((r) => {
            const shown = r.actions.filter((a) => mayAsk(me, a))
            return (
              <li key={r.id} className="flex flex-wrap items-start gap-3 px-4 py-3">
                <div className="min-w-0 flex-1">
                  <p className="flex flex-wrap items-center gap-2 font-medium">
                    {r.label}
                    <Badge variant={stateVariant(r.state)}>
                      {stateLabel[r.state]}
                      {r.since && passing.has(r.state) ? ` since ${new Date(r.since).toLocaleTimeString([], { hour: '2-digit', minute: '2-digit' })}` : ''}
                    </Badge>
                  </p>
                  {r.detail && <p className={`text-sm break-words ${r.state === 'down' ? 'text-destructive-ink' : 'text-muted-foreground'}`}>{r.detail}</p>}
                  <p className="text-xs text-muted-foreground">
                    {r.kind === 'sandbox' ? `${plural(r.busy, 'job')} running, ${r.waiting} waiting` : r.kind === 'engine' ? `${plural(r.busy, 'answer')} being written, ${r.waiting} waiting` : null}
                    {r.kind !== 'media' && r.last ? ' · ' : ''}
                    {r.last ? `${actionLabel[r.last.action]} by ${r.last.by ?? 'an admin'} ${ago(r.last.at)}` : ''}
                  </p>
                </div>
                {shown.length > 0 && (
                  <div className="flex flex-wrap gap-2">
                    {shown.map((a) => (
                      <Button
                        key={a}
                        size="sm"
                        variant="outline"
                        disabled={passing.has(r.state) && r.state !== 'loading'}
                        loading={asking?.id === r.id && asking.action === a}
                        onClick={() => run(r, a)}
                        aria-label={`${actionLabel[a]} ${r.label}`}
                      >
                        {a === 'reload' ? <RefreshCw /> : a === 'restart' ? <RotateCw /> : <RotateCcw />} {actionLabel[a]}
                      </Button>
                    ))}
                  </div>
                )}
              </li>
            )
          })}
        </ul>
      </CardContent>
    </Card>
  )
}
