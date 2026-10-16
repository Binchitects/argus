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

function tableBlock(t: Token): Block {
  const table = t as Tokens.Table
  return { kind: 'table', head: table.header.map((c) => tableCell(c.text)), align: table.align, rows: table.rows.map((r) => r.map((c) => tableCell(c.text))) }
}

/**
 * Tokens in blocks: top-level code fences become components, tables tables of their own (so they sort and filter),
 * and each run of the other tokens one piece of HTML, made by `html`.
 */
function blocksOf(tokens: Token[], html: (run: Token[]) => string, table: (t: Token) => Block): Block[] {
  const out: Block[] = []
  let pending: Token[] = []
  const flush = () => {
    if (!pending.length) return
    out.push({ kind: 'html', html: html(pending) })
    pending = []
  }
  for (const t of tokens) {
    if (t.type === 'table') {
      flush()
      out.push(table(t))
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

/** Markdown in blocks: top-level code fences become components, the rest sanitised HTML. */
export function toBlocks(text: string): Block[] {
  const tokens = marked.lexer(text)
  return blocksOf(tokens, (run) => sanitize(marked.parser(Object.assign(run, { links: tokens.links }) as TokensList)), tableBlock)
}

/**
 * An answer's blocks while it is written, made again as each piece of it comes: only the end of the text is read
 * again (from the start of its last two blocks, which the new words can still change), and each block's HTML is
 * made once, when it is complete. An answer of many paragraphs then costs about the same for each new word as one
 * of a single paragraph. What it shows matches toBlocks, which draws the answer once it is done.
 */
export class LiveBlocks {
  private text = ''
  private tokens: Token[] = []
  private html = new Map<string, string>()
  private tables = new Map<string, Block>()

  update(text: string): Block[] {
    this.tokens = this.lex(text)
    this.text = text
    // A link defined in it changes the words before the definition too (they name it): it is read whole, as when done.
    if (this.tokens.some((t) => t.type === 'def')) {
      this.tokens = []
      return toBlocks(text)
    }
    const links = (this.tokens as Partial<TokensList>).links ?? {}
    const html = new Map<string, string>()
    const tables = new Map<string, Block>()
    // A token's HTML by its Markdown: the same paragraph is made once, however often the words after it change.
    const tokenHtml = (t: Token) => {
      const made = html.get(t.raw) ?? this.html.get(t.raw) ?? sanitize(marked.parser(Object.assign([t], { links }) as TokensList))
      html.set(t.raw, made)
      return made
    }
    const table = (t: Token) => {
      const made = tables.get(t.raw) ?? this.tables.get(t.raw) ?? tableBlock(t)
      tables.set(t.raw, made)
      return made
    }
    const blocks = blocksOf(this.tokens, (run) => run.map(tokenHtml).join(''), table)
    // Only what this text still has is kept.
    this.html = html
    this.tables = tables
    return blocks
  }

  /** The text's tokens: those of the text before kept up to its last two blocks when the text only grew. */
  private lex(text: string): Token[] {
    if (this.tokens.length > 2 && text.startsWith(this.text)) {
      const kept = this.tokens.slice(0, -2)
      const before = kept.map((t) => t.raw).join('')
      // The tokens cover the text from its start; when they do not, it is all read again.
      if (text.startsWith(before)) {
        const rest = marked.lexer(text.slice(before.length))
        return Object.assign([...kept, ...rest], { links: rest.links })
      }
    }
    return marked.lexer(text)
  }
}
