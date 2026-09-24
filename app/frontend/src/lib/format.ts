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
