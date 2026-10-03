import { Marked } from 'marked'
import { sanitize } from './markdown-blocks'
import { toolTitle } from './format'
import type { Message } from './types'

/** What a tool answered, at most this much in an export (the chat keeps it all). */
const ToolResultChars = 4_000

const fence = (text: string, lang = '') => {
  // A fence longer than any run of backticks inside, so the text cannot close it.
  const longest = Math.max(2, ...[...text.matchAll(/`+/g)].map((m) => m[0].length))
  const ticks = '`'.repeat(longest + 1)
  return `${ticks}${lang}\n${text}\n${ticks}`
}

const cut = (text: string, max: number) => (text.length > max ? `${text.slice(0, max)}\n… (${(text.length - max).toLocaleString()} more characters)` : text)

/** A file name from a chat's title: no characters a file system refuses. */
export function exportName(title: string | null, what: string, extension: string) {
  // oxlint-disable-next-line no-control-regex -- control characters are what it takes out
  const base = (title ?? '').replace(/[\\/:*?"<>|\u0000-\u001f]+/g, ' ').replace(/\s+/g, ' ').trim().slice(0, 80) || 'chat'
  return `${base}${what ? ` ${what}` : ''}.${extension}`
}

/**
 * The branch on screen as Markdown: each question and answer under a heading, the
 * files a question carried, each tool call with what it was asked and what it
 * answered (long answers cut), where the chat was compacted. Thinking is left out.
 */
export function chatToMarkdown(title: string, path: Message[], at = new Date()): string {
  const results = new Map(path.filter((m) => m.role === 'tool').map((m) => [m.toolCallId, m]))
  const models = [...new Set(path.map((m) => m.model).filter(Boolean))]
  const out = [`# ${title}`, '', `*Exported from Argus Arena on ${at.toLocaleString()}${models.length ? ` · ${models.join(', ')}` : ''}*`, '']
  for (const m of path) {
    if (m.role === 'user') {
      out.push('## You', '', m.content.trim() || '*(files only)*')
      if (m.attachments.length) out.push('', `*Attached: ${m.attachments.map((a) => a.fileName).join(', ')}*`)
      out.push('')
    } else if (m.role === 'assistant') {
      if (m.content.trim() || m.toolCalls?.length) out.push(`## ${m.model ?? 'Assistant'}`, '')
      if (m.content.trim()) out.push(m.content.trim(), '')
      for (const call of m.toolCalls ?? []) {
        const result = results.get(call.id)
        out.push(`**${toolTitle(call.function.name)}**${result?.status === 'failed' ? ' (failed)' : ''}`, '', fence(call.function.arguments || '{}', 'json'), '')
        if (result) {
          if (result.content.trim()) out.push(fence(cut(result.content.trim(), ToolResultChars)), '')
          if (result.attachments.length) out.push(`*Made: ${result.attachments.map((a) => a.fileName).join(', ')}*`, '')
        }
      }
      if (m.status === 'failed' && m.error) out.push(`*The answer failed: ${m.error}*`, '')
      if (m.status === 'stopped') out.push('*Stopped.*', '')
    }
    if (m.summary) out.push('---', '', '*The chat was compacted here. The summary the next answers read:*', '', m.summary.trim(), '', '---', '')
  }
  return out.join('\n').replace(/\n{3,}/g, '\n\n').trim() + '\n'
}

const lexer = new Marked({ gfm: true, breaks: false })

/** Markdown as a web page of its own: readable, printable (Save as PDF), its HTML sanitized. */
export function markdownToHtml(title: string, markdown: string): string {
  const body = sanitize(lexer.parse(markdown, { async: false }))
  const esc = title.replace(/[&<>"]/g, (c) => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;' })[c]!)
  return `<!doctype html>
<html lang="en">
<head>
<meta charset="utf-8">
<meta name="viewport" content="width=device-width, initial-scale=1">
<title>${esc}</title>
<style>
  :root { color-scheme: light; }
  body { margin: 0 auto; max-width: 46rem; padding: 2rem 1.25rem 4rem; font: 16px/1.6 system-ui, -apple-system, "Segoe UI", sans-serif; color: #1c2230; background: #fff; }
  h1 { font-size: 1.6rem; line-height: 1.3; margin: 0 0 .25rem; }
  h2 { font-size: 1.05rem; margin: 2rem 0 .5rem; padding-top: 1rem; border-top: 1px solid #e3e7ef; color: #2a4fa0; }
  h1 + p em { color: #5b6476; }
  pre { overflow-x: auto; padding: .75rem 1rem; border-radius: .5rem; background: #f4f6fa; font-size: .85rem; line-height: 1.45; white-space: pre-wrap; word-break: break-word; }
  code { font-family: ui-monospace, "JetBrains Mono", Consolas, monospace; font-size: .9em; }
  :not(pre) > code { padding: .1em .35em; border-radius: .3em; background: #f0f2f7; }
  table { border-collapse: collapse; display: block; overflow-x: auto; }
  th, td { border: 1px solid #dde2ec; padding: .35rem .6rem; }
  blockquote { margin: 0; padding-left: 1rem; border-left: 3px solid #d5dbe8; color: #4a5468; }
  hr { border: 0; border-top: 1px dashed #c9d0de; margin: 1.5rem 0; }
  img { max-width: 100%; }
  [dir="rtl"] { text-align: right; }
  @media print { body { max-width: none; padding: 0; } h2 { break-after: avoid; } pre { break-inside: avoid; } }
</style>
</head>
<body>
${body}
</body>
</html>
`
}

/** The branch on screen as data: who said what, when, with which model, and its tool calls. */
export function chatToJson(conversation: { id: string; title: string }, path: Message[], at = new Date()): string {
  return JSON.stringify(
    {
      id: conversation.id,
      title: conversation.title,
      exportedAt: at.toISOString(),
      messages: path.map((m) => ({
        id: m.id,
        role: m.role,
        content: m.content,
        model: m.model,
        createdAt: m.createdAt,
        status: m.status,
        ...(m.toolCalls?.length ? { toolCalls: m.toolCalls.map((c) => ({ id: c.id, name: c.function.name, arguments: c.function.arguments })) } : {}),
        ...(m.role === 'tool' ? { toolCallId: m.toolCallId, toolName: m.toolName } : {}),
        ...(m.attachments.length ? { attachments: m.attachments.map((a) => ({ fileName: a.fileName, size: a.size, kind: a.kind })) } : {}),
        ...(m.promptTokens != null ? { tokens: { prompt: m.promptTokens, cached: m.cachedTokens, completion: m.completionTokens } } : {}),
        ...(m.summary ? { summary: m.summary } : {}),
      })),
    },
    null,
    2,
  )
}
