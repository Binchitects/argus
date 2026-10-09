import { render, screen, within } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { describe, expect, it } from 'vitest'
import { Markdown } from './markdown'
import { cellNumber } from './table-sort'

const table = (rows: string[]) => ['| Model | Score | Note |', '|---|---:|---|', ...rows].join('\n')
const column = (n: number) => screen.getAllByRole('row').slice(1).map((r) => within(r).getAllByRole('cell')[n]!.textContent)

describe('a table in an answer', () => {
  it('sorts by a column on a click: numbers as numbers, then the other way, then as written', async () => {
    render(<Markdown text={table(['| beta | 10 | **b** |', '| alpha | 9 | a |', '| gamma | 1,200 | c |'])} />)
    expect(screen.getByText('b').tagName).toBe('STRONG')
    const score = screen.getByRole('button', { name: 'Score, sort ascending' })
    await userEvent.click(score)
    expect(column(1)).toEqual(['9', '10', '1,200'])
    await userEvent.click(screen.getByRole('button', { name: 'Score, sort descending' }))
    expect(column(1)).toEqual(['1,200', '10', '9'])
    await userEvent.click(screen.getByRole('button', { name: 'Score, sort as written' }))
    expect(column(0)).toEqual(['beta', 'alpha', 'gamma'])
    await userEvent.click(screen.getByRole('button', { name: 'Model, sort ascending' }))
    expect(column(0)).toEqual(['alpha', 'beta', 'gamma'])
    expect(screen.queryByRole('searchbox')).not.toBeInTheDocument()
  })

  it('a longer one filters to the rows that hold what is typed', async () => {
    render(<Markdown text={table(Array.from({ length: 12 }, (_, i) => `| m${i} | ${i} | ${i % 3 === 0 ? 'fast' : 'slow'} |`))} />)
    await userEvent.type(screen.getByRole('searchbox', { name: "Filter the table's rows" }), 'fast')
    expect(column(0)).toEqual(['m0', 'm3', 'm6', 'm9'])
    await userEvent.type(screen.getByRole('searchbox'), 'er')
    expect(screen.getByText('No rows hold “faster”.')).toBeInTheDocument()
  })

  it('script in a cell never runs', () => {
    render(<Markdown text={table(['| <img src=x onerror=alert(1)> | 1 | <script>alert(1)</script> |'])} />)
    expect(document.querySelector('[onerror]')).toBeNull()
    expect(document.querySelector('script')).toBeNull()
  })

  it('reads numbers the way tables write them', () => {
    expect([cellNumber('1,204'), cellNumber('35%'), cellNumber('$4.20'), cellNumber('−3'), cellNumber('v2'), cellNumber('')]).toEqual([1204, 35, 4.2, -3, null, null])
  })
})
