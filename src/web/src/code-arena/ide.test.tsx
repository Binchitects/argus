import { act, render, screen, waitFor, within } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import codeArenaDoc from '@docs/code-arena.md?raw'
import { makeQueryClient, Providers } from '@/app/providers'
import { manualDocs, parseDoc } from '@/help/manual'
import { blank } from '@/pages/chat/live'
import type { ChatConfig, Message } from '@/pages/chat/types'
import { fakeApi, type Handler } from '@/test/utils'
import type { CodeState } from './api'
import { App } from './app'
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
    getFullModelRange() {
      return { startLineNumber: 1, startColumn: 1, endLineNumber: 1, endColumn: 1 }
    }
    pushEditOperations(_: unknown, ops: { text: string }[]) {
      this.setValue(ops[0]!.text)
      return null
    }
    pushStackElement() {}
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
    /** The editor's own actions (its right-click menu, F1): by id, run as the person would. */
    actions = new Map<string, { label: string; keybindings?: number[]; run: () => void }>()
    addAction(a: { id: string; label: string; keybindings?: number[]; run: () => void }) {
      this.actions.set(a.id, a)
      return { dispose() {} }
    }
    getSelection() {
      return this.selection
    }
    getSupportedActions() {
      return [...this.actions.entries()].map(([id, a]) => ({ id, label: a.label }))
    }
    getAction(id: string) {
      const a = this.actions.get(id)
      return a ? { run: async () => a.run() } : null
    }
    dispose() {}
  }
  class DiffEditor {
    model: { original: Model; modified: Model } | null = null
    constructor() {
      fakes.diffEditors.push(this)
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
    /** What the editor marks (a language's problems), by the file's address. */
    markers: new Map<string, { severity: number; startLineNumber: number; startColumn: number; endLineNumber: number; message: string }[]>(),
    commands: new Map<string, (...args: unknown[]) => void>(),
    codeActions: [] as { provideCodeActions: (...args: unknown[]) => unknown }[],
  }
})

