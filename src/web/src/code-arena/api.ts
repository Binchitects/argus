import { api } from './http'
import { blank, reduce, type LiveState } from '@/pages/chat/live'
import type { Attachment, ChatEvent, ChatTool, Message } from '@/pages/chat/types'
import type { Piece } from './bridge'

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
  /** When the session compacts itself, in percent of the window (0: only when a request would not fit), and what the recent part may keep whole. */
  compactAt: number
  compactTarget: number
  /** Where the threshold comes from: --compact-at, config.json, the person's Arena account, Arena's default, or the built-in default. */
  compactAtSource?: 'flag' | 'config' | 'account' | 'arena' | 'default'
  /** The threshold the Arena account gives (the person's own, else Arena's default); null when Arena did not say. */
  compactAtAccount?: number | null
  /** A newer Code Arena on the Arena ("Code Arena 7.0.0 is available…"), or what updating did; null: none. */
  update?: string | null
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
  /** Messages sent while an answer was written: each runs, in order, when the one before it ends. */
  queued: string[]
  /** The number of the answer (or compaction) running or last run: a new one is attached to when it differs. */
  turn?: number
  /** How many times the web chat's news came in while nothing ran: the session is read again when it changes. */
  synced?: number
  /** Sessions are kept with chats in Arena here: the side bar lists the person's chats there. */
  arenaChats?: boolean
  /** Whose chats in Arena the sessions are kept with: the key's account (null until Arena said). */
  account?: string | null
  /** The chat in Arena this session is kept in step with (null: none yet): the state is read often, for the web's news. */
  chat?: string | null
  /** Where the sync with Arena stands (null: sessions are not kept with Arena here). */
  sync?: SyncState | null
  /** What was said while nothing ran here (Arena out of reach, the web's news taken in), the newest last: each is shown once. */
  notices?: { id: number; text: string; at: string }[]
  /** Code completion in the editor, from this model; null: off in code-arena's config. */
  completion?: { model: string } | null
  arenaTools: boolean
  tools: { local: number; servers: { name: string; count: number }[] }
  /** The Tools menu: Arena's tools (as its chat has them) and each other server's, on or off in this session. */
  toolGroups?: ToolGroup[]
  /** Their definitions are past the budget: the model loads those it needs (load_tools). */
  toolsOnDemand?: boolean
}

/** A group of remote tools, in the shape of the web chat's tools, with whether it is on in this session and loaded. */
export interface ToolGroup extends ChatTool {
  on: boolean
  loaded: boolean
  functions: number
}

/** The session's sync with its chat in Arena. */
export interface SyncState {
  /** The chat (null: none yet: it is made with the first message). */
  chat: string | null
  /** Whose chats they are: the key's account (null until Arena said). */
  account: string | null
  /** Messages Arena does not have yet. */
  waiting: number
  /** What stopped them (Arena out of reach, a refusal); null: nothing. */
  trouble: string | null
  /** The web chat has messages not taken in yet (they come at the next turn, or at once while nothing runs). */
  behind: boolean
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
  /** A server or a watcher, run until stopped (never waited for). */
  background?: boolean
  /** The end of its output while it runs (it outlives the turn that started it); null once it ended. */
  output?: string | null
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
  /** The chat in Arena it is kept in step with; null: none. */
  chat?: string | null
  /** Arena lacks some of it (never sent, or sent in part): how many messages. */
  unsent?: boolean
  waiting?: number
}

