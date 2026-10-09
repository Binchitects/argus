import { keepPreviousData, useQuery } from '@tanstack/react-query'
import DOMPurify from 'dompurify'
import { Info, Table2, TrendingUp } from 'lucide-react'
import { marked } from 'marked'
import { useState, type ReactNode } from 'react'
import { Button } from '@/components/ui/button'
import { DataTable, type ColumnDef } from '@/components/ui/data-table'
import { Skeleton } from '@/components/ui/skeleton'
import { Tooltip } from '@/components/ui/tooltip'
import { ScrollRegion } from '@/components/app/scroll-region'
import { TimeChart } from '@/components/charts/time-chart'
import { api, errorMessage } from '@/lib/api'
import { formatValue } from '@/lib/format'
import { intervalFor, resolve, type TimeRange } from '@/lib/time'
import { cn } from '@/lib/utils'
import { LogsPanel } from './logs-panel'
import { reduceEach } from './reduce'
import type { Column, PanelData, PanelDef } from './types'
import type { Chosen } from './variables'
import { thresholdColor, tone } from './colors'
import { ems, fitting } from './fit'
import { Gauge, Sparkline } from './visuals'

/** level: its title's heading level (3 under a row's heading, 2 when the panel has none above it). */
export function PanelView({ uid, panel, range, tick, vars = {}, level = 3 }: { uid: string; panel: PanelDef; range: TimeRange; tick: number; vars?: Chosen; level?: 2 | 3 }) {
  if (panel.type === 'text') {
    return (
      <Frame panel={panel} level={level}>
        <div
          className="text-sm break-words text-muted-foreground [&_a]:text-primary-ink [&_a]:underline [&_code]:font-mono [&_code]:break-all [&_p]:mb-2 [&_pre]:whitespace-pre-wrap [&_pre]:break-all [&_strong]:text-foreground [&_ul]:list-disc [&_ul]:pl-5"
          // The dashboard files are ours; sanitised anyway, so a file can never run script here.
          dangerouslySetInnerHTML={{ __html: DOMPurify.sanitize(marked.parse(panel.options?.content ?? '', { async: false })) }}
        />
      </Frame>
    )
  }
  return <QueryPanel uid={uid} panel={panel} range={range} tick={tick} vars={vars} level={level} />
}

function Frame({ panel, action, children, className, compact, level = 3 }: { panel: PanelDef; action?: ReactNode; children: ReactNode; className?: string; compact?: boolean; level?: 2 | 3 }) {
  const Heading = level === 2 ? 'h2' : 'h3'
  return (
    <section data-panel className={cn('@container flex h-full min-w-0 flex-col gap-3 rounded-xl border bg-card p-4 shadow-xs', className)} aria-label={panel.title}>
      {(panel.title || action) && (
        <header className="flex min-h-7 items-start justify-between gap-2">
          <Heading className={cn('flex min-w-0 items-start gap-1.5 font-semibold', compact ? 'text-xs text-muted-foreground' : 'text-sm')}>
            {/* A stat's title wraps to two lines rather than losing its end in a narrow cell; a word too long for
                the cell breaks rather than being clipped with no "…". Whole on hover either way. */}
            <span className={compact ? 'line-clamp-2 break-words' : 'truncate'} title={panel.title}>
              {panel.title}
            </span>
            {panel.description && (
              <Tooltip content={panel.description}>
                <button type="button" className="mt-0.5 shrink-0 rounded text-muted-foreground outline-none focus-visible:ring-[3px] focus-visible:ring-ring" aria-label={`About ${panel.title}`}>
                  <Info className="size-3.5" />
                </button>
              </Tooltip>
            )}
          </Heading>
          {action}
        </header>
      )}
      <div className="min-h-0 min-w-0 flex-1">{children}</div>
    </section>
  )
}

