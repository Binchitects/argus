import { describe, expect, it } from 'vitest'
import { formatterOf, minimalEdit } from './format'

describe('formatting', () => {
  it('turns a formatted text into one edit of what differs, so the cursor stays where nothing changed', () => {
    expect(minimalEdit('a = 1\nb=2\nc = 3\n', 'a = 1\nb = 2\nc = 3\n')).toEqual({ start: 7, end: 8, text: ' = ' })
    expect(minimalEdit('same', 'same')).toBeNull()
    expect(minimalEdit('', 'x')).toEqual({ start: 0, end: 0, text: 'x' })
    expect(minimalEdit('xx', '')).toEqual({ start: 0, end: 2, text: '' })
    // Repeated letters at the edges are not counted twice.
    expect(minimalEdit('aaa', 'aa')).toEqual({ start: 2, end: 3, text: '' })
  })

  it("reads VS Code's extension ids in editor.defaultFormatter as code-arena's formatters", () => {
    expect(formatterOf('esbenp.prettier-vscode')).toBe('prettier')
    expect(formatterOf('ms-python.black-formatter')).toBe('black')
    expect(formatterOf('ruff')).toBe('ruff')
    expect(formatterOf('')).toBeUndefined()
    expect(formatterOf(null)).toBeUndefined()
  })
})
