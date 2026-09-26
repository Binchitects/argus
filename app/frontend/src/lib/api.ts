export interface AppInfo {
  name: string
  version: string
  /** Branding (Settings page). */
  signInHeadline?: string | null
  supportContact?: string | null
}

/** A support contact as a link: an email address becomes mailto:, a web address stays. */
export function supportHref(contact: string): string | null {
  if (/^https?:\/\//i.test(contact)) return contact
  if (/^[^\s@]+@[^\s@]+\.[^\s@]+$/.test(contact)) return `mailto:${contact}`
  return null
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
  /** The whole answer, for errors that carry more (the Settings page's per-field errors). */
  readonly data: unknown

  constructor(http: number, status: string, message: string, data?: unknown) {
    super(message)
    this.name = 'ApiError'
    this.http = http
    this.status = status
    this.data = data
  }

  /** Per-field messages ({"Ldap:Url": "..."}), when the answer has them. */
  get fieldErrors(): Record<string, string> | undefined {
    const e = (this.data as { errors?: unknown } | undefined)?.errors
    return e && typeof e === 'object' ? (e as Record<string, string>) : undefined
  }
}

export interface ApiInit {
  method?: string
  body?: unknown
  signal?: AbortSignal
}

/**
 * Every call goes through here. Every request carries X-Requested-With, which the
 * API requires on state changes (a cross-site page cannot add it).
 */
export async function api<T>(path: string, init: ApiInit = {}): Promise<T> {
  const isForm = typeof FormData !== 'undefined' && init.body instanceof FormData
  const res = await fetch(path, {
    method: init.method ?? (init.body === undefined ? 'GET' : 'POST'),
    signal: init.signal,
    credentials: 'same-origin',
    headers: {
      Accept: 'application/json',
      'X-Requested-With': 'fetch',
      ...(init.body === undefined || isForm ? {} : { 'Content-Type': 'application/json' }),
    },
    body: init.body === undefined ? undefined : isForm ? (init.body as FormData) : JSON.stringify(init.body),
  })
  if (res.status === 204) return undefined as T
  const text = await res.text()
  const data = text ? safeJson(text) : undefined
  if (!res.ok) {
    const d = (data ?? {}) as { status?: string; error?: string; title?: string }
    throw new ApiError(res.status, d.status ?? String(res.status), d.error ?? d.title ?? `Request failed (HTTP ${res.status}).`, data)
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

export const infoQuery = {
  queryKey: ['info'] as const,
  queryFn: ({ signal }: { signal: AbortSignal }) => api<AppInfo>('/api/info', { signal }),
  staleTime: Infinity,
}

/** Who is signed in; null when nobody is. */
export const meQuery = {
  queryKey: ['me'] as const,
  queryFn: async ({ signal }: { signal: AbortSignal }): Promise<Me | null> => {
    try {
      return await api<Me>('/api/auth/me', { signal })
    } catch (e) {
      if (e instanceof ApiError && e.http === 401) return null
      throw e
    }
  },
  staleTime: 60_000,
}

/** A message for people from anything thrown. */
export function errorMessage(e: unknown, fallback = 'Something went wrong. Try again.'): string {
  if (e instanceof ApiError) return e.message
  if (e instanceof TypeError) return 'The server could not be reached. Check the connection and try again.'
  return fallback
}
