import { describe, expect, it } from 'vitest'
import { insertFinalNewline, trimFinalNewlines, trimTrailingWhitespace, type SaveContext } from './save-participants'
import { effective, readLayer } from './settings'
import { settingsSchema } from './settings-schema'

describe('settings.json', () => {
  it('layers the defaults, the person\'s, the project\'s, then each for the language', () => {
    const user = readLayer('{\n  // mine\n  "editor.fontSize": 15,\n  "editor.tabSize": 2,\n  "[python]": { "editor.tabSize": 8 },\n  "[typescript][javascript]": { "editor.formatOnSave": true }\n}')
    const workspace = readLayer('{ "editor.tabSize": 3, "[python]": { "editor.insertSpaces": false } }', true)
    expect(effective(user, workspace)['editor.fontSize']).toBe(15)
    expect(effective(user, workspace)['editor.tabSize']).toBe(3)
    expect(effective(user, workspace)['editor.wordWrap']).toBe('off')
    // The person's for the language beats the project's for every language.
    expect(effective(user, workspace, 'python')['editor.tabSize']).toBe(8)
    expect(effective(user, workspace, 'python')['editor.insertSpaces']).toBe(false)
    expect(effective(user, workspace, 'javascript')['editor.formatOnSave']).toBe(true)
    expect(effective(user, workspace, 'go')['editor.formatOnSave']).toBe(false)
  })

  it('reads nested objects as dotted names, and refuses values of the wrong kind', () => {
    const l = readLayer('{ "editor": { "minimap": { "enabled": false }, "fontSize": "big" }, "files.autoSave": "sometimes", "someExtension.thing": 1 }')
    expect(l.values['editor.minimap.enabled']).toBe(false)
    expect(l.values['editor.fontSize']).toBeUndefined()
    expect(l.values['someExtension.thing']).toBe(1)
    expect(l.problems).toEqual(['"editor.fontSize" takes a number.', '"files.autoSave" is one of "off", "afterDelay", "onFocusChange", "onWindowChange".'])
  })

  it("keeps the person's own settings out of a project's reach", () => {
    const l = readLayer('{ "terminal.integrated.commandsToSkipShell": ["x"], "[python]": { "files.autoSave": "afterDelay" } }', true)
    expect(l.values['terminal.integrated.commandsToSkipShell']).toBeUndefined()
    expect(l.languages.python?.['files.autoSave']).toBeUndefined()
    expect(l.problems).toHaveLength(2)
    expect(readLayer('[1]').problems).toEqual(['The file holds one object: {"setting": value, …}.'])
    expect(readLayer('{ "a": ').problems[0]).toMatch(/not JSON/)
  })

  it('has a schema the editor checks the files with', () => {
    const schema = settingsSchema() as { properties: Record<string, { type: string }>; patternProperties: Record<string, { properties: Record<string, unknown> }> }
    expect(schema.properties['editor.tabSize']!.type).toBe('integer')
    expect(Object.keys(schema.patternProperties['^\\[.*\\]$']!.properties)).toContain('editor.formatOnSave')
    expect(Object.keys(schema.patternProperties['^\\[.*\\]$']!.properties)).not.toContain('files.autoSave')
  })
})

/** A model with Monaco's few calls the save's steps make. */
function model(text: string) {
  let value = text
  const offset = (line: number, column: number) => value.split('\n').slice(0, line - 1).reduce((n, l) => n + l.length + 1, 0) + column - 1
  return {
    get value() {
      return value
    },
    getLineCount: () => value.split('\n').length,
    getLineContent: (line: number) => value.split('\n')[line - 1]!,
    getEOL: () => '\n',
    pushStackElement: () => undefined,
    pushEditOperations: (_: unknown, edits: { range: { startLineNumber: number; startColumn: number; endLineNumber: number; endColumn: number }; text: string }[]) => {
      for (const e of [...edits].sort((a, b) => offset(b.range.startLineNumber, b.range.startColumn) - offset(a.range.startLineNumber, a.range.startColumn))) {
        value = value.slice(0, offset(e.range.startLineNumber, e.range.startColumn)) + e.text + value.slice(offset(e.range.endLineNumber, e.range.endColumn))
      }
      return null
    },
  }
}

class Range {
  startLineNumber: number
  startColumn: number
  endLineNumber: number
  endColumn: number
  constructor(startLineNumber: number, startColumn: number, endLineNumber: number, endColumn: number) {
    this.startLineNumber = startLineNumber
    this.startColumn = startColumn
    this.endLineNumber = endLineNumber
    this.endColumn = endColumn
  }
}

const run = (step: (c: SaveContext) => unknown, text: string, settings: Record<string, unknown>) => {
  const m = model(text)
  void step({ monaco: { Range } as never, model: m as never, path: 'a.ts', settings, reason: 'explicit' })
  return m.value
}

describe('saving', () => {
  it('trims trailing whitespace, inserts the final newline and trims the final newlines, each when its setting is on', () => {
    expect(run(trimTrailingWhitespace, 'a  \n\tb\t\nc', { 'files.trimTrailingWhitespace': true })).toBe('a\n\tb\nc')
    expect(run(trimTrailingWhitespace, 'a  \n', { 'files.trimTrailingWhitespace': false })).toBe('a  \n')
    expect(run(insertFinalNewline, 'a\nb', { 'files.insertFinalNewline': true })).toBe('a\nb\n')
    expect(run(insertFinalNewline, 'a\nb\n', { 'files.insertFinalNewline': true })).toBe('a\nb\n')
    expect(run(insertFinalNewline, '', { 'files.insertFinalNewline': true })).toBe('')
    expect(run(trimFinalNewlines, 'a\n\n\n', { 'files.trimFinalNewlines': true })).toBe('a\n')
    expect(run(trimFinalNewlines, 'a\n', { 'files.trimFinalNewlines': true })).toBe('a\n')
    expect(run(trimFinalNewlines, 'a', { 'files.trimFinalNewlines': true })).toBe('a')
  })
})