vi.mock('monaco-editor', () => ({
  editor: {
    create: (host: HTMLElement, options: Record<string, unknown>) => new fakes.Editor(host, options),
    createDiffEditor: () => new fakes.DiffEditor(),
    createModel: (value: string, language: string, uri?: InstanceType<typeof fakes.Uri>) => new fakes.Model(value, language, uri),
    getModel: (uri: InstanceType<typeof fakes.Uri>) => fakes.models.find((m) => !m.disposed && m.uri?.toString() === uri.toString()) ?? null,
    setModelLanguage: (m: { language: string }, language: string) => {
      m.language = language
    },
    setTheme: (t: string) => fakes.themes.push(t),
    getModelMarkers: ({ resource }: { resource: InstanceType<typeof fakes.Uri> }) => fakes.markers.get(resource.toString()) ?? [],
    registerCommand: (id: string, run: (...args: unknown[]) => void) => {
      fakes.commands.set(id, run)
      return { dispose() {} }
    },
    defineTheme: () => undefined,
    remeasureFonts: () => undefined,
  },
  KeyMod: { CtrlCmd: 2048 },
  KeyCode: { KeyL: 42 },
  MarkerSeverity: { Hint: 1, Info: 2, Warning: 4, Error: 8 },
  languages: {
    registerCodeActionProvider: (_: string, provider: { provideCodeActions: (...args: unknown[]) => unknown }) => {
      fakes.codeActions.push(provider)
      return { dispose() {} }
    },
    getLanguages: () => [
      { id: 'typescript', extensions: ['.ts', '.tsx'], aliases: ['TypeScript'] },
      { id: 'markdown', extensions: ['.md'], aliases: ['Markdown'] },
      { id: 'dockerfile', extensions: ['.dockerfile'], filenames: ['Dockerfile'], aliases: ['Dockerfile'] },
    ],
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
  for (const list of [fakes.models, fakes.editors, fakes.diffEditors, fakes.terminals, fakes.sockets, fakes.themes]) list.length = 0
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

/** code-arena web with a small folder: src/app.ts (changed by the agent), src/lib/, README.md; the page's preferences as an earlier run left them. */
function backend(extra: Record<string, Handler> = {}, preferences: Preferences = {}) {
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
  const now = { changes: [{ path: 'src/app.ts', created: false, deleted: false, added: 1, removed: 1 }] as Change[], terminals: [] as TerminalInfo[], next: 1, preferences }
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
    'GET /api/preferences': () => ({ json: now.preferences }),
    'POST /api/preferences': (body) => {
      now.preferences = { ...now.preferences, ...(body as Preferences) }
      return { json: now.preferences }
    },
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
    expect(within(bar).getAllByRole('button').map((b) => b.getAttribute('aria-label'))).toEqual(['Explorer', 'Search', 'Agent changes (1)', 'Chat', 'Terminal', 'Theme', 'Help', 'About Code Arena'])
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
    await waitFor(() => expect(hold.held()).toBe(2))
    hold.release()
    await waitFor(() => expect(editor().model?.value).toBe('const total = 1\nconsole.log(total)\n'))
    await waitFor(() => expect(calls.filter((c) => c.path === '/api/file?path=src%2Fapp.ts')).toHaveLength(2))
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
    act(() => ed.actions.get('arena.addToChat')!.run())
    expect(ed.actions.get('arena.addToChat')!.keybindings).toEqual([2048 | 42])
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
    act(() => ed.actions.get('arena.explain')!.run())
    await waitFor(() => expect(sent()).toHaveLength(2))
    expect(sent()[1]).toEqual({ text: asks.explain, context: [{ path: 'src/app.ts', startLine: 2, endLine: 2, text: 'console.log(total)' }] })

    // Fix this: with the problems the editor marks there.
    await screen.findByRole('button', { name: 'Send' })
    fakes.markers.set('file:///src/app.ts', [{ severity: 8, startLineNumber: 1, startColumn: 15, endLineNumber: 1, message: "';' expected." }])
    ed.setSelection({ startLineNumber: 1, startColumn: 1, endLineNumber: 1, endColumn: 1 })
    act(() => ed.actions.get('arena.fix')!.run())
    await waitFor(() => expect(sent()).toHaveLength(3))
    expect(sent()[2]!.text).toBe("Fix the problems in this code: 1:15 ';' expected.")
    expect(ed.actions.get('arena.complete')!.label).toBe('Code Arena: Complete this')

    // The light bulb on a marked line: Fix with Code Arena.
    const [provider] = fakes.codeActions
    const offered = provider!.provideCodeActions({ uri: new fakes.Uri('file', '/src/app.ts') }, { startLineNumber: 1, endLineNumber: 1 }, { markers: fakes.markers.get('file:///src/app.ts') }) as {
      actions: { title: string; command: { id: string; arguments: unknown[] } }[]
    }
    expect(offered.actions.map((a) => a.title)).toEqual(['Fix with Code Arena'])
    await screen.findByRole('button', { name: 'Send' })
    act(() => fakes.commands.get(offered.actions[0]!.command.id)!(null, ...offered.actions[0]!.command.arguments))
    await waitFor(() => expect(sent()).toHaveLength(4))
    expect(sent()[3]!.context).toEqual([{ path: 'src/app.ts', startLine: 1, endLine: 1 }])

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
    await userEvent.clear(command)
    await userEvent.type(command, 'show the terminal')
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

  it('opens as it was left in an earlier run, whatever its port: code-arena keeps the layout and the theme', async () => {
    const { calls, now } = backend({}, { layout: { view: 'search', chatOpen: false, side: 300, chat: 5000, unknown: 1 }, theme: 'dark' })
    renderIde()
    expect(await screen.findByRole('textbox', { name: 'Search' })).toBeInTheDocument()
    expect(screen.getByRole('complementary', { name: 'Search' })).toHaveStyle({ width: '300px' })
    expect(screen.queryByRole('region', { name: 'Chat' })).not.toBeInTheDocument()
    await waitFor(() => expect(document.documentElement).toHaveClass('dark'))
    const saves = () => calls.filter((c) => c.method === 'POST' && c.path === '/api/preferences')
    expect(saves()).toHaveLength(0)

    // A change is kept a moment later (the sizes within bounds), the theme at once.
    await userEvent.click(screen.getByRole('button', { name: 'Explorer' }))
    await waitFor(() => expect(now.preferences.layout).toEqual({ view: 'explorer', side: 300, chat: 960, panel: 280, sideOpen: true, chatOpen: false, panelOpen: false }))
    expect(saves()).toHaveLength(1)
    await userEvent.click(screen.getByRole('button', { name: 'Theme' }))
    await userEvent.click(await screen.findByRole('menuitemradio', { name: 'Light' }))
    await waitFor(() => expect(now.preferences.theme).toBe('light'))
    expect(document.documentElement).not.toHaveClass('dark')
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
    for (const name of ['Search', 'Agent changes (1)', 'Chat']) {
      await userEvent.click(within(bar).getByRole('button', { name }))
      for (const r of regionsShown()) seen.add(r)
    }
    await userEvent.click(within(bar).getByRole('button', { name: 'Terminal' }))
    await screen.findByRole('region', { name: 'Terminal' })
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
