import type { QueryClient } from '@tanstack/react-query'
import type * as Monaco from 'monaco-editor'
import { toast } from '@/components/ui/toaster'
import { ApiError, errorMessage } from '@/lib/api'
import { runCommand } from './commands'
import type { MonacoModule } from './editor-state'
import { formatText, formattersQuery, workspaceStateQuery, type Formatters, type WorkspaceState } from './ide-api'
import { addSaveParticipant, type SaveParticipant } from './save-participants'
import { askTrust } from './trust'

/**
 * Formatting as VS Code does it, with the project's own tools run by code-arena (Prettier from its node_modules, ruff,
 * gofmt, rustfmt, clang-format, dotnet format, the person's own from config.json): Format Document (Shift+Alt+F),
 * Format Selection, Format Document With…, format on save and on paste. A file with no formatter of the project's that
 * can run (or none asked for) keeps Monaco's own (TypeScript, JSON, CSS, HTML).
 */

/** The editor's own formatter (Monaco's), as "editor.defaultFormatter" names it with VS Code's built-in ones' ids. */
export const editorsOwn = 'editor:monaco'

/** VS Code's extension ids for these formatters, as "editor.defaultFormatter" names them; its built-in ones are the editor's own. */
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
  'vscode.typescript-language-features': editorsOwn,
  'vscode.json-language-features': editorsOwn,
  'vscode.css-language-features': editorsOwn,
  'vscode.html-language-features': editorsOwn,
}

/**
 * "editor.defaultFormatter" as one of code-arena's formatters (VS Code's extension ids too), or editorsOwn for VS Code's
 * built-in ones; undefined: the server's first.
 */
export const formatterOf = (setting: unknown) => (typeof setting === 'string' && setting ? (extensionIds[setting.toLowerCase()] ?? setting) : undefined)

type EditorProvider = Monaco.languages.DocumentFormattingEditProvider | Monaco.languages.DocumentRangeFormattingEditProvider
type Register = (selector: Monaco.languages.LanguageSelector, provider: EditorProvider) => Monaco.IDisposable

/** Monaco's own formatting providers (its TypeScript, JSON, CSS and HTML services'), the latest first, as they register. */
const editorProviders: { selector: Monaco.languages.LanguageSelector; provider: EditorProvider }[] = []
/** Code Arena's providers, which are not the editor's own. */
const ours = new WeakSet<object>()

/**
 * Keeps the formatting providers Monaco's language services register (as a language's first file opens), so that the
 * editor's own formatter can still run where Code Arena's provider takes its place: Monaco itself gives a language's
 * newest provider only. Called once, before any model is made.
 */
export function keepEditorFormatters(languages: typeof Monaco.languages) {
  const api = languages as unknown as Record<string, Register | undefined>
  for (const name of ['registerDocumentFormattingEditProvider', 'registerDocumentRangeFormattingEditProvider']) {
    const register = api[name]
    if (!register) continue
    api[name] = (selector, provider) => {
      const registered = register(selector, provider)
      if (ours.has(provider)) return registered
      const kept = { selector, provider }
      editorProviders.unshift(kept)
      return {
        dispose: () => {
          const at = editorProviders.indexOf(kept)
          if (at >= 0) editorProviders.splice(at, 1)
          registered.dispose()
        },
      }
    }
  }
}

/** Whether a provider's language selector takes a language (an id, "*", a filter with a language, or a list of them). */
function selects(selector: Monaco.languages.LanguageSelector, language: string): boolean {
  if (typeof selector === 'string') return selector === language || selector === '*'
  if ('length' in selector) return selector.some((s) => selects(s, language))
  return selector.language === undefined || selector.language === language || selector.language === '*'
}

/**
 * The edits the editor's own formatter makes to a model, or to a range of it, picked as Monaco picks one: for a whole
 * file, one that formats whole files first, else one that formats ranges, over the whole file. Null: it has none for the
 * model's language.
 */
async function editorEdits(model: Monaco.editor.ITextModel, range: Monaco.Range | undefined, options: Monaco.languages.FormattingOptions, token: Monaco.CancellationToken): Promise<Monaco.languages.TextEdit[] | null> {
  const language = model.getLanguageId()
  const mine = editorProviders.filter((p) => selects(p.selector, language)).map((p) => p.provider)
  const whole = range ? undefined : mine.find((p) => 'provideDocumentFormattingEdits' in p)
  if (whole) return (await whole.provideDocumentFormattingEdits(model, options, token)) ?? []
  const part = mine.find((p) => 'provideDocumentRangeFormattingEdits' in p)
  if (part) return (await part.provideDocumentRangeFormattingEdits(model, range ?? model.getFullModelRange(), options, token)) ?? []
  return null
}

