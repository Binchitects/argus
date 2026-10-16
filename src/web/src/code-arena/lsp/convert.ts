import type * as Monaco from 'monaco-editor'

/**
 * The Language Server Protocol's shapes the page reads and writes, and how each becomes Monaco's. Lines and characters
 * count from 0 in the protocol and from 1 in Monaco; both count characters in UTF-16 code units (the protocol's
 * default), so a column is a character plus one.
 */

export interface Position {
  line: number
  character: number
}
export interface Range {
  start: Position
  end: Position
}
export interface Location {
  uri: string
  range: Range
}
export interface LocationLink {
  originSelectionRange?: Range
  targetUri: string
  targetRange: Range
  targetSelectionRange: Range
}
export interface TextEdit {
  range: Range
  newText: string
}
export interface Command {
  title: string
  command: string
  arguments?: unknown[]
}
export interface MarkupContent {
  kind: 'markdown' | 'plaintext'
  value: string
}
export type MarkedString = string | { language: string; value: string }
export interface Diagnostic {
  range: Range
  severity?: number
  code?: number | string
  codeDescription?: { href: string }
  source?: string
  message: string
  tags?: number[]
  relatedInformation?: { location: Location; message: string }[]
  data?: unknown
}
export interface TextDocumentEdit {
  textDocument: { uri: string; version?: number | null }
  edits: TextEdit[]
}
export interface WorkspaceEdit {
  changes?: Record<string, TextEdit[]>
  documentChanges?: (TextDocumentEdit | { kind: 'create' | 'rename' | 'delete' })[]
}
export interface CompletionItem {
  label: string
  labelDetails?: { detail?: string; description?: string }
  kind?: number
  tags?: number[]
  detail?: string
  documentation?: string | MarkupContent
  deprecated?: boolean
  preselect?: boolean
  sortText?: string
  filterText?: string
  insertText?: string
  insertTextFormat?: number
  textEdit?: TextEdit | { newText: string; insert: Range; replace: Range }
  textEditText?: string
  additionalTextEdits?: TextEdit[]
  commitCharacters?: string[]
  command?: Command
  data?: unknown
}
export interface CompletionList {
  isIncomplete: boolean
  itemDefaults?: { editRange?: Range | { insert: Range; replace: Range }; insertTextFormat?: number; commitCharacters?: string[]; data?: unknown }
  items: CompletionItem[]
}
export interface DocumentSymbol {
  name: string
  detail?: string
  kind: number
  tags?: number[]
  deprecated?: boolean
  range: Range
  selectionRange: Range
  children?: DocumentSymbol[]
}
export interface SymbolInformation {
  name: string
  kind: number
  tags?: number[]
  deprecated?: boolean
  location: Location
  containerName?: string
}
export interface CodeAction {
  title: string
  kind?: string
  diagnostics?: Diagnostic[]
  isPreferred?: boolean
  disabled?: { reason: string }
  edit?: WorkspaceEdit
  command?: Command
  data?: unknown
}

type M = typeof Monaco

export const toRange = (r: Range): Monaco.IRange => ({ startLineNumber: r.start.line + 1, startColumn: r.start.character + 1, endLineNumber: r.end.line + 1, endColumn: r.end.character + 1 })

export const fromPosition = (p: Monaco.IPosition): Position => ({ line: p.lineNumber - 1, character: p.column - 1 })

export const fromRange = (r: Monaco.IRange): Range => ({ start: { line: r.startLineNumber - 1, character: r.startColumn - 1 }, end: { line: r.endLineNumber - 1, character: r.endColumn - 1 } })

/** Text as markdown that shows it as it is. */
const escape = (text: string) => text.replace(/[\\`*_{}[\]()#+\-.!|<>]/g, '\\$&')

/** Documentation, a hover's contents: markdown as it is, plain text escaped, code in a fenced block. */
export function toMarkdown(c: string | MarkupContent | MarkedString | null | undefined, plainIsMarkdown = false): Monaco.IMarkdownString | null {
  if (c === null || c === undefined) return null
  if (typeof c === 'string') return c ? { value: plainIsMarkdown ? c : escape(c) } : null
  if ('kind' in c) return c.value ? { value: c.kind === 'markdown' ? c.value : escape(c.value) } : null
  return { value: `\`\`\`${c.language}\n${c.value}\n\`\`\`` }
}

