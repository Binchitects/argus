import { useQuery, useQueryClient } from '@tanstack/react-query'
import { Pause, Play, RefreshCw } from 'lucide-react'
import { useCallback, useEffect, useState, type CSSProperties } from 'react'
import { useSearchParams } from 'react-router'
import { QueryError } from '@/components/app/query-state'
import { RangeSelect } from '@/components/app/range-select'
import { Button } from '@/components/ui/button'
import { Skeleton } from '@/components/ui/skeleton'
import { api } from '@/lib/api'
import { dashboardRange, dashboardRangeOptions, fromRangeKey, rangeKey, refreshMs } from '@/lib/time'
import { PanelView } from './panel'
import type { DashboardDef, PanelDef } from './types'
import { Variables, type Chosen } from './variables'

/**
 * A provisioned dashboard, drawn by the app: Grafana's 24-column grid on wide
 * screens, one panel per row on a phone; Grafana rows become section headings.
 * Live dashboards (a refresh in their file) refresh themselves, and can pause.
 * The time range is the last hour unless the file says another; one chosen goes
 * in the address (Grafana's from and to), so a link opens the same view. Its
 * menu is as wide as its label, but no wider than the page: a long one is cut
 * short. A panel marked hideWhenEmpty is left out while it has no data (a sensor
 * this machine lacks); a row with emptyText says it instead of its panels while
 * none of them has data (the GPU section of a machine without one).
 */
export function DashboardView({ uid }: { uid: string }) {
  const def = useQuery({ queryKey: ['dashboard', uid], queryFn: ({ signal }) => api<DashboardDef>(`/api/dashboards/${uid}`, { signal }) })
  const [params, setParams] = useSearchParams()
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
  const current = dashboardRange(params, d.time)
  const sections = split(d.panels)
  const choose = (key: string) =>
    setParams(
      (p) => {
        const next = new URLSearchParams(p)
        const r = fromRangeKey(key)
        next.set('from', r.from)
        next.set('to', r.to)
        return next
      },
      { replace: true },
    )

  return (
    <div className="grid gap-6">
      <div className="flex min-w-0 flex-wrap items-end gap-2">
        <RangeSelect value={rangeKey(current)} onChange={choose} options={dashboardRangeOptions(current)} className="h-9 w-auto max-w-full min-w-44" />
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
        <Section key={i} uid={uid} section={s} range={current} tick={tick} vars={vars} />
      ))}
    </div>
  )
}

interface SectionDef {
  title?: string
  emptyText?: string
  panels: PanelDef[]
}

function Section({ uid, section: s, range, tick, vars }: { uid: string; section: SectionDef; range: ReturnType<typeof dashboardRange>; tick: number; vars: Chosen }) {
  // Which panels came back with nothing; the panels stay mounted while hidden, so data that comes back shows them again.
  const [empty, setEmpty] = useState<Record<number, boolean>>({})
  const report = useCallback((key: number, is: boolean) => setEmpty((e) => (e[key] === is ? e : { ...e, [key]: is })), [])
  const queried = s.panels.filter((p) => p.type !== 'text')
  const none = !!s.emptyText && queried.length > 0 && queried.every((p) => empty[p.key] === true)
  return (
    <section className="grid gap-3" aria-label={s.title}>
      {s.title && <h2 className="text-base font-semibold">{s.title}</h2>}
      {none && <p className="rounded-xl border border-dashed bg-card px-4 py-3 text-sm text-muted-foreground">{s.emptyText}</p>}
      <div className="grid grid-cols-1 gap-3 lg:grid-cols-24" hidden={none}>
        {s.panels.map((p) => (
          <div
            key={p.key}
            className="min-w-0 lg:col-span-(--w)"
            hidden={!!p.options?.hideWhenEmpty && empty[p.key] === true}
            style={{ '--w': p.gridPos?.w ?? 24 } as CSSProperties}
          >
            <PanelView uid={uid} panel={p} range={range} tick={tick} vars={vars} level={s.title ? 3 : 2} onEmpty={(is) => report(p.key, is)} />
          </div>
        ))}
      </div>
    </section>
  )
}

/** Grafana rows become headings; panels keep Grafana's top-to-bottom, left-to-right order. */
function split(panels: PanelDef[]): SectionDef[] {
  const out: SectionDef[] = [{ panels: [] }]
  const ordered = [...panels].sort((a, b) => (a.gridPos?.y ?? 0) - (b.gridPos?.y ?? 0) || (a.gridPos?.x ?? 0) - (b.gridPos?.x ?? 0))
  for (const p of ordered) {
    if (p.type === 'row') out.push({ title: p.title, emptyText: p.emptyText, panels: [] })
    else out[out.length - 1]!.panels.push(p)
  }
  return out.filter((s) => s.panels.length || s.title)
}