/** A cancellation token, as Monaco's providers take, that an abort signal cancels. */
function tokenOf(signal?: AbortSignal): Monaco.CancellationToken {
  return {
    get isCancellationRequested() {
      return signal?.aborted ?? false
    },
    onCancellationRequested: (listener: () => unknown) => {
      const cancelled = () => listener()
      signal?.addEventListener('abort', cancelled)
      return { dispose: () => signal?.removeEventListener('abort', cancelled) }
    },
  }
}

/** A promise that fails as the signal aborts: a provider that goes on once cancelled is not waited for. */
function untilAborted<T>(p: Promise<T>, signal?: AbortSignal): Promise<T> {
  if (!signal) return p
  return new Promise<T>((resolve, reject) => {
    const stop = () => reject(signal.reason)
    if (signal.aborted) {
      stop()
      return
    }
    signal.addEventListener('abort', stop, { once: true })
    p.then(resolve, reject).finally(() => signal.removeEventListener('abort', stop))
  })
}

/**
 * The model, or a range of it, through the editor's own formatter, as one edit Undo takes back (a version of the model
 * that changed meanwhile drops it); false: the editor has no formatter for its language. An aborted signal throws.
 */
async function formatWithEditor(m: MonacoModule, model: Monaco.editor.ITextModel, range?: Monaco.IRange, signal?: AbortSignal): Promise<boolean> {
  const version = model.getVersionId()
  const { tabSize, insertSpaces } = model.getOptions()
  const part = range ? new m.monaco.Range(range.startLineNumber, range.startColumn, range.endLineNumber, range.endColumn) : undefined
  const edits = await untilAborted(editorEdits(model, part, { tabSize, insertSpaces }, tokenOf(signal)), signal)
  if (!edits) return false
  if (edits.length && !model.isDisposed() && model.getVersionId() === version) {
    model.pushStackElement()
    model.pushEditOperations([], edits.map((e) => ({ range: e.range, text: e.text })), () => null)
    model.pushStackElement()
  }
  return true
}

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

/**
 * The one edit of whole lines that turns a text into another: the lines both start and end with are left alone, so what
 * is marked on them (a breakpoint, the cursor) moves with its line, as an edit across them would not take it.
 */
export function lineEdit(before: string, after: string): { start: number; end: number; text: string } | null {
  if (before === after) return null
  // Each line with its end (\n, \r\n): the last one may have none.
  const a = before.split(/(?<=\n)/)
  const b = after.split(/(?<=\n)/)
  let head = 0
  while (head < a.length && head < b.length && a[head] === b[head]) head++
  let tail = 0
  while (tail < a.length - head && tail < b.length - head && a[a.length - 1 - tail] === b[b.length - 1 - tail]) tail++
  const start = a.slice(0, head).join('').length
  const end = before.length - a.slice(a.length - tail).join('').length
  return { start, end, text: b.slice(head, b.length - tail).join('') }
}

interface Context {
  m: MonacoModule
  queryClient: QueryClient
  /** The settings for a language. */
  settings: (language?: string) => Record<string, unknown>
}

let context: Context | null = null
/** The formatters a save has said it skipped (once each). */
const noted = new Set<string>()
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

/**
 * The formatter a file is formatted with: the one asked for, "editor.defaultFormatter", else the first that can run, else
 * the first that waits on the folder's trust (as code-arena picks); undefined: none (all of them missing).
 */
function chosen(list: Formatters, language: string, asked?: string): string | undefined {
  return asked ?? formatterOf(context?.settings(language)['editor.defaultFormatter']) ?? list.default ?? list.formatters.find((f) => f.reason === 'trust')?.id
}

/**
 * The model's text through a formatter, applied as one edit Undo takes back; a version of the model that changed
 * meanwhile drops the result. Errors are thrown with what the formatter said; a formatter in an untrusted folder asks for
 * the folder's trust first. With no formatter of the project's that can run (or the editor's own asked for), the editor's
 * own formats it, if it has one for the language (editorsOwn returned; null: nothing formatted it).
 */
