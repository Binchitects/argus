import { useSyncExternalStore } from 'react'
import { saveWorkspaceState } from '../ide-api'
import { DapClient } from './dap'
import { debugSessions, debugSocketUrl, stopDebug, type DebugStart } from './run-api'

/**
 * The debugger's state for the page: the debug sessions (each one adapter, spoken to over its socket), the breakpoints
 * and watch expressions (kept for this folder on this computer), and what the session shown is stopped at. One store for
 * the page; the Run and Debug view, the editor's gutter, the toolbar and the Debug Console read it.
 */

export interface SourceBreakpoint {
  line: number
  condition?: string
  hitCondition?: string
  logMessage?: string
  enabled: boolean
  /** Run to Cursor's, for this run only: taken away at the next stop or end, the breakpoint it stood in for (was) back. */
  temporary?: boolean
  was?: SourceBreakpoint
}

/** What the adapter made of a breakpoint: verified (bound to code), perhaps on another line, or why not. */
export interface BreakpointResult {
  verified: boolean
  line?: number
  message?: string
}

export interface StackFrame {
  id: number
  name: string
  line: number
  column: number
  source?: { name?: string; path?: string; sourceReference?: number }
  presentationHint?: string
  /** Its file in the folder, when it is one. */
  path: string | null
}

export interface Scope {
  name: string
  variablesReference: number
  expensive?: boolean
  presentationHint?: string
}

export interface Variable {
  name: string
  value: string
  type?: string
  variablesReference: number
  evaluateName?: string
}

export type DebugState = 'starting' | 'running' | 'stopped' | 'ended'

export interface OutputLine {
  /** The adapter's (stdout, stderr, console, important…), or the Debug Console's own: repl.in, repl.out, repl.error. */
  category: string
  text: string
}

/** The most output kept for a session, in characters. */
const maxOutput = 1_000_000

let version = 0
const listeners = new Set<() => void>()
let outputVersion = 0
const outputListeners = new Set<() => void>()
let outputQueued = false
/** Output came: the console redraws once for all that came in the next moment. */
function outputChanged() {
  if (outputQueued) return
  outputQueued = true
  setTimeout(() => {
    outputQueued = false
    outputVersion++
    for (const l of outputListeners) l()
  }, 50)
}
const changed = () => {
  version++
  for (const l of listeners) l()
}
const subscribe = (f: () => void) => {
  listeners.add(f)
  return () => {
    listeners.delete(f)
  }
}

/** One debug session: its adapter, its state, its threads and frames while stopped, its output. */
export class Session {
  readonly start: DebugStart
  readonly client: DapClient
  /** Its adapter let go (it ended, or failed to start). */
  closed = false
  state: DebugState = 'starting'
  error: string | null = null
  capabilities: Record<string, unknown> = {}
  threads: { id: number; name: string }[] = []
  stopped: { threadId?: number; reason: string; description?: string; text?: string } | null = null
  exception: { id?: string; description?: string } | null = null
  frames: StackFrame[] = []
  frameId: number | null = null
  output: OutputLine[] = []
  outputSize = 0
  exitCode: number | null = null
  /** The breakpoints as the adapter took them, by file. */
  results: Record<string, BreakpointResult[]> = {}
  /** Its program runs in a terminal the adapter asked for (runInTerminal): the Debug Console does not take its place. */
  ranInTerminal = false
  /** The compound it was started with, when that compound stops all its sessions together (stopAll). */
  group: string | null = null
  /** Counts what a value was set to while stopped: the views that show values ask again. */
  revision = 0
  private readonly cache = new Map<number, Promise<Variable[]>>()
  // The stops so far: a step answered after the stop it led to does not mean running (some adapters stop first).
  private stops = 0

  constructor(start: DebugStart) {
    this.start = start
    this.client = new DapClient(debugSocketUrl(start.id))
    this.client.on('*', (body, m) => this.event(m.event!, body))
  }

  get id() {
    return this.start.id
  }

  get name() {
    return this.start.name ?? this.start.adapter
  }

