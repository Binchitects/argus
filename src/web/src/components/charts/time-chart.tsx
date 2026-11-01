import { BarChart, LineChart } from 'echarts/charts'
import { AxisPointerComponent, GridComponent, LegendComponent, TooltipComponent } from 'echarts/components'
import * as echarts from 'echarts/core'
import { SVGRenderer } from 'echarts/renderers'
import { useEffect, useMemo, useRef } from 'react'
import { formatValue, when } from '@/lib/format'
import { useTheme } from '@/lib/theme'
import { alignForStack, foldBy, foldSeries, type ChartSeries } from './fold'
import { legendNameWidth } from './legend'
import { cssColor, seriesColors } from './palette'

echarts.use([BarChart, LineChart, GridComponent, TooltipComponent, LegendComponent, AxisPointerComponent, SVGRenderer])

/** The band's two lines behind a folded average: no legend entry, no tooltip row of their own. */
const bandOf = (name: string) => `\u0000band ${name}`

interface TipParam {
  seriesName?: string
  marker?: unknown
  value?: unknown
}

/**
 * Time on x, one value axis (never two). Stacked bars or lines, a crosshair
 * tooltip, and a legend whenever there is more than one series. At most eight
 * lines: the series highest on average keep theirs, the rest are one line
 * (summed, or averaged where a sum means nothing, with a faint band from the
 * lowest to the highest of them and that range in the tooltip).
 */
