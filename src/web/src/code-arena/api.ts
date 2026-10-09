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
  /** Code Arena's page of the manual, in the Arena signed in to (https://DOMAIN/help/code-arena); null without its address. */
  manual: string | null
  folder: string
  project: string
  branch: string | null
  model: string
  /** The model's window, in tokens. */
  context: number
  /** About how many of them the conversation fills now (the prompt, the tools and the history). */
  contextUsed: number
  /** When the session compacts itself, in percent of the window, and what the recent part may keep whole. */
  compactAt: number
  compactTarget: number
  /** Arena's, Argus's and the person's MCP servers, each connected in the background. */
  servers: ServerStatus[]
  /** The commands run with no time limit in this session, running or ended. */
  jobs: JobSummary[]
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

/** One MCP server of the session, as code-arena sees it now. */
export interface ServerStatus {
  /** arena, argus, or the name in the config. */
  name: string
  title: string
  url: string | null
  state: 'connecting' | 'connected' | 'unavailable' | 'failed'
  /** Its tools in the session (Argus's that Arena serves count as Arena's). */
  tools: number
  /** In words: "Arena: 14 tools", "Argus: not connected, tried again at 10:41:07 (…)". */
  status: string
  error: string | null
  nextTry: string | null
}

/** A command run with no time limit. */
export interface JobSummary {
  id: number
  command: string
  running: boolean
  /** "running for 2m 10s", "exit code 0 after 3m 12s", "stopped (by the person, in the IDE) after 41s". */
  status: string
}

/** When the session compacts itself, as code-arena bounds it. */
export const compaction = { defaultAt: 80, defaultTarget: 25, minAt: 20, maxAt: 95, minTarget: 5, gap: 10 } as const

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

/**
 * Arena's chat events, and what Code Arena adds: a diff with an edit's result, "always" and Laya's look with a question, the history
 * afresh after compaction, and the commands run with no time limit (their start, their output as it comes, their end).
 */
export type CodeEvent =
  | Exclude<ChatEvent, { type: 'tool_result' } | { type: 'approval' }>
  | (Extract<ChatEvent, { type: 'tool_result' }> & { diff?: FileDiff })
  | (Extract<ChatEvent, { type: 'approval' }> & { always?: string; risk?: string })
  | { type: 'reset'; messages: Message[]; diffs: Record<string, FileDiff> }
  | { type: 'job'; job: number; command: string; running: true; status: string }
  | { type: 'job_output'; job: number; text: string }
  | { type: 'job_end'; job: number; running: false; status: string; exitCode: number | null; stopped: boolean }

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

export const changeSettings = (body: { mode?: Mode; model?: string; thinking?: string | null; compactAt?: number; compactTarget?: number }) => api<CodeState>('/api/settings', { body })
/** Tries a server again now (or every one not connected); the state as it is then. */
export const retryServers = (name?: string) => api<CodeState>('/api/servers/retry', { body: name ? { name } : {} })
/** Stops a command run with no time limit: the agent is told it ended. */
export const stopJob = (id: number) => api('/api/jobs/stop', { body: { id } })
export const newSession = () => api<CodeSession>('/api/sessions/new', { body: {} })
export const resumeSession = (id: string) => api<CodeSession>('/api/sessions/resume', { body: { id } })
export const stopTurn = () => api('/api/stop', { body: {} })
export const answerApproval = (id: string, answer: 'allow' | 'always' | 'deny') => api('/api/approvals', { body: { id, answer } })

/** A command with no time limit as the page watches it: the end of its output, and how it ended. */
export interface LiveJob {
  id: number
  command: string
  running: boolean
  status: string
  output: string
  failed: boolean
}

/** The output a page keeps of each command: the end of it (the agent reads the rest with command_output). */
export const keptOutput = 64 * 1024

/** The session as it streams: Arena's live state, with the edits' diffs, what "always" covers for each question, Laya's probabilities for a command, and the commands with no time limit. */
export interface CodeLive extends LiveState {
  diffs: Record<string, FileDiff>
  always: Record<string, string>
  risks: Record<string, string>
  jobs: LiveJob[]
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
  jobs: [],
})

/** The jobs with one changed (or added). */
const withJob = (jobs: LiveJob[], id: number, change: (j: LiveJob) => LiveJob, added?: LiveJob) =>
  jobs.some((j) => j.id === id) ? jobs.map((j) => (j.id === id ? change(j) : j)) : added ? [...jobs, added] : jobs

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
    case 'job':
      return { ...state, jobs: withJob(state.jobs, e.job, (j) => j, { id: e.job, command: e.command, running: true, status: e.status, output: '', failed: false }) }
    case 'job_output':
      return { ...state, jobs: withJob(state.jobs, e.job, (j) => ({ ...j, output: (j.output + e.text).slice(-keptOutput) })) }
    case 'job_end':
      return { ...state, jobs: withJob(state.jobs, e.job, (j) => ({ ...j, running: false, status: e.status, failed: e.stopped || e.exitCode !== 0 })) }
    default:
      return reduce(state, e, localId) as CodeLive
  }
}

/** Each message's place: the history is one line, m0, m1… (a question not yet taken goes last). */
const place = (m: Message) => (/^m\d+$/.test(m.id) ? Number(m.id.slice(1)) : Number.MAX_SAFE_INTEGER)

/** The messages in order. The results of calls made at once may arrive in any order; their places say where they go. */
export const inOrder = (messages: Message[]) => [...messages].sort((a, b) => place(a) - place(b))

