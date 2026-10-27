import { screen, waitFor, within } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { describe, expect, it } from 'vitest'
import { admin, fakeApi, member, renderApp } from '@/test/utils'

const view = { frameworks: {}, coverage: {}, controls: [], checks: [], checkedAt: new Date().toISOString() }
const policy = { id: 'p1', key: 'acceptable-use', title: 'Acceptable use policy', owner: 'ciso', audience: 'Everyone', groups: [], reviewMonths: 12, version: 1,
  approvedAt: '2026-10-01T00:00:00Z', approvedBy: 'admin', reviewDue: '2027-10-01T00:00:00Z', reviewOverdue: false, draft: null, acknowledged: 3, readers: 5 }

describe('Admin → Compliance → Policies', () => {
  it('lists each policy with its version, review and readers, and adopts a template', async () => {
    const calls = fakeApi(admin, {
      'GET /api/admin/compliance': () => ({ json: view }),
      'GET /api/admin/compliance/policies': () => ({ json: [policy] }),
      'GET /api/admin/compliance/policies/templates': () => ({ json: [{ key: 'acceptable-use', title: 'Acceptable use policy' }, { key: 'ai', title: 'AI policy' }] }),
      'POST /api/admin/compliance/policies': () => ({ json: { ...policy, id: 'p2', key: 'ai', title: 'AI policy', version: null, draft: 1 } }),
      'GET /api/admin/compliance/policies/p2': () => ({ json: { policy: { ...policy, id: 'p2', title: 'AI policy', version: null, draft: 1 }, versions: [{ id: 'v1', version: 1, changes: null, createdBy: 'admin', createdAt: '2026-10-27T00:00:00Z', approvedBy: null, approvedAt: null, reviewDue: null, reviewedBy: null, reviewedAt: null }], pending: [] } }),
      'GET /api/admin/compliance/policies/p2/versions/1': () => ({ json: { text: '# AI policy\n\nThe text.' } }),
      'GET /api/admin/groups': () => ({ json: [] }),
    })
    renderApp('/admin/compliance?tab=policies')
    const row = await screen.findByRole('button', { name: /Acceptable use policy/ })
    expect(row).toHaveTextContent('v1 in force')
    expect(row).toHaveTextContent('read by 3 of 5')
    // An adopted template is not offered again.
    await userEvent.click(screen.getByRole('combobox', { name: 'Adopt a template' }))
    expect(screen.queryByRole('option', { name: 'Acceptable use policy' })).not.toBeInTheDocument()
    await userEvent.click(screen.getByRole('option', { name: 'AI policy' }))
    await userEvent.click(screen.getByRole('button', { name: 'Adopt' }))
    await waitFor(() => expect(calls.some((c) => c.method === 'POST' && c.path === '/api/admin/compliance/policies')).toBe(true))
    expect(calls.find((c) => c.method === 'POST')!.body).toEqual({ template: 'ai' })
    const dialog = await screen.findByRole('dialog', { name: 'AI policy' })
    expect(await within(dialog).findByText('The text.')).toBeInTheDocument()
    expect(within(dialog).getByRole('button', { name: 'Approve the draft' })).toBeEnabled()
  })
})

describe('Your account → Policies to read', () => {
  it('reads a policy and says so', async () => {
    const calls = fakeApi(member, {
      'GET /api/account/policies': () => ({ json: [{ versionId: 'v1', key: 'acceptable-use', title: 'Acceptable use policy', version: 2, approvedAt: '2026-10-01T00:00:00Z', acknowledgedAt: null }] }),
      'GET /api/account/policies/v1': () => ({ json: { text: '# Acceptable use policy\n\nUse it well.' } }),
      'POST /api/account/policies/v1/acknowledge': () => ({ json: {} }),
    })
    renderApp('/account')
    expect(await screen.findByText('1 of the company\'s policies for you to read and say so.')).toBeInTheDocument()
    await userEvent.click(screen.getByRole('button', { name: 'Read' }))
    const dialog = await screen.findByRole('dialog', { name: 'Acceptable use policy' })
    expect(await within(dialog).findByText('Use it well.')).toBeInTheDocument()
    await userEvent.click(within(dialog).getByRole('button', { name: 'I have read it' }))
    await waitFor(() => expect(calls.some((c) => c.path === '/api/account/policies/v1/acknowledge')).toBe(true))
  })
})
