import { render, screen, waitFor, within } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { describe, expect, it, onTestFinished } from 'vitest'
import { Providers } from '@/app/providers'
import { fakeApi } from '@/test/utils'
import { register } from './commands'
import { ProjectsView } from './projects-view'

const summary = (id: string, name: string, more: object = {}) => ({
  id, name, description: '', global: false, status: 'active', startsOn: null, dueOn: null, monthlyBudget: null, role: 'owner', owner: 'Mo Member',
  mine: true, members: 2, chats: 3, goals: 4, goalsDone: 1, spentThisMonth: 2.5, updatedAt: '2026-11-01T10:00:00Z', ...more,
})

const projects = [
  summary('p1', 'Payments', { monthlyBudget: 10 }),
  summary('p2', 'Company wiki', { global: true, mine: false, role: 'contributor', updatedAt: '2026-10-01T10:00:00Z' }),
  summary('p3', 'Old site', { status: 'archived', role: 'viewer', updatedAt: '2026-11-02T10:00:00Z' }),
]

/** The workbench's command that opens a project's tab, caught. */
function opened() {
  const seen: unknown[] = []
  onTestFinished(register({ id: 'codeArena.project.open', category: 'Projects', title: 'Open', run: (args) => void seen.push(args) }))
  return seen
}

describe('the Projects view', () => {
  it('lists Arena’s projects as its page does, marks the session’s and the repository’s, and opens one in a tab', async () => {
    const seen = opened()
    const calls = fakeApi(null, {
      'GET /api/projects': () => ({ json: projects }),
      'GET /api/projects/may': () => ({ json: { create: true, global: false } }),
      'GET /api/project': () => ({ json: { projects: [{ id: 'p1', name: 'Payments' }, { id: 'p2', name: 'Company wiki' }], suggested: 'p2', problem: null } }),
      'POST /api/project': () => ({ json: { said: 'In Company wiki.' } }),
    })
    render(
      <Providers>
        <ProjectsView session={{ id: 'p1', name: 'Payments' }} />
      </Providers>,
    )
    const list = await screen.findByRole('list', { name: 'Projects' })
    // Mine: the archived one last; the session's and the repository's said.
    expect(within(list).getAllByRole('listitem').map((li) => within(li).getAllByRole('button')[0]!.getAttribute('aria-label'))).toEqual([
      'Payments (this session)',
      'Old site',
    ])
    expect(within(list).getByText('This session counts to it')).toBeInTheDocument()
    expect(within(list).getByText('$2.50 this month of $10.00')).toBeInTheDocument()
    await userEvent.click(screen.getByRole('radio', { name: 'Global' }))
    expect(await within(list).findByRole('button', { name: "Company wiki (this folder's repository)" })).toBeInTheDocument()
    await userEvent.click(screen.getByRole('radio', { name: 'All I see' }))
    expect(within(list).getAllByRole('listitem')).toHaveLength(3)
    await userEvent.type(screen.getByRole('searchbox', { name: 'Find a project' }), 'wiki')
    expect(within(list).getAllByRole('listitem')).toHaveLength(1)

    // Put this session in another: as the chat's picker does; and open it.
    await userEvent.click(within(list).getByRole('button', { name: 'Put this session in Company wiki' }))
    await waitFor(() => expect(calls.find((c) => c.method === 'POST' && c.path === '/api/project')?.body).toEqual({ project: 'p2' }))
    await userEvent.click(within(list).getByRole('button', { name: "Company wiki (this folder's repository)" }))
    expect(seen.at(-1)).toMatchObject({ id: 'p2', name: 'Company wiki' })
  })

  it('makes a project when Arena lets the person, and opens it', async () => {
    const seen = opened()
    const calls = fakeApi(null, {
      'GET /api/projects': () => ({ json: [] }),
      'GET /api/projects/may': () => ({ json: { create: true, global: true } }),
      'GET /api/project': () => ({ json: { projects: [], suggested: null, problem: null } }),
      'POST /api/projects': () => ({ status: 201, json: { id: 'p9' } }),
    })
    render(
      <Providers>
        <ProjectsView session={null} />
      </Providers>,
    )
    expect(await screen.findByText('You are in no project yet.')).toBeInTheDocument()
    await userEvent.click(screen.getAllByRole('button', { name: 'New project' })[0]!)
    const dialog = await screen.findByRole('dialog', { name: 'New project' })
    await userEvent.type(within(dialog).getByLabelText('Name'), 'Search')
    // A global one is offered: Arena says the person may.
    await userEvent.click(within(dialog).getByRole('switch'))
    await userEvent.click(within(dialog).getByRole('button', { name: 'Make it' }))
    await waitFor(() => expect(seen.at(-1)).toEqual({ id: 'p9', name: 'Search' }))
    expect(calls.find((c) => c.method === 'POST' && c.path === '/api/projects')?.body).toEqual({ name: 'Search', description: '', global: true })
  })

  it('offers no new project to one who may not make one, and says to sign in when Arena is not reached', async () => {
    fakeApi(null, {
      'GET /api/projects': () => ({ status: 503, json: { status: 'no_arena', error: 'This needs Arena.' } }),
      'GET /api/projects/may': () => ({ json: { create: false, global: false } }),
      'GET /api/project': () => ({ json: { projects: null, suggested: null, problem: 'Sign in.' } }),
    })
    render(
      <Providers>
        <ProjectsView session={null} />
      </Providers>,
    )
    expect(await screen.findByRole('alert')).toHaveTextContent('Projects are in Arena: sign in to Arena')
    expect(screen.queryByRole('button', { name: 'New project' })).not.toBeInTheDocument()
  })
})
