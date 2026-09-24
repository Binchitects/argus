import { screen, waitFor, within } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { afterEach, describe, expect, it, vi } from 'vitest'
import { areas, legacyUrl } from './areas'
import { admin, fakeBackend, member, renderApp } from './test-utils'

afterEach(() => vi.unstubAllGlobals())

describe('app shell', () => {
  it('lists every area for an admin, and shows who is signed in', async () => {
    fakeBackend(admin)
    renderApp('/')
    const nav = await screen.findByRole('navigation', { name: 'Main' })
    for (const a of areas) {
      expect(within(nav).getByRole('link', { name: a.title })).toHaveAttribute('href', a.path)
    }
    expect(screen.getByRole('link', { name: 'Ada Admin' })).toHaveAttribute('href', '/account')
    expect(await screen.findByText('v1.2.3')).toBeInTheDocument()
  })

  it('hides Admin from members', async () => {
    fakeBackend(member)
    renderApp('/')
    const nav = await screen.findByRole('navigation', { name: 'Main' })
    expect(within(nav).queryByRole('link', { name: 'Admin' })).not.toBeInTheDocument()
    expect(screen.queryByRole('heading', { level: 2, name: 'Admin' })).not.toBeInTheDocument()
  })

  it('sends signed-out visitors to sign in, remembering where they were going', async () => {
    fakeBackend(null)
    const { router } = renderApp('/dashboards/usage?x=1')
    await waitFor(() => expect(router.state.location.pathname).toBe('/login'))
    expect(new URLSearchParams(router.state.location.search).get('rd')).toBe('/dashboards/usage?x=1')
  })

  it('shows the overview cards with what is here and what is coming', async () => {
    fakeBackend(admin)
    renderApp('/')
    expect(await screen.findByRole('heading', { level: 1, name: 'Overview' })).toBeInTheDocument()
    expect(screen.getAllByText('Here now')).toHaveLength(areas.filter((a) => a.native).length)
    expect(screen.getAllByText(/Arrives in phase \d/)).toHaveLength(areas.filter((a) => !a.native).length)
  })

  it('points a not-yet-native area to the service that covers it today', async () => {
    fakeBackend(member)
    renderApp('/dashboards')
    expect(await screen.findByRole('status')).toHaveTextContent('moves here in phase 5')
    expect(screen.getByRole('link', { name: 'Open Grafana' })).toHaveAttribute('href', `http://grafana.${window.location.host}/`)
  })

  it('shows not found for unknown pages', async () => {
    fakeBackend(member)
    renderApp('/nope')
    expect(await screen.findByRole('heading', { name: 'Page not found' })).toBeInTheDocument()
  })

  it('signs out', async () => {
    const calls = fakeBackend(member)
    renderApp('/')
    await userEvent.click(await screen.findByRole('button', { name: 'Sign out' }))
    expect(calls.some((c) => c.method === 'POST' && c.path === '/api/auth/logout')).toBe(true)
  })
})

describe('sign in', () => {
  it('asks for the second factor, then goes where the visitor was headed', async () => {
    const assign = vi.fn()
    vi.stubGlobal('location', { ...window.location, assign })
    const calls = fakeBackend(null, {
      'POST /api/auth/login': () => ({ json: { status: '2fa' } }),
      'POST /api/auth/login/2fa': () => ({ json: { status: 'ok', redirect: 'https://grafana.llm.test/d/1' } }),
    })
    renderApp('/login?rd=https%3A%2F%2Fgrafana.llm.test%2Fd%2F1')
    await userEvent.type(screen.getByLabelText('Username or email'), 'ada')
    await userEvent.type(screen.getByLabelText('Password'), 'long enough password')
    await userEvent.click(screen.getByRole('button', { name: 'Sign in' }))
    await userEvent.type(await screen.findByLabelText('Code'), '123456')
    await userEvent.click(screen.getByRole('button', { name: 'Verify' }))
    await waitFor(() => expect(assign).toHaveBeenCalledWith('https://grafana.llm.test/d/1'))
    const login = calls.find((c) => c.path === '/api/auth/login')!
    expect(login.body).toMatchObject({ userName: 'ada', password: 'long enough password', redirect: 'https://grafana.llm.test/d/1' })
    expect(login.headers['X-Requested-With']).toBe('fetch')
  })

  it('shows the reason a sign-in failed', async () => {
    fakeBackend(null, {
      'POST /api/auth/login': () => ({ status: 423, json: { status: 'locked', error: 'Too many wrong attempts.' } }),
    })
    renderApp('/login')
    await userEvent.type(screen.getByLabelText('Username or email'), 'ada')
    await userEvent.type(screen.getByLabelText('Password'), 'x')
    await userEvent.click(screen.getByRole('button', { name: 'Sign in' }))
    expect(await screen.findByRole('alert')).toHaveTextContent('Too many wrong attempts.')
  })
})

