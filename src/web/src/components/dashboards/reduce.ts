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

/** Grafana's reducers on one list of values. */
export function calc(values: number[], how = 'lastNotNull'): number | null {
  if (!values.length) return null
  switch (how) {
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
      return values[0]!
    case 'count':
      return values.length
    case 'range':
      return Math.max(...values) - Math.min(...values)
    case 'delta':
      return values[values.length - 1]! - values[0]!
    default:
      return values[values.length - 1]!
  }
}

/**
 * One value per series, as a stat or gauge panel shows several: its name, the
 * reduced value, and its points (for a sparkline). A table gives its first
 * numeric column, named by its first text column.
 */
export function reduceEach(results: TargetResult[], calcs: string[] = ['lastNotNull']): { name: string; value: number | null; points: (number | null)[][] }[] {
  const out: { name: string; value: number | null; points: (number | null)[][] }[] = []
  for (const r of results) {
    if (r.series) {
      for (const s of r.series) {
        const values = s.points.map((p) => p[1]).filter((v): v is number => typeof v === 'number')
        out.push({ name: s.name, value: calc(values, calcs[0]), points: s.points })
      }
    } else if (r.table) {
      const num = r.table.columns.findIndex((c) => c.type === 'number')
      const text = r.table.columns.findIndex((c) => c.type === 'string')
      if (num < 0) continue
      if (text < 0 || r.table.rows.length <= 1) {
        const values = r.table.rows.map((row) => row[num]).filter((v): v is number => typeof v === 'number')
        out.push({ name: r.table.columns[num]!.name, value: calc(values, calcs[0]), points: [] })
      } else {
        for (const row of r.table.rows) out.push({ name: String(row[text] ?? ''), value: typeof row[num] === 'number' ? (row[num] as number) : null, points: [] })
      }
    }
  }
  return out
}
