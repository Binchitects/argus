import { api } from '@/lib/api'

export interface Person {
  id: string
  userName: string
  displayName: string
  email: string
  isAdmin: boolean
  source: 'local' | 'ldap'
  disabled: boolean
  disabledReason: string | null
  twoFactorEnabled: boolean
  lockedOut: boolean
  lastSignInAt: string | null
  createdAt: string
  spend: number | null
  budget: number | null
}

export interface Created {
  id: string
  password: string
  apiKey: string | null
  warning: string | null
}

export interface PersonDetail {
  person: Person
  keys: { alias: string; preview: string | null; spend: number; blocked: boolean; createdAt: string | null }[]
  warning: string | null
}

export const peopleQuery = {
  queryKey: ['admin', 'people'] as const,
  queryFn: ({ signal }: { signal: AbortSignal }) => api<{ warning: string | null; people: Person[] }>('/api/admin/people', { signal }),
}

export const personQuery = (id: string) => ({
  queryKey: ['admin', 'person', id] as const,
  queryFn: ({ signal }: { signal: AbortSignal }) => api<PersonDetail>(`/api/admin/people/${id}`, { signal }),
})

/** Empty means unlimited. */
export function parseCredit(v: string): number | null | 'invalid' {
  const t = v.trim()
  if (t === '') return null
  const n = Number(t)
  return Number.isFinite(n) && n >= 0 ? n : 'invalid'
}
