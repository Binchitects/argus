import { api } from '@/lib/api'

export interface Checklist {
  id: string
  name: string
  description: string | null
  items: string[]
  createdAt: string
  updatedAt: string
}

export const checklistsQuery = {
  queryKey: ['checklists'] as const,
  queryFn: ({ signal }: { signal: AbortSignal }) => api<Checklist[]>('/api/checklists', { signal }),
}
