import type * as Monaco from 'monaco-editor'
import * as c from './convert'
import { codes, LspConnection, ResponseError, type Cancellation } from './rpc'

/**
 * One language server, spoken to by the page over code-arena's relay (Monaco's own LSP client syncs every model it
 * sees, so the page has its own): the folder's files of the server's languages kept in step with it (opened, changed,
 * closed, saved), never a compare's sides nor the person's settings files; what it answers given to Monaco as the
 * editor's completions, hovers, signature help, definitions, references, highlights, symbols, renames, code actions and
 * formatting; and its diagnostics marked in the files, as owner "lsp:" and its id.
 */

/** A language server as code-arena lists it (GET /api/lsp). */
export interface ServerInfo {
  id: string
  name: string
  languages: string[]
  found: boolean
  path?: string | null
  version?: string | null
  reason?: string | null
  install?: string | null
  /** What it is answered with besides settings.json's (config.json's "settings", and Code Arena's own). */
  settings?: Record<string, unknown> | null
}

/** What a client needs of the page. */
export interface ClientHost {
  monaco: typeof Monaco
  /** A model's file in the folder; null for any other model (a compare's side, the person's settings files). */
  pathOf: (uri: Monaco.Uri) => string | null
  /** The settings as settings.json and the project's .vscode/settings.json give them, flat. */
  settings: () => Record<string, unknown>
  /** The folder's name, for the server's workspace folders. */
  folderName: () => string
  /** Opens the folder's files in tabs behind the one shown, so an edit can be applied to them. */
  keep: (paths: string[]) => Promise<void>
  /** Opens a file of the folder at a place (window/showDocument). */
  open: (path: string, at?: Monaco.IRange) => void
  /** A line for the server's output. */
  log: (line: string) => void
  /** The client changed (its state, its progress, its diagnostics): whoever shows it reads it again. */
  changed: () => void
  /** Formats with the project's own formatter when it has one for the file: true when it did (or tried). */
  formatWithProject: (model: Monaco.editor.ITextModel, path: string, range?: Monaco.IRange) => Promise<boolean>
  /** Says something to the person (window/showMessage's errors and warnings). */
  notify: (kind: 'error' | 'warning', text: string) => void
  /** Asks the person to choose one of these (window/showMessageRequest): the one chosen, or undefined. */
  ask: (message: string, actions: string[]) => Promise<string | undefined>
}

export type ClientState = 'starting' | 'ready' | 'ended'

/** How the server ended, as code-arena says it (the notification codeArena/ended), or as the page saw it. */
export interface Ending {
  reason: string
  /** Another page took the server over: not started again by itself. */
  takenOver: boolean
  /** code-arena stopped it (the folder no longer trusted, code-arena stopping): not started again by itself. */
  stopped: boolean
}

interface ServerCapabilities {
  textDocumentSync?: number | { openClose?: boolean; change?: number; save?: boolean | { includeText?: boolean } }
  completionProvider?: { triggerCharacters?: string[]; resolveProvider?: boolean }
  hoverProvider?: unknown
  signatureHelpProvider?: { triggerCharacters?: string[]; retriggerCharacters?: string[] }
  definitionProvider?: unknown
  declarationProvider?: unknown
  typeDefinitionProvider?: unknown
  implementationProvider?: unknown
  referencesProvider?: unknown
  documentHighlightProvider?: unknown
  documentSymbolProvider?: unknown
  codeActionProvider?: boolean | { codeActionKinds?: string[]; resolveProvider?: boolean }
  documentFormattingProvider?: unknown
  documentRangeFormattingProvider?: unknown
  renameProvider?: boolean | { prepareProvider?: boolean }
  executeCommandProvider?: { commands?: string[] }
}

/** The Monaco command a server's own command runs through (workspace/executeCommand): its arguments are the server's id and the command. */
export const executeCommandId = 'codeArena.lsp.executeCommand'
/** The Monaco command that applies a server's edit to files the editor does not hold yet (they open first): the server's id, the edit, and a command to run after it. */
export const applyEditId = 'codeArena.lsp.applyEdit'

const lspCompletionKinds = Array.from({ length: 25 }, (_, i) => i + 1)
const lspSymbolKinds = Array.from({ length: 26 }, (_, i) => i + 1)

