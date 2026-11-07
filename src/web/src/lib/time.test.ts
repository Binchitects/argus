import { describe, expect, it } from 'vitest'
import { allTime, rangeLabel, resolve, resolveRange } from './time'
import { customProblem } from './use-time-window'

// A Wednesday afternoon, in the test's time zone.
const now = new Date(2026, 10, 4, 15, 30)

describe('time windows', () => {
  it('reads the calendar presets as the period they name, the end as its end', () => {
    expect(resolve('now/d', now)).toEqual(new Date(2026, 10, 4))
    expect(resolve('now/d', now, true)).toEqual(new Date(2026, 10, 5))
    // A week starts on Monday.
    expect(resolve('now/w', now)).toEqual(new Date(2026, 10, 2))
    expect(resolve('now/M', now)).toEqual(new Date(2026, 10, 1))
    expect(resolveRange({ from: 'now-1M/M', to: 'now-1M/M' }, now)).toEqual({ from: new Date(2026, 9, 1), to: new Date(2026, 10, 1) })
    // Last month of January is December of the year before.
    expect(resolveRange({ from: 'now-1M/M', to: 'now-1M/M' }, new Date(2026, 0, 31))).toEqual({ from: new Date(2025, 11, 1), to: new Date(2026, 0, 1) })
  })

  it('reads a custom range of two days as both whole days', () => {
    expect(resolveRange({ from: '2026-10-01', to: '2026-10-31' }, now)).toEqual({ from: new Date(2026, 9, 1), to: new Date(2026, 10, 1) })
    expect(resolveRange({ from: 'now-7d', to: 'now' }, now).to).toEqual(now)
  })

  it('names the window the way the control shows it', () => {
    expect(rangeLabel({ from: 'now-30d', to: 'now' })).toBe('Last 30 days')
    expect(rangeLabel({ from: 'now/M', to: 'now' })).toBe('This month')
    expect(rangeLabel({ from: 'now-1M/M', to: 'now-1M/M' })).toBe('Last month')
    const day = new Date(2026, 9, 1).toLocaleDateString(undefined, { dateStyle: 'medium' })
    expect(rangeLabel({ from: '2026-10-01', to: '2026-10-01' })).toBe(day)
    expect(rangeLabel({ from: '2026-10-01', to: '2026-10-31' })).toBe(`${day} to ${new Date(2026, 9, 31).toLocaleDateString(undefined, { dateStyle: 'medium' })}`)
  })

  it('says why two days are no range', () => {
    expect(customProblem('', '2026-10-01')).toBe('Choose both days.')
    expect(customProblem('2026-10-02', '2026-10-01')).toBe('The first day comes before the last.')
    expect(customProblem('2024-01-01', '2026-10-01')).toBe('A range is at most 400 days.')
    expect(customProblem('2026-10-01', '2026-10-01')).toBeNull()
  })
  it('reads All time from the start of time, and names it', () => {
    expect(resolve(allTime.from, now)).toEqual(new Date(0))
    expect(rangeLabel({ from: allTime.from, to: 'now' })).toBe('All time')
  })
})
