import { screen, waitFor, within } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { describe, expect, it } from 'vitest'
import { fakeApi, member, renderApp } from '@/test/utils'

describe("what's new", () => {
  it('opens once after a release, goes through its pages, and is kept as seen', async () => {
    let seen: string | null = '7.0'
    const calls = fakeApi(member, {
      'GET /api/account/whats-new': () => ({ json: { seen } }),
      'PUT /api/account/whats-new': (body) => {
        seen = (body as { release: string }).release
        return { json: { seen } }
      },
    })
    renderApp('/')
    const tour = await screen.findByRole('dialog', { name: 'Your data, your rights' })
    expect(within(tour).getByText("What's new in Argus Arena 7.1")).toBeInTheDocument()
    await userEvent.click(within(tour).getByRole('button', { name: /Next/ }))
    expect(await screen.findByRole('dialog', { name: 'Two-factor where it matters' })).toBeInTheDocument()
    // A page by its dot, then to the last one: a member has no Compliance center to see.
    await userEvent.click(screen.getByRole('button', { name: 'Health and personal data kept in bounds (3 of 3)' }))
    const last = await screen.findByRole('dialog', { name: 'Health and personal data kept in bounds' })
    await userEvent.click(within(last).getByRole('button', { name: 'Done' }))
    await waitFor(() => expect(calls.find((c) => c.method === 'PUT' && c.path === '/api/account/whats-new')?.body).toEqual({ release: '7.1' }))
    expect(screen.queryByRole('dialog')).not.toBeInTheDocument()

    // Again from the account menu, as often as they like.
    await userEvent.click(screen.getByRole('button', { name: /Account menu/ }))
    await userEvent.click(await screen.findByRole('menuitem', { name: "What's new" }))
    expect(await screen.findByRole('dialog', { name: 'Your data, your rights' })).toBeInTheDocument()
  })

  it('shows the Compliance center to those who may open it', async () => {
    fakeApi({ ...member, permissions: ['compliance.read'] }, { 'GET /api/account/whats-new': () => ({ json: { seen: '7.0' } }), 'PUT /api/account/whats-new': () => ({ json: { seen: '7.1' } }) })
    renderApp('/')
    await screen.findByRole('dialog', { name: 'Your data, your rights' })
    await userEvent.click(screen.getByRole('button', { name: 'The Compliance center (4 of 4)' }))
    const page = await screen.findByRole('dialog', { name: 'The Compliance center' })
    expect(within(page).getByRole('button', { name: 'Open Compliance' })).toBeInTheDocument()
  })

  it('does not open for someone who has seen it, or a later one', async () => {
    fakeApi(member, { 'GET /api/account/whats-new': () => ({ json: { seen: '9999.0' } }) })
    renderApp('/')
    await screen.findByRole('button', { name: /Account menu/ })
    await new Promise((r) => setTimeout(r, 50))
    expect(screen.queryByRole('dialog')).not.toBeInTheDocument()
  })

  it('takes them where to try it', async () => {
    const calls = fakeApi(member, { 'GET /api/account/whats-new': () => ({ json: { seen: null } }), 'PUT /api/account/whats-new': () => ({ json: { seen: '7.1' } }) })
    renderApp('/')
    const tour = await screen.findByRole('dialog', { name: 'Your data, your rights' })
    await userEvent.click(within(tour).getByRole('button', { name: 'Your data rights' }))
    // Their account, with their data rights.
    expect(await screen.findByText('Your data rights')).toBeInTheDocument()
    expect(calls.some((c) => c.method === 'PUT' && c.path === '/api/account/whats-new')).toBe(true)
  })
})
