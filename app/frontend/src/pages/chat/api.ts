import { api, ApiError } from '@/lib/api'
import type { Attachment, ChatConfig, ChatEvent, Conversation, ConversationSummary } from './types'

export const configQuery = {
  queryKey: ['chat', 'config'] as const,
  queryFn: ({ signal }: { signal: AbortSignal }) => api<ChatConfig>('/api/chat/config', { signal }),
  staleTime: 60_000,
}

export const listQuery = (search: string) => ({
  queryKey: ['chat', 'list', search] as const,
  queryFn: ({ signal }: { signal: AbortSignal }) => api<ConversationSummary[]>(`/api/chat/conversations${search ? `?q=${encodeURIComponent(search)}` : ''}`, { signal }),
})

export const conversationQuery = (id: string) => ({
  queryKey: ['chat', 'conversation', id] as const,
  queryFn: ({ signal }: { signal: AbortSignal }) => api<Conversation>(`/api/chat/conversations/${id}`, { signal }),
})

/** Server-sent events: calls `on` for each as it arrives. Throws ApiError when the request is refused. */
export async function streamChat(path: string, body: object, on: (e: ChatEvent) => void, signal: AbortSignal): Promise<void> {
  const res = await fetch(path, {
    method: 'POST',
    signal,
    credentials: 'same-origin',
    headers: { 'Content-Type': 'application/json', Accept: 'text/event-stream', 'X-Requested-With': 'fetch' },
    body: JSON.stringify(body),
  })
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
