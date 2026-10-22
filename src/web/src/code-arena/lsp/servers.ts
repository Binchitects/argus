import type * as Monaco from 'monaco-editor'
import { useSyncExternalStore } from 'react'
import { toast } from '@/components/ui/toaster'
import { api } from '../http'
import type { MonacoModule, Reveal } from '../editor-state'
import { formatError, formatModel, formatterOf, formattersOf } from '../format'
import { languageServerList, lspSocketUrl, type LanguageServerLog } from '../ide-api'
import { quickPick } from '../quick-pick'
import { applyEditId, executeCommandId, LanguageClient, type ClientHost, type ServerInfo } from './client'
import type { Command, Diagnostic, WorkspaceEdit } from './convert'

/**
 * The page's language servers: for each language of the folder's files open in the editor, the first server code-arena
 * found for it that the settings leave on, started as the first such file opens (in a trusted folder only: in another,
 * a quiet note in the status bar, never a prompt), and kept until the page goes. One that ends by itself is started
 * again (three times in three minutes at most). While a server runs, the editor's own services for its languages
 * (Monaco's TypeScript, JSON, CSS and HTML workers) are off, so nothing is answered twice. One store for the page: the
 * status bar, the Output panel and Problems read it.
 */

export type ServerState = 'starting' | 'ready' | 'failed' | 'restricted' | 'taken' | 'missing' | 'off'

/** A language server as the page shows it: its state, and why. */
export interface ServerStatus {
  id: string
  name: string
  state: ServerState
  /** Why it failed or stopped, why it is missing, what it is doing. */
  detail?: string
  /** How to get it, when it is missing. */
  install?: string
  /** The work it reports (indexing…). */
  progress?: string
  path?: string | null
  version?: string | null
}

/** A diagnostic of a language server's in a file of the folder, for Problems. */
export interface ServerProblem {
  server: string
  path: string
  line: number
  column: number
  severity: 'error' | 'warning' | 'info'
  code: string | null
  message: string
}

/** What the workbench gives the store: the folder's trust, the settings, and the editor. */
export interface ServersHost {
  trusted: boolean
  /** The settings, flat (for a language when given). */
  settings: (language?: string) => Record<string, unknown>
  /** The folder's full path and its name. */
  folder: string
  project: string
  open: (path: string, at?: Reveal) => void
  /** Opens files in tabs behind the one shown (an edit changes them). */
  keep: (paths: string[]) => Promise<void>
}

/** The lines of a server's output the page keeps. */
const outputLines = 1000
/** How often a server that ends by itself is started again, and in how long. */
const restarts = { most: 3, within: 180_000 }

/** Whether a server is on: "codeArena.languageServers.enabled", then its own ("codeArena.languageServers.ID.enabled"). */
export function serverEnabled(settings: Record<string, unknown>, id: string): boolean {
  if (settings['codeArena.languageServers.enabled'] === false) return false
  const own = settings[`codeArena.languageServers.${id}.enabled`] ?? (settings[`codeArena.languageServers.${id}`] as { enabled?: unknown } | undefined)?.enabled
  return own !== false
}

/** What a server's state says, in words. */
export function stateText(s: ServerStatus): string {
  switch (s.state) {
    case 'starting':
      return 'starting'
    case 'ready':
      return s.progress ? `ready, ${s.progress}` : 'ready'
    case 'failed':
      return 'failed'
    case 'restricted':
      return 'off until the folder is trusted'
    case 'taken':
      return 'in use by another page'
    case 'missing':
      return 'not installed'
    case 'off':
      return 'off'
  }
}

const time = () => new Date().toLocaleTimeString([], { hour12: false })