describe('admin', () => {
  const person = {
    id: 'p1', userName: 'zoe', displayName: 'Zoe', email: 'zoe@example.test', isAdmin: false, source: 'local',
    disabled: false, disabledReason: null, twoFactorEnabled: true, lockedOut: false, lastSignInAt: null,
    createdAt: '2026-01-01T00:00:00Z', spend: 1.5, budget: 10,
  }

  it('lists people and adds one, showing the password and key once', async () => {
    const calls = fakeBackend(admin, {
      'GET /api/admin/people': () => ({ json: { warning: null, people: [person] } }),
      'POST /api/admin/people': () => ({ status: 201, json: { id: 'p2', password: 'pw-shown-once', apiKey: 'sk-shown-once', warning: null } }),
    })
    renderApp('/admin/people')
    expect(await screen.findByRole('link', { name: 'Zoe' })).toHaveAttribute('href', '/admin/people/p1')
    expect(screen.getByText('2FA')).toBeInTheDocument()
    await userEvent.click(screen.getByRole('button', { name: 'Add a person' }))
    await userEvent.type(screen.getByLabelText(/Username/), 'yan')
    await userEvent.type(screen.getByLabelText('Email'), 'yan@example.test')
    await userEvent.type(screen.getByLabelText(/Credit/), '5')
    await userEvent.click(screen.getByRole('button', { name: 'Add' }))
    expect(await screen.findByLabelText('Password')).toHaveTextContent('pw-shown-once')
    expect(screen.getByLabelText('API key')).toHaveTextContent('sk-shown-once')
    expect(screen.getByText('Shown once.')).toBeInTheDocument()
    expect(calls.find((c) => c.method === 'POST' && c.path === '/api/admin/people')!.body)
      .toMatchObject({ userName: 'yan', email: 'yan@example.test', budget: 5, admin: false })
  })

  it("shows a person's page with the actions that apply to them", async () => {
    fakeBackend(admin, {
      'GET /api/admin/people/p1': () => ({ json: { person, keys: [{ alias: 'app-zoe', preview: 'sk-...abcd', spend: 1.5, blocked: false, createdAt: null }], warning: null } }),
      'POST /api/admin/people/p1/password': () => ({ json: { password: 'fresh-password' } }),
    })
    vi.spyOn(window, 'confirm').mockReturnValue(true)
    renderApp('/admin/people/p1')
    expect(await screen.findByRole('heading', { name: /Zoe/ })).toBeInTheDocument()
    expect(screen.getByRole('button', { name: 'Reset 2FA' })).toBeInTheDocument()
    expect(screen.getByText('sk-...abcd')).toBeInTheDocument()
    await userEvent.click(screen.getByRole('button', { name: 'Reset password' }))
    expect(await screen.findByLabelText('New password')).toHaveTextContent('fresh-password')
  })

  it('refuses the admin area to members even by URL', async () => {
    fakeBackend(member)
    renderApp('/admin')
    expect(await screen.findByText('Only admins can open this page.')).toBeInTheDocument()
  })
})

describe('legacyUrl', () => {
  it('puts the legacy subdomain in front of the app host, port included', () => {
    const chat = areas.find((a) => a.path === '/chat')!
    expect(legacyUrl(chat, { protocol: 'https:', host: 'llm.example.com' })).toBe('https://chat.llm.example.com/')
    expect(legacyUrl(chat, { protocol: 'https:', host: 'llm.localhost:8443' })).toBe('https://chat.llm.localhost:8443/')
  })
})
