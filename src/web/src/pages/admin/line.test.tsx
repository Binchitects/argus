import { screen, within } from '@testing-library/react'
import { describe, expect, it } from 'vitest'
import { admin, fakeApi, renderApp } from '@/test/utils'

describe('line', () => {
  it('shows who runs and who waits, in the order they will be served, chat or API', async () => {
    fakeApi(admin, {
      'GET /api/admin/line': () => ({
        json: {
          running: [{ person: { id: 'p1', userName: 'ann', displayName: 'Ann' }, model: 'Qwen3-4B', kind: 'chat', since: '', seconds: 42, position: null, priority: null }],
          waiting: [
            { person: { id: 'p2', userName: 'ben', displayName: 'Ben' }, model: 'Qwen3-4B', kind: 'api', since: '', seconds: 3, position: 1, priority: 5 },
            { person: { id: 'p3', userName: 'cy', displayName: null }, model: 'Qwen3-4B', kind: 'chat', since: '', seconds: 1, position: 2, priority: 0 },
          ],
          places: { 'Qwen3-4B': 1 },
          answersAtOnce: 0,
          answersPerPerson: 1,
          queueLength: 20,
          queueTimeoutMinutes: 10,
        },
      }),
    })
    renderApp('/admin/line')
    const waiting = await screen.findByRole('table', { name: 'Waiting' })
    const rows = within(waiting).getAllByRole('row').slice(1)
    expect(rows.map((r) => within(r).getAllByRole('cell').map((c) => c.textContent))).toEqual([
      ['1', 'Ben (ben)', 'Qwen3-4B', 'API', '3.0 s', '5'],
      ['2', 'cy', 'Qwen3-4B', 'Chat', '1.0 s', '0'],
    ])
    expect(within(screen.getByRole('table', { name: 'Running' })).getByRole('link', { name: 'Ann (ann)' })).toHaveAttribute('href', '/admin/people/p1')
    expect(screen.getByText(/at most 20 waiting, the next refused as busy/)).toBeInTheDocument()
    expect(screen.getByText(/: 1 at once/)).toBeInTheDocument()
  })
})
