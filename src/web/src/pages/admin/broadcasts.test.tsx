import { screen, waitFor, within } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { describe, expect, it } from 'vitest'
import { admin, fakeApi, member, renderApp } from '@/test/utils'

describe('broadcasts', () => {
  it('shows above every page until dismissed; a critical one has no X', async () => {
    const calls = fakeApi(member, {
      'GET /api/broadcasts': () => ({
        json: [
          { id: 'b1', title: 'Maintenance tonight', body: 'At 22:00.', level: 'warning', createdAt: '2026-11-10T10:00:00Z', until: null, dismissable: true },
          { id: 'b2', title: 'Outage now', body: null, level: 'critical', createdAt: '2026-11-10T11:00:00Z', until: null, dismissable: false },
        ],
      }),
      'POST /api/broadcasts/b1/dismiss': () => ({ status: 204 }),
    })
    renderApp('/account')
    const banner = await screen.findByRole('region', { name: 'Messages from the admins' })
    expect(within(banner).getByText('Maintenance tonight')).toBeInTheDocument()
    expect(within(banner).getByRole('alert')).toHaveTextContent('Outage now')
    expect(within(banner).queryByRole('button', { name: 'Dismiss: Outage now' })).toBeNull()
    await userEvent.click(within(banner).getByRole('button', { name: 'Dismiss: Maintenance tonight' }))
    await waitFor(() => expect(within(banner).queryByText('Maintenance tonight')).toBeNull())
    expect(calls.some((c) => c.method === 'POST' && c.path === '/api/broadcasts/b1/dismiss')).toBe(true)
  })

  it('an admin sends one to some groups, and ends one that is on', async () => {
    const calls = fakeApi(admin, {
      'GET /api/admin/groups': () => ({ json: [{ id: 'g1', name: 'Research', members: 3 }] }),
      'GET /api/admin/broadcasts': () => ({
        json: [{ id: 'b1', title: 'Old news', body: null, level: 'info', createdAt: '2026-11-09T10:00:00Z', until: null, endedAt: null, sent: 12, on: true, groups: [], by: 'Ada', dismissed: 4 }],
      }),
      'POST /api/admin/broadcasts': () => ({ status: 201, json: { id: 'b2', sent: 3 } }),
      'POST /api/admin/broadcasts/b1/end': () => ({ status: 204 }),
    })
    renderApp('/admin/broadcasts')
    const form = await screen.findByRole('form', { name: 'New broadcast' })
    await userEvent.type(within(form).getByLabelText('Title'), 'Upgrade on Friday')
    await userEvent.click(within(form).getByRole('radio', { name: 'Warning' }))
    await userEvent.click(await within(form).findByRole('checkbox', { name: 'Research' }))
    await userEvent.click(within(form).getByRole('button', { name: 'Send' }))
    await waitFor(() =>
      expect(calls.find((c) => c.method === 'POST' && c.path === '/api/admin/broadcasts')?.body).toEqual({ title: 'Upgrade on Friday', body: null, level: 'warning', groups: ['g1'], until: null }),
    )
    const sent = screen.getByRole('list', { name: 'Broadcasts sent' })
    expect(within(sent).getByText(/12 people · 4 dismissed · by Ada/)).toBeInTheDocument()
    await userEvent.click(within(sent).getByRole('button', { name: 'End it' }))
    await userEvent.click(await screen.findByRole('button', { name: 'End it' }))
    await waitFor(() => expect(calls.some((c) => c.path === '/api/admin/broadcasts/b1/end')).toBe(true))
  })
})
