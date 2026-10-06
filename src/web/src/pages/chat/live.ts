import type { AgentWork, Attachment, ChatEvent, Message } from './types'

/** A tool call while it runs: since when, and the progress its server last reported. */
export interface ToolRunning {
  since: number
  progress?: number
  total?: number | null
  message?: string | null
}

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
  /** Tool calls waiting for the person to allow them. */
  waiting?: string[]
  /** Tool calls running: since when, and how far they are when their server says. */
  calls?: Record<string, ToolRunning>
  /** Sub-agents of a delegate call (by its id), as they work. */
  agents?: Record<string, AgentWork[]>
  /** In line for a turn (the model serves few at once): how many go first. */
  queued?: number | null
  /** The answer being written: text and tool calls go to it. */
  current?: string | null
  /** The older messages are being summarized. */
  compacting?: boolean
  /** "compact": this stream only compacts the chat (no answer is written). */
  mode?: 'answer' | 'compact'
  /** The answer is deep research (its status line says which step it is on). */
  research?: boolean
  /** A comparison being answered: its question, the side answering now, and each side's first message once it started. */
  arena?: LiveArena | null
}

/** A comparison as it streams: step `step` of `of` is `side`'s answer. */
export interface LiveArena {
  id: string
  questionId: string
  side: 'a' | 'b'
  step: number
  of: number
  a?: string
  b?: string
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

/** Puts a message in its place when the page has it already (an answer watched again from its start), else at the end. */
function put(messages: Message[], m: Message) {
  const i = messages.findIndex((x) => x.id === m.id)
  if (i >= 0) messages[i] = m
  else messages.push(m)
}

/**
 * Applies one event from the stream. `localId` is the id the question was shown
 * under. Replaying an answer from its start over the saved chat (a page that came
 * back) rebuilds it: each message the answer made is started again from nothing.
 */
export function reduce(state: LiveState, e: ChatEvent, localId: string | null, now = Date.now()): LiveState {
  const messages = [...state.messages]
  const lastAssistant = (): Message | undefined => {
    const i = state.current ? messages.findIndex((m) => m.id === state.current) : -1
    if (i >= 0) return (messages[i] = { ...messages[i]! })
    for (let j = messages.length - 1; j >= 0; j--) if (messages[j]!.role === 'assistant') return (messages[j] = { ...messages[j]! })
    return undefined
  }
  switch (e.type) {
    case 'question': {
      // The server's id replaces the one the question was shown under.
      const i = messages.findIndex((m) => m.id === localId)
      if (i >= 0) messages[i] = { ...messages[i]!, id: e.id, parentId: e.parentId }
      return { ...state, messages, leaf: e.id, mode: 'answer' }
    }
    case 'compacting':
      return { ...state, compacting: true, mode: state.mode ?? 'compact' }
    case 'compacted': {
      const i = messages.findIndex((m) => m.id === e.id)
      if (i >= 0) messages[i] = { ...messages[i]!, summary: e.summary }
      return { ...state, messages, compacting: false }
    }
    case 'title':
      return { ...state, title: e.title }
    case 'assistant': {
      put(messages, { ...blank(e.id, 'assistant', e.parentId), model: e.model })
      // A comparison's answer: its first message is where that side starts.
      const arena = e.side && state.arena && e.parentId === state.arena.questionId && !state.arena[e.side] ? { ...state.arena, [e.side]: e.id } : state.arena
      return { ...state, messages, leaf: e.id, current: e.id, thinkingSince: null, queued: null, arena }
    }
    case 'route': {
      const i = messages.findIndex((m) => m.id === e.id)
      if (i >= 0) messages[i] = { ...messages[i]!, details: { ...messages[i]!.details, route: e.route } }
      return { ...state, messages }
    }
    case 'arena': {
      const same = state.arena?.id === e.id ? state.arena : null
      return { ...state, arena: { a: same?.a, b: same?.b, id: e.id, questionId: e.questionId, side: e.side, step: e.step, of: e.of }, thinkingSince: null }
    }
    case 'queued':
      return { ...state, queued: e.ahead }
    case 'reasoning': {
      const a = lastAssistant()
      if (a) a.reasoning = (a.reasoning ?? '') + e.text
      return { ...state, messages, thinkingSince: state.thinkingSince ?? now }
    }
    case 'thought': {
      const a = lastAssistant()
      if (a) Object.assign(a, { thinkingMs: e.ms, cutShort: e.cutShort ?? a.cutShort })
      return { ...state, messages, thinkingSince: null }
    }
    case 'content': {
      const a = lastAssistant()
      if (a) a.content += e.text
      return { ...state, messages }
    }
    case 'usage': {
      const a = lastAssistant()
      if (a)
        Object.assign(a, {
          promptTokens: e.prompt, cachedTokens: e.cached, completionTokens: e.completion, cost: e.cost ?? null, thinkingMs: e.thinkingMs ?? a.thinkingMs, durationMs: e.durationMs,
          context: e.context ?? a.context,
        })
      return { ...state, messages, thinkingSince: null }
    }
    case 'tool_call': {
      const a = lastAssistant()
      if (a) a.toolCalls = [...(a.toolCalls ?? []), { id: e.id, function: { name: e.name, arguments: e.arguments } }]
      return { ...state, messages, calls: { ...state.calls, [e.id]: { since: now } } }
    }
    case 'agent':
      return { ...state, agents: { ...state.agents, [e.id]: agentStep(state.agents?.[e.id] ?? [], e) } }
    case 'tool_progress': {
      const call = state.calls?.[e.id]
      return { ...state, calls: { ...state.calls, [e.id]: { since: call?.since ?? now, progress: e.progress, total: e.total, message: e.message ?? call?.message } } }
    }
    case 'approval':
      return { ...state, waiting: [...(state.waiting ?? []).filter((w) => w !== e.id), e.id] }
    case 'tool_result': {
      const parent = state.leaf
      put(messages, {
        ...blank(e.messageId, 'tool', parent, e.text), toolCallId: e.id, toolName: e.name, noAccess: e.noAccess, durationMs: e.durationMs,
        status: e.declined ? 'declined' : e.isError ? 'failed' : 'complete', attachments: e.attachments ?? [], details: e.details ?? null, cost: e.cost ?? null,
      })
      return { ...state, messages, leaf: e.messageId, waiting: (state.waiting ?? []).filter((w) => w !== e.id) }
    }
    case 'notice':
      return { ...state, notices: [...state.notices, { kind: e.kind, text: e.text }] }
    case 'research':
      return { ...state, research: true }
    case 'error': {
      // A compaction the person asked for has no answer to carry it: the page says it.
      if (state.mode === 'compact') return { ...state, compacting: false }
      const a = lastAssistant()
      if (a) Object.assign(a, { status: 'failed', error: e.message })
      return { ...state, messages, thinkingSince: null }
    }
    case 'stopped':
      return stopped({ ...state, messages })
    case 'done':
      return state
  }
}

/** Stop pressed: the answer being written keeps what it has, marked stopped. */
export function stopped(state: LiveState): LiveState {
  const messages = state.messages.map((m) => (m.id === state.leaf && m.role === 'assistant' ? { ...m, status: 'stopped' as const } : m))
  return { ...state, messages, thinkingSince: null }
}

/** One sub-agent step folded into what the page shows of it. */
function agentStep(agents: AgentWork[], e: Extract<ChatEvent, { type: 'agent' }>): AgentWork[] {
  const next = [...agents]
  const blankAgent: AgentWork = { title: '', instructions: '', reasoning: '', text: '', steps: [], status: 'running', error: null, ms: null }
  const a: AgentWork = { ...(next[e.index] ?? blankAgent) }
  switch (e.event) {
    case 'start':
      Object.assign(a, { title: e.title ?? a.title, instructions: e.instructions ?? a.instructions, status: 'running' })
      break
    case 'reasoning':
      a.reasoning += e.text ?? ''
      break
    case 'content':
      a.text += e.text ?? ''
      break
    case 'tool_call':
      // Words before a tool call were on the way to it: what it says last is its answer.
      a.text = ''
      if (e.call) a.steps = [...a.steps, { id: e.call.id, name: e.call.name ?? '', arguments: e.call.arguments ?? '' }]
      break
    case 'tool_result':
      a.steps = a.steps.map((s) => (s.id === e.call?.id ? { ...s, result: e.text ?? '', isError: !!e.isError, files: e.files ?? null } : s))
      break
    case 'done':
      Object.assign(a, { status: e.error ? 'failed' : 'done', error: e.error ?? null, ms: e.ms ?? null, model: e.model ?? null, usage: e.usage ?? null })
      break
  }
  next[e.index] = a
  return next
}
