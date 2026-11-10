import { api } from './http'
import { blank, reduce, type LiveState } from '@/pages/chat/live'
import type { Attachment, ChatEvent, ChatTool, MeasuredContext, Message } from '@/pages/chat/types'
import type { Piece } from './bridge'
import type { CodeGoal } from './tasks-api'

/** What code-arena web says of itself: the folder, the model, the mode, the session open now. */
export interface CodeState {
  name: string
  version: string
  /** The licence and where this version's source is, for the about box (LICENSING.md, section 7(b)). */
  license: string
  source: string
  /** The IDE's log, code-arena.log beside the program (else in its data folder); null: none could be written. */
  log: string | null
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
  /**
   * What fills the next request, by the web chat's kinds in characters, and its biggest messages (by their ids here): the
   * chat's gauge and the status bar's Context scale it to contextUsed. Measured while no turn runs.
   */
  window?: MeasuredContext | null
  /** The room the session keeps in the window for the answer, in tokens: a request holds its prompt and its answer together. */
  reserve?: number
  /** When the session compacts itself, in percent of the window (0: only when a request would not fit), and what the recent part may keep whole. */
  compactAt: number
  compactTarget: number
  /** How the model is given its tools now (direct, code, both); whether that is the person's own choice; Arena's word for the model (null: none). */
  toolMode?: 'direct' | 'code' | 'both'
  toolModeSet?: boolean
  toolModeArena?: 'direct' | 'code' | 'both' | null
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
  /** Messages sent while an answer was written: the answer reads them at its next step; one sent as it ends runs after it. */
  queued: QueuedCode[]
  /** The sub-agents and background tasks at work now (the Tasks panel reads them while any is). */
  tasksRunning?: number
  /** The goal the agent works toward (goal mode), as it stands; null: none. */
  goal?: CodeGoal | null
  /** The goals waiting their turn after it. */
  nextGoals?: CodeGoal[]
  /** How many loops run in the session. */
  loops?: number
  /** The Arena assistant the session works as; null: none. */
  assistant?: { id: string; name: string; icon: string; color: string } | null
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
  /** The project in Arena the session counts to (/project); null: none. */
  workProject?: { id: string; name: string } | null
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
  /** In words: "Arena: 14 tools", "CodeIntel: not connected, tried again at 10:41:07 (…)". */
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
  /** Its last turns (as many as asked for: sessionTurns), and how many turns came before them. */
  messages: Message[]
  earlier?: number
  diffs: Record<string, FileDiff>
  busy: boolean
}

/**
 * Arena's chat events, and what Code Arena adds: a diff with an edit's result, "always" and Laya's look with a question, the
 * conversation afresh after a compaction (each message in its place, the last one it covers marked with its summary), and the
 * commands run with no time limit (their start, their output as it comes, their end).
 */
/** A message waiting for the answer being written: q1, q2… name it for Edit and Cancel. */
export interface QueuedCode {
  id: string
  text: string
  quote?: string | null
  attachments?: Attachment[] | null
}

export type CodeEvent =
  | Exclude<ChatEvent, { type: 'tool_result' } | { type: 'approval' } | { type: 'took_in' }>
  /** A queued message the answer took in at its next step, with what went with it (path, or path:first-last) and its files. */
  | (Extract<ChatEvent, { type: 'took_in' }> & { files?: string[] | null; attachments?: Attachment[] | null })
  | (Extract<ChatEvent, { type: 'tool_result' }> & { diff?: FileDiff })
  | (Extract<ChatEvent, { type: 'approval' }> & { always?: string; risk?: string })
  | { type: 'reset'; messages: Message[]; diffs: Record<string, FileDiff> }
  | { type: 'job'; job: number; command: string; running: true; status: string; background?: boolean }
  | { type: 'job_output'; job: number; text: string }
  | { type: 'job_end'; job: number; running: false; status: string; exitCode: number | null; stopped: boolean }
  /** What went with the question (@files, the lines chosen in the editor): path, or path:first-last. */
  | { type: 'attached'; id: string; files: string[] }
  /** A VM's sign-in the VM tools ask for (a dialog: vm-login.ts), and its end. */
  | { type: 'vm_login'; id: string; vm: string; hypervisor: string; user: string | null; refused: boolean; canSave: boolean; keyring: string }
  | { type: 'vm_login_done'; id: string }

/** The projects in Arena the person may add to (null with why when Arena cannot say), and the repository's. */
export interface CodeProjects {
  projects: { id: string; name: string }[] | null
  suggested: string | null
  problem: string | null
}

