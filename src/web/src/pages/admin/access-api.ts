import { api } from '@/lib/api'

export type SubjectKind = 'Person' | 'Group' | 'Role' | 'Everyone'

export interface Subject {
  kind: SubjectKind
  id: string | null
  name: string
}

export interface Permission {
  key: string
  area: string
  description: string
  admin: boolean
  parent?: string | null
}

export type Role = AccessOverview['roles'][number]

export interface AccessOverview {
  version: number
  permissions: Permission[]
  resourceKinds: { kind: string; actions: string[] }[]
  /** Starting points for a custom role. */
  templates?: { key: string; name: string; description: string; permissions: string[] }[]
  roles: {
    id: string
    key: string
    name: string
    description: string | null
    builtIn: boolean
    permissions: string[]
    /** What it gives in all: what it names, their parts, and the reading of the areas it acts in. */
    gives?: string[]
    holders: { id: string; subject: Subject; scope: { groups: string[]; repos: string[] } | null; expiresAt: string | null }[]
  }[]
  rules: { id: string; effect: 'Allow' | 'Deny'; subject: Subject; resourceKind: string; resource: string; action: string; expiresAt: string | null; note: string | null }[]
}

export interface AccessCheck {
  allowed: boolean
  because: string[]
  isDisabled: boolean
  roles: { id: string; name: string; via: string }[]
}

/** Everything a person may do, each with why. */
export interface Effective {
  person: { id: string; userName: string; displayName: string; isDisabled: boolean; isOwner: boolean }
  roles: { id: string; name: string; via: string; scope: { groups: string[]; repos: string[] } | null; assignmentId: string | null }[]
  groups: { id: string; name: string; directory: string | null }[]
  permissions: { key: string; allowed: boolean; because: string[] }[]
  rules: AccessOverview['rules']
}

/** A group as Access sees it: its members, the roles it holds, the rules on it, and whether new people join it. */
export interface AccessGroup {
  id: string
  name: string
  description: string | null
  directory: string | null
  scim: boolean
  joinsNewPeople: boolean
  canBeDefault: boolean
  memberCount: number
  members: { id: string; name: string; userName: string; isDisabled: boolean }[]
  roles: { id: string; roleId: string; role: string; scope: { groups: string[]; repos: string[] } | null; expiresAt: string | null }[]
  rules: { id: string; effect: 'Allow' | 'Deny'; resourceKind: string; resource: string; action: string; expiresAt: string | null; note: string | null }[]
}

export const accessQuery = {
  queryKey: ['admin', 'access'] as const,
  queryFn: ({ signal }: { signal: AbortSignal }) => api<AccessOverview>('/api/admin/access', { signal }),
}

export const accessGroupsQuery = {
  queryKey: ['admin', 'access', 'groups'] as const,
  queryFn: ({ signal }: { signal: AbortSignal }) => api<AccessGroup[]>('/api/admin/access/groups', { signal }),
}

/**
 * Whether a role naming these permissions gives one, as the server decides it: it names it or a broader one that holds
 * it; and an area's administrative ".read" to whoever holds any administrative permission of the area.
 */
export function gives(named: ReadonlySet<string>, key: string, permissions: Permission[]): boolean {
  const byKey = new Map(permissions.map((p) => [p.key, p]))
  for (let k: string | null | undefined = key; k; k = byKey.get(k)?.parent) {
    if (named.has(k)) return true
  }
  const p = byKey.get(key)
  if (p?.admin && key.endsWith('.read')) {
    const area = key.slice(0, key.indexOf('.'))
    return [...named].some((n) => n.slice(0, n.indexOf('.')) === area && byKey.get(n)?.admin)
  }
  return false
}