class LanguageServers {
  private m: MonacoModule | null = null
  private host: ServersHost | null = null
  private list: ServerInfo[] | null = null
  private listing: Promise<void> | null = null
  private readonly clients = new Map<string, LanguageClient>()
  private readonly states = new Map<string, ServerStatus>()
  private readonly started = new Map<string, number[]>()
  private readonly output = new Map<string, string[]>()
  private queued = false
  private version = 0
  private readonly listeners = new Set<() => void>()
  /** The server whose output the Output panel shows (the first when none was chosen). */
  shown: string | null = null

  readonly subscribe = (f: () => void) => {
    this.listeners.add(f)
    return () => {
      this.listeners.delete(f)
    }
  }
  readonly snapshot = () => this.version

  private notifying = false
  private changed() {
    this.version++
    // Once for what one message of a server's changed (its diagnostics, a line of its log).
    if (this.notifying) return
    this.notifying = true
    queueMicrotask(() => {
      this.notifying = false
      for (const l of this.listeners) l()
    })
  }

  /** Monaco loaded (the first file opened): the store follows its models from now on. Once. */
  attach(m: MonacoModule) {
    if (this.m) return
    this.m = m
    const { monaco } = m
    const later = () => this.later()
    monaco.editor.onDidCreateModel?.(later)
    monaco.editor.onDidChangeModelLanguage?.(later)
    // Go to Definition (and the like) into another file of the folder: it opens in its tab.
    monaco.editor.registerEditorOpener?.({
      openCodeEditor: (source, resource, at) => {
        if (source.getModel()?.uri.toString() === resource.toString()) return false
        if (resource.scheme === 'outside') {
          toast.info(`${resource.path} is outside the folder: the editor opens the folder's files only.`)
          return true
        }
        const path = this.pathIn(resource)
        if (path === null || !this.host) return false
        this.host.open(path, reveal(at))
        return true
      },
    })
    monaco.editor.registerCommand(executeCommandId, (_: unknown, id: string, command: Command) => void this.clients.get(id)?.execute(command))
    monaco.editor.registerCommand(applyEditId, (_: unknown, id: string, edit: WorkspaceEdit, then?: Command) =>
      void (async () => {
        const client = this.clients.get(id)
        if (!client) return
        const done = await client.apply(edit)
        if (!done.applied) toast.error(done.failureReason ?? 'The edit could not be made.')
        else if (then) await client.execute(then)
      })(),
    )
    this.later()
  }

  /** The workbench's trust, settings and editor, as they are now: servers start, stop or are told the settings. */
  configure(host: ServersHost) {
    const before = this.host
    this.host = host
    if (before && before.settings !== host.settings) for (const c of this.clients.values()) c.configurationChanged()
    if (!before || before.trusted !== host.trusted || before.settings !== host.settings) {
      // Trusted now: a server refused before (or untrusted) may start; the list may say more.
      if (before && !before.trusted && host.trusted) this.list = null
      this.later()
    }
  }

  private later() {
    if (this.queued) return
    this.queued = true
    queueMicrotask(() => {
      this.queued = false
      void this.consider()
    })
  }

  /** A file URI's path in the folder: file:///src/a.ts, or the folder's full path (a link in a server's markdown). */
  private pathIn(uri: Monaco.Uri): string | null {
    if (uri.scheme !== 'file') return null
    const folder = this.host?.folder
    if (folder && uri.path.startsWith(`${folder}/`)) return uri.path.slice(folder.length + 1)
    return uri.path.length > 1 ? uri.path.slice(1) : null
  }

  /** The languages of the folder's files the editor holds. */
  private languages(): Set<string> {
    const m = this.m
    const out = new Set<string>()
    for (const model of m?.monaco.editor.getModels?.() ?? []) {
      if (model.uri.scheme === 'file' && m!.pathOf(model.uri) !== null) out.add(model.getLanguageId())
    }
    return out
  }

  private async fetchList() {
    this.listing ??= (async () => {
      try {
        this.list = await languageServerList()
      } catch {
        // code-arena cannot say (an older one, or it is gone): no servers.
        this.list = []
      } finally {
        this.listing = null
      }
    })()
    await this.listing
  }

