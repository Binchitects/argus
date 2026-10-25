import { screen, waitFor, within } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { describe, expect, it } from 'vitest'
import { admin, fakeApi, renderApp } from '@/test/utils'
import type { ReviewDetail, ReviewItem } from './compliance-api'

const item = (over: Partial<ReviewItem>): ReviewItem => ({
  id: 'i1', kind: 'role', subject: 'pat', what: 'People admin, given to them', decision: null, decidedBy: null, decidedAt: null, note: null, outcome: null, ...over,
})

const detail = (items: ReviewItem[], closedAt: string | null = null): ReviewDetail => ({
  review: { id: 'r1', title: 'Access review, 2026-10-26', createdAt: '2026-10-26T00:00:00Z', createdBy: 'the schedule', dueAt: '2099-01-01T00:00:00Z', closedAt, closedBy: closedAt ? 'cora' : null },
  items,
})

describe('an access review', () => {
  it('keeps or revokes each item, revoking after a yes, and closes once all are decided', async () => {
    let items = [item({}), item({ id: 'i2', kind: 'member', subject: 'mo', what: 'member of Ops, which holds Auditor' }), item({ id: 'i3', kind: 'unused', subject: 'old', what: 'last signed in 2026-01-01' })]
    const calls = fakeApi(admin, {
      'GET /api/admin/compliance/reviews/r1': () => ({ json: detail(items) }),
      'GET /api/admin/compliance/reviews': () => ({ json: [] }),
      'POST /api/admin/compliance/reviews/r1/items/i1': (body) => {
        const decided = { ...items[0]!, decision: (body as { decision: 'keep' | 'revoke' }).decision, decidedBy: 'admin', decidedAt: new Date().toISOString(), outcome: 'The role was taken away.' }
        items = [decided, ...items.slice(1)]
        return { json: decided }
      },
      'POST /api/admin/compliance/reviews/r1/items/i2': () => {
        items = items.map((i) => (i.id === 'i2' ? { ...i, decision: 'keep', decidedBy: 'admin', decidedAt: new Date().toISOString() } : i))
        return { json: items[1] }
      },
      'POST /api/admin/compliance/reviews/r1/items/i3': () => {
        items = items.map((i) => (i.id === 'i3' ? { ...i, decision: 'keep', decidedBy: 'admin', decidedAt: new Date().toISOString() } : i))
        return { json: items[2] }
      },
      'POST /api/admin/compliance/reviews/r1/close': () => ({ json: {} }),
    })
    renderApp('/admin/compliance/reviews/r1')
    expect(await screen.findByRole('heading', { name: 'Access review, 2026-10-26' })).toBeInTheDocument()
    for (const name of ['Privileged roles', 'Members of privileged groups', 'Unused accounts']) expect(screen.getByRole('heading', { name })).toBeInTheDocument()
    expect(screen.getByRole('button', { name: 'Close the review' })).toBeDisabled()

    const roles = screen.getByRole('heading', { name: 'Privileged roles' }).closest('section')!
    await userEvent.click(within(roles).getByRole('button', { name: 'Revoke' }))
    await userEvent.click(within(await screen.findByRole('alertdialog')).getByRole('button', { name: 'Revoke' }))
    expect(await within(roles).findByText('The role was taken away.')).toBeInTheDocument()
    expect(calls.find((c) => c.path === '/api/admin/compliance/reviews/r1/items/i1')?.body).toEqual({ decision: 'revoke' })

    await userEvent.click(screen.getByRole('button', { name: 'Keep the rest' }))
    await userEvent.click(within(await screen.findByRole('alertdialog')).getByRole('button', { name: 'Keep them' }))
    await waitFor(() => expect(screen.getByRole('button', { name: 'Close the review' })).toBeEnabled())
    await userEvent.click(screen.getByRole('button', { name: 'Close the review' }))
    await waitFor(() => expect(calls.some((c) => c.path === '/api/admin/compliance/reviews/r1/close')).toBe(true))
  })

  it('shows a closed review as it was, with nothing to change', async () => {
    fakeApi(admin, {
      'GET /api/admin/compliance/reviews/r1': () => ({ json: detail([item({ decision: 'keep', decidedBy: 'cora', decidedAt: '2026-10-27T00:00:00Z' })], '2026-10-28T00:00:00Z') }),
    })
    renderApp('/admin/compliance/reviews/r1')
    expect(await screen.findByText(/Closed by cora/)).toBeInTheDocument()
    expect(within(screen.getByRole('heading', { name: 'Privileged roles' }).closest('section')!).getByText('Kept')).toBeInTheDocument()
    expect(screen.queryByRole('button', { name: 'Revoke' })).not.toBeInTheDocument()
    expect(screen.queryByRole('button', { name: 'Close the review' })).not.toBeInTheDocument()
  })
})
