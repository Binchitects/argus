export interface ChartSeries {
  name: string
  points: (number | null)[][]
  /** The query the series came from: series of two queries (a temperature and a power) are never folded together. */
  group?: string
  /** For a line that averages folded series: at each time, the lowest and the highest of them ([t, low, high]). */
  range?: number[][]
}

export type Fold = 'sum' | 'mean'

/** Units whose series do not add up (a temperature, a share, a clock, a latency): their "Other" is an average, not a sum. */
const averaged = new Set(['celsius', 'fahrenheit', 'percent', 'percentunit', 'hertz', 'rotmhz', 's', 'ms', 'µs', 'ns'])
const temperatures = new Set(['celsius', 'fahrenheit'])

/** How a chart in this unit folds its smallest series: summed, unless adding them makes no sense and they are not stacked. */
export function foldBy(unit: string | undefined, stacked = false): Fold {
  return !stacked && unit !== undefined && averaged.has(unit) ? 'mean' : 'sum'
}

/** A series' average over the range, of the times it has a value: what it is ranked by (a gap never makes it look cooler). */
function mean(s: ChartSeries): number {
  let sum = 0
  let n = 0
  for (const p of s.points)
    if (typeof p[1] === 'number') {
      sum += p[1]
      n++
    }
  return n ? sum / n : -Infinity
}

function peak(s: ChartSeries): number {
  return s.points.reduce((a, p) => (typeof p[1] === 'number' && p[1] > a ? p[1] : a), -Infinity)
}

/** What the series of a group are called in common ("temp " of "temp gpu0" and "temp gpu1"), cut at a word's end. */
function commonPrefix(names: string[]): string {
  if (names.length < 2) return ''
  let prefix = names[0]!
  for (const n of names) while (!n.startsWith(prefix)) prefix = prefix.slice(0, -1)
  const cut = prefix.lastIndexOf(' ')
  return cut > 0 ? prefix.slice(0, cut).trim() : ''
}

/** How many lines each group keeps: the eight shared out, a group that fits keeping all of it, and a folded one at least two (its highest and the rest). */
function share(sizes: number[], max: number): number[] {
  const out = sizes.map(() => 0)
  let left = max
  let open = sizes.map((_, i) => i)
  while (open.length) {
    const each = Math.max(2, Math.floor(left / open.length))
    const fits = open.filter((i) => sizes[i]! <= each)
    if (!fits.length) {
      for (const i of open) out[i] = each
      break
    }
    for (const i of fits) {
      out[i] = sizes[i]!
      left -= sizes[i]!
    }
    open = open.filter((i) => !fits.includes(i))
  }
  return out
}

/**
 * At most 8 lines (one colour each): the series highest on average over the range keep a line each (the hottest cores,
 * the busiest GPUs), and the rest become one line: their sum, or for `mean` their average, with the lowest and highest
 * of them beside it (ten more cores at 50 °C are not one line at 500 °C, and the band shows how far apart they are).
 * Series of different queries (a GPU's temperature and its power) fold apart, each its own lines.
 */
export function foldSeries(series: ChartSeries[], max = 8, how: Fold | ((group: string | undefined) => Fold) = 'sum', unit?: string | ((group: string | undefined) => string | undefined)): ChartSeries[] {
  if (series.length <= max) return series
  const groups = [...new Set(series.map((s) => s.group))]
  const slots = share(groups.map((g) => series.filter((s) => s.group === g).length), max)
  const folded = new Map<string | undefined, ChartSeries>()
  const kept = new Set<ChartSeries>()
  groups.forEach((group, gi) => {
    const members = series.filter((s) => s.group === group)
    if (members.length <= slots[gi]!) {
      for (const s of members) kept.add(s)
      return
    }
    const by = typeof how === 'function' ? how(group) : how
    const ranked = [...members].sort((a, b) => mean(b) - mean(a) || peak(b) - peak(a))
    for (const s of ranked.slice(0, slots[gi]! - 1)) kept.add(s)
    const rest = ranked.slice(slots[gi]! - 1)
    const at = new Map<number, { sum: number; n: number; low: number; high: number }>()
    for (const s of rest)
      for (const [t, v] of s.points) {
        if (typeof t !== 'number' || typeof v !== 'number') continue
        const was = at.get(t) ?? { sum: 0, n: 0, low: Infinity, high: -Infinity }
        at.set(t, { sum: was.sum + v, n: was.n + 1, low: Math.min(was.low, v), high: Math.max(was.high, v) })
      }
    const times = [...at].sort((a, b) => a[0] - b[0])
    const u = typeof unit === 'function' ? unit(group) : unit
    const lower = u !== undefined && temperatures.has(u) ? 'cooler' : 'lower'
    const prefix = groups.length > 1 ? commonPrefix(members.map((s) => s.name)) : ''
    const name = by === 'mean' ? `${rest.length} ${lower}, average` : `Other (${rest.length})`
    folded.set(group, {
      name: prefix ? `${prefix}: ${name}` : name,
      group,
      points: times.map(([t, x]) => [t, by === 'mean' ? x.sum / x.n : x.sum]),
      range: by === 'mean' ? times.map(([t, x]) => [t, x.low, x.high]) : undefined,
    })
  })
  // The survivors in their original order, so a colour follows its series; each group's folded line after them.
  const out = [...series.filter((s) => kept.has(s)), ...groups.flatMap((g) => (folded.has(g) ? [folded.get(g)!] : []))]
  // Two folded lines of one name (groups with no name in common): each named by its query too.
  const seen = new Map<string, number>()
  for (const s of out) seen.set(s.name, (seen.get(s.name) ?? 0) + 1)
  const mine = new Set(folded.values())
  return out.map((s) => (mine.has(s) && seen.get(s.name)! > 1 && s.group ? { ...s, name: `${s.name} (${s.group})` } : s))
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
    return { name: s.name, group: s.group, points: times.map((t) => [t, byTime.get(t) ?? 0]) }
  })
}
