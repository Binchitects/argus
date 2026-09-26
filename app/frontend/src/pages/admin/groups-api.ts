import { api } from '@/lib/api'

export interface GroupSummary {
  id: string
  name: string
  description: string | null
  /** A directory group's name or DN; null for an app group. */
  directory: string | null
  members: number
  createdAt: string
}

export interface GroupMember {
  id: string
  userName: string
  displayName: string
  email: string
  isDisabled: boolean
}

export interface GroupDetail extends Omit<GroupSummary, 'members'> {
  members: GroupMember[]
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
