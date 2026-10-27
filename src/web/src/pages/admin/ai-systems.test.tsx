import { screen, waitFor, within } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { describe, expect, it } from 'vitest'
import { admin, fakeApi, renderApp } from '@/test/utils'

const view = { frameworks: {}, coverage: {}, controls: [], checks: [], checkedAt: new Date().toISOString() }
const system = { id: 's1', name: 'HR screening assistant', kind: 'assistant', source: null, purpose: 'Sorts applications', intendedUse: null, affected: null, class: 'high',
  classWhy: null, highRiskArea: 'employment and workers management', role: 'deployer', owner: null, oversight: null, evaluation: null, impactAssessment: null,
  impactAssessedAt: null, impactAssessedBy: null, inUseSince: null, reviewDue: '2027-01-01T00:00:00Z', reviewOverdue: false, retiredAt: null,
  missing: ['its impact assessment', 'its human oversight', 'its evaluation'] }

describe('Admin → Compliance → AI systems', () => {
  it('shows what a system needs before use, and puts it into use when asked', async () => {
    const calls = fakeApi(admin, {
      'GET /api/admin/compliance': () => ({ json: view }),
      'GET /api/admin/compliance/ai-systems': () => ({ json: { systems: [system], suggested: [{ source: 'assistant:a1', name: 'Policy helper', kind: 'assistant' }] } }),
      'POST /api/admin/compliance/ai-systems/s1/in-use': () => ({ status: 400, json: { status: 'ai-system', error: 'Not yet: its impact assessment.' } }),
    })
    renderApp('/admin/compliance?tab=ai')
    const row = await screen.findByRole('button', { name: /HR screening assistant/ })
    expect(row).toHaveTextContent('High-risk')
    expect(row).toHaveTextContent('3 to say first')
    expect(screen.getByText(/People use these/)).toBeInTheDocument()
    await userEvent.click(row)
    const dialog = await screen.findByRole('dialog', { name: 'HR screening assistant' })
    expect(within(dialog).getByText(/Before it is in use: its impact assessment; its human oversight; its evaluation/)).toBeInTheDocument()
    await userEvent.click(within(dialog).getByRole('button', { name: 'Put into use' }))
    await waitFor(() => expect(calls.some((c) => c.path === '/api/admin/compliance/ai-systems/s1/in-use')).toBe(true))
  })
})
