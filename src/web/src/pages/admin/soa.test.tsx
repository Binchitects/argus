import { screen, waitFor, within } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { describe, expect, it } from 'vitest'
import { admin, fakeApi, renderApp } from '@/test/utils'

const view = { frameworks: { iso27001: 'ISO/IEC 27001:2022' }, coverage: {}, checks: [], checkedAt: new Date().toISOString(),
  controls: [{ id: 'IAM-03', title: 'Two-factor', meets: '', frameworks: {}, docs: [], tests: [], checks: [], operator: null, state: 'warn', attested: null }] }

const line = (clause: string, over = {}) => ({ clause, title: `Control ${clause}`, decided: false, applicable: true, status: null, justification: null, updatedBy: null, updatedAt: null, controls: [], risks: [], ...over })

describe('Admin → Compliance → Statement of Applicability', () => {
  it('shows each control with what serves it, and saves a decision', async () => {
    const calls = fakeApi(admin, {
      'GET /api/admin/compliance': () => ({ json: view }),
      'GET /api/admin/compliance/soa/iso27001': () => ({ json: { framework: 'iso27001', title: 'ISO 27001', approved: null, changedSinceApproval: false, versions: [],
        lines: [line('A.8.5', { controls: ['IAM-03'], risks: [{ id: 'r1', title: 'An account is taken over' }] }), line('A.7.1', { decided: true, applicable: false, status: 'not_implemented', justification: 'No premises', updatedBy: 'ciso' })] } }),
      'PUT /api/admin/compliance/soa/iso27001/A.8.5': () => ({ json: {} }),
    })
    renderApp('/admin/compliance?tab=soa')
    expect(await screen.findByText('Control A.8.5')).toBeInTheDocument()
    expect(screen.getByText('1 of 2 decided, 1 excluded')).toBeInTheDocument()
    expect(screen.getByText('not approved yet')).toBeInTheDocument()
    expect(screen.getByText('treats 1 risk')).toBeInTheDocument()
    const row = screen.getByText('Control A.8.5').closest('tr')!
    await userEvent.type(within(row).getByLabelText('A.8.5: why'), 'Admins only{Enter}')
    await waitFor(() => expect(calls.some((c) => c.method === 'PUT')).toBe(true))
    // Served by a control: in place, until the company says otherwise.
    expect(calls.find((c) => c.method === 'PUT')!.body).toMatchObject({ applicable: true, status: 'implemented', justification: 'Admins only' })
    expect(screen.getByRole('link', { name: 'Download' })).toHaveAttribute('href', '/api/admin/compliance/soa/iso27001/csv')
  })
})
