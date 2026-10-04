import { screen, waitFor, within } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { describe, expect, it } from 'vitest'
import { admin, fakeApi, member, renderApp } from '@/test/utils'

const counts = (answers: number, up: number, down: number, reasons: Record<string, number> = {}) => ({
  answers, rated: up + down, up, down, ratedShare: answers ? (up + down) / answers : 0, upRate: up + down ? up / (up + down) : null, reasons,
})

const board = {
  votes: 3,
  people: 2,
  models: [
    { model: 'Main-Model', rating: 1031, matches: 3, wins: 2, losses: 0, ties: 1, bad: 1, winRate: 0.667 },
    { model: 'Eyes-Model', rating: 969, matches: 3, wins: 0, losses: 2, ties: 1, bad: 1, winRate: 0 },
  ],
}

const quality = {
  from: '2026-09-04T00:00:00Z',
  to: '2026-10-04T00:00:00Z',
  total: counts(40, 6, 4, { too_long: 3, wrong: 1 }),
  models: [
    { model: 'Main-Model', counts: counts(30, 5, 3, { too_long: 3 }) },
    { model: 'Eyes-Model', counts: counts(10, 1, 1, { wrong: 1 }) },
  ],
  projects: [
    { id: null, name: null, counts: counts(35, 5, 4, { too_long: 3, wrong: 1 }) },
    { id: 'p1', name: 'Billing service', counts: counts(5, 1, 0) },
  ],
  latest: [
    { id: 'f1', at: '2026-10-03T10:00:00Z', title: 'Rotate the logs', model: 'Main-Model', reason: 'too_long', comment: 'Half would do.', shared: true, project: null, person: 'Quinn' },
    { id: 'f2', at: '2026-10-02T10:00:00Z', title: 'Payroll question', model: 'Eyes-Model', reason: 'wrong', comment: null, shared: false, project: 'Billing service', person: null },
  ],
  leaderboard: board,
}

describe('quality page', () => {
  it('shows ratings per model and project, the latest down-rated, and opens only a shared chat', async () => {
    const calls = fakeApi(admin, {
      'GET /api/admin/quality': () => ({ json: quality }),
      'GET /api/admin/quality/feedback/f1': () => ({
        json: {
          title: 'Rotate the logs', reason: 'too_long', comment: 'Half would do.', person: 'Quinn', model: 'Main-Model',
          messages: [
            { id: 'q1', role: 'user', content: 'How do I rotate logs?', toolName: null, model: null, rated: false },
            { id: 'a1', role: 'assistant', content: 'Use logrotate, and here is a very long story.', toolName: null, model: 'Main-Model', rated: true },
          ],
        },
      }),
    })
    renderApp('/admin/quality')
    const byModel = await screen.findByRole('table', { name: 'By model' })
    const main = within(byModel).getByRole('row', { name: /Main-Model/ })
    expect(main).toHaveTextContent('30')
    expect(main).toHaveTextContent('8 (27%)')
    expect(main).toHaveTextContent('63%')
    expect(main).toHaveTextContent('Too long 3')
    expect(within(screen.getByRole('table', { name: 'By project' })).getByRole('row', { name: /No project/ })).toHaveTextContent('35')
    expect(screen.getByLabelText('Thumbs up')).toHaveTextContent('60%')

    const latest = screen.getByRole('list', { name: 'Latest down-rated answers' })
    const items = within(latest).getAllByRole('listitem')
    expect(items[0]).toHaveTextContent('Rotate the logs')
    expect(items[0]).toHaveTextContent('“Half would do.”')
    expect(items[1]).toHaveTextContent('Not shared')
    expect(within(items[1]!).queryByRole('button')).not.toBeInTheDocument()

    // The leaderboard of the votes in the range.
    const arena = screen.getByRole('table', { name: 'Arena leaderboard' })
    expect(within(arena).getAllByRole('row')[1]).toHaveTextContent('Main-Model1031')

    await userEvent.click(within(items[0]!).getByRole('button', { name: 'Open the shared chat' }))
    const dialog = await screen.findByRole('dialog', { name: 'Rotate the logs' })
    expect(await within(dialog).findByText('Use logrotate, and here is a very long story.')).toBeInTheDocument()
    expect(within(dialog).getByText('Rated down')).toBeInTheDocument()
    expect(calls.some((c) => c.path === '/api/admin/quality/feedback/f1')).toBe(true)
  })

  it('asks for the time range chosen', async () => {
    const calls = fakeApi(admin, { 'GET /api/admin/quality': () => ({ json: quality }) })
    renderApp('/admin/quality')
    await screen.findByRole('table', { name: 'By model' })
    await userEvent.click(screen.getByRole('radio', { name: '7 days' }))
    await waitFor(() => expect(calls.filter((c) => c.path.startsWith('/api/admin/quality?')).length).toBe(2))
    const asked = new URL(calls.filter((c) => c.path.startsWith('/api/admin/quality?')).at(-1)!.path, 'https://llm.test').searchParams
    const days = (new Date(asked.get('to')!).getTime() - new Date(asked.get('from')!).getTime()) / 86_400_000
    expect(Math.round(days)).toBe(7)
  })
})

describe('leaderboard', () => {
  it('everyone sees the company models by rating, with their votes', async () => {
    fakeApi(member, { 'GET /api/arena/leaderboard': () => ({ json: { public: true, board } }) })
    renderApp('/leaderboard')
    const table = await screen.findByRole('table', { name: 'Leaderboard' })
    const rows = within(table).getAllByRole('row')
    expect(rows[1]).toHaveTextContent('Main-Model')
    expect(rows[1]).toHaveTextContent('67%')
    expect(rows[2]).toHaveTextContent('Eyes-Model')
    expect(screen.getByText(/3 votes from 2 people/)).toBeInTheDocument()
  })

  it('says so when the admins keep it to themselves, and when nobody has voted', async () => {
    fakeApi(member, { 'GET /api/arena/leaderboard': () => ({ status: 403, json: { status: 'hidden', error: 'Your admins keep the leaderboard to themselves.' } }) })
    const { unmount } = renderApp('/leaderboard')
    expect(await screen.findByText('Your admins keep the leaderboard to themselves')).toBeInTheDocument()
    unmount()
    fakeApi(member, { 'GET /api/arena/leaderboard': () => ({ json: { public: true, board: { votes: 0, people: 0, models: [] } } }) })
    renderApp('/leaderboard')
    expect(await screen.findByText('No votes yet')).toBeInTheDocument()
  })
})
