import { api } from '@/lib/api'

export interface ChecklistRun {
  id: string
  checklistId: string | null
  name: string
  items: { number: number; text: string; done: boolean; note: string | null; by: 'model' | 'person' | null; doneAt: string | null }[]
  done: number
  total: number
}

export const checklistRunQuery = (chat: string) => ({
  queryKey: ['chat', chat, 'checklist'] as const,
  // 204 (none on) comes back as null.
  queryFn: async ({ signal }: { signal: AbortSignal }) => (await api<ChecklistRun | undefined>(`/api/chat/conversations/${chat}/checklist`, { signal })) ?? null,
})
