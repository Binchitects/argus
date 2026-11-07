/** A time range as the dashboards use it: Grafana-style relative strings or absolute times. */
export interface TimeRange {
  from: string
  to: string
}

export const presets: { label: string; from: string }[] = [
  { label: 'Last 24 hours', from: 'now-24h' },
  { label: 'Last 7 days', from: 'now-7d' },
  { label: 'Last 30 days', from: 'now-30d' },
  { label: 'Last 90 days', from: 'now-90d' },
]

/** The ranges for live metrics and logs: minutes to a month. */
export const metricPresets: { label: string; from: string }[] = [
  { label: 'Last 5 minutes', from: 'now-5m' },
  { label: 'Last 15 minutes', from: 'now-15m' },
  { label: 'Last hour', from: 'now-1h' },
  { label: 'Last 6 hours', from: 'now-6h' },
  { label: 'Last 24 hours', from: 'now-24h' },
  { label: 'Last 7 days', from: 'now-7d' },
  { label: 'Last 30 days', from: 'now-30d' },
]

/** The ranges a dashboard offers: five minutes to three months. */
export const dashboardPresets: { label: string; from: string }[] = [...metricPresets, { label: 'Last 90 days', from: 'now-90d' }]

/** A preset that does not end now (last month) names its end too. */
export interface Preset {
  label: string
  from: string
  to?: string
}

/**
 * The ranges a report offers (usage, a project, the usage by group): the last day to the last year, and the calendar's
 * today, this week, this month and last month (Grafana's rounding: now/d is the day's start; as an end, its end).
 */
export const reportPresets: Preset[] = [
  { label: 'Last 24 hours', from: 'now-24h' },
  { label: 'Last 7 days', from: 'now-7d' },
  { label: 'Last 30 days', from: 'now-30d' },
  { label: 'Last 90 days', from: 'now-90d' },
  { label: 'Last year', from: 'now-1y' },
  { label: 'Today', from: 'now/d' },
  { label: 'This week', from: 'now/w' },
  { label: 'This month', from: 'now/M' },
  { label: 'Last month', from: 'now-1M/M', to: 'now-1M/M' },
]

/** The ranges a dashboard offers: its live ones, then the report ones it lacks. */
export const dashboardWindowPresets: Preset[] = [...dashboardPresets, ...reportPresets.filter((p) => !dashboardPresets.some((m) => m.from === p.from))]

/** The longest range the server reads at once (DashboardEndpoints.MaxRange). */
export const maxRangeDays = 400

/** A dashboard's range when neither the address nor its file says another. */
export const defaultRange: TimeRange = { from: 'now-1h', to: 'now' }

/** A range that can be drawn: both ends read as times, the start before the end. */
function readable(r: TimeRange): boolean {
  try {
    return resolve(r.from).getTime() < resolveEnd(r.to).getTime()
  } catch {
    return false
  }
}

/** A range as two times: its start, and its end (a rounded end or a day is that period's end). */
export function resolveRange(r: TimeRange, now: Date = new Date()): { from: Date; to: Date } {
  return { from: resolve(r.from, now), to: resolveEnd(r.to, now) }
}

/**
 * A dashboard's range: the address's (Grafana's from and to, so a link opens the
 * same view), else the dashboard file's, else the last hour.
 */
export function dashboardRange(params: URLSearchParams, own?: TimeRange | null): TimeRange {
  const asked = params.get('from')
  if (asked) {
    const r = { from: asked, to: params.get('to') || 'now' }
    if (readable(r)) return r
  }
  return own && readable(own) ? { from: own.from, to: own.to } : defaultRange
}

/** A range as one value for a menu: "now-6h", or "from|to" when it does not end now. */
export const rangeKey = (r: TimeRange) => (r.to === 'now' ? r.from : `${r.from}|${r.to}`)

export function fromRangeKey(key: string): TimeRange {
  const [from = '', to = 'now'] = key.split('|')
  return { from, to }
}

/** The dashboard's ranges for a menu, and the one shown now when it is not one of them (from a link). */
export function dashboardRangeOptions(current: TimeRange): { value: string; label: string }[] {
  const options = dashboardPresets.map((p) => ({ value: p.from, label: p.label }))
  const key = rangeKey(current)
  return options.some((o) => o.value === key) ? options : [...options, { value: key, label: rangeLabel(current) }]
}

/** "30s", "1m", "5m" (a dashboard's refresh) in milliseconds, or null. */
export function refreshMs(text: string | undefined | null): number | null {
  const m = /^(\d+)([smhd])$/.exec(text ?? '')
  return m ? Number(m[1]) * ({ s: 1e3, m: 6e4, h: 3.6e6, d: 8.64e7 } as Record<string, number>)[m[2]!]! : null
}

const units: Record<string, number> = { s: 1e3, m: 6e4, h: 3.6e6, d: 8.64e7, w: 6.048e8, M: 2.592e9, y: 3.1536e10 }

/** The start of the day, week (from Monday), month or year a time is in, in the browser's time zone; or the next one's start. */
function roundTo(d: Date, unit: string, up: boolean): Date {
  const r = new Date(d)
  r.setHours(0, 0, 0, 0)
  if (unit === 'w') r.setDate(r.getDate() - ((r.getDay() + 6) % 7))
  if (unit === 'M' || unit === 'y') r.setDate(1)
  if (unit === 'y') r.setMonth(0)
  if (up) {
    if (unit === 'd') r.setDate(r.getDate() + 1)
    else if (unit === 'w') r.setDate(r.getDate() + 7)
    else if (unit === 'M') r.setMonth(r.getMonth() + 1)
    else r.setFullYear(r.getFullYear() + 1)
  }
  return r
}