  /** The server for a language: the first code-arena lists for it that the settings leave on. */
  private pick(language: string): { server: ServerInfo | null; listed: ServerInfo[] } {
    const settings = this.host?.settings(language) ?? {}
    const listed = (this.list ?? []).filter((s) => s.languages.includes(language))
    const on = listed.filter((s) => serverEnabled(settings, s.id))
    return { server: on.find((s) => s.found) ?? null, listed: on.length ? on : listed }
  }

  /** Starts what the open files want, stops what the settings turned off, and says why for the others. */
  private async consider() {
    const host = this.host
    if (!this.m || !host) return
    const languages = this.languages()
    if (languages.size === 0 && this.clients.size === 0) return
    if (this.list === null) {
      await this.fetchList()
      if (this.list === null) return
    }
    // Turned off in the settings: stopped.
    for (const [id, client] of [...this.clients]) {
      if (!client.info.languages.some((l) => serverEnabled(host.settings(l), id))) {
        this.stopClient(id)
        this.set({ ...this.base(client.info), state: 'off', detail: 'Turned off in the settings.' })
      }
    }
    for (const language of languages) {
      const { server, listed } = this.pick(language)
      if (!server) {
        const first = listed[0]
        if (first && !this.clients.has(first.id)) {
          const off = !serverEnabled(host.settings(language), first.id)
          const state: ServerState = off ? 'off' : 'missing'
          if (this.states.get(first.id)?.state !== state) this.set({ ...this.base(first), state, detail: off ? 'Turned off in the settings.' : (first.reason ?? undefined), install: first.install ?? undefined })
        }
        continue
      }
      if (this.clients.has(server.id)) continue
      const now = this.states.get(server.id)?.state
      if (!host.trusted) {
        if (now !== 'restricted') this.set({ ...this.base(server), state: 'restricted', detail: "Language servers run the folder's code: they start once you trust the folder." })
        continue
      }
      // Failed, or taken by another page: started again by Restart only.
      if (now === 'failed' || now === 'taken') continue
      this.start(server)
    }
  }

  private base(s: ServerInfo): ServerStatus {
    return { id: s.id, name: s.name, state: 'off', path: s.path, version: s.version }
  }

  private set(status: ServerStatus) {
    this.states.set(status.id, status)
    this.changed()
  }

  private start(info: ServerInfo) {
    const m = this.m!
    const client = new LanguageClient(info, this.clientHost(info, m), lspSocketUrl(info.id))
    this.clients.set(info.id, client)
    this.started.set(info.id, [...(this.started.get(info.id) ?? []), Date.now()].filter((t) => Date.now() - t < restarts.within))
    this.log(info.id, `Starting ${info.name}${info.path ? ` (${info.path})` : ''}.`)
    this.set({ ...this.base(info), state: 'starting' })
    client.onEnd = (c) => void this.ended(c)
    void client.start().then(() => {
      if (this.clients.get(info.id) !== client || client.state !== 'ready') return
      this.overlap(info, true)
      this.set({ ...this.base(info), state: 'ready' })
    })
  }

  private clientHost(info: ServerInfo, m: MonacoModule): ClientHost {
    return {
      monaco: m.monaco,
      pathOf: (uri) => m.pathOf(uri),
      settings: () => this.host?.settings() ?? {},
      folderName: () => this.host?.project ?? '',
      keep: (paths) => this.host?.keep(paths) ?? Promise.resolve(),
      open: (path, at) => this.host?.open(path, at && { line: at.startLineNumber, column: at.startColumn }),
      log: (line) => this.log(info.id, line),
      changed: () => {
        const s = this.states.get(info.id)
        const client = this.clients.get(info.id)
        if (s && client) {
          const work = [...client.progress.values()][0]
          const progress = work ? [work.title, work.message, work.percentage !== undefined ? `${Math.round(work.percentage)}%` : ''].filter(Boolean).join(' ') : undefined
          if (progress !== s.progress) this.states.set(info.id, { ...s, progress })
        }
        this.changed()
      },
      formatWithProject: (model, path, range) => this.formatWithProject(model, path, range),
      notify: (kind, text) => void (kind === 'error' ? toast.error(text) : toast.warning(text)),
      ask: async (message, actions) => (await quickPick({ title: info.name, placeholder: message, items: actions.map((a) => ({ id: a, label: a })) }))?.id,
    }
  }

