import { screen, waitFor, within } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { describe, expect, it } from 'vitest'
import { admin, fakeApi, renderApp } from '@/test/utils'
import { manualLink, tally, type ComplianceView, type Control } from './compliance-api'

const control = (over: Partial<Control>): Control => ({
  id: 'IAM-01', title: 'Each person signs in as themselves', meets: 'Strong passwords.', frameworks: { iso27001: ['A.5.17'], soc2: ['CC6.1'] },
  docs: ['authentication.md#what-protects-the-sign-in'], tests: ['Llm.Tests/IdentityTests.Wrong_password_and_unknown_name_get_the_same_answer'],
  checks: ['app:passwords'], operator: 'A password policy people are told about.', state: 'ok', attested: null, ...over,
})

const view: ComplianceView = {
  frameworks: { iso27001: 'ISO/IEC 27001:2022, Annex A', soc2: 'SOC 2, Trust Services Criteria', hipaa: 'HIPAA, 45 CFR Part 164', gdpr: 'GDPR (EU) 2016/679',
    euaiact: 'EU AI Act, Regulation (EU) 2024/1689' },
  coverage: { iso27001: { title: 'ISO/IEC 27001:2022, clauses 4 to 10 and Annex A', clauses: 123, served: 2 } },
  checkedAt: new Date().toISOString(),
  retention: [{ what: 'The audit log', rule: 'Kept 6 years, then deleted by the hourly sweep.' }],
  checks: [
    { id: 'passwords', title: 'Strong passwords and lockout', state: 'ok', detail: 'Passwords score 3 or more.' },
    { id: 'two-factor', title: 'Admins sign in with two factors', state: 'warn', detail: '1 of 1 admins has no second factor: admin.' },
  ],
  controls: [
    control({}),
    control({ id: 'IAM-03', title: 'Two-factor sign-in', frameworks: { iso27001: ['A.8.5'], hipaa: ['164.312(d)'] }, checks: ['app:two-factor'], state: 'warn',
      attested: { actor: 'cora', at: '2025-01-01T00:00:00Z', note: 'MFA policy v1', due: '2026-01-01T00:00:00Z' } }),
    control({ id: 'NET-01', title: 'Encrypted in transit', frameworks: { gdpr: ['Art.32(1)(a)'] }, checks: ['live:tls'], state: null, docs: ['compliance/README.md#checking-a-live-stack'] }),
  ],
}

describe('Admin → Compliance', () => {
  it('shows each framework, the checks now and the controls, filtered by a framework', async () => {
    fakeApi(admin, { 'GET /api/admin/compliance': () => ({ json: view }) })
    renderApp('/admin/compliance')
    const iso = await screen.findByRole('button', { name: /ISO 27001/ })
    expect(iso).toHaveTextContent('2 controls, serving 2 of its 123 clauses')
    // Grouped: security, privacy and AI here; only the frameworks the controls file names.
    expect(screen.getByRole('heading', { name: 'AI' })).toBeInTheDocument()
    expect(screen.getByRole('button', { name: /EU AI Act/ })).toHaveTextContent('0 controls, 0 clauses')
    expect(screen.queryByRole('button', { name: /PCI DSS/ })).not.toBeInTheDocument()
    expect(iso).toHaveTextContent('1 met')
    expect(iso).toHaveTextContent('1 in part')
    // An attestation older than a year no longer counts.
    expect(iso).toHaveTextContent('0 of 2 attested')
    expect(screen.getByText('1 of 1 admins has no second factor: admin.')).toBeInTheDocument()
    expect(within(screen.getByRole('heading', { name: 'How long things are kept' }).closest('section')!).getByText('Kept 6 years, then deleted by the hourly sweep.')).toBeInTheDocument()

    const net = screen.getByText('Encrypted in transit').closest('[id="NET-01"]') as HTMLElement
    expect(within(net).getByText('No live check here')).toBeInTheDocument()
    expect(within(net).getByRole('link', { name: /checking a live stack/ })).toHaveAttribute('href', '/help/compliance#checking-a-live-stack')
    const iam03 = document.getElementById('IAM-03')!
    expect(within(iam03).getByText(/Due again: attested by cora/)).toBeInTheDocument()

    await userEvent.click(screen.getByRole('button', { name: /HIPAA/ }))
    expect(screen.queryByText('Each person signs in as themselves')).not.toBeInTheDocument()
    expect(screen.getByText('Two-factor sign-in')).toBeInTheDocument()
    expect(screen.getByRole('link', { name: /Download evidence/ })).toHaveAttribute('href', '/api/admin/compliance/evidence')
  })

  it('attests the company’s part with what was done', async () => {
    const calls = fakeApi(admin, {
      'GET /api/admin/compliance': () => ({ json: view }),
      'POST /api/admin/compliance/controls/IAM-01/attest': () => ({ json: { actor: 'admin', at: new Date().toISOString(), note: 'Policy v2', due: '2099-01-01T00:00:00Z' } }),
    })
    renderApp('/admin/compliance')
    const card = (await screen.findByText('Each person signs in as themselves')).closest('[id="IAM-01"]') as HTMLElement
    await userEvent.click(within(card).getByRole('button', { name: 'Attest' }))
    const dialog = await screen.findByRole('dialog')
    expect(within(dialog).getByRole('button', { name: 'Attest' })).toBeDisabled()
    await userEvent.type(within(dialog).getByLabelText(/What was done/), 'Policy v2 in the ISMS folder')
    await userEvent.click(within(dialog).getByRole('button', { name: 'Attest' }))
    await waitFor(() => expect(calls.find((c) => c.method === 'POST')?.body).toEqual({ note: 'Policy v2 in the ISMS folder' }))
  })
})

describe('compliance helpers', () => {
  it('links a control’s document into the manual', () => {
    expect(manualLink('authentication.md#what-protects-the-sign-in')).toBe('/help/authentication#what-protects-the-sign-in')
    expect(manualLink('compliance/README.md#controls-and-how-they-are-kept')).toBe('/help/compliance#controls-and-how-they-are-kept')
    expect(manualLink('admin.md')).toBe('/help/admin')
  })

  it('counts a framework’s controls by state and the attestations that still count', () => {
    const now = new Date('2026-06-01T00:00:00Z')
    const fresh = control({ id: 'IAM-02', state: 'fail', attested: { actor: 'cora', at: '2026-01-01T00:00:00Z', note: 'x', due: '2027-01-01T00:00:00Z' } })
    expect(tally([control({}), fresh, control({ id: 'X', frameworks: { gdpr: ['Art.32'] } })], 'soc2', now)).toEqual({ controls: 2, clauses: 1, ok: 1, warn: 0, fail: 1, attested: 1 })
  })
})