/** What the page can do, as initialize tells the server. */
const clientCapabilities = {
  general: { positionEncodings: ['utf-16'] },
  workspace: {
    applyEdit: true,
    workspaceEdit: { documentChanges: true },
    didChangeConfiguration: { dynamicRegistration: false },
    didChangeWatchedFiles: { dynamicRegistration: false },
    executeCommand: { dynamicRegistration: false },
    configuration: true,
    workspaceFolders: true,
  },
  textDocument: {
    synchronization: { dynamicRegistration: false, willSave: false, willSaveWaitUntil: false, didSave: true },
    completion: {
      dynamicRegistration: false,
      contextSupport: true,
      completionItem: {
        snippetSupport: true,
        commitCharactersSupport: true,
        documentationFormat: ['markdown', 'plaintext'],
        deprecatedSupport: true,
        preselectSupport: true,
        tagSupport: { valueSet: [1] },
        insertReplaceSupport: true,
        labelDetailsSupport: true,
        resolveSupport: { properties: ['documentation', 'detail', 'additionalTextEdits'] },
      },
      completionItemKind: { valueSet: lspCompletionKinds },
      completionList: { itemDefaults: ['editRange', 'insertTextFormat', 'commitCharacters', 'data'] },
    },
    hover: { dynamicRegistration: false, contentFormat: ['markdown', 'plaintext'] },
    signatureHelp: {
      dynamicRegistration: false,
      contextSupport: true,
      signatureInformation: { documentationFormat: ['markdown', 'plaintext'], parameterInformation: { labelOffsetSupport: true }, activeParameterSupport: true },
    },
    declaration: { dynamicRegistration: false, linkSupport: true },
    definition: { dynamicRegistration: false, linkSupport: true },
    typeDefinition: { dynamicRegistration: false, linkSupport: true },
    implementation: { dynamicRegistration: false, linkSupport: true },
    references: { dynamicRegistration: false },
    documentHighlight: { dynamicRegistration: false },
    documentSymbol: { dynamicRegistration: false, hierarchicalDocumentSymbolSupport: true, symbolKind: { valueSet: lspSymbolKinds }, tagSupport: { valueSet: [1] } },
    codeAction: {
      dynamicRegistration: false,
      isPreferredSupport: true,
      disabledSupport: true,
      dataSupport: true,
      resolveSupport: { properties: ['edit'] },
      codeActionLiteralSupport: { codeActionKind: { valueSet: ['', 'quickfix', 'refactor', 'refactor.extract', 'refactor.inline', 'refactor.rewrite', 'source', 'source.organizeImports', 'source.fixAll'] } },
    },
    formatting: { dynamicRegistration: false },
    rangeFormatting: { dynamicRegistration: false },
    rename: { dynamicRegistration: false, prepareSupport: true },
    publishDiagnostics: { relatedInformation: true, tagSupport: { valueSet: [1, 2] }, codeDescriptionSupport: true },
  },
  window: { workDoneProgress: true, showMessage: { messageActionItem: { additionalPropertiesSupport: false } }, showDocument: { support: true } },
}

const overlaps = (a: Monaco.IRange, b: Monaco.IRange) =>
  !(a.endLineNumber < b.startLineNumber || (a.endLineNumber === b.startLineNumber && a.endColumn < b.startColumn) || a.startLineNumber > b.endLineNumber || (a.startLineNumber === b.endLineNumber && a.startColumn > b.endColumn))

export class LanguageClient {
  state: ClientState = 'starting'
  /** How it ended, once it did. */
  ending: Ending | null = null
  /** The work it reports (indexing, loading the project), by its token. */
  readonly progress = new Map<string, { title: string; message?: string; percentage?: number }>()
  /** Its diagnostics, by the file's URI as Monaco writes it: the files open and the others it named. */
  readonly diagnostics = new Map<string, c.Diagnostic[]>()
  /** Called once it ended (not when the page stopped it). */
  onEnd: (client: LanguageClient) => void = () => undefined
  private capabilities: ServerCapabilities = {}
  private readonly conn: LspConnection
  private readonly docs = new Map<string, { model: Monaco.editor.ITextModel; listening: Monaco.IDisposable }>()
  private readonly disposables: Monaco.IDisposable[] = []
  private readonly completions = new WeakMap<object, c.CompletionItem>()
  private readonly actions = new WeakMap<object, c.CodeAction>()
  private closing = false
  readonly info: ServerInfo
  private readonly host: ClientHost
  /** Its markers' owner: "lsp:" and its id. */
  readonly owner: string

  constructor(info: ServerInfo, host: ClientHost, url: string) {
    this.info = info
    this.host = host
    this.owner = `lsp:${info.id}`
    this.conn = new LspConnection(url)
    this.conn.onNotification = (method, params) => this.notified(method, params)
    this.conn.onRequest = (method, params) => this.asked(method, params)
    void this.conn.closed.then(() => this.closed())
  }

