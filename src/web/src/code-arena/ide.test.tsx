import { act, render, screen, waitFor, within } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { afterEach, beforeEach, describe, expect, it, onTestFinished, vi } from 'vitest'
import codeArenaDoc from '@docs/code-arena.md?raw'
import { makeQueryClient, Providers } from '@/app/providers'
import { manualDocs, parseDoc } from '@/help/manual'
import { blank } from '@/pages/chat/live'
import type { ChatConfig, Message } from '@/pages/chat/types'
import { fakeApi, type Handler } from '@/test/utils'
import type { CodeState } from './api'
import { App } from './app'
import { debug } from './debug/session'
import { asks } from './bridge'
import { rankFiles } from './editor-state'
import { parts, regions, tasks } from './help-text'
import type { Change, Entry, Preferences, TerminalInfo } from './ide-api'

// Monaco, xterm.js and the terminal's socket, as fakes that remember what the page did with them.
const fakes = vi.hoisted(() => {
  let alt = 0
  class Uri {
    scheme: string
    path: string
    query: string
    constructor(scheme: string, path: string, query = '') {
      this.scheme = scheme
      this.path = path
      this.query = query
    }
    toString() {
      return `${this.scheme}://${this.path}${this.query ? `?${this.query}` : ''}`
    }
  }
  class Model {
    value: string
    language: string
    uri: Uri | undefined
    alt = ++alt
    disposed = false
    listeners: (() => void)[] = []
    constructor(value: string, language: string, uri?: Uri) {
      // As Monaco's: one model at an address.
      if (uri && fakes.models.some((m) => !m.disposed && m.uri?.toString() === uri.toString())) throw new Error(`Cannot add model because it already exists: ${uri}`)
      this.value = value
      this.language = language
      this.uri = uri
      fakes.models.push(this)
    }
    /** As Monaco's: a model disposed of throws when read. */
    live() {
      if (this.disposed) throw new Error('Model is disposed!')
    }
    isDisposed() {
      return this.disposed
    }
    getValue() {
      this.live()
      return this.value
    }
    /** What typing in the editor does: new text, a new version. */
    setValue(value: string) {
      this.value = value
      this.alt = ++alt
      for (const f of this.listeners) f()
    }
    getAlternativeVersionId() {
      this.live()
      return this.alt
    }
    onDidChangeContent(f: () => void) {
      this.listeners.push(f)
      return { dispose() {} }
    }
    getValueLength() {
      return this.value.length
    }
    getLineCount() {
      return this.value.split('\n').length
    }
    getValueInRange(r: { startLineNumber: number; startColumn: number; endLineNumber: number; endColumn: number }) {
      const lines = this.value.split('\n').slice(r.startLineNumber - 1, r.endLineNumber)
      if (lines.length === 0) return ''
      lines[lines.length - 1] = lines[lines.length - 1]!.slice(0, r.endColumn - 1)
      lines[0] = lines[0]!.slice(r.startColumn - 1)
      return lines.join('\n')
    }
    getLineMaxColumn(line: number) {
      return (this.value.split('\n')[line - 1] ?? '').length + 1
    }
    getWordAtPosition() {
      return null
    }
    /** The offset of a line and column (from 1), as Monaco's. */
    getOffsetAt(p: { lineNumber: number; column: number }) {
      const lines = this.value.split('\n')
      return lines.slice(0, p.lineNumber - 1).reduce((n, l) => n + l.length + 1, 0) + p.column - 1
    }
    getFullModelRange() {
      const lines = this.value.split('\n')
      return { startLineNumber: 1, startColumn: 1, endLineNumber: lines.length, endColumn: lines.at(-1)!.length + 1 }
    }
    /** As Monaco's: the edits applied at their ranges, the last first. */
    pushEditOperations(_: unknown, ops: { range: { startLineNumber: number; startColumn: number; endLineNumber: number; endColumn: number }; text: string }[]) {
      let value = this.value
      const at = (line: number, column: number) => this.getOffsetAt({ lineNumber: line, column })
      for (const op of [...ops].sort((a, b) => at(b.range.startLineNumber, b.range.startColumn) - at(a.range.startLineNumber, a.range.startColumn))) {
        value = value.slice(0, at(op.range.startLineNumber, op.range.startColumn)) + op.text + value.slice(at(op.range.endLineNumber, op.range.endColumn))
      }
      this.setValue(value)
      return null
    }
    pushStackElement() {}
    options: Record<string, unknown> = {}
    updateOptions(o: Record<string, unknown>) {
      Object.assign(this.options, o)
    }
    detectIndentation() {}
    getLanguageId() {
      return this.language
    }
    getLineContent(line: number) {
      return this.value.split('\n')[line - 1] ?? ''
    }
    getEOL() {
      return '\n'
    }
    getVersionId() {
      return this.alt
    }
    getOptions() {
      return { tabSize: 4, insertSpaces: true, ...this.options }
    }
    getPositionAt(offset: number) {
      const before = this.value.slice(0, offset).split('\n')
      return { lineNumber: before.length, column: before.at(-1)!.length + 1 }
    }
    dispose() {
      this.disposed = true
    }
  }
  class Editor {
    model: Model | null = null
    selection: { startLineNumber: number; startColumn: number; endLineNumber: number; endColumn: number } | null = null
    revealed: number | null = null
    options: Record<string, unknown>
    cursor: ((e: { position: { lineNumber: number; column: number } }) => void)[] = []
    constructor(_host: HTMLElement, options: Record<string, unknown>) {
      this.options = { ...options }
      fakes.editors.push(this)
    }
    setModel(m: Model | null) {
      this.model = m
    }
    getModel() {
      return this.model
    }
    saveViewState() {
      return null
    }
    restoreViewState() {}
    updateOptions(o: Record<string, unknown>) {
      Object.assign(this.options, o)
    }
    focus() {}
    setSelection(s: Editor['selection']) {
      this.selection = s
    }
    revealLineInCenter(line: number) {
      this.revealed = line
    }
    getPosition() {
      return this.selection ? { lineNumber: this.selection.startLineNumber, column: this.selection.startColumn } : { lineNumber: 1, column: 1 }
    }
    onDidChangeCursorPosition(f: Editor['cursor'][number]) {
      this.cursor.push(f)
      return { dispose() {} }
    }
    onDidBlurEditorText() {
      return { dispose() {} }
    }
    onDidChangeModel() {
      return { dispose() {} }
    }
    onDidChangeModelContent() {
      return { dispose() {} }
    }
    onMouseDown() {
      return { dispose() {} }
    }
    /** The decorations the page drew (breakpoints, the line stopped at). */
    decorations: { range: { startLineNumber: number }; options: { glyphMarginClassName?: string; className?: string } }[] = []
    createDecorationsCollection() {
      return {
        set: (list: Editor['decorations']) => {
          this.decorations = list
        },
        clear: () => {
          this.decorations = []
        },
        getRanges: () => this.decorations.map((d) => d.range),
      }
    }
    contextKeys: Record<string, unknown> = {}
    createContextKey(key: string, value: unknown) {
      this.contextKeys[key] = value
      return { set: (v: unknown) => (this.contextKeys[key] = v), get: () => this.contextKeys[key], reset() {} }
    }
    /** The editor's own actions (its right-click menu, F1): by id, run as the person would. */
    actions = new Map<string, { label: string; keybindings?: number[]; run: () => void }>()
    addAction(a: { id: string; label: string; keybindings?: number[]; run: () => void }) {
      this.actions.set(a.id, a)
      return { dispose() {} }
    }
    getSelection() {
      return this.selection
    }
    /** As Monaco's: an action added to an editor is listed under that editor's id first. */
    getSupportedActions() {
      return [...this.actions.entries()].map(([id, a]) => ({ id: `vs.editor.ICodeEditor:1:${id}`, label: a.label }))
    }
    getAction(id: string) {
      const a = this.actions.get(id)
      return a ? { run: async () => a.run() } : null
    }
    dispose() {}
  }
  class DiffEditor {
    model: { original: Model; modified: Model } | null = null
    options: Record<string, unknown>
    constructor(_host: HTMLElement, options: Record<string, unknown> = {}) {
      this.options = { ...options }
      fakes.diffEditors.push(this)
    }
    updateOptions(o: Record<string, unknown>) {
      Object.assign(this.options, o)
    }
    side = { createContextKey: () => ({ set() {}, get() {}, reset() {} }), focus() {}, hasTextFocus: () => false, getSelection: () => ({ startLineNumber: 1, startColumn: 1, endLineNumber: 1, endColumn: 1 }) }
    /** What Monaco would work out: one change, a line each way. */
    lineChanges = [{ originalStartLineNumber: 1, originalEndLineNumber: 1, modifiedStartLineNumber: 1, modifiedEndLineNumber: 1 }]
    getLineChanges() {
      return this.lineChanges
    }
    onDidUpdateDiff() {
      return { dispose() {} }
    }
    went: string[] = []
    goToDiff(to: string) {
      this.went.push(to)
    }
    getOriginalEditor() {
      return this.side
    }
    getModifiedEditor() {
      return this.side
    }
    setModel(m: DiffEditor['model']) {
      this.model = m
    }
    getModel() {
      return this.model
    }
    dispose() {}
  }
  class Terminal {
    cols = 80
    rows = 24
    options: Record<string, unknown>
    written: (string | Uint8Array)[] = []
    data: ((d: string) => void)[] = []
    resizes: ((s: { cols: number; rows: number }) => void)[] = []
    disposed = false
    constructor(options: Record<string, unknown>) {
      this.options = { ...options }
      fakes.terminals.push(this)
    }
    loadAddon(addon: { activate: (t: Terminal) => void }) {
      addon.activate(this)
    }
    /** What it shows, line by line (from 0), for its links. */
    lines: string[] = []
    links: { provideLinks: (y: number, done: (links: { text: string; activate: () => void }[] | undefined) => void) => void }[] = []
    registerLinkProvider(p: Terminal['links'][number]) {
      this.links.push(p)
      return { dispose() {} }
    }
    buffer = { active: { getLine: (y: number) => ({ translateToString: () => this.lines[y] ?? '' }) } }
    open() {}
    write(d: string | Uint8Array) {
      this.written.push(d)
    }
    onData(f: (d: string) => void) {
      this.data.push(f)
      return { dispose() {} }
    }
    onBinary() {
      return { dispose() {} }
    }
    onResize(f: (s: { cols: number; rows: number }) => void) {
      this.resizes.push(f)
      return { dispose() {} }
    }
    resize(cols: number, rows: number) {
      this.cols = cols
      this.rows = rows
      for (const f of this.resizes) f({ cols, rows })
    }
    focus() {}
    dispose() {
      this.disposed = true
    }
    /** Keys typed into it. */
    type(d: string) {
      for (const f of this.data) f(d)
    }
  }
  class Socket {
    static CONNECTING = 0
    static OPEN = 1
    static CLOSED = 3
    readyState = 0
    binaryType = 'blob'
    sent: unknown[] = []
    onopen: (() => void) | null = null
    onmessage: ((e: { data: unknown }) => void) | null = null
    onclose: (() => void) | null = null
    url: string
    constructor(url: string) {
      this.url = url
      fakes.sockets.push(this)
    }
    send(d: unknown) {
      this.sent.push(d)
    }
    close() {
      this.readyState = 3
    }
    open() {
      this.readyState = 1
      this.onopen?.()
    }
    receive(data: unknown) {
      this.onmessage?.({ data })
    }
  }
  return {
    Uri,
    Model,
    Editor,
    DiffEditor,
    Terminal,
    Socket,
    models: [] as Model[],
    editors: [] as Editor[],
    diffEditors: [] as DiffEditor[],
    terminals: [] as Terminal[],
    sockets: [] as Socket[],
    themes: [] as string[],
    /** The keybinding rules the page gave Monaco, the last given last. */
    keybindingRules: [] as { keybinding: number; command: string; when?: string }[][],
    jsonOptions: [] as { schemas?: { fileMatch: string[] }[] }[],
    formatting: new Map<string, { provideDocumentFormattingEdits: (...args: unknown[]) => unknown }>(),
    /** What the editor marks (a language's problems), by the file's address. */
    markers: new Map<string, { severity: number; startLineNumber: number; startColumn: number; endLineNumber: number; message: string }[]>(),
    commands: new Map<string, (...args: unknown[]) => void>(),
    /** The markers the page set, by owner and the file's address. */
    marked: new Map<string, { startLineNumber: number; message: string }[]>(),
    codeActions: [] as { provideCodeActions: (...args: unknown[]) => unknown }[],
    inline: [] as { provideInlineCompletions: (...args: unknown[]) => Promise<{ items: { insertText: string }[] }> }[],
    /** The themes the page gave Monaco, by name. */
    definedThemes: new Map<string, { base: string; rules: { token: string; foreground?: string; fontStyle?: string }[]; colors: Record<string, string> }>(),
    /** The languages the page added, and their configurations. */
    languages: [] as { id: string; extensions?: string[]; aliases?: string[] }[],
    languageConfigs: new Map<string, { comments?: { lineComment?: string }; brackets?: [string, string][] }>(),
    /** The completion providers the page gave Monaco (snippets), by language. */
    completions: [] as { language: string; provider: { provideCompletionItems: (...args: unknown[]) => Promise<{ suggestions: { label: { label: string }; insertText: string; insertTextRules: number }[] }> } }[],
  }
})

vi.mock('monaco-editor', () => ({
  editor: {
    create: (host: HTMLElement, options: Record<string, unknown>) => new fakes.Editor(host, options),
    createDiffEditor: (host: HTMLElement, options: Record<string, unknown>) => new fakes.DiffEditor(host, options),
    addKeybindingRules: (rules: { keybinding: number; command: string; when?: string }[]) => {
      fakes.keybindingRules.push(rules)
      return { dispose() {} }
    },
    createModel: (value: string, language: string, uri?: InstanceType<typeof fakes.Uri>) => new fakes.Model(value, language, uri),
    getModel: (uri: InstanceType<typeof fakes.Uri>) => fakes.models.find((m) => !m.disposed && m.uri?.toString() === uri.toString()) ?? null,
    setModelLanguage: (m: { language: string }, language: string) => {
      m.language = language
    },
    setTheme: (t: string) => fakes.themes.push(t),
    getModelMarkers: ({ resource }: { resource: InstanceType<typeof fakes.Uri> }) => fakes.markers.get(resource.toString()) ?? [],
    setModelMarkers: (model: InstanceType<typeof fakes.Model>, owner: string, markers: { startLineNumber: number; message: string }[]) =>
      fakes.marked.set(`${owner}:${model.uri?.toString()}`, markers),
    registerCommand: (id: string, run: (...args: unknown[]) => void) => {
      fakes.commands.set(id, run)
      return { dispose() {} }
    },
    defineTheme: (name: string, data: Parameters<typeof fakes.definedThemes.set>[1]) => fakes.definedThemes.set(name, data),
    remeasureFonts: () => undefined,
    TrackedRangeStickiness: { NeverGrowsWhenTypingAtEdges: 1 },
    OverviewRulerLane: { Left: 1 },
    MouseTargetType: { GUTTER_GLYPH_MARGIN: 2 },
  },
  Range: class {
    startLineNumber: number
    startColumn: number
    endLineNumber: number
    endColumn: number
    constructor(startLineNumber: number, startColumn: number, endLineNumber: number, endColumn: number) {
      this.startLineNumber = startLineNumber
      this.startColumn = startColumn
      this.endLineNumber = endLineNumber
      this.endColumn = endColumn
    }
  },
  KeyMod: { CtrlCmd: 2048, Shift: 1024, Alt: 512, WinCtrl: 256, chord: (a: number, b: number) => (a | (b << 16)) >>> 0 },
  KeyCode: { F1: 59, F2: 60, F3: 61, F5: 63, KeyB: 32, KeyK: 41, KeyL: 42, KeyP: 46, KeyS: 49, Backquote: 92, Delete: 20, Enter: 3 },
  json: { jsonDefaults: { setDiagnosticsOptions: (o: { schemas?: { fileMatch: string[] }[] }) => fakes.jsonOptions.push(o) } },
  MarkerSeverity: { Hint: 1, Info: 2, Warning: 4, Error: 8 },
  languages: {
    registerInlineCompletionsProvider: (_: unknown, provider: (typeof fakes.inline)[number]) => {
      fakes.inline.push(provider)
      return { dispose() {} }
    },
    registerDocumentFormattingEditProvider: (language: string, provider: { provideDocumentFormattingEdits: (...args: unknown[]) => unknown }) => {
      fakes.formatting.set(language, provider)
      return { dispose() {} }
    },
    registerDocumentRangeFormattingEditProvider: () => ({ dispose() {} }),
    registerHoverProvider: () => ({ dispose() {} }),
    registerCodeActionProvider: (_: string, provider: { provideCodeActions: (...args: unknown[]) => unknown }) => {
      fakes.codeActions.push(provider)
      return { dispose() {} }
    },
    getLanguages: () => [
      { id: 'typescript', extensions: ['.ts', '.tsx'], aliases: ['TypeScript'] },
      { id: 'markdown', extensions: ['.md'], aliases: ['Markdown'] },
      { id: 'dockerfile', extensions: ['.dockerfile'], filenames: ['Dockerfile'], aliases: ['Dockerfile'] },
      ...fakes.languages,
    ],
    register: (l: (typeof fakes.languages)[number]) => fakes.languages.push(l),
    setLanguageConfiguration: (id: string, c: Parameters<typeof fakes.languageConfigs.set>[1]) => {
      fakes.languageConfigs.set(id, c)
      return { dispose: () => fakes.languageConfigs.delete(id) }
    },
    registerCompletionItemProvider: (language: string, provider: (typeof fakes.completions)[number]['provider']) => {
      const entry = { language, provider }
      fakes.completions.push(entry)
      return { dispose: () => fakes.completions.splice(fakes.completions.indexOf(entry), 1) }
    },
    CompletionItemKind: { Snippet: 27 },
    CompletionItemInsertTextRule: { InsertAsSnippet: 4 },
  },
  Uri: {
    file: (path: string) => new fakes.Uri('file', path),
    from: (c: { scheme: string; path: string; query?: string }) => new fakes.Uri(c.scheme, c.path, c.query),
  },
  typescript: {
    typescriptDefaults: { setDiagnosticsOptions: () => undefined, getCompilerOptions: () => ({}), setCompilerOptions: () => undefined },
    javascriptDefaults: { setDiagnosticsOptions: () => undefined, getCompilerOptions: () => ({}), setCompilerOptions: () => undefined },
    JsxEmit: { ReactJSX: 4 },
    ScriptTarget: { ESNext: 99 },
    ModuleKind: { ESNext: 99 },
    ModuleResolutionKind: { NodeJs: 2 },
  },
}))
vi.mock('@xterm/xterm', () => ({ Terminal: fakes.Terminal }))
vi.mock('@xterm/addon-fit', () => ({
  FitAddon: class {
    term: InstanceType<typeof fakes.Terminal> | null = null
    activate(t: InstanceType<typeof fakes.Terminal>) {
      this.term = t
    }
    /** The panel holds 120 by 30. */
    fit() {
      if (this.term && (this.term.cols !== 120 || this.term.rows !== 30)) this.term.resize(120, 30)
    }
    dispose() {}
  },
}))

beforeEach(() => {
  vi.stubGlobal('WebSocket', fakes.Socket)
  // The debugger's store is the page's: each test starts with none of its sessions or breakpoints.
  debug.sessions = []
  debug.activeId = null
  debug.breakpoints = {}
  debug.watch = []
  debug.replHistory = []
  for (const list of [fakes.models, fakes.editors, fakes.diffEditors, fakes.terminals, fakes.sockets, fakes.themes, fakes.keybindingRules]) list.length = 0
  fakes.definedThemes.clear()
})
afterEach(() => {
  vi.unstubAllGlobals()
})