  /** A file of the folder as the adapter names it (absolute, in its system's form). */
  toAdapter(path: string) {
    return this.start.pathStyle === 'win' ? `${this.start.root}\\${path.replaceAll('/', '\\')}` : `${this.start.root}/${path}`
  }

  /** A path the adapter named, as the folder's file it is; null outside the folder. */
  fromAdapter(path: string | undefined): string | null {
    if (!path) return null
    const win = this.start.pathStyle === 'win'
    const norm = (p: string) => (win ? p.replaceAll('\\', '/').toLowerCase() : p)
    const p = win ? path.replaceAll('\\', '/') : path
    for (const root of [this.start.root, this.start.rootReal]) {
      const r = (win ? root.replaceAll('\\', '/') : root).replace(/\/$/, '')
      if (norm(p).startsWith(`${norm(r)}/`)) return p.slice(r.length + 1)
    }
    return null
  }

  private event(event: string, body: unknown) {
    const b = (body ?? {}) as Record<string, unknown>
    switch (event) {
      case 'codeArena.snapshot': {
        this.state = (b.state as DebugState) ?? this.state
        this.error = (b.error as string) ?? null
        this.capabilities = (b.capabilities as Record<string, unknown>) ?? this.capabilities
        this.threads = ((b.threads as { threadId?: number; reason?: string }[] | undefined) ?? []).map((t) => ({ id: t.threadId ?? 0, name: `Thread ${t.threadId}` }))
        if (b.stopped) void this.onStopped(b.stopped as Record<string, unknown>)
        break
      }
      case 'codeArena.state':
        this.state = (b.state as DebugState) ?? this.state
        if (b.error) this.error = b.error as string
        break
      case 'codeArena.closed':
        this.state = 'ended'
        break
      case 'stopped':
        void this.onStopped(b)
        break
      case 'continued':
        if (b.allThreadsContinued !== false || b.threadId === this.stopped?.threadId) this.resumed()
        break
      case 'thread':
        if (b.reason === 'exited') this.threads = this.threads.filter((t) => t.id !== b.threadId)
        else if (!this.threads.some((t) => t.id === b.threadId)) this.threads = [...this.threads, { id: b.threadId as number, name: `Thread ${b.threadId}` }]
        break
      case 'output':
        // Kept to 1 MB of text, oldest first out; the console alone redraws for it, a moment later (a program printing in
        // a loop sends thousands a second).
        if (b.category !== 'telemetry' && typeof b.output === 'string') this.write((b.category as string) ?? 'console', b.output)
        return
      case 'exited':
        this.exitCode = (b.exitCode as number) ?? null
        break
      case 'terminated':
        this.state = 'ended'
        this.resumed()
        // Ended by itself (the program exited): the adapter is let go, as VS Code disconnects it; its output stays to read.
        void debug.end(this)
        break
      case 'breakpoint':
        // A breakpoint verified later (a library loaded): the gutter shows it as the adapter has it now.
        void debug.syncResults(this)
        break
      case 'capabilities':
        this.capabilities = { ...this.capabilities, ...(b.capabilities as Record<string, unknown>) }
        break
      default:
        return
    }
    changed()
  }

  /** A line of output: the adapter's, or the Debug Console's (an expression and its value, in turn with the program's). */
  write(category: string, text: string) {
    this.output.push({ category, text })
    this.outputSize += text.length
    while (this.outputSize > maxOutput && this.output.length > 1) this.outputSize -= this.output.shift()!.text.length
    outputChanged()
  }

  private resumed() {
    if (this.state !== 'ended') this.state = 'running'
    this.stopped = null
    this.exception = null
    this.frames = []
    this.frameId = null
    this.cache.clear()
  }

