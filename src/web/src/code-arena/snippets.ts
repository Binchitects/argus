import type * as Monaco from 'monaco-editor'
import { parse } from './jsonc'

// VS Code's snippets (an extension's snippets/*.json, a .code-snippets file): JSON with comments, each snippet by its
// name with its prefixes, its body (a line or lines) and what it does. Monaco takes the same snippet syntax ($1, ${2:x},
// ${3|a,b|}, $0, $TM_FILENAME…), so a body goes to the editor as it is.

export interface Snippet {
  name: string
  prefixes: string[]
  body: string
  description?: string
  /** The languages it is for ("scope" in a .code-snippets file); none: every language its file is for. */
  scope?: string[]
}

/** A snippets file's text read; what is not a snippet (no prefix, no body) is left out. */
export function readSnippets(text: string): Snippet[] {
  const { value } = parse(text)
  if (!value || typeof value !== 'object' || Array.isArray(value)) return []
  const out: Snippet[] = []
  for (const [name, raw] of Object.entries(value as Record<string, unknown>)) {
    if (!raw || typeof raw !== 'object' || Array.isArray(raw)) continue
    const s = raw as { prefix?: unknown; body?: unknown; description?: unknown; scope?: unknown }
    const prefixes = (Array.isArray(s.prefix) ? s.prefix : [s.prefix]).filter((p): p is string => typeof p === 'string' && p.length > 0)
    const body = Array.isArray(s.body) ? s.body.filter((l): l is string => typeof l === 'string').join('\n') : typeof s.body === 'string' ? s.body : null
    if (prefixes.length === 0 || body === null) continue
    const description = Array.isArray(s.description) ? s.description.join('\n') : typeof s.description === 'string' ? s.description : undefined
    const scope = typeof s.scope === 'string' ? s.scope.split(',').map((x) => x.trim()).filter(Boolean) : undefined
    out.push({ name, prefixes, body, ...(description ? { description } : {}), ...(scope?.length ? { scope } : {}) })
  }
  return out
}

type Kinds = Pick<typeof Monaco.languages, 'CompletionItemKind' | 'CompletionItemInsertTextRule'>

/** The suggestions snippets make at a position: one per prefix, replacing the word typed so far. */
export function snippetSuggestions(m: Kinds, snippets: Snippet[], model: Monaco.editor.ITextModel, position: Monaco.Position): Monaco.languages.CompletionItem[] {
  const word = model.getWordUntilPosition(position)
  const range = { startLineNumber: position.lineNumber, startColumn: word.startColumn, endLineNumber: position.lineNumber, endColumn: position.column }
  const language = model.getLanguageId()
  return snippets
    .filter((s) => !s.scope || s.scope.includes(language))
    .flatMap((s) =>
      s.prefixes.map(
        (prefix): Monaco.languages.CompletionItem => ({
          label: { label: prefix, description: s.name },
          kind: m.CompletionItemKind.Snippet,
          detail: s.description ?? s.name,
          documentation: { value: '```\n' + s.body.replace(/```/g, '` ` `') + '\n```' },
          insertText: s.body,
          insertTextRules: m.CompletionItemInsertTextRule.InsertAsSnippet,
          filterText: prefix,
          sortText: prefix,
          range,
        }),
      ),
    )
}
