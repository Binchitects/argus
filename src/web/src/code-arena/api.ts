import { api } from '@/lib/api'
import { reduce, type LiveState } from '@/pages/chat/live'
import type { ChatEvent, Message } from '@/pages/chat/types'

/** What code-arena web says of itself: the folder, the model, the mode, the session open now. */
export interface CodeState {
  name: string
  version: string
  /** The licence and where this version's source is, for the about box (LICENSING.md, section 7(b)). */
  license: string
  source: string
  folder: string
  project: string
  branch: string | null
  model: string
  /** The model's window, in tokens. */
  context: number
  /** off, low, medium, high, xhigh; null for the model's default. */
  thinking: string | null
  mode: Mode
  modes: { name: Mode; description: string }[]
  session: string
  /** A turn is running (the page watches it from its start). */
  busy: boolean
  arenaTools: boolean
  tools: { local: number; servers: { name: string; count: number }[] }
}

export type Mode = 'ask' | 'auto-edit' | 'plan' | 'yolo'

export const modeLabels: Record<Mode, string> = { ask: 'Ask', 'auto-edit': 'Auto-edit', plan: 'Plan', yolo: 'Yolo' }

/** A saved session of this folder, as the sidebar lists it. */
export interface SessionSummary {
  id: string
  title: string
  updatedAt: string
  messages: number
}

/** An edit as the page draws it: [op, old line, new line, text] with op ' ', '-', '+' or '⋮' (lines left out); 0: no number. */
export interface FileDiff {
  path: string
  added: number
  removed: number
  /** Changed lines past what is shown. */
  more: number
  created: boolean
  lines: [string, number, number, string][]
}

/** The session open now: its messages in the shape of Arena's chat, and its edits' diffs by the result's message. */
export interface CodeSession {
  id: string
  messages: Message[]
  diffs: Record<string, FileDiff>
  busy: boolean
}

/** Arena's chat events, and what Code Arena adds: a diff with an edit's result, "always" and Laya's look with a question, the history afresh after compaction. */
export type CodeEvent =
  | Exclude<ChatEvent, { type: 'tool_result' } | { type: 'approval' }>
  | (Extract<ChatEvent, { type: 'tool_result' }> & { diff?: FileDiff })
  | (Extract<ChatEvent, { type: 'approval' }> & { always?: string; risk?: string })
  | { type: 'reset'; messages: Message[]; diffs: Record<string, FileDiff> }

export const stateQuery = {
  queryKey: ['code', 'state'] as const,
  queryFn: ({ signal }: { signal: AbortSignal }) => api<CodeState>('/api/state', { signal }),
}

export const sessionsQuery = {
  queryKey: ['code', 'sessions'] as const,
  queryFn: ({ signal }: { signal: AbortSignal }) => api<SessionSummary[]>('/api/sessions', { signal }),
}

export const sessionQuery = {
  queryKey: ['code', 'session'] as const,
  queryFn: ({ signal }: { signal: AbortSignal }) => api<CodeSession>('/api/session', { signal }),
}

export const changeSettings = (body: { mode?: Mode; model?: string; thinking?: string | null }) => api<CodeState>('/api/settings', { body })
export const newSession = () => api<CodeSession>('/api/sessions/new', { body: {} })
export const resumeSession = (id: string) => api<CodeSession>('/api/sessions/resume', { body: { id } })
export const stopTurn = () => api('/api/stop', { body: {} })
export const answerApproval = (id: string, answer: 'allow' | 'always' | 'deny') => api('/api/approvals', { body: { id, answer } })

/** The session as it streams: Arena's live state, with the edits' diffs, what "always" covers for each question, and Laya's probabilities for a command. */
export interface CodeLive extends LiveState {
  diffs: Record<string, FileDiff>
  always: Record<string, string>
  risks: Record<string, string>
}

export const fromSession = (s: CodeSession | undefined): CodeLive => ({
  messages: s?.messages ?? [],
  leaf: s?.messages.at(-1)?.id ?? null,
  notices: [],
  title: null,
  thinkingSince: null,
  diffs: s?.diffs ?? {},
  always: {},
  risks: {},
})

/** One event folded in: Arena's reducer, and Code Arena's own fields around it. */
export function reduceCode(state: CodeLive, e: CodeEvent, localId: string | null): CodeLive {
  switch (e.type) {
    case 'reset':
      return { ...state, messages: e.messages, diffs: e.diffs, leaf: e.messages.at(-1)?.id ?? null, current: null, compacting: false }
    case 'approval':
      return {
        ...(reduce(state, e, localId) as CodeLive),
        always: e.always ? { ...state.always, [e.id]: e.always } : state.always,
        risks: e.risk ? { ...state.risks, [e.id]: e.risk } : state.risks,
      }
    case 'tool_result':
      return { ...(reduce(state, e, localId) as CodeLive), diffs: e.diff ? { ...state.diffs, [e.messageId]: e.diff } : state.diffs }
    default:
      return reduce(state, e, localId) as CodeLive
  }
}

/** Each message's place: the history is one line, m0, m1… (a question not yet taken goes last). */
const place = (m: Message) => (/^m\d+$/.test(m.id) ? Number(m.id.slice(1)) : Number.MAX_SAFE_INTEGER)

/** The messages in order. The results of calls made at once may arrive in any order; their places say where they go. */
export const inOrder = (messages: Message[]) => [...messages].sort((a, b) => place(a) - place(b))

