import { api, ApiError } from '@/lib/api'
import type { Assistant, AssistantSummary, Attachment, ChatConfig, ChatEvent, ChatShare, Conversation, ConversationSummary, Named, QueuedMessage, SharedChat } from './types'

export const configQuery = {
  queryKey: ['chat', 'config'] as const,
  queryFn: ({ signal }: { signal: AbortSignal }) => api<ChatConfig>('/api/chat/config', { signal }),
  // Models load and unload while people chat (Admin -> Models): the menu keeps up.
  staleTime: 10_000,
  refetchInterval: 20_000,
  refetchOnWindowFocus: true,
}

/** The model a chat answers with: its own choice, else the default (the loaded one), not simply the first listed. On Auto, the main model it hands on to. */
export const chatModel = (config: ChatConfig, chosen: string | null | undefined) =>
  config.models.find((m) => m.name === chosen) ?? config.models.find((m) => m.name === config.model) ?? config.models[0]

/** What a chat that chose Auto has as its model. */
export const AUTO = 'auto'

/** The chat is on Auto: it chose it, or chose no model while Auto is the default. */
export const onAuto = (config: ChatConfig, chosen: string | null | undefined) => !!config.auto && (chosen === AUTO || (!chosen && config.auto.byDefault))

export const listQuery = (search: string, archived = false) => ({
  queryKey: ['chat', 'list', search, archived] as const,
  queryFn: ({ signal }: { signal: AbortSignal }) => {
    const q = new URLSearchParams()
    if (search) q.set('q', search)
    if (archived) q.set('archived', 'true')
    return api<ConversationSummary[]>(`/api/chat/conversations${q.size ? `?${q}` : ''}`, { signal })
  },
  // A chat answering on its own (its page was closed) shows so until it is done; chats made or changed elsewhere (another
  // device, Code Arena) come within half a minute.
  refetchInterval: (q: { state: { data?: ConversationSummary[] } }) => (q.state.data?.some((c) => c.answering) ? 4000 : 30_000),
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
export const queueMessage = (id: string, body: { content: string; attachments: string[] }) =>
  api<QueueState>(`/api/chat/conversations/${id}/queue`, { body })

/** Takes a queued message out of line. */
export const cancelQueued = (id: string, queuedId: string) => api(`/api/chat/conversations/${id}/queue/${queuedId}`, { method: 'DELETE' })

/** Send now: first in line, and the answer running stops so it goes at once. */
export const sendQueuedNow = (id: string, queuedId: string) => api<QueueState>(`/api/chat/conversations/${id}/queue/${queuedId}/now`, { body: {} })

export const archiveChat = (id: string, archived: boolean) => api(`/api/chat/conversations/${id}`, { method: 'PATCH', body: { archived } })

/**
 * Where a chat stands, asked every few seconds while it is open: when its stamp changes, something changed elsewhere (in
 * another tab or device, or Code Arena added to its session) and the chat is read again.
 */
export const stampQuery = (id: string) => ({
  queryKey: ['chat', 'stamp', id] as const,
  queryFn: ({ signal }: { signal: AbortSignal }) => api<{ stamp: string; answering: boolean }>(`/api/chat/conversations/${id}/stamp`, { signal }),
  refetchInterval: 4000,
})

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

/** Every assistant the person may use (the gallery); the chat list shows theirs and those they use. */
export const assistantsQuery = {
  queryKey: ['assistants'] as const,
  queryFn: ({ signal }: { signal: AbortSignal }) => api<AssistantSummary[]>('/api/assistants', { signal }),
}

export const assistantQuery = (id: string) => ({
  queryKey: ['assistants', id] as const,
  queryFn: ({ signal }: { signal: AbortSignal }) => api<Assistant>(`/api/assistants/${id}`, { signal }),
})

/** The groups the person can share with (every group, for an admin). */
export const sharingGroupsQuery = {
  queryKey: ['sharing', 'groups'] as const,
  queryFn: ({ signal }: { signal: AbortSignal }) => api<(Named & { mine: boolean })[]>('/api/sharing/groups', { signal }),
}

/** A chat's link, if it has one. */
export const shareQuery = (chatId: string) => ({
  queryKey: ['chat', 'share', chatId] as const,
  queryFn: ({ signal }: { signal: AbortSignal }) => api<{ share: ChatShare | null }>(`/api/chat/conversations/${chatId}/share`, { signal }).then((r) => r.share),
})

export const sharedQuery = (shareId: string) => ({
  queryKey: ['shared', shareId] as const,
  queryFn: ({ signal }: { signal: AbortSignal }) => api<SharedChat>(`/api/shared/${shareId}`, { signal }),
})

/** Where a shared chat opens. */
export const shareUrl = (shareId: string) => `${window.location.origin}/shared/${shareId}`
