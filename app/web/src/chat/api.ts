import { ApiError } from '../api'

export interface Preset {
  level: string
  label: string
}

export interface ChatConfig {
  model: string | null
  presets: Preset[]
  defaultThinking: string | null
  argus: boolean
}

export interface ConversationSummary {
  id: string
  title: string
  updatedAt: string
}

export interface ToolCall {
  id: string
  function: { name: string; arguments: string }
}

export interface Attachment {
  id: string
  fileName: string
  size: number
  truncated: boolean
}

export interface Message {
  id: string
  role: 'user' | 'assistant' | 'tool'
  content: string
  reasoning: string | null
  toolName: string | null
  toolCallId: string | null
  toolCalls: ToolCall[] | null
  attachments: Attachment[]
  status: 'complete' | 'stopped' | 'failed'
  error: string | null
  promptTokens: number | null
  cachedTokens: number | null
  completionTokens: number | null
  noAccess: boolean
}

export interface Conversation {
  id: string
  title: string
  thinking: string | null
  useArgus: boolean
  messages: Message[]
}

export type ChatEvent =
  | { type: 'title'; title: string }
  | { type: 'assistant'; id: string }
  | { type: 'reasoning'; text: string }
  | { type: 'content'; text: string }
  | { type: 'usage'; prompt: number | null; cached: number | null; completion: number | null }
  | { type: 'tool_call'; id: string; name: string; arguments: string }
  | { type: 'tool_result'; id: string; name: string; text: string; isError: boolean; noAccess: boolean }
  | { type: 'notice'; kind: string; text: string }
  | { type: 'error'; message: string }
  | { type: 'done'; id: string }

/** POST and read the answer as server-sent events, calling `on` for each one. Abort to stop. */
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
    throw new ApiError(res.status, d.status ?? String(res.status), d.error ?? `Request failed (HTTP ${res.status}).`)
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

export async function upload(file: File): Promise<Attachment & { chars: number }> {
  const form = new FormData()
  form.append('file', file)
  const res = await fetch('/api/chat/attachments', { method: 'POST', body: form, credentials: 'same-origin', headers: { 'X-Requested-With': 'fetch' } })
  const d = await res.json().catch(() => ({}))
  if (!res.ok) throw new ApiError(res.status, d.status ?? 'error', d.error ?? `Upload failed (HTTP ${res.status}).`)
  return d
}
