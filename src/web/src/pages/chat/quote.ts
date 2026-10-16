import { locate } from './canvas-selection'
import { markdownElement } from './markdown-blocks'
import type { Message } from './types'

/** The longest quote a message carries (the server refuses more). */
export const QuoteChars = 4000

/** Part of an answer quoted in a reply: what the model reads (`text`), the words as they were shown (`words`), and whether it was cut. */
export interface Quote {
  text: string
  words: string
  cut: boolean
}

/** Elements whose words end with a line break. */
const blocks = new Set(['P', 'DIV', 'LI', 'PRE', 'BLOCKQUOTE', 'H1', 'H2', 'H3', 'H4', 'H5', 'H6', 'TR', 'TABLE', 'UL', 'OL', 'HR', 'SECTION'])

/**
 * The words of a part of a page as they read: hidden parts, buttons and the copy of each formula meant for screen readers
 * left out, a formula written back as its TeX ($…$, or $$…$$ shown on a line of its own), a block's words ending its line.
 */
export function plainOf(root: Node): string {
  let out = ''
  const walk = (node: Node) => {
    if (node.nodeType === Node.TEXT_NODE) {
      out += node.nodeValue ?? ''
      return
    }
    if (node.nodeType !== Node.ELEMENT_NODE && node.nodeType !== Node.DOCUMENT_FRAGMENT_NODE) return
    const el = node.nodeType === Node.ELEMENT_NODE ? (node as Element) : null
    if (el) {
      if (el.hasAttribute('hidden') || el.getAttribute('aria-hidden') === 'true' || el.tagName === 'BUTTON' || el.tagName === 'SCRIPT' || el.tagName === 'STYLE') return
      if (el.classList.contains('katex')) {
        // KaTeX keeps the TeX in its MathML's annotation; sanitized, the annotation's text is left in the math element itself.
        const math = el.querySelector('math')
        const tex = (
          el.querySelector('annotation[encoding="application/x-tex"]')?.textContent ??
          [...(math?.childNodes ?? [])].filter((n) => n.nodeType === Node.TEXT_NODE).map((n) => n.nodeValue).join('')
        ).trim()
        if (tex) out += el.closest('.katex-display') ? `\n$$${tex}$$\n` : `$${tex}$`
        return
      }
      if (el.tagName === 'BR') {
        out += '\n'
        return
      }
      if (el.tagName === 'TD' || el.tagName === 'TH') {
        if (el.previousElementSibling) out += '\t'
      }
    }
    for (const child of node.childNodes) walk(child)
    if (el && blocks.has(el.tagName)) out += '\n'
  }
  walk(root)
  return out
}

/** Line endings tidied, spaces at line ends and runs of empty lines taken out. */
const tidy = (text: string) =>
  text
    .replace(/\r\n?/g, '\n')
    .replace(/[ \t]+\n/g, '\n')
    .replace(/\n{3,}/g, '\n\n')
    .trim()

/** A quote cut to what a message carries, "…" where it was cut. */
export function quoteOf(text: string, words = text): Quote | null {
  const tidied = tidy(text)
  if (!tidied) return null
  const cut = tidied.length > QuoteChars
  return { text: cut ? `${tidied.slice(0, QuoteChars - 1)}…` : tidied, words: tidy(words), cut }
}

/** The answer's message an element is in: the one marked as a source of quotes (`data-quote-source`). */
const sourceElement = (node: Node | null): HTMLElement | null => (node instanceof Element ? node : node?.parentElement)?.closest<HTMLElement>('[data-quote-source]') ?? null

/** Which answer's message (its id) a selection starts in; null when not in one that can be quoted. */
export const quotedIn = (range: Range): string | null => sourceElement(range.startContainer)?.dataset.quoteSource ?? null

/**
 * What a selection in an answer quotes. Only the part inside the message where it starts counts. Its words are looked up in
 * the message's Markdown (`sourceOf`): found, and not much longer than what was selected, the Markdown is quoted, so code,
 * links and formatting survive; otherwise the words as they were shown.
 */
export function quoteOfRange(range: Range, sourceOf: (id: string) => string | undefined): Quote | null {
  const host = sourceElement(range.startContainer)
  if (!host) return null
  const part = range.cloneRange()
  if (!host.contains(range.endContainer)) part.setEnd(host, host.childNodes.length)
  const words = plainOf(part.cloneContents())
  if (!words.trim()) return null
  const source = sourceOf(host.dataset.quoteSource!)
  const found = source ? locate(source, tidy(words)) : null
  const wanted = tidy(words)
  return quoteOf(found && found.text.length <= wanted.length * 1.5 + 200 ? found.text : wanted, wanted)
}

/** A quote's Markdown as plain text, for showing it in a line or two. */
export const quoteForDisplay = (md: string) => tidy(plainOf(markdownElement(md)))

/**
 * The answer that holds a quote: the nearest before the question (by its id) on the branch, the quote's Markdown or its words
 * found in it. Null when none does (another branch, or a chat from Code Arena).
 */
export function findQuoted(path: Message[], beforeId: string, quote: string): string | null {
  const end = path.findIndex((m) => m.id === beforeId)
  const shown = quoteForDisplay(quote).slice(0, 200)
  for (let i = (end < 0 ? path.length : end) - 1; i >= 0; i--) {
    const m = path[i]!
    if (m.role !== 'assistant' || !m.content) continue
    if (m.content.includes(quote) || (shown && tidy(plainOf(markdownElement(m.content))).includes(shown))) return m.id
  }
  return null
}
