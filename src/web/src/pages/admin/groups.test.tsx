import { screen, waitFor, within } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { describe, expect, it } from 'vitest'
import { admin, fakeApi, renderApp } from '@/test/utils'

const person = (id: string, name: string) => ({
  id, userName: name.toLowerCase(), displayName: name, email: `${name.toLowerCase()}@example.test`, isAdmin: false, source: 'local', disabled: false,
  disabledReason: null, twoFactorEnabled: false, lockedOut: false, lastSignInAt: null, createdAt: '', spend: null, budget: null,
})

describe('groups', () => {
  it('lists groups, and a new app group is made and opened', async () => {
    const calls = fakeApi(admin, {
      'GET /api/admin/groups': () => ({ json: [{ id: 'g1', name: 'Platform', description: 'Infra people', directory: 'platform', members: 4, createdAt: '' }] }),
      'POST /api/admin/groups': () => ({ status: 201, json: { id: 'g2', name: 'Data science' } }),
      'GET /api/admin/groups/g2': () => ({ json: { id: 'g2', name: 'Data science', description: null, directory: null, createdAt: '', members: [] } }),
    })
    const { router } = renderApp('/admin/groups')
    const row = (await screen.findByText('Platform')).closest('tr')!
    expect(within(row).getByText('Directory')).toBeInTheDocument()
    expect(within(row).getByText('4')).toBeInTheDocument()

    await userEvent.click(screen.getByRole('button', { name: 'New group' }))
    const dialog = await screen.findByRole('dialog', { name: 'New group' })
    await userEvent.click(within(dialog).getByRole('button', { name: 'Make group' }))
    expect(await within(dialog).findByText('Give it a name.')).toBeInTheDocument()
    await userEvent.type(within(dialog).getByLabelText('Name'), 'Data science')
    await userEvent.click(within(dialog).getByRole('button', { name: 'Make group' }))
    await waitFor(() => expect(router.state.location.pathname).toBe('/admin/groups/g2'))
    expect(calls.find((c) => c.method === 'POST')?.body).toEqual({ name: 'Data science', description: null, directory: null })
    expect(await screen.findByRole('heading', { name: 'Data science' })).toBeInTheDocument()
  })

  it('a directory group needs the directory name, suggested from the groups people are in', async () => {
    const calls = fakeApi(admin, {
      'GET /api/admin/groups': () => ({ json: [] }),
      'GET /api/admin/groups/directory': () => ({ json: [{ dn: 'cn=platform,ou=groups,dc=example,dc=test', name: 'platform', people: 3 }] }),
      'POST /api/admin/groups': () => ({ status: 201, json: { id: 'g3', name: 'Platform' } }),
      'GET /api/admin/groups/g3': () => ({ json: { id: 'g3', name: 'Platform', description: null, directory: 'platform', createdAt: '', members: [] } }),
    })
    renderApp('/admin/groups')
    await userEvent.click(await screen.findByRole('button', { name: 'New group' }))
    const dialog = await screen.findByRole('dialog', { name: 'New group' })
    await userEvent.click(within(dialog).getByRole('radio', { name: 'Directory group' }))
    await userEvent.type(within(dialog).getByLabelText('Name'), 'Platform')
    await userEvent.click(within(dialog).getByRole('button', { name: 'Make group' }))
    expect(await within(dialog).findByText(/Which directory group/)).toBeInTheDocument()
    await waitFor(() => expect(document.querySelector('datalist option')).toHaveAttribute('value', 'platform'))
    await userEvent.type(within(dialog).getByLabelText('Directory group'), 'platform')
    await userEvent.click(within(dialog).getByRole('button', { name: 'Make group' }))
    await waitFor(() => expect(calls.find((c) => c.method === 'POST')?.body).toEqual({ name: 'Platform', description: null, directory: 'platform' }))
    // The directory decides who is in it: no adding people by hand.
    expect(await screen.findByText(/Whoever the directory puts in this group/)).toBeInTheDocument()
    expect(screen.queryByRole('button', { name: 'Add people' })).toBeNull()
  })

  it('people are added to and removed from an app group', async () => {
    const calls = fakeApi(admin, {
      'GET /api/admin/groups/g1': () => ({ json: { id: 'g1', name: 'Data science', description: null, directory: null, createdAt: '', members: [{ id: 'p1', userName: 'ann', displayName: 'Ann', email: 'ann@example.test', isDisabled: false }] } }),
      'GET /api/admin/people': () => ({ json: { warning: null, people: [person('p1', 'Ann'), person('p2', 'Ben'), person('p3', 'Cy')] } }),
      'POST /api/admin/groups/g1/members': () => ({ status: 204 }),
      'DELETE /api/admin/groups/g1/members/p1': () => ({ status: 204 }),
    })
    renderApp('/admin/groups/g1')
    await userEvent.click(await screen.findByRole('button', { name: 'Add people' }))
    const dialog = await screen.findByRole('dialog', { name: 'Add people to Data science' })
    // Ann is already in it.
    expect(within(dialog).queryByLabelText('Ann')).toBeNull()
    await userEvent.type(within(dialog).getByRole('searchbox', { name: 'Search people' }), 'be')
    await userEvent.click(await within(dialog).findByRole('checkbox'))
    await userEvent.click(within(dialog).getByRole('button', { name: 'Add 1' }))
    await waitFor(() => expect(calls.find((c) => c.path.endsWith('/members') && c.method === 'POST')?.body).toEqual({ userIds: ['p2'] }))

    await userEvent.click(screen.getByRole('button', { name: 'Remove Ann from Data science' }))
    await waitFor(() => expect(calls.some((c) => c.method === 'DELETE' && c.path === '/api/admin/groups/g1/members/p1')).toBe(true))
  })
})