  private async onStopped(b: Record<string, unknown>) {
    this.stops++
    this.state = 'stopped'
    this.stopped = { threadId: b.threadId as number | undefined, reason: (b.reason as string) ?? 'pause', description: b.description as string | undefined, text: b.text as string | undefined }
    this.cache.clear()
    changed()
    try {
      const threads = await this.client.request<{ threads: { id: number; name: string }[] }>('threads')
      this.threads = threads.threads ?? []
      const threadId = this.stopped?.threadId ?? this.threads[0]?.id
      if (threadId === undefined) return
      await this.loadFrames(threadId)
      if (this.stopped?.reason === 'exception' && this.capabilities.supportsExceptionInfoRequest) {
        const info = await this.client.request<{ exceptionId?: string; description?: string }>('exceptionInfo', { threadId }).catch(() => null)
        this.exception = info ? { id: info.exceptionId, description: info.description } : null
      }
    } catch {
      // Resumed or ended while it was asked: nothing to show.
    }
    changed()
    debug.afterStop()
    const top = this.frames.find((f) => f.path) ?? this.frames[0]
    if (top) debug.onStop?.(this, top)
  }

  /** The frames of a thread, the first 20 (more as the Call Stack asks). */
  async loadFrames(threadId: number, startFrame = 0) {
    const r = await this.client.request<{ stackFrames: Omit<StackFrame, 'path'>[]; totalFrames?: number }>('stackTrace', { threadId, startFrame, levels: 20 })
    const frames = (r.stackFrames ?? []).map((f) => ({ ...f, path: this.fromAdapter(f.source?.path) }))
    this.frames = startFrame === 0 ? frames : [...this.frames, ...frames]
    if (startFrame === 0) this.frameId = frames[0]?.id ?? null
    changed()
  }

  selectFrame(id: number) {
    this.frameId = id
    changed()
  }

  scopes(frameId: number) {
    return this.client.request<{ scopes: Scope[] }>('scopes', { frameId }).then((r) => r.scopes ?? [])
  }

  /** A variable's children (or a scope's variables), asked once per stop. */
  variables(reference: number): Promise<Variable[]> {
    let p = this.cache.get(reference)
    if (!p) {
      p = this.client.request<{ variables: Variable[] }>('variables', { variablesReference: reference }).then((r) => r.variables ?? [])
      this.cache.set(reference, p)
    }
    return p
  }

  async setVariable(container: number, name: string, value: string) {
    const r = await this.client.request<{ value: string }>('setVariable', { variablesReference: container, name, value })
    this.cache.clear()
    this.revision++
    changed()
    return r.value
  }

  /** An expression's value in the frame chosen: for a watch, the console (repl) or a hover. */
  evaluate(expression: string, context: 'watch' | 'repl' | 'hover' | 'clipboard') {
    return this.client.request<{ result: string; type?: string; variablesReference: number }>('evaluate', { expression, frameId: this.frameId ?? undefined, context })
  }

  private thread() {
    return this.stopped?.threadId ?? this.threads[0]?.id ?? 1
  }

  /** Continue or a step: running once the adapter answers, unless it stopped again first. */
  private resume(command: 'continue' | 'next' | 'stepIn' | 'stepOut') {
    const stops = this.stops
    return this.client
      .request(command, { threadId: this.thread() })
      .then(
        () => {
          if (this.stops === stops) this.resumed()
        },
        () => undefined,
      )
      .finally(changed)
  }
  continue() {
    return this.resume('continue')
  }
  next() {
    return this.resume('next')
  }
  stepIn() {
    return this.resume('stepIn')
  }
  stepOut() {
    return this.resume('stepOut')
  }
  pause() {
    return this.client.request('pause', { threadId: this.thread() }).catch(() => undefined)
  }
  /** Runs to a line of a file (a temporary breakpoint, as VS Code's Run to Cursor without its own request). */
  async goto(path: string, line: number) {
    if (this.capabilities.supportsGotoTargetsRequest) {
      const t = await this.client.request<{ targets: { id: number }[] }>('gotoTargets', { source: { path: this.toAdapter(path) }, line })
      if (t.targets[0]) await this.client.request('goto', { threadId: this.thread(), targetId: t.targets[0].id })
    }
  }

