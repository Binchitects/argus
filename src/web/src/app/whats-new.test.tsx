import { screen, waitFor, within } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { describe, expect, it } from 'vitest'
import { fakeApi, member, renderApp } from '@/test/utils'

describe("what's new", () => {
  it('opens once after a release, goes through its pages, and is kept as seen', async () => {
    let seen: string | null = '7.3'
    const calls = fakeApi(member, {
      'GET /api/account/whats-new': () => ({ json: { seen } }),
      'PUT /api/account/whats-new': (body) => {
        seen = (body as { release: string }).release
        return { json: { seen } }
      },
    })
    renderApp('/')
    const tour = await screen.findByRole('dialog', { name: 'Checklists' })
    expect(within(tour).getByText("What's new in Argus Arena 7.5")).toBeInTheDocument()
    await userEvent.click(within(tour).getByRole('button', { name: /Next/ }))
    expect(await screen.findByRole('dialog', { name: 'API keys of your own' })).toBeInTheDocument()
    // A page by its dot, then to the last one: a member has no admin pages to see.
    await userEvent.click(screen.getByRole('button', { name: 'Say what you think (4 of 4)' }))
    const last = await screen.findByRole('dialog', { name: 'Say what you think' })
    await userEvent.click(within(last).getByRole('button', { name: 'Done' }))
    await waitFor(() => expect(calls.find((c) => c.method === 'PUT' && c.path === '/api/account/whats-new')?.body).toEqual({ release: '7.5' }))
    expect(screen.queryByRole('dialog')).not.toBeInTheDocument()

    // Again from the account menu, as often as they like.
    await userEvent.click(screen.getByRole('button', { name: /Account menu/ }))
    await userEvent.click(await screen.findByRole('menuitem', { name: "What's new" }))
    expect(await screen.findByRole('dialog', { name: 'Checklists' })).toBeInTheDocument()
  })

  it('shows the features chosen at install to those who may read the settings', async () => {
    fakeApi({ ...member, permissions: ['settings.read'] }, { 'GET /api/account/whats-new': () => ({ json: { seen: '7.4' } }), 'PUT /api/account/whats-new': () => ({ json: { seen: '7.5' } }) })
    renderApp('/')
    await screen.findByRole('dialog', { name: 'Checklists' })
    await userEvent.click(screen.getByRole('button', { name: 'SGLang beside vLLM (5 of 5)' }))
    const page = await screen.findByRole('dialog', { name: 'SGLang beside vLLM' })
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
      'PUT /api/account/whats-new': () => ({ json: { seen: '7.5' } }),
      'GET /api/checklists': () => ({ json: [] }),
    })
    renderApp('/')
    const tour = await screen.findByRole('dialog', { name: 'Checklists' })
    await userEvent.click(within(tour).getByRole('button', { name: 'Open Checklists' }))
    // The checklists' page.
    expect(await screen.findByRole('heading', { level: 1, name: 'Checklists' })).toBeInTheDocument()
    expect(calls.some((c) => c.method === 'PUT' && c.path === '/api/account/whats-new')).toBe(true)
  })
})
