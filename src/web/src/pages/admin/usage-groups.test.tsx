import { screen, waitFor, within } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { describe, expect, it } from 'vitest'
import { admin, fakeApi, renderApp } from '@/test/utils'

const used = (requests: number, cost: number) => ({ requests, inputMiss: 100 * requests, inputHit: 100 * requests, output: 10 * requests, tokens: 210 * requests, cost })
const member = (id: string, name: string, requests: number, cost: number) => ({ id, userName: name.toLowerCase(), displayName: name, email: `${name.toLowerCase()}@example.test`, used: used(requests, cost) })

describe('usage by group', () => {
  it('lists each group by its spend with the members who used it, and the people in no group, over the time chosen', async () => {
    const calls = fakeApi(admin, {
      'GET /api/admin/usage/groups': () => ({
        json: {
          groups: [
            { id: 'g1', name: 'Research', members: 3, active: 1, used: used(4, 1.5), people: [member('p1', 'Ann', 4, 1.5)] },
            { id: 'g2', name: 'Data science', members: 5, active: 2, used: used(10, 7), people: [member('p2', 'Ben', 8, 6), member('p1', 'Ann', 2, 1)] },
          ],
          none: { id: null, name: '(in no group)', members: 2, active: 0, used: used(0, 0), people: [] },
          total: used(12, 7.5),
        },
      }),
    })
    const { router } = renderApp('/admin/usage-groups')
    const table = await screen.findByRole('table', { name: 'Usage by group' })
    const names = within(table).getAllByRole('button').map((b) => b.textContent)
    expect(names).toEqual(['Data science', 'Research', '(in no group)'])
    expect(within(table).getByRole('button', { name: '(in no group)' })).toBeDisabled()
    expect(screen.getByText(/Everyone: 12 requests/)).toBeInTheDocument()

    await userEvent.click(within(table).getByRole('button', { name: 'Data science' }))
    expect(within(table).getByRole('link', { name: 'Ben' })).toHaveAttribute('href', '/admin/people/p2')
    expect(within(table).getByText('$6.00')).toBeInTheDocument()

    await userEvent.click(screen.getByRole('combobox', { name: 'Time range' }))
    await userEvent.click(await screen.findByRole('option', { name: 'Last 30 days' }))
    await waitFor(() => expect(calls.filter((c) => c.path.startsWith('/api/admin/usage/groups')).length).toBe(2))
    const last = new URL(calls.at(-1)!.path, 'https://x').searchParams
    expect(Math.round((Date.parse(last.get('to')!) - Date.parse(last.get('from')!)) / 86_400_000)).toBe(30)
    // The window is the page's address; the export is for it.
    expect(new URLSearchParams(router.state.location.search).get('from')).toBe('now-30d')
    await userEvent.click(screen.getByRole('button', { name: 'Export' }))
    const pdf = new URL((await screen.findByRole('menuitem', { name: 'PDF report' })).getAttribute('href')!, 'https://x')
    expect(pdf.pathname).toBe('/api/admin/usage/groups/export')
    expect(pdf.searchParams.get('format')).toBe('pdf')
    expect(Math.round((Date.parse(pdf.searchParams.get('to')!) - Date.parse(pdf.searchParams.get('from')!)) / 86_400_000)).toBe(30)
  })
})
