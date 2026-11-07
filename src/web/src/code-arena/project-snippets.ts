import type { QueryClient } from '@tanstack/react-query'
import type * as Monaco from 'monaco-editor'
import { toast } from '@/components/ui/toaster'
import type { MonacoModule } from './editor-state'
import { api, ApiError } from './http'
import { writeFile, writeUserFile, type UserFileName } from './ide-api'
import { inputBox, quickPick } from './quick-pick'
import { applies, readSnippets, snippetSuggestions, type Snippet } from './snippets'

// The snippets VS Code reads besides an extension's: the project's .vscode/*.code-snippets (the folder's, and each added
// folder's) and the person's own (User/snippets: NAME.json for one language, NAME.code-snippets for any). Read from
// code-arena, suggested as you type, inserted by Insert Snippet and Surround With Snippet; Configure Snippets makes and
// opens the files.

/** A snippets file as code-arena lists it. */
export interface SnippetsFile {
  source: 'project' | 'user'
  /** As the editor opens it: the folder's path, a whole path for an added folder's, user:snippets/NAME for the person's. */
  path: string
  /** A user NAME.json's language (its name); null for a .code-snippets file (each snippet says its scope, or is for all). */
  language: string | null
  text: string
  version: string
}

export interface SnippetsList {
  files: SnippetsFile[]
  problems: string[]
}

export const snippetsQuery = {
  queryKey: ['code', 'snippets'] as const,
  queryFn: ({ signal }: { signal: AbortSignal }) => api<SnippetsList>('/api/snippets', { signal }),
  staleTime: 60_000,
}

/** Whether a path is a snippets file code-arena reads: saving it, or a change on disk, reads them again. */
export const isSnippetsFile = (path: string) => /^user:snippets\/[^/]+\.(json|code-snippets)$/.test(path) || /(^|\/)\.vscode\/[^/]+\.code-snippets$/.test(path)

/** The snippets of the files listed, each saying where it is from; a user NAME.json's are for its language alone. */
export function snippetsOf(list: SnippetsList): Snippet[] {
  return list.files.flatMap((f) =>
    readSnippets(f.text).map((s) => ({
      ...s,
      ...(f.language && !s.scope ? { scope: [f.language] } : {}),
      from: f.source === 'project' ? 'Workspace Snippet' : 'User Snippet',
    })),
  )
}

let client: QueryClient | null = null
let installed: MonacoModule | null = null
let cache: { list: SnippetsList; snippets: Snippet[] } | null = null

/** The project's and the person's snippets now (read again once a snippets file changed). */
export async function currentSnippets(): Promise<Snippet[]> {
  if (!client) return []
  try {
    const list = await client.fetchQuery(snippetsQuery)
    if (cache?.list !== list) cache = { list, snippets: snippetsOf(list) }
    return cache.snippets
  } catch {
    return cache?.snippets ?? []
  }
}

/** A snippets file was saved or changed on disk: they are read again before the next suggestion. */
export function snippetsChanged() {
  void client?.invalidateQueries({ queryKey: snippetsQuery.queryKey })
}

/** Monaco, once loaded: suggestions from the project's and the person's snippets, in every language. */
export function installSnippets(m: MonacoModule, queryClient: QueryClient) {
  client = queryClient
  if (installed === m) return
  installed = m
  m.monaco.languages.registerCompletionItemProvider('*', {
    provideCompletionItems: async (model, position) => ({
      suggestions: snippetSuggestions(m.monaco.languages, await currentSnippets(), model, position, m.pathOf(model.uri)),
    }),
  })
}

/** The editor the person types in now (the one with the focus, else the one showing the file shown). */
function activeCodeEditor(m: MonacoModule, model: Monaco.editor.ITextModel | null): Monaco.editor.ICodeEditor | null {
  const all = m.monaco.editor.getEditors()
  return all.find((e) => e.hasTextFocus()) ?? all.find((e) => !!model && e.getModel() === model) ?? null
}

/** The snippets controller of an editor (Monaco's own: it fills $1, ${2:x} and the variables such as $TM_SELECTED_TEXT). */
type SnippetController = { insert(template: string, opts?: { undoStopBefore?: boolean; undoStopAfter?: boolean }): void }

/**
 * Insert Snippet (and Surround With Snippet: only those that take the selection, $TM_SELECTED_TEXT or $SELECTION): every
 * snippet for the file's language, the extensions' among them, chosen by its name or prefix and inserted at the cursor.
 */
