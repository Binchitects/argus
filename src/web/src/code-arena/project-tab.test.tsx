import { render, screen, waitFor, within } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { describe, expect, it, onTestFinished } from 'vitest'
import { Providers } from '@/app/providers'
import { fakeApi, type Handler } from '@/test/utils'
import { register } from './commands'
import { ProjectTab } from './project-tab'

const project = (more: object = {}) => ({
  id: 'p1', name: 'Payments', description: 'The new checkout.', global: false, status: 'active', startsOn: null, dueOn: null, monthlyBudget: 20, role: 'owner', owner: 'Mo Member',
  alertPercent: 80, stopAtBudget: false, createdAt: '2026-10-01T10:00:00Z', updatedAt: '2026-11-01T10:00:00Z', ownerId: 'm1', isOwner: true,
  members: [{ id: 'x1', role: 'contributor', userId: 'u2', groupId: null, name: 'Ann Lee', email: 'ann@example.test', addedAt: '2026-10-01T10:00:00Z' }],
  repositories: [{ path: 'payments/api', host: 'gitlab.corp', addedAt: '2026-10-01T10:00:00Z' }],
  items: [],
  chats: [
    { id: 'c1', title: 'Refund flow', kind: 'session', place: '/w/api', updatedAt: '2026-11-01T10:00:00Z', mine: true },
    { id: 'c2', title: 'Design room', kind: 'room', place: null, updatedAt: '2026-11-01T10:00:00Z', mine: true },
  ],
  otherChats: [{ person: 'Ann Lee', count: 2 }],
  goals: [{ id: 'g1', title: 'Card payments', description: null, status: 'doing', dueOn: null, doneAt: null, position: 0 }],
  tasks: [{ id: 't1', title: 'Wire the gateway', goalId: 'g1', assigneeId: null, assignee: null, done: false, doneAt: null, createdAt: '2026-10-02T10:00:00Z' }],
  ...more,
})

const report = {
  from: '2026-10-08T00:00:00Z', to: '2026-11-07T00:00:00Z', intervalMs: 86_400_000,
  totals: { requests: 42, input: 30_000, cached: 10_000, output: 5_000, cost: 3.25, agentSeconds: 600, activeSeconds: 5_400, people: 2, messages: 17 },
  progress: { goals: 1, goalsDone: 0, tasks: 1, tasksDone: 0, tasksDoneInRange: 0, steps: 0, stepsDone: 0, chats: 1, rooms: 1, sessions: 1 },
  models: [{ name: 'qwen', requests: 42, tokens: 45_000, cost: 3.25 }],
  surfaces: [{ name: 'Code Arena', requests: 40, tokens: 40_000, cost: 3 }],
  series: [{ at: '2026-11-01T00:00:00Z', input: 30_000, output: 5_000, cost: 3.25 }],
  people: [{ id: 'm1', name: 'Mo Member', email: 'mo@example.test', requests: 42, tokens: 45_000, cost: 3.25, agentSeconds: 600, activeSeconds: 5_400, messages: 17 }],
  peopleHidden: false,
  budget: { monthly: 20, spent: 3.25, percent: 16, alertPercent: 80, stopAtBudget: false },
  merged: [],
  mergedError: null,
  human: { estimated: 2, notEstimated: 1, byHandHours: 12, peopleHours: 1.5, modelHours: 0.17, hourlyRate: 50, byHandCost: 600, actualCost: 78.25, faster: 8, savedHours: 10.5, savedCost: 521.75 },
}

const state = (more: object = {}) => ({ manual: 'https://arena.example.test/help/code-arena', workProject: null, chat: 'c9', busy: false, ...more })

function routes(more: Record<string, Handler> = {}) {
  return {
    'GET /api/state': () => ({ json: state() }),
    'GET /api/projects/p1': () => ({ json: project() }),
    'GET /api/projects/p1/report': () => ({ json: report }),
    'PATCH /api/projects/p1/tasks/t1': () => ({ status: 204 }),
    'POST /api/projects/p1/goals': () => ({ status: 201, json: { id: 'g2' } }),
    'POST /api/project': () => ({ json: { said: 'In Payments.' } }),
    'POST /api/sessions/web': () => ({ json: {} }),
    ...more,
  }
}

const show = () =>
  render(
    <Providers>
      <div className="relative h-[800px]">
        <ProjectTab id="p1" />
      </div>
    </Providers>,
  )

