import type { KeyLimits } from '@/lib/rate-limits'
import { api } from '@/lib/api'

/** What a credit is for: the chat's answers, API keys' text requests, pictures, video, speech. */
export type CreditKind = 'chat' | 'api' | 'pictures' | 'video' | 'speech'

export const creditKinds: { kind: CreditKind; label: string; hint: string }[] = [
  { kind: 'chat', label: 'Chat', hint: "the chat's answers" },
  { kind: 'api', label: 'API keys', hint: 'coding agents, IDEs and scripts: their text requests' },
  { kind: 'pictures', label: 'Pictures', hint: 'made in the chat or with a key' },
  { kind: 'video', label: 'Video', hint: 'made in the chat or with a key' },
  { kind: 'speech', label: 'Speech', hint: 'read aloud, and sound turned into text' },
]

/** A credit per kind, in dollars a calendar month; null: no limit of that kind. */
export type Credits = Record<CreditKind, number | null>

/** Where someone stands on one kind this month: spent, their own credit, and the tightest of their groups'. */
export interface Standing {
  kind: CreditKind
  spent: number
  credit: number | null
  group: string | null
  groupLeft: number | null
}

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
  /** This month's spend, every kind together; null when the gateway's request log cannot be read. */
  spend: number | null
  /** Each kind's spend this month (null when the request log cannot be read) and their own credit of it. */
  credits: Record<CreditKind, { spent: number | null; credit: number | null }>
  /** The kinds whose own credit they have used up this month. */
  overCredit: CreditKind[]
  /** An admin took their API access away: their keys are blocked, none is shown or made. */
  apiOff: boolean
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
  /** Kind by kind, with the tightest of their groups' credits; null when the request log cannot be read. */
  standing: Standing[] | null
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

/** Whether the company directory is on: what moving local accounts to it needs. */
export const signInQuery = {
  queryKey: ['admin', 'sign-in'] as const,
  queryFn: ({ signal }: { signal: AbortSignal }) => api<{ ldap: boolean }>('/api/admin/sign-in', { signal }),
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
