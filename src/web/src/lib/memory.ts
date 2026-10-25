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
  /** Proposed from a chat that went quiet: waiting for the person, not read by answers until kept. */
  proposed: boolean
  /** The memory a proposal says otherwise than: kept, it replaces that one. */
  replaces: string | null
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
  /** What memory proposes from the person's quiet chats is kept at once. */
  auto: boolean
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
export const keepProposal = (id: string) => api<MemoryItem>(`/api/account/memories/${id}/keep`, { body: {} })

/** A change the memory garden made: memories merged into one, let go, or one tightened; with what they were, to undo it. */
export interface MemoryChange {
  id: string
  action: 'merged' | 'let_go' | 'tightened'
  reason: string
  before: { id: string; text: string; kind: MemoryKind }[]
  memoryId: string | null
  after: string | null
  at: string
  undoneAt: string | null
}

export const memoryHistoryQuery = {
  queryKey: ['account', 'memories', 'history'],
  queryFn: ({ signal }: { signal: AbortSignal }) => api<{ tidiedAt: string | null; changes: MemoryChange[] }>('/api/account/memories/history', { signal }),
}

export const undoMemoryChange = (id: string) => api<MemoryChange>(`/api/account/memories/history/${id}/undo`, { body: {} })
export const tidyMemories = () => api<{ changes: number }>('/api/account/memories/tidy', { body: {} })
/** Memories from Markdown (Arena's own download, another assistant's export, or a list of lines). */
export const importMemories = (markdown: string) => api<{ kept: number; skipped: number }>('/api/account/memories/import', { body: { markdown } })

export const changeLabel: Record<MemoryChange['action'], string> = { merged: 'Merged', let_go: 'Let go', tightened: 'Tightened' }

export const kindLabel: Record<MemoryKind, string> = { preference: 'Preference', fact: 'Fact', decision: 'Decision', howto: 'How to', lesson: 'Lesson' }
export const scopeLabel: Record<MemoryScope, string> = { person: 'About you', project: 'A project', company: "The company's" }
