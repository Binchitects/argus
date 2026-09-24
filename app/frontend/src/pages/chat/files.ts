import { Marked, type Token, type Tokens } from 'marked'
import { argsOf, parseResult, toHits } from './argus'
import { extensionFor, languageOf } from './highlight'
import type { Attachment, Message } from './types'

/**
 * A fence's language and file name. Understood: ```ts title="src/a.ts"```,
 * ```ts:src/a.ts```, ```src/a.ts```, and a first line that names the file
 * (// file: src/a.ts, # path/to/x.py).
 */
export function parseFence(info: string | undefined, code: string): { lang: string | null; name: string | null } {
  const raw = (info ?? '').trim()
  let lang: string | null = null
  let name: string | null = null
  const title = /(?:title|file(?:name)?)=["']?([^"'\s]+)["']?/.exec(raw)
  const first = raw.split(/\s+/)[0] ?? ''
  if (title) name = title[1]!
  if (first.includes(':') && !first.includes('://')) {
    const [l, n] = first.split(':', 2)
    lang = l || null
    name ??= n || null
  } else if (/[./]/.test(first) && /\.\w+$/.test(first)) {
    name ??= first
    lang = first.split('.').pop() ?? null
  } else {
    lang = first || null
  }
  if (!name) {
    const line = /^\s*(?:\/\/|#|--|<!--)\s*(?:file(?:name)?:\s*)?([\w./-]+\.\w+)\s*(?:-->)?\s*$/.exec(code.split('\n')[0] ?? '')
    if (line) name = line[1]!
  }
  if (!lang && name) lang = name.split('.').pop() ?? null
  return { lang: lang && (languageOf(lang) ?? lang), name }
}

export type FileItem =
  | { kind: 'attachment'; key: string; name: string; attachment: Attachment }
  | { kind: 'code'; key: string; name: string; lang: string | null; code: string; messageId: string }
  | { kind: 'repo'; key: string; name: string; repo: string | null; path: string; branch: string | null; lang: string | null; code: string; truncated: boolean }

const lexer = new Marked({ gfm: true })

function codeTokens(tokens: Token[]): Tokens.Code[] {
  const out: Tokens.Code[] = []
  for (const t of tokens) {
    if (t.type === 'code') out.push(t as Tokens.Code)
    const nested = (t as { tokens?: Token[]; items?: { tokens: Token[] }[] })
    if (nested.tokens) out.push(...codeTokens(nested.tokens))
    if (nested.items) for (const i of nested.items) out.push(...codeTokens(i.tokens))
  }
  return out
}

/** Everything in the branch on screen that is a file: attachments, files Argus read, and code the model wrote. */
export function collectFiles(path: Message[]): FileItem[] {
  const items: FileItem[] = []
  const seen = new Set<string>()
  const calls = new Map(path.flatMap((m) => m.toolCalls ?? []).map((c) => [c.id, argsOf(c.function.arguments)]))
  let snippet = 0
  for (const m of path) {
    for (const a of m.attachments) {
      if (seen.has(a.id)) continue
      seen.add(a.id)
      items.push({ kind: 'attachment', key: `a-${a.id}`, name: a.fileName, attachment: a })
    }
    if (m.role === 'tool' && m.status !== 'failed') {
      const file = toHits(parseResult(m.content))?.find((h) => h.content !== null)
      if (file) {
        const branch = calls.get(m.toolCallId ?? '')?.branch
        items.push({
          kind: 'repo', key: `r-${file.repo}:${file.path}`, name: file.path.split('/').pop()!, repo: file.repo, path: file.path,
          branch: typeof branch === 'string' ? branch : null, lang: languageOf(file.path.split('.').pop()), code: file.content!, truncated: file.truncated,
        })
      }
      continue
    }
    if (m.role !== 'assistant' || !m.content.includes('```')) continue
    codeTokens(lexer.lexer(m.content)).forEach((t, i) => {
      if (!t.text.trim()) return
      const { lang, name } = parseFence(t.lang, t.text)
      items.push({ kind: 'code', key: `c-${m.id}-${i}`, name: name ?? `snippet-${++snippet}.${extensionFor(lang)}`, lang, code: t.text, messageId: m.id })
    })
  }
  // A file the model rewrote shows once: its latest version.
  const latest = new Map<string, FileItem>()
  for (const f of items) latest.set(f.kind === 'code' && !f.name.startsWith('snippet-') ? `code:${f.name}` : f.key, f)
  return [...latest.values()]
}
