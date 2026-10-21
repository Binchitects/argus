import { screen, waitFor, within } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { afterEach, describe, expect, it, vi } from 'vitest'
import { fakeApi, member, renderApp } from '@/test/utils'
import type { Board, DataAnswer, DataSourceBrief, Pin } from './data-api'

const shop: DataSourceBrief = {
  id: 's1', name: 'Shop', description: 'The web shop.',
  tables: [{ name: 'shop.orders', kind: 'table', comment: 'What was bought', columns: [{ name: 'total', type: 'numeric(10,2)', comment: 'In euros' }, { name: 'placed_at', type: 'timestamp with time zone', comment: null }] }],
}

const byCountry: DataAnswer = {
  question: 'Orders by country', sql: 'select country as "Country", count(*) as "Orders" from shop.orders group by 1', explanation: 'Counts the orders from each country.',
  title: 'Orders by country', chart: { type: 'bar', x: 'Country', y: ['Orders'] },
  result: { columns: [{ name: 'Country', type: 'text' }, { name: 'Orders', type: 'number' }], rows: [['DE', 1], ['PT', 2]], truncated: false, milliseconds: 12 }, error: null, model: 'Main-Model',
}

const board = (over: Partial<Board>): Board => ({ id: 'b1', name: 'Sales week', description: '', reach: 'private', groupIds: [], createdAt: '', updatedAt: '', owner: 'Mo Member', mine: true, pins: 1, ...over })

const pin: Pin = {
  id: 'p1', boardId: 'b1', sourceId: 's1', source: 'Shop', title: 'Orders so far', question: 'Orders so far', sql: 'select count(*) as "Orders" from shop.orders', chart: { type: 'number' },
  refresh: 'daily', position: 0, result: { columns: [{ name: 'Orders', type: 'number' }], rows: [[1234]], truncated: false, milliseconds: 5 }, ranAt: '2026-10-22T08:00:00Z', error: null,
}

afterEach(() => vi.restoreAllMocks())

