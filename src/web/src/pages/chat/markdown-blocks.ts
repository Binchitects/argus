import DOMPurify from 'dompurify'
import { Marked, type Token, type Tokens, type TokensList } from 'marked'
import markedKatex from 'marked-katex-extension'
import type { PreviewKind } from '@/preview/kind'
import { parseFence } from './files'
import { highlight } from './highlight'

const marked = new Marked({ gfm: true, breaks: false })
marked.use(markedKatex({ throwOnError: false, nonStandard: true }))
marked.use({
  renderer: {
    // Code inside a list or a quote: highlighted in place (top-level code is a component).
    code({ text, lang }) {
      const { lang: l } = parseFence(lang, text)
      return `<pre class="md-nested-code"><code class="hljs">${highlight(text, l)}</code></pre>`
    },
  },
})

// Model output is never trusted with script, forms or styles. KaTeX lays formulas
// out with inline styles, so those survive inside its output only.
const purify = DOMPurify()
purify.addHook('uponSanitizeAttribute', (node, data) => {
  if (data.attrName === 'style' && !(node as Element).closest?.('.katex')) data.keepAttr = false
})
/**
 * Right-to-left text (Persian, Arabic, Hebrew) reads right to left, each block by its own
 * first letters, as ChatGPT does: a Persian paragraph beside an English one. A list, a
 * quote or a table takes the direction of its first words, and what is inside follows
 * (an inner dir="auto" would hide those words from it). Code stays left to right.
 */
const directional = new Set(['P', 'H1', 'H2', 'H3', 'H4', 'H5', 'H6', 'BLOCKQUOTE', 'UL', 'OL', 'TABLE', 'DL'])
purify.addHook('afterSanitizeAttributes', (node) => {
  if (directional.has(node.tagName) && !node.parentElement?.closest('li, td, th, blockquote')) node.setAttribute('dir', 'auto')
  if (node.tagName === 'PRE' || node.tagName === 'CODE' || node.classList?.contains('katex')) node.setAttribute('dir', 'ltr')
  if (node.tagName === 'A' && /^https?:/i.test(node.getAttribute('href') ?? '')) {
    node.setAttribute('target', '_blank')
    node.setAttribute('rel', 'noopener noreferrer nofollow')
  }
})

export function sanitize(html: string): string {
  return purify.sanitize(html, { FORBID_TAGS: ['style', 'form', 'input', 'button', 'textarea', 'select', 'iframe', 'object', 'embed'], ADD_ATTR: ['target', 'dir'] })
}

/** A table's cell: sanitised HTML to show, its text to sort and filter by. */
export interface TableCell {
  html: string
  text: string
}

export type Align = 'left' | 'center' | 'right' | null

type Block =
  | { kind: 'html'; html: string }
  | { kind: 'code'; code: string; lang: string | null; name: string | null; preview: PreviewKind | null }
  | { kind: 'table'; head: TableCell[]; align: Align[]; rows: TableCell[][] }

function tableCell(text: string): TableCell {
  const html = sanitize(marked.parseInline(text, { async: false }))
  return { html, text: new DOMParser().parseFromString(html, 'text/html').body.textContent ?? '' }
}

/** Markdown in blocks: top-level code fences become components, the rest sanitised HTML. */
export function toBlocks(text: string): Block[] {
  const tokens = marked.lexer(text)
  const out: Block[] = []
  let pending: Token[] = []
  const flush = () => {
    if (!pending.length) return
    const list = Object.assign(pending, { links: tokens.links }) as TokensList
    out.push({ kind: 'html', html: sanitize(marked.parser(list)) })
    pending = []
  }
  for (const t of tokens) {
    if (t.type === 'table') {
      // A table of its own, so it sorts and filters (Tokens.Table: its header and rows of cells).
      flush()
      const table = t as Tokens.Table
      out.push({ kind: 'table', head: table.header.map((c) => tableCell(c.text)), align: table.align, rows: table.rows.map((r) => r.map((c) => tableCell(c.text))) })
    } else if (t.type === 'code' && (t as { codeBlockStyle?: string }).codeBlockStyle !== 'indented') {
      flush()
      const { lang, name, preview } = parseFence((t as { lang?: string }).lang, (t as { text: string }).text)
      out.push({ kind: 'code', code: (t as { text: string }).text, lang, name, preview })
    } else {
      pending.push(t)
    }
  }
  flush()
  return out
}