/** A hover's contents, in any of the shapes the protocol has had. */
export function hoverContents(contents: MarkupContent | MarkedString | MarkedString[]): Monaco.IMarkdownString[] {
  const list = Array.isArray(contents) ? contents : [contents]
  // A MarkedString that is a string is markdown.
  return list.map((c) => toMarkdown(c, true)).filter((x): x is Monaco.IMarkdownString => x !== null)
}

// The protocol's kinds by number (from 1), as Monaco names them.
const completionKinds = [
  'Text', 'Method', 'Function', 'Constructor', 'Field', 'Variable', 'Class', 'Interface', 'Module', 'Property', 'Unit', 'Value', 'Enum', 'Keyword', 'Snippet', 'Color', 'File',
  'Reference', 'Folder', 'EnumMember', 'Constant', 'Struct', 'Event', 'Operator', 'TypeParameter',
] as const
// Monaco's CompletionItemKind, by name: its numbers are not the protocol's.
const monacoCompletionKinds: Record<(typeof completionKinds)[number], number> = {
  Method: 0, Function: 1, Constructor: 2, Field: 3, Variable: 4, Class: 5, Struct: 6, Interface: 7, Module: 8, Property: 9, Event: 10, Operator: 11, Unit: 12, Value: 13,
  Constant: 14, Enum: 15, EnumMember: 16, Keyword: 17, Text: 18, Color: 19, File: 20, Reference: 21, Folder: 23, TypeParameter: 24, Snippet: 28,
}

export const completionKind = (kind: number | undefined): Monaco.languages.CompletionItemKind =>
  monacoCompletionKinds[completionKinds[(kind ?? 1) - 1] ?? 'Text'] as Monaco.languages.CompletionItemKind

/** A symbol's kind: Monaco's are the protocol's, from 0. */
export const symbolKind = (kind: number): Monaco.languages.SymbolKind => Math.max(0, Math.min(25, kind - 1)) as Monaco.languages.SymbolKind

/** The marker severities, by the protocol's (1 error … 4 hint). */
export const severity = (s: number | undefined): Monaco.MarkerSeverity => ([8, 4, 2, 1][(s ?? 1) - 1] ?? 8) as Monaco.MarkerSeverity

/** A diagnostic as the editor marks it. */
export function toMarker(m: M, d: Diagnostic, source: string): Monaco.editor.IMarkerData {
  const code = d.code === undefined ? undefined : d.codeDescription?.href ? { value: String(d.code), target: m.Uri.parse(d.codeDescription.href) } : String(d.code)
  return {
    ...toRange(d.range),
    severity: severity(d.severity),
    message: d.message,
    source: d.source ?? source,
    code,
    tags: d.tags?.filter((t) => t === 1 || t === 2) as Monaco.MarkerTag[] | undefined,
    relatedInformation: d.relatedInformation?.map((r) => ({ resource: m.Uri.parse(r.location.uri), message: r.message, ...toRange(r.location.range) })),
  }
}

/** Definitions, references: the protocol's one, many, or links, as Monaco's. */
export function toLocations(m: M, result: Location | Location[] | LocationLink[] | null): Monaco.languages.LocationLink[] {
  if (!result) return []
  const list: (Location | LocationLink)[] = Array.isArray(result) ? result : [result]
  return list.map((l) =>
    'targetUri' in l
      ? { uri: m.Uri.parse(l.targetUri), range: toRange(l.targetRange), targetSelectionRange: toRange(l.targetSelectionRange), originSelectionRange: l.originSelectionRange && toRange(l.originSelectionRange) }
      : { uri: m.Uri.parse(l.uri), range: toRange(l.range) },
  )
}

