import { screen, waitFor, within } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { describe, expect, it, vi } from 'vitest'
import { admin, fakeApi, member, renderApp } from '@/test/utils'

const item = {
  id: 'f1', kind: 'problem', text: 'grep fails on Windows', page: 'Code Arena: the IDE', status: 'new', answer: null, answeredAt: null, createdAt: '2026-10-28T10:00:00Z',
  person: { id: 'p1', userName: 'ann', displayName: 'Ann' }, answeredBy: null, images: 2,
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
    // Its screenshots, each opening full size.
    expect(within(list).getByRole('link', { name: 'Screenshot 2, full size' })).toHaveAttribute('href', '/api/admin/feedback/f1/images/1')
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

  it('sends screenshots with it: a picture picked, shown small, removable, at most three', async () => {
    let made = 0
    vi.stubGlobal('URL', Object.assign(URL, { createObjectURL: () => `blob:shot-${++made}`, revokeObjectURL: () => {} }))
    const calls = fakeApi(member, { 'POST /api/feedback': () => ({ status: 201, json: { id: 'f3' } }) })
    renderApp('/account')
    await userEvent.click(await screen.findByRole('button', { name: /Account menu for/ }))
    await userEvent.click(await screen.findByRole('menuitem', { name: 'Send feedback' }))
    const dialog = await screen.findByRole('dialog', { name: 'Send feedback' })
    await userEvent.type(within(dialog).getByLabelText('What you want to say'), 'The chart is cut off.')
    const png = (name: string) => new File([new Uint8Array([0x89, 0x50, 0x4e, 0x47])], name, { type: 'image/png' })
    await userEvent.upload(within(dialog).getByLabelText('A screenshot to send'), [png('a.png'), png('b.png')])
    const shots = await within(dialog).findByRole('list', { name: 'Screenshots to send' })
    await waitFor(() => expect(within(shots).getAllByRole('img')).toHaveLength(2))
    await userEvent.click(within(shots).getByRole('button', { name: 'Remove screenshot 1' }))
    await waitFor(() => expect(within(shots).getAllByRole('img')).toHaveLength(1))
    await userEvent.click(within(dialog).getByRole('button', { name: 'Send' }))
    await waitFor(() =>
      expect(calls.find((c) => c.path === '/api/feedback')?.body).toEqual({ kind: 'idea', text: 'The chart is cut off.', page: '/account', images: [{ data: btoa('\x89PNG') }] }),
    )
  })
})