/**
 * "now", "now-30d", a rounded "now/d", "now/w", "now/M" or "now-1M/M" (Grafana's: the period's start; as an end, with
 * up, its end), a day "2026-11-01" (its start, or with up its end, in the browser's time zone), an ISO time, or
 * milliseconds since 1970 (as Grafana's links have them), as a Date.
 */
export function resolve(t: string, now: Date = new Date(), up = false): Date {
  if (t === 'now') return now
  const m = /^now-(\d+)([smhdwMy])$/.exec(t)
  if (m) return new Date(now.getTime() - Number(m[1]) * units[m[2]!]!)
  const rounded = /^now(?:-(\d+)([smhdwMy]))?\/([dwMy])$/.exec(t)
  if (rounded) {
    const back = new Date(now)
    const n = Number(rounded[1] ?? 0)
    if (rounded[2] === 'M') back.setMonth(back.getMonth() - n)
    else if (rounded[2] === 'y') back.setFullYear(back.getFullYear() - n)
    else if (rounded[2]) back.setTime(back.getTime() - n * units[rounded[2]]!)
    return roundTo(back, rounded[3]!, up)
  }
  if (/^\d{4}-\d{2}-\d{2}$/.test(t)) {
    const day = new Date(`${t}T00:00:00`)
    if (Number.isNaN(day.getTime())) throw new Error(`Not a time: ${t}`)
    if (up) day.setDate(day.getDate() + 1)
    return day
  }
  const d = /^\d{10,}$/.test(t) ? new Date(Number(t)) : new Date(t)
  if (Number.isNaN(d.getTime())) throw new Error(`Not a time: ${t}`)
  return d
}

/** A range's end as a time: a rounded end or a day is that period's end. */
export const resolveEnd = (t: string, now: Date = new Date()) => resolve(t, now, true)

/** A day as the date inputs have it (2026-11-07), in the browser's time zone. */
export function dayOf(d: Date): string {
  return `${d.getFullYear()}-${String(d.getMonth() + 1).padStart(2, '0')}-${String(d.getDate()).padStart(2, '0')}`
}

/** Grafana's rangeutil.roundInterval, so a panel asks the server for the bucket Grafana would. */
export function roundInterval(ms: number): number {
  const table: [number, number][] = [
    [15, 10], [35, 20], [75, 50], [150, 100], [350, 200], [750, 500], [1500, 1000], [3500, 2000],
    [7500, 5000], [12500, 10000], [17500, 15000], [25000, 20000], [45000, 30000], [90000, 60000],
    [210000, 120000], [450000, 300000], [750000, 600000], [1050000, 900000], [1500000, 1200000],
    [2700000, 1800000], [5400000, 3600000], [9000000, 7200000], [16200000, 10800000], [32400000, 21600000],
    [86400000, 43200000], [604800000, 86400000], [1814400000, 604800000], [3628800000, 2592000000],
  ]
  for (const [below, rounded] of table) if (ms < below) return rounded
  return 31536000000
}

export function intervalFor(from: Date, to: Date, points: number): number {
  return roundInterval((to.getTime() - from.getTime()) / Math.max(10, points))
}

const unitNames: Record<string, string> = { s: 'second', m: 'minute', h: 'hour', d: 'day', w: 'week', M: 'month', y: 'year' }

/** "Last hour", "Last 30 minutes", or the two times of a range that does not end now (the day once when both are on it). */
export function rangeLabel(r: TimeRange): string {
  const named = [...presets, ...dashboardPresets].find((p) => p.from === r.from && r.to === 'now')?.label
  if (named) return named
  const preset = reportPresets.find((p) => p.from === r.from && (p.to ?? 'now') === r.to)?.label
  if (preset) return preset
  // Two days, as the custom range gives them: the first and the last.
  if (/^\d{4}-\d{2}-\d{2}$/.test(r.from) && /^\d{4}-\d{2}-\d{2}$/.test(r.to)) {
    const show = (t: string) => resolve(t).toLocaleDateString(undefined, { dateStyle: 'medium' })
    return r.from === r.to ? show(r.from) : `${show(r.from)} to ${show(r.to)}`
  }
  const m = /^now-(\d+)([smhdwMy])$/.exec(r.from)
  if (m && r.to === 'now') return `Last ${m[1] === '1' ? '' : `${m[1]} `}${unitNames[m[2]!]}${m[1] === '1' ? '' : 's'}`
  const when = (t: string) => {
    try {
      return t === 'now' ? null : resolve(t)
    } catch {
      return null
    }
  }
  const [from, to] = [when(r.from), when(r.to)]
  const show = (d: Date | null, t: string) => d?.toLocaleString(undefined, { dateStyle: 'medium', timeStyle: 'short' }) ?? t
  if (from && to && from.toDateString() === to.toDateString()) return `${show(from, r.from)} to ${to.toLocaleTimeString(undefined, { timeStyle: 'short' })}`
  return `${show(from, r.from)} to ${show(to, r.to)}`
}
