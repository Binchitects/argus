import { describe, expect, it } from 'vitest'
import { durationFromUnit, durationInUnit, initialValue, timeSpanSeconds, toTimeSpan, type SettingView } from './settings-model'

describe('settings model', () => {
  it('reads and writes .NET durations', () => {
    expect(timeSpanSeconds('01:30:00')).toBe(5400)
    expect(timeSpanSeconds('30.00:00:00')).toBe(30 * 86400)
    expect(toTimeSpan(5400)).toBe('01:30:00')
    expect(toTimeSpan(2 * 86400 + 60)).toBe('2.00:01:00')
  })
  it('edits durations in their unit', () => {
    expect(durationInUnit('01:00:00', 'minutes')).toBe('60')
    expect(durationInUnit('12:00:00', 'hours')).toBe('12')
    expect(durationInUnit('30.00:00:00', 'days')).toBe('30')
    expect(durationFromUnit('90', 'minutes')).toBe('01:30:00')
    expect(durationFromUnit('7', 'days')).toBe('7.00:00:00')
    expect(durationFromUnit('soon', 'days')).toBeNull()
  })
  it('starts a setting from its value, and a secret empty', () => {
    const base = { type: 'text', scope: 'live', value: 'medium', unit: null } as SettingView
    expect(initialValue(base)).toBe('medium')
    expect(initialValue({ ...base, type: 'secret', value: null })).toBe('')
  })
})
