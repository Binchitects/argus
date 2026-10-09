export interface ChartSeries {
  name: string
  points: (number | null)[][]
}

/** Units whose series do not add up (a temperature, a share, a clock, a latency): their "Other" is an average, not a sum. */
const averaged = new Set(['celsius', 'fahrenheit', 'percent', 'percentunit', 'hertz', 'rotmhz', 's', 'ms', 'µs', 'ns'])

/** How a chart in this unit folds its smallest series: summed, unless adding them makes no sense and they are not stacked. */
export function foldBy(unit: string | undefined, stacked = false): 'sum' | 'mean' {
  return !stacked && unit !== undefined && averaged.has(unit) ? 'mean' : 'sum'
}

/**
 * At most 8 colours: the 7 largest series by total keep theirs, the rest become
 * "Other": their sum, or their average for `mean` (ten more cores at 50 °C are
 * not one line at 500 °C).
 */
export function foldSeries(series: ChartSeries[], max = 8, how: 'sum' | 'mean' = 'sum'): ChartSeries[] {
  if (series.length <= max) return series
  const total = (s: ChartSeries) => s.points.reduce((a, p) => a + (typeof p[1] === 'number' ? p[1] : 0), 0)
  const ranked = [...series].sort((a, b) => total(b) - total(a))
  const keep = ranked.slice(0, max - 1)
  const other = new Map<number, { sum: number; n: number }>()
  for (const s of ranked.slice(max - 1))
    for (const [t, v] of s.points) {
      if (typeof t !== 'number' || typeof v !== 'number') continue
      const at = other.get(t) ?? { sum: 0, n: 0 }
      other.set(t, { sum: at.sum + v, n: at.n + 1 })
    }
  // Keep the original order among the survivors, so a colour follows its series.
  const kept = series.filter((s) => keep.includes(s))
  const rest = series.length - kept.length
  const points = [...other].sort((a, b) => a[0] - b[0]).map(([t, { sum, n }]) => [t, how === 'mean' ? sum / n : sum])
  return [...kept, { name: how === 'mean' ? `Other (${rest}, average)` : `Other (${rest})`, points }]
}

/**
 * ECharts stacks by position in each series' data, not by time: series with
 * points on different days stack onto the wrong neighbours. For a stack, every
 * series gets every timestamp, 0 where it had none (nothing happened then).
 */
export function alignForStack(series: ChartSeries[]): ChartSeries[] {
  const times = [...new Set(series.flatMap((s) => s.points.map((p) => p[0]).filter((t): t is number => typeof t === 'number')))].sort((a, b) => a - b)
  return series.map((s) => {
    const byTime = new Map(s.points.map((p) => [p[0], p[1]]))
    return { name: s.name, points: times.map((t) => [t, byTime.get(t) ?? 0]) }
  })
}
