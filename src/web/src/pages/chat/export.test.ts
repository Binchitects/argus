import { describe, expect, it } from 'vitest'
import { blank } from './live'
import { chatToJson, chatToMarkdown, exportName, markdownToHtml } from './export'

const q = { ...blank('q', 'user', null, 'Where is `DecodeFrame`?'), attachments: [{ id: 'f', fileName: 'notes.txt', size: 3, truncated: false, kind: 'text' as const, contentType: 'text/plain' }] }
const a1 = { ...blank('a1', 'assistant', 'q'), model: 'Main', toolCalls: [{ id: 'c1', function: { name: 'find_symbol', arguments: '{"name":"DecodeFrame"}' } }] }
const t1 = { ...blank('t1', 'tool', 'a1', 'src/decode.c:118 has ```fences``` inside'), toolCallId: 'c1', toolName: 'find_symbol' }
const a2 = { ...blank('a2', 'assistant', 't1', 'In `src/decode.c`.'), model: 'Main', summary: 'They found DecodeFrame.' }

describe('exporting a chat', () => {
  it('writes questions, answers, tool calls and compaction marks as Markdown', () => {
    const md = chatToMarkdown('Decode', [q, a1, t1, a2], new Date('2026-10-03T10:00:00Z'))
    expect(md).toContain('# Decode')
    expect(md).toContain('## You\n\nWhere is `DecodeFrame`?\n\n*Attached: notes.txt*')
    expect(md).toContain('**Find symbol**')
    // A result with backticks in it is fenced by more of them.
    expect(md).toContain('````\nsrc/decode.c:118 has ```fences``` inside\n````')
    expect(md).toContain('## Main\n\nIn `src/decode.c`.')
    expect(md).toContain('They found DecodeFrame.')
  })

  it('makes a page of its own whose HTML cannot run', () => {
    const html = markdownToHtml('A <b> title', '# Hi\n\n<script>alert(1)</script><img src=x onerror="alert(2)">')
    expect(html).toContain('<title>A &lt;b&gt; title</title>')
    expect(html).not.toContain('<script>alert')
    expect(html).not.toContain('onerror')
  })

  it('names the file after the chat and keeps the data whole', () => {
    expect(exportName('a/b: c?', 'summary', 'md')).toBe('a b c summary.md')
    const data = JSON.parse(chatToJson({ id: 'c1', title: 'Decode' }, [q, a1, t1]))
    expect(data.messages.map((m: { role: string }) => m.role)).toEqual(['user', 'assistant', 'tool'])
    expect(data.messages[1].toolCalls[0]).toEqual({ id: 'c1', name: 'find_symbol', arguments: '{"name":"DecodeFrame"}' })
  })
})
