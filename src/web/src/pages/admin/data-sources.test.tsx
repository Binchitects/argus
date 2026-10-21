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
      mostRows: 1000, seconds: 15,
    }))
    const list = await screen.findByRole('list', { name: 'Data sources' })
    expect(within(list).getByText('arena_reader@db/shop')).toBeInTheDocument()
    await userEvent.click(within(list).getByRole('button', { name: /What was asked/ }))
    const asked = await screen.findByRole('dialog', { name: 'What was asked of Shop' })
    expect(within(asked).getByRole('list', { name: 'Queries' })).toHaveTextContent('Ana · ask · 1 row · 9 ms')
  })
})
