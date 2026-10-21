import type { Cell, ChartSpec, ResultColumn } from '@/pages/data/data-api'

/** How a result is drawn: one figure, bars or lines over x (one value axis, at most eight series), or a table. */
export type ChartPlan =
  | { type: 'number'; value: Cell; label: string }
  | { type: 'bar' | 'line'; x: number; y: number[]; time: boolean; dropped: number }
  | { type: 'table' }

const MostSeries = 8
const MostBars = 50

/**
 * The model's suggestion when the result fits it, else what the result's shape says: a single figure; time first and
 * numbers after, lines; a name first and numbers after, bars; anything else, a table.
 */
export function planChart(columns: ResultColumn[], rows: Cell[][], spec: ChartSpec | undefined): ChartPlan {
  if (rows.length === 0 || columns.length === 0 || spec?.type === 'table') return { type: 'table' }
  const numeric = columns.flatMap((c, i) => (c.type === 'number' ? [i] : []))
  if ((spec?.type === 'number' || (rows.length === 1 && columns.length === 1)) && numeric.length > 0) {
    const i = numeric[0]!
    return { type: 'number', value: rows[0]![i] ?? null, label: columns[i]!.name }
  }
  const named = (name: string | undefined) => (name ? columns.findIndex((c) => c.name === name) : -1)
  let x = named(spec?.x)
  if (x < 0) x = columns.findIndex((c) => c.type !== 'number')
  if (x < 0 || numeric.length === 0) return { type: 'table' }
  const wanted = (spec?.y ?? []).map(named).filter((i) => i >= 0 && i !== x && columns[i]!.type === 'number')
  const y = (wanted.length > 0 ? wanted : numeric.filter((i) => i !== x)).slice(0, MostSeries)
  if (y.length === 0) return { type: 'table' }
  const time = columns[x]!.type === 'date' || columns[x]!.type === 'time'
  const type = spec?.type === 'line' || spec?.type === 'bar' ? spec.type : time ? 'line' : 'bar'
  return { type, x, y, time, dropped: type === 'bar' && !time ? Math.max(0, rows.length - MostBars) : 0 }
}

export const mostBars = MostBars