  /** The handshake, as VS Code does it: initialize; launch (not waited for: gdb answers it after configurationDone); on initialized, the breakpoints, then configurationDone. */
  async begin() {
    await this.client.ready()
    const initialized = new Promise<void>((resolve) => {
      const off = this.client.on('initialized', () => {
        off()
        resolve()
      })
    })
    this.client.onRequest = (command, args) => debug.reverse(this, command, args)
    this.capabilities =
      (await this.client.request<Record<string, unknown>>('initialize', {
        clientID: 'code-arena',
        clientName: 'Code Arena',
        adapterID: this.start.adapterId,
        pathFormat: 'path',
        linesStartAt1: true,
        columnsStartAt1: true,
        supportsVariableType: true,
        supportsVariablePaging: false,
        supportsRunInTerminalRequest: true,
        supportsStartDebuggingRequest: false,
        supportsInvalidatedEvent: true,
        locale: 'en',
      })) ?? {}
    changed()
    const request = this.start.configuration.request === 'attach' ? 'attach' : 'launch'
    const launched = this.client.request(request, this.start.configuration)
    // An adapter that fails the launch never sends initialized.
    await Promise.race([initialized, launched.then(() => initialized)])
    for (const path of Object.keys(debug.breakpoints)) await debug.sendBreakpoints(this, path)
    const filters = debug.exceptionFilters ?? ((this.capabilities.exceptionBreakpointFilters as { filter: string; default?: boolean }[] | undefined) ?? []).filter((f) => f.default).map((f) => f.filter)
    if ((this.capabilities.exceptionBreakpointFilters as unknown[] | undefined)?.length) await this.client.request('setExceptionBreakpoints', { filters }).catch(() => undefined)
    if (this.capabilities.supportsConfigurationDoneRequest) await this.client.request('configurationDone')
    await launched
    if (this.state === 'starting') this.state = 'running'
    changed()
  }
}

