import { useQuery, useQueryClient } from '@tanstack/react-query'
import { Pause, Play, RefreshCw } from 'lucide-react'
import { useEffect, useState, type CSSProperties } from 'react'
import { QueryError } from '@/components/app/query-state'
import { RangeSelect } from '@/components/app/range-select'
import { Segmented } from '@/components/app/segmented'
import { Button } from '@/components/ui/button'
import { Skeleton } from '@/components/ui/skeleton'
import { api } from '@/lib/api'
import { presets, refreshMs, resolve, type TimeRange } from '@/lib/time'
import { PanelView } from './panel'
import type { DashboardDef, PanelDef } from './types'
import { Variables, type Chosen } from './variables'

/**
 * A provisioned dashboard, drawn by the app: Grafana's 24-column grid on wide
 * screens, one panel per row on a phone; Grafana rows become section headings.
 * Live dashboards (a refresh in their file) refresh themselves, and can pause.
 */
export function DashboardView({ uid }: { uid: string }) {
  const def = useQuery({ queryKey: ['dashboard', uid], queryFn: ({ signal }) => api<DashboardDef>(`/api/dashboards/${uid}`, { signal }) })
  const [range, setRange] = useState<TimeRange | null>(null)
  const [vars, setVars] = useState<Chosen>({})
  const [tick, setTick] = useState(0)
  const [paused, setPaused] = useState(false)
  const queryClient = useQueryClient()
  const every = refreshMs(def.data?.refresh)

  useEffect(() => {
    if (!every || paused) return
    const t = setInterval(() => setTick((n) => n + 1), Math.max(every, 5000))
    return () => clearInterval(t)
  }, [every, paused])

  if (def.isPending) return <Skeleton className="h-96" />
  if (def.error) return <QueryError error={def.error} retry={() => def.refetch()} />
  const d = def.data
  const current = range ?? { from: d.time?.from ?? 'now-30d', to: d.time?.to ?? 'now' }
  const sections = split(d.panels)
  // Minutes to a month for live metrics; days to months for usage.
  const live = resolve('now').getTime() - resolve(d.time?.from ?? 'now-30d').getTime() < 2 * 86_400_000

  return (
    <div className="grid gap-6">
      <div className="flex flex-wrap items-end gap-2">
        {live ? (
          <RangeSelect value={current.to === 'now' ? current.from : ''} onChange={(from) => setRange({ from, to: 'now' })} />
        ) : (
          <Segmented
            label="Time range"
            value={current.to === 'now' && presets.some((p) => p.from === current.from) ? current.from : ''}
            onChange={(from) => setRange({ from, to: 'now' })}
            options={presets.map((p) => ({ value: p.from, label: p.label.replace('Last ', '') }))}
          />
        )}
        <Button
          variant="outline"
          size="sm"
          className="h-9"
          onClick={() => {
            setTick(tick + 1)
            void queryClient.invalidateQueries({ queryKey: ['panel', uid] })
          }}
        >
          <RefreshCw /> Refresh
        </Button>
        {every && (
          <Button variant="ghost" size="sm" className="h-9 text-muted-foreground" onClick={() => setPaused(!paused)} aria-pressed={!paused}>
            {paused ? <Play /> : <Pause />} {paused ? `Refresh every ${d.refresh}` : `Refreshing every ${d.refresh}`}
          </Button>
        )}
        {d.variables && d.variables.length > 0 && (
          <div className="basis-full">
            <Variables uid={uid} variables={d.variables} from={current.from} to={current.to} value={vars} onChange={setVars} />
          </div>
        )}
      </div>
      {sections.map((s, i) => (
        <section key={i} className="grid gap-3" aria-label={s.title}>
          {s.title && <h2 className="text-base font-semibold">{s.title}</h2>}
          <div className="grid grid-cols-1 gap-3 lg:grid-cols-24">
            {s.panels.map((p) => (
              <div key={p.key} className="min-w-0 lg:col-span-(--w)" style={{ '--w': p.gridPos?.w ?? 24 } as CSSProperties}>
                <PanelView uid={uid} panel={p} range={current} tick={tick} vars={vars} />
              </div>
            ))}
          </div>
        </section>
      ))}
    </div>
  )
}

/** Grafana rows become headings; panels keep Grafana's top-to-bottom, left-to-right order. */
function split(panels: PanelDef[]): { title?: string; panels: PanelDef[] }[] {
  const out: { title?: string; panels: PanelDef[] }[] = [{ panels: [] }]
  const ordered = [...panels].sort((a, b) => (a.gridPos?.y ?? 0) - (b.gridPos?.y ?? 0) || (a.gridPos?.x ?? 0) - (b.gridPos?.x ?? 0))
  for (const p of ordered) {
    if (p.type === 'row') out.push({ title: p.title, panels: [] })
    else out[out.length - 1]!.panels.push(p)
  }
  return out.filter((s) => s.panels.length || s.title)
}
