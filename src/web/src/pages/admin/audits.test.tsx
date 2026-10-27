import { screen, waitFor, within } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { describe, expect, it } from 'vitest'
import { admin, fakeApi, renderApp } from '@/test/utils'

const view = { frameworks: {}, coverage: {}, controls: [], checks: [], checkedAt: new Date().toISOString() }
const finding = { id: 'f1', auditId: 'a1', audit: 'Q3 internal audit', title: 'Two admins without a second factor', kind: 'minor', description: null, clause: 'iso27001 A.8.5', control: 'IAM-03',
  owner: 'it', due: '2026-01-01T00:00:00Z', late: true, correction: null, rootCause: 'Optional for admins', correctiveAction: 'Required', status: 'open', doneBy: null, verifiedBy: null, verification: null }

describe('Admin → Compliance → Audits', () => {
  it('shows the audits, findings, certificates and waivers, and takes a finding\'s action', async () => {
    const calls = fakeApi(admin, {
      'GET /api/admin/compliance': () => ({ json: view }),
      'GET /api/admin/compliance/audits': () => ({ json: {
        audits: [{ id: 'a1', title: 'Q3 internal audit', kind: 'internal', framework: 'iso27001', scope: null, auditor: 'IA', starts: '2026-09-01T00:00:00Z', ends: null, conclusion: null, reportedAt: null, findings: 1, openFindings: 1 }],
        findings: [finding],
        certificates: [{ id: 'c1', framework: 'iso27001', body: 'BSI', number: 'IS 1', scope: null, issued: '2024-01-01T00:00:00Z', expires: '2027-01-01T00:00:00Z', daysLeft: 60 }],
        waivers: [{ id: 'w1', control: 'iso27001 A.7.1', reason: "The provider's data centre", compensating: 'Its SOC 2', approvedBy: 'ciso', until: '2027-06-01T00:00:00Z', withdrawnAt: null, expired: false }],
      } }),
      'POST /api/admin/compliance/findings/f1/done': () => ({ json: { ...finding, status: 'done' } }),
    })
    renderApp('/admin/compliance?tab=audits')
    expect(await screen.findByText('Q3 internal audit')).toBeInTheDocument()
    expect(screen.getByText('1 finding, 1 open')).toBeInTheDocument()
    expect(screen.getByText('ISO 27001', { selector: 'span.flex-1' })).toBeInTheDocument()
    expect(screen.getByText("The provider's data centre")).toBeInTheDocument()
    const row = screen.getByRole('button', { name: /Two admins without a second factor/ })
    expect(within(row).getByText(/by /)).toHaveClass('text-destructive-ink')
    await userEvent.click(row)
    const dialog = await screen.findByRole('dialog', { name: 'Two admins without a second factor' })
    await userEvent.click(within(dialog).getByRole('button', { name: 'The action is taken' }))
    await waitFor(() => expect(calls.some((c) => c.path === '/api/admin/compliance/findings/f1/done')).toBe(true))
  })
})