/** The debugger's store for the page. */
export const debug = {
  sessions: [] as Session[],
  activeId: null as string | null,
  /** The folder, as the server names it (for sessions attached again after a reload). */
  root: '',
  /** Whether values show at the lines' ends while stopped ("debug.inlineValues", set by the workbench). */
  inlineValues: () => true as boolean,
  breakpoints: {} as Record<string, SourceBreakpoint[]>,
  watch: [] as string[],
  /** What was typed in the Debug Console, the last first (↑ goes back through it). */
  replHistory: [] as string[],
  /** The exception filters chosen; null: the adapter's defaults. */
  exceptionFilters: null as string[] | null,
  /** The editor shows where a session stopped (set by the editor). */
  onStop: null as ((s: Session, frame: StackFrame) => void) | null,
  /** The adapter asks to run the program in a terminal (set by the workbench). */
  onRunInTerminal: null as ((s: Session, args: { args: string[]; cwd?: string; env?: Record<string, string | null>; title?: string }) => Promise<number | undefined>) | null,

  get active(): Session | null {
    return this.sessions.find((s) => s.id === this.activeId) ?? this.sessions.at(-1) ?? null
  },

  /** The breakpoints and watches kept for this folder. */
  load(state: Record<string, unknown>) {
    const bps = state.breakpoints
    if (bps && typeof bps === 'object' && !Array.isArray(bps)) this.breakpoints = withoutTemporary(bps as Record<string, SourceBreakpoint[]>)
    if (Array.isArray(state.watch)) this.watch = state.watch.filter((x): x is string => typeof x === 'string')
    if (Array.isArray(state.exceptionFilters)) this.exceptionFilters = state.exceptionFilters.filter((x): x is string => typeof x === 'string')
    changed()
  },

  /** A session started on the server: spoken to, its handshake made. */
  async open(start: DebugStart, group: string | null = null): Promise<Session> {
    const s = new Session(start)
    s.group = group
    for (const x of this.sessions) if (x.state === 'ended') x.client.close()
    this.sessions = [...this.sessions.filter((x) => x.state !== 'ended'), s]
    this.activeId = s.id
    changed()
    try {
      await s.begin()
    } catch (e) {
      s.error = e instanceof Error ? e.message : String(e)
      // A launch that failed leaves no adapter waiting for a Stop.
      void this.end(s)
      changed()
      throw e
    }
    return s
  },

  /**
   * A session over (it ended by itself, or failed to start): code-arena stops its adapter and the socket closes; it stays
   * listed, ended, with its output, until the next start.
   */
  async end(s: Session) {
    if (s.closed) return
    s.closed = true
    s.state = 'ended'
    this.afterStop()
    try {
      await stopDebug(s.id)
    } catch {
      // Gone already.
    }
    s.client.close()
    changed()
  },

  /** Sessions still running when the page opens (it was reloaded): attached again, their state from the server's snapshot. */
  async reattach() {
    const live = (await debugSessions().catch(() => [])).filter((x) => x.state !== 'ended')
    for (const info of live) {
      if (this.sessions.some((s) => s.id === info.id)) continue
      const s = new Session({ id: info.id, name: info.name, adapter: info.adapter, adapterId: info.type, configuration: {}, notes: [], root: this.root, rootReal: this.root, pathStyle: this.root.includes('\\') ? 'win' : 'posix' })
      s.state = info.state
      this.sessions = [...this.sessions, s]
    }
    changed()
  },

  async stop(target?: Session | null) {
    const s = target === undefined ? debug.active : target
    if (!s) return
    s.closed = true
    try {
      await stopDebug(s.id)
    } catch {
      // Gone already.
    }
    s.state = 'ended'
    this.afterStop()
    s.client.close()
    this.sessions = this.sessions.filter((x) => x !== s)
    if (this.activeId === s.id) this.activeId = this.sessions.at(-1)?.id ?? null
    changed()
    // A compound with stopAll: the others it started stop with it.
    if (s.group) await Promise.all(this.sessions.filter((x) => x.group === s.group).map((x) => this.stop(x)))
  },

  select(id: string) {
    this.activeId = id
    changed()
  },

  /** A file's breakpoints, sent to a session (those enabled), and the adapter's answer kept for the gutter. */
  async sendBreakpoints(s: Session, path: string) {
    const bps = (this.breakpoints[path] ?? []).filter((b) => b.enabled)
    try {
      const r = await s.client.request<{ breakpoints: BreakpointResult[] }>('setBreakpoints', {
        source: { path: s.toAdapter(path), name: path.slice(path.lastIndexOf('/') + 1) },
        breakpoints: bps.map((b) => ({ line: b.line, condition: b.condition || undefined, hitCondition: b.hitCondition || undefined, logMessage: b.logMessage || undefined })),
        lines: bps.map((b) => b.line),
      })
      s.results = { ...s.results, [path]: r.breakpoints ?? [] }
    } catch {
      s.results = { ...s.results, [path]: bps.map(() => ({ verified: false, message: 'The adapter did not take it.' })) }
    }
    changed()
  },

  async syncResults(s: Session) {
    for (const path of Object.keys(this.breakpoints)) await this.sendBreakpoints(s, path)
  },

  /** The breakpoints of a file changed: kept, and sent to every session. */
  setBreakpoints(path: string, list: SourceBreakpoint[]) {
    const next = { ...this.breakpoints }
    // One per line (lines deleted bring theirs together): the first, with what the others had that it lacks.
    const byLine = new Map<number, SourceBreakpoint>()
    for (const b of [...list].sort((a, b) => a.line - b.line)) {
      const there = byLine.get(b.line)
      byLine.set(b.line, there ? { ...b, ...there, condition: there.condition || b.condition, hitCondition: there.hitCondition || b.hitCondition, logMessage: there.logMessage || b.logMessage } : b)
    }
    if (byLine.size === 0) delete next[path]
    else next[path] = [...byLine.values()]
    this.breakpoints = next
    changed()
    keep()
    for (const s of this.sessions) if (s.state !== 'ended' && s.state !== 'starting') void this.sendBreakpoints(s, path)
  },

  /** F9: a breakpoint on the line, or none. */
  toggle(path: string, line: number) {
    const now = this.breakpoints[path] ?? []
    this.setBreakpoints(path, now.some((b) => b.line === line) ? now.filter((b) => b.line !== line) : [...now, { line, enabled: true }])
  },

  /** A breakpoint's condition, hit count or log message (empty: none). */
  edit(path: string, line: number, change: Partial<SourceBreakpoint>) {
    const now = this.breakpoints[path] ?? []
    const there = now.find((b) => b.line === line)
    this.setBreakpoints(path, there ? now.map((b) => (b.line === line ? { ...b, ...change } : b)) : [...now, { line, enabled: true, ...change }])
  },

  removeAll() {
    for (const path of Object.keys(this.breakpoints)) this.setBreakpoints(path, [])
  },

  /** Breakpoints follow the file's lines as it is edited (the editor says where each moved). */
  moveBreakpoints(path: string, lines: Map<number, number>) {
    const now = this.breakpoints[path]
    if (!now) return
    let moved = false
    const next = now.map((b) => {
      const to = lines.get(b.line)
      if (to !== undefined && to !== b.line) {
        moved = true
        return { ...b, line: to }
      }
      return b
    })
    if (moved) this.setBreakpoints(path, next)
  },

  /**
   * Run to Cursor: a breakpoint there for this run only, then continue. It is taken away at the next stop or when the
   * session ends, as VS Code's; one the person had there, turned off, comes back as it was (condition and all).
   */
  async runTo(path: string, line: number) {
    const s = this.active
    if (!s || s.state !== 'stopped') return
    const now = this.breakpoints[path] ?? []
    const there = now.find((b) => b.line === line)
    if (!there?.enabled) {
      this.setBreakpoints(path, [...now.filter((b) => b !== there), { line, enabled: true, temporary: true, was: there }])
      await this.sendBreakpoints(s, path)
    }
    await s.continue()
  },

  /** A session stopped or ended: Run to Cursor's breakpoints go, those they stood in for come back. */
  afterStop() {
    for (const [path, list] of Object.entries(this.breakpoints)) {
      if (list.some((b) => b.temporary)) this.setBreakpoints(path, list.flatMap((b) => (b.temporary ? (b.was ? [{ ...b.was, line: b.line }] : []) : [b])))
    }
  },

  setWatch(list: string[]) {
    this.watch = list
    changed()
    keep()
  },

  setExceptionFilters(filters: string[]) {
    this.exceptionFilters = filters
    changed()
    keep()
    for (const s of this.sessions) if (s.state !== 'ended') void s.client.request('setExceptionBreakpoints', { filters }).catch(() => undefined)
  },

  /** What the adapter asks the page: a program run in a terminal (runInTerminal); nothing else. */
  async reverse(s: Session, command: string, args: unknown) {
    if (command === 'runInTerminal' && this.onRunInTerminal) {
      const pid = await this.onRunInTerminal(s, args as { args: string[]; cwd?: string; env?: Record<string, string | null>; title?: string })
      return pid === undefined ? { success: false, message: 'The terminal did not open.' } : { success: true, body: { processId: pid } }
    }
    return { success: false, message: `Code Arena does not do "${command}" yet.` }
  },
}

