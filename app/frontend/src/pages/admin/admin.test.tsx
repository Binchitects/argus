import { screen, waitFor, within } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { describe, expect, it } from 'vitest'
import { admin, fakeApi, member, renderApp } from '@/test/utils'
import { toCsv } from './audit-csv'
import { blockChanges } from './model-switch'
import type { Person } from './people-api'

const person = (over: Partial<Person>): Person => ({
  id: 'p1', userName: 'grace', displayName: 'Grace Hopper', email: 'grace@example.test', isAdmin: false, source: 'local',
  disabled: false, disabledReason: null, twoFactorEnabled: false, lockedOut: false, lastSignInAt: '2026-09-20T10:00:00Z',
  createdAt: '2026-01-01T00:00:00Z', spend: 3, budget: 10, ...over,
})
const people = [
  person({}),
  person({ id: 'p2', userName: 'alan', displayName: 'Alan Turing', email: 'alan@example.test', spend: 12, budget: 10 }),
  person({ id: 'a1', userName: 'admin', displayName: 'Ada Admin', email: 'admin@example.test', isAdmin: true, budget: null }),
]

describe('people', () => {
  it('lists people, filters those over credit, and opens a person', async () => {
    fakeApi(admin, { 'GET /api/admin/people': () => ({ json: { warning: null, people } }) })
    const { router } = renderApp('/admin/people')
    expect(await screen.findByText('Alan Turing')).toBeInTheDocument()
    await userEvent.click(screen.getByRole('radio', { name: 'Over credit' }))
    expect(screen.queryByText('Grace Hopper')).not.toBeInTheDocument()
    expect(screen.getByText('Alan Turing')).toBeInTheDocument()
    await userEvent.click(screen.getByText('Alan Turing'))
    await waitFor(() => expect(router.state.location.pathname).toBe('/admin/people/p2'))
  })

  it('a bulk action asks first, then acts on each selected person', async () => {
    const calls = fakeApi(admin, {
      'GET /api/admin/people': () => ({ json: { warning: null, people } }),
      'PATCH /api/admin/people/p1': () => ({ status: 204 }),
      'PATCH /api/admin/people/p2': () => ({ status: 204 }),
    })
    renderApp('/admin/people')
    await screen.findByText('Grace Hopper')
    const rows = screen.getAllByRole('checkbox', { name: 'Select row' })
    await userEvent.click(rows[1]!)
    await userEvent.click(rows[2]!)
    await userEvent.click(within(screen.getByRole('region', { name: 'Bulk actions' })).getByRole('button', { name: 'Disable' }))
    await userEvent.click(within(await screen.findByRole('alertdialog')).getByRole('button', { name: 'Disable' }))
    await waitFor(() => expect(calls.filter((c) => c.method === 'PATCH').map((c) => c.path).sort()).toEqual(['/api/admin/people/p1', '/api/admin/people/p2']))
    expect(calls.find((c) => c.method === 'PATCH')?.body).toEqual({ disabled: true })
  })

  it('adding a person shows their password once', async () => {
    const calls = fakeApi(admin, {
      'GET /api/admin/people': () => ({ json: { warning: null, people } }),
      'POST /api/admin/people': () => ({ json: { id: 'n1', password: 'generated-pass-123', apiKey: 'sk-new', warning: null } }),
    })
    renderApp('/admin/people')
    await userEvent.click(await screen.findByRole('button', { name: 'Add person' }))
    const dialog = await screen.findByRole('dialog')
    await userEvent.type(within(dialog).getByLabelText('Username'), 'Bad Name')
    await userEvent.type(within(dialog).getByLabelText('Email'), 'kate@example.test')
    await userEvent.click(within(dialog).getByRole('button', { name: 'Add person' }))
    expect(await within(dialog).findByText(/Lower-case letters/)).toBeInTheDocument()
    await userEvent.clear(within(dialog).getByLabelText('Username'))
    await userEvent.type(within(dialog).getByLabelText('Username'), 'kate')
    await userEvent.type(within(dialog).getByLabelText('Credit ($)'), '25')
    await userEvent.click(within(dialog).getByRole('button', { name: 'Add person' }))
    expect(await within(dialog).findByText('Person added')).toBeInTheDocument()
    expect(calls.find((c) => c.method === 'POST' && c.path === '/api/admin/people')?.body).toEqual({ userName: 'kate', email: 'kate@example.test', displayName: null, admin: false, budget: 25 })
    await userEvent.click(within(dialog).getByRole('button', { name: 'Show Password' }))
    expect(within(dialog).getByLabelText('Password')).toHaveTextContent('generated-pass-123')
  })

  it('deleting asks for the username to be typed', async () => {
    const calls = fakeApi(admin, {
      'GET /api/admin/people/p1': () => ({ json: { person: people[0], keys: [], warning: null } }),
      'DELETE /api/admin/people/p1': () => ({ status: 204 }),
      'GET /api/admin/people': () => ({ json: { warning: null, people } }),
    })
    const { router } = renderApp('/admin/people/p1')
    await userEvent.click(await screen.findByRole('button', { name: 'Delete grace' }))
    const dialog = await screen.findByRole('dialog')
    const del = within(dialog).getByRole('button', { name: 'Delete' })
    expect(del).toBeDisabled()
    await userEvent.type(within(dialog).getByLabelText('Username'), 'grace')
    await userEvent.click(del)
    await waitFor(() => expect(router.state.location.pathname).toBe('/admin/people'))
    expect(calls.some((c) => c.method === 'DELETE')).toBe(true)
  })

  it('credit is set from the person page', async () => {
    const calls = fakeApi(admin, {
      'GET /api/admin/people/p1': () => ({ json: { person: people[0], keys: [{ alias: 'grace', preview: 'sk-...1234', spend: 3, blocked: false, createdAt: null }], warning: null } }),
      'PUT /api/admin/people/p1/budget': () => ({ status: 204 }),
    })
    renderApp('/admin/people/p1')
    const credit = await screen.findByLabelText('Credit ($)')
    await userEvent.clear(credit)
    await userEvent.click(screen.getByRole('button', { name: 'Set credit' }))
    await waitFor(() => expect(calls.find((c) => c.method === 'PUT')?.body).toEqual({ budget: null }))
  })
})

