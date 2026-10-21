import { api } from '@/lib/api'

export type AppReach = 'private' | 'groups' | 'company'

export interface AppBrief {
  id: string
  name: string
  description: string
  kind: 'html' | 'react'
  version: number
  reach: AppReach
  groupIds: string[]
  remixOfId: string | null
  conversationId: string | null
  opens: number
  createdAt: string
  updatedAt: string
  owner: string | null
  mine: boolean
}

export interface AppVersion {
  version: number
  note: string | null
  createdAt: string
  chars: number
}

export const appsQuery = {
  queryKey: ['apps'] as const,
  queryFn: ({ signal }: { signal: AbortSignal }) => api<AppBrief[]>('/api/apps', { signal }),
}

export const appQuery = (id: string) => ({
  queryKey: ['apps', id] as const,
  queryFn: ({ signal }: { signal: AbortSignal }) => api<{ app: AppBrief; code: string; viewer: string }>(`/api/apps/${id}?open=true`, { signal }),
})

export const versionsQuery = (id: string) => ({
  queryKey: ['apps', id, 'versions'] as const,
  queryFn: ({ signal }: { signal: AbortSignal }) => api<AppVersion[]>(`/api/apps/${id}/versions`, { signal }),
})

export const publishApp = (body: { name: string; description: string; kind: 'html' | 'react'; code: string; reach: AppReach; groupIds: string[]; conversationId?: string }) =>
  api<AppBrief>('/api/apps', { body })

export const changeApp = (id: string, body: Partial<{ name: string; description: string; code: string; reach: AppReach; groupIds: string[]; note: string }>) =>
  api<AppBrief>(`/api/apps/${id}`, { method: 'PUT', body })

export const removeApp = (id: string) => api(`/api/apps/${id}`, { method: 'DELETE' })

export const remixApp = (id: string) => api<AppBrief>(`/api/apps/${id}/remix`, { body: {} })

export const restoreVersion = (id: string, version: number) => api<AppBrief>(`/api/apps/${id}/versions/${version}/restore`, { body: {} })

export const reachLabels: Record<AppReach, string> = { private: 'Only me', groups: 'Some groups', company: 'Everyone' }