  /** Opens the socket and initializes the server: the folder's files of its languages are synced, and its features given to Monaco. */
  async start(): Promise<void> {
    try {
      await this.conn.opened
    } catch (e) {
      this.end({ reason: e instanceof Error ? e.message : String(e), takenOver: false, stopped: false })
      return
    }
    let result: { capabilities?: ServerCapabilities; serverInfo?: { name?: string; version?: string } } | null
    try {
      // code-arena sets processId, rootUri, rootPath, workspaceFolders and initializationOptions: the folder's and its own.
      result = await this.conn.request('initialize', {
        processId: null,
        clientInfo: { name: 'Code Arena' },
        locale: typeof navigator === 'undefined' ? 'en' : navigator.language,
        rootUri: 'file:///',
        workspaceFolders: [{ uri: 'file:///', name: this.host.folderName() }],
        capabilities: clientCapabilities,
        trace: 'off',
      })
    } catch (e) {
      if (this.state !== 'ended') this.end({ reason: `It did not start: ${e instanceof Error ? e.message : String(e)}`, takenOver: false, stopped: false })
      this.conn.close()
      return
    }
    if (this.closing || this.state === 'ended') return
    this.capabilities = result?.capabilities ?? {}
    if (result?.serverInfo?.name) this.host.log(`${result.serverInfo.name}${result.serverInfo.version ? ` ${result.serverInfo.version}` : ''} started.`)
    this.conn.notify('initialized', {})
    this.conn.notify('workspace/didChangeConfiguration', { settings: this.configuration() })
    this.register()
    this.state = 'ready'
    const m = this.host.monaco
    this.disposables.push(
      m.editor.onDidCreateModel((model) => this.sync(model)),
      m.editor.onWillDisposeModel((model) => this.unsync(model)),
      m.editor.onDidChangeModelLanguage(({ model }) => {
        this.unsync(model)
        this.sync(model)
      }),
    )
    for (const model of m.editor.getModels()) this.sync(model)
    this.host.changed()
  }

  /** Stops it: the socket closes, and code-arena shuts the server down. */
  stop() {
    this.closing = true
    this.conn.close()
    this.dispose()
  }

  /** Whether it keeps this model in step: a file of the folder, of one of its languages. */
  serves(model: Monaco.editor.ITextModel) {
    return model.uri.scheme === 'file' && this.host.pathOf(model.uri) !== null && this.info.languages.includes(model.getLanguageId())
  }

  /** The settings changed: the server is told. */
  configurationChanged() {
    if (this.state === 'ready') this.conn.notify('workspace/didChangeConfiguration', { settings: this.configuration() })
  }

  /** A file was saved: the server is told, when it asks to be. */
  saved(model: Monaco.editor.ITextModel) {
    const uri = model.uri.toString()
    if (this.state !== 'ready' || !this.docs.has(uri)) return
    const sync = this.capabilities.textDocumentSync
    const save = typeof sync === 'object' ? sync.save : undefined
    if (!save) return
    this.conn.notify('textDocument/didSave', { textDocument: { uri }, ...(typeof save === 'object' && save.includeText ? { text: model.getValue() } : {}) })
  }

  /** Files changed on disk (by the agent, a command): the server is told (1 made, 2 changed, 3 deleted). */
  filesChanged(changes: { path: string; type: 1 | 2 | 3 }[]) {
    if (this.state !== 'ready' || changes.length === 0) return
    const m = this.host.monaco
    this.conn.notify('workspace/didChangeWatchedFiles', { changes: changes.map((ch) => ({ uri: m.Uri.file(`/${ch.path}`).toString(), type: ch.type })) })
  }

  /** The server's own command (workspace/executeCommand). */
  async execute(command: c.Command) {
    await this.ask('workspace/executeCommand', { command: command.command, arguments: command.arguments })
  }

  /** The server's formatting of a file it keeps in step (of a range, when given); null when it formats none. */
  async format(model: Monaco.editor.ITextModel, options: Monaco.languages.FormattingOptions, range?: Monaco.IRange, token?: Cancellation): Promise<Monaco.languages.TextEdit[] | null> {
    const uri = this.uriOf(model)
    if (!uri || !(range ? this.capabilities.documentRangeFormattingProvider : this.capabilities.documentFormattingProvider)) return null
    const opts = { tabSize: options.tabSize, insertSpaces: options.insertSpaces }
    const edits = range
      ? await this.ask<c.TextEdit[]>('textDocument/rangeFormatting', { textDocument: { uri }, range: c.fromRange(range), options: opts }, token)
      : await this.ask<c.TextEdit[]>('textDocument/formatting', { textDocument: { uri }, options: opts }, token)
    return c.toTextEdits(edits)
  }

  /** Applies a server's edit, opening the folder's files it changes that the editor does not hold yet. */
  async apply(edit: c.WorkspaceEdit): Promise<{ applied: boolean; failureReason?: string }> {
    const { files, fileOperations } = c.editsByUri(edit)
    if (fileOperations) return { applied: false, failureReason: 'Code Arena does not make, rename or delete files for a language server.' }
    await this.load(edit)
    const m = this.host.monaco
    const models: [Monaco.editor.ITextModel, c.TextEdit[]][] = []
    for (const [uri, edits] of files) {
      const model = m.editor.getModel(m.Uri.parse(uri))
      if (!model) return { applied: false, failureReason: `${uri} is not a file of the folder.` }
      models.push([model, edits])
    }
    for (const [model, edits] of models) {
      model.pushStackElement()
      model.pushEditOperations([], edits.map((e) => ({ range: c.toRange(e.range), text: e.newText })), () => null)
      model.pushStackElement()
    }
    return { applied: true }
  }

