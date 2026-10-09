import { describe, expect, it } from 'vitest'
import { formatValue } from '@/lib/format'
import { ems, fitting } from './fit'

// Widths in em, measured in Chrome in the app's font (Inter, semibold, tight, tabular figures) as a stat value is drawn.
const measured: [string, number][] = [
  ['52.9 °C', 3.49],
  ['5 °C', 2.01],
  ['24.4%', 3.09],
  ['100.0%', 3.71],
  ['35 W', 2.48],
  ['1,000 W', 3.97],
  ['150 W', 3.1],
  ['2.1 GHz', 3.72],
  ['1000 MB/s', 5.14],
  ['1.23 GB/s', 4.59],
  ['45.2 tok/s', 4.68],
  ['12.3K', 2.79],
  ['999.9 ms', 4.38],
  ['No data', 3.59],
  ['—', 0.98],
]

describe('a value sized to its cell', () => {
  it('is never thought narrower than it is drawn, and not much wider', () => {
    for (const [text, width] of measured) {
      expect(ems(text), text).toBeGreaterThanOrEqual(width - 0.01)
      expect(ems(text), text).toBeLessThan(width * 1.12)
    }
  })

  it('gets the size at which it fills the room, for a class to cap', () => {
    expect(fitting(ems('52.9 °C'), '100cqi')).toEqual({ '--fit': 'calc(100cqi / 3.62)' })
    expect(fitting(ems(formatValue(53, 'celsius')), '100cqi * 0.62')).toEqual({ '--fit': 'calc(100cqi * 0.62 / 2.72)' })
  })
})