/** A chat of the person's in Arena, to continue here: with this folder's session kept in step with it, if one is. */
export interface WebChatSummary {
  id: string
  title: string
  /** "code-arena": a Code Arena session's chat, in `place`. */
  origin: string | null
  place: string | null
  updatedAt: string
  messages: number
  session: string | null
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
  | { type: 'job'; job: number; command: string; running: true; status: string; background?: boolean }
  | { type: 'job_output'; job: number; text: string }
  | { type: 'job_end'; job: number; running: false; status: string; exitCode: number | null; stopped: boolean }
  /** What went with the question (@files, the lines chosen in the editor): path, or path:first-last. */
  | { type: 'attached'; id: string; files: string[] }

export const stateQuery = {
  queryKey: ['code', 'state'] as const,
  queryFn: ({ signal }: { signal: AbortSignal }) => api<CodeState>('/api/state', { signal }),
}

/** What fills the model's window, by kind in tokens, and its biggest messages (measured while no turn runs). */
export interface CodeWindow {
  kinds: { kind: string; tokens: number }[]
  biggest: { role: 'user' | 'assistant' | 'tool' | 'summary'; what: string; tokens: number }[]
}

export const windowQuery = {
  queryKey: ['code', 'window'] as const,
  queryFn: ({ signal }: { signal: AbortSignal }) => api<CodeWindow>('/api/window', { signal }),
}

/** A memory as Arena gives it to Code Arena: whose (the person's, this project's, the company's), its kind, pinned or not. */
export interface CodeMemory {
  id: string
  text: string
  scope: 'person' | 'project' | 'company'
  kind: string
  project: string | null
  pinned: boolean
  mine: boolean
}

export const memoryQuery = {
  queryKey: ['code', 'memory'] as const,
  queryFn: ({ signal }: { signal: AbortSignal }) => api<{ online: boolean; project: string; notebook?: string | null; memories: CodeMemory[] }>('/api/memory', { signal }),
}

/** A step of the agent's plan: what done means (its check), the files it changes, what it waits for, how its check went. */
export interface CodePlanStep {
  number: number
  title: string
  status: 'todo' | 'doing' | 'blocked' | 'done'
  check: string | null
  files: string[]
  dependsOn: number[]
  notes: string | null
  checkResult: 'passed' | 'failed' | null
  checkOutput: string | null
  /** The functions, classes or types it changes; what they reach, as Argus found it. */
  symbols?: string[]
  impact?: string | null
}

/** The agent's working plan (its plan tool), and the Arena chat it is kept with (null: not kept in step). */
export interface CodePlan {
  title: string
  goal: string | null
  steps: CodePlanStep[]
  chat: string | null
  /** proposed: made in plan mode, waiting for the person's review; approved: being carried out. */
  review?: 'proposed' | 'approved' | null
  /** The session's mode now. */
  mode?: string
  /** What a second reading with fresh eyes said of it before its review ('': it held). */
  critique?: string | null
  /** An earlier session's plan in progress in this folder, when this session has none: it can be carried on here. */
  resumable?: { title: string; done: number; steps: number; updated: string } | null
}

export const planQuery = {
  queryKey: ['code', 'plan'] as const,
  queryFn: ({ signal }: { signal: AbortSignal }) => api<CodePlan>('/api/plan', { signal }),
  // The agent changes it as it works: read again every few seconds while the view shows.
  refetchInterval: 2000,
}

export const sessionsQuery = {
  queryKey: ['code', 'sessions'] as const,
  queryFn: ({ signal }: { signal: AbortSignal }) => api<SessionSummary[]>('/api/sessions', { signal }),
}

/** The person's chats in Arena (asked of Arena when the side bar's list of them opens). */
export const webChatsQuery = {
  queryKey: ['code', 'web-chats'] as const,
  queryFn: ({ signal }: { signal: AbortSignal }) => api<WebChatSummary[]>('/api/sessions/web', { signal }),
  staleTime: 15_000,
}

export const sessionQuery = {
  queryKey: ['code', 'session'] as const,
  queryFn: ({ signal }: { signal: AbortSignal }) => api<CodeSession>('/api/session', { signal }),
}

/** What this folder sent before, newest first: the terminal's and the page's, without the terminal's own commands. */
export const historyQuery = {
  queryKey: ['code', 'history'] as const,
  queryFn: ({ signal }: { signal: AbortSignal }) => api<{ text: string }[]>('/api/history', { signal }),
}

/** compactAt: 0 compacts only when a request would not fit; null follows the Arena account's again. */
export const changeSettings = (body: { mode?: Mode; model?: string; thinking?: string | null; compactAt?: number | null; compactTarget?: number; tools?: string[] }) =>
  api<CodeState>('/api/settings', { body })

/** Where the session's threshold comes from, as the gauge and the Context dialog say it. */
export function compactWho(state: Pick<CodeState, 'compactAtSource'>): string {
  switch (state.compactAtSource) {
    case 'flag':
      return 'set with --compact-at'
    case 'config':
      return 'set in config.json'
    case 'account':
      return 'from your Arena account'
    case 'arena':
      return "Arena's default"
    default:
      return 'the default'
  }
}
/** Tries a server again now (or every one not connected); the state as it is then. */
export const retryServers = (name?: string) => api<CodeState>('/api/servers/retry', { body: name ? { name } : {} })
/** Stops a command run with no time limit: the agent is told it ended. */
export const stopJob = (id: number) => api('/api/jobs/stop', { body: { id } })
export const newSession = () => api<CodeSession>('/api/sessions/new', { body: {} })
export const resumeSession = (id: string) => api<CodeSession>('/api/sessions/resume', { body: { id } })
/** Continues a chat from Arena here: in this folder's session kept with it, else a new one with its history. */
export const openWebChat = (id: string) => api<CodeSession>('/api/sessions/web', { body: { id } })
/** Sends Arena each session of this folder it never had: how many went, and what kept the others back. */
export const sendAllSessions = () => api<{ sent: number; failed: string[] }>('/api/sessions/send-all', { body: {} })
export const stopTurn = () => api('/api/stop', { body: {} })
/** What a question is asked again with: its new words and quote (edited), shorter or longer, another model or thinking, the files put back first. */
export interface AgainBody {
  question: string
  text?: string
  quote?: string | null
  length?: 'shorter' | 'longer'
  model?: string
  thinking?: string
  files?: boolean
}
/** A message for after the answer being written (at once when none is), with the lines chosen in the editor and the part of an answer it quotes. */
export const queueMessage = (text: string, context?: Piece[], quote?: string, attachments?: Attachment[]) =>
  api<{ queued: number }>('/api/queue', { body: { text, ...(context?.length && { context }), ...(quote && { quote }), ...(attachments?.length && { attachments }) } })
// A body, so it goes as JSON: code-arena web refuses any other call that changes something.
export const clearQueue = () => api('/api/queue', { method: 'DELETE', body: {} })
export const answerApproval = (id: string, answer: 'allow' | 'always' | 'deny') => api('/api/approvals', { body: { id, answer } })

/** A command with no time limit as the page watches it: the end of its output, and how it ended. */
export interface LiveJob {
  id: number
  command: string
  running: boolean
  status: string
  output: string
  failed: boolean
  /** Running, but no turn on this page streams it: known from the state (read every second), with its Stop. */
  unwatched?: boolean
  /** A server or a watcher, run until stopped. */
  background?: boolean
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
    case 'question': {
      // A turn this page did not start (a queued message, another tab): its question comes with its text and its quote.
      const q = e as { id: string; parentId: string | null; text?: string; quote?: string | null }
      if (localId === null && q.text != null && !state.messages.some((m) => m.id === q.id))
        return reduce({ ...state, messages: [...state.messages, { ...blank(q.id, 'user', q.parentId), content: q.text, quote: q.quote ?? null }] }, e, localId) as CodeLive
      return reduce(state, e, localId) as CodeLive
    }
    case 'attached': {
      // The question, as the page shows it: the one it sent (its own id until the server's comes), or the server's.
      const question = state.messages.find((m) => m.id === e.id) ?? (localId ? state.messages.find((m) => m.id === localId) : undefined)
      return question ? { ...state, messages: state.messages.map((m) => (m === question ? { ...m, files: e.files } : m)) } : state
    }
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
      return {
        ...state,
        jobs: withJob(state.jobs, e.job, (j) => j, { id: e.job, command: e.command, running: true, status: e.status, output: '', failed: false, background: e.background }),
      }
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

