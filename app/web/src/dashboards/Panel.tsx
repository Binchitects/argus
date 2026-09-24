import { useQuery } from '@tanstack/react-query'
import DOMPurify from 'dompurify'
import { marked } from 'marked'
import { useState } from 'react'
import { api } from '../api'
import { TimeChart } from '../charts/TimeChart'
import { formatValue } from '../format'
import { intervalFor, resolve, type TimeRange } from '../time'
import { reduce } from './reduce'
import type { Column, PanelData, PanelDef } from './types'

export function PanelView({ uid, panel, range, tick }: { uid: string; panel: PanelDef; range: TimeRange; tick: number }) {
  if (panel.type === 'text') {
    return (
      <section className="panel text-panel">
        {panel.title && <h3>{panel.title}</h3>}
        <div
          className="markdown"
          // The dashboard files are ours; sanitised anyway, so a file can never run script here.
          dangerouslySetInnerHTML={{ __html: DOMPurify.sanitize(marked.parse(panel.options?.content ?? '', { async: false })) }}
        />
      </section>
    )
  }
  return <QueryPanel uid={uid} panel={panel} range={range} tick={tick} />
}

function QueryPanel({ uid, panel, range, tick }: { uid: string; panel: PanelDef; range: TimeRange; tick: number }) {
  const [asTable, setAsTable] = useState(false)
  const data = useQuery({
    queryKey: ['panel', uid, panel.key, range.from, range.to, tick],
    enabled: panel.supported,
    queryFn: () => {
      const from = resolve(range.from)
      const to = resolve(range.to)
      // As Grafana: roughly one bucket per 3 px of a full-width panel.
      const points = Math.round(((panel.gridPos?.w ?? 24) / 24) * 400)
      return api<PanelData>(`/api/dashboards/${uid}/panels/${panel.key}/query`, {
        body: { from: from.toISOString(), to: to.toISOString(), intervalMs: intervalFor(from, to, points) },
      })
    },
  })
  const unit = panel.fieldConfig?.defaults?.unit
  const decimals = panel.fieldConfig?.defaults?.decimals
  const error = data.error?.message ?? data.data?.results.find((r) => r.error)?.error

  let body: React.ReactNode = null
  if (!panel.supported) {
    body = <p className="muted">This panel reads {panel.datasources.join(', ')}, which moves into the app in phase 5. It is in Grafana until then.</p>
  } else if (data.isPending) {
    body = <div className="skeleton" aria-busy="true" />
  } else if (error) {
    body = <p className="error" role="alert">{error}</p>
  } else if (data.data) {
    const results = data.data.results
    if (panel.type === 'stat' || panel.type === 'gauge') {
      const v = reduce(results, panel.options?.reduceOptions?.calcs)
      body = (
        <div className="stat-value" aria-label={panel.title}>
          {formatValue(v, unit, decimals)}
        </div>
      )
    } else if (panel.type === 'timeseries' || panel.type === 'barchart') {
      const series = results.flatMap((r) => r.series ?? [])
      const custom = panel.fieldConfig?.defaults?.custom
      body = !series.length ? (
        <p className="muted">No data in this time range.</p>
      ) : asTable ? (
        <SeriesTable series={series} unit={unit} />
      ) : (
        <TimeChart
          series={series}
          unit={unit}
          stacked={custom?.stacking?.mode === 'normal'}
          bars={custom?.drawStyle === 'bars'}
          label={panel.title ?? 'chart'}
          from={resolve(range.from).getTime()}
          to={resolve(range.to).getTime()}
        />
      )
    } else {
      const t = results.find((r) => r.table)?.table
      body = t ? <ResultTable columns={t.columns} rows={t.rows} panel={panel} capped={t.capped} /> : <p className="muted">No data.</p>
    }
  }

  const isChart = panel.type === 'timeseries' || panel.type === 'barchart'
  const hasSeries = (data.data?.results ?? []).some((r) => (r.series?.length ?? 0) > 0)
  return (
    <section className={`panel panel-${panel.type}`} aria-label={panel.title}>
      <header className="panel-head">
        <h3 title={panel.description}>{panel.title}</h3>
        {isChart && hasSeries && !error && (
          <button type="button" className="link small" onClick={() => setAsTable(!asTable)}>
            {asTable ? 'Show chart' : 'Show as table'}
          </button>
        )}
      </header>
      {body}
    </section>
  )
}

function columnUnit(panel: PanelDef, column: string): { unit?: string; decimals?: number } {
  const out: { unit?: string; decimals?: number } = {}
  for (const o of panel.fieldConfig?.overrides ?? []) {
    if (o.matcher.id !== 'byName' || o.matcher.options !== column) continue
    for (const p of o.properties) {
      if (p.id === 'unit') out.unit = String(p.value)
      if (p.id === 'decimals') out.decimals = Number(p.value)
    }
  }
  return out
}

function ResultTable({ columns, rows, panel, capped }: { columns: Column[]; rows: unknown[][]; panel: PanelDef; capped: boolean }) {
  if (!rows.length) return <p className="muted">No rows in this time range.</p>
  const fmt = columns.map((c) => columnUnit(panel, c.name))
  return (
    <div className="table-wrap">
      <table>
        <thead>
          <tr>
            {columns.map((c) => (
              <th key={c.name} className={c.type === 'number' ? 'num' : undefined}>
                {c.name}
              </th>
            ))}
          </tr>
        </thead>
        <tbody>
          {rows.map((r, i) => (
            <tr key={i}>
              {r.map((v, j) => (
                <td key={j} className={columns[j].type === 'number' ? 'num' : undefined}>
                  {columns[j].type === 'time' && typeof v === 'string' ? new Date(v).toLocaleString() : formatValue(v, fmt[j].unit, fmt[j].decimals)}
                </td>
              ))}
            </tr>
          ))}
        </tbody>
      </table>
      {capped && <p className="muted">Showing the first {rows.length} rows.</p>}
    </div>
  )
}

/** The table view of a chart: one row per time, one column per series. */
function SeriesTable({ series, unit }: { series: { name: string; points: (number | null)[][] }[]; unit?: string }) {
  const times = [...new Set(series.flatMap((s) => s.points.map((p) => p[0] as number)))].sort((a, b) => a - b)
  const lookup = series.map((s) => new Map(s.points.map((p) => [p[0], p[1]])))
  return (
    <div className="table-wrap">
      <table>
        <thead>
          <tr>
            <th>Time</th>
            {series.map((s) => (
              <th key={s.name} className="num">
                {s.name}
              </th>
            ))}
          </tr>
        </thead>
        <tbody>
          {times.map((t) => (
            <tr key={t}>
              <td>{new Date(t).toLocaleString()}</td>
              {lookup.map((m, i) => (
                <td key={i} className="num">
                  {formatValue(m.get(t) ?? null, unit)}
                </td>
              ))}
            </tr>
          ))}
        </tbody>
      </table>
    </div>
  )
}