const state: CodeState = {
  name: 'Code Arena', version: '5.2.0', license: 'AGPL-3.0-only', source: 'https://github.com/Binchitects/argus', manual: 'https://llm.test/help/code-arena', folder: '/home/ada/shop', project: 'shop', branch: 'feature/cart', model: 'model-a', context: 32768, contextUsed: 4200, compactAt: 80, compactTarget: 25, servers: [], jobs: [], queued: [], thinking: null, mode: 'auto-edit',
  modes: [
    { name: 'ask', description: 'edits and commands ask first' },
    { name: 'auto-edit', description: 'file edits run without asking; commands ask' },
    { name: 'plan', description: 'read-only: no edits, no commands' },
    { name: 'yolo', description: 'nothing asks: edits and commands run at once' },
  ],
  session: 's1', busy: false, arenaTools: false, tools: { local: 10, servers: [] },
}

const config: ChatConfig = {
  model: 'model-a',
  models: [{ name: 'model-a', context: 32768, maxOutput: 8192, vision: false, tools: true, thinking: false, loaded: true, prices: { input: null, cachedInput: null, output: null } }],
  auto: null, presets: [], defaultThinking: null, argus: false, tools: [], gitlabUrl: null, maxUploadBytes: 0, imageTypes: [],
}

/** code-arena web with a small folder: src/app.ts (changed by the agent), src/lib/, README.md; the page's preferences as an earlier run left them, and the person's settings.json and keybindings.json. */
function backend(extra: Record<string, Handler> = {}, preferences: Preferences = {}, user: Record<string, string> = {}) {
  const disk: Record<string, { text: string; version: string }> = {
    'src/app.ts': { text: 'const total = 1\nconsole.log(total)\n', version: 'v1' },
    'README.md': { text: '# Shop\n', version: 'r1' },
  }
  const dirs: Record<string, Entry[]> = {
    '': [
      { name: 'src', path: 'src', kind: 'dir' },
      { name: 'README.md', path: 'README.md', kind: 'file', size: 7 },
    ],
    src: [
      { name: 'lib', path: 'src/lib', kind: 'dir' },
      { name: 'app.ts', path: 'src/app.ts', kind: 'file', size: 34 },
    ],
    'src/lib': [],
  }
  const now = { changes: [{ path: 'src/app.ts', created: false, deleted: false, added: 1, removed: 1 }] as Change[], terminals: [] as TerminalInfo[], next: 1, preferences, user: { ...user }, userVersions: {} as Record<string, number> }
  const calls = fakeApi(null, {
    'GET /api/state': () => ({ json: state }),
    'GET /api/chat/config': () => ({ json: config }),
    'GET /api/sessions': () => ({ json: [] }),
    'GET /api/session': () => ({ json: { id: 's1', messages: [], diffs: {}, busy: false } }),
    'GET /api/files': (_, __, url) => {
      const path = url.searchParams.get('path') ?? ''
      return dirs[path] ? { json: { path: path || '.', entries: dirs[path] } } : { status: 404, json: { status: 'not_found', error: `There is no folder ${path}.` } }
    },
    'GET /api/files/all': () => ({ json: { files: ['README.md', 'src/app.ts', 'src/lib/cart.ts'], truncated: false } }),
    'GET /api/file': (_, __, url) => {
      const path = url.searchParams.get('path')!
      const f = disk[path] ?? (path === 'src/lib/cart.ts' ? { text: 'export {}\n', version: 'c1' } : undefined)
      return f ? { json: { path, size: f.text.length, version: f.version, text: f.text } } : { status: 404, json: { status: 'not_found', error: 'There is no such file.' } }
    },
    'POST /api/file': (body) => {
      const b = body as { path: string; text: string; version: string | null }
      if (b.version !== null && disk[b.path] && disk[b.path]!.version !== b.version) return { status: 409, json: { status: 'changed', error: `${b.path} changed on disk since it was opened.` } }
      disk[b.path] = { text: b.text, version: `${disk[b.path]?.version ?? 'n'}+` }
      return { json: { path: b.path, version: disk[b.path]!.version, size: b.text.length } }
    },
    'POST /api/files/new': (body) => {
      const b = body as { path: string; kind: 'file' | 'dir' }
      const folder = b.path.includes('/') ? b.path.slice(0, b.path.lastIndexOf('/')) : ''
      dirs[folder] = [...(dirs[folder] ?? []), { name: b.path.slice(b.path.lastIndexOf('/') + 1), path: b.path, kind: b.kind }]
      if (b.kind === 'file') disk[b.path] = { text: '', version: 'e1' }
      else dirs[b.path] = []
      return { json: { path: b.path, kind: b.kind } }
    },
    'POST /api/files/rename': (body) => {
      const b = body as { from: string; to: string }
      disk[b.to] = disk[b.from]!
      delete disk[b.from]
      dirs.src = dirs.src!.map((e) => (e.path === b.from ? { ...e, path: b.to, name: b.to.slice(b.to.lastIndexOf('/') + 1) } : e))
      return { json: { from: b.from, to: b.to } }
    },
    'POST /api/files/delete': (body) => {
      const b = body as { path: string }
      dirs[''] = dirs['']!.filter((e) => e.path !== b.path)
      delete disk[b.path]
      return { status: 204 }
    },
    'GET /api/changes': () => ({ json: now.changes }),
    'GET /api/changes/diff': (_, __, url) => ({ json: { path: url.searchParams.get('path'), original: 'const total = 0\n', modified: disk['src/app.ts']!.text, version: 'v1' } }),
    'POST /api/changes/accept': () => {
      now.changes = []
      return { json: [] }
    },
    'POST /api/changes/revert': () => {
      now.changes = []
      disk['src/app.ts'] = { text: 'const total = 0\n', version: 'v0' }
      return { json: [] }
    },
    'GET /api/search': () => ({
      json: { files: [{ path: 'src/app.ts', matches: [{ line: 2, column: 13, length: 5, preview: 'console.log(total)', start: 12 }] }], count: 1, truncated: false },
    }),
    'GET /api/terminals': () => ({ json: now.terminals }),
    'POST /api/terminals': (body) => {
      const b = body as { cols: number; rows: number }
      const t: TerminalInfo = { id: String(now.next++), title: 'bash', pid: 100 + now.next, cols: b.cols, rows: b.rows, exitCode: null }
      now.terminals = [...now.terminals, t]
      return { json: t }
    },
    'POST /api/terminals/close': (body) => {
      now.terminals = now.terminals.filter((t) => t.id !== (body as { id: string }).id)
      return { status: 204 }
    },
    'GET /api/workspace/state': () => ({ json: {} }),
    'POST /api/workspace/state': (body) => ({ json: body }),
    'GET /api/debug/sessions': () => ({ json: [] }),
    'GET /api/user-files': (_, __, url) => {
      const name = url.searchParams.get('name')!
      return { json: { name, text: now.user[name] ?? '', version: `u${now.userVersions[name] ?? 0}`, exists: name in now.user } }
    },
    'POST /api/user-files': (body) => {
      const b = body as { name: string; text: string; version: string | null }
      if (b.version !== null && b.version !== `u${now.userVersions[b.name] ?? 0}`) return { status: 409, json: { status: 'changed', error: `${b.name} changed since it was opened.` } }
      now.user[b.name] = b.text
      now.userVersions[b.name] = (now.userVersions[b.name] ?? 0) + 1
      return { json: { name: b.name, version: `u${now.userVersions[b.name]}` } }
    },
    'GET /api/preferences': () => ({ json: now.preferences }),
    'POST /api/preferences': (body) => {
      now.preferences = { ...now.preferences, ...(body as Preferences) }
      return { json: now.preferences }
    },
    'GET /api/extensions': () => ({ json: [] }),
    'GET /api/extensions/contributions': () => ({ json: { themes: [], iconThemes: [], snippets: [], languages: [], keybindings: [], configurationDefaults: [] } }),
    'GET /api/extensions/local': () => ({ json: [] }),
    'GET /api/extensions/recommended': () => ({ json: { file: false, recommendations: [], unwanted: [] } }),
    ...extra,
  })
  return { calls, disk, now }
}

function renderIde() {
  const client = makeQueryClient()
  client.setDefaultOptions({ queries: { ...client.getDefaultOptions().queries, retry: false } })
  const view = render(
    <Providers client={client}>
      <App />
    </Providers>,
  )
  return { ...view, client }
}

/**
 * Holds back the answers to the calls whose address and method match until let go: what the page does while one is on its way.
 * The server answers when let go, or with `answeredEarly` when asked: an answer from before what the page does meanwhile.
 */
function holdBack(match: (path: string, method: string) => boolean, { answeredEarly = false } = {}) {
  const answer = vi.mocked(fetch).getMockImplementation()!
  const waiting: (() => void)[] = []
  let held = 0
  let open = false
  vi.mocked(fetch).mockImplementation(async (input, init) => {
    const url = new URL(String(input), 'https://llm.test')
    if (open || !match(url.pathname + url.search, (init?.method ?? 'GET').toUpperCase())) return answer(input, init)
    held++
    const early = answeredEarly ? answer(input, init) : null
    await new Promise<void>((go) => waiting.push(go))
    return early ?? answer(input, init)
  })
  return {
    held: () => held,
    release: () => {
      open = true
      for (const go of waiting.splice(0)) go()
    },
  }
}

/** One turn of the agent that only answers: its end checks every open tab against the disk. */
const answerOnly = (at = 0) => ({
  events: [
    { type: 'question', id: `m${at}`, parentId: at ? `m${at - 1}` : null },
    { type: 'assistant', id: `m${at + 1}`, parentId: `m${at}`, model: 'model-a' },
    { type: 'content', text: 'Done.' },
  ],
})

const editor = () => fakes.editors[0]!
const tabs = () => screen.getByRole('tablist', { name: 'Open files' })

/** Opens src/app.ts from the explorer and waits for its model in the editor. */
async function openApp() {
  await userEvent.click(await screen.findByRole('treeitem', { name: 'src, the agent changed files in it' }))
  await userEvent.click(await screen.findByRole('treeitem', { name: 'app.ts, changed by the agent' }))
  await waitFor(() => expect(editor().model?.value).toBe('const total = 1\nconsole.log(total)\n'))
}

