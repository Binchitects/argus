import { describe, expect, it } from 'vitest'
import { queryUnits } from './units'
import type { PanelDef } from './types'

describe('queryUnits', () => {
  it("gives a query the unit its overrides give every one of its series, as Grafana matches them", () => {
    const panel = {
      type: 'timeseries',
      fieldConfig: {
        defaults: { unit: 'none' },
        overrides: [
          { matcher: { id: 'byRegexp', options: 'temp.*' }, properties: [{ id: 'unit', value: 'celsius' }] },
          { matcher: { id: 'byRegexp', options: 'fan' }, properties: [{ id: 'unit', value: 'percent' }] },
          { matcher: { id: 'byName', options: 'odd one' }, properties: [{ id: 'unit', value: 'watt' }] },
          { matcher: { id: 'byRegexp', options: '([' }, properties: [{ id: 'unit', value: 'bytes' }] },
        ],
      },
    } as unknown as PanelDef
    const results = [
      { refId: 'A', series: [{ name: 'temp gpu0' }, { name: 'temp gpu1' }] },
      { refId: 'B', series: [{ name: 'fan % gpu0' }, { name: 'fan % gpu1' }] },
      { refId: 'C', series: [{ name: 'odd one' }, { name: 'another' }] },
      { refId: 'D', series: null },
    ]
    expect(queryUnits(panel, results)).toEqual({ A: 'celsius', B: 'percent' })
  })
})
