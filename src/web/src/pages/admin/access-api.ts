import { api } from '@/lib/api'

export type SubjectKind = 'Person' | 'Group' | 'Role' | 'Everyone'

export interface Subject {
  kind: SubjectKind
  id: string | null
  name: string
}

export interface AccessOverview {
  version: number
  permissions: { key: string; area: string; description: string; admin: boolean; parent?: string | null }[]
  resourceKinds: { kind: string; actions: string[] }[]
  roles: {
    id: string
    key: string
    name: string
    description: string | null
    builtIn: boolean
    permissions: string[]
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

export const accessQuery = {
  queryKey: ['admin', 'access'] as const,
  queryFn: ({ signal }: { signal: AbortSignal }) => api<AccessOverview>('/api/admin/access', { signal }),
}
