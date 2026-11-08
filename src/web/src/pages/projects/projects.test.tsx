import { screen, waitFor, within } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { describe, expect, it } from 'vitest'
import { fakeApi, member, renderApp } from '@/test/utils'

const summary = (id: string, name: string, more: object = {}) => ({
  id, name, description: '', global: false, status: 'active', startsOn: null, dueOn: null, monthlyBudget: null, role: 'owner', owner: 'Mo Member',
  mine: true, members: 2, chats: 3, goals: 4, goalsDone: 1, spentThisMonth: 2.5, updatedAt: '2026-11-01T10:00:00Z', ...more,
})

const project = (more: object = {}) => ({
  ...summary('p1', 'Payments'), alertPercent: 80, stopAtBudget: false, createdAt: '2026-10-01T10:00:00Z', ownerId: 'm1', isOwner: true, role: 'contributor',
  members: [{ id: 'x1', role: 'owner', userId: 'm1', groupId: null, name: 'Mo Member', email: 'mo@example.test', addedAt: '2026-10-01T10:00:00Z' }],
  repositories: [{ path: 'payments/api', host: null, addedAt: '2026-10-01T10:00:00Z' }],
  items: [], chats: [{ id: 'c1', title: 'Refunds', kind: 'chat', place: null, updatedAt: '2026-11-01T10:00:00Z', mine: true }], otherChats: [],
  goals: [{ id: 'g1', title: 'Refunds in a day', description: null, status: 'doing', dueOn: null, doneAt: null, position: 0 }],
  tasks: [],
  ...more,
})

const report = {
  from: '2026-10-08T00:00:00Z', to: '2026-11-07T00:00:00Z', intervalMs: 86_400_000,
  totals: { requests: 42, input: 30_000, cached: 10_000, output: 5_000, cost: 3.25, agentSeconds: 600, activeSeconds: 5_400, people: 2, messages: 17 },
  progress: { goals: 1, goalsDone: 0, tasks: 2, tasksDone: 1, tasksDoneInRange: 1, steps: 0, stepsDone: 0, chats: 1, rooms: 0, sessions: 1 },
  models: [{ name: 'qwen', requests: 42, tokens: 45_000, cost: 3.25 }],
  surfaces: [{ name: 'Chat', requests: 40, tokens: 40_000, cost: 3 }],
  series: [{ at: '2026-11-01T00:00:00Z', input: 30_000, output: 5_000, cost: 3.25 }],
  people: [{ id: 'm1', name: 'Mo Member', email: 'mo@example.test', requests: 42, tokens: 45_000, cost: 3.25, agentSeconds: 600, activeSeconds: 5_400, messages: 17 }],
  peopleHidden: true,
  budget: null,
  merged: [{ repository: 'payments/api', iid: 7, title: 'Refund in one call', author: 'ann', mergedAt: '2026-11-02T10:00:00Z', url: 'https://gitlab.example.test/payments/api/-/merge_requests/7' }],
  mergedError: null,
  human: { estimated: 2, notEstimated: 1, byHandHours: 12, peopleHours: 1.5, modelHours: 0.17, hourlyRate: 50, byHandCost: 600, actualCost: 78.25, faster: 8, savedHours: 10.5, savedCost: 521.75 },
}

