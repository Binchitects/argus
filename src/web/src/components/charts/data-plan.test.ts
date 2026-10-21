import { describe, expect, it } from 'vitest'
import type { ResultColumn } from '@/pages/data/data-api'
import { planChart } from './data-plan'

const col = (name: string, type: ResultColumn['type']): ResultColumn => ({ name, type })

describe('how a result is drawn', () => {
  it('one figure for one number', () => {
    expect(planChart([col('Orders', 'number')], [[42]], undefined)).toEqual({ type: 'number', value: 42, label: 'Orders' })
    expect(planChart([col('Orders', 'number'), col('Note', 'text')], [[7, 'x']], { type: 'number' })).toEqual({ type: 'number', value: 7, label: 'Orders' })
  })

  it('lines over time and bars for names, from the shape when the model suggests nothing that fits', () => {
    expect(planChart([col('Week', 'date'), col('Orders', 'number')], [['2026-10-01', 3], ['2026-10-08', 5]], undefined)).toEqual({ type: 'line', x: 0, y: [1], time: true, dropped: 0 })
    expect(planChart([col('Country', 'text'), col('Orders', 'number'), col('Spent', 'number')], [['PT', 2, 200]], { type: 'bar', x: 'Nope' })).toEqual({
      type: 'bar', x: 0, y: [1, 2], time: false, dropped: 0,
    })
    // The model's own columns, when they are there.
    expect(planChart([col('Country', 'text'), col('Orders', 'number'), col('Spent', 'number')], [['PT', 2, 200]], { type: 'bar', x: 'Country', y: ['Spent'] })).toMatchObject({ y: [2] })
  })

  it('at most eight series and fifty bars', () => {
    const columns = [col('Name', 'text'), ...Array.from({ length: 10 }, (_, i) => col(`v${i}`, 'number'))]
    const rows = Array.from({ length: 60 }, (_, i) => [`n${i}`, ...Array.from({ length: 10 }, () => i)])
    expect(planChart(columns, rows, undefined)).toMatchObject({ type: 'bar', y: [1, 2, 3, 4, 5, 6, 7, 8], dropped: 10 })
  })

  it('a table when there is nothing to plot, or when asked', () => {
    expect(planChart([col('Name', 'text'), col('Country', 'text')], [['Ana', 'PT']], undefined)).toEqual({ type: 'table' })
    expect(planChart([col('Country', 'text'), col('Orders', 'number')], [['PT', 2]], { type: 'table' })).toEqual({ type: 'table' })
    expect(planChart([col('Orders', 'number')], [], undefined)).toEqual({ type: 'table' })
  })
})
