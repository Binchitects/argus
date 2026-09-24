import { useQuery, useQueryClient } from '@tanstack/react-query'
import { useState } from 'react'
import { api } from '../api'
import { ErrorText } from '../components/ErrorText'
import { presets, type TimeRange } from '../time'
import { PanelView } from './Panel'
import type { DashboardDef, PanelDef } from './types'

/** A provisioned dashboard, drawn by the app: Grafana's 24-column grid, one row of panels per `y`. */
export function DashboardView({ uid }: { uid: string }) {
  const def = useQuery({ queryKey: ['dashboard', uid], queryFn: () => api<DashboardDef>(`/api/dashboards/${uid}`) })
  const [range, setRange] = useState<TimeRange | null>(null)
  const [tick, setTick] = useState(0)
  const queryClient = useQueryClient()

  if (def.isPending) return <p aria-busy="true">Loading…</p>
  if (def.error) return <ErrorText error={def.error} />
  const d = def.data
  const current = range ?? { from: d.time?.from ?? 'now-30d', to: d.time?.to ?? 'now' }
  const sections = split(d.panels)

  return (
    <div className="stack">
      <div className="toolbar" role="group" aria-label="Time range">
        {presets.map((p) => (
          <button
            key={p.from}
            type="button"
            className={`chip${current.from === p.from && current.to === 'now' ? ' active' : ''}`}
            aria-pressed={current.from === p.from && current.to === 'now'}
            onClick={() => setRange({ from: p.from, to: 'now' })}
          >
            {p.label}
          </button>
        ))}
        <button
          type="button"
          className="chip"
          onClick={() => {
            setTick(tick + 1)
            void queryClient.invalidateQueries({ queryKey: ['panel', uid] })
          }}
        >
          Refresh
        </button>
      </div>
      {sections.map((s, i) => (
        <div key={i} className="stack">
          {s.title && <h2 className="section-title">{s.title}</h2>}
          <div className="dash-grid">
            {s.panels.map((p) => (
              <div
                key={p.key}
                className="dash-cell"
                style={{ '--w': p.gridPos?.w ?? 24, '--h': p.gridPos?.h ?? 8 } as React.CSSProperties}
              >
                <PanelView uid={uid} panel={p} range={current} tick={tick} />
              </div>
            ))}
          </div>
        </div>
      ))}
    </div>
  )
}

/** Grafana rows become headings; panels keep file order, which is Grafana's top-to-bottom, left-to-right. */
function split(panels: PanelDef[]): { title?: string; panels: PanelDef[] }[] {
  const out: { title?: string; panels: PanelDef[] }[] = [{ panels: [] }]
  const ordered = [...panels].sort((a, b) => (a.gridPos?.y ?? 0) - (b.gridPos?.y ?? 0) || (a.gridPos?.x ?? 0) - (b.gridPos?.x ?? 0))
  for (const p of ordered) {
    if (p.type === 'row') out.push({ title: p.title, panels: [] })
    else out[out.length - 1].panels.push(p)
  }
  return out.filter((s) => s.panels.length || s.title)
}
