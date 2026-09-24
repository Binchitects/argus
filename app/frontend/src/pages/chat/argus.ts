/**
 * Argus's answers, read for what they are. Its tools return rows of code
 * locations (find_symbol, find_references, search_code, semantic_search) or a
 * file (get_file); the app stores them as JSON (src/argus/store/queries.py).
 */

/** One place in the code: where it is, and what to show of it. */
export interface CodeHit {
  repo: string | null
  path: string
  line: number | null
  endLine: number | null
  name: string | null
  kind: string | null
  /** One line to highlight: a signature, or the line a reference is on. */
  code: string | null
  /** search_code's excerpt, with the matched words in [brackets]. */
  snippet: string | null
  doc: string | null
  /** get_file's whole text. */
  content: string | null
  truncated: boolean
  score: number | null
}

export type Row = Record<string, unknown>

const str = (v: unknown) => (typeof v === 'string' && v.length > 0 ? v : null)
const num = (v: unknown) => (typeof v === 'number' && Number.isFinite(v) ? v : null)
export const isRow = (v: unknown): v is Row => typeof v === 'object' && v !== null && !Array.isArray(v)

/** The tool's answer as JSON, or undefined when it is text (an error, a notice). */
export function parseResult(text: string): unknown {
  const t = text.trim()
  if (!t.startsWith('{') && !t.startsWith('[')) return undefined
  try {
    return JSON.parse(t) as unknown
  } catch {
    return undefined
  }
}

function hitOf(r: Row): CodeHit | null {
  const path = str(r.path)
  if (!path) return null
  return {
    repo: str(r.path_with_namespace) ?? str(r.repo),
    path,
    line: num(r.line),
    endLine: num(r.end_line),
    name: str(r.name),
    kind: str(r.kind) ?? (r.is_definition === true ? 'definition' : null),
    code: str(r.signature) ?? str(r.context),
    snippet: str(r.snippet),
    doc: str(r.doc),
    content: typeof r.content === 'string' ? r.content : null,
    truncated: r.truncated === true,
    score: num(r.score),
  }
}

/** Code locations, when the answer is made of them; null when it is something else. */
export function toHits(value: unknown): CodeHit[] | null {
  if (isRow(value)) {
    const hit = hitOf(value)
    return hit && hit.content !== null ? [hit] : null
  }
  if (!Array.isArray(value) || value.length === 0 || !value.every(isRow)) return null
  const hits = value.map(hitOf)
  return hits.every((h) => h !== null) ? hits : null
}

/**
 * A link to the file and lines in GitLab. `ref` is the branch the tool was asked
 * about; without one, HEAD, which GitLab resolves to the default branch.
 */
export function gitlabLink(base: string, repo: string, path: string, ref: string | null, line: number | null, endLine: number | null): string {
  const segments = (s: string) => s.split('/').filter(Boolean).map(encodeURIComponent).join('/')
  const lines = line ? `#L${line}${endLine && endLine > line ? `-${endLine}` : ''}` : ''
  return `${base.replace(/\/+$/, '')}/${segments(repo)}/-/blob/${encodeURIComponent(ref || 'HEAD')}/${segments(path)}${lines}`
}

/**
 * search_code's excerpt in parts, the matches marked. SQLite marks matches with
 * brackets, which code uses too (`a[i]`), so only brackets around a word of the
 * query count as a match.
 */
export function snippetParts(snippet: string, query: string | null): { text: string; match: boolean }[] {
  const words = new Set((query ?? '').toLowerCase().match(/[\p{L}\p{N}_]+/gu) ?? [])
  const parts: { text: string; match: boolean }[] = []
  let last = 0
  for (const m of snippet.matchAll(/\[([^[\]\n]+)\]/g)) {
    const word = m[1]!.toLowerCase()
    if (!words.has(word) && ![...words].some((w) => word.startsWith(w))) continue
    if (m.index > last) parts.push({ text: snippet.slice(last, m.index), match: false })
    parts.push({ text: m[1]!, match: true })
    last = m.index + m[0].length
  }
  if (last < snippet.length) parts.push({ text: snippet.slice(last), match: false })
  return parts
}

/** A tool call's arguments as an object ({} when they are not JSON). */
export function argsOf(raw: string | undefined): Row {
  try {
    const v = JSON.parse(raw || '{}') as unknown
    return isRow(v) ? v : {}
  } catch {
    return {}
  }
}

/** "3 results", for the card's header, when the answer is a list. */
export function resultCount(value: unknown): string | null {
  if (Array.isArray(value)) return value.length === 1 ? '1 result' : `${value.length} results`
  return null
}
