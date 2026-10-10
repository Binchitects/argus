import { api } from '@/lib/api'
import type { CreditKind, Credits } from './people-api'

export interface GroupSummary {
  id: string
  name: string
  description: string | null
  /** A directory group's name or DN; null for an app group. */
  directory: string | null
  /** Made by the company's identity provider through SCIM: it decides the name and the members. */
  scim: boolean
  /** Its members' place in the answers' line: higher goes first, 0 is everyone's. */
  priority: number
  members: number
  createdAt: string
  retentionDays?: number | null
  /** What the group may spend a month, kind by kind; null: no group limit of that kind. */
  credits?: Credits
  creditPerMember?: boolean
  costCentre?: string | null
  requestsPerMinute?: number | null
  tokensPerMinute?: number | null
}

export interface GroupMember {
  id: string
  userName: string
  displayName: string
  email: string
  isDisabled: boolean
  /** Spent this month, every kind together; null when the gateway's log cannot be read. */
  spend?: number | null
  /** ... kind by kind. */
  spent?: Record<CreditKind, number> | null
}

/** A group's own retention, credit, safeguards and API keys' rate limits; null keeps the company's setting. */
export interface GroupPolicies {
  retentionDays: number | null
  credits: Credits
  creditPerMember: boolean
  costCentre: string | null
  secretScanning: 'refuse' | 'mask' | 'off' | null
  redactPii: 'mask' | 'off' | null
  moderation: 'check' | 'off' | null
  blockedPatterns: boolean | null
  /** Each member's API key, a minute: null is the company's setting, 0 no limit. */
  requestsPerMinute: number | null
  tokensPerMinute: number | null
}

export const noPolicies: GroupPolicies = {
  retentionDays: null,
  credits: { chat: null, api: null, pictures: null, video: null, speech: null },
  creditPerMember: false,
  costCentre: null,
  secretScanning: null,
  redactPii: null,
  moderation: null,
  blockedPatterns: null,
  requestsPerMinute: null,
  tokensPerMinute: null,
}

export interface GroupDetail extends Omit<GroupSummary, 'members'> {
  members: GroupMember[]
  policies?: GroupPolicies
  /** The members' spend this month, together. */
  spentThisMonth?: number | null
  /** ... kind by kind. */
  spentByKind?: Record<CreditKind, number> | null
}

/** A group's spend in a month, a cost centre's (each person once), or the people in no group. */
export interface ChargeRow {
  month: string
  kind: 'group' | 'cost centre' | 'none'
  name: string
  costCentre: string | null
  members: number
  spend: number
  credit: number | null
}

export interface DirectoryGroup {
  dn: string
  name: string
  people: number
}

export const groupsQuery = {
  queryKey: ['admin', 'groups'] as const,
  queryFn: ({ signal }: { signal: AbortSignal }) => api<GroupSummary[]>('/api/admin/groups', { signal }),
}

export const groupQuery = (id: string) => ({
  queryKey: ['admin', 'groups', id] as const,
  queryFn: ({ signal }: { signal: AbortSignal }) => api<GroupDetail>(`/api/admin/groups/${id}`, { signal }),
})

export const directoryGroupsQuery = {
  queryKey: ['admin', 'groups', 'directory'] as const,
  queryFn: ({ signal }: { signal: AbortSignal }) => api<DirectoryGroup[]>('/api/admin/groups/directory', { signal }),
}

export const chargebackPath = (from: string, to: string, csv = false) =>
  `/api/admin/groups/chargeback?from=${encodeURIComponent(from)}&to=${encodeURIComponent(to)}${csv ? '&format=csv' : ''}`

export const chargebackQuery = (from: string, to: string) => ({
  queryKey: ['admin', 'groups', 'chargeback', from, to] as const,
  queryFn: ({ signal }: { signal: AbortSignal }) => api<{ from: string; to: string; rows: ChargeRow[] }>(chargebackPath(from, to), { signal }),
})

/** The month (yyyy-MM, UTC) of a date, or so many months before it. */
export function monthOf(date: Date, back = 0): string {
  const d = new Date(Date.UTC(date.getUTCFullYear(), date.getUTCMonth() - back, 1))
  return `${d.getUTCFullYear()}-${String(d.getUTCMonth() + 1).padStart(2, '0')}`
}
