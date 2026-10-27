import { screen, waitFor, within } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { describe, expect, it } from 'vitest'
import { admin, fakeApi, renderApp } from '@/test/utils'

const view = { frameworks: {}, coverage: {}, controls: [], checks: [], checkedAt: new Date().toISOString() }

describe('Admin → Compliance → Suppliers', () => {
  it('offers what Arena connects to and registers it with its data', async () => {
    const calls = fakeApi(admin, {
      'GET /api/admin/compliance': () => ({ json: view }),
      'GET /api/admin/compliance/suppliers': () => ({ json: {
        suppliers: [{ id: 's1', name: 'GitLab', kind: 'software', service: null, data: ['code', 'personal'], location: 'EU', certifications: null, agreements: null, risk: 'high', owner: null, notes: null, source: 'gitlab', reviewDue: '2027-01-01T00:00:00Z', reviewOverdue: false, reviewedAt: null, reviewedBy: null, retiredAt: null, agreementMissing: true }],
        suggested: [{ source: 'huggingface', name: 'Hugging Face', kind: 'model', service: 'The models an admin downloads' }],
      } }),
      'POST /api/admin/compliance/suppliers': () => ({ json: {} }),
    })
    renderApp('/admin/compliance?tab=suppliers')
    expect(await screen.findByText('no agreement')).toBeInTheDocument()
    expect(screen.getByText(/Arena connects to these/)).toBeInTheDocument()
    await userEvent.click(screen.getByRole('button', { name: 'Register' }))
    const dialog = await screen.findByRole('dialog', { name: 'Register a supplier' })
    expect(within(dialog).getByLabelText('Name')).toHaveValue('Hugging Face')
    await userEvent.click(within(dialog).getByRole('button', { name: 'confidential' }))
    await userEvent.click(within(dialog).getByRole('button', { name: 'Save' }))
    await waitFor(() => expect(calls.some((c) => c.method === 'POST' && c.path === '/api/admin/compliance/suppliers')).toBe(true))
    expect(calls.find((c) => c.method === 'POST')!.body).toMatchObject({ name: 'Hugging Face', kind: 'model', source: 'huggingface', data: ['confidential'] })
  })
})
