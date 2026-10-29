import { describe, expect, it } from 'vitest'
import { merge } from './tables'

const t = (value: string, rows: [string, number][]) => ({
  columns: [
    { name: 'Time', type: 'time' },
    { name: 'gpu', type: 'string' },
    { name: value, type: 'number' },
  ],
  rows: rows.map(([gpu, v]) => ['2026-10-29T00:00:00Z', gpu, v]),
})

describe('merge', () => {
  it('puts each GPU on one line, its values from each query side by side, without the times', () => {
    const out = merge([
      t('Value #A', [
        ['GPU 0', 0.5],
        ['GPU 1', 0.9],
      ]),
      t('Value #B', [
        ['GPU 1', 70],
        ['GPU 0', 60],
      ]),
    ])
    expect(out.columns.map((c) => c.name)).toEqual(['gpu', 'Value #A', 'Value #B'])
    expect(out.rows).toEqual([
      ['GPU 0', 0.5, 60],
      ['GPU 1', 0.9, 70],
    ])
  })

  it('keeps a row only one query has, with its other values empty', () => {
    const out = merge([t('Value #A', [['GPU 0', 1]]), t('Value #B', [['GPU 2', 3]])])
    expect(out.rows).toEqual([
      ['GPU 0', 1, null],
      ['GPU 2', null, 3],
    ])
  })

  it('joins on the text columns the tables share, and keeps the ones only one of them has', () => {
    const models = {
      columns: [
        { name: 'model', type: 'string' },
        { name: 'on', type: 'string' },
        { name: 'Value #A', type: 'number' },
      ],
      rows: [
        ['qwen', 'GPU 0', 10],
        ['qwen', 'GPU 1', 12],
        ['gemma', 'GPU 0', 4],
      ],
    }
    const out = merge([models])
    expect(out.rows).toHaveLength(3)
  })
})