export function TimeChart({ series: raw, unit, units, stacked, bars, height = 260, label, from, to, fixed }: {
  series: ChartSeries[]
  /** A query's own unit, by its group (a GPU's temperature and its fan in one panel), where it is not `unit`. */
  units?: Record<string, string | undefined>
  /** Series whose colour is a state, not an identity (a log level): name to CSS custom property. */
  fixed?: Record<string, string>
  /** The selected range, epoch ms: the axis spans it even where there is no data. */
  from?: number
  to?: number
  unit?: string
  stacked?: boolean
  bars?: boolean
  height?: number
  label: string
}) {
  const ref = useRef<HTMLDivElement>(null)
  // The text, grid and tooltip colours are read again as the theme or the colour theme changes.
  const { resolved, shownColorTheme } = useTheme()
  const series = useMemo(() => {
    const unitOf = (group: string | undefined) => (group !== undefined && units?.[group]) || unit
    const folded = foldSeries(raw, 8, (group) => foldBy(unitOf(group), stacked), unitOf)
    return stacked ? alignForStack(folded) : folded
  }, [raw, stacked, unit, units])

  useEffect(() => {
    const el = ref.current
    if (!el) return
    const chart = echarts.init(el, undefined, { renderer: 'svg' })
    const colors = seriesColors(series.map((s) => s.name), resolved)
    for (const [name, token] of Object.entries(fixed ?? {})) colors.set(name, cssColor(token, colors.get(name) ?? '#888'))
    const text = cssColor('--muted-foreground', '#62656d')
    const grid = cssColor('--border', '#e2e3e7')
    const surface = cssColor('--popover', '#ffffff')
    const ink = cssColor('--popover-foreground', '#1b1c1f')
    const tip = { backgroundColor: surface, borderColor: grid, textStyle: { color: ink, fontSize: 12 }, extraCssText: 'box-shadow: 0 8px 24px rgb(0 0 0 / 0.12); border-radius: 8px;' }
    const unitOf = new Map(series.map((s) => [s.name, (s.group !== undefined && units?.[s.group]) || unit]))
    const ranges = new Map(series.filter((s) => s.range).map((s) => [s.name, new Map(s.range!.map(([t, low, high]) => [t, [low, high]]))]))
    const banded = bars || stacked ? [] : series.filter((s) => s.range?.length)
    const esc = echarts.format.encodeHTML
    // The tooltip's rows: each line's value in its own unit, a folded average with the range it covers.
    const formatter = (params: TipParam | TipParam[]) => {
      const rows = (Array.isArray(params) ? params : [params]).filter((p) => p.seriesName && !p.seriesName.startsWith('\u0000'))
      if (!rows.length) return ''
      const at = (rows[0]!.value as number[] | undefined)?.[0]
      const lines = rows.map((p) => {
        const [t, v] = (p.value as (number | null)[]) ?? []
        const u = unitOf.get(p.seriesName!)
        const r = t != null ? ranges.get(p.seriesName!)?.get(t) : undefined
        const spread = r ? ` <span style="opacity:.7">(${esc(formatValue(r[0], u))} to ${esc(formatValue(r[1], u))})</span>` : ''
        return `<div style="display:flex;gap:12px;justify-content:space-between"><span>${typeof p.marker === 'string' ? p.marker : ''}${esc(p.seriesName!)}</span><span><b>${esc(formatValue(v, u))}</b>${spread}</span></div>`
      })
      return `${typeof at === 'number' ? `<div style="margin-bottom:4px;opacity:.7">${esc(when(at / 1000))}</div>` : ''}${lines.join('')}`
    }
    chart.setOption({
      animation: false,
      textStyle: { fontFamily: 'Inter Variable, ui-sans-serif, system-ui, sans-serif' },
      grid: { left: 8, right: 16, top: 16, bottom: series.length > 1 ? 36 : 12, containLabel: true },
      tooltip: {
        trigger: 'axis',
        axisPointer: { type: bars ? 'shadow' : 'line' },
        formatter,
        ...tip,
      },
      // One line that pages: a legend never grows over the plot. Two names always fit on it, beside the pager, a long one cut short.
      legend: series.length > 1 ? { data: series.map((s) => s.name), type: 'scroll', bottom: 0, textStyle: { color: text, width: legendNameWidth(el.clientWidth, series.length), overflow: 'truncate' }, tooltip: { show: true, ...tip }, icon: 'roundRect', itemWidth: 12, itemHeight: 8, pageTextStyle: { color: text } } : undefined,
      xAxis: { type: 'time', min: from, max: to, axisLine: { lineStyle: { color: grid } }, axisLabel: { color: text, hideOverlap: true }, splitLine: { show: false } },
      yAxis: { type: 'value', axisLabel: { color: text, formatter: (v: number) => formatValue(v, unit) }, splitLine: { lineStyle: { color: grid } } },
      series: series.map((s) => ({
        name: s.name,
        type: bars ? 'bar' : 'line',
        stack: stacked ? 'all' : undefined,
        data: s.points,
        color: colors.get(s.name),
        showSymbol: false,
        lineStyle: { width: 2 },
        barMaxWidth: 28,
        // A surface gap between stacked segments.
        itemStyle: bars ? { borderColor: surface, borderWidth: stacked ? 1 : 0, borderRadius: stacked ? 0 : [3, 3, 0, 0] } : undefined,
        emphasis: { focus: 'series' },
      })).concat(banded.flatMap((s) => {
        // From the lowest to the highest of the folded series: an invisible line at the low, and the gap above it filled.
        const band = { type: 'line' as const, stack: bandOf(s.name), showSymbol: false, silent: true, lineStyle: { opacity: 0 }, color: colors.get(s.name), z: 1 }
        return [
          { ...band, name: bandOf(s.name), data: s.range!.map(([t, low]) => [t, low]) },
          { ...band, name: `${bandOf(s.name)} high`, data: s.range!.map(([t, low, high]) => [t, high! - low!]), areaStyle: { color: colors.get(s.name), opacity: 0.14 } },
        ] as never[]
      })),
    })
    const resize = new ResizeObserver(() => {
      if (series.length > 1) chart.setOption({ legend: { textStyle: { width: legendNameWidth(el.clientWidth, series.length) } } })
      chart.resize()
    })
    resize.observe(el)
    return () => {
      resize.disconnect()
      chart.dispose()
    }
  }, [series, unit, units, stacked, bars, from, to, resolved, shownColorTheme, fixed])

  // oxlint-disable-next-line jsx-a11y/prefer-tag-over-role -- ECharts draws into a div; the table view carries the data
  return <div ref={ref} style={{ height }} role="img" aria-label={label} className="w-full" />
}
