import { screen, waitFor, within } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { describe, expect, it } from 'vitest'
import { admin, fakeApi, member, renderApp } from '@/test/utils'
import { monthOf, type GroupDetail } from './groups-api'
import type { Person } from './people-api'

const group: GroupDetail = {
  id: 'g1', name: 'Data science', description: null, directory: null, scim: false, priority: 0, createdAt: '',
  members: [{ id: 'p1', userName: 'ann', displayName: 'Ann', email: 'ann@example.test', isDisabled: false, spend: 3.5 }],
  policies: {
    retentionDays: null, credit: 50, creditPerMember: false, costCentre: null, secretScanning: null, redactPii: null, moderation: null, blockedPatterns: null,
    requestsPerMinute: null, tokensPerMinute: null,
  },
  spentThisMonth: 3.5,
}

const grace: Person = {
  id: 'p1', userName: 'grace', displayName: 'Grace Hopper', email: 'grace@example.test', isAdmin: false, source: 'local',
  disabled: false, disabledReason: null, twoFactorEnabled: false, lockedOut: false, lastSignInAt: null,
  createdAt: '2026-01-01T00:00:00Z', spend: 3, budget: 10, legalHoldSince: null, legalHoldReason: null,
}

describe('retention, credit and safeguards per group', () => {
  it('a group keeps chats, spends and checks as its policies say, and they are saved together', async () => {
    const calls = fakeApi(admin, {
      'GET /api/admin/groups/g1': () => ({ json: group }),
      'PUT /api/admin/groups/g1/policies': () => ({ status: 204 }),
    })
    renderApp('/admin/groups/g1')
    const card = (await screen.findByText('Policies')).closest('section')!
    expect(await screen.findByText('Spent this month: $3.50 of $50.00')).toBeInTheDocument()
    // Each member's spend this month.
    expect(screen.getByRole('button', { name: /This month/ })).toBeInTheDocument()

    await userEvent.type(within(card).getByLabelText('Keep chats for (days)'), '90')
    await userEvent.type(within(card).getByLabelText('Cost centre'), 'CC-42')
    await userEvent.click(within(card).getByRole('radio', { name: 'Each member’s' }))
    await userEvent.click(within(card).getByRole('combobox', { name: 'Secrets in messages and files' }))
    await userEvent.click(await screen.findByRole('option', { name: 'Mask each secret' }))
    await userEvent.click(within(card).getByRole('combobox', { name: 'Blocked words' }))
    await userEvent.click(await screen.findByRole('option', { name: 'Not for this group' }))
    await userEvent.click(within(card).getByRole('button', { name: 'Save policies' }))
    await waitFor(() =>
      expect(calls.find((c) => c.method === 'PUT')?.body).toEqual({
        retentionDays: 90, credit: 50, creditPerMember: true, costCentre: 'CC-42', secretScanning: 'mask', redactPii: null, moderation: null, blockedPatterns: false,
        requestsPerMinute: null, tokensPerMinute: null,
      }),
    )
  })

  it("a group's rate limits for its members' keys are saved with its policies, and a wrong one is caught", async () => {
    const calls = fakeApi(admin, {
      'GET /api/admin/groups/g1': () => ({ json: { ...group, policies: { ...group.policies!, requestsPerMinute: 30, tokensPerMinute: null } } }),
      'PUT /api/admin/groups/g1/policies': () => ({ status: 204 }),
    })
    renderApp('/admin/groups/g1')
    const requests = await screen.findByLabelText('Requests a minute, per key')
    expect(requests).toHaveValue('30')
    const tokens = screen.getByLabelText('Tokens a minute, per key')
    expect(tokens).toHaveValue('')
    expect(tokens).toHaveAttribute('placeholder', "the company's setting")

    await userEvent.type(tokens, '-5')
    await userEvent.click(screen.getByRole('button', { name: 'Save policies' }))
    expect(await screen.findByText(/Limits are whole numbers/)).toBeInTheDocument()
    expect(calls.some((c) => c.method === 'PUT')).toBe(false)

    await userEvent.clear(tokens)
    await userEvent.type(tokens, '200,000')
    await userEvent.clear(requests)
    await userEvent.type(requests, '0')
    await userEvent.click(screen.getByRole('button', { name: 'Save policies' }))
    await waitFor(() => expect(calls.find((c) => c.method === 'PUT')?.body).toMatchObject({ credit: 50, requestsPerMinute: 0, tokensPerMinute: 200000 }))
  })

  it('a wrong number of days is caught before it is sent', async () => {
    const calls = fakeApi(admin, { 'GET /api/admin/groups/g1': () => ({ json: { ...group, policies: undefined } }) })
    renderApp('/admin/groups/g1')
    await userEvent.type(await screen.findByLabelText('Keep chats for (days)'), '0')
    await userEvent.click(screen.getByRole('button', { name: 'Save policies' }))
    expect(await screen.findByText(/whole number of days/)).toBeInTheDocument()
    expect(calls.some((c) => c.method === 'PUT')).toBe(false)
  })

  it('the chargeback report lists spend per group and cost centre per month, with a CSV', async () => {
    const calls = fakeApi(admin, {
      'GET /api/admin/groups': () => ({ json: [{ id: 'g1', name: 'Data science', description: null, directory: null, members: 2, createdAt: '', credit: 50, creditPerMember: false, costCentre: 'CC-42', retentionDays: 30 }] }),
      'GET /api/admin/groups/chargeback': () => ({
        json: {
          from: '2026-08', to: '2026-10',
          rows: [
            { month: '2026-10', kind: 'group', name: 'Data science', costCentre: 'CC-42', members: 2, spend: 12.5, credit: 50 },
            { month: '2026-10', kind: 'cost centre', name: 'CC-42', costCentre: 'CC-42', members: 2, spend: 12.5, credit: null },
            { month: '2026-10', kind: 'none', name: '(in no group)', costCentre: null, members: 1, spend: 4, credit: null },
          ],
        },
      }),
    })
    renderApp('/admin/groups')
    const row = (await screen.findByText('Data science', { selector: 'p' })).closest('tr')!
    expect(within(row).getByText('30 days')).toBeInTheDocument()
    expect(within(row).getByText(/\$50\.00 shared/)).toBeInTheDocument()
    expect(await screen.findByText('(in no group)')).toBeInTheDocument()
    expect(screen.getAllByText('$12.50')).toHaveLength(2)
    const from = monthOf(new Date(), 2)
    const to = monthOf(new Date())
    expect(calls.some((c) => c.path === `/api/admin/groups/chargeback?from=${from}&to=${to}`)).toBe(true)
    expect(screen.getByRole('link', { name: 'CSV' })).toHaveAttribute('href', `/api/admin/groups/chargeback?from=${from}&to=${to}&format=csv`)
  })
})