  private configuration() {
    return c.configurationOf(this.host.settings(), this.info.settings)
  }

  /** The URI the server knows a model by, when it keeps it in step. */
  private uriOf(model: Monaco.editor.ITextModel): string | null {
    const uri = model.uri.toString()
    return this.docs.has(uri) ? uri : null
  }

  private sync(model: Monaco.editor.ITextModel) {
    const uri = model.uri.toString()
    if (this.state !== 'ready' || this.docs.has(uri) || model.isDisposed() || !this.serves(model)) return
    const path = this.host.pathOf(model.uri)!
    this.conn.notify('textDocument/didOpen', { textDocument: { uri, languageId: c.languageIdOf(model.getLanguageId(), path), version: model.getVersionId(), text: model.getValue() } })
    const listening = model.onDidChangeContent((e?: Monaco.editor.IModelContentChangedEvent) => this.changedContent(model, uri, e))
    this.docs.set(uri, { model, listening })
    this.mark(uri)
  }

  private unsync(model: Monaco.editor.ITextModel) {
    const uri = model.uri.toString()
    const doc = this.docs.get(uri)
    if (!doc) return
    doc.listening.dispose()
    this.docs.delete(uri)
    this.host.monaco.editor.setModelMarkers(model, this.owner, [])
    this.conn.notify('textDocument/didClose', { textDocument: { uri } })
  }

  private changedContent(model: Monaco.editor.ITextModel, uri: string, e?: Monaco.editor.IModelContentChangedEvent) {
    const sync = this.capabilities.textDocumentSync
    const kind = typeof sync === 'number' ? sync : (sync?.change ?? 0)
    if (kind === 0 || model.isDisposed()) return
    // Incremental: Monaco's changes of one edit are in an order that applies one after the other, as the protocol's do.
    const contentChanges =
      kind === 2 && e?.changes?.length ? e.changes.map((ch) => ({ range: c.fromRange(ch.range), rangeLength: ch.rangeLength, text: ch.text })) : [{ text: model.getValue() }]
    this.conn.notify('textDocument/didChange', { textDocument: { uri, version: model.getVersionId() }, contentChanges })
  }

  /** A file's diagnostics marked, when the editor holds it. */
  private mark(uri: string) {
    const m = this.host.monaco
    const model = m.editor.getModel(m.Uri.parse(uri))
    if (!model || !this.docs.has(model.uri.toString())) return
    m.editor.setModelMarkers(model, this.owner, (this.diagnostics.get(uri) ?? []).map((d) => c.toMarker(m, d, this.info.name)))
  }

  /** A request whose failure is logged, not thrown (a provider that throws makes Monaco log it as unexpected): null then. */
  private async ask<T>(method: string, params: unknown, token?: Cancellation): Promise<T | null> {
    try {
      return await this.conn.request<T>(method, params, token)
    } catch (e) {
      if (!(e instanceof ResponseError && [codes.requestCancelled, codes.contentModified, codes.serverCancelled].includes(e.code)) && !this.closing) {
        this.host.log(`${method} failed: ${e instanceof Error ? e.message : String(e)}`)
      }
      return null
    }
  }

  /** A command of the server's as Monaco runs it: through workspace/executeCommand when it is the server's, else Monaco's own (editor.action.triggerParameterHints). */
  private command(cmd: c.Command | undefined): Monaco.languages.Command | undefined {
    if (!cmd) return undefined
    if (this.capabilities.executeCommandProvider?.commands?.includes(cmd.command)) return { id: executeCommandId, title: cmd.title, arguments: [this.info.id, cmd] }
    return { id: cmd.command, title: cmd.title, arguments: cmd.arguments }
  }

  /** Whether every file an edit changes is held by the editor (Monaco applies edits to those only). */
  private held(edit: c.WorkspaceEdit) {
    const m = this.host.monaco
    return [...c.editsByUri(edit).files.keys()].every((uri) => m.editor.getModel(m.Uri.parse(uri)))
  }

  /** The folder's files an edit changes, opened (behind the tab shown) when the editor does not hold them yet. */
  private async load(edit: c.WorkspaceEdit) {
    const m = this.host.monaco
    const paths = [...c.editsByUri(edit).files.keys()]
      .map((uri) => m.Uri.parse(uri))
      .filter((uri) => !m.editor.getModel(uri))
      .map((uri) => this.host.pathOf(uri))
      .filter((p): p is string => p !== null)
    if (paths.length > 0) await this.host.keep(paths)
  }

