import { api, ApiError } from '@/lib/api'
import type { Attachment, ChatConfig, ChatEvent, Conversation, ConversationSummary, Project, ProjectSummary, QueuedMessage } from './types'

export const configQuery = {
  queryKey: ['chat', 'config'] as const,
  queryFn: ({ signal }: { signal: AbortSignal }) => api<ChatConfig>('/api/chat/config', { signal }),
  // Models load and unload while people chat (Admin -> Models): the menu keeps up.
  staleTime: 10_000,
  refetchInterval: 20_000,
  refetchOnWindowFocus: true,
}

/** The model a chat answers with: its own choice, else the default (the loaded one), not simply the first listed. */
export const chatModel = (config: ChatConfig, chosen: string | null | undefined) =>
  config.models.find((m) => m.name === chosen) ?? config.models.find((m) => m.name === config.model) ?? config.models[0]

export const listQuery = (search: string, archived = false) => ({
  queryKey: ['chat', 'list', search, archived] as const,
  queryFn: ({ signal }: { signal: AbortSignal }) => {
    const q = new URLSearchParams()
    if (search) q.set('q', search)
    if (archived) q.set('archived', 'true')
    return api<ConversationSummary[]>(`/api/chat/conversations${q.size ? `?${q}` : ''}`, { signal })
  },
  // A chat answering on its own (its page was closed) shows so until it is done.
  refetchInterval: (q: { state: { data?: ConversationSummary[] } }) => (q.state.data?.some((c) => c.answering) ? 4000 : false),
})

/** Forks a chat up to a message (default: the end of the branch on screen); the new chat's id and title. */
export const forkChat = (id: string, messageId?: string) =>
  api<{ id: string; title: string }>(`/api/chat/conversations/${id}/fork`, { body: messageId ? { messageId } : {} })

/** Stops the chat's answer on the server; it keeps what it has. */
export const stopChat = (id: string) => api(`/api/chat/conversations/${id}/stop`, { body: {} })

/** "Answer now": the answer stops thinking and answers with what it has. */
export const hurryChat = (id: string) => api(`/api/chat/conversations/${id}/hurry`, { body: {} })

/** What the chat's line is after a change, and whether it is answering (a message that went at once). */
export interface QueueState {
  queued: QueuedMessage[]
  answering: boolean
}

/** Queues a message to send when the answer ends (at once when the chat is not answering). */
export const queueMessage = (id: string, body: { content: string; attachments: string[]; research?: boolean }) =>
  api<QueueState>(`/api/chat/conversations/${id}/queue`, { body })

/** Takes a queued message out of line. */
export const cancelQueued = (id: string, queuedId: string) => api(`/api/chat/conversations/${id}/queue/${queuedId}`, { method: 'DELETE' })

/** Send now: first in line, and the answer running stops so it goes at once. */
export const sendQueuedNow = (id: string, queuedId: string) => api<QueueState>(`/api/chat/conversations/${id}/queue/${queuedId}/now`, { body: {} })

export const archiveChat = (id: string, archived: boolean) => api(`/api/chat/conversations/${id}`, { method: 'PATCH', body: { archived } })

export const conversationQuery = (id: string) => ({
  queryKey: ['chat', 'conversation', id] as const,
  queryFn: ({ signal }: { signal: AbortSignal }) => api<Conversation>(`/api/chat/conversations/${id}`, { signal }),
})

/**
 * Server-sent events: calls `on` for each as it arrives. Throws ApiError when the
 * request is refused. No body: watch the answer being written (GET), which ends at
 * once when there is none. Aborting only stops watching: the answer goes on.
 */
export async function streamChat(path: string, body: object | null, on: (e: ChatEvent) => void, signal: AbortSignal): Promise<void> {
  const res = await fetch(path, {
    method: body ? 'POST' : 'GET',
    signal,
    credentials: 'same-origin',
    headers: { ...(body && { 'Content-Type': 'application/json' }), Accept: 'text/event-stream', 'X-Requested-With': 'fetch' },
    body: body ? JSON.stringify(body) : undefined,
  })
  if (res.status === 204) return
  if (!res.ok || !res.body) {
    const d = (await res.json().catch(() => ({}))) as { status?: string; error?: string }
    throw new ApiError(res.status, d.status ?? String(res.status), d.error ?? `Request failed (HTTP ${res.status}).`, d)
  }
  const reader = res.body.pipeThrough(new TextDecoderStream()).getReader()
  let buffer = ''
  for (;;) {
    const { value, done } = await reader.read()
    if (done) break
    buffer += value
    let end: number
    while ((end = buffer.indexOf('\n\n')) >= 0) {
      const block = buffer.slice(0, end)
      buffer = buffer.slice(end + 2)
      for (const line of block.split('\n')) if (line.startsWith('data: ')) on(JSON.parse(line.slice(6)) as ChatEvent)
    }
  }
}

/** Uploads one file with progress (fetch cannot report upload progress). */
export function uploadFile(file: File, onProgress: (share: number) => void, signal?: AbortSignal): Promise<Attachment> {
  return new Promise((resolve, reject) => {
    const xhr = new XMLHttpRequest()
    xhr.open('POST', '/api/chat/attachments')
    xhr.setRequestHeader('X-Requested-With', 'fetch')
    xhr.setRequestHeader('Accept', 'application/json')
    xhr.upload.onprogress = (e) => e.lengthComputable && onProgress(e.loaded / e.total)
    xhr.onload = () => {
      let data: { status?: string; error?: string } & Partial<Attachment> = {}
      try {
        data = JSON.parse(xhr.responseText)
      } catch {
        // not JSON: an error page from a proxy
      }
      if (xhr.status >= 200 && xhr.status < 300) resolve({ truncated: false, ...data } as Attachment)
      else reject(new ApiError(xhr.status, data.status ?? String(xhr.status), data.error ?? `Upload failed (HTTP ${xhr.status}).`, data))
    }
    xhr.onerror = () => reject(new TypeError('The upload did not reach the server.'))
    signal?.addEventListener('abort', () => xhr.abort())
    const form = new FormData()
    form.append('file', file)
    xhr.send(form)
  })
}

export const attachmentUrl = (id: string) => `/api/chat/attachments/${id}/content`
/** The file itself, to save (never shown in the page). */
export const downloadUrl = (id: string) => `${attachmentUrl(id)}?download=1`

export const projectsQuery = {
  queryKey: ['projects'] as const,
  queryFn: ({ signal }: { signal: AbortSignal }) => api<ProjectSummary[]>('/api/projects', { signal }),
}

export const projectQuery = (id: string) => ({
  queryKey: ['projects', id] as const,
  queryFn: ({ signal }: { signal: AbortSignal }) => api<Project>(`/api/projects/${id}`, { signal }),
})
