import { screen, waitFor, within } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { describe, expect, it } from 'vitest'
import { admin, fakeApi, member, renderApp } from '@/test/utils'

describe('shell', () => {
  it('sends someone signed out to sign in, and back to where they were going', async () => {
    fakeApi(null)
    const { router } = renderApp('/account?tab=keys')
    await waitFor(() => expect(router.state.location.pathname).toBe('/login'))
    expect(router.state.location.search).toBe(`?rd=${encodeURIComponent('/account?tab=keys')}`)
  })

  it('shows members the workspace only', async () => {
    fakeApi(member)
    renderApp('/')
    const nav = await screen.findByRole('navigation', { name: 'Main' })
    expect(within(nav).getByRole('link', { name: 'Chat' })).toBeInTheDocument()
    expect(within(nav).queryByRole('link', { name: 'People' })).not.toBeInTheDocument()
    expect(within(nav).queryByText('Administration')).not.toBeInTheDocument()
  })

  it('shows admins the administration sections', async () => {
    fakeApi(admin)
    renderApp('/')
    const nav = await screen.findByRole('navigation', { name: 'Main' })
    for (const name of ['People', 'Settings', 'Audit log', 'Indexing', 'Monitoring']) expect(within(nav).getByRole('link', { name })).toBeInTheDocument()
    expect(await screen.findByText('All 1 up')).toBeInTheDocument()
  })

  it('the command palette opens with Ctrl K and goes to a page', async () => {
    fakeApi(admin)
    const { router } = renderApp('/')
    await screen.findByRole('navigation', { name: 'Main' })
    await userEvent.keyboard('{Control>}k{/Control}')
    const input = await screen.findByPlaceholderText('Go to a page or run a command…')
    await userEvent.type(input, 'audit')
    await userEvent.keyboard('{Enter}')
    await waitFor(() => expect(router.state.location.pathname).toBe('/admin/audit'))
  })

  it('the best match comes first: the page named so, not one with it as a keyword', async () => {
    fakeApi(admin)
    const { router } = renderApp('/')
    await screen.findByRole('navigation', { name: 'Main' })
    await userEvent.click(screen.getByRole('button', { name: 'Search and commands' }))
    await userEvent.type(await screen.findByPlaceholderText('Go to a page or run a command…'), 'account')
    const options = await screen.findAllByRole('option')
    expect(options[0]).toHaveTextContent('Your account')
    expect(options.some((o) => o.textContent?.includes('People'))).toBe(true)
    await userEvent.keyboard('{Enter}')
    await waitFor(() => expect(router.state.location.pathname).toBe('/account'))
  })

  it('signing out ends the session and shows the sign-in page', async () => {
    const calls = fakeApi(member)
    const { router } = renderApp('/')
    await userEvent.click(await screen.findByRole('button', { name: /Account menu/ }))
    await userEvent.click(await screen.findByRole('menuitem', { name: 'Sign out' }))
    await waitFor(() => expect(router.state.location.pathname).toBe('/login'))
    expect(calls.some((c) => c.method === 'POST' && c.path === '/api/auth/logout' && c.headers['X-Requested-With'] === 'fetch')).toBe(true)
  })

  it('a 401 on any call means the session ended: back to sign-in', async () => {
    let expired = false
    fakeApi(member, {
      'GET /api/account/keys': () => (expired ? { status: 401, json: { status: 'unauthorized' } } : { json: { keys: [], spend: 0, budget: null } }),
      'GET /api/auth/me': () => ({ json: member }),
    })
    const { router, client } = renderApp('/')
    await screen.findByText('Your credit')
    expired = true
    await client.invalidateQueries({ queryKey: ['account', 'keys'] })
    await waitFor(() => expect(router.state.location.pathname).toBe('/login'))
  })

  it('a page that is not rebuilt yet opens in the current app', async () => {
    fakeApi(admin)
    renderApp('/dashboards')
    expect(await screen.findByText('Being rebuilt')).toBeInTheDocument()
    expect(screen.getByRole('link', { name: /Open in Grafana/ })).toHaveAttribute('href', `${window.location.protocol}//grafana.${window.location.host}/`)
  })

  it('unknown pages say so', async () => {
    fakeApi(member)
    renderApp('/no-such-page')
    expect(await screen.findByRole('heading', { name: 'Page not found' })).toBeInTheDocument()
  })
})
