export interface ChartSeries {
  name: string
  points: (number | null)[][]
}

/** At most 8 colours: the 7 largest series by total keep theirs, the rest become "Other". */
export function foldSeries(series: ChartSeries[], max = 8): ChartSeries[] {
  if (series.length <= max) return series
  const total = (s: ChartSeries) => s.points.reduce((a, p) => a + (typeof p[1] === 'number' ? p[1] : 0), 0)
  const ranked = [...series].sort((a, b) => total(b) - total(a))
  const keep = ranked.slice(0, max - 1)
  const other = new Map<number, number>()
  for (const s of ranked.slice(max - 1)) for (const [t, v] of s.points) if (typeof t === 'number' && typeof v === 'number') other.set(t, (other.get(t) ?? 0) + v)
  // Keep the original order among the survivors, so a colour follows its series.
  const kept = series.filter((s) => keep.includes(s))
  return [...kept, { name: `Other (${series.length - kept.length})`, points: [...other].sort((a, b) => a[0] - b[0]) }]
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
