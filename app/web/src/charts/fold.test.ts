import { describe, expect, it } from 'vitest'
import { formatValue } from '../format'
import { alignForStack, foldSeries } from './fold'

describe('foldSeries', () => {
  const s = (name: string, v: number) => ({ name, points: [[1000, v], [2000, v]] })

  it('leaves eight or fewer series alone', () => {
    const eight = Array.from({ length: 8 }, (_, i) => s(`p${i}`, i))
    expect(foldSeries(eight)).toBe(eight)
  })

  it('keeps the 7 largest in their original order and sums the rest into Other', () => {
    const many = Array.from({ length: 10 }, (_, i) => s(`p${i}`, i + 1)) // p9 largest
    const out = foldSeries(many)
    expect(out.map((x) => x.name)).toEqual(['p3', 'p4', 'p5', 'p6', 'p7', 'p8', 'p9', 'Other (3)'])
    expect(out[7].points).toEqual([[1000, 6], [2000, 6]]) // p0+p1+p2 = 1+2+3
  })
})

describe('formatValue', () => {
  it('never rounds a small cost to zero', () => {
    expect(formatValue(0.000102)).toBe('0.000102')
    expect(formatValue(10.1)).toBe('10.1')
    expect(formatValue(10.1234)).toBe('10.12')
    expect(formatValue(5440)).toBe('5.44 K')
    expect(formatValue(0.0018, 'currencyUSD')).toBe('$0.0018')
    expect(formatValue(null)).toBe('—')
  })
})

describe('alignForStack', () => {
  it('gives every series every timestamp, 0 where it had none', () => {
    const out = alignForStack([
      { name: 'a', points: [[1, 5], [3, 7]] },
      { name: 'b', points: [[2, 9]] },
    ])
    expect(out).toEqual([
      { name: 'a', points: [[1, 5], [2, 0], [3, 7]] },
      { name: 'b', points: [[1, 0], [2, 9], [3, 0]] },
    ])
  })
})
