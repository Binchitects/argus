import { QueryClient } from '@tanstack/react-query'
import { render } from '@testing-library/react'
import { createMemoryRouter } from 'react-router'
import { RouterProvider } from 'react-router/dom'
import { vi } from 'vitest'
import { makeQueryClient, Providers } from '@/app/providers'
import { routes } from '@/app/routes'
import type { Me } from '@/lib/api'

/** The whole app at `path`, with its own query cache so tests never share data. */
export function renderApp(path: string) {
  const client: QueryClient = makeQueryClient()
  client.setDefaultOptions({ queries: { ...client.getDefaultOptions().queries, retry: false } })
  const router = createMemoryRouter(routes, { initialEntries: [path] })
  const view = render(
    <Providers client={client}>
      <RouterProvider router={router} />
    </Providers>,
  )
  return { ...view, router, client }
}

export const admin: Me = {
  id: 'a1', userName: 'admin', displayName: 'Ada Admin', email: 'admin@example.test',
  isAdmin: true, source: 'local', twoFactorEnabled: false, signedInAt: 1_700_000_000,
}
export const member: Me = { ...admin, id: 'm1', userName: 'mo', displayName: 'Mo Member', email: 'mo@example.test', isAdmin: false }

export type Handler = (body: unknown, init: RequestInit) => { status?: number; json?: unknown }
export interface Call {
  method: string
  path: string
  body: unknown
  headers: Record<string, string>
}

/**
 * A fake API: routes are "METHOD /path" -> handler; unknown routes are 404.
 * Returns every call made, so tests can check what the UI sent.
 */
export function fakeApi(me: Me | null, routes: Record<string, Handler> = {}) {
  const calls: Call[] = []
  let current = me
  const all: Record<string, Handler> = {
    'GET /api/info': () => ({ json: { name: 'LLM Service', version: '1.2.3' } }),
    'GET /api/auth/me': () => (current ? { json: current } : { status: 401, json: { status: 'unauthorized' } }),
    'POST /api/auth/logout': () => {
      current = null
      return { status: 204 }
    },
    'GET /api/account/keys': () => ({ json: { keys: [], spend: 1.5, budget: 10 } }),
    'GET /api/admin/overview': () => ({ json: { people: 3, admins: 1, spend: 4.2, overCredit: [], services: [{ name: 'gateway', purpose: '', ok: true, detail: '' }], model: 'Test-Model' } }),
    ...routes,
  }
  vi.spyOn(globalThis, 'fetch').mockImplementation(async (input, init = {}) => {
    const url = new URL(String(input), 'https://llm.test')
    const method = (init.method ?? 'GET').toUpperCase()
    const body = typeof init.body === 'string' ? JSON.parse(init.body) : init.body
    calls.push({ method, path: url.pathname + url.search, body, headers: (init.headers ?? {}) as Record<string, string> })
    const handler = all[`${method} ${url.pathname}`]
    if (!handler) return new Response('{"status":"not_found"}', { status: 404 })
    const { status = 200, json } = handler(body, init)
    return new Response(status === 204 ? null : JSON.stringify(json ?? {}), { status })
  })
  return calls
}