  /**
   * Formats with the project's formatter when it has one for the file (format.ts's): the project's comes first, unless
   * "editor.defaultFormatter" names another (a server's id, a language extension's): then the server formats.
   */
  private async formatWithProject(model: Monaco.editor.ITextModel, path: string, range?: Monaco.IRange): Promise<boolean> {
    const language = model.getLanguageId()
    const list = await formattersOf(path, language)
    const id = formatterOf(this.host?.settings(language)['editor.defaultFormatter']) ?? list?.default
    if (!list || !id || !list.formatters.some((f) => f.id === id)) return false
    try {
      await formatModel(model, path, { range })
    } catch (e) {
      toast.error(formatError(e))
    }
    return true
  }

  /** A server ended by itself (the page did not stop it): why, and started again when it had been running. */
  private async ended(client: LanguageClient) {
    const id = client.info.id
    if (this.clients.get(id) !== client) return
    this.clients.delete(id)
    this.overlap(client.info, false)
    const e = client.ending
    this.log(id, e?.reason ?? 'It ended.')
    if (e?.takenOver) {
      this.set({ ...this.base(client.info), state: 'taken', detail: 'Another page of this folder took this language server over: Restart takes it back.' })
      return
    }
    if (e?.stopped || !this.host?.trusted) {
      this.set({ ...this.base(client.info), state: this.host?.trusted ? 'off' : 'restricted', detail: e?.reason })
      if (this.host?.trusted) this.later()
      return
    }
    const wasReady = this.states.get(id)?.state === 'ready'
    if (wasReady && (this.started.get(id)?.length ?? 0) < restarts.most) {
      this.start(client.info)
      return
    }
    // Refused before it ran (its socket never opened), or it keeps ending: the log says why.
    let why = e?.reason ?? 'It ended.'
    try {
      const log = await api<LanguageServerLog>(`/api/lsp/log?server=${encodeURIComponent(id)}`)
      if (log.refused) why = log.refused
      else if (!wasReady && log.lines.length) why = `${why} ${log.lines.slice(-3).join(' ')}`
    } catch {
      // As it is.
    }
    this.set({ ...this.base(client.info), state: 'failed', detail: why })
  }

  private stopClient(id: string) {
    const client = this.clients.get(id)
    if (!client) return
    this.clients.delete(id)
    client.stop()
    this.overlap(client.info, false)
  }

  /** The editor's own services for a server's languages: aside for the files it keeps while it runs, back after. */
  private overlap(info: ServerInfo, running: boolean) {
    const client = this.clients.get(info.id)
    for (const language of info.languages) this.m?.stepAside(language, running && client ? (model) => client.serves(model) : null)
  }

  private log(id: string, line: string) {
    const lines = this.output.get(id) ?? []
    lines.push(...line.split('\n').map((l, i) => (i === 0 ? `[${time()}] ${l}` : l)))
    if (lines.length > outputLines) lines.splice(0, lines.length - outputLines)
    this.output.set(id, lines)
    this.changed()
  }

  /** The status of the server for a language (null: code-arena knows none for it). */
  statusFor(language: string | undefined): ServerStatus | null {
    if (!language || !this.list) return null
    const { server, listed } = this.pick(language)
    const id = server?.id ?? listed[0]?.id
    return id ? (this.states.get(id) ?? null) : null
  }

  /** Every server the page started or said something of, for the Output panel. */
  channels(): ServerStatus[] {
    return [...this.states.values()].filter((s) => this.output.has(s.id) || s.state !== 'missing')
  }

