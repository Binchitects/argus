import { screen, waitFor, within } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { describe, expect, it } from 'vitest'
import { admin, fakeApi, member, renderApp } from '@/test/utils'
import type { DataRequest } from './compliance-api'

const request = (over: Partial<DataRequest>): DataRequest => ({
  id: 'd1', userId: 'm1', person: 'mo', email: 'mo@example.test', kind: 'erasure', details: 'Please delete me.', status: 'open',
  receivedAt: new Date().toISOString(), dueAt: new Date(Date.now() + 29 * 86_400_000).toISOString(), receivedBy: 'themselves',
  doneAt: null, doneBy: null, outcome: null, certificate: false, ...over,
})

describe('data requests', () => {
  it('a person asks from Your account and sees how it stands', async () => {
    let mine: DataRequest[] = []
    const calls = fakeApi(member, {
      'GET /api/account/data-requests': () => ({ json: { restrictedAt: null, requests: mine } }),
      'POST /api/account/data-requests': (body) => {
        mine = [request({ kind: (body as { kind: DataRequest['kind'] }).kind, details: (body as { details: string }).details })]
        return { json: mine[0] }
      },
    })
    renderApp('/account')
    const card = (await screen.findByText('Your data rights')).closest('#your-data-rights') as HTMLElement
    await userEvent.click(within(card).getByRole('combobox'))
    await userEvent.click(await screen.findByRole('option', { name: 'A correction' }))
    await userEvent.type(within(card).getByLabelText('Anything to add (optional)'), 'My surname is spelt Smyth.')
    await userEvent.click(within(card).getByRole('button', { name: 'Send the request' }))
    await waitFor(() => expect(calls.find((c) => c.path === '/api/account/data-requests' && c.method === 'POST')?.body).toEqual({ kind: 'rectification', details: 'My surname is spelt Smyth.' }))
    const list = await within(card).findByRole('list')
    expect(within(list).getByText('A correction')).toBeInTheDocument()
    expect(within(list).getByText(/^Due /)).toBeInTheDocument()
  })

  it('an admin erases after a clear yes, and answers others with what was done', async () => {
    let all = [request({}), request({ id: 'd2', kind: 'rectification', details: 'Surname is Smyth.' })]
    const calls = fakeApi(admin, {
      'GET /api/admin/compliance': () => ({ json: { frameworks: {}, controls: [], checks: [], checkedAt: new Date().toISOString() } }),
      'GET /api/admin/compliance/reviews': () => ({ json: [] }),
      'GET /api/admin/compliance/data-requests': () => ({ json: all }),
      'POST /api/admin/compliance/data-requests/d1/erase': () => {
        all = [request({ status: 'done', userId: null, outcome: 'Erased: 3 chats.', doneBy: 'admin', certificate: true }), all[1]!]
        return { json: all[0] }
      },
      'POST /api/admin/compliance/data-requests/d2/complete': (body) => {
        all = [all[0]!, request({ id: 'd2', kind: 'rectification', status: 'done', outcome: (body as { outcome: string }).outcome, doneBy: 'admin' })]
        return { json: all[1] }
      },
    })
    renderApp('/admin/compliance')
    const section = (await screen.findByRole('heading', { name: 'Data requests' })).closest('section')!
    await userEvent.click(await within(section).findByRole('button', { name: 'Erase everything' }))
    const dialog = await screen.findByRole('alertdialog')
    expect(dialog).toHaveTextContent('The audit log keeps naming them')
    await userEvent.click(within(dialog).getByRole('button', { name: 'Erase everything' }))
    expect(await within(section).findByRole('link', { name: /Certificate/ })).toHaveAttribute('href', '/api/admin/compliance/data-requests/d1/certificate')

    await userEvent.click(within(section).getByRole('button', { name: 'Answered…' }))
    await userEvent.type(await screen.findByLabelText('What was done'), 'Profile corrected.')
    await userEvent.click(screen.getByRole('button', { name: 'Answer it' }))
    await waitFor(() => expect(calls.find((c) => c.path === '/api/admin/compliance/data-requests/d2/complete')?.body).toEqual({ outcome: 'Profile corrected.' }))
    expect(await within(section).findByText('Profile corrected.')).toBeInTheDocument()
  })
})