describe('projects', () => {
  it('lists the person’s projects and the global ones, and makes a new one', async () => {
    const calls = fakeApi(member, {
      'GET /api/projects': () => ({ json: [summary('p1', 'Payments'), summary('p2', 'Company wiki', { global: true, mine: false, role: 'contributor' })] }),
      'POST /api/projects': () => ({ status: 201, json: { id: 'p3' } }),
      'GET /api/projects/p3': () => ({ json: project({ id: 'p3', name: 'Search' }) }),
      'GET /api/projects/p3/report': () => ({ json: report }),
    })
    const { router } = renderApp('/projects')
    const list = await screen.findByRole('list', { name: 'Projects' })
    expect((await within(list).findAllByRole('link')).map((l) => l.textContent)).toEqual(['Payments'])
    expect(within(list).getByText('Goals 1 of 4')).toBeInTheDocument()

    await userEvent.click(screen.getByRole('radio', { name: 'Global' }))
    expect(within(list).getAllByRole('link').map((l) => l.textContent)).toEqual(['Company wiki'])

    await userEvent.click(screen.getAllByRole('button', { name: 'New project' })[0]!)
    const dialog = await screen.findByRole('dialog', { name: 'New project' })
    await userEvent.type(within(dialog).getByLabelText('Name'), 'Search')
    await userEvent.click(within(dialog).getByRole('switch'))
    await userEvent.click(within(dialog).getByRole('button', { name: 'Make it' }))
    await waitFor(() => expect(router.state.location.pathname).toBe('/projects/p3'))
    expect(calls.find((c) => c.method === 'POST' && c.path === '/api/projects')?.body).toEqual({ name: 'Search', description: '', global: true })
  })

  it('offers no new project to a person who may not make one, nor a global one', async () => {
    fakeApi({ ...member, denied: ['feature.projects', 'feature.projects.global'] }, { 'GET /api/projects': () => ({ json: [] }) })
    renderApp('/projects')
    expect(await screen.findByText('You are in no project yet')).toBeInTheDocument()
    expect(screen.queryByRole('button', { name: 'New project' })).not.toBeInTheDocument()
  })

  it('shows a project’s numbers for the window in its address, and exports them for it', async () => {
    const calls = fakeApi(member, {
      'GET /api/projects/p1': () => ({ json: project() }),
      'GET /api/projects/p1/report': () => ({ json: report }),
    })
    const { router } = renderApp('/projects/p1?from=now-7d&to=now')
    expect(await screen.findByRole('list', { name: 'Merged changes' })).toBeInTheDocument()
    expect(screen.getAllByText('1 h 30 min').length).toBeGreaterThan(0)
    expect(screen.getAllByText('$3.25').length).toBeGreaterThan(0)
    expect(screen.getByRole('link', { name: /Refund in one call/ })).toHaveAttribute('href', report.merged[0]!.url)
    expect(screen.getByText('Your own share. Each person’s is for the project’s owners.')).toBeInTheDocument()
    // Against doing it by hand: 12 hours by hand, done in 1.5, 8 times faster, $521.75 saved.
    expect(screen.getByText('12 h')).toBeInTheDocument()
    expect(screen.getByText('8×')).toBeInTheDocument()
    expect(screen.getByText('$521.75')).toBeInTheDocument()
    const asked = new URL(calls.find((c) => c.path.startsWith('/api/projects/p1/report'))!.path, 'https://x').searchParams
    expect(Math.round((Date.parse(asked.get('to')!) - Date.parse(asked.get('from')!)) / 86_400_000)).toBe(7)

    await userEvent.click(screen.getByRole('combobox', { name: 'Time range' }))
    await userEvent.click(await screen.findByRole('option', { name: 'This month' }))
    await waitFor(() => expect(new URLSearchParams(router.state.location.search).get('from')).toBe('now/M'))
    await waitFor(() => expect(calls.filter((c) => c.path.startsWith('/api/projects/p1/report')).length).toBe(2))

    await userEvent.click(screen.getByRole('button', { name: 'Export' }))
    const xlsx = new URL((await screen.findByRole('menuitem', { name: 'Excel workbook (.xlsx)' })).getAttribute('href')!, 'https://x')
    expect(xlsx.pathname).toBe('/api/projects/p1/report/export')
    expect(xlsx.searchParams.get('format')).toBe('xlsx')
    const first = new Date()
    first.setDate(1)
    first.setHours(0, 0, 0, 0)
    expect(Date.parse(xlsx.searchParams.get('from')!)).toBe(first.getTime())
  })

  it('keeps a project’s goals and tasks', async () => {
    const calls = fakeApi(member, {
      'GET /api/projects/p1': () => ({ json: project() }),
      'GET /api/projects/p1/report': () => ({ json: report }),
      'POST /api/projects/p1/goals': () => ({ status: 201, json: { id: 'g2' } }),
      'PATCH /api/projects/p1/goals/g1': () => ({ json: {} }),
      'POST /api/projects/p1/tasks': () => ({ status: 201, json: { id: 't1' } }),
    })
    renderApp('/projects/p1')
    await userEvent.click(await screen.findByRole('tab', { name: 'Goals and tasks' }))
    await userEvent.type(screen.getByRole('textbox', { name: 'New goal' }), 'Refunds in an hour{Enter}')
    await waitFor(() => expect(calls.find((c) => c.method === 'POST' && c.path === '/api/projects/p1/goals')?.body).toEqual({ title: 'Refunds in an hour' }))

    await userEvent.click(screen.getByRole('combobox', { name: 'State of Refunds in a day' }))
    await userEvent.click(await screen.findByRole('option', { name: 'Done' }))
    await waitFor(() => expect(calls.find((c) => c.method === 'PATCH')?.body).toEqual({ status: 'done' }))
    // The hours it would take by hand, saved when the field is left.
    const hours = screen.getByRole('spinbutton', { name: 'Hours by hand for Refunds in a day' })
    await userEvent.type(hours, '16')
    await userEvent.tab()
    await waitFor(() => expect(calls.filter((c) => c.method === 'PATCH').at(-1)?.body).toEqual({ humanHours: 16 }))

    await userEvent.type(screen.getByRole('textbox', { name: 'New task' }), 'Write the runbook{Enter}')
    await waitFor(() => expect(calls.find((c) => c.method === 'POST' && c.path === '/api/projects/p1/tasks')?.body).toEqual({ title: 'Write the runbook' }))
  })

  it('lets a viewer see a project but not change it', async () => {
    fakeApi(member, {
      'GET /api/projects/p1': () => ({ json: project({ role: 'viewer', isOwner: false }) }),
      'GET /api/projects/p1/report': () => ({ json: report }),
    })
    renderApp('/projects/p1')
    await userEvent.click(await screen.findByRole('tab', { name: 'Goals and tasks' }))
    expect(screen.getByText('Refunds in a day')).toBeInTheDocument()
    expect(screen.queryByRole('textbox', { name: 'New goal' })).not.toBeInTheDocument()
    expect(screen.getByRole('combobox', { name: 'State of Refunds in a day' })).toBeDisabled()
    expect(screen.queryByRole('tab', { name: 'Settings' })).not.toBeInTheDocument()
  })
})