  private toCompletion(item: c.CompletionItem, defaults: c.CompletionList['itemDefaults'], range: Monaco.languages.CompletionItemRanges): Monaco.languages.CompletionItem {
    const edit = item.textEdit
    const defaultRange = defaults?.editRange
    const at: Monaco.IRange | Monaco.languages.CompletionItemRanges = edit
      ? 'range' in edit
        ? c.toRange(edit.range)
        : { insert: c.toRange(edit.insert), replace: c.toRange(edit.replace) }
      : defaultRange
        ? 'start' in defaultRange
          ? c.toRange(defaultRange)
          : { insert: c.toRange(defaultRange.insert), replace: c.toRange(defaultRange.replace) }
        : range
    const format = item.insertTextFormat ?? defaults?.insertTextFormat
    const own: Monaco.languages.CompletionItem = {
      label: item.labelDetails ? { label: item.label, detail: item.labelDetails.detail, description: item.labelDetails.description } : item.label,
      kind: c.completionKind(item.kind),
      tags: item.deprecated || item.tags?.includes(1) ? [1] : undefined,
      detail: item.detail,
      documentation: typeof item.documentation === 'object' ? (item.documentation.kind === 'markdown' ? { value: item.documentation.value } : item.documentation.value) : item.documentation,
      sortText: item.sortText,
      filterText: item.filterText,
      preselect: item.preselect,
      insertText: edit?.newText ?? item.textEditText ?? item.insertText ?? item.label,
      insertTextRules: format === 2 ? 4 : undefined,
      range: at,
      commitCharacters: item.commitCharacters ?? defaults?.commitCharacters,
      additionalTextEdits: item.additionalTextEdits?.map((e) => ({ range: c.toRange(e.range), text: e.newText })),
      command: this.command(item.command),
    }
    this.completions.set(own, defaults?.data !== undefined && item.data === undefined ? { ...item, data: defaults.data } : item)
    return own
  }

  private toAction(a: c.CodeAction | c.Command): Monaco.languages.CodeAction {
    if (typeof a.command === 'string') return { title: a.title, command: this.command(a as c.Command) }
    const action = a as c.CodeAction
    const m = this.host.monaco
    const own: Monaco.languages.CodeAction = {
      title: action.title,
      kind: action.kind,
      isPreferred: action.isPreferred,
      disabled: action.disabled?.reason,
      diagnostics: action.diagnostics?.map((d) => c.toMarker(m, d, this.info.name)),
    }
    this.fill(own, action)
    this.actions.set(own, action)
    return own
  }

  /** A code action's edit and command: the edit as Monaco's when it holds every file, else a command that opens them and applies it. */
  private fill(own: Monaco.languages.CodeAction, action: c.CodeAction) {
    if (action.edit && !this.held(action.edit)) {
      own.edit = undefined
      own.command = { id: applyEditId, title: action.title, arguments: [this.info.id, action.edit, action.command] }
      return
    }
    own.edit = action.edit ? c.toWorkspaceEdit(this.host.monaco, action.edit) : undefined
    own.command = this.command(action.command)
  }

