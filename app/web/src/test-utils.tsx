import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { render } from '@testing-library/react'
import { createMemoryRouter } from 'react-router'
import { RouterProvider } from 'react-router/dom'
import { vi } from 'vitest'
import type { Me } from './api'
import { routes } from './routes'

/** Renders the whole app at `path`, with its own query cache so tests never share data. */
export function renderApp(path: string) {
  const client = new QueryClient({ defaultOptions: { queries: { retry: false } } })
  const router = createMemoryRouter(routes, { initialEntries: [path] })
  const view = render(
    <QueryClientProvider client={client}>
      <RouterProvider router={router} />
    </QueryClientProvider>,
  )
  return { ...view, router }
}

export const admin: Me = {
  id: 'a1', userName: 'admin', displayName: 'Ada Admin', email: 'admin@example.test',
  isAdmin: true, source: 'local', twoFactorEnabled: false, signedInAt: 1_700_000_000,
}
export const member: Me = { ...admin, id: 'm1', userName: 'mo', displayName: 'Mo Member', email: 'mo@example.test', isAdmin: false }

type Handler = (body: unknown, init: RequestInit) => { status?: number; json?: unknown; events?: object[]; hang?: boolean; offline?: boolean }
export interface Call { method: string; path: string; body: unknown; headers: Record<string, string> }

/**
 * A fake backend: routes are "METHOD /path" -> handler. Unknown routes are 404.
 * Returns every call made, so tests can check what the UI sent.
 */
export function fakeBackend(me: Me | null, routes: Record<string, Handler> = {}) {
  const calls: Call[] = []
  const all: Record<string, Handler> = {
    'GET /api/info': () => ({ json: { name: 'LLM Service', version: '1.2.3' } }),
    'GET /api/auth/me': () => (me ? { json: me } : { status: 401, json: { status: 'unauthorized' } }),
    'POST /api/auth/logout': () => ({ status: 204 }),
    ...routes,
  }
  vi.spyOn(globalThis, 'fetch').mockImplementation(async (input, init = {}) => {
    const url = new URL(String(input), 'https://llm.test')
    const method = (init.method ?? 'GET').toUpperCase()
    const body = init.body ? JSON.parse(String(init.body)) : undefined
    calls.push({ method, path: url.pathname + url.search, body, headers: (init.headers ?? {}) as Record<string, string> })
    const handler = all[`${method} ${url.pathname}`]
    if (!handler) return new Response('{"status":"not_found"}', { status: 404 })
    const { status = 200, json, events, hang, offline } = handler(body, init)
    // What fetch does when the request never gets an answer (network down, TLS failure).
    if (offline) throw new TypeError('Failed to fetch')
    if (events) {
      // Server-sent events, one per chunk; `hang` keeps the stream open until aborted.
      const stream = new ReadableStream<Uint8Array>({
        start(controller) {
          for (const e of events) controller.enqueue(new TextEncoder().encode(`data: ${JSON.stringify(e)}\n\n`))
          if (!hang) controller.close()
          init.signal?.addEventListener('abort', () => controller.error(new DOMException('Aborted', 'AbortError')))
        },
      })
      return new Response(stream, { status: 200, headers: { 'Content-Type': 'text/event-stream' } })
    }
    return new Response(status === 204 ? null : JSON.stringify(json ?? {}), { status })
  })
  return calls
}
