import { describe, expect, it } from 'vitest'
import { gitlabLink, parseResult, resultCount, snippetParts, toHits } from './argus'
import { collectFiles } from './files'
import { blank } from './live'
import type { Message } from './types'

// The shapes Argus returns (src/argus/store/queries.py).
const symbol = { repo_id: 1, path_with_namespace: 'group/app', path: 'src/parse.c', name: 'ParseHeader', kind: 'function', line: 10, end_line: 24, signature: 'int ParseHeader(const char *buf)', scope: null, is_public: 1, doc: 'Reads a header.' }
const reference = { repo: 'group/app', repo_id: 1, path: 'src/main.c', line: 42, context: '  ParseHeader(buf);', is_definition: false }
const search = { repo_id: 1, path_with_namespace: 'group/app', path: 'src/parse.c', rank: -3.2, snippet: '…int [ParseHeader](const char *buf) { return buf[0]; }…' }
const file = { repo_id: 1, path_with_namespace: 'group/app', path: 'src/parse.c', lang: 'c', size: 120, content: 'int ParseHeader(const char *buf) {\n  return 0;\n}\n', truncated: false }

describe('Argus results', () => {
  it('reads JSON, and leaves text (errors, notices) as text', () => {
    expect(parseResult('[{"a":1}]')).toEqual([{ a: 1 }])
    expect(parseResult('Nothing you have access to matches this')).toBeUndefined()
    expect(parseResult('{"a":1}\n{"b":2}')).toBeUndefined()
  })

  it('turns symbols, references, search hits and a file into places in the code', () => {
    expect(toHits([symbol])![0]).toMatchObject({ repo: 'group/app', path: 'src/parse.c', line: 10, endLine: 24, name: 'ParseHeader', kind: 'function', code: 'int ParseHeader(const char *buf)', doc: 'Reads a header.' })
    expect(toHits([reference])![0]).toMatchObject({ repo: 'group/app', line: 42, code: '  ParseHeader(buf);', kind: null })
    expect(toHits([{ ...reference, is_definition: true }])![0]!.kind).toBe('definition')
    expect(toHits([search])![0]).toMatchObject({ snippet: search.snippet, code: null })
    expect(toHits(file)![0]).toMatchObject({ content: file.content, truncated: false })
  })

  it('does not treat other answers as places in the code', () => {
    expect(toHits([{ repo_id: 1, path_with_namespace: 'group/app', confidence: 0.9, why: 'named' }])).toBeNull()
    expect(toHits({ repos: [], truncated: false })).toBeNull()
    expect(toHits({ path: 'src', files: 3 })).toBeNull()
    expect(toHits([])).toBeNull()
    expect(resultCount([symbol, symbol])).toBe('2 results')
    expect(resultCount(file)).toBeNull()
  })

  it('links to the file and lines in GitLab, on the branch asked about', () => {
    expect(gitlabLink('https://gitlab.example.com/', 'group/sub/app', 'src/a b.c', null, 10, 24)).toBe('https://gitlab.example.com/group/sub/app/-/blob/HEAD/src/a%20b.c#L10-24')
    expect(gitlabLink('https://gitlab.example.com', 'group/app', 'x.c', 'release/2.0', 7, 7)).toBe('https://gitlab.example.com/group/app/-/blob/release%2F2.0/x.c#L7')
    expect(gitlabLink('https://gitlab.example.com', 'group/app', 'x.c', null, null, null)).toBe('https://gitlab.example.com/group/app/-/blob/HEAD/x.c')
  })

  it('marks the words searched for, not every bracket in the code', () => {
    const parts = snippetParts(search.snippet, 'ParseHeader')
    expect(parts.filter((p) => p.match).map((p) => p.text)).toEqual(['ParseHeader'])
    expect(parts.map((p) => p.text).join('')).toBe('…int ParseHeader(const char *buf) { return buf[0]; }…')
    // A prefix query (Parse*) marks the words it matched.
    expect(snippetParts('[ParseHeader] and [ParseBody]', 'Parse*').filter((p) => p.match)).toHaveLength(2)
  })

  it('a file Argus read is in the Files panel, once, at its latest', () => {
    const call = (id: string, args: object) => ({ id, function: { name: 'get_file', arguments: JSON.stringify(args) } })
    const path: Message[] = [
      { ...blank('q', 'user', null), content: 'show me' },
      { ...blank('a', 'assistant', 'q'), toolCalls: [call('c1', { repo_id: 1, path: 'src/parse.c' }), call('c2', { repo_id: 1, path: 'src/parse.c', branch: 'dev' })] },
      { ...blank('t1', 'tool', 'a'), toolCallId: 'c1', toolName: 'get_file', content: JSON.stringify(file) },
      { ...blank('t2', 'tool', 't1'), toolCallId: 'c2', toolName: 'get_file', content: JSON.stringify({ ...file, content: 'newer' }) },
      { ...blank('t3', 'tool', 't2'), toolCallId: 'c3', toolName: 'get_file', content: 'No file at repo_id=1', status: 'failed' },
    ]
    const files = collectFiles(path)
    expect(files).toHaveLength(1)
    expect(files[0]).toMatchObject({ kind: 'repo', name: 'parse.c', repo: 'group/app', path: 'src/parse.c', branch: 'dev', lang: 'c', code: 'newer' })
  })
})
