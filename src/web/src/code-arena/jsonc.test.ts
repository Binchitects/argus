import { describe, expect, it } from 'vitest'
import { appendElement, parse, positionAt, removeElement, setProperty } from './jsonc'

describe('JSON with comments', () => {
  it('reads comments and trailing commas, and says where a mistake is', () => {
    expect(parse('{\n  // a comment\n  "a": [1, 2,],\n  /* b */ "b": "x\\n",\n}').value).toEqual({ a: [1, 2], b: 'x\n' })
    const bad = parse('{\n  "a": 1\n  "b": 2\n}')
    expect(bad.value).toBeUndefined()
    expect(positionAt('{\n  "a": 1\n  "b": 2\n}', bad.errors[0]!.offset)).toEqual({ line: 3, column: 3 })
    expect(parse('').value).toBeUndefined()
    expect(parse('').errors).toEqual([])
  })

  it('sets a property in place, keeping the comments and the layout around it', () => {
    const text = '{\n    // mine\n    "editor.fontSize": 14, // big\n    "files.autoSave": "off"\n}\n'
    expect(setProperty(text, ['editor.fontSize'], 16)).toBe('{\n    // mine\n    "editor.fontSize": 16, // big\n    "files.autoSave": "off"\n}\n')
    expect(setProperty(text, ['editor.tabSize'], 2)).toBe('{\n    // mine\n    "editor.fontSize": 14, // big\n    "files.autoSave": "off",\n    "editor.tabSize": 2\n}\n')
    // A trailing comma stays a trailing comma.
    expect(setProperty('{\n  "a": 1,\n}', ['b'], true)).toBe('{\n  "a": 1,\n  "b": true,\n}')
    // A language's block made, and a property in it.
    expect(setProperty('{\n  "a": 1\n}', ['[python]', 'editor.tabSize'], 4)).toBe('{\n  "a": 1,\n  "[python]": {\n    "editor.tabSize": 4\n  }\n}')
    expect(setProperty('{}', ['a'], { b: [1] })).toBe('{\n    "a": {\n        "b": [\n            1\n        ]\n    }\n}')
    expect(setProperty('', ['a'], 1)).toBe('{\n    "a": 1\n}\n')
    expect(() => setProperty('{ "a": ', ['a'], 1)).toThrow(/not JSON/)
  })

  it('takes a property out with its comma', () => {
    const text = '{\n  "a": 1,\n  "b": 2, // two\n  "c": 3\n}'
    expect(setProperty(text, ['b'], undefined)).toBe('{\n  "a": 1,\n  "c": 3\n}')
    expect(setProperty(text, ['c'], undefined)).toBe('{\n  "a": 1,\n  "b": 2\n}')
    expect(setProperty('{\n  "a": 1\n}', ['a'], undefined)).toBe('{\n}')
    expect(setProperty(text, ['missing'], undefined)).toBe(text)
  })

  it('adds and removes array elements, as keybindings.json is written', () => {
    expect(appendElement('', [], { key: 'ctrl+b', command: 'x' })).toBe('[\n    {\n        "key": "ctrl+b",\n        "command": "x"\n    }\n]\n')
    const text = '[\n  // mine\n  { "key": "a", "command": "x" }\n]'
    const added = appendElement(text, [], { key: 'b', command: 'y' })
    expect(parse(added).value).toEqual([{ key: 'a', command: 'x' }, { key: 'b', command: 'y' }])
    expect(added).toContain('// mine')
    expect(parse(removeElement(added, [], 0)).value).toEqual([{ key: 'b', command: 'y' }])
    expect(removeElement(added, [], 0)).toContain('// mine')
    expect(parse(removeElement(added, [], 1)).value).toEqual([{ key: 'a', command: 'x' }])
    expect(appendElement('[]', [], 1)).toBe('[\n    1\n]')
  })
})
