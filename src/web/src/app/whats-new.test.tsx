import { screen, waitFor, within } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { describe, expect, it } from 'vitest'
import { fakeApi, member, renderApp } from '@/test/utils'

describe("what's new", () => {
  it('opens once after a release, goes through its pages, and is kept as seen', async () => {
    let seen: string | null = '7.5'
    const calls = fakeApi(member, {
      'GET /api/account/whats-new': () => ({ json: { seen } }),
      'PUT /api/account/whats-new': (body) => {
        seen = (body as { release: string }).release
        return { json: { seen } }
      },
    })
    renderApp('/')
    const tour = await screen.findByRole('dialog', { name: 'Argus knows who calls what' })
    expect(within(tour).getByText("What's new in Argus Arena")).toBeInTheDocument()
    await userEvent.click(within(tour).getByRole('button', { name: /Next/ }))
    expect(await screen.findByRole('dialog', { name: 'Code Arena on other providers' })).toBeInTheDocument()
    // A page by its dot, then to the last one: a member has no admin pages to see.
    await userEvent.click(screen.getByRole('button', { name: 'Calmer themes (5 of 5)' }))
    const last = await screen.findByRole('dialog', { name: 'Calmer themes' })
    await userEvent.click(within(last).getByRole('button', { name: 'Done' }))
    await waitFor(() => expect(calls.find((c) => c.method === 'PUT' && c.path === '/api/account/whats-new')?.body).toEqual({ release: '7.10' }))
    expect(screen.queryByRole('dialog')).not.toBeInTheDocument()

    // Again from the account menu, as often as they like.
    await userEvent.click(screen.getByRole('button', { name: /Account menu/ }))
    await userEvent.click(await screen.findByRole('menuitem', { name: "What's new" }))
    expect(await screen.findByRole('dialog', { name: 'Argus knows who calls what' })).toBeInTheDocument()
  })

  it('shows Accounting to those who may see it', async () => {
    fakeApi({ ...member, permissions: ['billing.read'] }, { 'GET /api/account/whats-new': () => ({ json: { seen: '7.9' } }), 'PUT /api/account/whats-new': () => ({ json: { seen: '7.10' } }) })
    renderApp('/')
    await screen.findByRole('dialog', { name: 'Argus knows who calls what' })
    await userEvent.click(screen.getByRole('button', { name: 'Accounting (6 of 6)' }))
    const page = await screen.findByRole('dialog', { name: 'Accounting' })
    expect(within(page).getByRole('button', { name: 'Open Accounting' })).toBeInTheDocument()
  })

  it('leaves out the pages about a feature that is off here', async () => {
    fakeApi({ ...member, featuresOff: ['argus'] }, { 'GET /api/account/whats-new': () => ({ json: { seen: '7.7' } }), 'PUT /api/account/whats-new': () => ({ json: { seen: '7.10' } }) })
    renderApp('/')
    expect(await screen.findByRole('dialog', { name: 'Code Arena on other providers' })).toBeInTheDocument()
    expect(screen.queryByRole('button', { name: /Argus knows who calls what/ })).not.toBeInTheDocument()
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
      'PUT /api/account/whats-new': () => ({ json: { seen: '7.10' } }),
    })
    renderApp('/')
    const tour = await screen.findByRole('dialog', { name: 'Argus knows who calls what' })
    await userEvent.click(within(tour).getByRole('button', { name: 'Calmer themes (5 of 5)' }))
    await userEvent.click(within(await screen.findByRole('dialog', { name: 'Calmer themes' })).getByRole('button', { name: 'Choose a theme' }))
    // Your account, where the theme is chosen.
    expect(await screen.findByRole('heading', { level: 1, name: 'Your account' })).toBeInTheDocument()
    expect(calls.some((c) => c.method === 'PUT' && c.path === '/api/account/whats-new')).toBe(true)
  })

})
