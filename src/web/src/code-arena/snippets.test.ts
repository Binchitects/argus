import type * as Monaco from 'monaco-editor'
import { describe, expect, it } from 'vitest'
import { readSnippets, snippetSuggestions } from './snippets'

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
})
