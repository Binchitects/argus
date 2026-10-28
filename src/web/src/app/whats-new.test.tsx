import { screen, waitFor, within } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { describe, expect, it } from 'vitest'
import { fakeApi, member, renderApp } from '@/test/utils'

describe("what's new", () => {
  it('opens once after a release, goes through its pages, and is kept as seen', async () => {
    let seen: string | null = '7.2'
    const calls = fakeApi(member, {
      'GET /api/account/whats-new': () => ({ json: { seen } }),
      'PUT /api/account/whats-new': (body) => {
        seen = (body as { release: string }).release
        return { json: { seen } }
      },
    })
    renderApp('/')
    const tour = await screen.findByRole('dialog', { name: 'Security reviews' })
    expect(within(tour).getByText("What's new in Argus Arena 7.4")).toBeInTheDocument()
    await userEvent.click(within(tour).getByRole('button', { name: /Next/ }))
    expect(await screen.findByRole('dialog', { name: 'Fix it, for security too' })).toBeInTheDocument()
    // A page by its dot, then to the last one: a member has no admin pages to see.
    await userEvent.click(screen.getByRole('button', { name: 'Quantum computing in the lab (3 of 3)' }))
    const last = await screen.findByRole('dialog', { name: 'Quantum computing in the lab' })
    await userEvent.click(within(last).getByRole('button', { name: 'Done' }))
    await waitFor(() => expect(calls.find((c) => c.method === 'PUT' && c.path === '/api/account/whats-new')?.body).toEqual({ release: '7.4' }))
    expect(screen.queryByRole('dialog')).not.toBeInTheDocument()

    // Again from the account menu, as often as they like.
    await userEvent.click(screen.getByRole('button', { name: /Account menu/ }))
    await userEvent.click(await screen.findByRole('menuitem', { name: "What's new" }))
    expect(await screen.findByRole('dialog', { name: 'Security reviews' })).toBeInTheDocument()
  })

  it('shows the features chosen at install to those who may read the settings', async () => {
    fakeApi({ ...member, permissions: ['settings.read'] }, { 'GET /api/account/whats-new': () => ({ json: { seen: '7.3' } }), 'PUT /api/account/whats-new': () => ({ json: { seen: '7.4' } }) })
    renderApp('/')
    await screen.findByRole('dialog', { name: 'Security reviews' })
    await userEvent.click(screen.getByRole('button', { name: 'Only what you need (4 of 4)' }))
    const page = await screen.findByRole('dialog', { name: 'Only what you need' })
    expect(within(page).getByRole('button', { name: 'Read how' })).toBeInTheDocument()
  })

  it('does not open for someone who has seen it, or a later one', async () => {
    fakeApi(member, { 'GET /api/account/whats-new': () => ({ json: { seen: '9999.0' } }) })
    renderApp('/')
    await screen.findByRole('button', { name: /Account menu/ })
    await new Promise((r) => setTimeout(r, 50))
    expect(screen.queryByRole('dialog')).not.toBeInTheDocument()
  })

  it('takes them where to try it', async () => {
    const calls = fakeApi(member, {
      'GET /api/account/whats-new': () => ({ json: { seen: null } }),
      'PUT /api/account/whats-new': () => ({ json: { seen: '7.4' } }),
      'GET /api/lab/fields': () => ({ json: { available: true, why: null, fields: [{ id: 'mathematics', title: 'Mathematics', about: 'Checked by SymPy.', tools: ['lab'] }] } }),
      'GET /api/lab/notebooks': () => ({ json: [] }),
    })
    renderApp('/')
    await screen.findByRole('dialog', { name: 'Security reviews' })
    await userEvent.click(screen.getByRole('button', { name: 'Quantum computing in the lab (3 of 3)' }))
    await userEvent.click(within(await screen.findByRole('dialog', { name: 'Quantum computing in the lab' })).getByRole('button', { name: 'Open the lab' }))
    // The lab's page.
    expect(await screen.findByRole('heading', { level: 1, name: 'Science lab' })).toBeInTheDocument()
    expect(calls.some((c) => c.method === 'PUT' && c.path === '/api/account/whats-new')).toBe(true)
  })
})
