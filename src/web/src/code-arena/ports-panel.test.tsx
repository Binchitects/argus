import { render, screen, waitFor, within } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import type { ReactNode } from 'react'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { makeQueryClient, Providers } from '@/app/providers'
import { fakeApi } from '@/test/utils'
import { setIdeBase } from './http'
import { parsePort, type ForwardedPort } from './ports-api'
import { PortsPanel } from './ports-panel'

const W = '0190a0b1-2c3d-7e4f-8a9b-0c1d2e3f4a5b'

function wrap(children: ReactNode) {
  const client = makeQueryClient()
  client.setDefaultOptions({ queries: { ...client.getDefaultOptions().queries, retry: false } })
  render(<Providers client={client}>{children}</Providers>)
}

const port = (over: Partial<ForwardedPort>): ForwardedPort => ({
  port: 5173, label: null, url: `https://ws-0190a0b12c3d7e4f8a9b0c1d2e3f4a5b-5173.llm.test/`, visibility: 'private', origin: 'auto', listening: true, stopped: false,
  process: 'node node_modules/.bin/vite', pid: 42, address: '127.0.0.1', ...over,
})

describe('the Ports panel', () => {
  beforeEach(() => setIdeBase(`/api/studio/w/${W}`))
  afterEach(() => setIdeBase(''))

  it('lists what the workspace listens on with its own address, process and who reaches it, and opens, copies and shares it', async () => {
    const calls = fakeApi(null, {
      [`GET /api/studio/workspaces/${W}/ports`]: () => ({
        json: { available: true, running: true, ports: [port({}), port({ port: 8080, label: 'API', origin: 'user', listening: false, process: null, url: 'https://ws-x-8080.llm.test/', visibility: 'signed-in' })] },
      }),
      [`PUT /api/studio/workspaces/${W}/ports/5173`]: () => ({ json: port({ visibility: 'signed-in' }) }),
    })
    const opened = vi.spyOn(window, 'open').mockReturnValue(null)
    const user = userEvent.setup()
    const copied = vi.spyOn(navigator.clipboard, 'writeText').mockResolvedValue()
    wrap(<PortsPanel shown />)
    const panel = await screen.findByRole('region', { name: 'Ports' })
    const rows = await within(panel).findAllByRole('row')
    // A header, then each port by its number.
    expect(rows).toHaveLength(3)
    expect(rows[1]).toHaveTextContent('5173')
    expect(rows[1]).toHaveTextContent('ws-0190a0b12c3d7e4f8a9b0c1d2e3f4a5b-5173.llm.test')
    expect(rows[1]).toHaveTextContent('node node_modules/.bin/vite')
    expect(rows[1]).toHaveTextContent('Private')
    expect(rows[1]).toHaveTextContent('Auto Forwarded')
    // Forwarded by hand, its server not started yet, shared.
    expect(rows[2]).toHaveTextContent('API (8080)')
    expect(rows[2]).toHaveTextContent('Nothing listening yet')
    expect(rows[2]).toHaveTextContent('Everyone signed in')
    expect(rows[2]).toHaveTextContent('User Forwarded')

    await user.click(within(rows[1]).getByRole('button', { name: 'Open 5173 in Browser' }))
    expect(opened).toHaveBeenCalledWith('https://ws-0190a0b12c3d7e4f8a9b0c1d2e3f4a5b-5173.llm.test/', '_blank', 'noopener,noreferrer')
    await user.click(within(rows[1]).getByRole('button', { name: 'Copy the address of 5173' }))
    expect(copied).toHaveBeenCalledWith('https://ws-0190a0b12c3d7e4f8a9b0c1d2e3f4a5b-5173.llm.test/')
    await user.click(within(rows[1]).getByRole('button', { name: 'Share port 5173 with everyone signed in' }))
    await waitFor(() => expect(calls.find((c) => c.method === 'PUT')?.body).toEqual({ visibility: 'signed-in' }))
    // Arena's own API, not the workspace's engine.
    expect(calls.every((c) => !c.path.startsWith('/api/studio/w/'))).toBe(true)
  })

  it('names a port, stops forwarding it, and forwards one by hand', async () => {
    const calls = fakeApi(null, {
      [`GET /api/studio/workspaces/${W}/ports`]: () => ({ json: { available: true, running: true, ports: [port({})] } }),
      [`PUT /api/studio/workspaces/${W}/ports/5173`]: () => ({ json: port({}) }),
      [`PUT /api/studio/workspaces/${W}/ports/3000`]: () => ({ json: port({ port: 3000, origin: 'user' }) }),
    })
    const user = userEvent.setup()
    wrap(<PortsPanel shown />)
    const panel = await screen.findByRole('region', { name: 'Ports' })
    const row = (await within(panel).findAllByRole('row'))[1]
    await user.click(within(row).getByRole('button', { name: 'Set the label of port 5173' }))
    await user.type(within(panel).getByRole('textbox', { name: 'Label of port 5173' }), 'Web app{Enter}')
    await waitFor(() => expect(calls.find((c) => c.method === 'PUT')?.body).toEqual({ label: 'Web app' }))
    await user.click(within(row).getByRole('button', { name: 'Stop forwarding port 5173' }))
    await waitFor(() => expect(calls.filter((c) => c.method === 'PUT').at(-1)?.body).toEqual({ stopped: true }))

    await user.click(within(panel).getByRole('button', { name: 'Forward a Port' }))
    const typed = within(panel).getByRole('textbox', { name: 'Port number or address to forward' })
    await user.type(typed, 'nope')
    expect(within(panel).getByRole('button', { name: 'Forward' })).toBeDisabled()
    await user.clear(typed)
    await user.type(typed, 'localhost:3000{Enter}')
    await waitFor(() => expect(calls.some((c) => c.method === 'PUT' && c.path === `/api/studio/workspaces/${W}/ports/3000`)).toBe(true))
  })

  it('says what to do with no ports yet, and why there are none for a machine of ones own', async () => {
    fakeApi(null, { [`GET /api/studio/workspaces/${W}/ports`]: () => ({ json: { available: true, running: true, ports: [] } }) })
    wrap(<PortsPanel shown />)
    expect(await screen.findByText(/Start a dev server in a terminal/)).toBeInTheDocument()
  })

  it('reads a typed port as VS Code does', () => {
    expect([parsePort('3000'), parsePort(' localhost:5173 '), parsePort('127.0.0.1:80'), parsePort('[::1]:8080')]).toEqual([3000, 5173, 80, 8080])
    expect([parsePort('0'), parsePort('70000'), parsePort('example.com:80'), parsePort('3.5'), parsePort('')]).toEqual([null, null, null, null, null])
  })
})
