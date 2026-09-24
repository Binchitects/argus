import type { TargetResult } from './types'

/** The number a stat panel shows: Grafana's reduceOptions.calcs over the first numeric field. */
export function reduce(results: TargetResult[], calcs: string[] = ['lastNotNull']): number | null {
  const values: number[] = []
  for (const r of results) {
    if (r.table) {
      const col = r.table.columns.findIndex((c) => c.type === 'number')
      if (col >= 0) for (const row of r.table.rows) if (typeof row[col] === 'number') values.push(row[col] as number)
    } else if (r.series?.[0]) {
      for (const p of r.series[0].points) if (typeof p[1] === 'number') values.push(p[1])
    }
    if (values.length) break
  }
  if (!values.length) return null
  switch (calcs[0]) {
    case 'sum':
      return values.reduce((a, b) => a + b, 0)
    case 'mean':
      return values.reduce((a, b) => a + b, 0) / values.length
    case 'max':
      return Math.max(...values)
    case 'min':
      return Math.min(...values)
    case 'first':
    case 'firstNotNull':
      return values[0]
    case 'count':
      return values.length
    default:
      return values[values.length - 1]
  }
}