  /** The server's features, as Monaco's providers for its languages: each answers for the files it keeps in step only. */
  private register() {
    const m = this.host.monaco
    const cap = this.capabilities
    const languages = this.info.languages
    const add = (d: Monaco.IDisposable | undefined) => d && this.disposables.push(d)
    const doc = (model: Monaco.editor.ITextModel, position: Monaco.IPosition) => ({ textDocument: { uri: model.uri.toString() }, position: c.fromPosition(position) })

    if (cap.completionProvider) {
      add(
        m.languages.registerCompletionItemProvider(languages, {
          triggerCharacters: cap.completionProvider.triggerCharacters,
          provideCompletionItems: async (model, position, context, token) => {
            if (!this.uriOf(model)) return undefined
            const result = await this.ask<c.CompletionList | c.CompletionItem[]>(
              'textDocument/completion',
              { ...doc(model, position), context: { triggerKind: context.triggerKind + 1, triggerCharacter: context.triggerCharacter } },
              token,
            )
            if (!result) return { suggestions: [] }
            const list: c.CompletionList = Array.isArray(result) ? { isIncomplete: false, items: result } : result
            const word = model.getWordUntilPosition(position)
            const range = {
              insert: { startLineNumber: position.lineNumber, startColumn: word.startColumn, endLineNumber: position.lineNumber, endColumn: position.column },
              replace: { startLineNumber: position.lineNumber, startColumn: word.startColumn, endLineNumber: position.lineNumber, endColumn: model.getWordAtPosition(position)?.endColumn ?? position.column },
            }
            return { suggestions: list.items.map((item) => this.toCompletion(item, list.itemDefaults, range)), incomplete: list.isIncomplete }
          },
          resolveCompletionItem: cap.completionProvider.resolveProvider
            ? async (item, token) => {
                const lsp = this.completions.get(item)
                if (!lsp) return item
                const done = await this.ask<c.CompletionItem>('completionItem/resolve', lsp, token)
                if (!done) return item
                if (done.documentation) item.documentation = typeof done.documentation === 'object' ? (done.documentation.kind === 'markdown' ? { value: done.documentation.value } : done.documentation.value) : done.documentation
                if (done.detail) item.detail = done.detail
                if (done.additionalTextEdits) item.additionalTextEdits = done.additionalTextEdits.map((e) => ({ range: c.toRange(e.range), text: e.newText }))
                if (done.command) item.command = this.command(done.command)
                return item
              }
            : undefined,
        }),
      )
    }
    if (cap.hoverProvider) {
      add(
        m.languages.registerHoverProvider(languages, {
          provideHover: async (model, position, token) => {
            if (!this.uriOf(model)) return undefined
            const h = await this.ask<{ contents: c.MarkupContent | c.MarkedString | c.MarkedString[]; range?: c.Range }>('textDocument/hover', doc(model, position), token)
            const contents = h ? c.hoverContents(h.contents) : []
            return contents.length ? { contents, range: h?.range && c.toRange(h.range) } : undefined
          },
        }),
      )
    }
    if (cap.signatureHelpProvider) {
      add(
        m.languages.registerSignatureHelpProvider(languages, {
          signatureHelpTriggerCharacters: cap.signatureHelpProvider.triggerCharacters,
          signatureHelpRetriggerCharacters: cap.signatureHelpProvider.retriggerCharacters,
          provideSignatureHelp: async (model, position, token, context) => {
            if (!this.uriOf(model)) return undefined
            type Signature = { label: string; documentation?: string | c.MarkupContent; parameters?: { label: string | [number, number]; documentation?: string | c.MarkupContent }[]; activeParameter?: number }
            const s = await this.ask<{ signatures: Signature[]; activeSignature?: number; activeParameter?: number }>(
              'textDocument/signatureHelp',
              { ...doc(model, position), context: { triggerKind: context.triggerKind, triggerCharacter: context.triggerCharacter, isRetrigger: context.isRetrigger } },
              token,
            )
            if (!s || s.signatures.length === 0) return undefined
            return {
              value: {
                signatures: s.signatures.map((sig) => ({
                  label: sig.label,
                  documentation: c.toMarkdown(sig.documentation) ?? undefined,
                  parameters: (sig.parameters ?? []).map((p) => ({ label: p.label, documentation: c.toMarkdown(p.documentation) ?? undefined })),
                  activeParameter: sig.activeParameter,
                })),
                activeSignature: s.activeSignature ?? 0,
                activeParameter: s.activeParameter ?? 0,
              },
              dispose: () => undefined,
            }
          },
        }),
      )
    }
    const locations = (method: string) => async (model: Monaco.editor.ITextModel, position: Monaco.Position, token: Monaco.CancellationToken) => {
      if (!this.uriOf(model)) return undefined
      const found = c.toLocations(m, await this.ask<c.Location | c.Location[] | c.LocationLink[]>(method, doc(model, position), token))
      // The folder's own first: a place outside it (a library's declaration beside the folder's) cannot be shown in the
      // editor, so it is offered only when there is no other (and opening it says why it does not open).
      const inside = found.filter((l) => l.uri.scheme !== 'outside')
      return inside.length ? inside : found.slice(0, 1)
    }
    if (cap.definitionProvider) add(m.languages.registerDefinitionProvider(languages, { provideDefinition: locations('textDocument/definition') }))
    if (cap.declarationProvider) add(m.languages.registerDeclarationProvider?.(languages, { provideDeclaration: locations('textDocument/declaration') }))
    if (cap.typeDefinitionProvider) add(m.languages.registerTypeDefinitionProvider?.(languages, { provideTypeDefinition: locations('textDocument/typeDefinition') }))
    if (cap.implementationProvider) add(m.languages.registerImplementationProvider?.(languages, { provideImplementation: locations('textDocument/implementation') }))
    if (cap.referencesProvider) {
      add(
        m.languages.registerReferenceProvider(languages, {
          provideReferences: async (model, position, context, token) => {
            if (!this.uriOf(model)) return undefined
            const found = await this.ask<c.Location[]>('textDocument/references', { ...doc(model, position), context: { includeDeclaration: context.includeDeclaration } }, token)
            // The folder's files only: the editor cannot show one outside it.
            return (found ?? []).map((l) => ({ uri: m.Uri.parse(l.uri), range: c.toRange(l.range) })).filter((l) => l.uri.scheme !== 'outside')
          },
        }),
      )
    }
    if (cap.documentHighlightProvider) {
      add(
        m.languages.registerDocumentHighlightProvider(languages, {
          provideDocumentHighlights: async (model, position, token) => {
            if (!this.uriOf(model)) return undefined
            const found = await this.ask<{ range: c.Range; kind?: number }[]>('textDocument/documentHighlight', doc(model, position), token)
            return (found ?? []).map((h) => ({ range: c.toRange(h.range), kind: ((h.kind ?? 1) - 1) as Monaco.languages.DocumentHighlightKind }))
          },
        }),
      )
    }
    if (cap.documentSymbolProvider) {
      add(
        m.languages.registerDocumentSymbolProvider(languages, {
          displayName: this.info.name,
          provideDocumentSymbols: async (model, token) => {
            const uri = this.uriOf(model)
            if (!uri) return undefined
            return c.toSymbols(await this.ask<(c.DocumentSymbol | c.SymbolInformation)[]>('textDocument/documentSymbol', { textDocument: { uri } }, token))
          },
        }),
      )
    }
    if (cap.renameProvider) {
      const prepare = typeof cap.renameProvider === 'object' && cap.renameProvider.prepareProvider
      add(
        m.languages.registerRenameProvider(languages, {
          provideRenameEdits: async (model, position, newName, token) => {
            if (!this.uriOf(model)) return undefined
            const edit = await this.ask<c.WorkspaceEdit>('textDocument/rename', { ...doc(model, position), newName }, token)
            if (!edit) return { edits: [], rejectReason: 'The language server made no change.' }
            if (c.editsByUri(edit).fileOperations) return { edits: [], rejectReason: 'This rename also renames files: Code Arena does not do that for a language server.' }
            // Monaco changes only the files the editor holds: the others open first, behind the tab shown.
            await this.load(edit)
            return c.toWorkspaceEdit(m, edit)
          },
          resolveRenameLocation: prepare
            ? async (model, position, token) => {
                if (!this.uriOf(model)) return undefined
                const r = await this.ask<c.Range | { range: c.Range; placeholder: string } | { defaultBehavior: boolean }>('textDocument/prepareRename', doc(model, position), token)
                const word = model.getWordAtPosition(position)
                const wordRange = word ? { startLineNumber: position.lineNumber, startColumn: word.startColumn, endLineNumber: position.lineNumber, endColumn: word.endColumn } : null
                if (!r) return { range: wordRange ?? { startLineNumber: position.lineNumber, startColumn: position.column, endLineNumber: position.lineNumber, endColumn: position.column }, text: '', rejectReason: 'This cannot be renamed.' }
                if ('defaultBehavior' in r) return wordRange ? { range: wordRange, text: word!.word } : undefined
                if ('placeholder' in r) return { range: c.toRange(r.range), text: r.placeholder }
                return { range: c.toRange(r), text: model.getValueInRange(c.toRange(r)) }
              }
            : undefined,
        }),
      )
    }
    if (cap.codeActionProvider) {
      const resolve = typeof cap.codeActionProvider === 'object' && cap.codeActionProvider.resolveProvider
      add(
        m.languages.registerCodeActionProvider(
          languages,
          {
            provideCodeActions: async (model, range, context, token) => {
              const uri = this.uriOf(model)
              if (!uri) return undefined
              const diagnostics = (this.diagnostics.get(uri) ?? []).filter((d) => overlaps(c.toRange(d.range), range))
              const found = await this.ask<(c.CodeAction | c.Command)[]>(
                'textDocument/codeAction',
                { textDocument: { uri }, range: c.fromRange(range), context: { diagnostics, only: context.only ? [context.only] : undefined, triggerKind: context.trigger } },
                token,
              )
              return { actions: (found ?? []).map((a) => this.toAction(a)), dispose: () => undefined }
            },
            resolveCodeAction: resolve
              ? async (own, token) => {
                  const action = this.actions.get(own)
                  if (!action || action.edit) return own
                  const done = await this.ask<c.CodeAction>('codeAction/resolve', action, token)
                  if (done) this.fill(own, done)
                  return own
                }
              : undefined,
          },
          { providedCodeActionKinds: typeof cap.codeActionProvider === 'object' ? cap.codeActionProvider.codeActionKinds : undefined },
        ),
      )
    }
    const format = async (model: Monaco.editor.ITextModel, options: Monaco.languages.FormattingOptions, token: Monaco.CancellationToken, range?: Monaco.IRange) => {
      const path = this.host.pathOf(model.uri)
      if (!this.uriOf(model) || !path) return undefined
      // The project's own formatter (Prettier, ruff, gofmt…) comes first, as format.ts has it.
      if (await this.host.formatWithProject(model, path, range)) return []
      return (await this.format(model, options, range, token)) ?? undefined
    }
    if (cap.documentFormattingProvider) {
      add(m.languages.registerDocumentFormattingEditProvider(languages, { displayName: this.info.name, provideDocumentFormattingEdits: (model, options, token) => format(model, options, token) }))
    }
    if (cap.documentRangeFormattingProvider) {
      add(m.languages.registerDocumentRangeFormattingEditProvider(languages, { displayName: this.info.name, provideDocumentRangeFormattingEdits: (model, range, options, token) => format(model, options, token, range) }))
    }
  }

