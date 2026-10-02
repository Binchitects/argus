import { describe, expect, it } from 'vitest'
import { daysLabel, timesLabel } from './hours'

describe('working hours in words', () => {
  it('names runs of days, and single days', () => {
    expect(daysLabel([1, 2, 3, 4, 5])).toBe('Mon–Fri')
    expect(daysLabel([6, 7])).toBe('Sat, Sun')
    expect(daysLabel([1, 3, 4, 5, 7])).toBe('Mon, Wed–Fri, Sun')
    expect(daysLabel([7, 1, 2, 3, 4, 5, 6])).toBe('Every day')
  })

  it('says when a window runs past midnight or all day', () => {
    expect(timesLabel('08:00', '18:00')).toBe('08:00–18:00')
    expect(timesLabel('22:00', '06:00')).toBe('22:00–06:00 (next day)')
    expect(timesLabel('00:00', '00:00')).toBe('all day')
  })
})
