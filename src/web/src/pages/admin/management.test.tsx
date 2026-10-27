import { screen, waitFor, within } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { describe, expect, it } from 'vitest'
import { admin, fakeApi, renderApp } from '@/test/utils'

const view = {
  frameworks: {},
  coverage: {},
  controls: [{ id: 'IAM-01', title: 'Two factors', area: 'iam', description: '', frameworks: {}, evidence: [], checks: ['two-factor'], state: 'ok', attestation: null }],
  checks: [{ id: 'two-factor', title: 'Two factors for admins', state: 'ok', detail: '' }],
  checkedAt: new Date().toISOString(),
}

describe('Admin → Compliance → Management', () => {
  it('draws up a review from the inputs, measures objectives and answers auditors', async () => {
    const calls = fakeApi(admin, {
      'GET /api/admin/compliance': () => ({ json: view }),
      'GET /api/admin/compliance/owners': () => ({ json: { 'IAM-01': 'Security lead' } }),
      'GET /api/admin/compliance/management-reviews': () => ({ json: [] }),
      'GET /api/admin/compliance/management-reviews/inputs': () => ({ json: { inputs: '# Management review: inputs\n\n## Actions from the review before\nThis is the first review.' } }),
      'POST /api/admin/compliance/management-reviews': () => ({ json: {} }),
      'GET /api/admin/compliance/objectives': () => ({
        json: [{ id: 'o1', title: 'Admins sign in with two factors', check: 'two-factor', target: 95, owner: null, achieved: 80, days: 30, met: false }],
      }),
      'GET /api/admin/compliance/evidence-requests': () => ({
        json: [{ id: 'r1', text: 'The last access review', clause: 'A.5.18', requestedBy: 'auditor', requestedAt: new Date().toISOString(), answer: null, answeredBy: null, answeredAt: null }],
      }),
      'POST /api/admin/compliance/evidence-requests/r1/answer': () => ({ json: {} }),
    })
    renderApp('/admin/compliance?tab=management')
    expect(await screen.findByText(/80% of 30 days, target 95%/)).toBeInTheDocument()
    expect(await screen.findByLabelText('IAM-01 owner')).toHaveValue('Security lead')

    await userEvent.type(screen.getByLabelText('Answer'), 'In the evidence zip, access-reviews/')
    await userEvent.click(screen.getByRole('button', { name: 'Answer' }))
    await waitFor(() => expect(calls.find((c) => c.path === '/api/admin/compliance/evidence-requests/r1/answer')?.body).toEqual({ answer: 'In the evidence zip, access-reviews/' }))

    await userEvent.click(screen.getByRole('button', { name: /record a review/ }))
    const dialog = await screen.findByRole('dialog', { name: 'A management review' })
    expect(await within(dialog).findByText('This is the first review.')).toBeInTheDocument()
    const record = within(dialog).getByRole('button', { name: 'Record it' })
    expect(record).toBeDisabled()
    await userEvent.type(within(dialog).getByLabelText('Who took part'), 'CEO, CISO')
    await userEvent.type(within(dialog).getByLabelText('Decisions'), 'Budget for a second region')
    await userEvent.click(record)
    await waitFor(() => {
      const body = calls.find((c) => c.method === 'POST' && c.path === '/api/admin/compliance/management-reviews')?.body as { attendees: string; decisions: string }
      expect(body).toMatchObject({ attendees: 'CEO, CISO', decisions: 'Budget for a second region' })
    })
  })
})
