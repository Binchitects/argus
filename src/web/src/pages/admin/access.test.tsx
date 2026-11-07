import { screen, waitFor, within } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { describe, expect, it } from 'vitest'
import { visibleNavigation } from '@/app/nav'
import { admin, fakeApi, member, renderApp } from '@/test/utils'

const overview = {
  version: 3,
  permissions: [
    { key: 'models.read', area: 'Models', description: 'See the models.', admin: true },
    { key: 'monitoring.read', area: 'Oversight', description: 'See the dashboards.', admin: true },
    { key: 'feature.web_search', area: 'Features', description: 'Search the web.', admin: false },
  ],
  resourceKinds: [
    { kind: 'model', actions: ['use'] },
    { kind: 'feature', actions: ['use'] },
  ],
  roles: [
    { id: 'r1', key: 'owner', name: 'Owner', description: 'Everything.', builtIn: true, permissions: ['models.read', 'monitoring.read'], holders: [{ id: 'a1', subject: { kind: 'Person', id: 'p1', name: 'person Ada' }, scope: null, expiresAt: null }] },
    { id: 'r2', key: 'custom-supervisor', name: 'Supervisor', description: null, builtIn: false, permissions: ['monitoring.read'], holders: [] },
  ],
  rules: [{ id: 'x1', effect: 'Deny', subject: { kind: 'Group', id: 'g1', name: 'group Interns' }, resourceKind: 'feature', resource: 'feature.web_search', action: 'use', expiresAt: null, note: 'Not yet' }],
}