describe('ask your data', () => {
  it('says how to get a database when none is open', async () => {
    fakeApi(member, { 'GET /api/data/sources': () => ({ json: [] }) })
    renderApp('/data')
    expect(await screen.findByText('No database is open to you yet')).toBeInTheDocument()
  })

  it('a question is answered with what it means, a chart or the table, and its SQL to change and run', async () => {
    const calls = fakeApi(member, {
      'GET /api/data/sources': () => ({ json: [shop] }),
      'POST /api/data/ask': () => ({ json: byCountry }),
      'POST /api/data/run': () => ({ json: { columns: [{ name: 'Country', type: 'text' }, { name: 'Orders', type: 'number' }], rows: [['PT', 2]], truncated: false, milliseconds: 4 } }),
    })
    renderApp('/data')
    await userEvent.click(await screen.findByRole('button', { name: /1 table/ }))
    const tables = await screen.findByRole('dialog', { name: 'Tables of Shop' })
    expect(within(tables).getByRole('region', { name: 'shop.orders' })).toHaveTextContent('total numeric(10,2) · In euros')
    await userEvent.keyboard('{Escape}')

    await userEvent.type(screen.getByLabelText('Ask Shop'), 'Orders by country{Enter}')
    const answer = await screen.findByRole('list', { name: 'Answers' })
    expect(within(answer).getByText('Counts the orders from each country.')).toBeInTheDocument()
    expect(within(answer).getByRole('img', { name: 'Orders by country' })).toBeInTheDocument()
    expect(within(answer).getByText(/Shop · 2 rows in 12 ms · Main-Model/)).toBeInTheDocument()
    expect(calls.find((c) => c.path === '/api/data/ask')?.body).toEqual({ sourceId: 's1', question: 'Orders by country' })

    await userEvent.click(within(answer).getByRole('radio', { name: 'Table' }))
    expect(within(answer).getByRole('table')).toHaveTextContent('PT2')

    // The SQL, changed and run by hand.
    await userEvent.click(within(answer).getByText('SQL'))
    const sql = within(answer).getByLabelText('The SQL')
    await userEvent.clear(sql)
    await userEvent.type(sql, "select 'PT' as \"Country\", 2 as \"Orders\"")
    await userEvent.click(within(answer).getByRole('button', { name: 'Run' }))
    await waitFor(() => expect(within(answer).getByText(/Shop · 1 row in 4 ms/)).toBeInTheDocument())
    expect(calls.find((c) => c.path === '/api/data/run')?.body).toEqual({ sourceId: 's1', sql: "select 'PT' as \"Country\", 2 as \"Orders\"", question: 'Orders by country' })

    // A follow-up carries the answer before it.
    await userEvent.click(within(answer).getByRole('button', { name: /Ask a follow-up/ }))
    expect(screen.getByText('Building on: Orders by country')).toBeInTheDocument()
    await userEvent.type(screen.getByLabelText('Ask Shop'), 'and by month?{Enter}')
    await waitFor(() => expect(calls.filter((c) => c.path === '/api/data/ask')).toHaveLength(2))
    expect(calls.filter((c) => c.path === '/api/data/ask')[1]?.body).toEqual({
      sourceId: 's1', question: 'and by month?', earlier: { question: 'Orders by country', sql: "select 'PT' as \"Country\", 2 as \"Orders\"" },
    })
  })

  it('an answer is pinned to a new board, run again every hour', async () => {
    const calls = fakeApi(member, {
      'GET /api/data/sources': () => ({ json: [shop] }),
      'POST /api/data/ask': () => ({ json: byCountry }),
      'GET /api/data/boards': () => ({ json: [board({ mine: false, owner: 'Ana', id: 'b9', name: 'Not mine' })] }),
      'POST /api/data/boards': () => ({ status: 201, json: board({ id: 'b2', name: 'Weekly' }) }),
      'POST /api/data/boards/b2/pins': () => ({ status: 201, json: pin }),
    })
    renderApp('/data')
    await userEvent.type(await screen.findByLabelText('Ask Shop'), 'Orders by country{Enter}')
    await userEvent.click(await screen.findByRole('button', { name: /Pin to a board/ }))
    const dialog = await screen.findByRole('dialog', { name: 'Pin to a board' })
    // Only one's own boards: someone else's is not offered.
    await userEvent.type(await within(dialog).findByLabelText("The new board's name"), 'Weekly')
    await userEvent.click(within(dialog).getByRole('radio', { name: 'Every hour' }))
    await userEvent.click(within(dialog).getByRole('button', { name: 'Pin' }))
    expect(await screen.findByRole('link', { name: 'Open the board' })).toHaveAttribute('href', '/data/boards/b2')
    expect(calls.find((c) => c.path === '/api/data/boards' && c.method === 'POST')?.body).toEqual({ name: 'Weekly' })
    expect(calls.find((c) => c.path === '/api/data/boards/b2/pins')?.body).toEqual({
      sourceId: 's1', title: 'Orders by country', question: 'Orders by country', sql: byCountry.sql, chart: byCountry.chart, refresh: 'hourly',
    })
  })

  it('a board shows its charts as they last ran, and one is posted to a room as a table', async () => {
    const calls = fakeApi(member, {
      'GET /api/data/boards/b1': () => ({ json: { board: board({}), pins: [pin, { ...pin, id: 'p2', title: 'Broken', result: null, error: 'Its board\'s owner may no longer ask this database.' }] } }),
      'GET /api/chat/conversations': () => ({ json: [{ id: 'c1', title: 'My chat', updatedAt: '' }, { id: 'r1', title: 'Sales room', updatedAt: '', isRoom: true }] }),
      'POST /api/chat/conversations/r1/queue': () => ({ json: { queued: [], answering: false } }),
      'POST /api/data/pins/p1/refresh': () => ({ json: pin }),
    })
    renderApp('/data/boards/b1')
    const charts = await screen.findByRole('list', { name: 'Charts' })
    expect(within(charts).getByRole('figure', { name: 'Orders so far' })).toHaveTextContent('1,234Orders')
    expect(within(charts).getByRole('alert')).toHaveTextContent('may no longer ask')

    await userEvent.click(within(charts).getByRole('button', { name: 'More for Orders so far' }))
    await userEvent.click(await screen.findByRole('menuitem', { name: /Run it now/ }))
    await waitFor(() => expect(calls.some((c) => c.path === '/api/data/pins/p1/refresh')).toBe(true))

    await userEvent.click(within(charts).getByRole('button', { name: 'More for Orders so far' }))
    await userEvent.click(await screen.findByRole('menuitem', { name: /Post to a room/ }))
    const dialog = await screen.findByRole('dialog', { name: 'Post Orders so far to a room' })
    await waitFor(() => expect(within(dialog).getByRole('combobox')).toHaveTextContent('Sales room'))
    await userEvent.click(within(dialog).getByRole('button', { name: 'Post' }))
    await waitFor(() => expect(calls.some((c) => c.path === '/api/chat/conversations/r1/queue')).toBe(true))
    const posted = (calls.find((c) => c.path === '/api/chat/conversations/r1/queue')?.body as { content: string }).content
    expect(posted).toContain('**Orders so far**')
    expect(posted).toContain('| Orders |\n| ---: |\n| 1234 |')
    expect(posted).toContain('From the board Sales week')
  })

  it('a board of someone else shows its charts, without its owner\'s controls', async () => {
    fakeApi(member, { 'GET /api/data/boards/b1': () => ({ json: { board: board({ mine: false, owner: 'Ana', reach: 'company' }), pins: [pin] } }) })
    renderApp('/data/boards/b1')
    expect(await screen.findByText(/By Ana/)).toBeInTheDocument()
    expect(screen.queryByRole('button', { name: /Change/ })).not.toBeInTheDocument()
    await userEvent.click(screen.getByRole('button', { name: 'More for Orders so far' }))
    expect(await screen.findByRole('menuitem', { name: /Post to a room/ })).toBeInTheDocument()
    expect(screen.queryByRole('menuitem', { name: /Run it now/ })).not.toBeInTheDocument()
  })
})