describe('admin pages', () => {
  it('members get a plain refusal', async () => {
    fakeApi(member)
    renderApp('/admin/settings')
    expect(await screen.findByRole('heading', { name: 'Admins only' })).toBeInTheDocument()
  })

  it('the overview warns about people over credit', async () => {
    fakeApi(admin, {
      'GET /api/admin/overview': () => ({
        json: { people: 3, admins: 1, spend: 20, overCredit: ['alan'], warning: null, services: [{ name: 'Gateway', purpose: 'the API', ok: false, detail: 'refused' }], index: { configured: false, summary: null, error: null }, model: 'M' },
      }),
    })
    renderApp('/admin')
    expect(await screen.findByText('1 person is at or past their credit')).toBeInTheDocument()
    expect(screen.getByLabelText('Services up')).toHaveTextContent('0/1')
    expect(screen.getByText(/Down · refused/)).toBeInTheDocument()
  })

  it('the audit export never writes a formula', () => {
    const csv = toCsv([{ id: 1, at: '2026-01-01', actor: '=cmd()', action: 'sign_in', target: 'a,b', success: false, ip: null, detail: 'say "hi"' }])
    expect(csv.split('\n')[1]).toBe(`2026-01-01,'=cmd(),sign_in,"a,b",false,,"say ""hi"""`)
  })

  it('switching a model keeps the host’s own model directory', () => {
    expect(blockChanges('# >>> MODEL\nMODEL_NAME=Big\nLLAMACPP_MODEL_DIR=/their/disk\nLLAMACPP_EXTRA_ARGS=--flash-attn on\n# <<< MODEL')).toEqual([
      { key: 'MODEL_NAME', value: 'Big' },
      { key: 'LLAMACPP_EXTRA_ARGS', value: '--flash-attn on' },
    ])
  })
})