let keeping: ReturnType<typeof setTimeout> | undefined
/** Breakpoints, watches and exception filters kept for this folder, a moment after the last change. */
function keep() {
  clearTimeout(keeping)
  keeping = setTimeout(() => void saveWorkspaceState({ breakpoints: withoutTemporary(debug.breakpoints), watch: debug.watch, exceptionFilters: debug.exceptionFilters }).catch(() => undefined), 300)
}

/** The breakpoints as the person set them: Run to Cursor's left out, those they stood in for kept. */
function withoutTemporary(all: Record<string, SourceBreakpoint[]>) {
  const out: Record<string, SourceBreakpoint[]> = {}
  for (const [path, list] of Object.entries(all)) {
    const kept = list.flatMap((b) => (b.temporary ? (b.was ? [{ ...b.was, line: b.line }] : []) : [b]))
    if (kept.length > 0) out[path] = kept
  }
  return out
}

/** The active session's output, read by the Debug Console: it redraws as output comes, nothing else does. */
export function useDebugOutput() {
  useSyncExternalStore(
    (f) => {
      outputListeners.add(f)
      return () => {
        outputListeners.delete(f)
      }
    },
    () => outputVersion,
  )
  return debug.active?.output ?? []
}

/** The debugger's store, read by a component: it renders again as it changes. */
export function useDebug() {
  useSyncExternalStore(subscribe, () => version)
  return debug
}

export const onDebugChange = subscribe