  private notified(method: string, params: unknown) {
    switch (method) {
      case 'textDocument/publishDiagnostics': {
        const p = params as { uri: string; diagnostics: c.Diagnostic[] }
        const m = this.host.monaco
        const uri = m.Uri.parse(p.uri).toString()
        if (p.diagnostics.length) this.diagnostics.set(uri, p.diagnostics)
        else this.diagnostics.delete(uri)
        this.mark(uri)
        this.host.changed()
        break
      }
      case 'window/logMessage':
      case 'window/showMessage': {
        const p = params as { type: number; message: string }
        const kind = ['Error', 'Warning', 'Info', 'Log', 'Debug'][p.type - 1] ?? 'Log'
        this.host.log(`[${kind}] ${p.message}`)
        if (method === 'window/showMessage' && (p.type === 1 || p.type === 2)) this.host.notify(p.type === 1 ? 'error' : 'warning', `${this.info.name}: ${p.message}`)
        break
      }
      case '$/logTrace':
        this.host.log((params as { message: string }).message)
        break
      case '$/progress': {
        const p = params as { token: string | number; value?: { kind?: string; title?: string; message?: string; percentage?: number } }
        const key = String(p.token)
        const v = p.value
        if (!v?.kind) break
        if (v.kind === 'end') this.progress.delete(key)
        else {
          const was = this.progress.get(key)
          this.progress.set(key, { title: v.title ?? was?.title ?? '', message: v.message ?? was?.message, percentage: v.percentage ?? was?.percentage })
        }
        this.host.changed()
        break
      }
      case 'codeArena/ended': {
        const p = params as { reason?: string; takenOver?: boolean; stopped?: boolean }
        this.ending = { reason: p.reason ?? 'The language server ended.', takenOver: !!p.takenOver, stopped: !!p.stopped }
        break
      }
      default:
        // telemetry/event and the like: nothing to show.
        break
    }
  }

