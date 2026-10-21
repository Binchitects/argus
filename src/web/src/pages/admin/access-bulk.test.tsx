import { screen, waitFor, within } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { describe, expect, it } from 'vitest'
import { admin, fakeApi, renderApp } from '@/test/utils'

const overview = {
  version: 1,
  permissions: [{ key: 'feature.web_search', area: 'Features', description: 'Search the web.', admin: false }],
  resourceKinds: [{ kind: 'feature', actions: ['use'] }],
  roles: [{ id: 'r2', key: 'supervisor', name: 'Supervisor', description: null, builtIn: true, permissions: ['monitoring.read'], holders: [] }],
  rules: [],
}

const report = (done: boolean) => ({
  dryRun: !done, done, people: 2, changes: 1,
  lines: [
    { id: 'p1', name: 'Ana', changes: ['gets Supervisor'], unchanged: [] },
    { id: 'p2', name: 'Ben', changes: [], unchanged: ['holds Supervisor already'] },
  ],
})

describe('changes to many people at once', () => {
  it('runs dry first, shows person by person what would change, then makes the changes', async () => {
    const calls = fakeApi(admin, {
      'GET /api/admin/access': () => ({ json: overview }),
      'GET /api/admin/groups': () => ({ json: [{ id: 'g1', name: 'Ops', description: null, directory: null, scim: false, priority: 0, members: 2, createdAt: '' }] }),
      'POST /api/admin/access/bulk': (body) => ({ json: report((body as { dryRun: boolean }).dryRun === false) }),
    })
    renderApp('/admin/access')
    await userEvent.click(await screen.findByRole('tab', { name: 'Many people' }))
    await userEvent.click(await screen.findByRole('checkbox', { name: 'Ops' }))
    await userEvent.click(screen.getByRole('combobox', { name: 'Change 1: the role' }))
    await userEvent.click(await screen.findByRole('option', { name: 'Supervisor' }))
    expect(screen.getByRole('button', { name: 'Make the changes' })).toBeDisabled()
    await userEvent.click(screen.getByRole('button', { name: 'Dry run' }))
    const list = await screen.findByRole('list', { name: 'Person by person' })
    expect(within(list).getByText('• gets Supervisor')).toBeInTheDocument()
    expect(within(list).getByText('– holds Supervisor already')).toBeInTheDocument()
    expect(calls.find((c) => c.path === '/api/admin/access/bulk')?.body).toMatchObject({ groups: ['g1'], dryRun: true, steps: [{ kind: 'role.give', roleId: 'r2' }] })
    await userEvent.click(screen.getByRole('button', { name: 'Make the changes' }))
    await waitFor(() => expect(calls.filter((c) => c.path === '/api/admin/access/bulk').at(-1)?.body).toMatchObject({ dryRun: false }))
    expect(await screen.findByText('Made, as one change in the history.')).toBeInTheDocument()
  })
})
