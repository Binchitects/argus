import { screen, waitFor, within } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { describe, expect, it } from 'vitest'
import { fakeApi, member, renderApp } from '@/test/utils'

describe("what's new", () => {
  it('opens once after a release, goes through its pages, and is kept as seen', async () => {
    let seen: string | null = '7.1'
    const calls = fakeApi(member, {
      'GET /api/account/whats-new': () => ({ json: { seen } }),
      'PUT /api/account/whats-new': (body) => {
        seen = (body as { release: string }).release
        return { json: { seen } }
      },
    })
    renderApp('/')
    const tour = await screen.findByRole('dialog', { name: 'Plans you approve' })
    expect(within(tour).getByText("What's new in Argus Arena 7.2")).toBeInTheDocument()
    await userEvent.click(within(tour).getByRole('button', { name: /Next/ }))
    expect(await screen.findByRole('dialog', { name: 'Long work that carries on' })).toBeInTheDocument()
    // A page by its dot, then to the last one: a member has no Models page to see.
    await userEvent.click(screen.getByRole('button', { name: 'Code Arena plans, proves and runs steps at once (4 of 4)' }))
    const last = await screen.findByRole('dialog', { name: 'Code Arena plans, proves and runs steps at once' })
    await userEvent.click(within(last).getByRole('button', { name: 'Done' }))
    await waitFor(() => expect(calls.find((c) => c.method === 'PUT' && c.path === '/api/account/whats-new')?.body).toEqual({ release: '7.2' }))
    expect(screen.queryByRole('dialog')).not.toBeInTheDocument()

    // Again from the account menu, as often as they like.
    await userEvent.click(screen.getByRole('button', { name: /Account menu/ }))
    await userEvent.click(await screen.findByRole('menuitem', { name: "What's new" }))
    expect(await screen.findByRole('dialog', { name: 'Plans you approve' })).toBeInTheDocument()
  })

  it("shows each model's tools to those who may open Models", async () => {
    fakeApi({ ...member, permissions: ['models.read'] }, { 'GET /api/account/whats-new': () => ({ json: { seen: '7.1' } }), 'PUT /api/account/whats-new': () => ({ json: { seen: '7.2' } }) })
    renderApp('/')
    await screen.findByRole('dialog', { name: 'Plans you approve' })
    await userEvent.click(screen.getByRole('button', { name: "Each model's tools, your way (5 of 5)" }))
    const page = await screen.findByRole('dialog', { name: "Each model's tools, your way" })
    expect(within(page).getByRole('button', { name: 'Open Models' })).toBeInTheDocument()
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
      'PUT /api/account/whats-new': () => ({ json: { seen: '7.2' } }),
      'GET /api/plans': () => ({ json: [{ id: 'p2', title: 'Rename the parser', conversationId: 'c2', source: 'code', place: 'codec', updatedAt: '', steps: 3, done: 1 }] }),
    })
    renderApp('/')
    await screen.findByRole('dialog', { name: 'Plans you approve' })
    await userEvent.click(screen.getByRole('button', { name: 'Long work that carries on (2 of 4)' }))
    await userEvent.click(within(await screen.findByRole('dialog', { name: 'Long work that carries on' })).getByRole('button', { name: 'Open Plans' }))
    // The Plans page, those in progress first.
    expect(await screen.findByRole('list', { name: 'Plans in progress' })).toBeInTheDocument()
    expect(calls.some((c) => c.method === 'PUT' && c.path === '/api/account/whats-new')).toBe(true)
  })
})
