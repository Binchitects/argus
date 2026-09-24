import { BarChart, LineChart } from 'echarts/charts'
import { AxisPointerComponent, GridComponent, LegendComponent, TooltipComponent } from 'echarts/components'
import * as echarts from 'echarts/core'
import { SVGRenderer } from 'echarts/renderers'
import { useEffect, useMemo, useRef } from 'react'
import { formatValue } from '@/lib/format'
import { useTheme } from '@/lib/theme'
import { alignForStack, foldSeries, type ChartSeries } from './fold'
import { cssColor, seriesColors } from './palette'

echarts.use([BarChart, LineChart, GridComponent, TooltipComponent, LegendComponent, AxisPointerComponent, SVGRenderer])

/**
 * Time on x, one value axis (never two). Stacked bars or lines, a crosshair
 * tooltip, and a legend whenever there is more than one series. At most eight
 * series: the seven largest and "Other".
 */
export function TimeChart({ series: raw, unit, stacked, bars, height = 260, label, from, to }: {
  series: ChartSeries[]
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
  const { resolved } = useTheme()
  const series = useMemo(() => (stacked ? alignForStack(foldSeries(raw)) : foldSeries(raw)), [raw, stacked])

  useEffect(() => {
    if (!ref.current) return
    const chart = echarts.init(ref.current, undefined, { renderer: 'svg' })
    const colors = seriesColors(series.map((s) => s.name), resolved)
    const text = cssColor('--muted-foreground', '#62656d')
    const grid = cssColor('--border', '#e2e3e7')
    const surface = cssColor('--popover', '#ffffff')
    const ink = cssColor('--popover-foreground', '#1b1c1f')
    chart.setOption({
      animation: false,
      textStyle: { fontFamily: 'Inter Variable, ui-sans-serif, system-ui, sans-serif' },
      grid: { left: 8, right: 16, top: 16, bottom: series.length > 1 ? 36 : 12, containLabel: true },
      tooltip: {
        trigger: 'axis',
        axisPointer: { type: bars ? 'shadow' : 'line' },
        valueFormatter: (v: number) => formatValue(v, unit),
        backgroundColor: surface,
        borderColor: grid,
        textStyle: { color: ink, fontSize: 12 },
        extraCssText: 'box-shadow: 0 8px 24px rgb(0 0 0 / 0.12); border-radius: 8px;',
      },
      // One line that pages: a legend never grows over the plot.
      legend: series.length > 1 ? { type: 'scroll', bottom: 0, textStyle: { color: text }, icon: 'roundRect', itemWidth: 12, itemHeight: 8, pageTextStyle: { color: text } } : undefined,
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
      })),
    })
    const resize = new ResizeObserver(() => chart.resize())
    resize.observe(ref.current)
    return () => {
      resize.disconnect()
      chart.dispose()
    }
  }, [series, unit, stacked, bars, from, to, resolved])

  // oxlint-disable-next-line jsx-a11y/prefer-tag-over-role -- ECharts draws into a div; the table view carries the data
  return <div ref={ref} style={{ height }} role="img" aria-label={label} className="w-full" />
}
