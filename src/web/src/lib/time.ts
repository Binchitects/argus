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

/** "30s", "1m", "5m" (a dashboard's refresh) in milliseconds, or null. */
export function refreshMs(text: string | undefined | null): number | null {
  const m = /^(\d+)([smhd])$/.exec(text ?? '')
  return m ? Number(m[1]) * ({ s: 1e3, m: 6e4, h: 3.6e6, d: 8.64e7 } as Record<string, number>)[m[2]!]! : null
}

const units: Record<string, number> = { s: 1e3, m: 6e4, h: 3.6e6, d: 8.64e7, w: 6.048e8, M: 2.592e9, y: 3.1536e10 }

/** "now", "now-30d", or an ISO time, as a Date. */
export function resolve(t: string, now: Date = new Date()): Date {
  if (t === 'now') return now
  const m = /^now-(\d+)([smhdwMy])$/.exec(t)
  if (m) return new Date(now.getTime() - Number(m[1]) * units[m[2]])
  const d = new Date(t)
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

export function rangeLabel(r: TimeRange): string {
  return [...presets, ...metricPresets].find((p) => p.from === r.from && r.to === 'now')?.label ?? `${r.from} to ${r.to}`
}