  /** Shows a server's output in the Output panel. */
  show(id: string) {
    this.shown = id
    this.changed()
  }

  /** What the page kept of a server's output (its messages, what was started and why it ended). */
  outputOf(id: string): string[] {
    return this.output.get(id) ?? []
  }

  /** The folder's files' diagnostics of every server running, for Problems. */
  problems(): ServerProblem[] {
    const out: ServerProblem[] = []
    const severity = (s: number | undefined) => (s === 2 ? 'warning' : s === 3 || s === 4 ? 'info' : 'error') as ServerProblem['severity']
    for (const client of this.clients.values()) {
      for (const [uri, list] of client.diagnostics) {
        const path = this.m ? this.pathIn(this.m.monaco.Uri.parse(uri)) : null
        if (!path) continue
        for (const d of list as Diagnostic[]) {
          out.push({ server: client.info.name, path, line: d.range.start.line + 1, column: d.range.start.character + 1, severity: severity(d.severity), code: d.code === undefined ? null : String(d.code), message: d.message })
        }
      }
    }
    return out
  }

  /** Starts a server again (the one for this language, or this id): the list read again first, the trust asked for when the folder has none. */
  async restart(which: { language?: string; id?: string }) {
    this.list = null
    await this.fetchList()
    const id = which.id ?? (which.language ? this.pick(which.language).server?.id : undefined)
    const info = (this.list as ServerInfo[] | null)?.find((s) => s.id === id)
    if (!info) {
      toast.info(which.language ? `There is no language server for ${which.language} here.` : 'There is no such language server.')
      return
    }
    if (!info.found) {
      toast.error(`${info.name}: ${info.reason ?? 'not installed'}${info.install ? ` ${info.install}` : ''}`)
      return
    }
    if (!this.host?.trusted) {
      this.set({ ...this.base(info), state: 'restricted', detail: "Language servers run the folder's code: they start once you trust the folder." })
      toast.info('Trust the folder first (Manage Workspace Trust): its language servers run its code.')
      return
    }
    this.stopClient(info.id)
    this.started.delete(info.id)
    this.states.delete(info.id)
    this.start(info)
  }

  /** Files changed on disk (by the agent, a command): every server running is told. */
  filesChanged(changes: { path: string; type: 1 | 2 | 3 }[]) {
    for (const c of this.clients.values()) c.filesChanged(changes)
  }

  /** A file of the folder was saved: the servers that keep it are told. */
  saved(model: Monaco.editor.ITextModel) {
    for (const c of this.clients.values()) c.saved(model)
  }

  /** Stops every server and forgets what the page knew of them (the list read again as a file opens): a test's start. */
  reset() {
    for (const id of [...this.clients.keys()]) this.stopClient(id)
    this.list = null
    this.states.clear()
    this.started.clear()
    this.output.clear()
    this.shown = null
    this.changed()
  }

  /** The servers running now, by id. */
  running(): ServerInfo[] {
    return [...this.clients.values()].map((c) => c.info)
  }

  /** Whether a server is started (or starting) now. */
  isRunning(id: string) {
    return this.clients.has(id)
  }
}

/** Where to put the cursor for a place Monaco names: a range on one line is selected, any other the cursor goes to its start. */
function reveal(at: Monaco.IRange | Monaco.IPosition | undefined): Reveal | undefined {
  if (!at) return undefined
  if ('startLineNumber' in at) {
    return at.startLineNumber === at.endLineNumber ? { line: at.startLineNumber, column: at.startColumn, length: at.endColumn - at.startColumn } : { line: at.startLineNumber, column: at.startColumn }
  }
  return { line: at.lineNumber, column: at.column }
}

export const languageServers = new LanguageServers()

/** The store's version: a part that shows the servers reads them again as it changes. */
export function useLanguageServers() {
  return useSyncExternalStore(languageServers.subscribe, languageServers.snapshot)
}
