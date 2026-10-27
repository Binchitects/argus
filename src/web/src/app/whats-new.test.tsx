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
    const tour = await screen.findByRole('dialog', { name: 'A science lab' })
    expect(within(tour).getByText("What's new in Argus Arena 7.3")).toBeInTheDocument()
    await userEvent.click(within(tour).getByRole('button', { name: /Next/ }))
    expect(await screen.findByRole('dialog', { name: 'Proofs checked by Lean' })).toBeInTheDocument()
    // A page by its dot, then to the last one: a member has no deployment page to see.
    await userEvent.click(screen.getByRole('button', { name: "Code Arena's panes, your way (4 of 4)" }))
    const last = await screen.findByRole('dialog', { name: "Code Arena's panes, your way" })
    await userEvent.click(within(last).getByRole('button', { name: 'Done' }))
    await waitFor(() => expect(calls.find((c) => c.method === 'PUT' && c.path === '/api/account/whats-new')?.body).toEqual({ release: '7.3' }))
    expect(screen.queryByRole('dialog')).not.toBeInTheDocument()

    // Again from the account menu, as often as they like.
    await userEvent.click(screen.getByRole('button', { name: /Account menu/ }))
    await userEvent.click(await screen.findByRole('menuitem', { name: "What's new" }))
    expect(await screen.findByRole('dialog', { name: 'A science lab' })).toBeInTheDocument()
  })

  it('shows the Kubernetes install to those who may read the settings', async () => {
    fakeApi({ ...member, permissions: ['settings.read'] }, { 'GET /api/account/whats-new': () => ({ json: { seen: '7.2' } }), 'PUT /api/account/whats-new': () => ({ json: { seen: '7.3' } }) })
    renderApp('/')
    await screen.findByRole('dialog', { name: 'A science lab' })
    await userEvent.click(screen.getByRole('button', { name: 'On Kubernetes, with Helm (5 of 5)' }))
    const page = await screen.findByRole('dialog', { name: 'On Kubernetes, with Helm' })
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
      'PUT /api/account/whats-new': () => ({ json: { seen: '7.3' } }),
      'GET /api/lab/fields': () => ({ json: { available: true, why: null, fields: [{ id: 'mathematics', title: 'Mathematics', about: 'Checked by SymPy.', tools: ['lab'] }] } }),
      'GET /api/lab/notebooks': () => ({ json: [] }),
    })
    renderApp('/')
    await screen.findByRole('dialog', { name: 'A science lab' })
    await userEvent.click(screen.getByRole('button', { name: 'Proofs checked by Lean (2 of 4)' }))
    await userEvent.click(within(await screen.findByRole('dialog', { name: 'Proofs checked by Lean' })).getByRole('button', { name: 'Prove something' }))
    // The lab's page.
    expect(await screen.findByRole('heading', { level: 1, name: 'Science lab' })).toBeInTheDocument()
    expect(calls.some((c) => c.method === 'PUT' && c.path === '/api/account/whats-new')).toBe(true)
  })
})
