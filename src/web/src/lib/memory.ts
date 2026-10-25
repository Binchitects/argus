import { api } from './api'

export type MemoryScope = 'person' | 'project' | 'company'
export type MemoryKind = 'preference' | 'fact' | 'decision' | 'howto' | 'lesson'

export interface MemoryItem {
  id: string
  text: string
  scope: MemoryScope
  kind: MemoryKind
  /** A project's: its repository's key (host/group/name), or one folder's on one machine. */
  project: string | null
  /** typed, chat, code-arena, import. */
  source: string
  /** The chat it came from. */
  sourceId: string | null
  pinned: boolean
  usedAt: string | null
  uses: number
  /** Replaced by a newer memory that said otherwise. */
  replacedBy: string | null
  createdAt: string
  updatedAt: string
  /** The person's own (else the company's). */
  mine: boolean
  canEdit: boolean
}

export interface MemoryView {
  /** Memory is on for everyone here (Settings → Chat → Memory). */
  enabled: boolean
  /** The person has it on. */
  on: boolean
  max: number
  maxChars: number
  /** The person keeps the company's memories (they manage its knowledge). */
  keepsCompany: boolean
  memories: MemoryItem[]
}

export const memoriesQuery = {
  queryKey: ['account', 'memories'],
  queryFn: ({ signal }: { signal: AbortSignal }) => api<MemoryView>('/api/account/memories', { signal }),
}

/** A memory to keep, or what changes of one. */
export interface MemoryInput {
  text?: string
  scope?: MemoryScope
  kind?: MemoryKind
  project?: string
  pinned?: boolean
}

export const keepMemory = (m: MemoryInput) => api<MemoryItem>('/api/account/memories', { body: { ...m, projectKey: m.project } })
export const changeMemory = (id: string, m: MemoryInput) => api<MemoryItem>(`/api/account/memories/${id}`, { method: 'PUT', body: m })
export const forgetMemory = (id: string) => api(`/api/account/memories/${id}`, { method: 'DELETE' })

export const kindLabel: Record<MemoryKind, string> = { preference: 'Preference', fact: 'Fact', decision: 'Decision', howto: 'How to', lesson: 'Lesson' }
export const scopeLabel: Record<MemoryScope, string> = { person: 'About you', project: 'A project', company: "The company's" }
