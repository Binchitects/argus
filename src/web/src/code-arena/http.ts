import { api as arenaApi, ApiError } from '@/lib/api'

// Where the IDE's own requests go. In code-arena's page, its own server (/api/…); in Arena's Code Studio, the workspace's
// engine through Arena (/api/studio/w/<id>/…), which signs the person in and passes each on with the workspace's key.
let base = ''

/** The IDE's requests from now on go under this prefix (Code Studio's workspace); '' for code-arena's own page. */
export function setIdeBase(prefix: string) {
  base = prefix.replace(/\/+$/, '')
}

/** An address of the IDE's API (/api/…) as it is reached from this page. */
export const ideUrl = (path: string) => (base && path.startsWith('/api/') ? `${base}${path.slice(4)}` : path)

/** A WebSocket's address of the IDE's API, on this page's own host. */
export const ideSocketUrl = (path: string, where: Location = window.location) => `${where.protocol === 'https:' ? 'wss' : 'ws'}://${where.host}${ideUrl(path)}`

/** The app's request helper, for the IDE's API. */
export const api = <T = unknown>(path: string, init?: Parameters<typeof arenaApi>[1]) => arenaApi<T>(ideUrl(path), init)

export { ApiError }
