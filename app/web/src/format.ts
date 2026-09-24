export const money = (n: number | null | undefined) =>
  n === null || n === undefined ? 'unlimited' : `$${n.toFixed(n < 1 ? 4 : 2)}`

export const when = (iso: string | number | null | undefined) => {
  if (iso === null || iso === undefined) return 'never'
  const d = typeof iso === 'number' ? new Date(iso * 1000) : new Date(iso)
  return d.toLocaleString(undefined, { dateStyle: 'medium', timeStyle: 'short' })
}

/** "3 minutes ago" for an epoch-seconds time. */
export function ago(epochSeconds: number | null | undefined): string {
  if (!epochSeconds) return 'never'
  const s = Date.now() / 1000 - epochSeconds
  if (s < 90) return `${Math.max(0, Math.round(s))}s ago`
  if (s < 5400) return `${Math.round(s / 60)}m ago`
  if (s < 172800) return `${Math.round(s / 3600)}h ago`
  return `${Math.round(s / 86400)}d ago`
}

export function duration(seconds: number): string {
  if (seconds < 60) return `${seconds} second${seconds === 1 ? '' : 's'}`
  if (seconds < 3600) return `${Math.round(seconds / 60)} minutes`
  return `${Math.round(seconds / 3600)} hours`
}

/** Grafana's display units that the dashboards use. */
export function formatValue(v: unknown, unit?: string, decimals?: number | null): string {
  if (v === null || v === undefined || v === '') return '—'
  if (typeof v !== 'number') return String(v)
  const fixed = (n: number, d: number) => n.toLocaleString(undefined, { minimumFractionDigits: d, maximumFractionDigits: d })
  switch (unit) {
    case 'currencyUSD': {
      const d = decimals ?? (Math.abs(v) < 1 ? 4 : 2)
      return `$${fixed(v, d)}`
    }
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
      if (Number.isInteger(v)) return v.toLocaleString()
      // Small amounts keep 3 significant digits (0.000102, not 0.00); others at most 2 decimals, no trailing zeros.
      if (Math.abs(v) < 1) return v.toLocaleString(undefined, { maximumSignificantDigits: 3 })
      return v.toLocaleString(undefined, { maximumFractionDigits: decimals ?? 2 })
    }
  }
}
