import DOMPurify from 'dompurify'
import { Marked, type Token, type TokensList } from 'marked'
import markedKatex from 'marked-katex-extension'
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
purify.addHook('afterSanitizeAttributes', (node) => {
  if (node.tagName === 'A' && /^https?:/i.test(node.getAttribute('href') ?? '')) {
    node.setAttribute('target', '_blank')
    node.setAttribute('rel', 'noopener noreferrer nofollow')
  }
})

export function sanitize(html: string): string {
  return purify.sanitize(html, { FORBID_TAGS: ['style', 'form', 'input', 'button', 'textarea', 'select', 'iframe', 'object', 'embed'], ADD_ATTR: ['target'] })
}

type Block = { kind: 'html'; html: string } | { kind: 'code'; code: string; lang: string | null; name: string | null }

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
    if (t.type === 'code' && (t as { codeBlockStyle?: string }).codeBlockStyle !== 'indented') {
      flush()
      const { lang, name } = parseFence((t as { lang?: string }).lang, (t as { text: string }).text)
      out.push({ kind: 'code', code: (t as { text: string }).text, lang, name })
    } else {
      pending.push(t)
    }
  }
  flush()
  return out
}
