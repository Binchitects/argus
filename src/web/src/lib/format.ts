const money2 = new Intl.NumberFormat('en-US', { style: 'currency', currency: 'USD', minimumFractionDigits: 2, maximumFractionDigits: 2 })

/** Dollars: two decimals, but tiny amounts keep 3 significant digits so they never read "$0.00". */
export function money(v: number | null | undefined): string {
  if (v === null || v === undefined || Number.isNaN(v)) return '—'
  if (v !== 0 && Math.abs(v) < 0.01) return `$${Number(v.toPrecision(3))}`
  return money2.format(v)
}

const compact = new Intl.NumberFormat('en-US', { notation: 'compact', maximumFractionDigits: 1 })
export const count = (v: number | null | undefined) => (v === null || v === undefined ? '—' : v >= 10_000 ? compact.format(v) : v.toLocaleString('en-US'))

const dateTime = new Intl.DateTimeFormat(undefined, { dateStyle: 'medium', timeStyle: 'short' })

/** A moment, from epoch seconds or an ISO string. */
export function when(v: number | string | null | undefined): string {
  if (v === null || v === undefined || v === '') return '—'
  const d = typeof v === 'number' ? new Date(v * 1000) : new Date(v)
  return Number.isNaN(d.getTime()) ? '—' : dateTime.format(d)
}

const rtf = new Intl.RelativeTimeFormat(undefined, { numeric: 'auto' })

/** "3 minutes ago", "yesterday". */
export function ago(v: number | string | null | undefined, now = Date.now()): string {
  if (v === null || v === undefined || v === '') return '—'
  const t = typeof v === 'number' ? v * 1000 : new Date(v).getTime()
  if (Number.isNaN(t)) return '—'
  const s = Math.round((t - now) / 1000)
  const abs = Math.abs(s)
  if (abs < 45) return 'just now'
  if (abs < 3600) return rtf.format(Math.round(s / 60), 'minute')
  if (abs < 86400) return rtf.format(Math.round(s / 3600), 'hour')
  if (abs < 30 * 86400) return rtf.format(Math.round(s / 86400), 'day')
  return when(v)
}

/** "3 minutes ago" for an epoch-seconds time (Argus reports those). */
export const agoSeconds = (s: number | null | undefined) => (s ? ago(s) : 'never')

export function duration(seconds: number): string {
  if (seconds < 60) return `${seconds} second${seconds === 1 ? '' : 's'}`
  if (seconds < 3600) return `${Math.round(seconds / 60)} minutes`
  if (seconds < 172800) return `${Math.round(seconds / 3600)} hours`
  return `${Math.round(seconds / 86400)} days`
}

/** Grafana's display units that the dashboards use. */
export function formatValue(v: unknown, unit?: string, decimals?: number | null): string {
  if (v === null || v === undefined || v === '') return '—'
  if (typeof v !== 'number') return String(v)
  const fixed = (n: number, d: number) => n.toLocaleString('en-US', { minimumFractionDigits: d, maximumFractionDigits: d })
  switch (unit) {
    case 'currencyUSD':
      return decimals !== undefined && decimals !== null ? `$${fixed(v, decimals)}` : money(v)
    case 'percent':
      return `${fixed(v, decimals ?? (Math.abs(v) < 10 ? 1 : 0))}%`
    case 'percentunit':
      return `${fixed(v * 100, decimals ?? 1)}%`
    case 's':
      return v < 1 ? `${fixed(v * 1000, 0)} ms` : `${fixed(v, decimals ?? 2)} s`
    case 'ms':
      return v >= 1000 ? `${fixed(v / 1000, decimals ?? 2)} s` : `${fixed(v, decimals ?? 0)} ms`
    case 'bytes': {
      const u = ['B', 'KiB', 'MiB', 'GiB', 'TiB']
      let i = 0
      let n = v
      while (Math.abs(n) >= 1024 && i < u.length - 1) {
        n /= 1024
        i++
      }
      return `${fixed(n, decimals ?? (i ? 1 : 0))} ${u[i]}`
    }
    default: {
      // "short": thousands as K, M, B, like Grafana.
      if (decimals !== undefined && decimals !== null && Math.abs(v) < 1000) return fixed(v, decimals)
      const steps: [number, string][] = [[1e12, 'T'], [1e9, 'B'], [1e6, 'M'], [1e3, 'K']]
      for (const [size, s] of steps) if (Math.abs(v) >= size) return `${fixed(v / size, 2).replace(/\.?0+$/, '')} ${s}`
      if (Number.isInteger(v)) return v.toLocaleString('en-US')
      // Small amounts keep 3 significant digits (0.000102, not 0.00).
      if (Math.abs(v) < 1) return v.toLocaleString('en-US', { maximumSignificantDigits: 3 })
      return v.toLocaleString('en-US', { maximumFractionDigits: decimals ?? 2 })
    }
  }
}
