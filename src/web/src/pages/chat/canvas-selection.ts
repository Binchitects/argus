/** A part of a canvas the person selected: its text, where it is, and its lines (from 1). */
export interface CanvasSelection {
  text: string
  start: number
  end: number
  from: number
  to: number
}

/** What a quote in a chat message carries at most; the model reads the rest from the canvas. */
const QuoteChars = 4000

const lineAt = (text: string, index: number) => text.slice(0, index).split('\n').length

/** The selection from `start` to `end` in the text, with its lines; null when nothing is selected. */
export function selectionOf(text: string, start: number, end: number): CanvasSelection | null {
  const [s, e] = start <= end ? [start, end] : [end, start]
  const selected = text.slice(s, e)
  if (!selected.trim()) return null
  // Ending just after a line break, it ends on the line before.
  const last = e > s && text[e - 1] === '\n' ? e - 1 : e
  return { text: selected, start: s, end: e, from: lineAt(text, s), to: lineAt(text, last) }
}

/**
 * Text selected in the preview, found in the source: exactly, or (as the preview shows
 * Markdown without its marks) the first and last lines of it. Null when it is not found.
 */
export function locate(text: string, selected: string): CanvasSelection | null {
  const wanted = selected.trim()
  if (!wanted) return null
  const exact = text.indexOf(wanted)
  if (exact >= 0) return selectionOf(text, exact, exact + wanted.length)
  const lines = wanted.split('\n').map((l) => l.trim()).filter(Boolean)
  const first = text.indexOf(lines[0]!)
  if (first < 0) return null
  const lastLine = lines.at(-1)!
  const last = text.indexOf(lastLine, first)
  if (last < 0) return null
  return selectionOf(text, first, last + lastLine.length)
}

const fence = (code: string, lang: string) => {
  const longest = Math.max(2, ...[...code.matchAll(/`+/g)].map((m) => m[0].length))
  const ticks = '`'.repeat(longest + 1)
  return `${ticks}${lang}\n${code}\n${ticks}`
}

/**
 * The chat message for a selection: it quotes the selected part with the canvas's id and
 * lines, then asks. The model answers by changing that part with canvas_edit.
 */
export function selectionMessage(canvas: { id: string; title: string; kind: string; language: string | null }, sel: CanvasSelection, ask: string): string {
  const lines = sel.from === sel.to ? `line ${sel.from}` : `lines ${sel.from}–${sel.to}`
  const cut = sel.text.length > QuoteChars
  const text = (cut ? sel.text.slice(0, QuoteChars) : sel.text).replace(/\n+$/, '')
  const quote = canvas.kind === 'code' ? fence(text, canvas.language ?? '') : text.split('\n').map((l) => (l ? `> ${l}` : '>')).join('\n')
  return [
    `About ${lines} of the canvas “${canvas.title}” (id ${canvas.id}):`,
    '',
    quote + (cut ? `\n\n(The quote is cut; all of it is ${lines} of the canvas.)` : ''),
    '',
    `${ask.trim()} If that needs a change, change only this part, with canvas_edit.`,
  ].join('\n')
}
