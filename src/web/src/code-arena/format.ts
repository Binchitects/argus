import type { QueryClient } from '@tanstack/react-query'
import type * as Monaco from 'monaco-editor'
import { toast } from '@/components/ui/toaster'
import { ApiError, errorMessage } from '@/lib/api'
import { runCommand } from './commands'
import type { MonacoModule } from './editor-state'
import { formatText, formattersQuery, type Formatters } from './ide-api'
import { addSaveParticipant, type SaveParticipant } from './save-participants'
import { askTrust } from './trust'

/**
 * Formatting as VS Code does it, with the project's own tools run by code-arena (Prettier from its node_modules, ruff,
 * gofmt, rustfmt, clang-format, dotnet format, the person's own from config.json): Format Document (Shift+Alt+F),
 * Format Selection, Format Document With…, format on save and on paste. A language with no formatter of the project's
 * keeps Monaco's own (TypeScript, JSON, CSS, HTML).
 */

/** VS Code's extension ids for these formatters, as "editor.defaultFormatter" names them. */
const extensionIds: Record<string, string> = {
  'esbenp.prettier-vscode': 'prettier',
  'prettier.prettier-vscode': 'prettier',
  'charliermarsh.ruff': 'ruff',
  'ms-python.black-formatter': 'black',
  'golang.go': 'gofmt',
  'rust-lang.rust-analyzer': 'rustfmt',
  'xaver.clang-format': 'clang-format',
  'ms-vscode.cpptools': 'clang-format',
  'ms-dotnettools.csharp': 'dotnet-format',
  'ms-dotnettools.csdevkit': 'dotnet-format',
}

/** "editor.defaultFormatter" as one of code-arena's formatters (VS Code's extension ids too); undefined: the server's first. */
export const formatterOf = (setting: unknown) => (typeof setting === 'string' && setting ? (extensionIds[setting.toLowerCase()] ?? setting) : undefined)

/** The one edit that turns a text into another: what differs between their common start and end (the cursor stays where nothing changed). */
export function minimalEdit(before: string, after: string): { start: number; end: number; text: string } | null {
  if (before === after) return null
  let start = 0
  const max = Math.min(before.length, after.length)
  while (start < max && before.charCodeAt(start) === after.charCodeAt(start)) start++
  let end = 0
  while (end < max - start && before.charCodeAt(before.length - 1 - end) === after.charCodeAt(after.length - 1 - end)) end++
  return { start, end: before.length - end, text: after.slice(start, after.length - end) }
}

interface Context {
  m: MonacoModule
  queryClient: QueryClient
  /** The settings for a language. */
  settings: (language?: string) => Record<string, unknown>
}

let context: Context | null = null
const registered = new Set<string>()

/** The formatters for a file, kept 10 s; none when code-arena cannot say. */
async function formattersFor(path: string, language: string): Promise<Formatters | null> {
  if (!context) return null
  try {
    return await context.queryClient.fetchQuery(formattersQuery(path, language))
  } catch {
    return null
  }
}

/** The formatter a file is formatted with: the one asked for, "editor.defaultFormatter", else the first that can run. */
function chosen(list: Formatters, language: string, asked?: string): string | undefined {
  return asked ?? formatterOf(context?.settings(language)['editor.defaultFormatter']) ?? list.default ?? undefined
}

/**
 * The model's text through a formatter, applied as one edit Undo takes back; a version of the model that changed
 * meanwhile drops the result. Errors are thrown with what the formatter said; a formatter in an untrusted folder asks for
 * the folder's trust first.
 */
