import { screen, waitFor, within } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { describe, expect, it } from 'vitest'
import { admin, fakeApi, renderApp } from '@/test/utils'

const tables = [
  { name: 'shop.customers', kind: 'table', comment: null, columns: [{ name: 'id', type: 'integer', comment: null }] },
  { name: 'shop.orders', kind: 'table', comment: null, columns: [{ name: 'total', type: 'numeric', comment: null }] },
  { name: 'shop.secrets', kind: 'table', comment: null, columns: [{ name: 'value', type: 'text', comment: null }] },
]

describe('data sources', () => {
  it('a database is tried, its tables chosen, and connected; what was asked of it is listed', async () => {
    let connected = false
    const source = { id: 's1', name: 'Shop', description: '', kind: 'postgres', tables: ['shop.orders'], notes: 'In euros.', reach: 'company', groupIds: [], mostRows: 1000, seconds: 15, schemaReadAt: '', where: 'arena_reader@db/shop', schema: tables }
    const calls = fakeApi(admin, {
      'GET /api/admin/data-sources': () => ({ json: connected ? [source] : [] }),
      'POST /api/admin/data-sources/probe': () => ({ json: { tables, warnings: ['This role may change 1 table.'], where: 'arena_reader@db/shop' } }),
      'POST /api/admin/data-sources': () => {
        connected = true
        return { status: 201, json: source }
      },
      'GET /api/admin/data-sources/s1/queries': () => ({ json: [{ id: 'q1', at: '2026-10-22T08:00:00Z', via: 'ask', question: 'How many orders?', sql: 'select count(*) from shop.orders', rows: 1, milliseconds: 9, error: null, person: 'Ana' }] }),
    })
    renderApp('/admin/data-sources')
    await userEvent.click(await screen.findByRole('button', { name: /Connect a database/ }))
    const dialog = await screen.findByRole('dialog', { name: 'Connect a database' })
    await userEvent.type(within(dialog).getByLabelText('Name'), 'Shop')
    await userEvent.type(within(dialog).getByLabelText('Connection'), 'Host=db;Database=shop;Username=arena_reader;Password=x')
    await userEvent.click(within(dialog).getByRole('button', { name: /Try it/ }))
    expect(await within(dialog).findByText('This role may change 1 table.')).toBeInTheDocument()
    await userEvent.click(within(dialog).getByRole('checkbox', { name: 'shop.orders' }))
    await userEvent.type(within(dialog).getByLabelText(/What the model should know/), 'In euros.')
    await userEvent.click(within(dialog).getByRole('button', { name: 'Connect' }))

    await waitFor(() => expect(calls.find((c) => c.path === '/api/admin/data-sources' && c.method === 'POST')?.body).toEqual({
      name: 'Shop', description: '', connection: 'Host=db;Database=shop;Username=arena_reader;Password=x', tables: ['shop.orders'], notes: 'In euros.', reach: 'company', groupIds: [],
      mostRows: 1000, seconds: 15, kind: 'postgres',
    }))
    const list = await screen.findByRole('list', { name: 'Data sources' })
    expect(within(list).getByText('arena_reader@db/shop')).toBeInTheDocument()
    await userEvent.click(within(list).getByRole('button', { name: /What was asked/ }))
    const asked = await screen.findByRole('dialog', { name: 'What was asked of Shop' })
    expect(within(asked).getByRole('list', { name: 'Queries' })).toHaveTextContent('Ana · ask · 1 row · 9 ms')
  })

  it('a MySQL database opens every table its account may read', async () => {
    const calls = fakeApi(admin, {
      'GET /api/admin/data-sources': () => ({ json: [] }),
      'POST /api/admin/data-sources/probe': () => ({ json: { tables: tables.slice(0, 2), warnings: [], where: 'arena_reader@db/shop', grantsOnly: true } }),
      'POST /api/admin/data-sources': () => ({ status: 201, json: {} }),
    })
    renderApp('/admin/data-sources')
    await userEvent.click(await screen.findByRole('button', { name: /Connect a database/ }))
    const dialog = await screen.findByRole('dialog', { name: 'Connect a database' })
    await userEvent.click(within(dialog).getByRole('radio', { name: 'MySQL or MariaDB' }))
    expect(within(dialog).getByText(/Server=db.example.com;Port=3306/)).toBeInTheDocument()
    await userEvent.type(within(dialog).getByLabelText('Name'), 'Shop')
    await userEvent.type(within(dialog).getByLabelText('Connection'), 'Server=db;Database=shop;User ID=arena_reader;Password=x')
    await userEvent.click(within(dialog).getByRole('button', { name: /Try it/ }))
    const orders = await within(dialog).findByRole('checkbox', { name: 'shop.orders' })
    expect(orders).toBeChecked()
    expect(orders).toBeDisabled()
    expect(within(dialog).getByText(/does not show which tables a query reads/)).toBeInTheDocument()
    expect(calls.find((c) => c.path === '/api/admin/data-sources/probe')?.body).toMatchObject({ kind: 'mysql' })
    await userEvent.click(within(dialog).getByRole('button', { name: 'Connect' }))
    await waitFor(() => expect(calls.find((c) => c.path === '/api/admin/data-sources' && c.method === 'POST')?.body).toMatchObject({ kind: 'mysql', tables: ['shop.customers', 'shop.orders'] }))
  })

  it('a data files source takes files as tables, and removes one', async () => {
    const schema = [{ name: 'files_1.sales', kind: 'table', comment: 'sales.csv', columns: [{ name: 'region', type: 'text', comment: null }, { name: 'revenue', type: 'numeric', comment: null }] }]
    const source = { id: 'f1', name: 'Sales files', description: '', kind: 'files', tables: ['files_1.sales'], notes: '', reach: 'company', groupIds: [], mostRows: 1000, seconds: 15, schemaReadAt: '', where: 'uploaded files', schema }
    const calls = fakeApi(admin, {
      'GET /api/admin/data-sources': () => ({ json: [source] }),
      'POST /api/admin/data-sources/f1/files': () => ({ json: { imported: [{ table: 'targets', rows: 2 }], source } }),
      'DELETE /api/admin/data-sources/f1/files/sales': () => ({ status: 204 }),
    })
    renderApp('/admin/data-sources')
    const list = await screen.findByRole('list', { name: 'Data sources' })
    expect(within(list).getByText('Data files')).toBeInTheDocument()
    await userEvent.click(within(list).getByRole('button', { name: /Files/ }))
    const dialog = await screen.findByRole('dialog', { name: 'Sales files: files' })
    expect(within(within(dialog).getByRole('list', { name: 'Tables' })).getByText(/sales.csv · 2 columns: region, revenue/)).toBeInTheDocument()
    await userEvent.upload(within(dialog).getByLabelText('Data files to upload'), [new File(['Region,Due\nNorth,2026-10-01\n'], 'targets.csv', { type: 'text/csv' })])
    await waitFor(() => expect(calls.find((c) => c.method === 'POST' && c.path === '/api/admin/data-sources/f1/files')?.body).toBeInstanceOf(FormData))
    await userEvent.click(within(dialog).getByRole('button', { name: 'Remove files_1.sales' }))
    await userEvent.click(await screen.findByRole('button', { name: 'Remove' }))
    await waitFor(() => expect(calls.some((c) => c.method === 'DELETE' && c.path === '/api/admin/data-sources/f1/files/sales')).toBe(true))
  })
})