describe('legal hold and exports', () => {
  it('a person is placed on legal hold with a reason, and their data exports for eDiscovery', async () => {
    let held = false
    const calls = fakeApi(admin, {
      'GET /api/admin/people/p1': () => ({
        json: { person: { ...grace, legalHoldSince: held ? '2026-10-04T10:00:00Z' : null, legalHoldReason: held ? 'Matter 42' : null }, keys: [], groups: [], directoryGroups: [], warning: null },
      }),
      'PUT /api/admin/people/p1/legal-hold': (body) => {
        held = (body as { hold: boolean }).hold
        return { json: { legalHoldSince: null, legalHoldReason: null, erasedChats: held ? 0 : 2 } }
      },
    })
    renderApp('/admin/people/p1')
    expect(await screen.findByText('Not on hold.')).toBeInTheDocument()
    expect(screen.getByRole('link', { name: 'Export their data' })).toHaveAttribute('href', '/api/admin/people/p1/export')

    await userEvent.click(screen.getByRole('button', { name: 'Place on legal hold' }))
    const dialog = await screen.findByRole('dialog', { name: 'Place Grace Hopper on legal hold?' })
    await userEvent.click(within(dialog).getByRole('button', { name: 'Place on hold' }))
    expect(await within(dialog).findByText('Say why the hold is placed.')).toBeInTheDocument()
    await userEvent.type(within(dialog).getByLabelText('Reason'), 'Matter 42')
    await userEvent.click(within(dialog).getByRole('button', { name: 'Place on hold' }))
    await waitFor(() => expect(calls.find((c) => c.method === 'PUT')?.body).toEqual({ hold: true, reason: 'Matter 42' }))
    expect(await screen.findByText('Matter 42')).toBeInTheDocument()
    // The badge beside their name, besides the card's title.
    expect(screen.getAllByText('Legal hold')).toHaveLength(2)

    await userEvent.click(screen.getByRole('button', { name: 'End the hold' }))
    await userEvent.click(within(await screen.findByRole('alertdialog')).getByRole('button', { name: 'End the hold' }))
    await waitFor(() => expect(calls.filter((c) => c.method === 'PUT').at(-1)?.body).toEqual({ hold: false }))
  })

  it('a person downloads their own data and sees how long chats are kept', async () => {
    fakeApi(member, {
      'GET /api/account/data': () => ({ json: { retentionDays: 90 } }),
      'GET /api/account/preferences': () => ({ json: { answerLength: 'normal' } }),
      'GET /api/account/2fa': () => ({ json: { enabled: false, recoveryCodesLeft: 0 } }),
      'GET /api/account/connections': () => ({ json: [] }),
    })
    renderApp('/account')
    expect(await screen.findByText(/Chats are kept for 90 days after their last message/)).toBeInTheDocument()
    expect(screen.getByRole('link', { name: 'Download your data' })).toHaveAttribute('href', '/api/account/export')
  })
})
