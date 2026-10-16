import { QueryClient } from '@tanstack/react-query'
import type * as Monaco from 'monaco-editor'
import { describe, expect, it, vi } from 'vitest'
import { toast } from '@/components/ui/toaster'
import type { MonacoModule } from './editor-state'
import { editorsOwn, formatModel, formatterOf, installFormatting, keepEditorFormatters, minimalEdit, offerFormatting } from './format'
import { formatText, formattersQuery, type Formatter, type Formatters } from './ide-api'

vi.mock('@/components/ui/toaster', () => ({ toast: { info: vi.fn(), error: vi.fn() } }))
// The project's formatters run on code-arena: none is reached here unless a test says so.
vi.mock('./ide-api', async (original) => ({ ...(await original<typeof import('./ide-api')>()), formatText: vi.fn() }))

describe('formatting', () => {
  it('turns a formatted text into one edit of what differs, so the cursor stays where nothing changed', () => {
    expect(minimalEdit('a = 1\nb=2\nc = 3\n', 'a = 1\nb = 2\nc = 3\n')).toEqual({ start: 7, end: 8, text: ' = ' })
    expect(minimalEdit('same', 'same')).toBeNull()
    expect(minimalEdit('', 'x')).toEqual({ start: 0, end: 0, text: 'x' })
    expect(minimalEdit('xx', '')).toEqual({ start: 0, end: 2, text: '' })
    // Repeated letters at the edges are not counted twice.
    expect(minimalEdit('aaa', 'aa')).toEqual({ start: 2, end: 3, text: '' })
  })

  it("reads VS Code's extension ids in editor.defaultFormatter as code-arena's formatters", () => {
    expect(formatterOf('esbenp.prettier-vscode')).toBe('prettier')
    expect(formatterOf('ms-python.black-formatter')).toBe('black')
    expect(formatterOf('ruff')).toBe('ruff')
    expect(formatterOf('')).toBeUndefined()
    expect(formatterOf(null)).toBeUndefined()
  })

  it("reads VS Code's built-in formatters in editor.defaultFormatter as the editor's own", () => {
    expect(formatterOf('vscode.typescript-language-features')).toBe(editorsOwn)
    expect(formatterOf('vscode.json-language-features')).toBe(editorsOwn)
    expect(formatterOf('vscode.css-language-features')).toBe(editorsOwn)
    expect(formatterOf('vscode.html-language-features')).toBe(editorsOwn)
  })
})

/** A text model as Monaco's has it, for what formatting asks of one. */
class Model {
  version = 1
  text: string
  language: string
  uri: { scheme: string; path: string }
  constructor(text: string, language: string, uri: { scheme: string; path: string }) {
    this.text = text
    this.language = language
    this.uri = uri
  }
  getLanguageId = () => this.language
  getValue = () => this.text
  getVersionId = () => this.version
  getOptions = () => ({ tabSize: 2, insertSpaces: true })
  isDisposed = () => false
  pushStackElement = () => undefined
  getOffsetAt({ lineNumber, column }: { lineNumber: number; column: number }) {
    return this.text.split('\n').slice(0, lineNumber - 1).reduce((n, line) => n + line.length + 1, 0) + column - 1
  }
  getPositionAt(offset: number) {
    const lines = this.text.slice(0, offset).split('\n')
    return { lineNumber: lines.length, column: lines.at(-1)!.length + 1 }
  }
  getFullModelRange() {
    const lines = this.text.split('\n')
    return new Range(1, 1, lines.length, lines.at(-1)!.length + 1)
  }
  pushEditOperations(_: unknown, edits: { range: Monaco.IRange; text: string | null }[]) {
    const at = edits.map((e) => ({ start: this.getOffsetAt({ lineNumber: e.range.startLineNumber, column: e.range.startColumn }), end: this.getOffsetAt({ lineNumber: e.range.endLineNumber, column: e.range.endColumn }), text: e.text ?? '' }))
    for (const e of at.sort((a, b) => b.start - a.start)) this.text = this.text.slice(0, e.start) + e.text + this.text.slice(e.end)
    this.version++
    return null
  }
}

class Range {
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
}

type Provider = Partial<Monaco.languages.DocumentFormattingEditProvider & Monaco.languages.DocumentRangeFormattingEditProvider>
/** The formatting providers given to Monaco, the newest first (Monaco asks the newest). */
const providers: { kind: 'document' | 'range'; language: string; provider: Provider }[] = []
const register = (kind: 'document' | 'range') => (language: string, provider: Provider) => {
  const p = { kind, language, provider }
  providers.unshift(p)
  return { dispose: () => void providers.splice(providers.indexOf(p), 1) }
}
const languages = { registerDocumentFormattingEditProvider: register('document'), registerDocumentRangeFormattingEditProvider: register('range') }
const m = { monaco: { languages, Range }, pathOf: (uri: { scheme: string; path: string }) => (uri.scheme === 'file' ? uri.path.slice(1) : null) } as unknown as MonacoModule
const editorModel = (model: Model) => model as unknown as Monaco.editor.ITextModel
const token: Monaco.CancellationToken = { isCancellationRequested: false, onCancellationRequested: () => ({ dispose() {} }) }

/** Format Document (Shift+Alt+F) as Monaco runs it: the newest provider that formats whole files, else the newest that formats ranges, over the whole file; Monaco applies what it gives. */
async function formatDocument(model: Model) {
  const mine = providers.filter((p) => p.language === model.language)
  const p = mine.find((x) => x.kind === 'document') ?? mine.find((x) => x.kind === 'range')
  if (!p) throw new Error(`No formatter for ${model.language}`)
  const options = { tabSize: 2, insertSpaces: true }
  const edits = p.kind === 'document' ? await p.provider.provideDocumentFormattingEdits!(editorModel(model), options, token) : await p.provider.provideDocumentRangeFormattingEdits!(editorModel(model), model.getFullModelRange() as unknown as Monaco.Range, options, token)
  model.pushEditOperations([], edits ?? [])
}

