import { describe, expect, it } from 'vitest'
import { durationFromUnit, durationInUnit, initialValue, limits, shownValue, timeSpanSeconds, toTimeSpan, type SettingView } from './settings-model'

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
  it('shows a value in its unit, and the limits of a number or a duration', () => {
    const base = { type: 'wholenumber', scope: 'live', unit: null, min: 1, max: 32 } as SettingView
    expect(shownValue(base, '8')).toBe('8')
    expect(shownValue({ ...base, unit: 'characters' }, '6000')).toBe('6000 characters')
    expect(shownValue({ ...base, unit: 'bytes' }, '20971520')).toBe('20 MB')
    expect(shownValue({ ...base, type: 'duration', unit: 'minutes' }, '01:00:00')).toBe('60 minutes')
    expect(shownValue({ ...base, type: 'boolean' }, 'false')).toBe('off')
    expect(shownValue({ ...base, type: 'choices' }, 'a,b')).toBe('a, b')
    expect(shownValue(base, '')).toBeNull()
    expect(shownValue(base, null)).toBeNull()
    expect(limits(base)).toBe('1–32')
    expect(limits({ ...base, type: 'duration', min: 5, max: 1440 })).toBe('5–1440')
    // A text's length is checked as it is saved; bytes show as megabytes beside the box.
    expect(limits({ ...base, type: 'text', min: null, max: 60 })).toBeNull()
    expect(limits({ ...base, unit: 'bytes' })).toBeNull()
  })
})
