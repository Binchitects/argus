import { describe, expect, it } from 'vitest'
import { formatValue } from '@/lib/format'
import { alignForStack, foldBy, foldSeries } from './fold'

describe('foldSeries', () => {
  const s = (name: string, v: number) => ({ name, points: [[1000, v], [2000, v]] })

  it('leaves eight or fewer series alone', () => {
    const eight = Array.from({ length: 8 }, (_, i) => s(`p${i}`, i))
    expect(foldSeries(eight)).toBe(eight)
  })

  it('keeps the 7 highest on average in their original order and sums the rest into Other', () => {
    const many = Array.from({ length: 10 }, (_, i) => s(`p${i}`, i + 1)) // p9 largest
    const out = foldSeries(many)
    expect(out.map((x) => x.name)).toEqual(['p3', 'p4', 'p5', 'p6', 'p7', 'p8', 'p9', 'Other (3)'])
    expect(out[7].points).toEqual([[1000, 6], [2000, 6]]) // p0+p1+p2 = 1+2+3
    expect(out[7].range).toBeUndefined()
  })

  it('keeps the hottest cores and averages the cooler ones, with the lowest and highest of them at each time', () => {
    const cores = Array.from({ length: 24 }, (_, i) => s(`core ${i}`, 40 + i)) // core 23 hottest
    const out = foldSeries(cores, 8, 'mean', 'celsius')
    expect(out.map((x) => x.name)).toEqual(['core 17', 'core 18', 'core 19', 'core 20', 'core 21', 'core 22', 'core 23', '17 cooler, average'])
    expect(out[7].points).toEqual([[1000, 48], [2000, 48]]) // 40..56 averaged, not 816 °C
    expect(out[7].range).toEqual([[1000, 40, 56], [2000, 40, 56]])
    const gappy = [...cores.slice(0, 8), { name: 'core 8', points: [[1000, 10]] }]
    expect(foldSeries(gappy, 8, 'mean')[7]).toMatchObject({ name: '2 lower, average', points: [[1000, 25], [2000, 40]] }) // (40+10)/2, then 40 alone
  })

  it('ranks by the average of the times a series has a value, so a gap never makes a hot core look cool', () => {
    const steady = Array.from({ length: 8 }, (_, i) => s(`core ${i}`, 50))
    // Hottest of all, but only for the last of four points: by its total it would be the coolest.
    const late = { name: 'core 8', points: [[1000, null], [2000, null], [3000, null], [4000, 90]] }
    const out = foldSeries([...steady, late], 8, 'mean', 'celsius')
    expect(out.map((x) => x.name)).toContain('core 8')
    expect(out.at(-1)!.name).toBe('2 cooler, average')
  })

  it('folds each query apart: a temperature is never averaged with a power, and each folded line says which it is', () => {
    const gpus = (what: string, v: number) => Array.from({ length: 8 }, (_, i) => ({ ...s(`${what} gpu${i}`, v + i), group: what }))
    const out = foldSeries([...gpus('temp', 60), ...gpus('power', 200)], 8, (g) => (g === 'temp' ? 'mean' : 'sum'), (g) => (g === 'temp' ? 'celsius' : 'watt'))
    expect(out).toHaveLength(8)
    expect(out.map((x) => x.name)).toEqual(['temp gpu5', 'temp gpu6', 'temp gpu7', 'power gpu5', 'power gpu6', 'power gpu7', 'temp: 5 cooler, average', 'power: Other (5)'])
    expect(out[6].points).toEqual([[1000, 62], [2000, 62]]) // 60..64
    expect(out[7].points).toEqual([[1000, 1010], [2000, 1010]]) // 200..204 summed
    // A query that fits keeps all of its lines; the others share what is left.
    const limit = [{ ...s('limit', 300), group: 'limit' }]
    const shared = foldSeries([...gpus('power', 200), ...limit], 8)
    expect(shared.map((x) => x.name)).toContain('limit')
    expect(shared).toHaveLength(8)
  })

  it('names two folded lines apart by their query when their series have no name in common', () => {
    const a = Array.from({ length: 6 }, (_, i) => ({ ...s(`a${i}`, i), group: 'A' }))
    const b = Array.from({ length: 6 }, (_, i) => ({ ...s(`b${i}`, i), group: 'B' }))
    const names = foldSeries([...a, ...b], 8, 'mean').map((x) => x.name)
    expect(names.filter((n) => n.includes('average'))).toEqual(['3 lower, average (A)', '3 lower, average (B)'])
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