// The editor's own formatters register as Monaco's language services start, after monaco.ts kept them.
keepEditorFormatters(languages as unknown as typeof Monaco.languages)
/** The editor's own formatter: one space each side of "=" (none for the one dropped, so a test tells which ran). */
const own = (spaced: string) => (model: Monaco.editor.ITextModel) => [{ range: (model as unknown as Model).getFullModelRange(), text: model.getValue().replace(/ *= */g, spaced) }]
languages.registerDocumentRangeFormattingEditProvider('typescript', { provideDocumentRangeFormattingEdits: own(' = ') })
languages.registerDocumentRangeFormattingEditProvider('javascript', { provideDocumentRangeFormattingEdits: own(' = ') })
languages.registerDocumentFormattingEditProvider('json', { provideDocumentFormattingEdits: own(' = ') })
// A language service that registers again (as JSON's does when its options change) drops the one it had.
languages.registerDocumentFormattingEditProvider('json', { provideDocumentFormattingEdits: own('=') }).dispose()
languages.registerDocumentFormattingEditProvider('css', { provideDocumentFormattingEdits: () => new Promise<Monaco.languages.TextEdit[]>(() => undefined) })

const queryClient = new QueryClient()
const settings: Record<string, Record<string, unknown>> = {}
installFormatting({ m, queryClient, settings: (language) => settings[language ?? ''] ?? {} })
const prettier = (more: Partial<Formatter>): Formatter => ({ id: 'prettier', name: 'Prettier', source: 'project', available: true, range: true, ...more })
/** What code-arena says the formatters of a file are. */
const formatters = (path: string, language: string, list: Formatter[]) => {
  const formatters: Formatters = { language, formatters: list, default: list.find((f) => f.available)?.id ?? null }
  queryClient.setQueryData(formattersQuery(path, language).queryKey, formatters)
}
const ours = (language: string) => providers.filter((p) => p.language === language && p.provider.displayName)

describe("formatting with the editor's own formatter", () => {
  it("keeps the editor's own for a language whose project formatter is only missing: Format Document and a save format with it", async () => {
    formatters('src/app.ts', 'typescript', [prettier({ available: false, reason: 'missing', hint: 'npm install --save-dev prettier' })])
    await offerFormatting('src/app.ts', 'typescript')
    // Code Arena takes no place of the editor's own for the language.
    expect(ours('typescript')).toHaveLength(0)
    const model = new Model('const total=2\n', 'typescript', { scheme: 'file', path: '/src/app.ts' })
    await formatDocument(model)
    expect(model.text).toBe('const total = 2\n')
    // A save (format on save) formats with it too, as one edit.
    model.text = 'let a=1\n'
    expect(await formatModel(editorModel(model), 'src/app.ts', { ask: false })).toBe(editorsOwn)
    expect(model.text).toBe('let a = 1\n')
    expect(formatText).not.toHaveBeenCalled()
  })

  it("formats with the editor's own where editor.defaultFormatter names VS Code's built-in formatter, though the project has Prettier", async () => {
    settings.javascript = { 'editor.defaultFormatter': 'vscode.typescript-language-features' }
    formatters('src/app.js', 'javascript', [prettier({})])
    await offerFormatting('src/app.js', 'javascript')
    expect(ours('javascript').map((p) => p.kind)).toEqual(['range', 'document'])
    const model = new Model('let a=1\n', 'javascript', { scheme: 'file', path: '/src/app.js' })
    await formatDocument(model)
    expect(model.text).toBe('let a = 1\n')
    expect(formatText).not.toHaveBeenCalled()
    expect(toast.error).not.toHaveBeenCalled()
  })

  it("formats a file that is none of the folder's (the person's own settings.json) with the editor's own, where the project's formatter took its place", async () => {
    formatters('package.json', 'json', [prettier({})])
    await offerFormatting('package.json', 'json')
    expect(ours('json')).toHaveLength(2)
    const model = new Model('{"a"=1}', 'json', { scheme: 'vscode-userdata', path: '/User/settings.json' })
    await formatDocument(model)
    expect(model.text).toBe('{"a" = 1}')
    expect(formatText).not.toHaveBeenCalled()
  })

  it("offers a formatter that waits on the folder's trust, and a save says it saved without it", async () => {
    formatters('main.py', 'python', [{ id: 'ruff', name: 'ruff', source: 'project', available: false, reason: 'trust', range: true }])
    await offerFormatting('main.py', 'python')
    expect(ours('python')).toHaveLength(2)
    const model = new Model('a=1\n', 'python', { scheme: 'file', path: '/main.py' })
    expect(await formatModel(editorModel(model), 'main.py', { ask: false })).toBeNull()
    expect(toast.info).toHaveBeenCalledWith(expect.stringContaining('Saved without formatting: ruff lives in this folder'))
    expect(model.text).toBe('a=1\n')
  })

  it("stops waiting on the editor's own formatter when a save's time is up", async () => {
    formatters('a.css', 'css', [])
    const model = new Model('a{}', 'css', { scheme: 'file', path: '/a.css' })
    const stop = new AbortController()
    const done = formatModel(editorModel(model), 'a.css', { signal: stop.signal, ask: false })
    stop.abort()
    await expect(done).rejects.toBeDefined()
    expect(model.text).toBe('a{}')
  })
})
