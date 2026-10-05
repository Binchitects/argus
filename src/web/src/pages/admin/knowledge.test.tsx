import { screen, waitFor, within } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { describe, expect, it } from 'vitest'
import { admin, fakeApi, renderApp } from '@/test/utils'
import type { KnowledgeSource } from './knowledge'

const source = (over: Partial<KnowledgeSource>): KnowledgeSource => ({
  id: 's1', name: 'Engineering', kind: 'gitlab', location: 'group', wiki: true, issues: true, hosts: null, maxPages: 200, audience: 'Everyone', groups: [],
  spaces: null, blogPosts: false, sitePages: true, account: null, tenant: null, secretSet: false, mirror: null,
  state: 'synced', error: null, syncStartedAt: null, syncedAt: new Date().toISOString(), documents: 12, passages: 40, projects: null, viewers: null, ...over,
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

  it('shows what a Confluence source mirrors, and whom it falls back to', async () => {
    const calls = fakeApi(admin, {
      'GET /api/admin/knowledge': () =>
        page([
          source({
            id: 'c1', name: 'Company wiki', kind: 'confluence', location: 'https://acme.atlassian.net', spaces: 'ENG, HR', account: 'bot@acme.test', secretSet: true,
            audience: 'Groups', groups: [{ id: 'g1', name: 'HR' }],
            viewers: [{ name: 'ENG · Engineering', people: 34, fetchedAt: new Date().toISOString() }],
            mirror: 'ENG: mirrored from Confluence, 34 people.\nHR: Confluence does not show the account who may view this space, so the people you chose read it.',
          }),
          source({ id: 'p1', name: 'Engineering site', kind: 'sharepoint', location: 'https://contoso.sharepoint.com/sites/eng', tenant: 'contoso.onmicrosoft.com', account: 'app-id', secretSet: true, audience: 'Admins' }),
        ]),
      'PATCH /api/admin/knowledge/c1': () => ({ status: 204 }),
      'GET /api/admin/groups': () => ({ json: [{ id: 'g1', name: 'HR', description: null, directory: null, members: 3, createdAt: '' }] }),
    })
    renderApp('/admin/knowledge')
    const wiki = await screen.findByRole('region', { name: 'Company wiki' })
    expect(within(wiki).getByText('Confluence')).toBeInTheDocument()
    expect(within(wiki).getByText(/spaces ENG, HR · Cloud/)).toBeInTheDocument()
    expect(within(wiki).getByText(/whoever may view each space and page in Confluence/)).toBeInTheDocument()
    expect(within(within(wiki).getByRole('list', { name: 'Spaces' })).getByText('ENG · Engineering')).toBeInTheDocument()
    expect(within(wiki).getByText(/34 people, read/)).toBeInTheDocument()
    const mirrored = within(wiki).getByRole('list', { name: 'What is mirrored' })
    expect(within(mirrored).getByText(/HR: Confluence does not show the account who may view this space/)).toBeInTheDocument()
    expect(within(wiki).getByRole('combobox', { name: 'Where Confluence cannot tell' })).toHaveTextContent('Chosen groups')

    const site = screen.getByRole('region', { name: 'Engineering site' })
    expect(within(site).getByText('SharePoint')).toBeInTheDocument()
    expect(within(site).getByText(/each file's permissions in SharePoint/)).toBeInTheDocument()
    expect(within(site).getByRole('combobox', { name: 'Where SharePoint cannot tell' })).toHaveTextContent('Admins only')

    // Its settings: the saved token is not shown, and a change without one keeps it.
    await userEvent.click(within(wiki).getByRole('button', { name: /Settings/ }))
    const dialog = await screen.findByRole('dialog', { name: 'Company wiki: settings' })
    expect(within(dialog).getByLabelText('API token or personal access token')).toHaveValue('')
    expect(within(dialog).getByLabelText('API token or personal access token')).toHaveAttribute('placeholder', 'Saved: leave empty to keep it')
    await userEvent.clear(within(dialog).getByLabelText('Spaces (optional)'))
    await userEvent.type(within(dialog).getByLabelText('Spaces (optional)'), 'ENG')
    await userEvent.click(within(dialog).getByRole('button', { name: 'Save' }))
    await waitFor(() =>
      expect(calls.find((c) => c.method === 'PATCH')?.body).toEqual({
        name: 'Company wiki', location: 'https://acme.atlassian.net', account: 'bot@acme.test', spaces: 'ENG', blogPosts: false, audience: 'Groups', groups: ['g1'],
      }),
    )
  })

  it('adds Confluence after testing its connection', async () => {
    const calls = fakeApi(admin, {
      'GET /api/admin/knowledge': () => page([]),
      'GET /api/admin/groups': () => ({ json: [] }),
      'POST /api/admin/knowledge/test': () => ({ json: { message: 'Signed in to Confluence Data Center as svc-bot. It may read 2 spaces: ENG, HR.' } }),
      'POST /api/admin/knowledge': () => ({ status: 201, json: { id: 'c1', name: 'Company wiki' } }),
    })
    renderApp('/admin/knowledge')
    await userEvent.click((await screen.findAllByRole('button', { name: /Add a source/ }))[0])
    const dialog = await screen.findByRole('dialog', { name: 'Add a knowledge source' })
    await userEvent.click(within(dialog).getByRole('combobox', { name: 'Kind' }))
    await userEvent.click(await screen.findByRole('option', { name: 'Confluence' }))
    await userEvent.type(within(dialog).getByLabelText('Name'), 'Company wiki')
    await userEvent.type(within(dialog).getByLabelText('Site address'), 'https://confluence.example.test')
    await userEvent.type(within(dialog).getByLabelText('API token or personal access token'), 'a-personal-token')
    await userEvent.type(within(dialog).getByLabelText('Spaces (optional)'), 'ENG, HR')
    await userEvent.click(within(dialog).getByRole('checkbox', { name: 'Blog posts too' }))
    // Where Confluence cannot tell: admins only, until the admin says otherwise.
    expect(within(dialog).getByRole('combobox', { name: 'Where Confluence cannot tell' })).toHaveTextContent('Admins only')
    const expected = {
      name: 'Company wiki', kind: 'confluence', location: 'https://confluence.example.test', account: '', spaces: 'ENG, HR', blogPosts: true, secret: 'a-personal-token',
      audience: 'Admins', groups: [],
    }

    await userEvent.click(within(dialog).getByRole('button', { name: /Test connection/ }))
    expect(await within(dialog).findByText(/Signed in to Confluence Data Center as svc-bot/)).toBeInTheDocument()
    expect(calls.find((c) => c.path === '/api/admin/knowledge/test')?.body).toEqual(expected)

    await userEvent.click(within(dialog).getByRole('button', { name: 'Add' }))
    await waitFor(() => expect(calls.find((c) => c.method === 'POST' && c.path === '/api/admin/knowledge')?.body).toEqual(expected))
  })

  it('adds SharePoint, and says why its connection fails', async () => {
    const calls = fakeApi(admin, {
      'GET /api/admin/knowledge': () => page([]),
      'GET /api/admin/groups': () => ({ json: [] }),
      'POST /api/admin/knowledge/test': () => ({ status: 400, json: { status: 'connection', error: 'Microsoft Entra refused the app (401 invalid_client): AADSTS7000215: Invalid client secret provided.' } }),
      'POST /api/admin/knowledge': () => ({ status: 201, json: { id: 'p1', name: 'Engineering site' } }),
    })
    renderApp('/admin/knowledge')
    await userEvent.click((await screen.findAllByRole('button', { name: /Add a source/ }))[0])
    const dialog = await screen.findByRole('dialog', { name: 'Add a knowledge source' })
    await userEvent.click(within(dialog).getByRole('combobox', { name: 'Kind' }))
    await userEvent.click(await screen.findByRole('option', { name: 'SharePoint or OneDrive' }))
    await userEvent.type(within(dialog).getByLabelText('Name'), 'Engineering site')
    await userEvent.type(within(dialog).getByLabelText('Tenant ID'), 'contoso.onmicrosoft.com')
    await userEvent.type(within(dialog).getByLabelText('Client ID'), 'app-id')
    await userEvent.type(within(dialog).getByLabelText('Client secret'), 'the-secret')
    await userEvent.type(within(dialog).getByLabelText('Sites or libraries'), 'https://contoso.sharepoint.com/sites/eng{Enter}https://contoso.sharepoint.com/sites/eng/Specs')
    await userEvent.click(within(dialog).getByRole('checkbox', { name: /pages too/ }))

    await userEvent.click(within(dialog).getByRole('button', { name: /Test connection/ }))
    expect(await within(dialog).findByText(/AADSTS7000215/)).toBeInTheDocument()

    await userEvent.click(within(dialog).getByRole('button', { name: 'Add' }))
    await waitFor(() =>
      expect(calls.find((c) => c.method === 'POST' && c.path === '/api/admin/knowledge')?.body).toEqual({
        name: 'Engineering site', kind: 'sharepoint', location: 'https://contoso.sharepoint.com/sites/eng\nhttps://contoso.sharepoint.com/sites/eng/Specs', tenant: 'contoso.onmicrosoft.com',
        account: 'app-id', sitePages: false, secret: 'the-secret', audience: 'Admins', groups: [],
      }),
    )
  })
})
