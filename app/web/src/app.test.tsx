import { screen, within } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { beforeEach, describe, expect, it, vi } from 'vitest'
import { areas, legacyUrl } from './areas'
import { renderApp } from './test-utils'

function mockInfo(ok = true) {
  vi.spyOn(globalThis, 'fetch').mockImplementation(async () =>
    ok
      ? new Response(JSON.stringify({ name: 'LLM Service', version: '1.2.3' }), { status: 200 })
      : new Response('down', { status: 503 }),
  )
}

describe('app shell', () => {
  beforeEach(() => mockInfo())

  it('lists every area in the main navigation', async () => {
    renderApp('/')
    const nav = screen.getByRole('navigation', { name: 'Main' })
    for (const a of areas) {
      expect(within(nav).getByRole('link', { name: a.title })).toHaveAttribute('href', a.path)
    }
  })

  it('shows the version reported by the backend', async () => {
    renderApp('/')
    expect(await screen.findByText('v1.2.3')).toBeInTheDocument()
  })

  it('says offline when the backend is unreachable', async () => {
    mockInfo(false)
    renderApp('/')
    expect(await screen.findByText('offline')).toBeInTheDocument()
  })

  it('shows an overview card per area with its phase', () => {
    renderApp('/')
    expect(screen.getByRole('heading', { level: 1, name: 'Overview' })).toBeInTheDocument()
    for (const a of areas) {
      expect(screen.getByRole('heading', { level: 2, name: a.title })).toBeInTheDocument()
    }
    expect(screen.getAllByText(/Arrives in phase \d/)).toHaveLength(areas.length)
  })

  it('navigates to an area and marks it current', async () => {
    renderApp('/')
    const nav = screen.getByRole('navigation', { name: 'Main' })
    await userEvent.click(within(nav).getByRole('link', { name: 'Admin' }))
    expect(screen.getByRole('heading', { level: 1, name: 'Admin' })).toBeInTheDocument()
    expect(within(nav).getByRole('link', { name: 'Admin' })).toHaveAttribute('aria-current', 'page')
  })

  it('points a not-yet-native area to the service that covers it today', () => {
    renderApp('/chat')
    expect(screen.getByRole('status')).toHaveTextContent('moves here in phase 3')
    expect(screen.getByRole('link', { name: 'Open Open WebUI' })).toHaveAttribute('href', `http://chat.${window.location.host}/`)
  })

  it('keeps deep links inside an area on that area', () => {
    renderApp('/dashboards/usage/by-person')
    expect(screen.getByRole('heading', { level: 1, name: 'Dashboards' })).toBeInTheDocument()
  })

  it('shows not found for unknown pages', () => {
    renderApp('/nope')
    expect(screen.getByRole('heading', { name: 'Page not found' })).toBeInTheDocument()
  })
})

describe('legacyUrl', () => {
  it('puts the legacy subdomain in front of the app host, port included', () => {
    const chat = areas.find((a) => a.path === '/chat')!
    expect(legacyUrl(chat, { protocol: 'https:', host: 'llm.example.com' })).toBe('https://chat.llm.example.com/')
    expect(legacyUrl(chat, { protocol: 'https:', host: 'llm.localhost:8443' })).toBe('https://chat.llm.localhost:8443/')
  })
})
