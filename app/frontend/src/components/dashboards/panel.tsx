import { useQuery } from '@tanstack/react-query'
import DOMPurify from 'dompurify'
import { Info, Table2, TrendingUp } from 'lucide-react'
import { marked } from 'marked'
import { useState, type ReactNode } from 'react'
import { Button } from '@/components/ui/button'
import { Skeleton } from '@/components/ui/skeleton'
import { Tooltip } from '@/components/ui/tooltip'
import { ScrollRegion } from '@/components/app/scroll-region'
import { TimeChart } from '@/components/charts/time-chart'
import { api, errorMessage } from '@/lib/api'
import { formatValue } from '@/lib/format'
import { intervalFor, resolve, type TimeRange } from '@/lib/time'
import { cn } from '@/lib/utils'
import { reduce } from './reduce'
import type { Column, PanelData, PanelDef } from './types'

export function PanelView({ uid, panel, range, tick }: { uid: string; panel: PanelDef; range: TimeRange; tick: number }) {
  if (panel.type === 'text') {
    return (
      <Frame panel={panel}>
        <div
          className="text-sm break-words text-muted-foreground [&_a]:text-primary-ink [&_a]:underline [&_code]:font-mono [&_code]:break-all [&_p]:mb-2 [&_pre]:whitespace-pre-wrap [&_pre]:break-all [&_strong]:text-foreground [&_ul]:list-disc [&_ul]:pl-5"
          // The dashboard files are ours; sanitised anyway, so a file can never run script here.
          dangerouslySetInnerHTML={{ __html: DOMPurify.sanitize(marked.parse(panel.options?.content ?? '', { async: false })) }}
        />
      </Frame>
    )
  }
  return <QueryPanel uid={uid} panel={panel} range={range} tick={tick} />
}

function Frame({ panel, action, children, className, compact }: { panel: PanelDef; action?: ReactNode; children: ReactNode; className?: string; compact?: boolean }) {
  return (
    <section data-panel className={cn('flex h-full min-w-0 flex-col gap-3 rounded-xl border bg-card p-4 shadow-xs', className)} aria-label={panel.title}>
      {(panel.title || action) && (
        <header className="flex min-h-7 items-start justify-between gap-2">
          <h3 className={cn('flex min-w-0 items-start gap-1.5 font-semibold', compact ? 'text-xs text-muted-foreground' : 'text-sm')}>
            {/* A stat's title wraps to two lines rather than losing its end in a narrow cell. */}
            <span className={compact ? 'line-clamp-2' : 'truncate'}>{panel.title}</span>
            {panel.description && (
              <Tooltip content={panel.description}>
                <button type="button" className="mt-0.5 shrink-0 rounded text-muted-foreground outline-none focus-visible:ring-[3px] focus-visible:ring-ring" aria-label={`About ${panel.title}`}>
                  <Info className="size-3.5" />
                </button>
              </Tooltip>
            )}
          </h3>
          {action}
        </header>
      )}
      <div className="min-h-0 min-w-0 flex-1">{children}</div>
    </section>
  )
}

