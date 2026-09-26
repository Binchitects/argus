import { render, screen, within } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { describe, expect, it } from 'vitest'
import { DataTable, SortHeader, selectColumn, type ColumnDef } from './data-table'
import { Providers } from '@/app/providers'

interface Row {
  name: string
  spend: number
}
const rows: Row[] = [
  { name: 'Ada', spend: 3 },
  { name: 'Grace', spend: 10 },
  { name: 'Alan', spend: 1 },
]
const columns: ColumnDef<Row>[] = [
  selectColumn<Row>(),
  { accessorKey: 'name', header: 'Name' },
  { accessorKey: 'spend', header: ({ column }) => <SortHeader column={column} title="Spend" /> },
]

const names = () => screen.getAllByRole('row').slice(1).map((r) => within(r).getAllByRole('cell')[1]!.textContent)

describe('data table', () => {
  it('searches, sorts, and offers bulk actions for the selection', async () => {
    render(
      <Providers>
        <DataTable columns={columns} data={rows} noun="people" getRowId={(r) => r.name} bulk={(sel) => <span>bulk: {sel.map((s) => s.name).join(',')}</span>} />
      </Providers>,
    )
    expect(names()).toEqual(['Ada', 'Grace', 'Alan'])
    await userEvent.click(screen.getByRole('button', { name: /Spend, sort ascending/ }))
    expect(names()).toEqual(['Alan', 'Ada', 'Grace'])
    expect(screen.getByRole('columnheader', { name: /Spend/ })).toHaveAttribute('aria-sort', 'ascending')

    await userEvent.type(screen.getByRole('searchbox', { name: 'Search people' }), 'gr')
    expect(names()).toEqual(['Grace'])
    await userEvent.clear(screen.getByRole('searchbox', { name: 'Search people' }))
    await userEvent.type(screen.getByRole('searchbox', { name: 'Search people' }), 'zzz')
    expect(screen.getByText('No people match “zzz”.')).toBeInTheDocument()
    await userEvent.clear(screen.getByRole('searchbox', { name: 'Search people' }))

    const [first, second] = screen.getAllByRole('checkbox', { name: 'Select row' })
    await userEvent.click(first!)
    await userEvent.click(second!)
    expect(screen.getByRole('region', { name: 'Bulk actions' })).toHaveTextContent('2 selected')
    expect(screen.getByText(/bulk:/)).toHaveTextContent('bulk: Ada,Alan')
  })

  it('shows the empty message when there are no rows', () => {
    render(
      <Providers>
        <DataTable columns={columns} data={[]} noun="events" />
      </Providers>,
    )
    expect(screen.getByText('No events yet.')).toBeInTheDocument()
  })
})