describe('access', () => {
  it('shows roles with their holders and rules, makes a role, and gives one to a group', async () => {
    const calls = fakeApi(admin, {
      'GET /api/admin/access': () => ({ json: overview }),
      'POST /api/admin/access/roles': () => ({ status: 201, json: { id: 'r3', key: 'custom-team-manager' } }),
      'GET /api/admin/groups': () => ({ json: [{ id: 'g1', name: 'Interns', description: null, directory: null, scim: false, priority: 0, members: 2, createdAt: '' }] }),
      'POST /api/admin/access/assignments': () => ({ status: 201, json: { id: 'a2' } }),
    })
    renderApp('/admin/access')
    expect(await screen.findByText('person Ada')).toBeInTheDocument()
    expect(screen.getByText('Nobody holds it.')).toBeInTheDocument()

    await userEvent.click(screen.getByRole('button', { name: 'New role' }))
    let dialog = await screen.findByRole('dialog', { name: 'New role' })
    await userEvent.type(within(dialog).getByLabelText('Name'), 'Team manager')
    await userEvent.click(within(dialog).getByLabelText(/models\.read/))
    await userEvent.click(within(dialog).getByRole('button', { name: 'Make role' }))
    await waitFor(() => expect(calls.find((c) => c.path.endsWith('/roles'))?.body).toEqual({ name: 'Team manager', description: '', permissions: ['models.read'] }))

    await userEvent.click(screen.getAllByRole('button', { name: 'Give it' })[1])
    dialog = await screen.findByRole('dialog', { name: 'Give Supervisor' })
    await userEvent.click(within(dialog).getAllByRole('combobox')[0])
    await userEvent.click(await screen.findByRole('option', { name: 'Interns' }))
    await userEvent.click(within(dialog).getByRole('button', { name: 'Give it' }))
    await waitFor(() => expect(calls.find((c) => c.path.endsWith('/assignments'))?.body).toEqual({ roleId: 'r2', subjectKind: 'Group', subjectId: 'g1', expiresAt: null }))

    await userEvent.click(screen.getByRole('tab', { name: 'Rules' }))
    expect(await screen.findByText('Not yet')).toBeInTheDocument()
    expect(screen.getByText('group Interns')).toBeInTheDocument()
  })

  it('shows admin pages by the permissions a person holds', () => {
    const titles = (me: Parameters<typeof visibleNavigation>[0]) => visibleNavigation(me).flatMap((s) => s.items.map((i) => i.title))
    expect(titles(member)).not.toContain('Dashboards')
    const supervisor = titles({ ...member, permissions: ['monitoring.read'] })
    expect(supervisor).toContain('Dashboards')
    expect(supervisor).not.toContain('People')
    expect(titles(admin)).toContain('Access')
  })

  const matrixOverview = {
    ...overview,
    permissions: [
      { key: 'people.read', area: 'People and groups', description: 'See people.', admin: true },
      { key: 'people.manage', area: 'People and groups', description: 'Change people.', admin: true },
      { key: 'people.passwords', area: 'People and groups', description: 'Reset passwords.', admin: true, parent: 'people.manage' },
      { key: 'feature.web_search', area: 'Chat and knowledge', description: 'Search the web.', admin: false },
    ],
    templates: [{ key: 'help-desk', name: 'Help desk', description: 'Resets passwords.', permissions: ['people.passwords'] }],
    roles: [
      { id: 'r1', key: 'owner', name: 'Owner', description: null, builtIn: true, permissions: ['people.manage'], gives: ['people.read', 'people.manage', 'people.passwords'], holders: [] },
      { id: 'r2', key: 'custom-desk', name: 'Desk', description: null, builtIn: false, permissions: ['people.passwords'], gives: ['people.read', 'people.passwords'], holders: [] },
    ],
  }

  it('the matrix shows what each role gives, changes a custom role in bulk and saves it', async () => {
    const calls = fakeApi(admin, {
      'GET /api/admin/access': () => ({ json: matrixOverview }),
      'PATCH /api/admin/access/roles/r2': () => ({ status: 204 }),
    })
    renderApp('/admin/access?tab=matrix')
    const read = await screen.findByRole('checkbox', { name: 'Desk: people.read (given through a broader permission)' })
    expect(read).toBeChecked()
    expect(read).toBeDisabled()
    expect(screen.getByRole('checkbox', { name: 'Owner: people.manage' })).toBeDisabled()
    await userEvent.click(screen.getByRole('checkbox', { name: 'Desk: feature.web_search' }))
    await userEvent.type(screen.getByRole('textbox', { name: 'Search permissions' }), 'people')
    expect(screen.queryByRole('checkbox', { name: 'Desk: feature.web_search' })).toBeNull()
    // Every permission of the area shown, at once.
    await userEvent.click(screen.getByRole('checkbox', { name: 'Desk: every permission of People and groups shown' }))
    const bar = screen.getByRole('region', { name: 'Changes not saved' })
    expect(within(bar).getByText(/1 role changed: Desk/)).toBeInTheDocument()
    await userEvent.click(within(bar).getByRole('button', { name: 'Save changes' }))
    await waitFor(() =>
      expect(calls.find((c) => c.method === 'PATCH')?.body).toEqual({ permissions: ['feature.web_search', 'people.manage', 'people.passwords', 'people.read'] }),
    )
  })

  it('a role starts from a template, to change before it is made', async () => {
    const calls = fakeApi(admin, {
      'GET /api/admin/access': () => ({ json: matrixOverview }),
      'POST /api/admin/access/roles': () => ({ status: 201, json: { id: 'r3', key: 'custom-help-desk' } }),
    })
    renderApp('/admin/access')
    await userEvent.click(await screen.findByRole('button', { name: 'From a template' }))
    await userEvent.click(within(await screen.findByRole('dialog', { name: 'A role from a template' })).getByRole('button', { name: /Help desk/ }))
    const dialog = await screen.findByRole('dialog', { name: 'New role' })
    expect(within(dialog).getByLabelText('Name')).toHaveValue('Help desk')
    expect(within(dialog).getByRole('checkbox', { name: /people\.passwords/ })).toBeChecked()
    await userEvent.click(within(dialog).getByRole('button', { name: 'Make role' }))
    await waitFor(() => expect(calls.find((c) => c.method === 'POST')?.body).toEqual({ name: 'Help desk', description: 'Resets passwords.', permissions: ['people.passwords'] }))
  })

  it('a person’s permissions say why, and filter to what they do not hold', async () => {
    fakeApi(admin, {
      'GET /api/admin/access': () => ({ json: matrixOverview }),
      'GET /api/admin/people': () => ({ json: { warning: null, people: [{ id: 'p1', userName: 'ana', displayName: 'Ana' }] } }),
      'GET /api/admin/access/effective': () => ({
        json: {
          person: { id: 'p1', userName: 'ana', displayName: 'Ana', isDisabled: false, isOwner: false },
          roles: [{ id: 'r2', name: 'Desk', via: 'person Ana', scope: null, assignmentId: 'a1' }],
          groups: [{ id: 'g1', name: 'Interns', directory: null }],
          permissions: [
            { key: 'people.read', allowed: true, because: ['role Desk via person Ana'] },
            { key: 'people.manage', allowed: false, because: ['no role gives it'] },
            { key: 'people.passwords', allowed: true, because: ['role Desk via person Ana'] },
            { key: 'feature.web_search', allowed: false, because: ['deny for group Interns on feature feature.web_search (Not yet)'] },
          ],
          rules: [{ id: 'x1', effect: 'Deny', subject: { kind: 'Group', id: 'g1', name: 'group Interns' }, resourceKind: 'feature', resource: 'feature.web_search', action: 'use', expiresAt: null, note: 'Not yet' }],
        },
      }),
    })
    renderApp('/admin/access?tab=people')
    await userEvent.click((await screen.findAllByRole('combobox'))[0])
    await userEvent.click(await screen.findByRole('option', { name: 'Ana (ana)' }))
    expect(await screen.findByText(/Desk \(person Ana\)/)).toBeInTheDocument()
    expect(screen.getByText('deny for group Interns on feature feature.web_search (Not yet)')).toBeInTheDocument()
    await userEvent.click(screen.getByRole('radio', { name: 'Not held' }))
    const people = screen.getByRole('region', { name: 'People and groups' })
    expect(within(people).queryByText('people.read')).toBeNull()
    expect(within(people).getByText('people.manage')).toBeInTheDocument()
  })

  it('groups show their members, roles and rules, and the default groups are saved together', async () => {
    const calls = fakeApi(admin, {
      'GET /api/admin/access': () => ({ json: matrixOverview }),
      'GET /api/admin/access/groups': () => ({
        json: [
          { id: 'g1', name: 'Staff', description: null, directory: null, scim: false, joinsNewPeople: true, canBeDefault: true, memberCount: 1, members: [{ id: 'p1', name: 'Ana', userName: 'ana', isDisabled: false }], roles: [{ id: 'a1', roleId: 'r2', role: 'Desk', scope: null, expiresAt: null }], rules: [] },
          { id: 'g2', name: 'Interns', description: null, directory: null, scim: false, joinsNewPeople: false, canBeDefault: true, memberCount: 0, members: [], roles: [], rules: [{ id: 'x1', effect: 'Deny', resourceKind: 'feature', resource: 'feature.web_search', action: 'use', expiresAt: null, note: null }] },
          { id: 'g3', name: 'Ops', description: null, directory: 'cn=ops', scim: false, joinsNewPeople: false, canBeDefault: false, memberCount: 0, members: [], roles: [], rules: [] },
        ],
      }),
      'PUT /api/admin/access/defaults': () => ({ status: 204 }),
    })
    renderApp('/admin/access?tab=groups')
    expect(await screen.findByText('Deny feature:feature.web_search')).toBeInTheDocument()
    expect(screen.getByText('Desk')).toBeInTheDocument()
    expect(screen.getByText('The directory decides its members.')).toBeInTheDocument()
    expect(screen.queryByRole('button', { name: 'Save default groups' })).toBeNull()
    await userEvent.click(screen.getAllByRole('switch', { name: 'New people join' })[1])
    await userEvent.click(screen.getByRole('button', { name: 'Save default groups' }))
    await waitFor(() => expect(calls.find((c) => c.method === 'PUT')?.body).toEqual({ groups: ['g1', 'g2'] }))
  })

  it('the history shows one kind of change, before beside after', async () => {
    const calls = fakeApi(admin, {
      'GET /api/admin/access': () => ({ json: matrixOverview }),
      'GET /api/admin/access/history': () => ({
        json: [{ id: 7, at: '2026-11-06T10:00:00Z', kind: 'defaults', actor: 'admin', before: '{"groups":[]}', after: '{"groups":[{"name":"Staff"}]}' }],
      }),
    })
    renderApp('/admin/access?tab=history')
    expect(await screen.findByText('Before')).toBeInTheDocument()
    expect(screen.getByText(/"name": "Staff"/)).toBeInTheDocument()
    await userEvent.click(screen.getByRole('combobox', { name: 'Show' }))
    await userEvent.click(await screen.findByRole('option', { name: 'Default groups' }))
    await waitFor(() => expect(calls.some((c) => c.path.endsWith('&kind=defaults'))).toBe(true))
  })
})
