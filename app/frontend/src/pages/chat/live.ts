import type { Attachment, ChatEvent, Message } from './types'

/** Notices that came with the answer (Argus unavailable, a model that cannot see...). */
export interface Notice {
  kind: string
  text: string
}

/** The chat as it is while an answer streams: the saved messages plus what has arrived. */
export interface LiveState {
  messages: Message[]
  leaf: string | null
  notices: Notice[]
  title: string | null
  /** When the first reasoning token arrived for the answer being written (for the live timer). */
  thinkingSince: number | null
}

export const blank = (id: string, role: Message['role'], parentId: string | null, content = ''): Message => ({
  id, parentId, role, content, reasoning: null, toolName: null, toolCallId: null, toolCalls: null, attachments: [], status: 'complete',
  error: null, model: null, promptTokens: null, cachedTokens: null, completionTokens: null, thinkingMs: null, durationMs: null,
  createdAt: new Date().toISOString(), noAccess: false,
})

/** The question shown at once, before the server has it. */
export function withQuestion(state: LiveState, localId: string, parentId: string | null, text: string, attachments: Attachment[]): LiveState {
  return { ...state, messages: [...state.messages, { ...blank(localId, 'user', parentId, text), attachments }], leaf: localId, notices: [] }
}

/** Applies one event from the stream. `localId` is the id the question was shown under. */
export function reduce(state: LiveState, e: ChatEvent, localId: string | null, now = Date.now()): LiveState {
  const messages = [...state.messages]
  const lastAssistant = (): Message | undefined => {
    for (let i = messages.length - 1; i >= 0; i--) if (messages[i]!.role === 'assistant') return (messages[i] = { ...messages[i]! })
    return undefined
  }
  switch (e.type) {
    case 'question': {
      // The server's id replaces the one the question was shown under.
      const i = messages.findIndex((m) => m.id === localId)
      if (i >= 0) messages[i] = { ...messages[i]!, id: e.id, parentId: e.parentId }
      return { ...state, messages, leaf: e.id }
    }
    case 'title':
      return { ...state, title: e.title }
    case 'assistant':
      messages.push({ ...blank(e.id, 'assistant', e.parentId), model: e.model })
      return { ...state, messages, leaf: e.id, thinkingSince: null }
    case 'reasoning': {
      const a = lastAssistant()
      if (a) a.reasoning = (a.reasoning ?? '') + e.text
      return { ...state, messages, thinkingSince: state.thinkingSince ?? now }
    }
    case 'thought': {
      const a = lastAssistant()
      if (a) a.thinkingMs = e.ms
      return { ...state, messages, thinkingSince: null }
    }
    case 'content': {
      const a = lastAssistant()
      if (a) a.content += e.text
      return { ...state, messages }
    }
    case 'usage': {
      const a = lastAssistant()
      if (a) Object.assign(a, { promptTokens: e.prompt, cachedTokens: e.cached, completionTokens: e.completion, thinkingMs: e.thinkingMs ?? a.thinkingMs, durationMs: e.durationMs })
      return { ...state, messages, thinkingSince: null }
    }
    case 'tool_call': {
      const a = lastAssistant()
      if (a) a.toolCalls = [...(a.toolCalls ?? []), { id: e.id, function: { name: e.name, arguments: e.arguments } }]
      return { ...state, messages }
    }
    case 'tool_result': {
      const parent = state.leaf
      messages.push({ ...blank(e.messageId, 'tool', parent, e.text), toolCallId: e.id, toolName: e.name, noAccess: e.noAccess, status: e.isError ? 'failed' : 'complete', durationMs: e.durationMs })
      return { ...state, messages, leaf: e.messageId }
    }
    case 'notice':
      return { ...state, notices: [...state.notices, { kind: e.kind, text: e.text }] }
    case 'error': {
      const a = lastAssistant()
      if (a) Object.assign(a, { status: 'failed', error: e.message })
      return { ...state, messages, thinkingSince: null }
    }
    case 'done':
      return state
  }
}

/** Stop pressed: the answer being written keeps what it has, marked stopped. */
export function stopped(state: LiveState): LiveState {
  const messages = state.messages.map((m) => (m.id === state.leaf && m.role === 'assistant' ? { ...m, status: 'stopped' as const } : m))
  return { ...state, messages, thinkingSince: null }
}
