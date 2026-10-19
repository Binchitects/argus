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
    await userEvent.click(within(dialog).getByRole('combobox'))
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
})
