import { useState } from 'react'
import { useSearchParams } from 'react-router'
import { allTime, dashboardRange, maxRangeDays, rangeKey, resolve, resolveRange, type TimeRange } from './time'

/** A time window as a page uses it: the range, its key, and the API's from and to. */
export type TimeWindowState = ReturnType<typeof windowOf>

function windowOf(range: TimeRange, set: (r: TimeRange) => void) {
  return {
    range,
    /** The same for the same window: for query keys. */
    key: rangeKey(range),
    set,
    /** The window as two times, worked out now (a window ending now moves with the clock). */
    resolved: () => resolveRange(range),
    /** The window as the API's from and to (ISO times); All time gives no start, and no end while it ends now. */
    query: () => {
      const r = resolveRange(range)
      if (range.from !== allTime.from) return new URLSearchParams({ from: r.from.toISOString(), to: r.to.toISOString() })
      return new URLSearchParams(range.to === 'now' ? {} : { to: r.to.toISOString() })
    },
  }
}

/**
 * A page's time window, kept in its address (from and to, as the dashboards' links have them) so a link opens the same
 * view and Back goes to the window before; <paramref name="fallback"/> when the address says none.
 */
export function useTimeWindow(fallback: TimeRange = { from: 'now-30d', to: 'now' }) {
  const [params, setParams] = useSearchParams()
  return windowOf(dashboardRange(params, fallback), (r: TimeRange) =>
    setParams((p) => {
      const next = new URLSearchParams(p)
      next.set('from', r.from)
      next.set('to', r.to)
      return next
    }),
  )
}

/** The same window kept by the component: where there is no address to keep it in (Code Arena's IDE, a tab of its own). */
export function useLocalTimeWindow(fallback: TimeRange = { from: 'now-30d', to: 'now' }) {
  const [range, setRange] = useState<TimeRange>(fallback)
  return windowOf(range, setRange)
}

/** Why two days are not a range: the first after the last, or longer than the server reads. */
export function customProblem(from: string, to: string): string | null {
  if (!from || !to) return 'Choose both days.'
  const a = resolve(from)
  const b = resolve(to, new Date(), true)
  if (a.getTime() >= b.getTime()) return 'The first day comes before the last.'
  if (b.getTime() - a.getTime() > maxRangeDays * 86_400_000 + 3_600_000) return `A range is at most ${maxRangeDays} days.`
  return null
}
