import type * as Monaco from 'monaco-editor'

// A VS Code language-configuration.json as Monaco's LanguageConfiguration: the same shape, but its patterns are texts
// (or {pattern, flags}) where Monaco takes regular expressions, its pairs may be arrays, and an Enter rule's indent is a
// word. What cannot be read (a pattern JavaScript does not take) is left out, not the whole file.

type Json = Record<string, unknown>

const isObject = (v: unknown): v is Json => !!v && typeof v === 'object' && !Array.isArray(v)

/** A pattern as VS Code writes it: a text, or {pattern, flags}; null when it is not one JavaScript takes. */
export function regexOf(v: unknown): RegExp | null {
  const source = typeof v === 'string' ? v : isObject(v) && typeof v.pattern === 'string' ? v.pattern : null
  if (source === null) return null
  const flags = isObject(v) && typeof v.flags === 'string' ? v.flags.replace(/[^dgimsuy]/g, '') : ''
  try {
    return new RegExp(source, [...new Set(flags)].join(''))
  } catch {
    return null
  }
}

const pair = (v: unknown): [string, string] | null => (Array.isArray(v) && v.length === 2 && typeof v[0] === 'string' && typeof v[1] === 'string' ? [v[0], v[1]] : null)

/** A pair as an array (["(", ")"]) or an object ({open, close, notIn}). */
function pairOf(v: unknown): Monaco.languages.IAutoClosingPairConditional | null {
  const p = pair(v)
  if (p) return { open: p[0], close: p[1] }
  if (isObject(v) && typeof v.open === 'string' && typeof v.close === 'string') {
    const notIn = Array.isArray(v.notIn) ? v.notIn.filter((x): x is string => typeof x === 'string') : typeof v.notIn === 'string' ? [v.notIn] : undefined
    return { open: v.open, close: v.close, ...(notIn?.length ? { notIn } : {}) }
  }
  return null
}

/** VS Code's Enter actions by name, as Monaco's IndentAction (None, Indent, IndentOutdent, Outdent). */
const indentActions: Record<string, number> = { none: 0, indent: 1, indentOutdent: 2, outdent: 3 }

/** A language-configuration.json read (already parsed) as Monaco's configuration. */
export function toLanguageConfiguration(json: unknown): Monaco.languages.LanguageConfiguration {
  const c: Monaco.languages.LanguageConfiguration = {}
  if (!isObject(json)) return c
  if (isObject(json.comments)) {
    const comments: Monaco.languages.CommentRule = {}
    const line = json.comments.lineComment
    if (typeof line === 'string') comments.lineComment = line
    else if (isObject(line) && typeof line.comment === 'string') comments.lineComment = line.comment
    const block = pair(json.comments.blockComment)
    if (block) comments.blockComment = block
    c.comments = comments
  }
  if (Array.isArray(json.brackets)) c.brackets = json.brackets.map(pair).filter((p) => p !== null)
  if (Array.isArray(json.colorizedBracketPairs)) c.colorizedBracketPairs = json.colorizedBracketPairs.map(pair).filter((p) => p !== null)
  if (Array.isArray(json.autoClosingPairs)) c.autoClosingPairs = json.autoClosingPairs.map(pairOf).filter((p) => p !== null)
  if (Array.isArray(json.surroundingPairs)) c.surroundingPairs = json.surroundingPairs.map(pairOf).filter((p) => p !== null).map(({ open, close }) => ({ open, close }))
  if (typeof json.autoCloseBefore === 'string') c.autoCloseBefore = json.autoCloseBefore
  const word = regexOf(json.wordPattern)
  if (word) c.wordPattern = word
  if (isObject(json.folding)) {
    const folding: Monaco.languages.FoldingRules = {}
    if (typeof json.folding.offSide === 'boolean') folding.offSide = json.folding.offSide
    if (isObject(json.folding.markers)) {
      const start = regexOf(json.folding.markers.start)
      const end = regexOf(json.folding.markers.end)
      if (start && end) folding.markers = { start, end }
    }
    c.folding = folding
  }
  if (isObject(json.indentationRules)) {
    const r = json.indentationRules
    const increase = regexOf(r.increaseIndentPattern)
    const decrease = regexOf(r.decreaseIndentPattern)
    // Monaco needs both; a rule with one of them means the other never matches.
    if (increase || decrease) {
      c.indentationRules = {
        increaseIndentPattern: increase ?? /^\b$/,
        decreaseIndentPattern: decrease ?? /^\b$/,
        ...(regexOf(r.indentNextLinePattern) ? { indentNextLinePattern: regexOf(r.indentNextLinePattern) } : {}),
        ...(regexOf(r.unIndentedLinePattern) ? { unIndentedLinePattern: regexOf(r.unIndentedLinePattern) } : {}),
      }
    }
  }
  if (Array.isArray(json.onEnterRules)) {
    const rules: Monaco.languages.OnEnterRule[] = []
    for (const r of json.onEnterRules) {
      if (!isObject(r) || !isObject(r.action)) continue
      const beforeText = regexOf(r.beforeText)
      const indent = indentActions[String(r.action.indent ?? r.action.indentAction)]
      if (!beforeText || indent === undefined) continue
      const afterText = regexOf(r.afterText)
      const previousLineText = regexOf(r.previousLineText)
      rules.push({
        beforeText,
        ...(afterText ? { afterText } : {}),
        ...(previousLineText ? { previousLineText } : {}),
        action: {
          indentAction: indent as Monaco.languages.IndentAction,
          ...(typeof r.action.appendText === 'string' ? { appendText: r.action.appendText } : {}),
          ...(typeof r.action.removeText === 'number' ? { removeText: r.action.removeText } : {}),
        },
      })
    }
    c.onEnterRules = rules
  }
  return c
}
