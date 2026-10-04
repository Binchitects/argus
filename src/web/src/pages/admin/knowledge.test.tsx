import { screen, waitFor, within } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { describe, expect, it } from 'vitest'
import { admin, fakeApi, renderApp } from '@/test/utils'
import type { KnowledgeSource } from './knowledge'

const source = (over: Partial<KnowledgeSource>): KnowledgeSource => ({
  id: 's1', name: 'Engineering', kind: 'gitlab', location: 'group', wiki: true, issues: true, hosts: null, maxPages: 200, audience: 'Everyone', groups: [],
  state: 'synced', error: null, syncStartedAt: null, syncedAt: new Date().toISOString(), documents: 12, passages: 40, projects: null, ...over,
})

const page = (sources: KnowledgeSource[], over: object = {}) => ({
  json: { embedder: true, problem: null, gitlabBot: true, folderRoot: '/knowledge', syncEvery: '01:00:00', sources, ...over },
})

describe('admin knowledge', () => {
  it('shows each source with its sync, counts and readers, and syncs one now', async () => {
    const calls = fakeApi(admin, {
      'GET /api/admin/knowledge': () =>
        page([
          source({ projects: [{ name: 'group/app', people: 12, fetchedAt: new Date().toISOString() }, { name: 'group/secret', people: 1, fetchedAt: new Date().toISOString() }] }),
          source({ id: 's2', name: 'Handbook', kind: 'folder', location: '/knowledge/handbook', audience: 'Groups', groups: [{ id: 'g1', name: 'HR' }], state: 'failed', error: 'The folder /knowledge/handbook does not exist.', documents: 0, passages: 0 }),
        ]),
      'POST /api/admin/knowledge/s1/sync': () => ({ status: 202 }),
      'GET /api/admin/groups': () => ({ json: [{ id: 'g1', name: 'HR', description: null, directory: null, members: 3, createdAt: '' }] }),
    })
    renderApp('/admin/knowledge')
    const gitlab = await screen.findByRole('region', { name: 'Engineering' })
    expect(within(gitlab).getByText('Synced')).toBeInTheDocument()
    expect(within(gitlab).getByText(/members of each project in GitLab/)).toBeInTheDocument()
    expect(within(within(gitlab).getByRole('list', { name: 'Projects' })).getByText('group/app')).toBeInTheDocument()
    expect(within(gitlab).getByText(/12 people/)).toBeInTheDocument()
    expect(within(gitlab).getByText('40')).toBeInTheDocument()

    const folder = screen.getByRole('region', { name: 'Handbook' })
    expect(within(folder).getByText('Failed')).toBeInTheDocument()
    expect(within(folder).getByText('The folder /knowledge/handbook does not exist.')).toBeInTheDocument()
    expect(within(folder).getByRole('combobox', { name: 'Who may read it' })).toHaveTextContent('Chosen groups')
    expect(within(folder).getByRole('button', { name: /HR/ })).toBeInTheDocument()

    await userEvent.click(within(gitlab).getByRole('button', { name: /Sync now/ }))
    await waitFor(() => expect(calls.some((c) => c.method === 'POST' && c.path === '/api/admin/knowledge/s1/sync')).toBe(true))
  })

  it('says when the embedder or the GitLab bot is missing', async () => {
    fakeApi(admin, {
      'GET /api/admin/knowledge': () => page([source({ state: 'new', syncedAt: null, documents: 0, passages: 0 })], { embedder: false, problem: 'The embedder (the embed module) is not running.', gitlabBot: false }),
    })
    renderApp('/admin/knowledge')
    expect(await screen.findByText('The embedder (the embed module) is not running.')).toBeInTheDocument()
    expect(screen.getByText(/GitLab sources are read with the GitLab bot/)).toBeInTheDocument()
    expect(screen.getByText('Waiting to sync')).toBeInTheDocument()
  })

  it('adds a GitLab group and a website', async () => {
    let sources: KnowledgeSource[] = []
    const calls = fakeApi(admin, {
      'GET /api/admin/knowledge': () => page(sources),
      'GET /api/admin/groups': () => ({ json: [] }),
      'POST /api/admin/knowledge': (body) => {
        const b = body as { name: string; kind: KnowledgeSource['kind']; location: string }
        sources = [...sources, source({ id: `s${sources.length + 1}`, name: b.name, kind: b.kind, location: b.location, state: 'syncing', syncedAt: null, syncStartedAt: new Date().toISOString(), documents: 0, passages: 0 })]
        return { status: 201, json: { id: 's1', name: b.name } }
      },
    })
    renderApp('/admin/knowledge')
    expect(await screen.findByText('No sources yet')).toBeInTheDocument()

    await userEvent.click(screen.getAllByRole('button', { name: /Add a source/ })[0])
    let dialog = await screen.findByRole('dialog', { name: 'Add a knowledge source' })
    await userEvent.type(within(dialog).getByLabelText('Name'), 'Engineering')
    await userEvent.type(within(dialog).getByLabelText('Project or group'), 'group')
    await userEvent.click(within(dialog).getByRole('checkbox', { name: /Issues/ }))
    await userEvent.click(within(dialog).getByRole('button', { name: 'Add' }))
    await waitFor(() => expect(calls.find((c) => c.method === 'POST')?.body).toEqual({ name: 'Engineering', kind: 'gitlab', location: 'group', wiki: true, issues: false }))
    expect(await within(await screen.findByRole('region', { name: 'Engineering' })).findByText('Syncing…')).toBeInTheDocument()

    await userEvent.click(screen.getByRole('button', { name: /Add a source/ }))
    dialog = await screen.findByRole('dialog', { name: 'Add a knowledge source' })
    await userEvent.click(within(dialog).getByRole('combobox', { name: 'Kind' }))
    await userEvent.click(await screen.findByRole('option', { name: 'Website' }))
    await userEvent.type(within(dialog).getByLabelText('Name'), 'Docs')
    await userEvent.type(within(dialog).getByLabelText('First page'), 'https://docs.example.test/')
    await userEvent.clear(within(dialog).getByLabelText('Most pages'))
    await userEvent.type(within(dialog).getByLabelText('Most pages'), '50')
    await userEvent.click(within(dialog).getByRole('button', { name: 'Add' }))
    await waitFor(() =>
      expect(calls.filter((c) => c.method === 'POST').at(-1)?.body).toEqual({
        name: 'Docs', kind: 'website', location: 'https://docs.example.test/', audience: 'Everyone', groups: [], hosts: '', maxPages: 50,
      }),
    )
  })
})