export function toSymbols(list: (DocumentSymbol | SymbolInformation)[] | null): Monaco.languages.DocumentSymbol[] {
  return (list ?? []).map((s) =>
    'location' in s
      ? { name: s.name, detail: '', kind: symbolKind(s.kind), tags: s.deprecated ? [1] : (s.tags ?? []), containerName: s.containerName, range: toRange(s.location.range), selectionRange: toRange(s.location.range) }
      : { name: s.name, detail: s.detail ?? '', kind: symbolKind(s.kind), tags: s.deprecated ? [1] : (s.tags ?? []), range: toRange(s.range), selectionRange: toRange(s.selectionRange), children: toSymbols(s.children ?? []) },
  )
}

export const toTextEdits = (edits: TextEdit[] | null): Monaco.languages.TextEdit[] => (edits ?? []).map((e) => ({ range: toRange(e.range), text: e.newText }))

/** A workspace edit's text edits by file (its URI); file operations (create, rename, delete) are not done here. */
export function editsByUri(edit: WorkspaceEdit): { files: Map<string, TextEdit[]>; fileOperations: boolean } {
  const files = new Map<string, TextEdit[]>()
  let fileOperations = false
  const add = (uri: string, edits: TextEdit[]) => files.set(uri, [...(files.get(uri) ?? []), ...edits])
  if (edit.documentChanges) {
    for (const c of edit.documentChanges) {
      if ('textDocument' in c) add(c.textDocument.uri, c.edits)
      else fileOperations = true
    }
  } else {
    for (const [uri, edits] of Object.entries(edit.changes ?? {})) add(uri, edits)
  }
  return { files, fileOperations }
}

/** A workspace edit as Monaco applies it (to the files the editor holds: the caller loads them first). */
export function toWorkspaceEdit(m: M, edit: WorkspaceEdit): Monaco.languages.WorkspaceEdit {
  const edits: Monaco.languages.IWorkspaceTextEdit[] = []
  for (const [uri, list] of editsByUri(edit).files) {
    const resource = m.Uri.parse(uri)
    for (const e of list) edits.push({ resource, textEdit: { range: toRange(e.range), text: e.newText }, versionId: undefined })
  }
  return { edits }
}

/** The protocol's language id for a model: Monaco reads .tsx as TypeScript, a server as TypeScript React. */
export function languageIdOf(language: string, path: string): string {
  const ext = path.slice(path.lastIndexOf('.') + 1).toLowerCase()
  if (language === 'typescript' && ext === 'tsx') return 'typescriptreact'
  if (language === 'javascript' && ext === 'jsx') return 'javascriptreact'
  if (language === 'shell') return 'shellscript'
  return language
}

/**
 * Settings as a server asks for them (workspace/configuration's sections, didChangeConfiguration): the flat keys of
 * settings.json ("python.analysis.typeCheckingMode") as nested objects, with the server's own from config.json over them.
 */
export function configurationOf(flat: Record<string, unknown>, own: Record<string, unknown> | null | undefined): Record<string, unknown> {
  const out: Record<string, unknown> = {}
  const isObject = (v: unknown): v is Record<string, unknown> => !!v && typeof v === 'object' && !Array.isArray(v)
  const merge = (into: Record<string, unknown>, over: Record<string, unknown>) => {
    for (const [k, v] of Object.entries(over)) {
      if (isObject(v) && isObject(into[k])) merge(into[k], v)
      else into[k] = isObject(v) ? merge({}, v) : v
    }
    return into
  }
  for (const [key, value] of Object.entries(flat)) {
    if (key.startsWith('[')) continue
    const parts = key.split('.')
    let at = out
    for (const p of parts.slice(0, -1)) {
      if (!isObject(at[p])) at[p] = {}
      at = at[p] as Record<string, unknown>
    }
    const last = parts.at(-1)!
    at[last] = isObject(value) && isObject(at[last]) ? merge(at[last], value) : isObject(value) ? merge({}, value) : value
  }
  return own ? merge(out, own) : out
}

/** One section of the settings ("python.analysis"): null when there is none. */
export function sectionOf(config: Record<string, unknown>, section: string | undefined): unknown {
  if (!section) return config
  let at: unknown = config
  for (const p of section.split('.')) {
    if (!at || typeof at !== 'object' || Array.isArray(at)) return null
    at = (at as Record<string, unknown>)[p]
  }
  return at ?? null
}
