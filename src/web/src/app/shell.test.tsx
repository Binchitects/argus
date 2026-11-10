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
    // The workspace only: no tabs.
    expect(screen.queryByRole('tablist', { name: 'Sidebar' })).not.toBeInTheDocument()
  })

  it("gives an admin three sidebars, the workspace's, the dashboards' and the administration's, each by the page opened", async () => {
    fakeApi(admin)
    renderApp('/')
    const tabs = await screen.findByRole('tablist', { name: 'Sidebar' })
    expect(within(tabs).getAllByRole('tab').map((t) => t.textContent)).toEqual(['Workspace', 'Monitoring', 'Admin'])
    expect(within(tabs).getByRole('tab', { name: 'Workspace' })).toHaveAttribute('aria-selected', 'true')
    const nav = screen.getByRole('navigation', { name: 'Main' })
    // The most used first: the everyday pages, then building, creating, organising, and one's own.
    expect(within(nav).getAllByRole('link').slice(0, 5).map((l) => l.textContent)).toEqual(['Home', 'Chat', 'Assistants', 'Projects', 'Wiki'])
    expect(within(nav).queryByRole('link', { name: 'People' })).not.toBeInTheDocument()
    expect(within(nav).queryByRole('link', { name: 'Dashboards' })).not.toBeInTheDocument()

    await userEvent.click(within(tabs).getByRole('tab', { name: 'Admin' }))
    expect(within(tabs).getByRole('tab', { name: 'Admin' })).toHaveAttribute('aria-selected', 'true')
    expect(within(nav).getByRole('link', { name: 'People' })).toBeInTheDocument()
    expect(within(nav).getByRole('link', { name: 'Models' })).toBeInTheDocument()
    expect(within(nav).queryByRole('link', { name: 'Chat' })).not.toBeInTheDocument()
    expect(within(nav).queryByRole('link', { name: 'Dashboards' })).not.toBeInTheDocument()

    await userEvent.click(within(tabs).getByRole('tab', { name: 'Monitoring' }))
    expect(within(nav).getByRole('link', { name: 'Dashboards' })).toBeInTheDocument()
    expect(within(nav).getByRole('link', { name: 'Overview' })).toBeInTheDocument()
    // A page opened shows its own tab, and its tab in the breadcrumb.
    await userEvent.click(within(nav).getByRole('link', { name: 'Logs' }))
    expect(await within(screen.getByRole('navigation', { name: 'Breadcrumb' })).findByText('Logs')).toBeInTheDocument()
    expect(within(tabs).getByRole('tab', { name: 'Monitoring' })).toHaveAttribute('aria-selected', 'true')
    expect(await within(screen.getByRole('navigation', { name: 'Breadcrumb' })).findByText('Monitoring')).toBeInTheDocument()
  })

  it('opens on the tab of the page it starts on, and shows only the tabs an admin has pages in', async () => {
    fakeApi({ ...member, permissions: ['people.read'] })
    const { router } = renderApp('/admin/people')
    const tabs = await screen.findByRole('tablist', { name: 'Sidebar' })
    expect(within(tabs).getAllByRole('tab').map((t) => t.textContent)).toEqual(['Workspace', 'Admin'])
    expect(within(tabs).getByRole('tab', { name: 'Admin' })).toHaveAttribute('aria-selected', 'true')
    expect(router.state.location.pathname).toBe('/admin/people')
  })

  const source = 'https://github.com/Binchitects/argus'
  const withSource = { 'GET /api/info': () => ({ json: { name: 'Argus Arena', version: '5.1.0', license: 'AGPL-3.0-only', source } }) }

  it('offers the source beside the version, as the AGPL asks', async () => {
    fakeApi(member, withSource)
    renderApp('/')
    expect(await screen.findByLabelText('Version')).toHaveTextContent('v5.1.0')
    expect(screen.getByRole('link', { name: 'Source' })).toHaveAttribute('href', source)
  })

  it('offers the source in the collapsed sidebar too', async () => {
    localStorage.setItem('sidebar', 'collapsed')
    fakeApi(member, withSource)
    renderApp('/')
    const sidebar = await screen.findByRole('complementary', { name: 'Sidebar' })
    expect(await within(sidebar).findByRole('link', { name: 'Source' })).toHaveAttribute('href', source)
  })

  it('offers the version and the source in the navigation on a phone', async () => {
    fakeApi(member, withSource)
    renderApp('/')
    await screen.findByLabelText('Version')
    await userEvent.click(screen.getByRole('button', { name: 'Open navigation' }))
    const sheet = await screen.findByRole('dialog', { name: 'Navigation' })
    expect(within(sheet).getByLabelText('Version')).toHaveTextContent('v5.1.0')
    expect(within(sheet).getByRole('link', { name: 'Source' })).toHaveAttribute('href', source)
  })

  it('says a page of a feature that is off is not installed, and how an admin turns it on', async () => {
    fakeApi({ ...admin, featuresOff: ['lab'] })
    renderApp('/lab')
    expect(await screen.findByText('Science lab is not installed here')).toBeInTheDocument()
    expect(screen.getByText('./arenactl repair --with lab')).toBeInTheDocument()
    const nav = screen.getByRole('navigation', { name: 'Main' })
    expect(within(nav).queryByRole('link', { name: 'Science lab' })).not.toBeInTheDocument()
  })

  it('tells a member to ask an admin for a feature that is off', async () => {
    fakeApi({ ...member, featuresOff: ['meetings'] })
    renderApp('/meetings')
    expect(await screen.findByText('Meetings is not installed here')).toBeInTheDocument()
    expect(screen.getByText(/Ask an admin/)).toBeInTheDocument()
  })

  it('leaves out the pages a member may not use, from the sidebar, the palette and Home, and says so on the page', async () => {
    fakeApi({ ...member, denied: ['feature.decks', 'feature.studio', 'feature.api_keys', 'code.studio', 'code.read', 'review.run'] })
    renderApp('/decks')
    expect(await screen.findByText('Decks is not among what you may use')).toBeInTheDocument()
    expect(screen.getByRole('link', { name: 'Your account' })).toHaveAttribute('href', '/account')
    const nav = screen.getByRole('navigation', { name: 'Main' })
    for (const name of ['Decks', 'Agent Studio', 'Code Studio', 'Connect your tools']) expect(within(nav).queryByRole('link', { name })).not.toBeInTheDocument()
    expect(within(nav).getByRole('link', { name: 'Meetings' })).toBeInTheDocument()
    await userEvent.click(within(nav).getByRole('link', { name: 'Home' }))
    const starters = await screen.findByRole('region', { name: 'Get started' })
    expect(within(starters).getByRole('link', { name: /Your usage/ })).toBeInTheDocument()
    expect(within(starters).getByRole('link', { name: /Meetings/ })).toHaveAttribute('href', '/meetings')
    for (const name of [/Connect your tools/, /Agent Studio/, /Code Studio/]) expect(within(starters).queryByRole('link', { name })).not.toBeInTheDocument()
    await userEvent.keyboard('{Control>}k{/Control}')
    await userEvent.type(await screen.findByPlaceholderText('Go to a page or run a command…'), 'agent studio')
    expect(screen.queryByRole('option', { name: /^Agent Studio/ })).not.toBeInTheDocument()
  })

  it('shows admins the administration sections', async () => {
    fakeApi(admin)
    renderApp('/')
    const nav = await screen.findByRole('navigation', { name: 'Main' })
    await userEvent.click(screen.getByRole('tab', { name: 'Admin' }))
    for (const name of ['People', 'Settings', 'Audit log', 'Indexing']) expect(within(nav).getByRole('link', { name })).toBeInTheDocument()
    await userEvent.click(screen.getByRole('tab', { name: 'Monitoring' }))
    expect(within(nav).getByRole('link', { name: 'Monitoring' })).toBeInTheDocument()
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
      'GET /api/account/keys': () => (expired ? { status: 401, json: { status: 'unauthorized' } } : { json: { apiOff: false, keys: [], spend: 0, apiSpend: 0, apiCredit: null, standing: [] } }),
      'GET /api/auth/me': () => ({ json: member }),
    })
    const { router, client } = renderApp('/')
    await screen.findByText('Your credit this month')
    expired = true
    await client.invalidateQueries({ queryKey: ['account', 'keys'] })
    await waitFor(() => expect(router.state.location.pathname).toBe('/login'))
  })

  it('the width on wide screens is chosen in the user menu and remembered', async () => {
    fakeApi(member)
    renderApp('/')
    await userEvent.click(await screen.findByRole('button', { name: /Account menu/ }))
    await userEvent.click(await screen.findByRole('menuitem', { name: 'Width' }))
    await userEvent.click(await screen.findByRole('menuitemradio', { name: 'Full width' }))
    expect(document.documentElement.dataset.width).toBe('full')
    expect(localStorage.getItem('width')).toBe('full')
    // The page's width comes from the chosen width (a CSS variable), not a fixed size.
    expect(screen.getByRole('main')).toHaveClass('max-w-(--page-max)')
  })

  it('the colour theme the account keeps is taken on this device as it loads', async () => {
    localStorage.setItem('color-theme', 'copper')
    const calls = fakeApi({ ...member, colorTheme: 'midnight' })
    renderApp('/')
    await waitFor(() => expect(document.documentElement.dataset.colorTheme).toBe('midnight'))
    expect(localStorage.getItem('color-theme')).toBe('midnight')
    // Taken, not saved again.
    expect(calls.some((c) => c.method === 'PUT')).toBe(false)
  })

  it('the colour theme is chosen in the user menu, and in the command palette', async () => {
    const calls = fakeApi(member, { 'PUT /api/account/preferences': (body) => ({ json: { answerLength: 'normal', compactAt: null, compactAtDefault: 80, ...(body as object) } }) })
    renderApp('/')
    await userEvent.click(await screen.findByRole('button', { name: /Account menu/ }))
    await userEvent.click(await screen.findByRole('menuitem', { name: 'Colour theme' }))
    await userEvent.click(await screen.findByRole('menuitemradio', { name: 'Forest' }))
    expect(document.documentElement.dataset.colorTheme).toBe('forest')
    await waitFor(() => expect(calls.find((c) => c.method === 'PUT')?.body).toEqual({ colorTheme: 'forest' }))

    await userEvent.keyboard('{Control>}k{/Control}')
    await userEvent.type(await screen.findByPlaceholderText('Go to a page or run a command…'), 'lavender')
    await userEvent.keyboard('{Enter}')
    expect(document.documentElement.dataset.colorTheme).toBe('lavender')
    expect(localStorage.getItem('color-theme')).toBe('lavender')
    await waitFor(() => expect(calls.filter((c) => c.method === 'PUT').at(-1)?.body).toEqual({ colorTheme: 'lavender' }))
  })

  it('the collapsed sidebar keeps each link laid out and the current page marked', async () => {
    fakeApi(admin)
    renderApp('/admin/people')
    await userEvent.click(await screen.findByRole('button', { name: 'Collapse sidebar' }))
    const nav = screen.getByRole('navigation', { name: 'Main' })
    const people = within(nav).getByRole('link', { name: 'People' })
    // Its class is its own, not a class function's source text (a tooltip trigger joins classes as strings).
    expect(people.className.split(' ')).toEqual(expect.arrayContaining(['flex', 'size-9', 'bg-sidebar-accent']))
    expect(people.className).not.toMatch(/isActive|=>/)
    expect(people).toHaveAttribute('aria-current', 'page')
    expect(within(nav).getByRole('link', { name: 'Groups' })).not.toHaveAttribute('aria-current')
    // Collapsed, the tabs are icons, named.
    expect(screen.getByRole('tab', { name: 'Admin' })).toHaveAttribute('aria-selected', 'true')
    await userEvent.click(screen.getByRole('button', { name: 'Expand sidebar' }))
    expect(within(nav).getByRole('link', { name: 'People' })).toHaveAttribute('aria-current', 'page')
  })

  it('unknown pages say so', async () => {
    fakeApi(member)
    renderApp('/no-such-page')
    expect(await screen.findByRole('heading', { name: 'Page not found' })).toBeInTheDocument()
  })
})