function QueryPanel({ uid, panel, range, tick }: { uid: string; panel: PanelDef; range: TimeRange; tick: number }) {
  const [asTable, setAsTable] = useState(false)
  const data = useQuery({
    queryKey: ['panel', uid, panel.key, range.from, range.to, tick],
    enabled: panel.supported,
    queryFn: ({ signal }) => {
      const from = resolve(range.from)
      const to = resolve(range.to)
      // As Grafana: roughly one bucket per 3 px of a full-width panel.
      const points = Math.round(((panel.gridPos?.w ?? 24) / 24) * 400)
      return api<PanelData>(`/api/dashboards/${uid}/panels/${panel.key}/query`, {
        body: { from: from.toISOString(), to: to.toISOString(), intervalMs: intervalFor(from, to, points) },
        signal,
      })
    },
  })
  const unit = panel.fieldConfig?.defaults?.unit
  const decimals = panel.fieldConfig?.defaults?.decimals
  const error = data.error ? errorMessage(data.error) : data.data?.results.find((r) => r.error)?.error
  const isChart = panel.type === 'timeseries' || panel.type === 'barchart'
  const isStat = panel.type === 'stat' || panel.type === 'gauge'
  const hasSeries = (data.data?.results ?? []).some((r) => (r.series?.length ?? 0) > 0)

  let body: ReactNode = null
  if (!panel.supported) {
    body = <p className="text-sm text-muted-foreground">This panel reads {panel.datasources.join(', ')}, which moves into the app in phase 5. It is in Grafana until then.</p>
  } else if (data.isPending) {
    body = <Skeleton className={isStat ? 'h-9 w-28' : 'h-48'} />
  } else if (error) {
    body = (
      <p className="text-sm text-destructive-ink" role="alert">
        {error}
      </p>
    )
  } else if (data.data) {
    const results = data.data.results
    if (isStat) {
      const v = reduce(results, panel.options?.reduceOptions?.calcs)
      body = (
        <div className="text-2xl font-semibold tracking-tight whitespace-nowrap tabular-nums xl:text-[1.75rem]" aria-label={panel.title}>
          {formatValue(v, unit, decimals)}
        </div>
      )
    } else if (isChart) {
      const series = results.flatMap((r) => r.series ?? [])
      const custom = panel.fieldConfig?.defaults?.custom
      body = !series.length ? (
        <p className="py-10 text-center text-sm text-muted-foreground">No data in this time range.</p>
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
      body = t ? <ResultTable columns={t.columns} rows={t.rows} panel={panel} capped={t.capped} /> : <p className="text-sm text-muted-foreground">No data.</p>
    }
  }

  return (
    <Frame
      panel={panel}
      compact={isStat}
      className={isStat ? 'justify-between' : undefined}
      action={
        isChart && hasSeries && !error ? (
          <Button variant="ghost" size="sm" className="h-7 text-muted-foreground" onClick={() => setAsTable(!asTable)}>
            {asTable ? <TrendingUp /> : <Table2 />} {asTable ? 'Show chart' : 'Show as table'}
          </Button>
        ) : undefined
      }
    >
      {body}
    </Frame>
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

const th = 'h-9 px-3 text-left align-middle text-xs font-medium whitespace-nowrap text-muted-foreground'
const td = 'px-3 py-2 align-middle whitespace-nowrap'

function ResultTable({ columns, rows, panel, capped }: { columns: Column[]; rows: unknown[][]; panel: PanelDef; capped: boolean }) {
  if (!rows.length) return <p className="py-6 text-center text-sm text-muted-foreground">No rows in this time range.</p>
  const fmt = columns.map((c) => columnUnit(panel, c.name))
  return (
    <ScrollRegion label={`${panel.title ?? 'Panel'}, table`} className="max-h-96 rounded-lg border">
      <table className="w-full text-sm">
        <thead className="sticky top-0 border-b bg-muted/60 backdrop-blur">
          <tr>
            {columns.map((c) => (
              <th key={c.name} scope="col" className={cn(th, c.type === 'number' && 'text-right')}>
                {c.name}
              </th>
            ))}
          </tr>
        </thead>
        <tbody>
          {rows.map((r, i) => (
            <tr key={i} className="border-b last:border-0 hover:bg-muted/40">
              {r.map((v, j) => (
                <td key={j} className={cn(td, columns[j]!.type === 'number' && 'text-right tabular-nums')}>
                  {columns[j]!.type === 'time' && typeof v === 'string' ? new Date(v).toLocaleString() : formatValue(v, fmt[j]!.unit, fmt[j]!.decimals)}
                </td>
              ))}
            </tr>
          ))}
        </tbody>
      </table>
      {capped && <p className="border-t p-2 text-xs text-muted-foreground">Showing the first {rows.length} rows.</p>}
    </ScrollRegion>
  )
}

/** The table view of a chart: one row per time, one column per series. */
function SeriesTable({ series, unit }: { series: { name: string; points: (number | null)[][] }[]; unit?: string }) {
  const times = [...new Set(series.flatMap((s) => s.points.map((p) => p[0] as number)))].sort((a, b) => a - b)
  const lookup = series.map((s) => new Map(s.points.map((p) => [p[0], p[1]])))
  return (
    <ScrollRegion label="Chart data, table" className="max-h-96 rounded-lg border">
      <table className="w-full text-sm">
        <thead className="sticky top-0 border-b bg-muted/60 backdrop-blur">
          <tr>
            <th scope="col" className={th}>
              Time
            </th>
            {series.map((s) => (
              <th key={s.name} scope="col" className={cn(th, 'text-right')}>
                {s.name}
              </th>
            ))}
          </tr>
        </thead>
        <tbody>
          {times.map((t) => (
            <tr key={t} className="border-b last:border-0">
              <td className={td}>{new Date(t).toLocaleString()}</td>
              {lookup.map((m, i) => (
                <td key={i} className={cn(td, 'text-right tabular-nums')}>
                  {formatValue(m.get(t) ?? null, unit)}
                </td>
              ))}
            </tr>
          ))}
        </tbody>
      </table>
    </ScrollRegion>
  )
}
