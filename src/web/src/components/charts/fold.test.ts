import { describe, expect, it } from 'vitest'
import { formatValue } from '@/lib/format'
import { alignForStack, foldBy, foldSeries } from './fold'

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

  it('averages the rest instead where a sum means nothing, over the series that have each point', () => {
    const cores = Array.from({ length: 24 }, (_, i) => s(`core ${i}`, 40 + i)) // core 23 hottest
    const out = foldSeries(cores, 8, 'mean')
    expect(out.map((x) => x.name)).toEqual(['core 17', 'core 18', 'core 19', 'core 20', 'core 21', 'core 22', 'core 23', 'Other (17, average)'])
    expect(out[7].points).toEqual([[1000, 48], [2000, 48]]) // 40..56 averaged, not 816 °C
    const gappy = [...cores.slice(0, 8), { name: 'core 8', points: [[1000, 10]] }]
    expect(foldSeries(gappy, 8, 'mean')[7].points).toEqual([[1000, 25], [2000, 40]]) // (40+10)/2, then 40 alone
  })

  it('averages temperatures, shares, clocks and durations, unless they are stacked', () => {
    for (const unit of ['celsius', 'percent', 'percentunit', 'hertz', 'rotmhz', 's', 'ms']) expect(foldBy(unit)).toBe('mean')
    for (const unit of ['bytes', 'Bps', 'short', 'none', 'currencyUSD', 'watt', undefined]) expect(foldBy(unit)).toBe('sum')
    expect(foldBy('percent', true)).toBe('sum')
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
