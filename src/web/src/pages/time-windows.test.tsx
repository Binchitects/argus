import { screen, waitFor } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { describe, expect, it } from 'vitest'
import { admin, fakeApi, member, renderApp, type Call } from '@/test/utils'

// The reports that had no time window take the shared one (presets, a custom range, the calendar's periods), kept in
// the address so a link opens the same view; All time until one is chosen.

/** The window the API was asked for, by the request's from and to. */
function asked(calls: Call[], path: string) {
  const call = calls.filter((c) => c.path === path || c.path.startsWith(`${path}?`)).at(-1)
  if (!call) return null
  const url = new URL(call.path, 'https://llm.test')
  return { from: url.searchParams.get('from'), to: url.searchParams.get('to'), url }
}

const day = (d: string) => new Date(`${d}T00:00:00`).getTime()

async function offersTheWindows() {
  await userEvent.click(await screen.findByRole('combobox', { name: 'Time range' }))
  for (const name of ['All time', 'Last 7 days', 'This month', 'Last month', 'Custom range…']) expect(await screen.findByRole('option', { name })).toBeInTheDocument()
  await userEvent.keyboard('{Escape}')
}

describe('a time window on every report', () => {
  it('the leaderboard counts every vote until a window is chosen, then those cast in it', async () => {
    const board = { votes: 0, people: 0, models: [] }
    const calls = fakeApi(member, { 'GET /api/arena/leaderboard': () => ({ json: { public: true, board } }) })
    const { router } = renderApp('/leaderboard')
    expect(await screen.findByText('No votes yet')).toBeInTheDocument()
    expect(asked(calls, '/api/arena/leaderboard')?.from).toBeNull()
    await offersTheWindows()
    await userEvent.click(screen.getByRole('combobox', { name: 'Time range' }))
    await userEvent.click(await screen.findByRole('option', { name: 'Last 30 days' }))
    expect(new URLSearchParams(router.state.location.search).get('from')).toBe('now-30d')
    await waitFor(() => expect(asked(calls, '/api/arena/leaderboard')?.from).not.toBeNull())
    const w = asked(calls, '/api/arena/leaderboard')!
    expect(Date.parse(w.to!) - Date.parse(w.from!)).toBe(30 * 86_400_000)
    expect(await screen.findByText('No votes in this time')).toBeInTheDocument()
  })

  it('the audit log reads the window in its address, and pages within it', async () => {
    const calls = fakeApi(admin, { 'GET /api/admin/audit': () => ({ json: [] }) })
    renderApp('/admin/audit?from=2026-10-03&to=2026-10-09')
    await waitFor(() => expect(asked(calls, '/api/admin/audit')).not.toBeNull())
    const w = asked(calls, '/api/admin/audit')!
    expect([Date.parse(w.from!), Date.parse(w.to!)]).toEqual([day('2026-10-03'), day('2026-10-10')])
    expect(w.url.searchParams.get('take')).toBe('500')
    await offersTheWindows()
  })

  it('the prompt log searches within the window', async () => {
    const calls = fakeApi(admin, { 'GET /api/admin/prompts': () => ({ json: { items: [], next: null } }) })
    renderApp('/admin/prompts?from=now-1M/M&to=now-1M/M')
    await waitFor(() => expect(asked(calls, '/api/admin/prompts')).not.toBeNull())
    const w = asked(calls, '/api/admin/prompts')!
    const now = new Date()
    expect(Date.parse(w.from!)).toBe(new Date(now.getFullYear(), now.getMonth() - 1, 1).getTime())
    expect(Date.parse(w.to!)).toBe(new Date(now.getFullYear(), now.getMonth(), 1).getTime())
    expect(w.url.searchParams.get('source')).toBe('all')
    expect(screen.getByRole('combobox', { name: 'Time range' })).toHaveTextContent('Last month')
  })

  it('feedback lists, and counts, what was sent in the window', async () => {
    const calls = fakeApi(admin, { 'GET /api/admin/feedback': () => ({ json: { items: [], counts: { new: 0, read: 0, done: 0 } } }) })
    const { router } = renderApp('/admin/feedback')
    await screen.findByText('Nothing here.')
    expect(asked(calls, '/api/admin/feedback')?.from).toBeNull()
    expect(asked(calls, '/api/admin/feedback')?.url.searchParams.get('status')).toBe('new')
    await userEvent.click(screen.getByRole('combobox', { name: 'Time range' }))
    await userEvent.click(await screen.findByRole('option', { name: 'Today' }))
    expect(new URLSearchParams(router.state.location.search).get('from')).toBe('now/d')
    await waitFor(() => expect(asked(calls, '/api/admin/feedback')?.from).not.toBeNull())
    const today = new Date()
    expect(Date.parse(asked(calls, '/api/admin/feedback')!.from!)).toBe(new Date(today.getFullYear(), today.getMonth(), today.getDate()).getTime())
  })
})