export async function formatModel(model: Monaco.editor.ITextModel, path: string, opts: { formatter?: string; range?: Monaco.IRange; signal?: AbortSignal } = {}): Promise<string | null> {
  if (!context) return null
  const language = model.getLanguageId()
  const list = await formattersFor(path, language)
  if (!list) return null
  const formatter = chosen(list, language, opts.formatter)
  if (!formatter) return null
  const found = list.formatters.find((f) => f.id === formatter)
  if (found && !found.available && found.reason === 'trust') {
    const trusted = await askTrust(`${found.name} lives in this folder (${found.path ?? 'its node_modules'}): it runs only in a folder you trust.`)
    if (!trusted) return null
  }
  const text = model.getValue()
  const version = model.getVersionId()
  const options = model.getOptions()
  const range = opts.range
    ? { start: model.getOffsetAt({ lineNumber: opts.range.startLineNumber, column: opts.range.startColumn }), end: model.getOffsetAt({ lineNumber: opts.range.endLineNumber, column: opts.range.endColumn }) }
    : undefined
  const done = await formatText({ path, text, formatter, range, language, options: { tabSize: options.tabSize, insertSpaces: options.insertSpaces } }, opts.signal)
  if (model.isDisposed() || model.getVersionId() !== version) return null
  const edit = minimalEdit(text, done.text)
  if (edit) {
    const a = model.getPositionAt(edit.start)
    const b = model.getPositionAt(edit.end)
    model.pushStackElement()
    model.pushEditOperations([], [{ range: { startLineNumber: a.lineNumber, startColumn: a.column, endLineNumber: b.lineNumber, endColumn: b.column }, text: edit.text }], () => null)
    model.pushStackElement()
  }
  return done.formatter
}

/** What a failed format says, for a toast: the formatter's own words, with where in the file. */
export function formatError(e: unknown): string {
  if (e instanceof ApiError && e.status === 'format_failed') {
    const where = (e.data as { line?: number; column?: number } | undefined)?.line
    return where ? `${e.message} (line ${where})` : e.message
  }
  return errorMessage(e)
}

/** Format on save: with the file's formatter, within "codeArena.formatOnSaveTimeout"; past it, or failing, the file is saved as it is. */
const formatOnSave: SaveParticipant = async (c) => {
  if (!c.settings['editor.formatOnSave'] || c.reason === 'auto') return
  const timeout = (c.settings['codeArena.formatOnSaveTimeout'] as number) ?? 1500
  const stop = new AbortController()
  const timer = setTimeout(() => stop.abort(), timeout)
  try {
    await formatModel(c.model, c.path, { signal: stop.signal })
  } catch (e) {
    if (stop.signal.aborted) throw new Error(`the formatter took longer than ${timeout} ms ("codeArena.formatOnSaveTimeout")`)
    throw new Error(formatError(e))
  } finally {
    clearTimeout(timer)
  }
}
formatOnSave.what = 'formatting'

/** Saves skip formatting while this is set (Save without Formatting). */
export const skipFormat = { once: false }

const formatUnlessSkipped: SaveParticipant = async (c) => {
  if (skipFormat.once) return
  await formatOnSave(c)
}
formatUnlessSkipped.what = 'formatting'

/**
 * The formatting providers for a language, once a file of it opens and code-arena has a formatter for it (later
 * providers come first in Monaco, so the project's formatter takes the place of Monaco's own for that language).
 */
export async function offerFormatting(path: string, language: string) {
  if (!context || registered.has(language)) return
  const list = await formattersFor(path, language)
  if (!list || list.formatters.length === 0 || registered.has(language)) return
  registered.add(language)
  const { monaco } = context.m
  const pathOf = (model: Monaco.editor.ITextModel) => context?.m.pathOf(model.uri) ?? null
  const run = async (model: Monaco.editor.ITextModel, range?: Monaco.IRange) => {
    const p = pathOf(model)
    if (!p) return []
    try {
      await formatModel(model, p, { range })
    } catch (e) {
      toast.error(formatError(e), e instanceof ApiError && e.status === 'not_trusted' ? { action: { label: 'Trust', onClick: () => void runCommand('workbench.trust.manage') } } : undefined)
    }
    // The edit is made already (as one step Undo takes back): Monaco has nothing more to do.
    return []
  }
  const name = list.formatters.find((f) => f.id === list.default)?.name ?? list.formatters[0]!.name
  monaco.languages.registerDocumentFormattingEditProvider(language, { displayName: name, provideDocumentFormattingEdits: (model) => run(model) })
  if (list.formatters.some((f) => f.range)) monaco.languages.registerDocumentRangeFormattingEditProvider(language, { displayName: name, provideDocumentRangeFormattingEdits: (model, range) => run(model, range) })
}

/** Formatting set up once Monaco is there: the save step, and the providers as files open. */
export function installFormatting(c: Context) {
  if (context) {
    context = c
    return
  }
  context = c
  addSaveParticipant(formatUnlessSkipped)
}

/** The formatters of a file, for the status bar and Format Document With…. */
export const formattersOf = formattersFor
