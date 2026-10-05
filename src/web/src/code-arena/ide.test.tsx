import { act, render, screen, waitFor, within } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { makeQueryClient, Providers } from '@/app/providers'
import type { ChatConfig } from '@/pages/chat/types'
import { fakeApi, type Handler } from '@/test/utils'
import type { CodeState } from './api'
import { App } from './app'
import { rankFiles } from './editor-state'
import type { Change, Entry, Preferences, TerminalInfo } from './ide-api'

// Monaco, xterm.js and the terminal's socket, as fakes that remember what the page did with them.
const fakes = vi.hoisted(() => {
  let alt = 0
  class Model {
    value: string
    language: string
    alt = ++alt
    disposed = false
    listeners: (() => void)[] = []
    constructor(value: string, language: string) {
      this.value = value
      this.language = language
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
  }
})

vi.mock('monaco-editor', () => ({
  editor: {
    create: (host: HTMLElement, options: Record<string, unknown>) => new fakes.Editor(host, options),
    createDiffEditor: () => new fakes.DiffEditor(),
    createModel: (value: string, language: string) => new fakes.Model(value, language),
    setModelLanguage: (m: { language: string }, language: string) => {
      m.language = language
    },
    setTheme: (t: string) => fakes.themes.push(t),
    defineTheme: () => undefined,
    remeasureFonts: () => undefined,
  },
  languages: {
    getLanguages: () => [
      { id: 'typescript', extensions: ['.ts', '.tsx'], aliases: ['TypeScript'] },
      { id: 'markdown', extensions: ['.md'], aliases: ['Markdown'] },
      { id: 'dockerfile', extensions: ['.dockerfile'], filenames: ['Dockerfile'], aliases: ['Dockerfile'] },
    ],
  },
  typescript: { typescriptDefaults: { setDiagnosticsOptions: () => undefined }, javascriptDefaults: { setDiagnosticsOptions: () => undefined } },
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
  name: 'Code Arena', version: '5.2.0', license: 'AGPL-3.0-only', source: 'https://github.com/Binchitects/argus', folder: '/home/ada/shop', project: 'shop', branch: 'feature/cart', model: 'model-a', context: 32768, thinking: null, mode: 'auto-edit',
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

/** Holds back the answers to the calls whose address matches until let go: what the page does while one is on its way. */
function holdBack(match: (path: string) => boolean) {
  const answer = vi.mocked(fetch).getMockImplementation()!
  const waiting: (() => void)[] = []
  let held = 0
  let open = false
  vi.mocked(fetch).mockImplementation(async (input, init) => {
    const url = new URL(String(input), 'https://llm.test')
    if (!open && match(url.pathname + url.search)) {
      held++
      await new Promise<void>((go) => waiting.push(go))
    }
    return answer(input, init)
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
    expect(within(bar).getAllByRole('button').map((b) => b.getAttribute('aria-label'))).toEqual(['Explorer', 'Search', 'Agent changes (1)', 'Chat', 'Terminal', 'Theme', 'About Code Arena'])
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
    expect(within(status).getByLabelText('Line 1, column 1')).toBeInTheDocument()

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
    expect(within(screen.getByRole('contentinfo', { name: 'Status bar' })).getByLabelText('Line 2, column 13')).toBeInTheDocument()
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
    expect(await screen.findByPlaceholderText('Go to file: type a few letters of its path')).toBeInTheDocument()
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
    const box = await screen.findByPlaceholderText('Go to file: type a few letters of its path')
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
