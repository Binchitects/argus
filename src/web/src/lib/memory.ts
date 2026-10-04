import { api } from './api'

export interface MemoryItem {
  id: string
  text: string
  createdAt: string
  updatedAt: string
}

export interface MemoryView {
  /** Memory is on for everyone here (Settings → Chat → Memory). */
  enabled: boolean
  /** The person has it on. */
  on: boolean
  max: number
  maxChars: number
  memories: MemoryItem[]
}

export const memoriesQuery = {
  queryKey: ['account', 'memories'],
  queryFn: ({ signal }: { signal: AbortSignal }) => api<MemoryView>('/api/account/memories', { signal }),
}
