import type { KeyLimits } from '@/lib/rate-limits'
import { api } from '@/lib/api'

export interface Person {
  id: string
  userName: string
  displayName: string
  email: string
  isAdmin: boolean
  source: 'local' | 'ldap' | 'oidc'
  disabled: boolean
  disabledReason: string | null
  twoFactorEnabled: boolean
  lockedOut: boolean
  lastSignInAt: string | null
  createdAt: string
  spend: number | null
  budget: number | null
  /** Set while on legal hold: nothing of theirs is deleted. */
  legalHoldSince?: string | null
  legalHoldReason?: string | null
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
  /** The groups they are in: app groups they were added to, and directory groups that match. */
  groups: { id: string; name: string; directory: boolean }[]
  /** For directory people: the directory's groups, as of their last sign-in or check. */
  directoryGroups: string[]
  /** Their API keys' rate limits: their own, and what applies with where it comes from. */
  limits?: KeyLimits
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
