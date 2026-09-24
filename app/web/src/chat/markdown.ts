import DOMPurify from 'dompurify'
import hljs from 'highlight.js/lib/core'
import bash from 'highlight.js/lib/languages/bash'
import c from 'highlight.js/lib/languages/c'
import cpp from 'highlight.js/lib/languages/cpp'
import csharp from 'highlight.js/lib/languages/csharp'
import css from 'highlight.js/lib/languages/css'
import diff from 'highlight.js/lib/languages/diff'
import dockerfile from 'highlight.js/lib/languages/dockerfile'
import go from 'highlight.js/lib/languages/go'
import java from 'highlight.js/lib/languages/java'
import javascript from 'highlight.js/lib/languages/javascript'
import json from 'highlight.js/lib/languages/json'
import markdown from 'highlight.js/lib/languages/markdown'
import powershell from 'highlight.js/lib/languages/powershell'
import python from 'highlight.js/lib/languages/python'
import rust from 'highlight.js/lib/languages/rust'
import sql from 'highlight.js/lib/languages/sql'
import typescript from 'highlight.js/lib/languages/typescript'
import xml from 'highlight.js/lib/languages/xml'
import yaml from 'highlight.js/lib/languages/yaml'
import { Marked } from 'marked'

const languages = { bash, c, cpp, csharp, css, diff, dockerfile, go, java, javascript, json, markdown, powershell, python, rust, sql, typescript, xml, yaml }
for (const [name, lang] of Object.entries(languages)) hljs.registerLanguage(name, lang)
const aliases: Record<string, string> = { sh: 'bash', shell: 'bash', zsh: 'bash', 'c++': 'cpp', cs: 'csharp', js: 'javascript', ts: 'typescript', py: 'python', yml: 'yaml', html: 'xml', ps1: 'powershell', rs: 'rust', md: 'markdown' }

const escape = (s: string) => s.replace(/[&<>"]/g, (ch) => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;' })[ch]!)

const marked = new Marked({
  gfm: true,
  breaks: false,
  renderer: {
    code({ text, lang }) {
      const name = aliases[(lang ?? '').toLowerCase()] ?? (lang ?? '').toLowerCase()
      const html = name && hljs.getLanguage(name) ? hljs.highlight(text, { language: name }).value : escape(text)
      return `<div class="code"><div class="code-bar"><span>${escape(lang || 'text')}</span><button type="button" class="copy-code">Copy</button></div><pre><code class="hljs">${html}</code></pre></div>`
    },
  },
})

/** Markdown to safe HTML: model output is never trusted with script, styles or handlers. */
export function renderMarkdown(text: string): string {
  return DOMPurify.sanitize(marked.parse(text, { async: false }), { FORBID_TAGS: ['style', 'form', 'input'], FORBID_ATTR: ['style'] })
}
