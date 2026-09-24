export interface AppInfo {
  name: string
  version: string
}

export interface Me {
  id: string
  userName: string
  displayName: string
  email: string
  isAdmin: boolean
  source: 'local' | 'ldap'
  twoFactorEnabled: boolean
  signedInAt: number | null
}

/** An API answer that was not 2xx. `status` is the app's code ("invalid", "locked", ...), `message` is for people. */
export class ApiError extends Error {
  readonly http: number
  readonly status: string

  constructor(http: number, status: string, message: string) {
    super(message)
    this.http = http
    this.status = status
  }
}

/**
 * Every call goes through here. State-changing calls carry X-Requested-With,
 * which the backend requires (a cross-site page cannot add it).
 */
export async function api<T>(path: string, init: { method?: string; body?: unknown; signal?: AbortSignal } = {}): Promise<T> {
  const res = await fetch(path, {
    method: init.method ?? (init.body === undefined ? 'GET' : 'POST'),
    signal: init.signal,
    credentials: 'same-origin',
    headers: {
      Accept: 'application/json',
      'X-Requested-With': 'fetch',
      ...(init.body === undefined ? {} : { 'Content-Type': 'application/json' }),
    },
    body: init.body === undefined ? undefined : JSON.stringify(init.body),
  })
  if (res.status === 204) return undefined as T
  const text = await res.text()
  const data = text ? safeJson(text) : undefined
  if (!res.ok) {
    const d = (data ?? {}) as { status?: string; error?: string; title?: string }
    throw new ApiError(res.status, d.status ?? String(res.status), d.error ?? d.title ?? `Request failed (HTTP ${res.status}).`)
  }
  return data as T
}

function safeJson(text: string): unknown {
  try {
    return JSON.parse(text)
  } catch {
    return undefined
  }
}

export const getJson = <T,>(path: string, signal?: AbortSignal) => api<T>(path, { signal })

export const infoQuery = {
  queryKey: ['info'],
  queryFn: ({ signal }: { signal: AbortSignal }) => getJson<AppInfo>('/api/info', signal),
  staleTime: Infinity,
}

/** Who is signed in; null when nobody is. */
export const meQuery = {
  queryKey: ['me'],
  queryFn: async ({ signal }: { signal: AbortSignal }): Promise<Me | null> => {
    try {
      return await getJson<Me>('/api/auth/me', signal)
    } catch (e) {
      if (e instanceof ApiError && e.http === 401) return null
      throw e
    }
  },
  staleTime: 60_000,
}