export async function insertSnippet(m: MonacoModule, model: Monaco.editor.ITextModel | null, extensions: Snippet[], surround = false) {
  const editor = activeCodeEditor(m, model)
  const shown = editor?.getModel()
  if (!editor || !shown) {
    toast.info('Open a file to insert a snippet into.')
    return
  }
  const language = shown.getLanguageId()
  const path = m.pathOf(shown.uri)
  const all = [...(await currentSnippets()), ...extensions].filter((s) => applies(s, language, path))
  const list = surround ? all.filter((s) => /\$\{?(TM_SELECTED_TEXT|SELECTION)\b/.test(s.body)) : all
  if (list.length === 0) {
    toast.info(surround ? `No snippet for ${language} takes the selection ($TM_SELECTED_TEXT).` : `There is no snippet for ${language}: Configure Snippets makes one.`)
    return
  }
  const chosen = await quickPick<Snippet>({
    title: surround ? 'Surround With Snippet' : 'Insert Snippet',
    placeholder: 'Select a snippet',
    items: list.map((s, i) => ({ id: `${i}`, label: s.name, description: s.prefixes.join(', '), detail: s.description, group: s.from ?? 'Extension Snippets', value: s })),
  })
  if (!chosen?.value) return
  editor.focus()
  ;(editor.getContribution('snippetController2') as unknown as SnippetController | null)?.insert(chosen.value.body)
}

/** A new snippets file's text, as VS Code starts one: an example in a comment. */
const template = (scoped: boolean) => `{
\t// Each snippet has a name, its prefix (typed, it is suggested), its body (a line, or lines) and a description.
\t// $1, $2 are where Tab goes, \${1:label} with a placeholder, $0 the end; $TM_FILENAME and the others are filled in.${scoped ? '\n\t// "scope" names its languages (none: all), "include" and "exclude" its files (globs).' : ''}
\t// "Print to console": {
${scoped ? '\t// \t"scope": "javascript,typescript",\n' : ''}\t// \t"prefix": "log",
\t// \t"body": [
\t// \t\t"console.log('$1');",
\t// \t\t"$2"
\t// \t],
\t// \t"description": "Log output to console"
\t// }
}
`

/** A file name as VS Code takes one for a snippets file: letters, digits, dots, dashes and underscores. */
const validName = (v: string) => (/^[A-Za-z0-9][A-Za-z0-9._-]{0,63}$/.test(v.trim()) ? null : 'Letters, digits, dots, dashes and underscores (64 at most).')

/**
 * Snippets: Configure Snippets, as VS Code's: a new snippets file for every project (the person's) or for this folder
 * (.vscode/NAME.code-snippets, shared with the team when committed), the files there are, and a language's own file.
 * The file chosen opens in the editor (made first, with an example, when it is new).
 */
export async function configureSnippets(open: (path: string) => Promise<void>, languages: { id: string; name: string }[], queryClient: QueryClient) {
  let list: SnippetsList
  try {
    list = await queryClient.fetchQuery(snippetsQuery)
  } catch (e) {
    toast.error(e instanceof ApiError ? e.message : 'The snippets files could not be listed.')
    return
  }
  type Choice = { kind: 'global' | 'project' | 'file' | 'language'; path?: string; language?: string }
  const own = new Set(list.files.map((f) => f.path))
  const chosen = await quickPick<Choice>({
    title: 'Configure Snippets',
    placeholder: 'Select a snippets file, or make one',
    items: [
      { id: 'global', label: 'New Global Snippets file…', description: 'for every project', value: { kind: 'global' } },
      { id: 'project', label: 'New Snippets file for this folder…', description: '.vscode/NAME.code-snippets', value: { kind: 'project' } },
      ...list.files.map((f) => ({ id: `f:${f.path}`, label: f.path.replace(/^user:snippets\//, ''), description: f.source === 'project' ? f.path : 'User', group: 'existing snippets', value: { kind: 'file' as const, path: f.path } })),
      ...languages
        .filter((l) => !own.has(`user:snippets/${l.id}.json`))
        .map((l) => ({ id: `l:${l.id}`, label: l.id, description: l.name, group: 'new snippets for a language', value: { kind: 'language' as const, language: l.id } })),
    ],
  })
  const c = chosen?.value
  if (!c) return
  try {
    if (c.kind === 'file' && c.path) {
      await open(c.path)
      return
    }
    let path: string
    if (c.kind === 'language' && c.language) path = `user:snippets/${c.language}.json`
    else {
      const typed = await inputBox({ title: c.kind === 'global' ? 'New Global Snippets file' : 'New Snippets file for this folder', placeholder: 'Type the snippets file name', validate: validName })
      if (!typed) return
      const base = typed.trim().replace(/\.code-snippets$/, '')
      path = c.kind === 'global' ? `user:snippets/${base}.code-snippets` : `.vscode/${base}.code-snippets`
    }
    // Made with its example only when it is not there ("none": a file there already is refused, and opens as it is).
    const text = template(c.kind !== 'language')
    await (path.startsWith('user:') ? writeUserFile(path.slice(5) as UserFileName, text, 'none') : writeFile(path, text, 'none')).catch((e: unknown) => {
      if (!(e instanceof ApiError && e.http === 409)) throw e
    })
    snippetsChanged()
    await open(path)
  } catch (e) {
    toast.error(e instanceof ApiError ? e.message : 'The snippets file could not be made.')
  }
}
