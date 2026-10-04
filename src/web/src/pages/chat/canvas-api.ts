import { api } from '@/lib/api'

/** A canvas of a chat, without its text: a document (Markdown) or code. */
export interface CanvasSummary {
  id: string
  conversationId: string
  title: string
  kind: 'document' | 'code'
  language: string | null
  /** Its latest version's number, from 1. */
  version: number
  lines: number
  createdAt: string
  updatedAt: string
}

export interface Canvas extends CanvasSummary {
  content: string
}

/** One change: who made it, when, and a short summary. */
export interface CanvasVersionInfo {
  number: number
  title: string
  author: 'person' | 'model'
  summary: string
  createdAt: string
}

export interface CanvasVersion extends CanvasVersionInfo {
  content: string
}

export const canvasesQuery = (chatId: string) => ({
  queryKey: ['chat', 'canvases', chatId] as const,
  queryFn: ({ signal }: { signal: AbortSignal }) => api<CanvasSummary[]>(`/api/chat/conversations/${chatId}/canvases`, { signal }),
})

export const canvasQuery = (id: string) => ({
  queryKey: ['chat', 'canvas', id] as const,
  queryFn: ({ signal }: { signal: AbortSignal }) => api<Canvas>(`/api/chat/canvases/${id}`, { signal }),
})

export const versionsQuery = (id: string) => ({
  queryKey: ['chat', 'canvas', id, 'versions'] as const,
  queryFn: ({ signal }: { signal: AbortSignal }) => api<CanvasVersionInfo[]>(`/api/chat/canvases/${id}/versions`, { signal }),
})

/** A version never changes once made. */
export const versionQuery = (id: string, number: number) => ({
  queryKey: ['chat', 'canvas', id, 'versions', number] as const,
  queryFn: ({ signal }: { signal: AbortSignal }) => api<CanvasVersion>(`/api/chat/canvases/${id}/versions/${number}`, { signal }),
  staleTime: Infinity,
})

export const createCanvas = (chatId: string, body: { title: string; kind: 'document' | 'code'; language?: string | null; content?: string }) =>
  api<Canvas>(`/api/chat/conversations/${chatId}/canvases`, { body })

/** The person's save, made on `baseVersion`: refused (409) when the canvas moved on since. */
export const saveCanvas = (id: string, body: { baseVersion: number; content: string; title?: string; language?: string | null }) =>
  api<Canvas>(`/api/chat/canvases/${id}`, { method: 'PUT', body })

export const restoreVersion = (id: string, number: number) => api<Canvas>(`/api/chat/canvases/${id}/versions/${number}/restore`, { body: {} })

export const deleteCanvas = (id: string) => api(`/api/chat/canvases/${id}`, { method: 'DELETE' })

export type CanvasExport = 'md' | 'docx' | 'pdf' | 'file'

export const exportUrl = (id: string, format: CanvasExport) => `/api/chat/canvases/${id}/export?format=${format}`
