import { describe, expect, it } from 'vitest'
import { legendNameWidth } from './legend'

describe('legendNameWidth', () => {
  it('lets two names share the legend line on any chart, from a phone to a full-width one', () => {
    // Two names, each with its swatch and gap, the gap between them, and the legend's padding.
    for (const width of [180, 225, 340, 550, 1100]) expect(2 * (12 + 5 + legendNameWidth(width, 2)) + 10 + 10).toBeLessThanOrEqual(width)
  })

  it('leaves room for the pager when there are more names than two', () => {
    for (const width of [260, 340, 550, 1100]) expect(2 * (12 + 5 + legendNameWidth(width, 6)) + 10 + 10 + 70).toBeLessThanOrEqual(width)
  })

  it('cuts a name only past half the chart, and never to nothing', () => {
    expect(legendNameWidth(1100, 2)).toBeGreaterThan(500)
    expect(legendNameWidth(340, 2)).toBe(143)
    expect(legendNameWidth(340, 3)).toBe(108)
    expect(legendNameWidth(60, 2)).toBe(48)
  })
})