describe('Code Arena, the IDE', () => {
  it('lays out an editor: the activity bar, the explorer, the editor, the chat and the status bar', async () => {
    backend()
    renderIde()
    const bar = await screen.findByRole('navigation', { name: 'Activity bar' })
    // The agent's changes are counted on their button.
    expect(await within(bar).findByRole('button', { name: 'Agent changes (1)' })).toBeInTheDocument()
    expect(within(bar).getAllByRole('button').map((b) => b.getAttribute('aria-label'))).toEqual(['Explorer', 'Search', 'Source control', 'Run and Debug', 'Extensions', 'Agent changes (1)', 'Problems', 'Chat', 'Terminal', 'Theme', 'Help', 'About Code Arena'])
    expect(within(bar).getByRole('button', { name: 'Explorer' })).toHaveAttribute('aria-pressed', 'true')
    expect(await screen.findByRole('tree', { name: 'Files' })).toBeInTheDocument()
    expect(screen.getByRole('heading', { name: 'No file is open' })).toBeInTheDocument()
    expect(screen.getByRole('region', { name: 'Chat' })).toBeInTheDocument()
    expect(screen.getByRole('textbox', { name: 'Message' })).toBeInTheDocument()

    // Pressing the view shown hides the side bar, as in an editor; another view shows it again.
    await userEvent.click(within(bar).getByRole('button', { name: 'Explorer' }))
    expect(screen.queryByRole('tree', { name: 'Files' })).not.toBeInTheDocument()
    await userEvent.click(within(bar).getByRole('button', { name: 'Search' }))
    expect(screen.getByRole('textbox', { name: 'Search' })).toHaveFocus()
  })

  it('lists the folder, opens folders as they are expanded, marks the agent’s changes and opens a file in Monaco', async () => {
    const { calls } = backend()
    renderIde()
    const tree = await screen.findByRole('tree', { name: 'Files' })
    const src = await within(tree).findByRole('treeitem', { name: 'src, the agent changed files in it' })
    expect(src).toHaveAttribute('aria-expanded', 'false')
    expect(within(tree).getByRole('treeitem', { name: 'README.md' })).toBeInTheDocument()
    expect(calls.some((c) => c.path === '/api/files?path=src')).toBe(false)

    await userEvent.click(src)
    expect(src).toHaveAttribute('aria-expanded', 'true')
    expect(await within(tree).findByRole('treeitem', { name: 'app.ts, changed by the agent' })).toBeInTheDocument()
    expect(calls.some((c) => c.path === '/api/files?path=src')).toBe(true)

    await userEvent.click(within(tree).getByRole('treeitem', { name: 'app.ts, changed by the agent' }))
    expect(calls.some((c) => c.method === 'GET' && c.path === '/api/file?path=src%2Fapp.ts')).toBe(true)
    await waitFor(() => expect(editor().model?.value).toBe('const total = 1\nconsole.log(total)\n'))
    expect(editor().model?.language).toBe('typescript')
    expect(within(tabs()).getByRole('tab', { name: 'app.ts' })).toHaveAttribute('aria-selected', 'true')
    const status = screen.getByRole('contentinfo', { name: 'Status bar' })
    expect(within(status).getByText('TypeScript')).toBeInTheDocument()
    expect(within(status).getByLabelText('Line 1, column 1: go to a line')).toBeInTheDocument()

    // The arrow keys move in the tree; Left closes a folder.
    const row = within(tree).getByRole('treeitem', { name: 'app.ts, changed by the agent' })
    act(() => row.focus())
    await userEvent.keyboard('{ArrowUp}')
    expect(within(tree).getByRole('treeitem', { name: 'lib' })).toHaveFocus()
    await userEvent.keyboard('{ArrowUp}{ArrowLeft}')
    expect(src).toHaveAttribute('aria-expanded', 'false')
  })

  it('makes, renames and deletes files and folders from the explorer, and the open tabs follow', async () => {
    const { calls } = backend()
    renderIde()
    await openApp()

    // A file in the selected file's folder: named in place, then made and opened.
    await userEvent.click(screen.getByRole('button', { name: 'New file' }))
    await userEvent.type(screen.getByRole('textbox', { name: 'Name of the new file' }), 'cart.ts{Enter}')
    await waitFor(() => expect(calls.find((c) => c.path === '/api/files/new')?.body).toEqual({ path: 'src/cart.ts', kind: 'file' }))
    expect(await within(tabs()).findByRole('tab', { name: 'cart.ts' })).toHaveAttribute('aria-selected', 'true')

    await userEvent.click(screen.getByRole('button', { name: 'New folder' }))
    await userEvent.type(screen.getByRole('textbox', { name: 'Name of the new folder' }), 'models{Enter}')
    await waitFor(() => expect(calls.filter((c) => c.path === '/api/files/new').map((c) => c.body)).toContainEqual({ path: 'src/models', kind: 'dir' }))

    // F2 renames: the tab of the file renamed takes its new name.
    const row = await screen.findByRole('treeitem', { name: 'app.ts, changed by the agent' })
    act(() => row.focus())
    await userEvent.keyboard('{F2}')
    const name = screen.getByRole('textbox', { name: 'New name for app.ts' })
    await userEvent.clear(name)
    await userEvent.type(name, 'main.ts{Enter}')
    await waitFor(() => expect(calls.find((c) => c.path === '/api/files/rename')?.body).toEqual({ from: 'src/app.ts', to: 'src/main.ts' }))
    expect(await within(tabs()).findByRole('tab', { name: 'main.ts' })).toBeInTheDocument()
    expect(within(tabs()).queryByRole('tab', { name: 'app.ts' })).not.toBeInTheDocument()
    // Its model is at the file's new address now, with its text: the language services read it as that file.
    const moved = fakes.models.filter((m) => !m.disposed && m.uri?.toString() === 'file:///src/main.ts')
    expect(moved).toHaveLength(1)
    expect(moved[0]!.getValue()).toBe('const total = 1\nconsole.log(total)\n')
    expect(fakes.models.filter((m) => m.uri?.toString() === 'file:///src/app.ts').every((m) => m.disposed)).toBe(true)

    // Delete asks first.
    const readme = screen.getByRole('treeitem', { name: 'README.md' })
    act(() => readme.focus())
    await userEvent.keyboard('{Delete}')
    const ask = await screen.findByRole('alertdialog', { name: 'Delete README.md?' })
    expect(ask).toHaveTextContent('This cannot be undone.')
    await userEvent.click(within(ask).getByRole('button', { name: 'Delete' }))
    await waitFor(() => expect(calls.find((c) => c.path === '/api/files/delete')?.body).toEqual({ path: 'README.md' }))
    await waitFor(() => expect(screen.queryByRole('treeitem', { name: 'README.md' })).not.toBeInTheDocument())
  })

  it('marks a tab with unsaved changes, saves it with Ctrl+S, and asks before overwriting a file changed on disk', async () => {
    const { calls, disk } = backend()
    renderIde()
    await openApp()

    act(() => editor().model!.setValue('const total = 2\n'))
    expect(within(tabs()).getByRole('tab', { name: 'app.ts, not saved' })).toBeInTheDocument()
    await userEvent.keyboard('{Control>}s{/Control}')
    await waitFor(() => expect(calls.find((c) => c.method === 'POST' && c.path === '/api/file')?.body).toEqual({ path: 'src/app.ts', text: 'const total = 2\n', version: 'v1' }))
    expect(await within(tabs()).findByRole('tab', { name: 'app.ts' })).toBeInTheDocument()
    expect(disk['src/app.ts']!.text).toBe('const total = 2\n')

    // Changed on disk meanwhile: the save asks, then sends no version.
    disk['src/app.ts'] = { text: 'by the agent\n', version: 'agent' }
    act(() => editor().model!.setValue('const total = 3\n'))
    await userEvent.keyboard('{Control>}s{/Control}')
    const ask = await screen.findByRole('alertdialog', { name: 'app.ts changed on disk' })
    await userEvent.click(within(ask).getByRole('button', { name: 'Overwrite' }))
    await waitFor(() => expect(calls.filter((c) => c.method === 'POST' && c.path === '/api/file').at(-1)?.body).toEqual({ path: 'src/app.ts', text: 'const total = 3\n', version: null }))
    expect(await within(tabs()).findByRole('tab', { name: 'app.ts' })).toBeInTheDocument()
  })

  it('asks before closing a tab with unsaved changes: Cancel keeps it, Don’t save closes it', async () => {
    const { calls } = backend()
    renderIde()
    await openApp()
    const model = editor().model!
    act(() => model.setValue('draft\n'))

    await userEvent.click(within(tabs()).getByRole('button', { name: 'Close app.ts' }))
    let ask = await screen.findByRole('dialog', { name: 'Save the changes to app.ts?' })
    await userEvent.click(within(ask).getByRole('button', { name: 'Cancel' }))
    await waitFor(() => expect(screen.queryByRole('dialog')).not.toBeInTheDocument())
    expect(within(tabs()).getByRole('tab', { name: 'app.ts, not saved' })).toBeInTheDocument()

    await userEvent.click(within(tabs()).getByRole('button', { name: 'Close app.ts' }))
    ask = await screen.findByRole('dialog', { name: 'Save the changes to app.ts?' })
    await userEvent.click(within(ask).getByRole('button', { name: "Don't save" }))
    await waitFor(() => expect(screen.queryByRole('tablist', { name: 'Open files' })).not.toBeInTheDocument())
    expect(model.disposed).toBe(true)
    expect(calls.some((c) => c.method === 'POST' && c.path === '/api/file')).toBe(false)
    expect(screen.getByRole('heading', { name: 'No file is open' })).toBeInTheDocument()
  })

  it("shows the agent's changes in Monaco's diff editor, before and now, and accepts or reverts them per file", async () => {
    const { calls } = backend()
    renderIde()
    await userEvent.click(await screen.findByRole('button', { name: 'Agent changes (1)' }))
    const list = await screen.findByRole('list', { name: 'Changed files' })
    expect(list).toHaveTextContent('app.ts')
    expect(list).toHaveTextContent('+1')

    await userEvent.click(within(list).getByRole('button', { name: /app\.ts.*changed/ }))
    expect(calls.some((c) => c.path === '/api/changes/diff?path=src%2Fapp.ts')).toBe(true)
    await waitFor(() => expect(fakes.diffEditors[0]?.model?.original.value).toBe('const total = 0\n'))
    expect(fakes.diffEditors[0]!.model!.modified.value).toBe('const total = 1\nconsole.log(total)\n')
    expect(within(tabs()).getByRole('tab', { name: /app\.ts\s*changes/ })).toHaveAttribute('aria-selected', 'true')

    // Revert asks, then puts the file back; the diff closes.
    await userEvent.click(screen.getByRole('button', { name: 'Revert' }))
    const ask = await screen.findByRole('alertdialog', { name: 'Revert app.ts?' })
    await userEvent.click(within(ask).getByRole('button', { name: 'Revert' }))
    await waitFor(() => expect(calls.find((c) => c.path === '/api/changes/revert')?.body).toEqual({ path: 'src/app.ts' }))
    await waitFor(() => expect(screen.queryByRole('tablist', { name: 'Open files' })).not.toBeInTheDocument())
    expect(await screen.findByText(/No changes to review/)).toBeInTheDocument()
  })

  it('accepts a change from the diff tab, and all of them from the list', async () => {
    const { calls } = backend()
    renderIde()
    await userEvent.click(await screen.findByRole('button', { name: 'Agent changes (1)' }))
    await userEvent.click(within(await screen.findByRole('list', { name: 'Changed files' })).getByRole('button', { name: 'Accept src/app.ts' }))
    await waitFor(() => expect(calls.find((c) => c.path === '/api/changes/accept')?.body).toEqual({ path: 'src/app.ts' }))
    expect(await screen.findByRole('button', { name: 'Agent changes' })).toBeInTheDocument()
  })

  it('searches the files with the options, and a result opens the file at its line', async () => {
    const { calls } = backend()
    renderIde()
    await userEvent.click(await screen.findByRole('button', { name: 'Search' }))
    await userEvent.click(screen.getByRole('button', { name: 'Match case' }))
    await userEvent.click(screen.getByRole('button', { name: 'Use regular expression' }))
    expect(screen.getByRole('button', { name: 'Match case' })).toHaveAttribute('aria-pressed', 'true')
    await userEvent.type(screen.getByRole('textbox', { name: 'files to include' }), 'src')
    await userEvent.type(screen.getByRole('textbox', { name: 'files to exclude' }), 'tests')
    await userEvent.type(screen.getByRole('textbox', { name: 'Search' }), 'tot.l')

    const results = await screen.findByRole('list', { name: 'Search results' })
    const asked = calls.filter((c) => c.path.startsWith('/api/search')).at(-1)!
    expect(Object.fromEntries(new URL(asked.path, 'http://x').searchParams)).toEqual({ q: 'tot.l', case: '1', regex: '1', include: 'src', exclude: 'tests' })
    expect(screen.getByText('1 result in 1 file')).toBeInTheDocument()
    expect(within(results).getByText('total', { selector: 'mark' })).toBeInTheDocument()

    await userEvent.click(within(results).getByRole('button', { name: 'src/app.ts, line 2, column 13: console.log(total)' }))
    await waitFor(() => expect(editor().selection).toEqual({ startLineNumber: 2, startColumn: 13, endLineNumber: 2, endColumn: 18 }))
    expect(editor().revealed).toBe(2)
    expect(within(screen.getByRole('contentinfo', { name: 'Status bar' })).getByLabelText('Line 2, column 13: go to a line')).toBeInTheDocument()
  })

  it('opens terminals on the server, each on its socket: keys, output, the size, several tabs, closing', async () => {
    const { calls } = backend()
    renderIde()
    await userEvent.click(await screen.findByRole('button', { name: 'Terminal' }))

    // None open yet: one starts, and its socket is this page's server's.
    await waitFor(() => expect(calls.find((c) => c.method === 'POST' && c.path === '/api/terminals')?.body).toEqual({ cols: 80, rows: 24 }))
    await waitFor(() => expect(fakes.sockets).toHaveLength(1))
    const socket = fakes.sockets[0]!
    expect(socket.url).toBe(`ws://${window.location.host}/api/terminals/socket?id=1`)
    expect(socket.binaryType).toBe('arraybuffer')
    const term = fakes.terminals[0]!

    // Open: the terminal says the size it fits (120 by 30), then sends keys and shows output.
    act(() => socket.open())
    expect(socket.sent).toContain(JSON.stringify({ type: 'resize', cols: 120, rows: 30 }))
    act(() => term.type('echo hi\r'))
    expect(socket.sent).toContain(JSON.stringify({ type: 'input', data: 'echo hi\r' }))
    act(() => socket.receive(new TextEncoder().encode('hi\r\n').buffer))
    expect(Array.from(term.written.at(-1) as Uint8Array)).toEqual(Array.from(new TextEncoder().encode('hi\r\n')))
    act(() => term.resize(100, 20))
    expect(socket.sent.at(-1)).toBe(JSON.stringify({ type: 'resize', cols: 100, rows: 20 }))

    // A second terminal: a second tab and socket, sized as the first.
    const panel = screen.getByRole('region', { name: 'Terminal' })
    await userEvent.click(within(panel).getByRole('button', { name: 'New terminal' }))
    await waitFor(() => expect(fakes.sockets).toHaveLength(2))
    expect(calls.filter((c) => c.method === 'POST' && c.path === '/api/terminals').at(-1)?.body).toEqual({ cols: 100, rows: 20 })
    expect(fakes.sockets[1]!.url).toBe(`ws://${window.location.host}/api/terminals/socket?id=2`)
    const list = within(panel).getByRole('tablist', { name: 'Terminals' })
    expect(within(list).getAllByRole('tab').map((t) => t.textContent)).toEqual(['bash 1', 'bash 2'])
    expect(within(list).getByRole('tab', { name: 'bash 2' })).toHaveAttribute('aria-selected', 'true')

    // The shell ends: the screen says so and the tab too.
    act(() => fakes.sockets[1]!.open())
    act(() => fakes.sockets[1]!.receive(JSON.stringify({ type: 'exit', code: 3 })))
    expect(String(fakes.terminals[1]!.written.at(-1))).toContain('exit code 3')
    expect(await within(list).findByRole('tab', { name: /bash 2\s*\(ended\)/ })).toBeInTheDocument()

    // Closing ends it on the server and takes its tab away.
    await userEvent.click(within(panel).getByRole('button', { name: 'Close bash 2' }))
    await waitFor(() => expect(calls.find((c) => c.path === '/api/terminals/close')?.body).toEqual({ id: '2' }))
    await waitFor(() => expect(within(list).getAllByRole('tab')).toHaveLength(1))
    expect(fakes.terminals[1]!.disposed).toBe(true)
    expect(within(list).getByRole('tab', { name: 'bash 1' })).toHaveAttribute('aria-selected', 'true')
  })

  it('shows the branch, the model, the mode, the cursor and the language in the status bar, and the licence and source in About', async () => {
    backend()
    renderIde()
    const status = await screen.findByRole('contentinfo', { name: 'Status bar' })
    expect(within(status).getByText('feature/cart')).toBeInTheDocument()
    expect(within(status).getByRole('button', { name: 'Agent model model-a' })).toBeInTheDocument()
    expect(within(status).getByRole('button', { name: 'Agent mode Auto-edit' })).toBeInTheDocument()
    expect(await within(status).findByRole('button', { name: 'Agent changes: 1 file' })).toBeInTheDocument()

    await openApp()
    act(() => editor().cursor[0]!({ position: { lineNumber: 2, column: 7 } }))
    expect(within(status).getByText('Ln 2, Col 7')).toBeInTheDocument()
    expect(within(status).getByText('TypeScript')).toBeInTheDocument()

    await userEvent.click(within(status).getByRole('button', { name: 'code-arena 5.2.0: about, licence and source' }))
    const about = await screen.findByRole('dialog', { name: 'Code Arena' })
    expect(about).toHaveTextContent('5.2.0')
    expect(about).toHaveTextContent('AGPL-3.0-only')
    expect(within(about).getByRole('link', { name: 'Source' })).toHaveAttribute('href', 'https://github.com/Binchitects/argus')
  })

  it("shows the window's use and sets when the session compacts, and the MCP servers with Try again, in the status bar", async () => {
    const servers: CodeState['servers'] = [
      { name: 'arena', title: 'Arena', url: 'https://llm.test/mcp', state: 'connected', tools: 14, status: 'Arena: 14 tools', error: null, nextTry: null },
      { name: 'argus', title: 'Argus', url: 'https://argus.llm.test/mcp', state: 'failed', tools: 0, status: 'Argus: not connected, tried again at 10:41:07 (answered 503)', error: 'answered 503', nextTry: '2026-10-09T10:41:07Z' },
    ]
    let now = { ...state, servers }
    const { calls } = backend({
      'GET /api/state': () => ({ json: now }),
      'POST /api/settings': (body) => {
        const b = body as { compactAt?: number; compactTarget?: number }
        if (b.compactAt === 99) return { status: 400, json: { status: 'invalid', error: 'The threshold is from 20% to 95% of the model\'s window.' } }
        now = { ...now, ...b }
        return { json: now }
      },
      'POST /api/servers/retry': () => {
        now = { ...now, servers: [servers[0]!, { ...servers[1]!, state: 'connected', tools: 17, status: 'Argus: 17 tools', error: null, nextTry: null }] }
        return { json: now }
      },
    })
    renderIde()
    const status = await screen.findByRole('contentinfo', { name: 'Status bar' })

    // The servers: one of two connected, the other said with why.
    await userEvent.click(within(status).getByRole('button', { name: 'MCP servers: Arena connected, Argus not connected' }))
    let dialog = await screen.findByRole('dialog', { name: 'MCP servers' })
    expect(within(dialog).getByText('Arena: 14 tools')).toBeInTheDocument()
    expect(within(dialog).getByText('Argus: not connected, tried again at 10:41:07 (answered 503)')).toBeInTheDocument()
    expect(within(dialog).getByText('https://argus.llm.test/mcp')).toBeInTheDocument()
    await userEvent.click(within(dialog).getByRole('button', { name: 'Try Argus again' }))
    expect(calls.find((c) => c.path === '/api/servers/retry')?.body).toEqual({ name: 'argus' })
    expect(await within(dialog).findByText('Argus: 17 tools')).toBeInTheDocument()
    await userEvent.keyboard('{Escape}')
    expect(await within(status).findByRole('button', { name: 'MCP servers: Arena connected, Argus connected' })).toHaveTextContent('2/2')

    // The window: tokens used of the model's, and when it compacts.
    const context = within(status).getByRole('button', { name: 'Context: 4.2 K of 32.77 K tokens (13%), compacts at 80%' })
    expect(context).toHaveTextContent('4.2 K / 32.77 K')
    await userEvent.click(context)
    dialog = await screen.findByRole('dialog', { name: 'Context' })
    expect(within(dialog).getByText(/About 4\.2 K of 32\.77 K tokens in use \(13%\)/)).toBeInTheDocument()
    const at = within(dialog).getByLabelText('Compact at (% of the window)')
    const keep = within(dialog).getByLabelText('Keep the recent part within (%)')
    expect(at).toHaveValue(80)
    expect(keep).toHaveValue(25)
    // Checked before it is sent: what is kept stays 10 points under the threshold.
    await userEvent.clear(at)
    await userEvent.type(at, '30')
    await userEvent.click(within(dialog).getByRole('button', { name: 'Save' }))
    expect(within(dialog).getByRole('alert')).toHaveTextContent('What is kept is from 5% to 20% (10 points under the threshold).')
    expect(calls.some((c) => c.path === '/api/settings')).toBe(false)
    await userEvent.clear(at)
    await userEvent.type(at, '70')
    await userEvent.clear(keep)
    await userEvent.type(keep, '30')
    await userEvent.click(within(dialog).getByRole('button', { name: 'Save' }))
    expect(calls.find((c) => c.path === '/api/settings')?.body).toEqual({ compactAt: 70, compactTarget: 30 })
    await waitFor(() => expect(screen.queryByRole('dialog', { name: 'Context' })).not.toBeInTheDocument())
    expect(within(status).getByRole('button', { name: /compacts at 70%$/ })).toBeInTheDocument()

    // Defaults puts 80 and 25 back in the boxes.
    await userEvent.click(within(status).getByRole('button', { name: /^Context:/ }))
    dialog = await screen.findByRole('dialog', { name: 'Context' })
    expect(within(dialog).getByLabelText('Compact at (% of the window)')).toHaveValue(70)
    await userEvent.click(within(dialog).getByRole('button', { name: 'Defaults' }))
    expect(within(dialog).getByLabelText('Compact at (% of the window)')).toHaveValue(80)
    expect(within(dialog).getByLabelText('Keep the recent part within (%)')).toHaveValue(25)

    // Closed without saving: what was typed (and a complaint about it) is gone at the next opening, the session's values back.
    await userEvent.clear(within(dialog).getByLabelText('Compact at (% of the window)'))
    await userEvent.type(within(dialog).getByLabelText('Compact at (% of the window)'), '30')
    await userEvent.click(within(dialog).getByRole('button', { name: 'Save' }))
    expect(within(dialog).getByRole('alert')).toHaveTextContent('What is kept is from 5% to 20%')
    await userEvent.keyboard('{Escape}')
    await waitFor(() => expect(screen.queryByRole('dialog', { name: 'Context' })).not.toBeInTheDocument())
    await userEvent.click(within(status).getByRole('button', { name: /^Context:/ }))
    dialog = await screen.findByRole('dialog', { name: 'Context' })
    expect(within(dialog).getByLabelText('Compact at (% of the window)')).toHaveValue(70)
    expect(within(dialog).getByLabelText('Keep the recent part within (%)')).toHaveValue(30)
    expect(within(dialog).queryByRole('alert')).not.toBeInTheDocument()
    await userEvent.keyboard('{Escape}')

    // On a phone the numbers give way to the icons (the model and About keep their room); the names say them.
    expect(within(status).getByText('4.2 K / 32.77 K')).toHaveClass('hidden', 'md:inline')
    expect(within(status).getByText('2/2')).toHaveClass('hidden', 'md:inline')
  })

  it('reloads an open file the agent edits when nothing in it is unsaved, and leaves one with unsaved changes alone', async () => {
    const texts = ['const total = 5\nconsole.log(total)\n', 'the agent again\n']
    let turn = 0
    const { calls, disk } = backend({
      // Each turn: the agent edits src/app.ts, then the turn is over.
      'POST /api/messages': () => {
        disk['src/app.ts'] = { text: texts[turn]!, version: `v${5 + turn}` }
        const at = turn * 3
        turn++
        const diff = { path: 'src/app.ts', added: 1, removed: 1, more: 0, created: false, lines: [['+', 0, 1, 'x']] }
        return {
          events: [
            { type: 'question', id: `m${at}`, parentId: at ? `m${at - 1}` : null },
            { type: 'assistant', id: `m${at + 1}`, parentId: `m${at}`, model: 'model-a' },
            { type: 'tool_call', id: `c${turn}`, name: 'edit_file', arguments: '{"path":"src/app.ts"}', tool: 'local' },
            { type: 'tool_result', id: `c${turn}`, messageId: `m${at + 2}`, name: 'edit_file', text: 'Edited src/app.ts.', isError: false, declined: false, noAccess: false, durationMs: 3, diff },
          ],
        }
      },
    })
    renderIde()
    await openApp()
    const model = editor().model!

    await userEvent.type(screen.getByRole('textbox', { name: 'Message' }), 'Make the total 5{Enter}')
    await waitFor(() => expect(model.value).toBe('const total = 5\nconsole.log(total)\n'))
    expect(within(tabs()).getByRole('tab', { name: 'app.ts' })).toBeInTheDocument()
    await screen.findByRole('button', { name: 'Send' })

    // With unsaved changes, the agent's next edit does not replace them: saving then asks first (the version moved on).
    act(() => model.setValue('mine\n'))
    const reads = calls.filter((c) => c.path === '/api/file?path=src%2Fapp.ts').length
    await userEvent.type(screen.getByRole('textbox', { name: 'Message' }), 'Again{Enter}')
    await waitFor(() => expect(turn).toBe(2))
    await screen.findByRole('button', { name: 'Send' })
    expect(model.value).toBe('mine\n')
    expect(calls.filter((c) => c.path === '/api/file?path=src%2Fapp.ts')).toHaveLength(reads)
    expect(within(tabs()).getByRole('tab', { name: 'app.ts, not saved' })).toBeInTheDocument()
  })

  it('keeps what is typed while the file the agent edited is read again: the tab stays unsaved, and saving asks', async () => {
    const { calls, disk } = backend({
      'POST /api/messages': () => {
        disk['src/app.ts'] = { text: 'by the agent\n', version: 'v5' }
        const diff = { path: 'src/app.ts', added: 1, removed: 1, more: 0, created: false, lines: [['+', 0, 1, 'x']] }
        return {
          events: [
            ...answerOnly().events,
            { type: 'tool_call', id: 'c1', name: 'edit_file', arguments: '{"path":"src/app.ts"}', tool: 'local' },
            { type: 'tool_result', id: 'c1', messageId: 'm2', name: 'edit_file', text: 'Edited src/app.ts.', isError: false, declined: false, noAccess: false, durationMs: 3, diff },
          ],
        }
      },
    })
    renderIde()
    await openApp()
    const model = editor().model!
    const read = (c: { path: string }) => c.path === '/api/file?path=src%2Fapp.ts'
    const before = calls.filter(read).length

    // The agent's edit is being read from the disk when the person types.
    const hold = holdBack((path) => path === '/api/file?path=src%2Fapp.ts')
    await userEvent.type(screen.getByRole('textbox', { name: 'Message' }), 'Change it{Enter}')
    await waitFor(() => expect(hold.held()).toBeGreaterThan(0))
    act(() => model.setValue('mine\n'))
    hold.release()
    await screen.findByRole('button', { name: 'Send' })
    await waitFor(() => expect(calls.filter(read)).toHaveLength(before + hold.held()))
    await act(async () => {})

    expect(model.value).toBe('mine\n')
    expect(within(tabs()).getByRole('tab', { name: 'app.ts, not saved' })).toBeInTheDocument()
    await userEvent.keyboard('{Control>}s{/Control}')
    expect(await screen.findByRole('alertdialog', { name: 'app.ts changed on disk' })).toBeInTheDocument()
  })

  it('keeps what is saved while the file is read again: a read from before the save does not put the older text back', async () => {
    const { calls, disk } = backend({ 'POST /api/messages': () => answerOnly() })
    renderIde()
    await openApp()
    const model = editor().model!

    // The end of the turn reads app.ts; the server answers before the save below, and the answer comes after it.
    const hold = holdBack((path) => path === '/api/file?path=src%2Fapp.ts', { answeredEarly: true })
    await userEvent.type(screen.getByRole('textbox', { name: 'Message' }), 'Hello{Enter}')
    await waitFor(() => expect(hold.held()).toBe(1))
    act(() => model.setValue('mine\n'))
    await userEvent.keyboard('{Control>}s{/Control}')
    await waitFor(() => expect(disk['src/app.ts']).toEqual({ text: 'mine\n', version: 'v1+' }))
    expect(await within(tabs()).findByRole('tab', { name: 'app.ts' })).toBeInTheDocument()
    hold.release()
    await screen.findByRole('button', { name: 'Send' })
    await act(async () => {})

    expect(model.value).toBe('mine\n')
    expect(within(tabs()).getByRole('tab', { name: 'app.ts' })).toBeInTheDocument()
    // The next save sends the version saved: nothing asks, nothing is overwritten.
    act(() => model.setValue('mine again\n'))
    await userEvent.keyboard('{Control>}s{/Control}')
    await waitFor(() => expect(calls.filter((c) => c.method === 'POST' && c.path === '/api/file').at(-1)?.body).toEqual({ path: 'src/app.ts', text: 'mine again\n', version: 'v1+' }))
    expect(await within(tabs()).findByRole('tab', { name: 'app.ts' })).toBeInTheDocument()
    expect(screen.queryByRole('alertdialog')).not.toBeInTheDocument()
    expect(disk['src/app.ts']!.text).toBe('mine again\n')
  })

  it('a tab closed while the files are checked against the disk does not stop the other tabs from reloading', async () => {
    const { disk } = backend({
      'POST /api/messages': () => {
        disk['src/app.ts'] = { text: 'app, by a command\n', version: 'v5' }
        disk['README.md'] = { text: '# Shop, by a command\n', version: 'r5' }
        return answerOnly()
      },
    })
    renderIde()
    await openApp()
    await userEvent.click(screen.getByRole('treeitem', { name: 'README.md' }))
    await waitFor(() => expect(editor().model?.value).toBe('# Shop\n'))
    const readme = editor().model!

    // The end of the turn reads app.ts first; it is closed meanwhile.
    const hold = holdBack((path) => path === '/api/file?path=src%2Fapp.ts')
    await userEvent.type(screen.getByRole('textbox', { name: 'Message' }), 'Run the formatter{Enter}')
    await waitFor(() => expect(hold.held()).toBe(1))
    await userEvent.click(within(tabs()).getByRole('button', { name: 'Close app.ts' }))
    hold.release()
    await waitFor(() => expect(readme.value).toBe('# Shop, by a command\n'))
  })

  it('a file closed while it opens and opened again has one model, the one the editor shows and saves', async () => {
    const { calls } = backend()
    renderIde()
    await userEvent.click(await screen.findByRole('treeitem', { name: 'src, the agent changed files in it' }))
    const row = await screen.findByRole('treeitem', { name: 'app.ts, changed by the agent' })
    const hold = holdBack((path) => path === '/api/file?path=src%2Fapp.ts')
    await userEvent.click(row)
    await userEvent.click(within(tabs()).getByRole('button', { name: 'Close app.ts' }))
    await userEvent.click(row)
    // Opened again while the first read is on its way: that read serves it (one model for the file).
    await waitFor(() => expect(hold.held()).toBe(1))
    hold.release()
    await waitFor(() => expect(editor().model?.value).toBe('const total = 1\nconsole.log(total)\n'))
    await waitFor(() => expect(calls.filter((c) => c.path === '/api/file?path=src%2Fapp.ts')).toHaveLength(1))
    await act(async () => {})
    expect(fakes.models.filter((m) => !m.disposed)).toHaveLength(1)

    act(() => editor().model!.setValue('typed\n'))
    await userEvent.keyboard('{Control>}s{/Control}')
    await waitFor(() => expect(calls.find((c) => c.method === 'POST' && c.path === '/api/file')?.body).toEqual({ path: 'src/app.ts', text: 'typed\n', version: 'v1' }))
    expect(await within(tabs()).findByRole('tab', { name: 'app.ts' })).toBeInTheDocument()
  })

  it('a file renamed while it opens opens under its new name', async () => {
    // Read before the rename: the answer comes after it.
    backend({ 'GET /api/file': () => ({ json: { path: 'src/app.ts', size: 34, version: 'v1', text: 'const total = 1\nconsole.log(total)\n' } }) })
    renderIde()
    await userEvent.click(await screen.findByRole('treeitem', { name: 'src, the agent changed files in it' }))
    const row = await screen.findByRole('treeitem', { name: 'app.ts, changed by the agent' })
    const hold = holdBack((path) => path === '/api/file?path=src%2Fapp.ts')
    await userEvent.click(row)
    await waitFor(() => expect(hold.held()).toBe(1))
    act(() => row.focus())
    await userEvent.keyboard('{F2}')
    const name = screen.getByRole('textbox', { name: 'New name for app.ts' })
    await userEvent.clear(name)
    await userEvent.type(name, 'main.ts{Enter}')
    expect(await within(tabs()).findByRole('tab', { name: 'main.ts' })).toBeInTheDocument()
    hold.release()
    await waitFor(() => expect(editor().model?.value).toBe('const total = 1\nconsole.log(total)\n'))
    expect(screen.queryByLabelText('Opening main.ts')).not.toBeInTheDocument()
  })

  it('deleting a file with unsaved changes says so, and keeps its tab: saving makes the file again', async () => {
    const { calls } = backend()
    renderIde()
    await openApp()
    act(() => editor().model!.setValue('draft\n'))

    const row = screen.getByRole('treeitem', { name: 'app.ts, changed by the agent' })
    act(() => row.focus())
    await userEvent.keyboard('{Delete}')
    const ask = await screen.findByRole('alertdialog', { name: 'Delete app.ts?' })
    expect(ask).toHaveTextContent('app.ts has unsaved changes: its tab stays open, and saving it makes the file again.')
    await userEvent.click(within(ask).getByRole('button', { name: 'Delete' }))
    await waitFor(() => expect(calls.find((c) => c.path === '/api/files/delete')?.body).toEqual({ path: 'src/app.ts' }))

    expect(within(tabs()).getByRole('tab', { name: 'app.ts, not saved' })).toBeInTheDocument()
    expect(editor().model?.disposed).toBe(false)
    await userEvent.keyboard('{Control>}s{/Control}')
    await waitFor(() => expect(calls.find((c) => c.method === 'POST' && c.path === '/api/file')?.body).toEqual({ path: 'src/app.ts', text: 'draft\n', version: 'v1' }))
  })

  it('keeps the workbench and its unsaved changes when code-arena web stops answering, and says so until it answers again', async () => {
    let down = false
    backend({ 'GET /api/state': () => (down ? { offline: true } : { json: state }) })
    const { client } = renderIde()
    await openApp()
    act(() => editor().model!.setValue('draft\n'))

    down = true
    await act(() => client.refetchQueries({ queryKey: ['code', 'state'] }))
    expect(await screen.findByText('code-arena web is not answering.')).toBeInTheDocument()
    expect(within(tabs()).getByRole('tab', { name: 'app.ts, not saved' })).toBeInTheDocument()
    expect(editor().model?.disposed).toBe(false)
    expect(editor().model?.value).toBe('draft\n')

    down = false
    await act(() => client.refetchQueries({ queryKey: ['code', 'state'] }))
    await waitFor(() => expect(screen.queryByText('code-arena web is not answering.')).not.toBeInTheDocument())
    expect(within(tabs()).getByRole('tab', { name: 'app.ts, not saved' })).toBeInTheDocument()
  })

  it('on macOS takes ⌘ for its keys and leaves Ctrl+P and Ctrl+S to the terminal and the editor', async () => {
    vi.spyOn(navigator, 'platform', 'get').mockReturnValue('MacIntel')
    backend()
    renderIde()
    await screen.findByRole('tree', { name: 'Files' })
    const press = (key: string, mods: KeyboardEventInit) => {
      const e = new KeyboardEvent('keydown', { key, bubbles: true, cancelable: true, ...mods })
      act(() => document.body.dispatchEvent(e))
      return e.defaultPrevented
    }
    expect(press('s', { ctrlKey: true })).toBe(false)
    expect(press('p', { ctrlKey: true })).toBe(false)
    expect(press('F', { ctrlKey: true, shiftKey: true })).toBe(false)
    expect(screen.queryByPlaceholderText('Go to file: type a few letters of its path')).not.toBeInTheDocument()

    await userEvent.keyboard('{Meta>}p{/Meta}')
    expect(await screen.findByPlaceholderText('Go to file: a few letters of its path (:12 for a line)')).toBeInTheDocument()
  })

  it('opens a terminal each time the panel is shown with none: after the last one closed too', async () => {
    const { calls } = backend()
    renderIde()
    const opened = () => calls.filter((c) => c.method === 'POST' && c.path === '/api/terminals')
    await userEvent.click(await screen.findByRole('button', { name: 'Terminal' }))
    await waitFor(() => expect(opened()).toHaveLength(1))
    const panel = screen.getByRole('region', { name: 'Terminal' })
    await userEvent.click(await within(panel).findByRole('button', { name: 'Close bash 1' }))
    await waitFor(() => expect(screen.queryByRole('region', { name: 'Terminal' })).not.toBeInTheDocument())

    await userEvent.keyboard('{Control>}[Backquote]{/Control}')
    await waitFor(() => expect(opened()).toHaveLength(2))
    expect(await within(screen.getByRole('region', { name: 'Terminal' })).findByRole('tab', { name: 'bash 2' })).toBeInTheDocument()
  })

  it('opens one terminal when the panel is hidden and shown again while its first one is opening', async () => {
    const { calls } = backend()
    renderIde()
    const opened = () => calls.filter((c) => c.method === 'POST' && c.path === '/api/terminals')
    await screen.findByRole('tree', { name: 'Files' })
    const hold = holdBack((path, method) => method === 'POST' && path === '/api/terminals')
    await userEvent.click(screen.getByRole('button', { name: 'Terminal' }))
    await waitFor(() => expect(hold.held()).toBe(1))
    await userEvent.keyboard('{Control>}[Backquote]{/Control}')
    await userEvent.keyboard('{Control>}[Backquote]{/Control}')
    await act(() => new Promise((wait) => setTimeout(wait, 20)))
    expect(hold.held()).toBe(1)

    hold.release()
    const panel = screen.getByRole('region', { name: 'Terminal' })
    expect(await within(panel).findByRole('tab', { name: 'bash 1' })).toBeInTheDocument()
    await act(() => new Promise((wait) => setTimeout(wait, 20)))
    expect(opened()).toHaveLength(1)
    expect(within(panel).getAllByRole('tab')).toHaveLength(1)
  })

  it("opens the folder's files an answer cites in the editor, at the lines cited; anything else stays text", async () => {
    const said = (id: string, role: Message['role'], parentId: string | null, over: Partial<Message> = {}): Message => ({ ...blank(id, role, parentId), ...over })
    const edit = { id: 'c1', function: { name: 'edit_file', arguments: '{"path":"src/app.ts","old_string":"1","new_string":"2"}' } }
    const read = { id: 'c2', function: { name: 'read_file', arguments: '{"path":"src/lib/cart.ts","offset":4}' } }
    const grep = { id: 'c3', function: { name: 'grep', arguments: '{"pattern":"total"}' } }
    const session = {
      id: 's1',
      busy: false,
      diffs: {
        m3: { path: 'src/app.ts', added: 1, removed: 1, more: 0, created: false, lines: [[' ', 1, 1, 'a'], ['-', 2, 0, 'const total = 1'], ['+', 0, 2, 'const total = 2']] },
      },
      messages: [
        said('m0', 'user', null, { content: 'Where is the total?' }),
        said('m1', 'assistant', 'm0', { toolCalls: [edit, read, grep], model: 'model-a' }),
        said('m3', 'tool', 'm1', { content: 'Edited src/app.ts (lines 2-3).', toolCallId: 'c1', toolName: 'edit_file' }),
        said('m4', 'tool', 'm3', { content: 'export {}', toolCallId: 'c2', toolName: 'read_file' }),
        said('m6', 'tool', 'm4', { content: 'src/app.ts:2:console.log(total)', toolCallId: 'c3', toolName: 'grep' }),
        said('m5', 'assistant', 'm6', {
          model: 'model-a',
          content: 'It is in `src/app.ts:2-3`, used by src/lib/cart.ts, as [the readme](README.md#L1) says; `nope.ts:3` and [elsewhere](docs/none.md) are not here.',
        }),
      ],
    }
    backend({ 'GET /api/session': () => ({ json: session }) })
    renderIde()
    const answer = await screen.findByRole('region', { name: 'Answer' })

    // Inline code: the file opens with the lines cited selected.
    await userEvent.click(await within(answer).findByRole('link', { name: 'src/app.ts:2-3' }))
    expect(await within(tabs()).findByRole('tab', { name: 'app.ts' })).toHaveAttribute('aria-selected', 'true')
    await waitFor(() => expect(editor().selection).toEqual({ startLineNumber: 2, startColumn: 1, endLineNumber: 4, endColumn: 1 }))
    expect(editor().revealed).toBe(2)

    // A path in the text, and a relative link with its line.
    await userEvent.click(within(answer).getByRole('link', { name: 'src/lib/cart.ts' }))
    expect(await within(tabs()).findByRole('tab', { name: 'cart.ts' })).toHaveAttribute('aria-selected', 'true')
    await userEvent.click(within(answer).getByRole('link', { name: 'the readme' }))
    expect(await within(tabs()).findByRole('tab', { name: 'README.md' })).toHaveAttribute('aria-selected', 'true')

    // What is no file of the folder stays text, and a link to it goes nowhere.
    expect(within(answer).getByText('nope.ts:3')).not.toHaveAttribute('data-ref')
    const elsewhere = within(answer).getByText('elsewhere')
    expect(elsewhere).not.toHaveAttribute('data-ref')
    await userEvent.click(elsewhere)
    expect(window.location.pathname).not.toContain('docs/none.md')

    // The edit's file opens at its first changed line, from its diff's path and its line numbers; read_file's at the line read.
    await userEvent.click(within(answer).getByRole('button', { name: 'Open src/app.ts at line 2' }))
    await waitFor(() => expect(editor().selection).toEqual({ startLineNumber: 2, startColumn: 1, endLineNumber: 2, endColumn: 1 }))
    await userEvent.click(within(answer).getByRole('button', { name: 'Open src/lib/cart.ts:4 in the editor' }))
    await waitFor(() => expect(within(tabs()).getByRole('tab', { name: 'cart.ts' })).toHaveAttribute('aria-selected', 'true'))
    await waitFor(() => expect(editor().selection?.startLineNumber).toBe(4))
    // A path in a tool's output opens too (grep's rows).
    const cards = within(answer).getAllByRole('button', { expanded: false })
    await userEvent.click(cards.find((b) => /grep|search/i.test(b.textContent ?? ''))!)
    await userEvent.click(await within(answer).findByRole('link', { name: 'src/app.ts:2' }))
    await waitFor(() => expect(editor().selection).toEqual({ startLineNumber: 2, startColumn: 1, endLineNumber: 2, endColumn: 1 }))

    // The edit's chip opens at the lines its result says.
    await userEvent.click(within(answer).getByRole('button', { name: 'Open src/app.ts:2-3 in the editor' }))
    await waitFor(() => expect(editor().selection).toEqual({ startLineNumber: 2, startColumn: 1, endLineNumber: 4, endColumn: 1 }))
  })

  it('sends lines from the editor with a message, and asks it to explain, fix or complete them', async () => {
    let asked = false
    const { calls } = backend({
      // The session saved: the question as typed, and what went with it.
      'GET /api/session': () => ({
        json: {
          id: 's1', busy: false, diffs: {},
          messages: asked ? [{ ...blank('m0', 'user', null), content: 'Why this?', files: ['src/app.ts:1-2'] }, { ...blank('m1', 'assistant', 'm0'), content: 'Done.' }] : [],
        },
      }),
      'POST /api/messages': () => ({
        events: [
          { type: 'question', id: 'm0', parentId: null },
          { type: 'attached', id: 'm0', files: ['src/app.ts:1-2'] },
          { type: 'assistant', id: 'm1', parentId: 'm0', model: 'model-a' },
          { type: 'content', text: 'Done.' },
        ],
      }),
    })
    renderIde()
    await openApp()
    const ed = editor()
    const sent = () => calls.filter((c) => c.method === 'POST' && c.path === '/api/messages').map((c) => c.body as { text: string; context?: unknown[] })

    // Add to chat (Ctrl+L): the lines chosen wait above the box, and go with the next message.
    ed.setSelection({ startLineNumber: 1, startColumn: 1, endLineNumber: 3, endColumn: 1 })
    act(() => ed.actions.get('codeArena.addToChat')!.run())
    // Its key is the keymap's (keybindings.json may change it), given to Monaco with the workbench's.
    await waitFor(() => expect(fakes.keybindingRules.at(-1)).toContainEqual({ keybinding: 2048 | 42, command: 'codeArena.addToChat', commandArgs: undefined, when: 'editorTextFocus' }))
    expect(fakes.keybindingRules.at(-1)![0]).toEqual({ keybinding: 59, command: '-editor.action.quickCommand' })
    const box = screen.getByRole('textbox', { name: 'Message' })
    expect(await within(screen.getByRole('list', { name: 'Sent with the message' })).findByText('src/app.ts:1-2')).toBeInTheDocument()
    await waitFor(() => expect(box).toHaveFocus())
    asked = true
    await userEvent.type(box, 'Why this?{Enter}')
    await waitFor(() => expect(sent()).toHaveLength(1))
    expect(sent()[0]).toEqual({ text: 'Why this?', context: [{ path: 'src/app.ts', startLine: 1, endLine: 2 }] })
    expect(screen.queryByRole('button', { name: 'Leave out src/app.ts:1-2' })).not.toBeInTheDocument()
    // The question lists what went with it; each opens in the editor.
    expect(await screen.findByRole('button', { name: 'Open src/app.ts:1-2 in the editor' })).toBeInTheDocument()

    // Explain this: asked at once, about the line the cursor is on; unsaved, the editor's text goes.
    ed.model!.setValue('const total = 3\nconsole.log(total)\n')
    ed.setSelection({ startLineNumber: 2, startColumn: 4, endLineNumber: 2, endColumn: 4 })
    act(() => ed.actions.get('codeArena.explain')!.run())
    await waitFor(() => expect(sent()).toHaveLength(2))
    expect(sent()[1]).toEqual({ text: asks.explain, context: [{ path: 'src/app.ts', startLine: 2, endLine: 2, text: 'console.log(total)' }] })

    // Fix this: with the problems the editor marks there.
    await screen.findByRole('button', { name: 'Send' })
    fakes.markers.set('file:///src/app.ts', [{ severity: 8, startLineNumber: 1, startColumn: 15, endLineNumber: 1, message: "';' expected." }])
    ed.setSelection({ startLineNumber: 1, startColumn: 1, endLineNumber: 1, endColumn: 1 })
    act(() => ed.actions.get('codeArena.fix')!.run())
    await waitFor(() => expect(sent()).toHaveLength(3))
    expect(sent()[2]!.text).toBe("Fix the problems in this code: 1:15 ';' expected.")
    expect(ed.actions.get('codeArena.complete')!.label).toBe('Code Arena: Complete this')

    // The light bulb on a marked line: Fix with Code Arena.
    const [provider] = fakes.codeActions
    const offered = provider!.provideCodeActions({ uri: new fakes.Uri('file', '/src/app.ts') }, { startLineNumber: 1, endLineNumber: 1 }, { markers: fakes.markers.get('file:///src/app.ts') }) as {
      actions: { title: string; command: { id: string; arguments: unknown[] } }[]
    }
    expect(offered.actions.map((a) => a.title)).toEqual(['Fix with Code Arena'])
    await screen.findByRole('button', { name: 'Send' })
    act(() => fakes.commands.get(offered.actions[0]!.command.id)!(null, ...offered.actions[0]!.command.arguments))
    await waitFor(() => expect(sent()).toHaveLength(4))
    // The file has unsaved changes: the editor's line goes, not the disk's.
    expect(sent()[3]!.context).toEqual([{ path: 'src/app.ts', startLine: 1, endLine: 1, text: 'const total = 3' }])

    // The Explorer's Add to chat names the whole file in the box.
    await screen.findByRole('button', { name: 'Send' })
    const row = screen.getByRole('treeitem', { name: 'app.ts, changed by the agent' })
    await userEvent.pointer({ keys: '[MouseRight]', target: row })
    await userEvent.click(await screen.findByRole('menuitem', { name: 'Add to chat' }))
    await waitFor(() => expect(screen.getByRole('textbox', { name: 'Message' })).toHaveValue('@src/app.ts '))

    // @ offers the folder's files, best first; Enter puts the one chosen in the box.
    const message = screen.getByRole('textbox', { name: 'Message' })
    await userEvent.clear(message)
    await userEvent.type(message, 'look at @cart')
    const picker = await screen.findByRole('listbox', { name: 'Files to send with the message' })
    expect(within(picker).getAllByRole('option').map((o) => o.textContent)).toEqual(['src/lib/cart.ts'])
    await userEvent.keyboard('{Enter}')
    expect(message).toHaveValue('look at @src/lib/cart.ts ')
    expect(screen.queryByRole('listbox', { name: 'Files to send with the message' })).not.toBeInTheDocument()

    // The lines the editor shows are a click away from the message.
    ed.setSelection({ startLineNumber: 2, startColumn: 1, endLineNumber: 2, endColumn: 1 })
    act(() => ed.cursor.forEach((f) => f({ position: { lineNumber: 2, column: 1 } })))
    await userEvent.click(await screen.findByRole('button', { name: /^Send src\/app.ts:2 with the message$/ }))
    expect(screen.getByRole('button', { name: 'Leave out src/app.ts:2' })).toBeInTheDocument()
  })

  it('runs any command by name, goes to a line, and opens a path:line a terminal shows', async () => {
    backend()
    renderIde()
    await openApp()

    // Ctrl+Shift+P: the workbench's commands and the editor's, narrowed by what is typed.
    await userEvent.keyboard('{Control>}{Shift>}p{/Shift}{/Control}')
    const palette = await screen.findByRole('dialog', { name: 'Commands' })
    const command = within(palette).getByPlaceholderText('Run a command: type a few words of it')
    await userEvent.type(command, 'code arena')
    expect(within(palette).getAllByRole('option').map((o) => o.textContent)).toEqual(
      expect.arrayContaining(['Code Arena: Add to chatCtrl+L', 'Code Arena: Explain this', 'Code Arena: Fix this', 'Code Arena: Complete this']),
    )
    // One of them run from the palette: the lines go with the next message.
    await userEvent.click(within(palette).getByRole('option', { name: /Code Arena: Add to chat/ }))
    // The chat redraws as the line comes: read again until it is there.
    await waitFor(() => expect(within(screen.getByRole('list', { name: 'Sent with the message' })).getByText('src/app.ts:1')).toBeInTheDocument(), { timeout: 3000 })
    await userEvent.keyboard('{Control>}{Shift>}p{/Shift}{/Control}')
    await userEvent.type(within(await screen.findByRole('dialog', { name: 'Commands' })).getByPlaceholderText('Run a command: type a few words of it'), 'x')
    const command2 = within(screen.getByRole('dialog', { name: 'Commands' })).getByPlaceholderText('Run a command: type a few words of it')
    await userEvent.clear(command2)
    await userEvent.type(command2, 'toggle terminal')
    expect(within(screen.getByRole('dialog', { name: 'Commands' })).getByRole('option', { name: /View: Toggle Terminal/ })).toHaveTextContent('Ctrl+`')
    await userEvent.keyboard('{Enter}')
    await waitFor(() => expect(fakes.terminals.length).toBeGreaterThan(0))

    // The line in the status bar: Go to line, with ":" typed; :2 goes there.
    await userEvent.click(screen.getByRole('button', { name: /^Line \d+, column \d+: go to a line$/ }))
    const quick = await screen.findByRole('dialog', { name: 'Go to file' })
    const typed = within(quick).getByPlaceholderText('Go to file: a few letters of its path (:12 for a line)')
    expect(typed).toHaveValue(':')
    await userEvent.type(typed, '2')
    await userEvent.click(await within(quick).findByRole('option', { name: /Go to line 2/ }))
    await waitFor(() => expect(editor().selection).toEqual({ startLineNumber: 2, startColumn: 1, endLineNumber: 2, endColumn: 1 }))

    // A compiler's path:line:column in a terminal opens the file there; a path that is not the folder's is no link.
    const t = fakes.terminals.at(-1)!
    t.lines = ['src/lib/cart.ts:1:8 - error TS1005', 'see /etc/hosts:2']
    let links: { text: string; activate: () => void }[] | undefined
    t.links[0]!.provideLinks(1, (l) => (links = l))
    expect(links?.map((l) => l.text)).toEqual(['src/lib/cart.ts:1:8'])
    act(() => links![0]!.activate())
    expect(await within(tabs()).findByRole('tab', { name: 'cart.ts' })).toHaveAttribute('aria-selected', 'true')
    t.links[0]!.provideLinks(2, (l) => (links = l))
    expect(links).toBeUndefined()
  })

  it('completes code at the cursor as grey text, and the status bar turns it off', async () => {
    const { calls } = backend({
      'GET /api/state': () => ({ json: { ...state, completion: { model: 'model-a' } } }),
      'POST /api/complete': () => ({ json: { text: '+ 1' } }),
    })
    renderIde()
    await openApp()
    expect(editor().options.inlineSuggest).toEqual({ enabled: true })
    const [provider] = fakes.inline
    const token = () => ({ isCancellationRequested: false, onCancellationRequested: () => ({ dispose() {} }) })
    const model = editor().model!
    // After a pause in the typing: the code before the cursor and after it go; the answer comes back to insert there.
    const got = await provider!.provideInlineCompletions(model, { lineNumber: 1, column: 16 }, {}, token())
    expect(got.items.map((i) => i.insertText)).toEqual(['+ 1'])
    expect(calls.find((c) => c.path === '/api/complete')?.body).toEqual({ path: 'src/app.ts', prefix: 'const total = 1', suffix: '\nconsole.log(total)\n' })

    // Turned off in the status bar: nothing is asked, and the choice is kept in the person's settings.json.
    const toggle = screen.getByRole('button', { name: 'Code completion on' })
    await userEvent.click(toggle)
    await waitFor(() => expect(calls.find((c) => c.path === '/api/user-files' && c.method === 'POST')?.body).toEqual({ name: 'settings.json', text: '{\n    "codeArena.inlineCompletion.enabled": false\n}\n', version: 'u0' }))
    await waitFor(() => expect(screen.getByRole('button', { name: 'Code completion off' })).toBeInTheDocument())
    const asked = calls.filter((c) => c.path === '/api/complete').length
    expect((await provider!.provideInlineCompletions(model, { lineNumber: 2, column: 1 }, {}, token())).items).toEqual([])
    expect(calls.filter((c) => c.path === '/api/complete')).toHaveLength(asked)
  })

  it("runs the project's check, lists its problems by file, marks them in the editor and asks the agent to fix one", async () => {
    let ran = false
    const problem = { path: 'src/app.ts', line: 2, column: 9, severity: 'error', code: 'TS2304', message: "Cannot find name 'totl'." }
    // A line the file does not have (0, or past its end) is not marked, and breaks nothing.
    const nowhere = { path: 'src/app.ts', line: 0, column: 0, severity: 'warning', code: null, message: 'Somewhere.' }
    const check = () => ({ command: 'npx --no-install tsc --noEmit', running: false, ran: ran ? '2026-10-11T10:00:00Z' : null, exitCode: ran ? 2 : null, said: null, problems: ran ? [problem, nowhere] : [] })
    const { calls } = backend({
      'GET /api/problems': () => ({ json: check() }),
      'POST /api/problems/run': () => {
        ran = true
        return { json: check() }
      },
      'POST /api/messages': () => answerOnly(),
    })
    renderIde()
    await openApp()
    await userEvent.click(screen.getByRole('button', { name: 'Problems' }))
    const panel = await screen.findByRole('complementary', { name: 'Problems' })
    expect(await within(panel).findByText(/Run the check/)).toBeInTheDocument()

    await userEvent.click(within(panel).getByRole('button', { name: 'Run check' }))
    const list = await within(panel).findByRole('list', { name: 'Problems in src/app.ts' })
    expect(within(list).getByText(/Cannot find name 'totl'/)).toBeInTheDocument()
    expect(within(screen.getByRole('contentinfo', { name: 'Status bar' })).getByRole('button', { name: 'Problems: 1 error, 1 warning' })).toBeInTheDocument()
    // Marked in the file open.
    await waitFor(() => expect(fakes.marked.get('check:file:///src/app.ts')?.map((m) => [m.startLineNumber, m.message])).toEqual([[2, "Cannot find name 'totl'."]]))

    // A problem opens its file at its line; Fix asks the agent, with the line.
    await userEvent.click(within(list).getByRole('button', { name: /Cannot find name 'totl'/ }))
    await waitFor(() => expect(editor().selection).toEqual({ startLineNumber: 2, startColumn: 9, endLineNumber: 2, endColumn: 9 }))
    await userEvent.click(within(list).getByRole('button', { name: 'Fix src/app.ts:2' }))
    await waitFor(() => expect(calls.find((c) => c.path === '/api/messages')?.body).toEqual({
      text: "Fix the problems in this code: 2:9 TS2304 Cannot find name 'totl'.",
      context: [{ path: 'src/app.ts', startLine: 2, endLine: 2 }],
    }))
  })

  it('lists what changed since the last commit, opens a file as it was and is, stages, discards and commits', async () => {
    let status = { repository: true, branch: 'main', ahead: 1, behind: 0, files: [{ path: 'src/app.ts', staged: null as string | null, changed: 'M' as string | null, from: null }, { path: 'notes.txt', staged: null, changed: '?', from: null }] }
    let index = 'const total = 0\n'
    const { calls } = backend({
      'GET /api/git/status': () => ({ json: status }),
      'GET /api/git/log': () => ({ json: [{ hash: 'abc1234', subject: 'First', author: 'Pat', at: '2026-10-11T09:00:00Z' }] }),
      'GET /api/git/branches': () => ({ json: [{ name: 'main', current: true }, { name: 'feature', current: false }] }),
      'GET /api/git/show': (_, __, url) => {
        const rev = url.searchParams.get('rev')
        const text = rev === ':' ? index : rev === 'HEAD' ? 'const total = 0\n' : rev === 'abc1234' ? 'const total = 0\n' : 'const total = -1\n'
        return { json: { path: url.searchParams.get('path'), rev, commit: null, exists: true, size: text.length, text, binary: false, tooLarge: false } }
      },
      'GET /api/git/commit': () => ({ json: { hash: 'abc1234', parents: ['0000001'], subject: 'First', author: 'Pat', at: '2026-10-11T09:00:00Z', files: [{ path: 'src/app.ts', status: 'M', from: null }], truncated: false } }),
      'POST /api/git/stage-text': (body) => {
        index = (body as { text: string }).text
        status = { ...status, files: [{ ...status.files[0]!, staged: 'M' }, status.files[1]!] }
        return { json: status }
      },
      'POST /api/git/stage': () => {
        status = { ...status, files: [{ ...status.files[0]!, staged: 'M', changed: null }, status.files[1]!] }
        return { json: status }
      },
      'POST /api/git/discard': () => {
        status = { ...status, files: [status.files[0]!] }
        return { json: status }
      },
      'POST /api/git/commit': () => {
        status = { ...status, files: [] }
        return { json: { hash: 'def5678', status } }
      },
    })
    renderIde()
    await userEvent.keyboard('{Control>}{Shift>}g{/Shift}{/Control}')
    const panel = await screen.findByRole('complementary', { name: 'Source control' })
    const changes = await within(panel).findByRole('list', { name: 'Changed files' })
    expect(within(changes).getAllByRole('listitem')).toHaveLength(2)

    // A change not staged opens as the index has the file, and the file itself (live, editable).
    await userEvent.click(within(changes).getByRole('button', { name: /^app\.ts/ }))
    expect(await within(tabs()).findByRole('tab', { name: /app\.ts.*working tree/ })).toHaveAttribute('aria-selected', 'true')
    const diff = () => fakes.diffEditors[0]!
    await waitFor(() => expect(diff().getModel()?.original.getValue()).toBe('const total = 0\n'))
    expect(diff().getModel()?.modified.uri?.toString()).toBe('file:///src/app.ts')
    // Stage the change at the cursor (Monaco's first, line 1): the index takes that line, nothing else.
    await userEvent.click(screen.getByRole('button', { name: 'Stage the change at the cursor' }))
    await waitFor(() => expect(calls.find((c) => c.path === '/api/git/stage-text')?.body).toEqual({ path: 'src/app.ts', text: 'const total = 1\n' }))
    // A commit of the history lists its files; one opens its change (its parent ↔ it).
    await userEvent.click(within(panel).getByRole('button', { name: /First/ }))
    await userEvent.click(within(await within(panel).findByRole('list', { name: 'Files of abc1234' })).getByRole('button', { name: /^app\.ts/ }))
    expect(await within(tabs()).findByRole('tab', { name: 'app.ts (abc1234^) ↔ app.ts (abc1234)', selected: true })).toBeInTheDocument()
    await waitFor(() => expect(diff().getModel()?.original.getValue()).toBe('const total = -1\n'))
    expect(calls.some((c) => c.path === '/api/git/show?path=src%2Fapp.ts&rev=abc1234%5E')).toBe(true)

    await userEvent.click(within(changes).getByRole('button', { name: 'Stage src/app.ts' }))
    expect(await within(panel).findByRole('list', { name: 'Staged files' })).toHaveTextContent('app.ts')
    await userEvent.click(within(panel).getByRole('button', { name: 'Discard notes.txt' }))
    await userEvent.click(await screen.findByRole('button', { name: 'Discard' }))
    await waitFor(() => expect(calls.find((c) => c.path === '/api/git/discard')?.body).toEqual({ paths: ['notes.txt'] }))

    await userEvent.type(within(panel).getByRole('textbox', { name: 'Commit message' }), 'Total is one')
    await userEvent.click(within(panel).getByRole('button', { name: 'Commit 1 staged' }))
    await waitFor(() => expect(calls.find((c) => c.path === '/api/git/commit')?.body).toEqual({ message: 'Total is one', all: false }))
    expect(await within(panel).findByText('Nothing changed since the last commit.')).toBeInTheDocument()
    expect(within(panel).getByRole('list', { name: 'Commits' })).toHaveTextContent('First')
  })

  it('replaces what the search finds, in one file or in all of them after asking', async () => {
    const { calls } = backend({ 'POST /api/replace': () => ({ json: { files: [{ path: 'src/app.ts', replaced: 2, version: 'v2' }], count: 2, truncated: false } }) })
    renderIde()
    await userEvent.click(await screen.findByRole('button', { name: 'Search' }))
    await userEvent.type(screen.getByRole('textbox', { name: 'Search' }), 'total')
    await screen.findByRole('list', { name: 'Search results' })
    await userEvent.click(screen.getByRole('button', { name: 'Replace' }))
    await userEvent.type(screen.getByRole('textbox', { name: 'Replace with' }), 'sum')
    await userEvent.click(await screen.findByRole('button', { name: 'Replace in src/app.ts' }))
    await waitFor(() => expect(calls.find((c) => c.path === '/api/replace')?.body).toEqual({ q: 'total', case: false, word: false, regex: false, include: '', exclude: '', replacement: 'sum', paths: ['src/app.ts'] }))
    expect(await screen.findByText('Replaced 2 in 1 file.')).toBeInTheDocument()

    await userEvent.click(screen.getByRole('button', { name: 'Replace all' }))
    const ask = await screen.findByRole('alertdialog')
    await userEvent.click(within(ask).getByRole('button', { name: 'Replace all' }))
    await waitFor(() => expect(calls.filter((c) => c.path === '/api/replace')).toHaveLength(2))
    expect(calls.filter((c) => c.path === '/api/replace')[1]!.body).not.toHaveProperty('paths')
  })

  it('does not search the files again at the end of each turn', async () => {
    const { calls } = backend({ 'POST /api/messages': () => answerOnly() })
    renderIde()
    await userEvent.click(await screen.findByRole('button', { name: 'Search' }))
    await userEvent.type(screen.getByRole('textbox', { name: 'Search' }), 'total')
    await screen.findByRole('list', { name: 'Search results' })
    const searches = () => calls.filter((c) => c.path.startsWith('/api/search')).length
    const before = searches()

    await userEvent.type(screen.getByRole('textbox', { name: 'Message' }), 'Hello{Enter}')
    // Over: the saved session is read again, and the changes; the search is not.
    await waitFor(() => expect(calls.filter((c) => c.path === '/api/session').length).toBeGreaterThan(1))
    await waitFor(() => expect(calls.filter((c) => c.path === '/api/changes').length).toBeGreaterThan(1))
    await screen.findByRole('button', { name: 'Send' })
    expect(searches()).toBe(before)
  })

  it('opens any file with Ctrl+P by a few letters of its path', async () => {
    const { calls } = backend()
    renderIde()
    await screen.findByRole('tree', { name: 'Files' })
    await userEvent.keyboard('{Control>}p{/Control}')
    const box = await screen.findByPlaceholderText('Go to file: a few letters of its path (:12 for a line)')
    await userEvent.type(box, 'lcart')
    expect(await screen.findByRole('option', { name: /cart\.ts/ })).toBeInTheDocument()
    await userEvent.keyboard('{Enter}')
    await waitFor(() => expect(calls.some((c) => c.path === '/api/file?path=src%2Flib%2Fcart.ts')).toBe(true))
    expect(await within(tabs()).findByRole('tab', { name: 'cart.ts' })).toHaveAttribute('aria-selected', 'true')
  })

  it('opens as it was left in an earlier run, whatever its port: code-arena keeps the layout, and the theme in settings.json', async () => {
    const { calls, now } = backend({}, { layout: { view: 'search', chatOpen: false, side: 300, chat: 5000, unknown: 1 } }, { 'settings.json': '{\n  // mine\n  "window.autoDetectColorScheme": false,\n  "workbench.colorTheme": "Code Arena Dark"\n}\n' })
    renderIde()
    expect(await screen.findByRole('textbox', { name: 'Search' })).toBeInTheDocument()
    expect(screen.getByRole('complementary', { name: 'Search' })).toHaveStyle({ width: '300px' })
    expect(screen.queryByRole('region', { name: 'Chat' })).not.toBeInTheDocument()
    await waitFor(() => expect(document.documentElement).toHaveClass('dark'))
    const saves = () => calls.filter((c) => c.method === 'POST' && c.path === '/api/preferences')
    expect(saves()).toHaveLength(0)

    // A change is kept a moment later (the sizes within bounds); the theme in the person's settings.json at once, its comments kept.
    await userEvent.click(screen.getByRole('button', { name: 'Explorer' }))
    await waitFor(() => expect(now.preferences.layout).toEqual({ view: 'explorer', side: 300, chat: 960, panel: 280, sideOpen: true, chatOpen: false, panelOpen: false, panelTab: 'terminal' }))
    expect(saves()).toHaveLength(1)
    await userEvent.click(screen.getByRole('button', { name: 'Theme' }))
    await userEvent.click(await screen.findByRole('menuitemradio', { name: 'Light' }))
    await waitFor(() => expect(now.user['settings.json']).toBe('{\n  // mine\n  "window.autoDetectColorScheme": false,\n  "workbench.colorTheme": "Code Arena Light"\n}\n'))
    await waitFor(() => expect(document.documentElement).not.toHaveClass('dark'))
  })

  it('follows the light and dark theme in the editor', async () => {
    backend()
    renderIde()
    await openApp()
    expect(editor().options.theme).toBe('arena-light')
    await userEvent.click(screen.getByRole('button', { name: 'Theme' }))
    await userEvent.click(await screen.findByRole('menuitemradio', { name: 'Dark' }))
    await waitFor(() => expect(fakes.themes.at(-1)).toBe('arena-dark'))
  })

  it('compares as VS Code does: with the saved file, the clipboard (pasted when the browser refuses), another file; a file\'s side is live', async () => {
    const { calls } = backend()
    renderIde()
    await openApp()
    const diff = () => fakes.diffEditors[0]!
    act(() => editor().model!.setValue('const total = 5\n'))

    // Ctrl+K D: the file as saved on the left, as the editor holds it on the right (the same model: typing there is typing in the file).
    await userEvent.keyboard('{Control>}k{/Control}d')
    expect(await within(tabs()).findByRole('tab', { name: 'app.ts (Saved) ↔ app.ts, not saved', selected: true })).toBeInTheDocument()
    await waitFor(() => expect(diff().model?.original.getValue()).toBe('const total = 1\nconsole.log(total)\n'))
    const file = fakes.models.find((m) => m.uri?.toString() === 'file:///src/app.ts')!
    expect(diff().model?.modified).toBe(file)
    expect(diff().options).toMatchObject({ originalEditable: false, readOnly: false, renderMarginRevertIcon: true })
    // The bar: the counts Monaco works out, the next change, the sides swapped.
    expect(screen.getByTitle('1 change')).toHaveTextContent('+1 −1')
    await userEvent.click(screen.getByRole('button', { name: 'Next change' }))
    expect(diff().went).toEqual(['next'])
    // Ctrl+S in the compare saves the file.
    await userEvent.keyboard('{Control>}s{/Control}')
    await waitFor(() => expect(calls.find((c) => c.method === 'POST' && c.path === '/api/file')?.body).toEqual({ path: 'src/app.ts', text: 'const total = 5\n', version: 'v1' }))
    await userEvent.click(screen.getByRole('button', { name: 'Show inline' }))
    await waitFor(() => expect(calls.find((c) => c.method === 'POST' && c.path === '/api/user-files')?.body).toMatchObject({ name: 'settings.json', text: '{\n    "diffEditor.renderSideBySide": false\n}\n' }))
    await userEvent.click(screen.getByRole('button', { name: 'Swap the sides' }))
    expect(await within(tabs()).findByRole('tab', { name: 'app.ts ↔ app.ts (Saved)', selected: true })).toBeInTheDocument()
    expect(diff().model?.original).toBe(file)

    // The clipboard: refused by the browser, pasted instead.
    await userEvent.click(within(tabs()).getByRole('tab', { name: 'app.ts' }))
    const had = Object.getOwnPropertyDescriptor(navigator, 'clipboard')
    Object.defineProperty(navigator, 'clipboard', { configurable: true, value: { readText: () => Promise.reject(new Error('denied')), writeText: () => Promise.resolve() } })
    onTestFinished(() => {
      if (had) Object.defineProperty(navigator, 'clipboard', had)
      else delete (navigator as { clipboard?: unknown }).clipboard
    })
    await userEvent.keyboard('{Control>}k{/Control}c')
    const paste = await screen.findByRole('dialog', { name: 'Compare app.ts with the clipboard' })
    await userEvent.type(within(paste).getByRole('textbox', { name: 'Text to compare with' }), 'pasted')
    await userEvent.click(within(paste).getByRole('button', { name: 'Compare' }))
    expect(await within(tabs()).findByRole('tab', { name: 'Clipboard ↔ app.ts', selected: true })).toBeInTheDocument()
    await waitFor(() => expect(diff().model?.original.getValue()).toBe('pasted'))

    // Two files of the Explorer: both sides live, both editable.
    await userEvent.pointer({ keys: '[MouseRight]', target: screen.getByRole('treeitem', { name: 'README.md' }) })
    await userEvent.click(await screen.findByRole('menuitem', { name: 'Select for Compare' }))
    await userEvent.pointer({ keys: '[MouseRight]', target: screen.getByRole('treeitem', { name: 'app.ts, changed by the agent' }) })
    await userEvent.click(await screen.findByRole('menuitem', { name: 'Compare with Selected' }))
    expect(await within(tabs()).findByRole('tab', { name: 'README.md ↔ app.ts', selected: true })).toBeInTheDocument()
    await waitFor(() => expect(diff().model?.original.getValue()).toBe('# Shop\n'))
    expect(diff().options).toMatchObject({ originalEditable: true, readOnly: false })
    // Closing the compares leaves the file's tab, and its model, alone.
    await userEvent.keyboard('{Control>}{Alt>}w{/Alt}{/Control}')
    await userEvent.click(within(tabs()).getByRole('button', { name: 'Close Clipboard ↔ app.ts' }))
    await userEvent.click(within(tabs()).getByRole('button', { name: 'Close app.ts ↔ app.ts (Saved)' }))
    expect(within(tabs()).getAllByRole('tab').map((t) => t.textContent)).toEqual(['app.ts'])
    expect(file.disposed).toBe(false)
    expect(fakes.models.find((m) => m.uri?.toString() === 'file:///README.md')?.disposed).toBe(true)
  })

  it('debugs as VS Code does: a breakpoint (F9), F5, the call stack, variables and a watch, a step (F10), and the program ending', async () => {
    const started = { id: 'd1', name: 'Run app', adapter: 'gdb', adapterId: 'cppdbg', configuration: { type: 'gdb', request: 'launch', program: '/home/ada/shop/app' }, notes: [], root: '/home/ada/shop', rootReal: '/home/ada/shop', pathStyle: 'posix' }
    let kept: Record<string, unknown> = {}
    const { calls } = backend({
      'GET /api/run/configs': () => ({ json: { configurations: [{ name: 'Run app', type: 'cppdbg', request: 'launch', source: 'launch', supported: true, command: '/home/ada/shop/app' }], compounds: [], errors: [], trusted: true, buildConfiguration: 'Debug' } }),
      'GET /api/debug/adapters': () => ({ json: [{ id: 'gdb', name: 'gdb', types: ['cppdbg', 'gdb'], found: true, path: '/usr/bin/gdb', version: '17.1' }] }),
      'GET /api/debug/sessions': () => ({ json: [] }),
      'GET /api/workspace/state': () => ({ json: kept }),
      'POST /api/workspace/state': (body) => {
        kept = { ...kept, ...(body as Record<string, unknown>) }
        return { json: kept }
      },
      'POST /api/run/start': () => ({ json: started }),
      'POST /api/debug/stop': () => ({ status: 204 }),
      'POST /api/terminals': (body) => ({ json: { id: '9', kind: 'debuggee', title: (body as { title: string }).title, pid: 4321, cols: 120, rows: 30, exitCode: null } }),
    })
    renderIde()
    await openApp()
    // F9: a breakpoint on the cursor's line, kept for the folder.
    await userEvent.keyboard('{F9}')
    await waitFor(() => expect(kept.breakpoints).toEqual({ 'src/app.ts': [{ line: 1, enabled: true }] }))
    // And one turned off on line 2, with a condition.
    act(() => debug.setBreakpoints('src/app.ts', [...debug.breakpoints['src/app.ts']!, { line: 2, enabled: false, condition: 'total > 9' }]))

    // F5: the configuration started with its debugger; the page drives the adapter as VS Code does.
    await userEvent.keyboard('{F5}')
    await waitFor(() => expect(calls.find((c) => c.path === '/api/run/start')?.body).toMatchObject({ name: 'Run app', noDebug: false, context: { file: 'src/app.ts', lineNumber: 1 } }))
    const dap = () => fakes.sockets.find((x) => x.url.endsWith('/api/debug/socket?id=d1'))!
    await waitFor(() => expect(dap()).toBeTruthy())
    act(() => dap().open())
    const answered = new Set<number>()
    const requests = () => dap().sent.map((x) => JSON.parse(x as string) as { seq: number; command: string; arguments?: Record<string, unknown> })
    // Each request of a command the page has sent and not had answered, answered alike (the view and the inline values ask the same).
    const answer = async (command: string, body: unknown = {}) => {
      let reqs: { seq: number; arguments?: Record<string, unknown> }[] = []
      await waitFor(() => {
        reqs = requests().filter((r) => r.command === command && !answered.has(r.seq))
        expect(reqs.length, `the page asks ${command}`).toBeGreaterThan(0)
      })
      for (const req of reqs) {
        answered.add(req.seq)
        act(() => dap().receive(JSON.stringify({ seq: 0, type: 'response', request_seq: req.seq, success: true, command, body })))
      }
      return reqs[0]!.arguments
    }
    const event = (name: string, body: unknown = {}) => act(() => dap().receive(JSON.stringify({ seq: 0, type: 'event', event: name, body })))
    expect(await answer('initialize', { supportsConfigurationDoneRequest: true })).toMatchObject({ adapterID: 'cppdbg', linesStartAt1: true })
    await waitFor(() => expect(requests().map((r) => r.command)).toContain('launch'))
    event('initialized')
    expect(await answer('setBreakpoints', { breakpoints: [{ verified: true, line: 1 }] })).toMatchObject({ source: { path: '/home/ada/shop/src/app.ts' }, breakpoints: [{ line: 1 }] })
    await answer('configurationDone')
    // The adapter asks for its program in a terminal (runInTerminal): it opens, and the adapter is told its process.
    act(() => dap().receive(JSON.stringify({ seq: 90, type: 'request', command: 'runInTerminal', arguments: { kind: 'integrated', title: 'Debuggee', args: ['/home/ada/shop/app', '--x'], cwd: '/home/ada/shop' } })))
    await waitFor(() => expect(calls.find((c) => c.method === 'POST' && c.path === '/api/terminals')?.body).toMatchObject({ program: '/home/ada/shop/app', args: ['--x'], cwd: '/home/ada/shop', title: 'Debuggee' }))
    await waitFor(() => expect(requests().length).toBeGreaterThan(0))
    await waitFor(() => expect(dap().sent.map((x) => JSON.parse(x as string) as { type: string; request_seq?: number; body?: unknown })).toContainEqual(expect.objectContaining({ type: 'response', request_seq: 90, success: true, body: { processId: 4321 } })))
    await answer('launch')
    // Stopped at the breakpoint: the call stack, the line in the editor, the variables, a watch.
    event('stopped', { reason: 'breakpoint', threadId: 1 })
    await answer('threads', { threads: [{ id: 1, name: 'main' }] })
    await answer('stackTrace', { stackFrames: [{ id: 7, name: 'main', line: 1, column: 1, source: { path: '/home/ada/shop/src/app.ts' } }] })
    const view = await screen.findByRole('complementary', { name: 'Run and Debug' })
    expect(await within(view).findByRole('button', { name: /main.*app\.ts:1/ })).toBeInTheDocument()
    expect(within(screen.getByRole('toolbar', { name: 'Debug' })).getByRole('button', { name: 'Continue' })).toBeInTheDocument()
    expect(await answer('scopes', { scopes: [{ name: 'Locals', variablesReference: 5 }] })).toEqual({ frameId: 7 })
    await answer('variables', { variables: [{ name: 'total', value: '1', type: 'int', variablesReference: 0 }] })
    expect(await within(within(view).getByRole('list', { name: 'Variables' })).findByText('total')).toBeInTheDocument()
    await userEvent.click(within(view).getByRole('button', { name: '+ Add expression' }))
    await userEvent.type(within(view).getByRole('textbox', { name: 'Expression to watch' }), 'total + 1{Enter}')
    expect(await answer('evaluate', { result: '2', variablesReference: 0 })).toMatchObject({ expression: 'total + 1', frameId: 7, context: 'watch' })
    expect(await within(within(view).getByRole('list', { name: 'Watch expressions' })).findByText('2')).toBeInTheDocument()
    await waitFor(() => expect(kept.watch).toEqual(['total + 1']))
    // Run to Cursor: a breakpoint there for this run only (where the one turned off is), then continue.
    editor().selection = { startLineNumber: 2, startColumn: 1, endLineNumber: 2, endColumn: 1 }
    act(() => editor().actions.get('editor.debug.action.runToCursor')!.run())
    expect(await answer('setBreakpoints', { breakpoints: [{ verified: true, line: 1 }, { verified: true, line: 2 }] })).toMatchObject({ breakpoints: [{ line: 1 }, { line: 2 }] })
    await answer('continue')
    event('stopped', { reason: 'breakpoint', threadId: 1 })
    await answer('threads', { threads: [{ id: 1, name: 'main' }] })
    await answer('stackTrace', { stackFrames: [{ id: 8, name: 'main', line: 2, column: 1, source: { path: '/home/ada/shop/src/app.ts' } }] })
    // Stopped there, the breakpoint goes, and the person's own comes back as it was; the folder never kept Run to Cursor's.
    expect(await answer('setBreakpoints', { breakpoints: [{ verified: true, line: 1 }] })).toMatchObject({ breakpoints: [{ line: 1 }] })
    await waitFor(() => expect(debug.breakpoints['src/app.ts']).toEqual([{ line: 1, enabled: true }, { line: 2, enabled: false, condition: 'total > 9' }]))
    await waitFor(() => expect(kept.breakpoints).toEqual({ 'src/app.ts': [{ line: 1, enabled: true }, { line: 2, enabled: false, condition: 'total > 9' }] }))
    await answer('scopes', { scopes: [] })
    await answer('evaluate', { result: '2', variablesReference: 0 })
    // The Debug Console evaluates in the frame; what it gave stays when the panel shows another tab.
    await userEvent.click(screen.getByRole('tab', { name: 'Debug Console' }))
    await userEvent.type(within(screen.getByRole('region', { name: 'Debug Console' })).getByRole('textbox', { name: 'Expression to evaluate' }), 'total * 10{Enter}')
    expect(await answer('evaluate', { result: '20', variablesReference: 0 })).toMatchObject({ expression: 'total * 10', context: 'watch' })
    expect(await within(screen.getByRole('region', { name: 'Debug Console' })).findByText('20')).toBeInTheDocument()
    await userEvent.click(screen.getByRole('tab', { name: 'Terminal' }))
    await userEvent.click(screen.getByRole('tab', { name: 'Debug Console' }))
    expect(within(screen.getByRole('region', { name: 'Debug Console' })).getByText('> total * 10')).toBeInTheDocument()
    expect(within(screen.getByRole('region', { name: 'Debug Console' })).getByText('20')).toBeInTheDocument()
    // F10 steps over. This adapter says it stopped before it answers the step: the program is stopped, not running.
    await userEvent.click(view)
    await userEvent.keyboard('{F10}')
    await waitFor(() => expect(requests().some((r) => r.command === 'next' && !answered.has(r.seq))).toBe(true))
    event('stopped', { reason: 'step', threadId: 1 })
    await answer('threads', { threads: [{ id: 1, name: 'main' }] })
    await answer('stackTrace', { stackFrames: [{ id: 9, name: 'main', line: 2, column: 1, source: { path: '/home/ada/shop/src/app.ts' } }] })
    expect(await answer('next')).toEqual({ threadId: 1 })
    await act(async () => {})
    expect(within(screen.getByRole('toolbar', { name: 'Debug' })).getByRole('button', { name: 'Continue' })).toBeInTheDocument()
    // F5 continues: running.
    await userEvent.click(view)
    await userEvent.keyboard('{F5}')
    expect(await answer('continue')).toEqual({ threadId: 1 })
    await waitFor(() => expect(within(screen.getByRole('toolbar', { name: 'Debug' })).getByRole('button', { name: 'Pause' })).toBeInTheDocument())
    // The program ends by itself: the adapter is let go (code-arena stops it), the toolbar goes, the output stays.
    event('output', { category: 'stdout', output: 'total 4\n' })
    event('exited', { exitCode: 0 })
    event('terminated')
    await waitFor(() => expect(calls.find((c) => c.path === '/api/debug/stop')?.body).toEqual({ id: 'd1' }))
    await waitFor(() => expect(screen.queryByRole('toolbar', { name: 'Debug' })).not.toBeInTheDocument())
    await userEvent.click(screen.getByRole('tab', { name: 'Debug Console' }))
    expect(await within(screen.getByRole('region', { name: 'Debug Console' })).findByText('total 4')).toBeInTheDocument()
  })

  it("starts a compound as VS Code does: its preLaunchTask first, then its configurations together, and stopAll stops them all", async () => {
    let kept: Record<string, unknown> = {}
    const { calls } = backend({
      'GET /api/run/configs': () => ({
        json: {
          configurations: [
            { name: 'Run app', type: 'cppdbg', request: 'launch', source: 'launch', supported: true, command: './app' },
            { name: 'Serve', type: 'cppdbg', request: 'launch', source: 'launch', supported: true, command: './server' },
          ],
          compounds: [{ name: 'Both', configurations: ['Run app', 'Serve'], preLaunchTask: 'build', stopAll: true }],
          errors: [],
          trusted: true,
          buildConfiguration: 'Debug',
        },
      }),
      'GET /api/debug/adapters': () => ({ json: [{ id: 'gdb', name: 'gdb', types: ['cppdbg', 'gdb'], found: true }] }),
      'GET /api/debug/sessions': () => ({ json: [] }),
      'GET /api/workspace/state': () => ({ json: kept }),
      'POST /api/workspace/state': (body) => {
        kept = { ...kept, ...(body as Record<string, unknown>) }
        return { json: kept }
      },
      'POST /api/tasks/run': () => ({ json: { task: 'build', terminal: null, terminals: [] } }),
      'POST /api/run/start': (body) => {
        const name = (body as { name: string }).name
        return { json: { id: name === 'Serve' ? 'd2' : 'd1', name, adapter: 'gdb', adapterId: 'cppdbg', configuration: { type: 'gdb', request: 'launch' }, notes: [], root: '/home/ada/shop', rootReal: '/home/ada/shop', pathStyle: 'posix' } }
      },
      'POST /api/debug/stop': () => ({ status: 204 }),
    })
    renderIde()
    await openApp()
    await userEvent.click(screen.getByRole('button', { name: 'Run and Debug' }))
    const view = await screen.findByRole('complementary', { name: 'Run and Debug' })
    // Chosen in Run and Debug: F5 starts it at once.
    await userEvent.selectOptions(await within(view).findByRole('combobox', { name: 'Run configuration' }), 'Both')
    expect(within(view).getByText('Starts Run app, Serve after build')).toBeInTheDocument()
    await userEvent.click(view)
    await userEvent.keyboard('{F5}')
    await waitFor(() => expect(calls.find((c) => c.path === '/api/tasks/run')?.body).toMatchObject({ label: 'build', wait: true }))
    await waitFor(() => expect(calls.filter((c) => c.path === '/api/run/start').map((c) => (c.body as { name: string }).name).sort()).toEqual(['Run app', 'Serve']))
    expect(calls.findIndex((c) => c.path === '/api/tasks/run')).toBeLessThan(calls.findIndex((c) => c.path === '/api/run/start'))
    await waitFor(() => expect(debug.sessions.map((x) => x.id).sort()).toEqual(['d1', 'd2']))
    await waitFor(() => expect(kept['launch.selected']).toBe('Both'))
    // Stopping one stops both.
    await act(() => debug.stop(debug.sessions[0]!))
    await waitFor(() => expect(calls.filter((c) => c.path === '/api/debug/stop').map((c) => (c.body as { id: string }).id).sort()).toEqual(['d1', 'd2']))
    expect(debug.sessions).toEqual([])
  })

  it('runs a configuration without debugging (Ctrl+F5) in a terminal of its own, shown in the panel, and a build task (Ctrl+Shift+B)', async () => {
    const run = { id: '7', kind: 'run', title: 'Run app', pid: 70, cols: 80, rows: 24, exitCode: null }
    const build = { id: '8', kind: 'task', title: 'build', pid: 80, cols: 80, rows: 24, exitCode: null }
    const terms: TerminalInfo[] = []
    const { calls } = backend({
      'GET /api/terminals': () => ({ json: terms }),
      'GET /api/run/configs': () => ({ json: { configurations: [{ name: 'Run app', type: 'cppdbg', request: 'launch', source: 'launch', supported: true, command: './app' }], compounds: [], errors: [], trusted: true, buildConfiguration: 'Debug' } }),
      'GET /api/tasks': () => ({ json: { tasks: [{ label: 'build', source: 'workspace', group: 'build', isDefault: true, command: 'make' }], errors: [], trusted: true, buildConfiguration: 'Debug' } }),
      'POST /api/run/start': () => {
        terms.push(run)
        return { json: { terminal: run, configuration: {}, command: './app' } }
      },
      'POST /api/tasks/run': () => {
        terms.push(build)
        return { json: { task: 'build', terminal: build, terminals: [build] } }
      },
    })
    renderIde()
    await screen.findByRole('tree', { name: 'Files' })
    await userEvent.keyboard('{Control>}{F5}{/Control}')
    await waitFor(() => expect(calls.find((c) => c.path === '/api/run/start')?.body).toMatchObject({ name: 'Run app', noDebug: true }))
    const panel = await screen.findByRole('region', { name: 'Terminal' })
    expect(await within(panel).findByRole('tab', { name: /Run app/, selected: true })).toBeInTheDocument()
    await userEvent.keyboard('{Control>}{Shift>}b{/Shift}{/Control}')
    await waitFor(() => expect(calls.find((c) => c.path === '/api/tasks/run')?.body).toMatchObject({ group: 'build' }))
    expect(await within(panel).findByRole('tab', { name: /build/, selected: true })).toBeInTheDocument()
  })

  it("asks a configuration's inputs before it starts: a password one typed as dots, never shown", async () => {
    const run = { id: '7', kind: 'run', title: 'Deploy', pid: 70, cols: 80, rows: 24, exitCode: null }
    const { calls } = backend({
      'GET /api/run/configs': () => ({ json: { configurations: [{ name: 'Deploy', type: 'node', request: 'launch', source: 'launch', supported: true, command: 'node deploy.js' }], compounds: [], errors: [], trusted: true, buildConfiguration: 'Debug' } }),
      'POST /api/run/start': (body) =>
        (body as { inputs?: Record<string, string> }).inputs?.token
          ? { json: { terminal: run, configuration: {}, command: 'node deploy.js' } }
          : { status: 409, json: { status: 'needs_input', error: 'Deploy needs its inputs.', inputs: [{ id: 'token', type: 'promptString', description: 'The deploy token', password: true }] } },
    })
    renderIde()
    await screen.findByRole('tree', { name: 'Files' })
    await userEvent.keyboard('{Control>}{F5}{/Control}')
    const ask = await screen.findByRole('dialog', { name: 'The deploy token' })
    const box = within(ask).getByLabelText('The deploy token')
    expect(box).toHaveAttribute('type', 'password')
    await userEvent.type(box, 's3cret')
    expect(ask).not.toHaveTextContent('s3cret')
    await userEvent.keyboard('{Enter}')
    await waitFor(() => expect(calls.filter((c) => c.path === '/api/run/start').at(-1)?.body).toMatchObject({ name: 'Deploy', inputs: { token: 's3cret' } }))
  })

  it("attaches to a process the person picks from their own, as VS Code's pickProcess", async () => {
    const { calls } = backend({
      'GET /api/run/configs': () => ({ json: { configurations: [{ name: 'Attach', type: 'cppdbg', request: 'attach', source: 'launch', supported: false, reason: 'attaching needs the debugger.' }], compounds: [], errors: [], trusted: true, buildConfiguration: 'Debug' } }),
      'GET /api/debug/adapters': () => ({ json: [{ id: 'gdb', name: 'gdb', types: ['cppdbg'], found: true }] }),
      'GET /api/processes': () => ({ json: [{ pid: 4242, name: 'server', command: '/home/ada/shop/server --port 8080' }, { pid: 77, name: 'bash', command: 'bash' }] }),
      'POST /api/run/start': (body) =>
        (body as { inputs?: Record<string, string> }).inputs?.['command:pickProcess']
          ? { status: 409, json: { status: 'no_adapter', error: 'stop here', adapter: 'gdb' } }
          : { status: 409, json: { status: 'needs_input', error: 'Pick the process.', inputs: [{ id: 'command:pickProcess', type: 'pickProcess', description: 'The process to attach to' }] } },
    })
    renderIde()
    await screen.findByRole('tree', { name: 'Files' })
    await userEvent.keyboard('{F5}')
    const pick = await screen.findByRole('dialog', { name: 'Attach to a process' })
    await userEvent.type(within(pick).getByRole('combobox'), 'port')
    await userEvent.click(within(pick).getByRole('option', { name: /server/ }))
    await waitFor(() => expect(calls.filter((c) => c.path === '/api/run/start').at(-1)?.body).toMatchObject({ name: 'Attach', noDebug: false, inputs: { 'command:pickProcess': '4242' } }))
  })

  it("takes VS Code's keys, chords too, and the person's keybindings.json over them", async () => {
    const keybindings = '[\n  // mine\n  { "key": "ctrl+shift+e", "command": "workbench.action.togglePanel" },\n  { "key": "ctrl+b", "command": "-workbench.action.toggleSidebarVisibility" }\n]'
    const { calls } = backend({}, {}, { 'keybindings.json': keybindings })
    renderIde()
    await screen.findByRole('tree', { name: 'Files' })
    // Ctrl+J: the panel; Ctrl+Alt+B: the chat.
    await userEvent.keyboard('{Control>}j{/Control}')
    await waitFor(() => expect(calls.some((c) => c.method === 'POST' && c.path === '/api/terminals')).toBe(true))
    expect(screen.getByRole('region', { name: 'Chat' })).toBeInTheDocument()
    await userEvent.keyboard('{Control>}{Alt>}b{/Alt}{/Control}')
    await waitFor(() => expect(screen.queryByRole('region', { name: 'Chat' })).not.toBeInTheDocument())
    // keybindings.json: Ctrl+Shift+E is the panel's now, and Ctrl+B is nobody's.
    expect(screen.getByRole('button', { name: 'Terminal' })).toHaveAttribute('aria-pressed', 'true')
    await userEvent.keyboard('{Control>}{Shift>}e{/Shift}{/Control}')
    await waitFor(() => expect(screen.getByRole('button', { name: 'Terminal' })).toHaveAttribute('aria-pressed', 'false'))
    await userEvent.keyboard('{Control>}b{/Control}')
    expect(screen.getByRole('complementary', { name: 'Explorer' })).toBeVisible()
    // A chord: Ctrl+K waits (the status bar says so), then Ctrl+S opens the keyboard shortcuts.
    await userEvent.keyboard('{Control>}k{/Control}')
    expect(within(screen.getByRole('contentinfo', { name: 'Status bar' })).getByRole('status')).toHaveTextContent('(Ctrl+K) was pressed. Waiting for the second key of the chord…')
    await userEvent.keyboard('{Control>}s{/Control}')
    expect(await within(tabs()).findByRole('tab', { name: 'Keyboard Shortcuts' })).toHaveAttribute('aria-selected', 'true')
    await userEvent.click(await screen.findByRole('button', { name: 'Open Keyboard Shortcuts (JSON)' }))
    expect(await within(tabs()).findByRole('tab', { name: /keybindings\.json/ })).toHaveAttribute('aria-selected', 'true')
    await waitFor(() => expect(editor().model?.value).toBe(keybindings))
    expect(editor().model?.uri?.toString()).toBe('vscode-userdata:///User/keybindings.json')
    await userEvent.keyboard('{Control>}k{/Control}x')
    expect(within(screen.getByRole('contentinfo', { name: 'Status bar' })).getByRole('status')).toHaveTextContent('The key combination (Ctrl+K, X) is not a command.')
  })

  it('has a Settings editor as VS Code\'s: searched, written into the person\'s or the project\'s settings.json, a language\'s own with @lang', async () => {
    const { calls, now } = backend({}, {}, { 'settings.json': '{\n  // mine\n  "editor.fontSize": 15\n}\n' })
    renderIde()
    await screen.findByRole('tree', { name: 'Files' })
    await userEvent.keyboard('{Control>},{/Control}')
    const page = await screen.findByRole('region', { name: 'Settings' })
    const search = within(page).getByRole('searchbox', { name: 'Search settings' })
    await userEvent.type(search, 'tab size')
    const size = await within(page).findByLabelText(/Tab Size/)
    await userEvent.clear(size)
    await userEvent.type(size, '2{Enter}')
    await waitFor(() => expect(now.user['settings.json']).toBe('{\n  // mine\n  "editor.fontSize": 15,\n  "editor.tabSize": 2\n}\n'))
    // A setting set is marked, and reset from its menu.
    await userEvent.clear(search)
    await userEvent.type(search, '@modified')
    expect(within(page).getAllByText('(modified)')).toHaveLength(2)
    // @lang: the language's own block.
    await userEvent.clear(search)
    await userEvent.type(search, '@lang:python format on save')
    await userEvent.click(await within(page).findByRole('checkbox', { name: /Formats a file as you save it/ }))
    await waitFor(() => expect(now.user['settings.json']).toContain('"[python]": {\n    "editor.formatOnSave": true\n  }'))
    // The project's: .vscode/settings.json.
    await userEvent.click(within(page).getByRole('tab', { name: 'Workspace' }))
    await userEvent.clear(search)
    await userEvent.type(search, 'word wrap column')
    const column = await within(page).findByLabelText(/Word Wrap Column/)
    await userEvent.clear(column)
    await userEvent.type(column, '100{Enter}')
    await waitFor(() => expect(calls.find((c) => c.method === 'POST' && c.path === '/api/file')?.body).toEqual({ path: '.vscode/settings.json', text: '{\n    "editor.wordWrapColumn": 100\n}\n', version: 'none' }))
  })

  it('has a Keyboard Shortcuts editor as VS Code\'s: a default changed or removed is written into keybindings.json', async () => {
    const { now } = backend()
    renderIde()
    await screen.findByRole('tree', { name: 'Files' })
    await userEvent.keyboard('{Control>}k{/Control}{Control>}s{/Control}')
    const page = await screen.findByRole('region', { name: 'Keyboard Shortcuts' })
    await userEvent.type(within(page).getByRole('searchbox', { name: 'Search keybindings' }), 'toggle panel')
    await userEvent.dblClick(await within(page).findByRole('button', { name: /Toggle Panel Visibility: Ctrl\+J/ }))
    const dialog = await screen.findByRole('dialog', { name: /Keys for View: Toggle Panel Visibility/ })
    await userEvent.keyboard('{Control>}{Alt>}j{/Alt}{/Control}')
    expect(within(dialog).getByRole('textbox', { name: 'Press the keys' })).toHaveValue('Ctrl+Alt+J')
    await userEvent.keyboard('{Enter}')
    await waitFor(() => expect(JSON.parse(now.user['keybindings.json']!)).toEqual([
      { key: 'ctrl+j', command: '-workbench.action.togglePanel' },
      { key: 'ctrl+alt+j', command: 'workbench.action.togglePanel' },
    ]))
    // Applied at once: the new key toggles the panel.
    await userEvent.click(page)
    await userEvent.keyboard('{Control>}{Alt>}j{/Alt}{/Control}')
    await waitFor(() => expect(screen.getByRole('button', { name: 'Terminal' })).toHaveAttribute('aria-pressed', 'true'))
    // Record Keys: search by pressing them.
    await userEvent.click(within(page).getByRole('button', { name: /Record Keys/ }))
    await userEvent.click(within(page).getByRole('searchbox', { name: 'Search keybindings' }))
    await userEvent.keyboard('{Control>}b{/Control}')
    expect(within(page).getByRole('button', { name: /Toggle Primary Side Bar Visibility: Ctrl\+B/ })).toBeInTheDocument()
    expect(within(page).queryByRole('button', { name: /Toggle Panel Visibility/ })).not.toBeInTheDocument()
  })

  it("formats with the project's own formatter on save once the folder is trusted, which Format Document asks for, and saves without formatting when asked", async () => {
    let available = false
    const { calls } = backend(
      {
        'GET /api/formatters': () => ({
          json: { language: 'typescript', default: available ? 'prettier' : null, formatters: [{ id: 'prettier', name: 'Prettier', version: '3.3.3', source: 'project', available, range: true, path: 'node_modules/prettier/bin/prettier.cjs', ...(available ? {} : { reason: 'trust', hint: 'Trust this folder to run the formatter it holds.' }) }] },
        }),
        'POST /api/format': (body) => ({ json: { text: (body as { text: string }).text.replace('total=2', 'total = 2'), formatter: 'prettier' } }),
        'POST /api/workspace/trust': () => {
          available = true
          return { json: { trust: 'trusted' } }
        },
      },
      {},
      { 'settings.json': '{ "editor.formatOnSave": true, "editor.defaultFormatter": "esbenp.prettier-vscode" }' },
    )
    renderIde()
    await openApp()
    // The status bar names the formatter; Monaco is given it for the language.
    expect(await screen.findByRole('button', { name: /Formatter Prettier, cannot run here/ })).toBeInTheDocument()
    await waitFor(() => expect(fakes.formatting.has('typescript')).toBe(true))
    const saves = () => calls.filter((c) => c.method === 'POST' && c.path === '/api/file').map((c) => (c.body as { text: string }).text)
    // Prettier is the project's (node_modules) and the folder is not trusted: a save never asks, it saves as typed.
    act(() => editor().model!.setValue('const total=2\n'))
    await userEvent.keyboard('{Control>}s{/Control}')
    await waitFor(() => expect(saves()).toEqual(['const total=2\n']))
    expect(screen.queryByRole('dialog', { name: /Trust the folder/ })).not.toBeInTheDocument()
    expect(calls.some((c) => c.path === '/api/format')).toBe(false)

    // Format Document With… asks to trust the folder first, then formats.
    await userEvent.keyboard('{Control>}{Shift>}p{/Shift}{/Control}')
    await userEvent.type(within(await screen.findByRole('dialog', { name: 'Commands' })).getByPlaceholderText('Run a command: type a few words of it'), 'format document with')
    await userEvent.keyboard('{Enter}')
    await userEvent.click(await screen.findByRole('option', { name: /^Prettier/ }))
    const trust = await screen.findByRole('dialog', { name: /Trust the folder shop\?/ })
    expect(trust).toHaveTextContent('node_modules/prettier/bin/prettier.cjs')
    await userEvent.click(within(trust).getByRole('button', { name: 'Trust the folder' }))
    await waitFor(() => expect(editor().model!.getValue()).toBe('const total = 2\n'))
    expect(calls.find((c) => c.path === '/api/format')?.body).toMatchObject({ path: 'src/app.ts', text: 'const total=2\n', formatter: 'prettier', language: 'typescript' })

    // Trusted now: a save formats.
    act(() => editor().model!.setValue('const total=2\n'))
    await userEvent.keyboard('{Control>}s{/Control}')
    await waitFor(() => expect(saves().at(-1)).toBe('const total = 2\n'))

    // Save without Formatting (Ctrl+K Ctrl+Shift+S): as typed.
    act(() => editor().model!.setValue('const total=3\n'))
    const formatted = calls.filter((c) => c.path === '/api/format').length
    await userEvent.keyboard('{Control>}k{/Control}{Control>}{Shift>}s{/Shift}{/Control}')
    await waitFor(() => expect(saves().at(-1)).toBe('const total=3\n'))
    expect(calls.filter((c) => c.path === '/api/format')).toHaveLength(formatted)
  })

  it("applies settings.json to the editor: the person's, the project's over them, a language's own, and Alt+Z", async () => {
    const { disk } = backend({}, {}, { 'settings.json': '{ "editor.fontSize": 16, "editor.wordWrap": "on", "editor.detectIndentation": false, "[typescript]": { "editor.tabSize": 2 } }' })
    disk['.vscode/settings.json'] = { text: '{ "editor.fontSize": 18 }', version: 'w1' }
    renderIde()
    await openApp()
    expect(editor().options).toMatchObject({ fontSize: 18, wordWrap: 'on' })
    expect(editor().model!.options).toMatchObject({ tabSize: 2, insertSpaces: true })
    await userEvent.keyboard('{Alt>}z{/Alt}')
    expect(editor().options.wordWrap).toBe('off')
  })

  it('saves as VS Code does: trailing whitespace and the final newline as set, Save All, and auto save after a delay', async () => {
    const { calls } = backend({}, {}, { 'settings.json': '{ "files.trimTrailingWhitespace": true, "files.insertFinalNewline": true }' })
    renderIde()
    await openApp()
    const saved = () => calls.filter((c) => c.method === 'POST' && c.path === '/api/file').map((c) => (c.body as { path: string; text: string }))
    act(() => editor().model!.setValue('const total = 2   \nconsole.log(total)'))
    await userEvent.keyboard('{Control>}s{/Control}')
    await waitFor(() => expect(saved().at(-1)).toMatchObject({ path: 'src/app.ts', text: 'const total = 2\nconsole.log(total)\n' }))
    expect(editor().model!.getValue()).toBe('const total = 2\nconsole.log(total)\n')

    // Save All (Ctrl+K S): every tab with unsaved changes.
    await userEvent.click(screen.getByRole('treeitem', { name: 'README.md' }))
    await waitFor(() => expect(editor().model?.value).toBe('# Shop\n'))
    act(() => editor().model!.setValue('# Shop!\n'))
    act(() => fakes.models.find((m) => m.uri?.toString() === 'file:///src/app.ts')!.setValue('const total = 3\n'))
    await userEvent.keyboard('{Control>}k{/Control}s')
    await waitFor(() => expect(saved().slice(-2).map((b) => b.path).sort()).toEqual(['README.md', 'src/app.ts']))
  })

  it('saves by itself a moment after the last change with "files.autoSave": "afterDelay", without the save\'s steps', async () => {
    const { calls } = backend({}, {}, { 'settings.json': '{ "files.autoSave": "afterDelay", "files.autoSaveDelay": 30, "files.trimTrailingWhitespace": true }' })
    renderIde()
    await openApp()
    act(() => editor().model!.setValue('typing  '))
    await waitFor(() => expect(calls.find((c) => c.method === 'POST' && c.path === '/api/file')?.body).toEqual({ path: 'src/app.ts', text: 'typing  ', version: 'v1' }))
    expect(within(tabs()).getByRole('tab', { name: 'app.ts' })).toBeInTheDocument()
  })

  it("installs a VS Code extension from its .vsix and applies what it declares: its theme (shown as it is chosen), snippets, a language, a key", async () => {
    // code-arena as it answers once the .vsix is kept: the extension, what it brings, and the files its contributions name.
    const owned = { extension: 'acme.night', version: '1.2.0' }
    const night = {
      id: 'acme.night', name: 'night', displayName: 'Night', version: '1.2.0', publisher: 'Acme', description: 'A dark theme.', icon: null, enabled: true, source: 'vsix',
      contributes: {
        themes: [{ label: 'Night Sky', uiTheme: 'vs-dark', path: 'themes/night.json', id: null }],
        iconThemes: [],
        snippets: [{ language: 'typescript', path: 'snippets/ts.json' }],
        languages: [{ id: 'toml', extensions: ['.toml'], aliases: ['TOML'], configuration: 'toml.language-configuration.json' }],
        grammars: [],
        keybindings: 1,
        configurationDefaults: {},
      },
      unused: ["its code (main: ./out/extension.js): Code Arena runs no extension's code"],
    }
    const files: Record<string, unknown> = {
      'themes/night.json': { include: './base.json', colors: { 'editor.background': '#101020', 'sideBar.background': '#0a0a18' }, tokenColors: [{ scope: 'comment', settings: { foreground: '#808080', fontStyle: 'italic' } }, { scope: 'constant.numeric', settings: { foreground: '#f0a' } }] },
      'themes/base.json': { colors: { 'editor.foreground': '#e0e0e0' } },
      'snippets/ts.json': { 'Log it': { prefix: 'log', body: ['console.log($1)', '$0'], description: 'Logs a value' } },
      'toml.language-configuration.json': { comments: { lineComment: '#' }, brackets: [['[', ']']] },
    }
    let installed: typeof night[] = []
    const { calls, now } = backend({
      'GET /api/extensions': () => ({ json: installed }),
      'GET /api/extensions/contributions': () => ({
        json: installed.some((e) => e.enabled)
          ? {
              themes: [{ ...night.contributes.themes[0], ...owned }],
              iconThemes: [],
              snippets: [{ ...night.contributes.snippets[0], ...owned }],
              languages: [{ ...night.contributes.languages[0], ...owned }],
              keybindings: [{ command: 'workbench.action.togglePanel', key: 'ctrl+alt+l', ...owned }, { command: 'night.own', key: 'ctrl+alt+p', ...owned }],
              configurationDefaults: [],
            }
          : { themes: [], iconThemes: [], snippets: [], languages: [], keybindings: [], configurationDefaults: [] },
      }),
      'POST /api/extensions/install': () => {
        installed = [night]
        return { json: night }
      },
      'POST /api/extensions/enable': (body) => {
        const b = body as { id: string; enabled: boolean }
        installed = installed.map((e) => (e.id === b.id ? { ...e, enabled: b.enabled } : e))
        return { json: installed[0] }
      },
      'GET /api/extensions/file': (_, __, url) => {
        const file = installed.length && url.searchParams.get('id') === 'acme.night' ? files[url.searchParams.get('path')!] : undefined
        return file ? { json: file } : { status: 404, json: { status: 'not_found', error: 'acme.night has no such file it uses.' } }
      },
    })
    renderIde()
    await openApp()

    // Ctrl+Shift+X: the Extensions view; a .vsix chosen is sent as its bytes.
    await userEvent.keyboard('{Control>}{Shift>}x{/Shift}{/Control}')
    const view = await screen.findByRole('complementary', { name: 'Extensions' })
    expect(await within(view).findByText(/No extension is installed/)).toBeInTheDocument()
    const vsix = new File([new Uint8Array([0x50, 0x4b, 3, 4])], 'night-1.2.0.vsix', { type: 'application/octet-stream' })
    await userEvent.upload(within(view).getByLabelText('A .vsix to install'), vsix)
    await waitFor(() => expect(calls.some((c) => c.method === 'POST' && c.path === '/api/extensions/install')).toBe(true))
    const sent = calls.find((c) => c.path === '/api/extensions/install')!
    expect(sent.body).toBe(vsix)
    expect(sent.headers['Content-Type']).toBe('application/octet-stream')
    const item = await within(view).findByRole('listitem', { name: 'Night' })
    expect(item).toHaveTextContent('1 colour theme: Night Sky')
    expect(item).toHaveTextContent('snippets (typescript)')
    expect(item).toHaveTextContent('Not used here (1)')
    expect(await screen.findByText('Night 1.2.0 is installed.')).toBeInTheDocument()

    // Its language, with its configuration (comments, brackets); its snippets in TypeScript's files.
    await waitFor(() => expect(fakes.languageConfigs.get('toml')).toEqual({ comments: { lineComment: '#' }, brackets: [['[', ']']] }))
    expect(fakes.languages.some((l) => l.id === 'toml' && l.extensions?.includes('.toml'))).toBe(true)
    const snippets = fakes.completions.find((c) => c.language === 'typescript')!.provider
    const model = { getWordUntilPosition: () => ({ word: 'lo', startColumn: 1, endColumn: 3 }), getLanguageId: () => 'typescript' }
    const { suggestions } = await snippets.provideCompletionItems(model, { lineNumber: 1, column: 3 })
    expect(suggestions.map((s) => [s.label.label, s.insertText, s.insertTextRules])).toEqual([['log', 'console.log($1)\n$0', 4]])

    // Its key for a command Code Arena has works (Ctrl+Alt+L: the panel), in the editor too; its key for its own command is given to neither.
    expect(screen.getByRole('button', { name: 'Terminal' })).toHaveAttribute('aria-pressed', 'false')
    await userEvent.keyboard('{Control>}{Alt>}l{/Alt}{/Control}')
    await waitFor(() => expect(screen.getByRole('button', { name: 'Terminal' })).toHaveAttribute('aria-pressed', 'true'))
    expect(fakes.keybindingRules.at(-1)!.some((r) => r.command === 'workbench.action.togglePanel' && r.keybinding === (2048 | 512 | 42))).toBe(true)
    expect(fakes.keybindingRules.at(-1)!.some((r) => r.command === 'night.own')).toBe(false)

    // Ctrl+K Ctrl+T: its theme among Code Arena's; choosing it in the list shows it at once (Monaco's, the page's colours), Escape goes back.
    expect(document.documentElement).not.toHaveClass('dark')
    await userEvent.keyboard('{Control>}k{/Control}{Control>}t{/Control}')
    let picker = await screen.findByRole('dialog', { name: 'Color theme' })
    expect(within(picker).getByRole('option', { name: /Night Sky/ })).toHaveTextContent('acme.night')
    await userEvent.type(within(picker).getByRole('combobox'), 'night')
    await waitFor(() => expect(fakes.themes.at(-1)).toBe('ext-acme-night-night-sky'))
    const theme = fakes.definedThemes.get('ext-acme-night-night-sky')!
    expect(theme.base).toBe('vs-dark')
    expect(theme.colors).toMatchObject({ 'editor.background': '#101020', 'editor.foreground': '#e0e0e0' })
    expect(theme.rules).toEqual(expect.arrayContaining([{ token: 'comment', foreground: '808080', fontStyle: 'italic' }, { token: 'number', foreground: 'ff00aa' }]))
    await waitFor(() => expect(document.documentElement).toHaveClass('dark'))
    expect(document.documentElement.style.getPropertyValue('--sidebar')).toBe('#0a0a18')
    await userEvent.keyboard('{Escape}')
    await waitFor(() => expect(fakes.themes.at(-1)).toBe('arena-light'))
    expect(document.documentElement.style.getPropertyValue('--sidebar')).toBe('')
    expect(now.user['settings.json']).toBeUndefined()

    // Enter takes it: written into the person's settings.json, as VS Code writes it.
    await userEvent.keyboard('{Control>}k{/Control}{Control>}t{/Control}')
    picker = await screen.findByRole('dialog', { name: 'Color theme' })
    await userEvent.type(within(picker).getByRole('combobox'), 'night{Enter}')
    await waitFor(() => expect(now.user['settings.json']).toContain('"workbench.colorTheme": "Night Sky"'))
    expect(now.user['settings.json']).toContain('"window.autoDetectColorScheme": false')
    await waitFor(() => expect(fakes.themes.at(-1)).toBe('ext-acme-night-night-sky'))
    expect(document.documentElement).toHaveClass('dark')

    // Turned off: its theme is no longer offered (the setting reads as Code Arena's of its kind), nor its language's configuration and snippets.
    await userEvent.click(within(view).getByRole('switch', { name: 'Disable Night' }))
    expect(calls.find((c) => c.path === '/api/extensions/enable')?.body).toEqual({ id: 'acme.night', enabled: false })
    await waitFor(() => expect(fakes.themes.at(-1)).toBe('arena-dark'))
    await waitFor(() => expect(fakes.languageConfigs.has('toml')).toBe(false))
    expect(fakes.completions.some((c) => c.language === 'typescript')).toBe(false)
  })

  it("shows the files and folders with an extension's file icon theme: its images, its font's characters", async () => {
    const owned = { extension: 'acme.icons', version: '2.0.0' }
    const files: Record<string, unknown> = {
      'dist/icons.json': {
        iconDefinitions: { _ts: { iconPath: './svg/ts.svg' }, _md: { iconPath: '../md.png' }, _folder: { fontCharacter: '\\E001', fontId: 'glyphs', fontColor: '#ffcc00' }, _file: { fontCharacter: 'f' } },
        fonts: [{ id: 'glyphs', src: [{ path: './glyphs.woff2', format: 'woff2' }] }],
        file: '_file',
        folder: '_folder',
        fileExtensions: { ts: '_ts' },
        languageIds: { markdown: '_md' },
      },
    }
    backend(
      {
        'GET /api/extensions/contributions': () => ({ json: { themes: [], iconThemes: [{ id: 'acme-icons', label: 'Acme Icons', path: 'dist/icons.json', ...owned }], snippets: [], languages: [], keybindings: [], configurationDefaults: [] } }),
        'GET /api/extensions/file': (_, __, url) => (url.searchParams.get('id') === 'acme.icons' && files[url.searchParams.get('path')!] ? { json: files[url.searchParams.get('path')!] } : { status: 404, json: { status: 'not_found' } }),
      },
      {},
      { 'settings.json': '{ "workbench.iconTheme": "acme-icons" }' },
    )
    renderIde()
    const tree = await screen.findByRole('tree', { name: 'Files' })
    await userEvent.click(await within(tree).findByRole('treeitem', { name: 'src, the agent changed files in it' }))
    const app = await within(tree).findByRole('treeitem', { name: 'app.ts, changed by the agent' })
    // By extension: an image of the extension's, served by code-arena (its path from the icon theme's folder).
    await waitFor(() => expect(app.querySelector('img')).toHaveAttribute('src', '/api/extensions/file?id=acme.icons&path=dist%2Fsvg%2Fts.svg&v=2.0.0'))
    // By language (VS Code's id: markdown), from a folder above.
    expect(within(tree).getByRole('treeitem', { name: 'README.md' }).querySelector('img')).toHaveAttribute('src', '/api/extensions/file?id=acme.icons&path=md.png&v=2.0.0')
    // A folder: a character of the theme's font, loaded from its files.
    const glyph = within(tree).getByRole('treeitem', { name: 'lib' }).querySelector('span[aria-hidden="true"][style]') as HTMLElement
    expect(glyph.textContent).toBe('')
    expect(glyph.style.fontFamily).toBe('"ca-icons-acme-icons-acme-icons-glyphs"')
    expect(glyph.style.color).toBe('rgb(255, 204, 0)')
    expect(document.head.querySelector('style[data-code-arena="icon-fonts"]')!.textContent).toContain('@font-face { font-family: "ca-icons-acme-icons-acme-icons-glyphs"; src: url("/api/extensions/file?id=acme.icons&path=dist%2Fglyphs.woff2&v=2.0.0") format("woff2")')
    // Preferences: File Icon Theme goes back to Code Arena's own icons.
    await userEvent.keyboard('{Control>}{Shift>}p{/Shift}{/Control}')
    const palette = await screen.findByRole('dialog', { name: 'Commands' })
    await userEvent.type(within(palette).getByPlaceholderText('Run a command: type a few words of it'), 'file icon theme{Enter}')
    const picker = await screen.findByRole('dialog', { name: 'File icon theme' })
    expect(within(picker).getAllByRole('option').map((o) => o.textContent)).toEqual(['Code Arena (by kind)', 'Acme Iconscurrent'])
    await userEvent.click(within(picker).getByRole('option', { name: /Code Arena/ }))
    await waitFor(() => expect(app.querySelector('img')).toBeNull())
  })
})

