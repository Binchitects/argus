import type * as Monaco from 'monaco-editor'
import { describe, expect, it } from 'vitest'
import { isSnippetsFile, snippetsOf } from './project-snippets'
import { applies, readSnippets, snippetSuggestions } from './snippets'

const kinds = { CompletionItemKind: { Snippet: 27 }, CompletionItemInsertTextRule: { InsertAsSnippet: 4 } } as unknown as Parameters<typeof snippetSuggestions>[0]

/** A model of a language, with the word before the cursor. */
const model = (language: string, word: { startColumn: number; endColumn: number }) => ({ getLanguageId: () => language, getWordUntilPosition: () => ({ word: '', ...word }) }) as unknown as Monaco.editor.ITextModel
const at = (lineNumber: number, column: number) => ({ lineNumber, column }) as Monaco.Position

describe("VS Code's snippets", () => {
  it('reads a snippets file: prefixes, a body of lines, the description; what is no snippet is left out', () => {
    const snippets = readSnippets(`{
      // As an extension writes them: JSON with comments.
      "Print": { "prefix": ["log", "print"], "body": ["console.log($1)", "$0"], "description": "Log a value" },
      "Class": { "prefix": "cls", "body": "class \${1:Name} {}", "scope": "typescript, javascript" },
      "No prefix": { "body": "x" },
      "No body": { "prefix": "nb" },
      "Not one": 5,
    }`)
    expect(snippets).toEqual([
      { name: 'Print', prefixes: ['log', 'print'], body: 'console.log($1)\n$0', description: 'Log a value' },
      { name: 'Class', prefixes: ['cls'], body: 'class ${1:Name} {}', scope: ['typescript', 'javascript'] },
    ])
    expect(readSnippets('not json')).toEqual([])
    expect(readSnippets('[1, 2]')).toEqual([])
  })

  it("takes VS Code's language ids in a snippet's scope as Monaco's", () => {
    const snippets = readSnippets('{ "Comp": { "prefix": "comp", "body": "export function $1() {}", "scope": "typescriptreact" } }')
    expect(snippetSuggestions(kinds, snippets, model('typescript', { startColumn: 1, endColumn: 5 }), at(1, 5)).map((s) => (s.label as { label: string }).label)).toEqual(['comp'])
  })

  it('suggests each prefix as a snippet over the word typed, for the languages it is for', () => {
    const snippets = readSnippets('{ "Print": { "prefix": ["log", "print"], "body": ["console.log($1)", "$0"], "description": "Log a value" }, "Py": { "prefix": "main", "body": "if __name__ == \'__main__\':\\n\\t$0", "scope": "python" } }')
    const items = snippetSuggestions(kinds, snippets, model('typescript', { startColumn: 5, endColumn: 7 }), at(3, 7))
    expect(items.map((i) => (i.label as { label: string }).label)).toEqual(['log', 'print'])
    expect(items[0]).toMatchObject({
      kind: 27,
      insertText: 'console.log($1)\n$0',
      insertTextRules: 4,
      detail: 'Log a value',
      filterText: 'log',
      range: { startLineNumber: 3, startColumn: 5, endLineNumber: 3, endColumn: 7 },
    })
    // A snippet scoped to Python is offered in Python's files only.
    expect(snippetSuggestions(kinds, snippets, model('python', { startColumn: 1, endColumn: 1 }), at(1, 1)).map((i) => i.insertText)).toEqual(['console.log($1)\n$0', 'console.log($1)\n$0', "if __name__ == '__main__':\n\t$0"])
  })

  it("takes include and exclude: a glob with no folder by the file's name, else by its path", () => {
    const [test, notGen] = readSnippets(`{
      "Test": { "prefix": "it", "body": "it('$1', () => {})", "include": "*.test.ts" },
      "Not generated": { "prefix": "fn", "body": "function $1() {}", "include": ["src/**"], "exclude": ["**/generated/**"] },
    }`)
    expect(test!.include).toEqual(['*.test.ts'])
    expect(applies(test!, 'typescript', 'src/a.test.ts')).toBe(true)
    expect(applies(test!, 'typescript', 'src/a.ts')).toBe(false)
    // A file with no path (a compare's side): only a snippet with no include.
    expect(applies(test!, 'typescript', null)).toBe(false)
    expect(applies(notGen!, 'typescript', 'src/x/a.ts')).toBe(true)
    expect(applies(notGen!, 'typescript', 'src/generated/a.ts')).toBe(false)
    expect(applies(notGen!, 'typescript', 'lib/a.ts')).toBe(false)
    const items = snippetSuggestions(kinds, [test!, notGen!], model('typescript', { startColumn: 1, endColumn: 1 }), at(1, 1), 'src/a.test.ts')
    expect(items.map((i) => (i.label as { label: string }).label)).toEqual(['it', 'fn'])
  })
})

describe("the project's and the person's snippets", () => {
  it('reads each file: a user NAME.json is for its language, each says where it is from', () => {
    const snippets = snippetsOf({
      problems: [],
      files: [
        { source: 'project', path: '.vscode/team.code-snippets', language: null, version: '1', text: '{ "Header": { "prefix": "hdr", "body": "// (c) $CURRENT_YEAR" } }' },
        { source: 'user', path: 'user:snippets/python.json', language: 'python', version: '2', text: '{ "Main": { "prefix": "main", "body": "if __name__ == 1:\\n\\t$0" } }' },
        { source: 'user', path: 'user:snippets/mine.code-snippets', language: null, version: '3', text: '{ "TS": { "prefix": "ts", "body": "x", "scope": "typescript" } }' },
      ],
    })
    expect(snippets.map((s) => [s.name, s.from, s.scope])).toEqual([
      ['Header', 'Workspace Snippet', undefined],
      ['Main', 'User Snippet', ['python']],
      ['TS', 'User Snippet', ['typescript']],
    ])
    const labels = (language: string) => snippetSuggestions(kinds, snippets, model(language, { startColumn: 1, endColumn: 1 }), at(1, 1), 'a.py').map((i) => i.label)
    expect(labels('python')).toEqual([{ label: 'hdr', description: 'Header (Workspace Snippet)' }, { label: 'main', description: 'Main (User Snippet)' }])
    expect(labels('typescript').map((l) => (l as { label: string }).label)).toEqual(['hdr', 'ts'])
  })

  it('knows the files whose saving reads the snippets again', () => {
    expect(isSnippetsFile('.vscode/team.code-snippets')).toBe(true)
    expect(isSnippetsFile('/srv/lib/.vscode/lib.code-snippets')).toBe(true)
    expect(isSnippetsFile('user:snippets/python.json')).toBe(true)
    expect(isSnippetsFile('.vscode/settings.json')).toBe(false)
    expect(isSnippetsFile('src/a.code-snippets')).toBe(false)
  })
})
