import { screen, waitFor, within } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { describe, expect, it } from 'vitest'
import { admin, fakeApi, renderApp } from '@/test/utils'
import type { Incident } from './compliance-api'

const incident = (over: Partial<Incident>): Incident => ({
  id: 'i1', title: 'Laptop stolen', summary: 'With an export on it.', severity: 'high', status: 'open', detectedAt: new Date(Date.now() - 80 * 3_600_000).toISOString(),
  openedBy: 'admin', personalData: true, healthData: false, authorityDecidedAt: null, authorityNotified: null, peopleNotifiedAt: null, closedAt: null, lessons: null,
  authorityDue: new Date(Date.now() - 8 * 3_600_000).toISOString(), peopleDue: null, ...over,
})

describe('incidents', () => {
  it('opens one from an alert with what is known', async () => {
    const calls = fakeApi(admin, {
      'GET /api/admin/compliance': () => ({ json: { frameworks: {}, controls: [], checks: [], checkedAt: new Date().toISOString() } }),
      'GET /api/admin/compliance/reviews': () => ({ json: [] }),
      'GET /api/admin/compliance/data-requests': () => ({ json: [] }),
      'GET /api/admin/compliance/incidents': () => ({ json: { alerts: [{ id: 'a1', kind: 'mass_export', title: 'eve exported data 5 times in an hour', detail: 'Is it their job?', at: new Date().toISOString(), incidentId: null }], incidents: [] } }),
      'POST /api/admin/compliance/incidents': () => ({ status: 201, json: incident({ id: 'i9' }) }),
      'GET /api/admin/compliance/incidents/i9': () => ({ json: { incident: incident({ id: 'i9' }), events: [], alerts: [] } }),
    })
    renderApp('/admin/compliance?tab=incidents')
    const section = (await screen.findByRole('heading', { name: 'Security alerts and incidents' })).closest('section')!
    const alert = (await within(section).findByText('eve exported data 5 times in an hour')).closest('li')!
    await userEvent.click(within(alert).getByRole('button', { name: 'Open an incident' }))
    const dialog = await screen.findByRole('dialog')
    expect(within(dialog).getByLabelText('What happened')).toHaveValue('eve exported data 5 times in an hour')
    await userEvent.click(within(dialog).getByLabelText(/Personal data may be involved/))
    await userEvent.click(within(dialog).getByRole('button', { name: 'Open it' }))
    await waitFor(() => expect(calls.find((c) => c.method === 'POST')?.body).toMatchObject({ alertId: 'a1', personalData: true, healthData: false, severity: 'medium' }))
    expect(await screen.findByRole('heading', { name: 'Laptop stolen' })).toBeInTheDocument()
  })

  it('shows the law’s clock, late, and records a step on the timeline', async () => {
    let current = incident({})
    const calls = fakeApi(admin, {
      'GET /api/admin/compliance/incidents/i1': () => ({ json: { incident: current, events: [{ id: 'e1', at: current.detectedAt, by: 'admin', kind: 'opened', text: null }], alerts: [] } }),
      'POST /api/admin/compliance/incidents/i1/steps': () => {
        current = { ...current, authorityDecidedAt: new Date().toISOString(), authorityNotified: true }
        return { json: current }
      },
    })
    renderApp('/admin/compliance/incidents/i1')
    expect(await screen.findByText(/8 hours late/)).toBeInTheDocument()
    await userEvent.click(screen.getByRole('button', { name: 'Authority told' }))
    await userEvent.type(await screen.findByLabelText('Which authority, when, its reference'), 'ICO, case 118')
    await userEvent.click(screen.getByRole('button', { name: 'Record it' }))
    await waitFor(() => expect(calls.find((c) => c.method === 'POST')?.body).toEqual({ kind: 'authority_notified', text: 'ICO, case 118' }))
    expect(await screen.findByText(/^done /)).toBeInTheDocument()
  })
})