export const projectsQuery = {
  queryKey: ['code', 'projects'] as const,
  queryFn: ({ signal }: { signal: AbortSignal }) => api<CodeProjects>('/api/project', { signal }),
}

/** The session (and the folder's next ones) in a project: its id, or "none". */
export const chooseProject = (project: string) => api<{ said: string }>('/api/project', { body: { project } })

export const stateQuery = {
  queryKey: ['code', 'state'] as const,
  queryFn: ({ signal }: { signal: AbortSignal }) => api<CodeState>('/api/state', { signal }),
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
  queryFn: ({ signal }: { signal: AbortSignal }) => api<{ online: boolean; denied?: boolean; project: string; notebook?: string | null; memories: CodeMemory[] }>('/api/memory', { signal }),
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

/**
 * A long session is read from its end: its last turns (this many at first, more with each "Show earlier"). The number is
 * kept beside the query, not in its key: what reads the session again after a turn reads as many.
 */
export const sessionTurns = { first: 30, step: 30 }
let turnsWanted = sessionTurns.first

/** Reads more of the session's earlier turns, the next time it is read; `reset`: a session opened afresh reads its last ones. */
export function widenSession(reset = false) {
  turnsWanted = reset ? sessionTurns.first : turnsWanted + sessionTurns.step
}

export const sessionQuery = {
  queryKey: ['code', 'session'] as const,
  queryFn: ({ signal }: { signal: AbortSignal }) => api<CodeSession>(`/api/session?turns=${turnsWanted}`, { signal }),
}

/** What this folder sent before, newest first: the terminal's and the page's, without the terminal's own commands. */
export const historyQuery = {
  queryKey: ['code', 'history'] as const,
  queryFn: ({ signal }: { signal: AbortSignal }) => api<{ text: string }[]>('/api/history', { signal }),
}

/** compactAt: 0 compacts only when a request would not fit; null follows the Arena account's again. */
export const changeSettings = (body: {
  mode?: Mode
  model?: string
  thinking?: string | null
  compactAt?: number | null
  compactTarget?: number
  tools?: string[]
  /** How the model is given its tools, kept in config.json; null follows Arena's word for the model again. */
  toolMode?: 'direct' | 'code' | 'both' | null
}) =>
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
/** Skip thinking: the answer running stops thinking and answers, and the rest of its turn does not think. */
export const skipThinking = () => api('/api/skip-thinking', { body: {} })
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
/**
 * What Rewind to here would do from a question (m3): how many of the session's messages go, whether its code can go back
 * too (this run kept that turn's checkpoint), and then the files that would change (removed: a turn made it) and those too
 * big to keep, which stay as they are.
 */
export interface RewindPreview {
  message: string
  messages: number
  code: boolean
  files: { path: string; removed: boolean }[]
  lost: string[]
}
export const rewindPreviewQuery = (message: string) => ({
  queryKey: ['code', 'rewind', message] as const,
  queryFn: ({ signal }: { signal: AbortSignal }) => api<RewindPreview>(`/api/rewind?message=${encodeURIComponent(message)}`, { signal }),
})
/** Back to before a question, as /rewind: the conversation and the code, only the conversation, or only the code; what it says. */
export const rewindSession = (body: { message: string; chat: boolean; code: boolean }) => api<{ said: string }>('/api/rewind', { body })
/** A message for the answer being written, read at its next step (at once when none is), with the lines chosen in the editor and the part of an answer it quotes. */
export const queueMessage = (text: string, context?: Piece[], quote?: string, attachments?: Attachment[]) =>
  api<{ queued: number; id: string }>('/api/queue', { body: { text, ...(context?.length && { context }), ...(quote && { quote }), ...(attachments?.length && { attachments }) } })
// A body, so it goes as JSON: code-arena web refuses any other call that changes something.
export const clearQueue = () => api('/api/queue', { method: 'DELETE', body: {} })
/** A waiting message's words changed; 404 once it went. */
export const editQueued = (id: string, text: string) => api(`/api/queue/${id}`, { method: 'PATCH', body: { text } })
/** One waiting message taken out of line. */
export const cancelQueued = (id: string) => api(`/api/queue/${id}`, { method: 'DELETE', body: {} })
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
    case 'took_in': {
      // Read at the answer's next step: the person's message there, with what went with it.
      const next = reduce(state, e, localId) as CodeLive
      return { ...next, messages: next.messages.map((m) => (m.id === e.id ? { ...m, files: e.files ?? null, attachments: e.attachments ?? [] } : m)) }
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
    case 'vm_login':
    case 'vm_login_done':
      return state
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