describe('a project in an editor tab', () => {
  it('shows the project as Arena’s page does: its numbers over the window, with its export', async () => {
    const calls = fakeApi(null, routes())
    show()
    expect(await screen.findByRole('heading', { name: /Payments/ })).toBeInTheDocument()
    expect(await screen.findByText('Budget this month: $3.25 of $20.00')).toBeInTheDocument()
    expect(screen.getAllByText('42').length).toBeGreaterThan(0)
    expect(calls.some((c) => c.path.startsWith('/api/projects/p1/report?from='))).toBe(true)
    // The window's presets, kept by the tab (the IDE has no address to keep them in).
    await userEvent.click(screen.getByRole('button', { name: 'Export' }))
    expect(await screen.findByRole('menuitem', { name: /PDF report/ })).toHaveAttribute('href', expect.stringMatching(/^\/api\/projects\/p1\/report\/export\?from=.*&format=pdf$/))
    expect(screen.getByRole('menuitem', { name: /Excel/ })).toHaveAttribute('href', expect.stringContaining('format=xlsx'))
    expect(screen.getByRole('menuitem', { name: /CSV/ })).toHaveAttribute('href', expect.stringContaining('format=csv'))
  })

  it('keeps its goals and tasks as the person may', async () => {
    const calls = fakeApi(null, routes())
    show()
    await userEvent.click(await screen.findByRole('tab', { name: 'Goals and tasks' }))
    await userEvent.click(screen.getByRole('checkbox', { name: 'Wire the gateway: done' }))
    await waitFor(() => expect(calls.find((c) => c.method === 'PATCH')).toMatchObject({ path: '/api/projects/p1/tasks/t1', body: { done: true } }))
    await userEvent.type(screen.getByRole('textbox', { name: 'New goal' }), 'Refunds{Enter}')
    await waitFor(() => expect(calls.find((c) => c.method === 'POST' && c.path === '/api/projects/p1/goals')?.body).toEqual({ title: 'Refunds' }))
  })

  it('opens what is in it here, and puts the session open now in it or takes it out', async () => {
    const commands: string[] = []
    onTestFinished(register({ id: 'workbench.action.chat.open', category: 'Chat', title: 'Open Chat', run: () => void commands.push('chat') }))
    const calls = fakeApi(null, routes())
    show()
    await userEvent.click(await screen.findByRole('tab', { name: 'In it' }))
    const inIt = await screen.findByRole('list', { name: 'Chats in the project' })
    // A session of the person's continues in the chat; a room is Arena's.
    await userEvent.click(within(inIt).getByRole('button', { name: /Refund flow/ }))
    await waitFor(() => expect(calls.find((c) => c.path === '/api/sessions/web')?.body).toEqual({ id: 'c1' }))
    await waitFor(() => expect(commands).toEqual(['chat']))
    expect(within(inIt).getByRole('link', { name: /Design room/ })).toHaveAttribute('href', 'https://arena.example.test/chat/c2')
    expect(screen.getByText('Also: Ann Lee (2)')).toBeInTheDocument()
    // What the person made goes in on Arena's page.
    expect(screen.getByRole('link', { name: "the project's page in Arena" })).toHaveAttribute('href', 'https://arena.example.test/projects/p1')

    await userEvent.click(screen.getByRole('button', { name: 'Put this session in it' }))
    await waitFor(() => expect(calls.find((c) => c.method === 'POST' && c.path === '/api/project')?.body).toEqual({ project: 'p1' }))
  })

  it('takes the session out when it is in it, and a viewer puts nothing in', async () => {
    const calls = fakeApi(null, routes({ 'GET /api/state': () => ({ json: state({ workProject: { id: 'p1', name: 'Payments' } }) }) }))
    const { unmount } = show()
    await userEvent.click(await screen.findByRole('tab', { name: 'In it' }))
    await userEvent.click(await screen.findByRole('button', { name: 'Take this session out' }))
    await waitFor(() => expect(calls.find((c) => c.method === 'POST' && c.path === '/api/project')?.body).toEqual({ project: 'none' }))
    unmount()

    fakeApi(null, routes({ 'GET /api/projects/p1': () => ({ json: project({ role: 'viewer', isOwner: false }) }) }))
    show()
    await userEvent.click(await screen.findByRole('tab', { name: 'In it' }))
    expect(await screen.findByText('Viewers see it; its contributors and owners put sessions in it.')).toBeInTheDocument()
    expect(screen.queryByRole('button', { name: 'Put this session in it' })).not.toBeInTheDocument()
    // No settings tab for a viewer, as on Arena's page.
    expect(screen.queryByRole('tab', { name: 'Settings' })).not.toBeInTheDocument()
  })

  it('gives owners its settings and people’s roles', async () => {
    const calls = fakeApi(null, { ...routes(), 'PATCH /api/projects/p1/members/x1': () => ({ status: 204 }), 'PATCH /api/projects/p1': () => ({ status: 204 }), 'GET /api/sharing/groups': () => ({ json: [] }) })
    show()
    await userEvent.click(await screen.findByRole('tab', { name: 'Settings' }))
    const budget = screen.getByLabelText('Dollars a month')
    await userEvent.clear(budget)
    await userEvent.type(budget, '50')
    await userEvent.click(screen.getByRole('button', { name: 'Save' }))
    await waitFor(() => expect(calls.find((c) => c.method === 'PATCH' && c.path === '/api/projects/p1')?.body).toMatchObject({ monthlyBudget: 50 }))
    await userEvent.click(screen.getByRole('tab', { name: 'People' }))
    expect(await screen.findByRole('combobox', { name: 'Role of Ann Lee' })).toBeInTheDocument()
  })
})
