import { describe, expect, it } from 'vitest'
import { blank } from './live'
import { collectFiles, parseFence } from './files'

describe('files in a chat', () => {
  it('reads a file name from the fence, or from the first line', () => {
    expect(parseFence('ts title="src/app.ts"', 'x')).toEqual({ lang: 'typescript', name: 'src/app.ts' })
    expect(parseFence('python:tools/run.py', 'x')).toEqual({ lang: 'python', name: 'tools/run.py' })
    expect(parseFence('src/main.rs', 'x')).toEqual({ lang: 'rust', name: 'src/main.rs' })
    expect(parseFence('bash', '# file: deploy.sh\necho hi')).toEqual({ lang: 'bash', name: 'deploy.sh' })
    expect(parseFence('js', 'console.log(1)')).toEqual({ lang: 'javascript', name: null })
    expect(parseFence('', 'plain')).toEqual({ lang: null, name: null })
  })

  it('collects attachments and code, the newest version of a named file once', () => {
    const q = { ...blank('q', 'user', null, 'see'), attachments: [{ id: 'f1', fileName: 'notes.txt', size: 5, truncated: false, kind: 'text' as const, contentType: 'text/plain' }] }
    const a1 = blank('a1', 'assistant', 'q', '```ts title="a.ts"\nconst v = 1\n```\n\n```\nplain\n```')
    const a2 = blank('a2', 'assistant', 'a1', 'Better:\n\n```ts title="a.ts"\nconst v = 2\n```')
    const files = collectFiles([q, a1, a2])
    expect(files.map((f) => f.name)).toEqual(['notes.txt', 'a.ts', 'snippet-1.txt'])
    expect(files.find((f) => f.name === 'a.ts')).toMatchObject({ code: 'const v = 2' })
  })
})
