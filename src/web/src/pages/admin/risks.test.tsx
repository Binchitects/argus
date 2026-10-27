import { screen, waitFor, within } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { describe, expect, it } from 'vitest'
import { admin, fakeApi, renderApp } from '@/test/utils'
import type { Risk } from './risks'

const view = { frameworks: { iso27001: 'ISO/IEC 27001:2022' }, coverage: {}, controls: [{ id: 'IAM-01', title: 'x', meets: '', frameworks: {}, docs: [], tests: [], checks: [], operator: null, state: null, attested: null }, { id: 'IAM-03', title: 'y', meets: '', frameworks: {}, docs: [], tests: [], checks: [], operator: null, state: null, attested: null }], checks: [], checkedAt: new Date().toISOString() }

const risk = (over: Partial<Risk> = {}): Risk => ({
  id: 'r1', title: 'An admin account is taken over', description: 'A phished password.', category: 'security', assets: ['The audit log'], threat: 'Phishing',
  likelihood: 3, impact: 5, score: 15, level: 'high', owner: 'ciso', treatment: 'reduce', controls: ['IAM-03'], plan: null, residualLikelihood: 1, residualImpact: 5,
  residualScore: 5, residualLevel: 'medium', acceptedBy: null, acceptedWhy: null, acceptedUntil: null, reviewDue: '2099-01-01T00:00:00Z', reviewOverdue: false,
  closedAt: null, createdBy: 'admin', createdAt: '2026-10-27T00:00:00Z', updatedAt: '2026-10-27T00:00:00Z', openActions: 0, lateActions: 0, ...over,
})

describe('Admin → Compliance → Risks', () => {
  it('shows the heat map and the register, a cell filtering the list', async () => {
    fakeApi(admin, {
      'GET /api/admin/compliance': () => ({ json: view }),
      'GET /api/admin/compliance/risks': () => ({ json: [risk(), risk({ id: 'r2', title: 'Backups are lost', likelihood: 4, impact: 5, score: 20, level: 'critical', residualLikelihood: null, residualImpact: null, residualScore: null, residualLevel: null, reviewOverdue: true, reviewDue: '2026-01-01T00:00:00Z' })] }),
    })
    renderApp('/admin/compliance?tab=risks')
    expect(await screen.findByText('An admin account is taken over')).toBeInTheDocument()
    expect(screen.getByText('2 open, 1 with a review or an action past due. A cell filters the list.')).toBeInTheDocument()
    // As left after treatment: the first at 1×5, the second (not assessed yet) at 4×5.
    await userEvent.click(screen.getByRole('button', { name: 'Likelihood 4, impact 5: 1 risk' }))
    expect(screen.queryByText('An admin account is taken over')).not.toBeInTheDocument()
    expect(screen.getByText('Backups are lost')).toBeInTheDocument()
    await userEvent.click(screen.getByRole('button', { name: 'Before' }))
    expect(screen.getByRole('button', { name: 'Likelihood 3, impact 5: 1 risk' })).toBeInTheDocument()
  })

  it('registers a risk with its assets, levels and controls', async () => {
    const calls = fakeApi(admin, {
      'GET /api/admin/compliance': () => ({ json: view }),
      'GET /api/admin/compliance/risks': () => ({ json: [] }),
      'GET /api/admin/compliance/risks/assets': () => ({ json: [{ group: 'Arena', name: "Arena's database (Postgres)" }] }),
      'POST /api/admin/compliance/risks': () => ({ json: risk() }),
    })
    renderApp('/admin/compliance?tab=risks')
    expect(await screen.findByText(/The register is empty/)).toBeInTheDocument()
    await userEvent.click(screen.getByRole('button', { name: /Register a risk/ }))
    const dialog = await screen.findByRole('dialog', { name: 'Register a risk' })
    await userEvent.type(within(dialog).getByLabelText('The risk'), 'Data is lost')
    await userEvent.type(within(dialog).getByLabelText(/What it threatens/), "Arena's database (Postgres){Enter}")
    await userEvent.click(within(dialog).getByRole('button', { name: 'IAM-01' }))
    expect(within(dialog).getByText(/Level now:/)).toHaveTextContent('Level now: medium (9). Reviewed again in a year.')
    await userEvent.click(within(dialog).getByRole('button', { name: 'Register it' }))
    await waitFor(() => expect(calls.some((c) => c.method === 'POST' && c.path.endsWith('/api/admin/compliance/risks'))).toBe(true))
    const body = calls.find((c) => c.method === 'POST' && c.path.endsWith('/api/admin/compliance/risks'))!.body as Record<string, unknown>
    expect(body).toMatchObject({ title: 'Data is lost', assets: ["Arena's database (Postgres)"], controls: ['IAM-01'], likelihood: 3, impact: 3, residualLikelihood: null })
  })

  it('opens a risk with its history, and reviews it', async () => {
    const calls = fakeApi(admin, {
      'GET /api/admin/compliance': () => ({ json: view }),
      'GET /api/admin/compliance/risks': () => ({ json: [risk()] }),
      'GET /api/admin/compliance/risks/r1': () => ({ json: { risk: risk(), actions: [{ id: 'a1', text: 'Two-factor for admins', owner: 'it', due: '2099-01-01T00:00:00Z', doneAt: null, doneBy: null }], events: [{ id: 'e1', at: '2026-10-27T00:00:00Z', by: 'admin', kind: 'created', text: 'security, 3×5 (high)' }] } }),
      'POST /api/admin/compliance/risks/r1/review': () => ({ json: risk() }),
    })
    renderApp('/admin/compliance?tab=risks')
    await userEvent.click(await screen.findByRole('button', { name: /An admin account is taken over/ }))
    const dialog = await screen.findByRole('dialog', { name: 'An admin account is taken over' })
    expect(within(dialog).getByText('Two-factor for admins')).toBeInTheDocument()
    expect(within(dialog).getByText(/Registered/)).toBeInTheDocument()
    await userEvent.click(within(dialog).getByRole('button', { name: 'Review' }))
    const review = await screen.findByRole('dialog', { name: 'Review' })
    await userEvent.type(within(review).getByLabelText('What the review found'), 'Two-factor is required now')
    await userEvent.click(within(review).getByRole('button', { name: 'Review' }))
    await waitFor(() => expect(calls.some((c) => c.path.endsWith('/risks/r1/review'))).toBe(true))
    expect(calls.find((c) => c.path.endsWith('/risks/r1/review'))!.body).toMatchObject({ note: 'Two-factor is required now' })
  })
})
