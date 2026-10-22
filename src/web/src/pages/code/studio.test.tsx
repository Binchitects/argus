import { screen, waitFor, within } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { afterEach, describe, expect, it } from 'vitest'
import { ideSocketUrl, ideUrl, setIdeBase } from '@/code-arena/http'
import { fakeApi, member, renderApp } from '@/test/utils'
import type { Workspace } from './api'

const shop: Workspace = {
  id: 'w1', kind: 'server', name: 'shop (main)', project: 'team/shop', branch: 'main', state: 'asleep', error: null, createdAt: '2026-10-22T00:00:00Z', usedAt: '2026-10-22T08:00:00Z',
}

afterEach(() => setIdeBase(''))

describe('code studio', () => {
  it("routes the IDE's own requests and sockets to a workspace through Arena, and nothing else", () => {
    expect(ideUrl('/api/state')).toBe('/api/state')
    setIdeBase('/api/studio/w/w1/')
    expect(ideUrl('/api/files?path=a')).toBe('/api/studio/w/w1/files?path=a')
    expect(ideUrl('https://elsewhere.test/api/x')).toBe('https://elsewhere.test/api/x')
    expect(ideSocketUrl('/api/terminals/socket?id=t1', { protocol: 'https:', host: 'llm.test' } as Location)).toBe('wss://llm.test/api/studio/w/w1/terminals/socket?id=t1')
  })

  it('lists one’s workspaces and opens a new one from a project and branch', async () => {
    const calls = fakeApi(member, {
      'GET /api/code/reviews': () => ({ json: [] }),
      'GET /api/code/rules': () => ({ json: [] }),
      'GET /api/fixes/token': () => ({ json: { linked: true, account: 'dana', gitlab: 'https://gitlab.test' } }),
      'GET /api/studio/workspaces': () => ({ json: { server: true, workspaces: [shop] } }),
      'POST /api/studio/workspaces': () => ({ json: { ...shop, id: 'w2', name: 'api (dev)' } }),
      'POST /api/studio/workspaces/w2/start': () => ({ status: 503, json: { status: 'workspace', error: 'The workspaces service is busy.' } }),
    })
    const { router } = renderApp('/code')
    expect(await screen.findByRole('heading', { name: 'Code Studio' })).toBeInTheDocument()
    const mine = await screen.findByRole('list', { name: 'Your workspaces' })
    expect(within(mine).getByRole('link', { name: 'shop (main)' })).toHaveAttribute('href', '/code/w/w1')
    expect(within(mine).getByText('Asleep')).toBeInTheDocument()

    const form = screen.getByRole('form', { name: 'New workspace' })
    await userEvent.type(within(form).getByLabelText('Project'), 'team/api')
    await userEvent.type(within(form).getByLabelText('Branch'), 'dev')
    await userEvent.click(within(form).getByRole('button', { name: /Open it/ }))
    await waitFor(() => expect(router.state.location.pathname).toBe('/code/w/w2'))
    expect(calls.find((c) => c.method === 'POST' && c.path === '/api/studio/workspaces')?.body).toEqual({ project: 'team/api', branch: 'dev' })
  })

  it('says when there are no workspaces on the server here', async () => {
    fakeApi(member, {
      'GET /api/code/reviews': () => ({ json: [] }),
      'GET /api/code/rules': () => ({ json: [] }),
      'GET /api/studio/workspaces': () => ({ json: { server: false, workspaces: [] } }),
    })
    renderApp('/code')
    expect(await screen.findByText(/Workspaces on the server are not set up here/)).toBeInTheDocument()
    expect(screen.queryByRole('button', { name: /Open it/ })).toBeNull()
  })

  it('a sleeping workspace is started as it opens, and what stopped it is said', async () => {
    const calls = fakeApi(member, {
      'GET /api/studio/workspaces': () => ({ json: { server: true, workspaces: [shop] } }),
      'POST /api/studio/workspaces/w1/start': () => ({ status: 502, json: { status: 'workspace', error: 'The project could not be cloned: Authentication failed' } }),
    })
    renderApp('/code/w/w1')
    expect(await screen.findByText(/could not be cloned/)).toBeInTheDocument()
    expect(calls.filter((c) => c.path === '/api/studio/workspaces/w1/start')).toHaveLength(1)
    expect(screen.getByRole('link', { name: /Code Studio/ })).toHaveAttribute('href', '/code')
  })
})