function QueryPanel({ uid, panel, range, tick, vars, level }: { uid: string; panel: PanelDef; range: TimeRange; tick: number; vars: Chosen; level: 2 | 3 }) {
  const [asTable, setAsTable] = useState(false)
  const data = useQuery({
    queryKey: ['panel', uid, panel.key, range.from, range.to, tick, JSON.stringify(vars)],
    enabled: panel.supported,
    // A refresh keeps what is on screen until the new answer is in.
    placeholderData: keepPreviousData,
    queryFn: ({ signal }) => {
      const from = resolve(range.from)
      const to = resolve(range.to)
      // As Grafana: roughly one bucket per 3 px of a full-width panel.
      const points = Math.round(((panel.gridPos?.w ?? 24) / 24) * 400)
      return api<PanelData>(`/api/dashboards/${uid}/panels/${panel.key}/query`, {
        // The dashboard's variables, when it has any.
        body: { from: from.toISOString(), to: to.toISOString(), intervalMs: intervalFor(from, to, points), ...(Object.keys(vars).length ? { vars } : {}) },
        signal,
      })
    },
  })
  const defaults = panel.fieldConfig?.defaults
  const unit = defaults?.unit
  const decimals = defaults?.decimals
  const error = data.error ? errorMessage(data.error) : data.data?.results.find((r) => r.error)?.error
  const isChart = panel.type === 'timeseries' || panel.type === 'barchart'
  const isStat = panel.type === 'stat' || panel.type === 'gauge'
  const hasSeries = (data.data?.results ?? []).some((r) => (r.series?.length ?? 0) > 0)

  let body: ReactNode = null
  if (!panel.supported) {
    body = <p className="text-sm text-muted-foreground">This panel reads {panel.datasources.join(', ')}, which the app does not serve.</p>
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
    const logs = results.flatMap((r) => r.logs ?? [])
    if (panel.type === 'logs') {
      const o = panel.options
      body = (
        <LogsPanel
          lines={logs}
          label={panel.title ?? 'Logs'}
          showTime={o?.showTime !== false}
          showLabels={o?.showLabels === true}
          wrap={o?.wrapLogMessage !== false}
          ascending={o?.sortOrder === 'Ascending'}
          details={o?.enableLogDetails !== false}
          highlight={vars.search?.[0] || undefined}
        />
      )
    } else if (isStat) {
      const values = reduceEach(results, panel.options?.reduceOptions?.calcs)
      const steps = defaults?.thresholds?.steps
      const fixed = defaults?.color?.mode === 'fixed' ? defaults.color.fixedColor : undefined
      if (!values.length || values.every((v) => v.value === null)) {
        body = <div className="text-2xl font-semibold text-muted-foreground">{defaults?.noValue ?? 'No data'}</div>
      } else if (panel.type === 'gauge') {
        const min = defaults?.min ?? 0
        const max = defaults?.max ?? (unit === 'percentunit' ? 1 : 100)
        // A gauge per series (a GPU each). Several are drawn small, as many to a line as fit, and past two
        // they scroll at a chart's height as a stat's values do: a machine with many GPUs keeps its row one height.
        const several = values.length > 1
        const em = Math.max(...values.map((v) => ems(formatValue(v.value, unit, decimals))))
        const gauges = (
          <div className={cn('grid gap-2', several && 'grid-cols-[repeat(auto-fit,minmax(min(4.5rem,100%),1fr))]')}>
            {values.map((v) => (
              <div key={v.name} className="grid min-w-0 justify-items-center">
                <Gauge value={v.value} min={min} max={max} steps={steps} unit={unit} decimals={decimals} label={several ? v.name : (panel.title ?? v.name)} small={several} em={em} />
                {several && (
                  <p className="line-clamp-2 max-w-full text-center text-xs break-words text-muted-foreground" title={v.name}>
                    {v.name}
                  </p>
                )}
              </div>
            ))}
          </div>
        )
        body =
          values.length > 2 ? (
            <ScrollRegion label={`${panel.title ?? 'Panel'}, every value`} className="lg:max-h-65">
              {gauges}
            </ScrollRegion>
          ) : (
            gauges
          )
      } else {
        // A value per series. Many of them (a sensor per core) must not make the row taller than a chart
        // beside it: past two they drop their sparklines, and on a wide screen the list scrolls at a chart's height.
        const many = values.length > 2
        const text = (v: (typeof values)[number]) => (v.value === null ? (defaults?.noValue ?? '—') : formatValue(v.value, unit, decimals))
        const em = Math.max(...values.map((v) => ems(text(v))))
        const list = (
          <div className={cn('grid gap-3', values.length > 1 && 'grid-cols-[repeat(auto-fit,minmax(min(7rem,100%),1fr))]')}>
            {values.map((v) => {
              const t = tone(fixed ?? thresholdColor(v.value, steps))
              const background = panel.options?.colorMode === 'background' && t
              return (
                // Its own width for the value to fit (@container): a column narrower than the number shrinks the number,
                // and every value of the panel by as much, so they stay one size.
                <div key={v.name} className={cn('grid min-w-0 gap-1 rounded-lg @container', background && `${t.fill} px-3 py-2`)}>
                  {/* Two lines before it is cut, and whole on hover: a narrow cell keeps more than the start of a name. */}
                  {values.length > 1 && (
                    <p className="line-clamp-2 text-xs break-words text-muted-foreground" title={v.name}>
                      {v.name}
                    </p>
                  )}
                  <div
                    className={cn(
                      'text-[length:min(1.5rem,var(--fit))] leading-[1.333] font-semibold tracking-tight whitespace-nowrap tabular-nums xl:text-[length:min(1.75rem,var(--fit))]',
                      panel.options?.colorMode !== 'none' && t?.ink,
                    )}
                    style={fitting(em, '100cqi')}
                    aria-label={values.length > 1 ? `${panel.title}: ${v.name}` : panel.title}
                  >
                    {text(v)}
                  </div>
                  {!many && panel.options?.graphMode === 'area' && v.points.length > 1 && <Sparkline points={v.points} className={t?.ink ?? 'text-primary'} />}
                </div>
              )
            })}
          </div>
        )
        body = many ? (
          <ScrollRegion label={`${panel.title ?? 'Panel'}, every value`} className="lg:max-h-65">
            {list}
          </ScrollRegion>
        ) : (
          list
        )
      }
    } else if (isChart) {
      const series = results.flatMap((r) => r.series ?? [])
      const custom = panel.fieldConfig?.defaults?.custom
      body = !series.length ? (
        <p className="py-10 text-center text-sm text-muted-foreground">{defaults?.noValue ?? 'No data in this time range.'}</p>
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
      const shaped = t ? organize(t.columns, t.rows, panel) : null
      body = shaped && t ? <ResultTable columns={shaped.columns} rows={shaped.rows} panel={panel} capped={t.capped} /> : <p className="text-sm text-muted-foreground">No data.</p>
    }
  }

  return (
    <Frame
      panel={panel}
      level={level}
      compact={isStat}
      className={isStat ? 'justify-between' : undefined}
      action={
        isChart && hasSeries && !error ? (
          // In a narrow panel only its icon, so the title beside it is not cut short.
          <Tooltip content={asTable ? 'Show chart' : 'Show as table'}>
            <Button variant="ghost" size="sm" className="h-7 text-muted-foreground" onClick={() => setAsTable(!asTable)} aria-label={asTable ? 'Show chart' : 'Show as table'}>
              {asTable ? <TrendingUp /> : <Table2 />} <span className="hidden @sm:inline">{asTable ? 'Show chart' : 'Show as table'}</span>
            </Button>
          </Tooltip>
        ) : undefined
      }
    >
      {body}
    </Frame>
  )
}

/** Grafana's "organize fields" transformation: columns left out, renamed and put in order. */
function organize(columns: Column[], rows: unknown[][], panel: PanelDef): { columns: Column[]; rows: unknown[][] } {
  const o = panel.transformations?.find((t) => t.id === 'organize')?.options
  if (!o) return { columns, rows }
  const keep = columns.map((c, i) => ({ c, i })).filter(({ c }) => !o.excludeByName?.[c.name])
  keep.sort((a, b) => (o.indexByName?.[a.c.name] ?? 999 + a.i) - (o.indexByName?.[b.c.name] ?? 999 + b.i))
  return {
    columns: keep.map(({ c }) => ({ ...c, name: o.renameByName?.[c.name] || c.name })),
    rows: rows.map((r) => keep.map(({ i }) => r[i])),
  }
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

const right = { className: 'text-right tabular-nums' }

function ResultTable({ columns, rows, panel, capped }: { columns: Column[]; rows: unknown[][]; panel: PanelDef; capped: boolean }) {
  if (!rows.length) return <p className="py-6 text-center text-sm text-muted-foreground">No rows in this time range.</p>
  const fmt = columns.map((c) => columnUnit(panel, c.name))
  const defs: ColumnDef<unknown[]>[] = columns.map((c, j) => ({
    id: `c${j}`,
    header: c.name,
    accessorFn: (r) => r[j] ?? null,
    meta: c.type === 'number' ? right : { className: 'whitespace-nowrap' },
    cell: ({ row }) => {
      const v = row.original[j]
      return c.type === 'time' && typeof v === 'string' ? new Date(v).toLocaleString() : formatValue(v, fmt[j]!.unit, fmt[j]!.decimals)
    },
  }))
  return (
    <DataTable
      columns={defs}
      data={rows}
      noun="rows"
      label={panel.title ?? 'Panel'}
      compact
      pageSize={500}
      scrollClassName="max-h-96"
      footer={capped ? `the first ${rows.length} only` : undefined}
    />
  )
}

/** The table view of a chart: one row per time, one column per series. */
function SeriesTable({ series, unit }: { series: { name: string; points: (number | null)[][] }[]; unit?: string }) {
  const times = [...new Set(series.flatMap((s) => s.points.map((p) => p[0] as number)))].sort((a, b) => a - b)
  const lookup = series.map((s) => new Map(s.points.map((p) => [p[0], p[1]])))
  const rows = times.map((t) => [t, ...lookup.map((m) => m.get(t) ?? null)])
  const defs: ColumnDef<(number | null)[]>[] = [
    { id: 'time', header: 'Time', accessorFn: (r) => r[0], meta: { className: 'whitespace-nowrap' }, cell: ({ row }) => new Date(row.original[0]!).toLocaleString() },
    ...series.map(
      (s, i): ColumnDef<(number | null)[]> => ({
        id: `s${i}`,
        header: s.name,
        accessorFn: (r) => r[i + 1],
        sortUndefined: 'last',
        meta: right,
        cell: ({ row }) => formatValue(row.original[i + 1] ?? null, unit),
      }),
    ),
  ]
  return <DataTable columns={defs} data={rows} noun="times" label="Chart data" compact pageSize={500} scrollClassName="max-h-96" />
}
