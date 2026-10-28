import { screen, waitFor, within } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { describe, expect, it } from 'vitest'
import { admin, fakeApi, member, renderApp } from '@/test/utils'

const item = {
  id: 'f1', kind: 'problem', text: 'grep fails on Windows', page: 'Code Arena: the IDE', status: 'new', answer: null, answeredAt: null, createdAt: '2026-10-28T10:00:00Z',
  person: { id: 'p1', userName: 'ann', displayName: 'Ann' }, answeredBy: null,
}

describe('feedback', () => {
  it('lists what people sent, by status, and answers and marks it', async () => {
    const calls = fakeApi(admin, {
      'GET /api/admin/feedback': () => ({ json: { items: [item], counts: { new: 1, read: 0, done: 4 } } }),
      'PUT /api/admin/feedback/f1': (body) => ({ json: { id: 'f1', status: 'read', ...(body as object) } }),
    })
    renderApp('/admin/feedback')
    const list = await screen.findByRole('list', { name: 'Feedback' })
    expect(within(list).getByText('grep fails on Windows')).toBeInTheDocument()
    expect(within(list).getByText('from Code Arena: the IDE')).toBeInTheDocument()
    expect(screen.getByRole('radio', { name: 'Done (4)' })).toBeInTheDocument()
    await userEvent.type(within(list).getByRole('textbox', { name: 'Answer Ann' }), 'Fixed in the next release.')
    await userEvent.click(within(list).getByRole('button', { name: 'Answer' }))
    await userEvent.click(within(list).getByRole('button', { name: 'Done' }))
    await waitFor(() =>
      expect(calls.filter((c) => c.method === 'PUT').map((c) => c.body)).toEqual([{ answer: 'Fixed in the next release.' }, { status: 'done' }]),
    )
  })

  it('anyone sends feedback from their menu, with the page they are on', async () => {
    const calls = fakeApi(member, { 'POST /api/feedback': () => ({ status: 201, json: { id: 'f2' } }) })
    renderApp('/account')
    await userEvent.click(await screen.findByRole('button', { name: /Account menu for/ }))
    await userEvent.click(await screen.findByRole('menuitem', { name: 'Send feedback' }))
    const dialog = await screen.findByRole('dialog', { name: 'Send feedback' })
    await userEvent.click(within(dialog).getByRole('radio', { name: 'A problem' }))
    await userEvent.type(within(dialog).getByLabelText('What you want to say'), 'The menu is hard to read.')
    await userEvent.click(within(dialog).getByRole('button', { name: 'Send' }))
    await waitFor(() => expect(calls.find((c) => c.path === '/api/feedback')?.body).toEqual({ kind: 'problem', text: 'The menu is hard to read.', page: '/account' }))
  })
})
