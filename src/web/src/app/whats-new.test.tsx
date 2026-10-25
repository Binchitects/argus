import { screen, waitFor, within } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { describe, expect, it } from 'vitest'
import { fakeApi, member, renderApp } from '@/test/utils'

describe("what's new", () => {
  it('opens once after a release, goes through its pages, and is kept as seen', async () => {
    let seen: string | null = '6.9'
    const calls = fakeApi(member, {
      'GET /api/account/whats-new': () => ({ json: { seen } }),
      'PUT /api/account/whats-new': (body) => {
        seen = (body as { release: string }).release
        return { json: { seen } }
      },
    })
    renderApp('/')
    const tour = await screen.findByRole('dialog', { name: 'Memory that lasts' })
    expect(within(tour).getByText("What's new in Argus Arena 7.0")).toBeInTheDocument()
    await userEvent.click(within(tour).getByRole('button', { name: /Next/ }))
    expect(await screen.findByRole('dialog', { name: 'Chats that never lose the thread' })).toBeInTheDocument()
    // A page by its dot, then to the last one.
    await userEvent.click(screen.getByRole('button', { name: 'Code Arena in one line (4 of 4)' }))
    const last = await screen.findByRole('dialog', { name: 'Code Arena in one line' })
    await userEvent.click(within(last).getByRole('button', { name: 'Done' }))
    await waitFor(() => expect(calls.find((c) => c.method === 'PUT' && c.path === '/api/account/whats-new')?.body).toEqual({ release: '7.0' }))
    expect(screen.queryByRole('dialog')).not.toBeInTheDocument()

    // Again from the account menu, as often as they like.
    await userEvent.click(screen.getByRole('button', { name: /Account menu/ }))
    await userEvent.click(await screen.findByRole('menuitem', { name: "What's new" }))
    expect(await screen.findByRole('dialog', { name: 'Memory that lasts' })).toBeInTheDocument()
  })

  it('does not open for someone who has seen it, or a later one', async () => {
    fakeApi(member, { 'GET /api/account/whats-new': () => ({ json: { seen: '9999.0' } }) })
    renderApp('/')
    await screen.findByRole('button', { name: /Account menu/ })
    await new Promise((r) => setTimeout(r, 50))
    expect(screen.queryByRole('dialog')).not.toBeInTheDocument()
  })

  it('takes them where to try it', async () => {
    const calls = fakeApi(member, { 'GET /api/account/whats-new': () => ({ json: { seen: null } }), 'PUT /api/account/whats-new': () => ({ json: { seen: '7.0' } }) })
    renderApp('/')
    const tour = await screen.findByRole('dialog', { name: 'Memory that lasts' })
    await userEvent.click(within(tour).getByRole('button', { name: 'Open Memory' }))
    // The Memory page, asking for the person's memories.
    await waitFor(() => expect(calls.some((c) => c.path === '/api/account/memories')).toBe(true))
    expect(calls.some((c) => c.method === 'PUT' && c.path === '/api/account/whats-new')).toBe(true)
  })
})