describe('Code Arena, the help', () => {
  /** The workbench's regions on screen, by the names a screen reader gives them. */
  const regionsShown = () => {
    // The workbench: from the activity bar up to what holds the status bar too (not the page's toasts, nor a dialog).
    let workbench = screen.getByRole('navigation', { name: 'Activity bar' }).parentElement!
    while (!workbench.querySelector('footer[aria-label="Status bar"]')) workbench = workbench.parentElement!
    return [...workbench.querySelectorAll('nav[aria-label], aside[aria-label], section[aria-label], footer[aria-label], form[aria-label]')].map((el) => el.getAttribute('aria-label')!)
  }

  it('has help for every part of the workbench: a region without it fails here', async () => {
    backend()
    renderIde()
    const bar = await screen.findByRole('navigation', { name: 'Activity bar' })
    await screen.findByRole('tree', { name: 'Files' })
    const seen = new Set(regionsShown())
    // Every view of the side bar, and the terminal panel, drawn once.
    for (const name of ['Search', 'Source control', 'Run and Debug', 'Extensions', 'Agent changes (1)', 'Problems', 'Chat']) {
      await userEvent.click(within(bar).getByRole('button', { name }))
      for (const r of regionsShown()) seen.add(r)
    }
    await userEvent.click(within(bar).getByRole('button', { name: 'Terminal' }))
    await screen.findByRole('region', { name: 'Terminal' })
    for (const r of regionsShown()) seen.add(r)
    await userEvent.click(screen.getByRole('tab', { name: 'Debug Console' }))
    await screen.findByRole('region', { name: 'Debug Console' })
    for (const r of regionsShown()) seen.add(r)
    // The workbench's own pages: the Settings and the Keyboard Shortcuts editors.
    await userEvent.keyboard('{Control>},{/Control}')
    await screen.findByRole('region', { name: 'Settings' })
    for (const r of regionsShown()) seen.add(r)
    await userEvent.keyboard('{Control>}k{/Control}{Control>}s{/Control}')
    await screen.findByRole('region', { name: 'Keyboard Shortcuts' })
    for (const r of regionsShown()) seen.add(r)
    expect([...seen].filter((name) => !Object.hasOwn(regions, name)), 'a region with no help: give it its part in help-text.ts (regions), and the part its help').toEqual([])
    expect([...seen].toSorted()).toEqual(Object.keys(regions).toSorted())
    // Each part is a region on screen.
    expect(new Set(Object.values(regions))).toEqual(new Set(Object.keys(parts)))
  })

  it("points each part at a heading of the manual's Code Arena page, at the address code-arena gives", () => {
    const ids = parseDoc(codeArenaDoc).headings.map((h) => h.id)
    for (const p of Object.values(parts)) expect(ids, p.name).toContain(p.manual)
    expect(ids).toContain('the-ide')
    // code-arena's state names /help/code-arena (Config.ManualUrl): the manual's page for docs/code-arena.md.
    expect(manualDocs.find((d) => d.file === 'code-arena.md')?.id).toBe('code-arena')
    for (const t of tasks) expect(t.steps.length, t.title).toBeGreaterThan(0)
  })

  it("opens from Help in the activity bar with every part, and at a panel's part from its ?", async () => {
    backend()
    renderIde()
    const bar = await screen.findByRole('navigation', { name: 'Activity bar' })
    const help = within(bar).getByRole('button', { name: 'Help' })
    await userEvent.click(help)
    let sheet = await screen.findByRole('dialog', { name: 'Code Arena' })
    expect(within(sheet).getAllByRole('term').map((t) => t.textContent)).toEqual(expect.arrayContaining(Object.values(parts).map((p) => p.name)))
    expect(within(sheet).queryByRole('heading', { name: /^This part/ })).not.toBeInTheDocument()
    expect(within(sheet).getByRole('heading', { name: 'Ask the agent for a change' })).toBeInTheDocument()
    const manual = within(sheet).getByRole('link', { name: 'Code Arena in the manual' })
    expect(manual).toHaveAttribute('href', 'https://llm.test/help/code-arena#the-ide')
    expect(manual).toHaveAttribute('target', '_blank')
    // Over the workbench: Esc closes it, and the focus goes back to Help.
    await userEvent.keyboard('{Escape}')
    await waitFor(() => expect(screen.queryByRole('dialog')).not.toBeInTheDocument())
    expect(help).toHaveFocus()

    const at = async (button: string, part: string, anchor: string) => {
      await userEvent.click(screen.getByRole('button', { name: button }))
      sheet = await screen.findByRole('dialog', { name: 'Code Arena' })
      expect(within(sheet).getByRole('heading', { name: `This part: ${part}` })).toBeInTheDocument()
      expect(within(sheet).getByRole('link', { name: 'More on this in the manual' })).toHaveAttribute('href', `https://llm.test/help/code-arena#${anchor}`)
      expect(within(sheet).getByText(part, { selector: 'dt' }).parentElement).toHaveAttribute('aria-current', 'true')
      await userEvent.click(within(sheet).getByRole('button', { name: 'Close' }))
      await waitFor(() => expect(screen.queryByRole('dialog')).not.toBeInTheDocument())
      expect(screen.getByRole('button', { name: button })).toHaveFocus()
    }
    await at('Help: Explorer', 'Explorer', 'the-ide')
    await at('Help: Chat with the agent', 'Chat with the agent', 'modes')
    await userEvent.click(within(bar).getByRole('button', { name: 'Search' }))
    await at('Help: Search', 'Search', 'the-ide')
    await userEvent.click(within(bar).getByRole('button', { name: 'Agent changes (1)' }))
    await at('Help: Agent changes', 'Agent changes', 'the-ide')
    await userEvent.click(within(bar).getByRole('button', { name: 'Chat' }))
    await at('Help: Sessions', 'Sessions', 'sessions-and-the-models-window')
    await userEvent.click(within(bar).getByRole('button', { name: 'Terminal' }))
    await at('Help: Terminal', 'Terminal', 'terminals')
  })
})

describe('quick open ranking', () => {
  it('ranks by letters in order, the file name and runs first', () => {
    const files = ['src/components/cart-item.tsx', 'src/cart.ts', 'docs/architecture.md', 'tests/cart.test.ts', 'src/app.ts']
    // In the name first (the shorter path first when they score alike), then anywhere in the path.
    expect(rankFiles(files, 'cart')).toEqual(['src/cart.ts', 'tests/cart.test.ts', 'src/components/cart-item.tsx', 'docs/architecture.md'])
    expect(rankFiles(files, 'sapp')).toEqual(['src/app.ts'])
    expect(rankFiles(files, 'zzz')).toEqual([])
    expect(rankFiles(files, '', 2)).toEqual(files.slice(0, 2))
  })
})
