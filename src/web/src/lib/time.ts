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

/** A dashboard's range when neither the address nor its file says another. */
export const defaultRange: TimeRange = { from: 'now-1h', to: 'now' }

/** A range that can be drawn: both ends read as times, the start before the end. */
function readable(r: TimeRange): boolean {
  try {
    return resolve(r.from).getTime() < resolve(r.to).getTime()
  } catch {
    return false
  }
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

/** "now", "now-30d", an ISO time, or milliseconds since 1970 (as Grafana's links have them), as a Date. */
export function resolve(t: string, now: Date = new Date()): Date {
  if (t === 'now') return now
  const m = /^now-(\d+)([smhdwMy])$/.exec(t)
  if (m) return new Date(now.getTime() - Number(m[1]) * units[m[2]])
  const d = /^\d{10,}$/.test(t) ? new Date(Number(t)) : new Date(t)
  if (Number.isNaN(d.getTime())) throw new Error(`Not a time: ${t}`)
  return d
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
