import { afterEach, describe, expect, it, vi } from 'vitest'
import type * as Monaco from 'monaco-editor'
import { configurationOf, editsByUri, hoverContents, languageIdOf, sectionOf, toLocations, toMarker, toSymbols, toWorkspaceEdit } from './convert'
import { LspConnection, ResponseError } from './rpc'
import { serverEnabled } from './servers'

// Monaco's Uri, as far as the conversions use it.
const m = {
  Uri: {
    parse: (s: string) => ({ toString: () => s, scheme: s.slice(0, s.indexOf(':')) }),
  },
} as unknown as typeof Monaco

describe('the protocol in Monaco\'s words', () => {
  it('turns lines and characters from 0 into lines and columns from 1, and links into locations', () => {
    const range = { start: { line: 0, character: 4 }, end: { line: 2, character: 0 } }
    expect(toLocations(m, { uri: 'file:///a.ts', range }).map((l) => ({ uri: l.uri.toString(), range: l.range }))).toEqual([
      { uri: 'file:///a.ts', range: { startLineNumber: 1, startColumn: 5, endLineNumber: 3, endColumn: 1 } },
    ])
    const [link] = toLocations(m, [{ targetUri: 'file:///b.ts', targetRange: range, targetSelectionRange: range, originSelectionRange: range }])
    expect(link!.uri.toString()).toBe('file:///b.ts')
    expect(link!.targetSelectionRange).toEqual({ startLineNumber: 1, startColumn: 5, endLineNumber: 3, endColumn: 1 })
    expect(toLocations(m, null)).toEqual([])
  })

  it('shows hovers in every shape the protocol has had, plain text as it is', () => {
    expect(hoverContents({ kind: 'markdown', value: '**x**' })).toEqual([{ value: '**x**' }])
    expect(hoverContents({ kind: 'plaintext', value: 'a*b' })).toEqual([{ value: 'a\\*b' }])
    expect(hoverContents(['text', { language: 'ts', value: 'let x' }])).toEqual([{ value: 'text' }, { value: '```ts\nlet x\n```' }])
    expect(hoverContents('')).toEqual([])
  })

  it('marks a diagnostic with its severity, code, source and tags', () => {
    const marker = toMarker(m, { range: { start: { line: 1, character: 2 }, end: { line: 1, character: 5 } }, severity: 2, code: 'E1', message: 'unused', tags: [1, 9] }, 'pyright')
    expect(marker).toMatchObject({ startLineNumber: 2, startColumn: 3, endColumn: 6, severity: 4, code: 'E1', source: 'pyright', message: 'unused', tags: [1] })
    expect(toMarker(m, { range: { start: { line: 0, character: 0 }, end: { line: 0, character: 1 } }, message: 'x' }, 's').severity).toBe(8)
  })

  it('reads symbols as a tree or as a list', () => {
    const r = { start: { line: 0, character: 0 }, end: { line: 3, character: 1 } }
    const tree = toSymbols([{ name: 'Cart', kind: 5, range: r, selectionRange: r, children: [{ name: 'add', kind: 6, range: r, selectionRange: r }] }])
    expect(tree[0]).toMatchObject({ name: 'Cart', kind: 4, children: [{ name: 'add', kind: 5 }] })
    expect(toSymbols([{ name: 'total', kind: 13, location: { uri: 'file:///a.ts', range: r }, containerName: 'main' }])[0]).toMatchObject({ name: 'total', kind: 12, containerName: 'main' })
  })

  it('reads a workspace edit by file, both ways it is written, and says when it would make or rename files', () => {
    const e = (text: string) => ({ range: { start: { line: 0, character: 0 }, end: { line: 0, character: 3 } }, newText: text })
    expect([...editsByUri({ changes: { 'file:///a.ts': [e('x')], 'file:///b.ts': [e('y')] } }).files.keys()]).toEqual(['file:///a.ts', 'file:///b.ts'])
    const both = editsByUri({ documentChanges: [{ textDocument: { uri: 'file:///a.ts', version: 3 }, edits: [e('x')] }, { textDocument: { uri: 'file:///a.ts' }, edits: [e('z')] }, { kind: 'rename' }] })
    expect(both.files.get('file:///a.ts')!.map((x) => x.newText)).toEqual(['x', 'z'])
    expect(both.fileOperations).toBe(true)
    expect(toWorkspaceEdit(m, { changes: { 'file:///a.ts': [e('x')] } }).edits).toEqual([{ resource: expect.anything(), textEdit: { range: { startLineNumber: 1, startColumn: 1, endLineNumber: 1, endColumn: 4 }, text: 'x' }, versionId: undefined }])
  })

  it('names a file\'s language as a server does: .tsx is TypeScript React', () => {
    expect(languageIdOf('typescript', 'src/App.tsx')).toBe('typescriptreact')
    expect(languageIdOf('javascript', 'a.jsx')).toBe('javascriptreact')
    expect(languageIdOf('typescript', 'a.ts')).toBe('typescript')
    expect(languageIdOf('shell', 'run.sh')).toBe('shellscript')
  })

  it("answers a server's settings from settings.json's keys, nested, with its own from config.json over them", () => {
    const config = configurationOf(
      { 'python.analysis.typeCheckingMode': 'strict', 'python.analysis.extraPaths': ['lib'], gopls: { 'ui.semanticTokens': true }, 'editor.tabSize': 2, '[python]': { 'editor.tabSize': 4 } },
      { python: { analysis: { typeCheckingMode: 'basic' } } },
    )
    expect(config).toEqual({ python: { analysis: { typeCheckingMode: 'basic', extraPaths: ['lib'] } }, gopls: { 'ui.semanticTokens': true }, editor: { tabSize: 2 } })
    expect(sectionOf(config, 'python.analysis')).toEqual({ typeCheckingMode: 'basic', extraPaths: ['lib'] })
    expect(sectionOf(config, 'gopls')).toEqual({ 'ui.semanticTokens': true })
    expect(sectionOf(config, 'rust-analyzer.cargo')).toBeNull()
    expect(sectionOf(config, undefined)).toBe(config)
  })

  it('turns a server on or off by the settings', () => {
    expect(serverEnabled({}, 'gopls')).toBe(true)
    expect(serverEnabled({ 'codeArena.languageServers.gopls.enabled': false }, 'gopls')).toBe(false)
    expect(serverEnabled({ 'codeArena.languageServers.lua': { enabled: false } }, 'lua')).toBe(false)
    expect(serverEnabled({ 'codeArena.languageServers.enabled': false, 'codeArena.languageServers.gopls.enabled': true }, 'gopls')).toBe(false)
  })
})

