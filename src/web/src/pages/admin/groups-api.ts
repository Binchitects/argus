import { api } from '@/lib/api'

export interface GroupSummary {
  id: string
  name: string
  description: string | null
  /** A directory group's name or DN; null for an app group. */
  directory: string | null
  /** Made by the company's identity provider through SCIM: it decides the name and the members. */
  scim: boolean
  members: number
  createdAt: string
  retentionDays?: number | null
  credit?: number | null
  creditPerMember?: boolean
  costCentre?: string | null
}

export interface GroupMember {
  id: string
  userName: string
  displayName: string
  email: string
  isDisabled: boolean
  /** Spent this month, over the chat and API keys; null when the gateway's log cannot be read. */
  spend?: number | null
}

/** A group's own retention, credit and safeguards; null keeps the company's setting. */
export interface GroupPolicies {
  retentionDays: number | null
  credit: number | null
  creditPerMember: boolean
  costCentre: string | null
  secretScanning: 'refuse' | 'mask' | 'off' | null
  redactPii: 'mask' | 'off' | null
  moderation: 'check' | 'off' | null
  blockedPatterns: boolean | null
}

export const noPolicies: GroupPolicies = {
  retentionDays: null,
  credit: null,
  creditPerMember: false,
  costCentre: null,
  secretScanning: null,
  redactPii: null,
  moderation: null,
  blockedPatterns: null,
}

export interface GroupDetail extends Omit<GroupSummary, 'members'> {
  members: GroupMember[]
  policies?: GroupPolicies
  /** The members' spend this month, together. */
  spentThisMonth?: number | null
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