export async function formatModel(
  model: Monaco.editor.ITextModel,
  path: string,
  opts: { formatter?: string; range?: Monaco.IRange; signal?: AbortSignal; /** False: never ask for the folder's trust (a save does not wait on a dialog). */ ask?: boolean } = {},
): Promise<string | null> {
  if (!context) return null
  const language = model.getLanguageId()
  const list = await formattersFor(path, language)
  const formatter = list ? chosen(list, language, opts.formatter) : undefined
  if (!list || !formatter || formatter === editorsOwn) return (await formatWithEditor(context.m, model, opts.range, opts.signal)) ? editorsOwn : null
  const found = list.formatters.find((f) => f.id === formatter)
  if (found && !found.available && found.reason === 'trust') {
    // A save never asks (it would wait on the dialog, at every save): it says so once. Nor is the person asked again
    // once they kept the folder restricted: Manage Workspace Trust changes that.
    if (opts.ask === false) {
      if (!noted.has(found.id)) {
        noted.add(found.id)
        toast.info(`Saved without formatting: ${found.name} lives in this folder, which is not trusted. Format Document asks to trust it.`)
      }
      return null
    }
    if (context.queryClient.getQueryData<WorkspaceState>(workspaceStateQuery.queryKey)?.trust === 'restricted') {
      toast.error(`${found.name} lives in this folder, which you keep restricted.`, { action: { label: 'Manage Workspace Trust', onClick: () => void runCommand('workbench.trust.manage') } })
      return null
    }
    const trusted = await askTrust(`${found.name} lives in this folder (${found.path ?? 'its node_modules'}): it runs only in a folder you trust.`)
    if (!trusted) return null
  }
  const text = model.getValue()
  const version = model.getVersionId()
  const options = model.getOptions()
  const range = opts.range
    ? { start: model.getOffsetAt({ lineNumber: opts.range.startLineNumber, column: opts.range.startColumn }), end: model.getOffsetAt({ lineNumber: opts.range.endLineNumber, column: opts.range.endColumn }) }
    : undefined
  // VS Code's prettier.* settings, as prettier-vscode passes them (a project's own config file wins over them).
  const all = context.settings(language)
  const prettier = Object.fromEntries(Object.entries(all).filter(([k]) => k.startsWith('prettier.')).map(([k, v]) => [k.slice(9), v]))
  const done = await formatText({ path, text, formatter, range, language, options: { tabSize: options.tabSize, insertSpaces: options.insertSpaces, ...(Object.keys(prettier).length ? { prettier } : {}) } }, opts.signal)
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
    await formatModel(c.model, c.path, { signal: stop.signal, ask: false })
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
 * The formatting providers for a language, once a file of it opens and code-arena has a formatter for it that can run,
 * or will once the folder is trusted (later providers come first in Monaco, so the project's formatter takes the place
 * of Monaco's own for that language: where none of the project's runs, or the editor's own is asked for, Monaco's runs
 * through it). A formatter that is only missing leaves the language to Monaco's own.
 */
export async function offerFormatting(path: string, language: string) {
  if (!context || registered.has(language)) return
  const list = await formattersFor(path, language)
  const usable = list?.formatters.filter((f) => f.available || f.reason === 'trust') ?? []
  if (usable.length === 0 || registered.has(language)) return
  registered.add(language)
  const { monaco } = context.m
  const pathOf = (model: Monaco.editor.ITextModel) => context?.m.pathOf(model.uri) ?? null
  const run = async (model: Monaco.editor.ITextModel, range: Monaco.Range | undefined, options: Monaco.languages.FormattingOptions, token: Monaco.CancellationToken) => {
    const p = pathOf(model)
    // A model that is no file of the folder (the person's own settings.json) is the editor's own to format.
    if (!p) return (await editorEdits(model, range, options, token)) ?? []
    try {
      await formatModel(model, p, { range })
    } catch (e) {
      toast.error(formatError(e), e instanceof ApiError && e.status === 'not_trusted' ? { action: { label: 'Trust', onClick: () => void runCommand('workbench.trust.manage') } } : undefined)
    }
    // The edit is made already (as one step Undo takes back): Monaco has nothing more to do.
    return []
  }
  const name = usable.find((f) => f.id === list?.default)?.name ?? usable[0]!.name
  const whole: Monaco.languages.DocumentFormattingEditProvider = { displayName: name, provideDocumentFormattingEdits: (model, options, token) => run(model, undefined, options, token) }
  ours.add(whole)
  monaco.languages.registerDocumentFormattingEditProvider(language, whole)
  if (usable.some((f) => f.range)) {
    const part: Monaco.languages.DocumentRangeFormattingEditProvider = { displayName: name, provideDocumentRangeFormattingEdits: (model, range, options, token) => run(model, range, options, token) }
    ours.add(part)
    monaco.languages.registerDocumentRangeFormattingEditProvider(language, part)
  }
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