describe('JSON-RPC over the socket', () => {
  class Socket {
    static OPEN = 1
    static last: Socket
    readyState = 0
    sent: string[] = []
    onopen: (() => void) | null = null
    onerror: (() => void) | null = null
    onclose: (() => void) | null = null
    onmessage: ((e: { data: unknown }) => void) | null = null
    url: string
    constructor(url: string) {
      this.url = url
      Socket.last = this
    }
    send(d: string) {
      this.sent.push(d)
    }
    close() {
      this.readyState = 3
    }
    open() {
      this.readyState = 1
      this.onopen?.()
    }
    receive(m: unknown) {
      this.onmessage?.({ data: JSON.stringify(m) })
    }
  }
  afterEach(() => vi.unstubAllGlobals())
  const sent = () => Socket.last.sent.map((x) => JSON.parse(x) as { id?: number | string; method?: string; params?: unknown; result?: unknown; error?: { code: number } })

  it('answers each request with its response, cancels one Monaco gave up, and answers the server\'s own requests', async () => {
    vi.stubGlobal('WebSocket', Socket)
    const c = new LspConnection('ws://x/api/lsp/socket?server=gopls')
    const notes: string[] = []
    c.onNotification = (method) => notes.push(method)
    c.onRequest = async (method) => {
      if (method === 'workspace/workspaceFolders') return [{ uri: 'file:///', name: 'shop' }]
      throw new ResponseError(-32601, 'no')
    }
    Socket.last.open()
    await c.opened
    const hover = c.request('textDocument/hover', { a: 1 })
    let cancel: () => void = () => undefined
    const token = { isCancellationRequested: false, onCancellationRequested: (f: () => void) => ((cancel = f), { dispose() {} }) }
    const slow = c.request('textDocument/references', {}, token)
    expect(sent().map((m) => [m.id, m.method])).toEqual([
      [1, 'textDocument/hover'],
      [2, 'textDocument/references'],
    ])
    Socket.last.receive({ jsonrpc: '2.0', id: 1, result: { contents: 'x' } })
    await expect(hover).resolves.toEqual({ contents: 'x' })
    cancel()
    await expect(slow).rejects.toMatchObject({ code: -32800 })
    expect(sent().at(-1)).toEqual({ jsonrpc: '2.0', method: '$/cancelRequest', params: { id: 2 } })

    Socket.last.receive({ jsonrpc: '2.0', method: 'window/logMessage', params: {} })
    Socket.last.receive({ jsonrpc: '2.0', id: 'w', method: 'workspace/workspaceFolders' })
    Socket.last.receive({ jsonrpc: '2.0', id: 7, method: 'something/else' })
    await vi.waitFor(() => expect(sent().filter((m) => m.method === undefined)).toEqual([
      { jsonrpc: '2.0', id: 'w', result: [{ uri: 'file:///', name: 'shop' }] },
      { jsonrpc: '2.0', id: 7, error: { code: -32601, message: 'no' } },
    ]))
    expect(notes).toEqual(['window/logMessage'])

    // The server gone: what waits is refused, and nothing more is sent.
    const late = c.request('textDocument/definition', {})
    Socket.last.onclose?.()
    await expect(late).rejects.toBeInstanceOf(ResponseError)
    await expect(c.request('x')).rejects.toThrow('not running')
  })

  it('says it could not be reached when the socket never opens (code-arena refused it)', async () => {
    vi.stubGlobal('WebSocket', Socket)
    const c = new LspConnection('ws://x/api/lsp/socket?server=gopls')
    Socket.last.onerror?.()
    Socket.last.onclose?.()
    await expect(c.opened).rejects.toThrow('could not be reached')
    await c.closed
  })
})