  private async asked(method: string, params: unknown): Promise<unknown> {
    switch (method) {
      case 'workspace/configuration': {
        const config = this.configuration()
        return ((params as { items?: { section?: string }[] }).items ?? []).map((i) => c.sectionOf(config, i.section))
      }
      case 'workspace/workspaceFolders':
        return [{ uri: 'file:///', name: this.host.folderName() }]
      case 'client/registerCapability':
      case 'client/unregisterCapability':
      case 'window/workDoneProgress/create':
      case 'workspace/codeLens/refresh':
      case 'workspace/semanticTokens/refresh':
      case 'workspace/inlayHint/refresh':
      case 'workspace/inlineValue/refresh':
      case 'workspace/diagnostic/refresh':
      case 'workspace/foldingRange/refresh':
        return null
      case 'workspace/applyEdit':
        return this.apply((params as { edit: c.WorkspaceEdit }).edit)
      case 'window/showDocument': {
        const p = params as { uri: string; external?: boolean; selection?: c.Range }
        const path = p.external ? null : this.host.pathOf(this.host.monaco.Uri.parse(p.uri))
        if (!path) return { success: false }
        this.host.open(path, p.selection && c.toRange(p.selection))
        return { success: true }
      }
      case 'window/showMessageRequest': {
        const p = params as { type: number; message: string; actions?: { title: string }[] }
        this.host.log(`[${['Error', 'Warning', 'Info', 'Log'][p.type - 1] ?? 'Log'}] ${p.message}`)
        if (!p.actions?.length) {
          if (p.type === 1 || p.type === 2) this.host.notify(p.type === 1 ? 'error' : 'warning', `${this.info.name}: ${p.message}`)
          return null
        }
        const chosen = await this.host.ask(`${this.info.name}: ${p.message}`, p.actions.map((a) => a.title))
        return chosen === undefined ? null : { title: chosen }
      }
      case 'vscode/content':
        // The JSON server asks for a schema it does not read itself: nothing is fetched from the network here.
        throw new ResponseError(codes.internalError, 'Code Arena does not fetch schemas from the network.')
      default:
        throw new ResponseError(codes.methodNotFound, `Code Arena does not handle ${method}.`)
    }
  }

  private end(ending: Ending) {
    if (this.state === 'ended') return
    this.state = 'ended'
    this.ending ??= ending
    this.dispose()
    this.host.changed()
    if (!this.closing) this.onEnd(this)
  }

  /** The socket closed: the server ended (code-arena said why, last), or the page let it go. */
  private closed() {
    this.end({ reason: 'The language server ended.', takenOver: false, stopped: false })
  }

  private dispose() {
    const m = this.host.monaco
    for (const d of this.disposables.splice(0)) d.dispose()
    for (const [, doc] of this.docs) {
      doc.listening.dispose()
      if (!doc.model.isDisposed()) m.editor.setModelMarkers(doc.model, this.owner, [])
    }
    this.docs.clear()
    this.diagnostics.clear()
    this.progress.clear()
  }
}
