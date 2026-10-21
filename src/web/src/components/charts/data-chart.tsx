import { BarChart, LineChart } from 'echarts/charts'
import { AxisPointerComponent, GridComponent, LegendComponent, TooltipComponent } from 'echarts/components'
import * as echarts from 'echarts/core'
import { SVGRenderer } from 'echarts/renderers'
import { useEffect, useRef } from 'react'
import { formatValue } from '@/lib/format'
import { useTheme } from '@/lib/theme'
import { cn } from '@/lib/utils'
import type { Cell, ChartSpec, DataResult } from '@/pages/data/data-api'
import { legendNameWidth } from './legend'
import { cssColor, seriesColors } from './palette'
import { mostBars, planChart } from './data-plan'

echarts.use([BarChart, LineChart, GridComponent, TooltipComponent, LegendComponent, AxisPointerComponent, SVGRenderer])

// The rows and a single figure in full (12,345.67); the axis and its tooltip short (12.35 K).
const show = (v: Cell) => (typeof v === 'number' ? v.toLocaleString('en-US', { maximumFractionDigits: 2 }) : v === null ? '—' : String(v))

/** A query's result as its plan says: one figure, bars or lines (one value axis, a legend for more than one series), or the table. */
export function DataChart({ result, chart, label, height = 280 }: { result: DataResult; chart: ChartSpec | undefined; label: string; height?: number }) {
  const plan = planChart(result.columns, result.rows, chart)
  if (plan.type === 'number') {
    return (
      <figure className="flex flex-col items-start gap-1 py-4" aria-label={label}>
        <span className="text-4xl font-semibold tabular-nums">{show(plan.value)}</span>
        <figcaption className="text-sm text-muted-foreground">{plan.label}</figcaption>
      </figure>
    )
  }
  if (plan.type === 'table') return <ResultTable result={result} />
  return (
    <>
      <Plot result={result} type={plan.type} x={plan.x} y={plan.y} time={plan.time} label={label} height={height} />
      {plan.dropped > 0 && <p className="text-xs text-muted-foreground">The first {mostBars} of {result.rows.length}; the table has them all.</p>}
    </>
  )
}

function Plot({ result, type, x, y, time, label, height }: { result: DataResult; type: 'bar' | 'line'; x: number; y: number[]; time: boolean; label: string; height: number }) {
  const ref = useRef<HTMLDivElement>(null)
  const { resolved } = useTheme()

  useEffect(() => {
    const el = ref.current
    if (!el) return
    const chart = echarts.init(el, undefined, { renderer: 'svg' })
    const names = y.map((i) => result.columns[i]!.name)
    const colors = seriesColors(names, resolved)
    const text = cssColor('--muted-foreground', '#62656d')
    const grid = cssColor('--border', '#e2e3e7')
    const surface = cssColor('--popover', '#ffffff')
    const ink = cssColor('--popover-foreground', '#1b1c1f')
    const tip = { backgroundColor: surface, borderColor: grid, textStyle: { color: ink, fontSize: 12 }, extraCssText: 'box-shadow: 0 8px 24px rgb(0 0 0 / 0.12); border-radius: 8px;' }
    const rows = type === 'bar' && !time ? result.rows.slice(0, mostBars) : result.rows
    chart.setOption({
      animation: false,
      textStyle: { fontFamily: 'Inter Variable, ui-sans-serif, system-ui, sans-serif' },
      grid: { left: 8, right: 16, top: 16, bottom: names.length > 1 ? 36 : 12, containLabel: true },
      tooltip: { trigger: 'axis', axisPointer: { type: type === 'bar' ? 'shadow' : 'line' }, valueFormatter: (v: number) => formatValue(v), ...tip },
      legend: names.length > 1
        ? { type: 'scroll', bottom: 0, textStyle: { color: text, width: legendNameWidth(el.clientWidth, names.length), overflow: 'truncate' }, icon: 'roundRect', itemWidth: 12, itemHeight: 8, pageTextStyle: { color: text } }
        : undefined,
      xAxis: time
        ? { type: 'time', axisLine: { lineStyle: { color: grid } }, axisLabel: { color: text, hideOverlap: true }, splitLine: { show: false } }
        : { type: 'category', data: rows.map((r) => show(r[x] ?? null)), axisLine: { lineStyle: { color: grid } }, axisLabel: { color: text, hideOverlap: true, width: 120, overflow: 'truncate' } },
      yAxis: { type: 'value', axisLabel: { color: text, formatter: (v: number) => formatValue(v) }, splitLine: { lineStyle: { color: grid } } },
      series: y.map((i, n) => ({
        name: names[n],
        type,
        data: time ? rows.map((r) => [r[x], r[i]]) : rows.map((r) => r[i]),
        color: colors.get(names[n]!),
        showSymbol: rows.length <= 1,
        symbolSize: 8,
        lineStyle: { width: 2 },
        barMaxWidth: 28,
        itemStyle: type === 'bar' ? { borderColor: surface, borderWidth: names.length > 1 ? 1 : 0, borderRadius: [4, 4, 0, 0] } : undefined,
        emphasis: { focus: 'series' },
      })),
    })
    const resize = new ResizeObserver(() => chart.resize())
    resize.observe(el)
    return () => {
      resize.disconnect()
      chart.dispose()
    }
  }, [result, type, x, y, time, resolved])

  // oxlint-disable-next-line jsx-a11y/prefer-tag-over-role -- ECharts draws into a div; the table view carries the data
  return <div ref={ref} style={{ height }} role="img" aria-label={label} className="w-full" />
}

const MostShown = 200

/** The rows as a table: numbers to the right, the first 200 shown. */
export function ResultTable({ result, className }: { result: DataResult; className?: string }) {
  const shown = result.rows.slice(0, MostShown)
  return (
    <div className={cn('max-h-96 overflow-auto rounded-lg border', className)}>
      <table className="w-full text-sm">
        <thead className="sticky top-0 bg-muted/80 text-xs text-muted-foreground backdrop-blur">
          <tr>
            {result.columns.map((c) => (
              <th key={c.name} scope="col" className={cn('px-3 py-2 font-medium whitespace-nowrap', c.type === 'number' ? 'text-right' : 'text-left')}>
                {c.name}
              </th>
            ))}
          </tr>
        </thead>
        <tbody>
          {shown.map((r, i) => (
            <tr key={i} className="border-t">
              {r.map((v, j) => (
                <td key={j} className={cn('px-3 py-1.5', result.columns[j]?.type === 'number' ? 'text-right tabular-nums' : 'max-w-xs truncate')}>
                  {show(v)}
                </td>
              ))}
            </tr>
          ))}
        </tbody>
      </table>
      {(result.rows.length > MostShown || result.truncated) && (
        <p className="border-t px-3 py-2 text-xs text-muted-foreground">
          {result.rows.length > MostShown ? `The first ${MostShown} of ${result.rows.length} rows.` : ''} {result.truncated ? "There were more: the database's limit stopped them." : ''}
        </p>
      )}
    </div>
  )
}
