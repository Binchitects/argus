import { describe, expect, it } from 'vitest'
import { blank } from './live'
import { collectFiles, parseFence } from './files'

describe('files in a chat', () => {
  it('reads a file name from the fence, or from the first line', () => {
    expect(parseFence('ts title="src/app.ts"', 'x')).toEqual({ lang: 'typescript', name: 'src/app.ts', preview: null })
    expect(parseFence('python:tools/run.py', 'x')).toEqual({ lang: 'python', name: 'tools/run.py', preview: null })
    expect(parseFence('src/main.rs', 'x')).toEqual({ lang: 'rust', name: 'src/main.rs', preview: null })
    expect(parseFence('bash', '# file: deploy.sh\necho hi')).toEqual({ lang: 'bash', name: 'deploy.sh', preview: null })
    expect(parseFence('js', 'console.log(1)')).toEqual({ lang: 'javascript', name: null, preview: null })
    expect(parseFence('', 'plain')).toEqual({ lang: null, name: null, preview: null })
    // What can run keeps its own language for the preview, while highlighting uses the alias.
    expect(parseFence('tsx', 'export default () => <p/>')).toEqual({ lang: 'typescript', name: null, preview: 'react' })
    expect(parseFence('html title="index.html"', '<p/>')).toEqual({ lang: 'xml', name: 'index.html', preview: 'html' })
  })

  it('collects attachments and code, the newest version of a named file once', () => {
    const q = { ...blank('q', 'user', null, 'see'), attachments: [{ id: 'f1', fileName: 'notes.txt', size: 5, truncated: false, kind: 'text' as const, contentType: 'text/plain' }] }
    const a1 = blank('a1', 'assistant', 'q', '```ts title="a.ts"\nconst v = 1\n```\n\n```\nplain\n```')
    const a2 = blank('a2', 'assistant', 'a1', 'Better:\n\n```ts title="a.ts"\nconst v = 2\n```')
    const files = collectFiles([q, a1, a2])
    expect(files.map((f) => f.name)).toEqual(['notes.txt', 'a.ts', 'snippet-1.txt'])
    expect(files.find((f) => f.name === 'a.ts')).toMatchObject({ code: 'const v = 2' })
  })
  it("lists sub-agents' pictures and code as soon as they are made, once when kept", () => {
    const picture = { id: 'p1', fileName: 'apple.png', size: 9, truncated: false, kind: 'image' as const, contentType: 'image/png' }
    const work = [{ title: 'A', instructions: '', reasoning: '', text: '```py title="fit.py"\nfit()\n```', steps: [{ id: 's1', name: 'generate_image', arguments: '{}', result: 'ok', files: [picture] }], status: 'running' as const, error: null, ms: null }]
    const q = blank('q', 'user', null, 'draw')
    const a = { ...blank('a', 'assistant', 'q'), toolCalls: [{ id: 'd1', function: { name: 'delegate', arguments: '{}' } }] }
    expect(collectFiles([q, a], { d1: work }).map((f) => [f.name, f.kind === 'attachment' && f.made])).toEqual([['apple.png', true], ['fit.py', false]])
    const kept = { ...blank('t', 'tool', 'a', '[]'), toolCallId: 'd1', attachments: [picture], details: { agents: work } }
    expect(collectFiles([q, a, kept], { d1: work }).map((f) => f.name)).toEqual(['apple.png', 'fit.py'])
  })
})
